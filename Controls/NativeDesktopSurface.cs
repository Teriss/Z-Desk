using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using System.Windows.Interop;
using System.Windows;
using ZDesk.Models;

namespace ZDesk.Controls;

/// <summary>Small native drawing surface used while the desktop renderer is migrated.</summary>
internal sealed class NativeDesktopSurface : HwndHost
{
    private static readonly string ClassName = "ZDesk.NativeDesktopSurface";
    private IntPtr _handle;
    private IntPtr _backgroundBrush;
    private INativeDropTarget? _dropTarget;
    private int _scrollOffset;
    private DesktopPresentationSnapshot? _snapshot;

    public event Action<string, bool, bool>? ItemSelectionRequested;
    public event Action<string, System.Drawing.Point>? ItemContextMenuRequested;
    public event Action<string>? ItemDragRequested;
    public event Action<int, bool, bool>? NavigationRequested;
    public event Action<string>? ItemActivated;
    public event Action<string, string>? RenameCommitted;
    private string? _pressedPath;
    private int _pressedX;
    private int _pressedY;
    private bool _dragStarted;
    private IntPtr _renameEdit;
    private string? _renamePath;
    private IntPtr _oldEditProc;
    private EditProcDelegate? _editProc;

    public void BeginRename(string path)
    {
        if (_handle == IntPtr.Zero || _snapshot is null) return;
        var index = _snapshot.Items.ToList().FindIndex(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        GetClientRect(_handle, out var client);
        var rect = ToNativeRect(NativeDesktopLayout.GetItemRect(_snapshot, index, client.Right - client.Left));
        rect.Top -= _scrollOffset;
        rect.Bottom -= _scrollOffset;
        DestroyRenameEdit();
        _renamePath = path;
        _renameEdit = CreateWindowEx(0, "EDIT", _snapshot.Items[index].Name, WsChild | WsVisible | WsBorder,
            rect.Left + 38, rect.Top + 5, Math.Max(100, rect.Right - rect.Left - 44), Math.Max(22, rect.Bottom - rect.Top - 8),
            _handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        _editProc = EditWindowProc;
        _oldEditProc = SetWindowLongPtr(_renameEdit, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_editProc));
        SetFocus(_renameEdit);
        SendMessage(_renameEdit, EmSetSel, IntPtr.Zero, new IntPtr(-1));
    }

    public void Update(DesktopPresentationSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (_handle != IntPtr.Zero) InvalidateRect(_handle, IntPtr.Zero, false);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        Register();
        _backgroundBrush = CreateSolidBrush(unchecked((int)0x00292120));
        _handle = CreateWindowEx(0, ClassName, string.Empty, WsChild | WsVisible,
            0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the native desktop surface.");
        _dropTarget = new NativeDropTarget(this);
        RegisterDragDrop(_handle, _dropTarget);
        return new HandleRef(this, _handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyRenameEdit();
        if (hwnd.Handle != IntPtr.Zero)
        {
            RevokeDragDrop(hwnd.Handle);
            DestroyWindow(hwnd.Handle);
        }
        _dropTarget = null;
        _handle = IntPtr.Zero;
    }

    private IntPtr EditWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmKeyDown && wParam.ToInt64() == 0x0D)
        {
            var length = GetWindowTextLength(hwnd);
            var text = new StringBuilder(length + 1);
            GetWindowText(hwnd, text, text.Capacity);
            RenameCommitted?.Invoke(_renamePath ?? string.Empty, text.ToString().Trim());
            DestroyRenameEdit();
            return IntPtr.Zero;
        }
        if (message == WmKeyDown && wParam.ToInt64() == 0x1B)
        {
            DestroyRenameEdit();
            return IntPtr.Zero;
        }
        return CallWindowProc(_oldEditProc, hwnd, message, wParam, lParam);
    }

    private void DestroyRenameEdit()
    {
        if (_renameEdit == IntPtr.Zero) return;
        if (_oldEditProc != IntPtr.Zero) SetWindowLongPtr(_renameEdit, GwlWndProc, _oldEditProc);
        DestroyWindow(_renameEdit);
        _renameEdit = IntPtr.Zero;
        _renamePath = null;
        _oldEditProc = IntPtr.Zero;
        _editProc = null;
    }

    private void RaiseDrop(ComTypes.IDataObject data, POINTL point, uint keys)
    {
        var managed = new System.Windows.DataObject(data);
        if (!managed.GetDataPresent(System.Windows.DataFormats.FileDrop, true)) return;
        if (managed.GetData(System.Windows.DataFormats.FileDrop, true) is not string[] paths || paths.Length == 0) return;
        DropRequested?.Invoke(paths, new System.Drawing.Point(point.X, point.Y), (keys & 0x0008) != 0);
    }

    public event Action<string[], System.Drawing.Point, bool>? DropRequested;

    private void Register()
    {
        if (GetClassInfoEx(GetModuleHandle(null), ClassName, out _)) return;
        var wnd = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Style = 0x0008,
            WindowProc = NativeWindowProc,
            Instance = GetModuleHandle(null),
            ClassName = ClassName,
            Background = IntPtr.Zero
        };
        RegisterClassEx(ref wnd);
    }

    private IntPtr NativeWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmPaint)
        {
            BeginPaint(hwnd, out var paint);
            try
            {
                FillRect(paint.DeviceContext, ref paint.Paint, _backgroundBrush);
                if (_snapshot is { } snapshot)
                {
                    using var title = new NativeFont(14, true);
                    title.Use(paint.DeviceContext, () =>
                        TextOut(paint.DeviceContext, 12, 10, snapshot.Title, snapshot.Title.Length));
                    using var itemFont = new NativeFont(12, false);
                    itemFont.Use(paint.DeviceContext, () =>
                    {
                        GetClientRect(hwnd, out var client);
                        _scrollOffset = NativeDesktopLayout.ClampScrollOffset(
                            snapshot, _scrollOffset, client.Right - client.Left, client.Bottom);
                        for (var itemIndex = 0; itemIndex < snapshot.Items.Count; itemIndex++)
                        {
                            var item = snapshot.Items[itemIndex];
                            var itemRect = ToNativeRect(NativeDesktopLayout.GetItemRect(
                                snapshot, itemIndex, client.Right - client.Left));
                            itemRect.Top -= _scrollOffset;
                            itemRect.Bottom -= _scrollOffset;
                            if (itemRect.Bottom < NativeDesktopLayout.HeaderHeight || itemRect.Top > client.Bottom) continue;
                            if (item.IsSelected)
                            {
                                var selectedBrush = CreateSolidBrush(unchecked((int)0x00B66635));
                                var selectedRect = itemRect;
                                FillRect(paint.DeviceContext, ref selectedRect, selectedBrush);
                                DeleteObject(selectedBrush);
                            }
                            var icon = LoadShellIcon(item.FullPath);
                            if (icon != IntPtr.Zero)
                            {
                                DrawIconEx(paint.DeviceContext, itemRect.Left + 8, itemRect.Top + 8, icon, 20, 20, 0, IntPtr.Zero, DiNormal);
                                DestroyIcon(icon);
                            }
                            TextOut(paint.DeviceContext, itemRect.Left + 42, itemRect.Top + 10, item.Name, item.Name.Length);
                        }
                    });
                }
            }
            finally { EndPaint(hwnd, ref paint); }
            return IntPtr.Zero;
        }
        if (message == WmMouseWheel && _snapshot is { } wheelSnapshot)
        {
            var delta = (short)(((long)wParam >> 16) & 0xFFFF);
            GetClientRect(hwnd, out var wheelClient);
            _scrollOffset = NativeDesktopLayout.ClampScrollOffset(
                wheelSnapshot,
                _scrollOffset - Math.Sign(delta) * ItemHeight * 3,
                wheelClient.Right - wheelClient.Left,
                wheelClient.Bottom);
            InvalidateRect(hwnd, IntPtr.Zero, false);
            return IntPtr.Zero;
        }
        if (message == WmRightButtonUp && _snapshot is { } contextSnapshot)
        {
            GetClientRect(hwnd, out var client);
            var index = NativeDesktopLayout.HitTestIndex(contextSnapshot, GetX(lParam), GetY(lParam) + _scrollOffset, client.Right - client.Left);
            if (index >= 0 && index < contextSnapshot.Items.Count && GetCursorPos(out var cursor))
                ItemContextMenuRequested?.Invoke(contextSnapshot.Items[index].FullPath,
                    new System.Drawing.Point(cursor.X, cursor.Y));
            return IntPtr.Zero;
        }
        if (message == WmLeftButtonDown && _snapshot is { } clickedSnapshot)
        {
            SetFocus(hwnd);
            GetClientRect(hwnd, out var client);
            var index = NativeDesktopLayout.HitTestIndex(clickedSnapshot, GetX(lParam), GetY(lParam) + _scrollOffset, client.Right - client.Left);
            if (index >= 0 && index < clickedSnapshot.Items.Count)
            {
                _pressedPath = clickedSnapshot.Items[index].FullPath;
                _pressedX = GetX(lParam);
                _pressedY = GetY(lParam);
                _dragStarted = false;
                var keys = wParam.ToInt64();
                ItemSelectionRequested?.Invoke(clickedSnapshot.Items[index].FullPath,
                    (keys & 0x0008) != 0, (keys & 0x0004) != 0);
            }
            return IntPtr.Zero;
        }
        if (message == WmLeftButtonDblClk && _snapshot is { } activatedSnapshot)
        {
            GetClientRect(hwnd, out var client);
            var index = NativeDesktopLayout.HitTestIndex(activatedSnapshot, GetX(lParam), GetY(lParam) + _scrollOffset, client.Right - client.Left);
            if (index >= 0 && index < activatedSnapshot.Items.Count)
                ItemActivated?.Invoke(activatedSnapshot.Items[index].FullPath);
            return IntPtr.Zero;
        }
        if (message == WmKeyDown)
        {
            NavigationRequested?.Invoke((int)wParam,
                (GetKeyState(0x11) & 0x8000) != 0,
                (GetKeyState(0x10) & 0x8000) != 0);
            return IntPtr.Zero;
        }
        if (message == WmMouseMove && _pressedPath is not null && !_dragStarted &&
            (wParam.ToInt64() & 1) != 0 &&
            (Math.Abs(GetX(lParam) - _pressedX) >= 6 || Math.Abs(GetY(lParam) - _pressedY) >= 6))
        {
            _dragStarted = true;
            ItemDragRequested?.Invoke(_pressedPath);
            return IntPtr.Zero;
        }
        if (message == WmLeftButtonUp)
        {
            _pressedPath = null;
            _dragStarted = false;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private sealed class NativeFont : IDisposable
    {
        private readonly IntPtr _font;
        public NativeFont(int size, bool bold) => _font = CreateFont(-size, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        public void Use(IntPtr deviceContext, Action action)
        {
            var previous = SelectObject(deviceContext, _font);
            try { action(); }
            finally { if (previous != IntPtr.Zero) SelectObject(deviceContext, previous); }
        }
        public void Dispose() { if (_font != IntPtr.Zero) DeleteObject(_font); }
    }

    private static Rect ToNativeRect(NativeDesktopRect source) => new()
    {
        Left = source.Left,
        Top = source.Top,
        Right = source.Right,
        Bottom = source.Bottom
    };

    [ComVisible(true)]
    private sealed class NativeDropTarget : INativeDropTarget
    {
        private readonly NativeDesktopSurface _owner;
        public NativeDropTarget(NativeDesktopSurface owner) => _owner = owner;
        public int DragEnter(ComTypes.IDataObject data, uint keys, POINTL point, ref uint effect) { effect = (keys & 0x0008) != 0 ? 1u : 2u; return 0; }
        public int DragOver(uint keys, POINTL point, ref uint effect) { effect = (keys & 0x0008) != 0 ? 1u : 2u; return 0; }
        public int DragLeave() => 0;
        public int Drop(ComTypes.IDataObject data, uint keys, POINTL point, ref uint effect)
        {
            _owner.RaiseDrop(data, point, keys);
            effect = (keys & 0x0008) != 0 ? 1u : 2u;
            return 0;
        }
    }

    [ComVisible(true)]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeDropTarget
    {
        int DragEnter(ComTypes.IDataObject data, uint keys, POINTL point, ref uint effect);
        int DragOver(uint keys, POINTL point, ref uint effect);
        int DragLeave();
        int Drop(ComTypes.IDataObject data, uint keys, POINTL point, ref uint effect);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _backgroundBrush != IntPtr.Zero) DeleteObject(_backgroundBrush);
        _backgroundBrush = IntPtr.Zero;
        base.Dispose(disposing);
    }

    private const int ItemHeight = 22;
    private const uint WmPaint = 0x000F, WmKeyDown = 0x0100, WmLeftButtonDown = 0x0201, WmLeftButtonDblClk = 0x0203, WmLeftButtonUp = 0x0202, WmMouseMove = 0x0200, WmRightButtonUp = 0x0205, WmMouseWheel = 0x020A, WsChild = 0x40000000, WsVisible = 0x10000000, WsBorder = 0x00800000;
    private const int GwlWndProc = -4, EmSetSel = 0x00B1;
    private static int GetX(IntPtr lParam) => unchecked((short)((long)lParam & 0xFFFF));
    private static int GetY(IntPtr lParam) => unchecked((short)(((long)lParam >> 16) & 0xFFFF));
    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct { public IntPtr DeviceContext; public Rect Paint; public bool Erase; public bool Restore; public bool IncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINTL { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct WndClassEx { public uint Size, Style; public NativeWindowProcDelegate WindowProc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string MenuName, ClassName; public IntPtr IconSmall; }
    private delegate IntPtr NativeWindowProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr EditProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WndClassEx wnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetClassInfoEx(IntPtr instance, string name, out WndClassEx wnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr hwnd, INativeDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern bool FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFont(int height, int width, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(IntPtr dc, int x, int y, string text, int count);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0, DiNormal = 0x3;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    private static IntPtr LoadShellIcon(string path)
    {
        var result = SHGetFileInfo(path, 0, out var info,
            (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        return result == IntPtr.Zero ? IntPtr.Zero : info.Icon;
    }
}
