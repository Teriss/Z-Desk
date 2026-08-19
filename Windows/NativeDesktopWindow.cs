using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using ZDesk.Models;
using ZDesk.Services;

namespace ZDesk.Windows;

/// <summary>
/// An independently owned Win32 desktop window. Unlike the earlier HwndHost
/// experiment this is a real top-level HWND, so it can be placed in the
/// WorkerW desktop band without composing a WPF Window or child surface.
/// </summary>
internal sealed class NativeDesktopWindow : IDisposable
{
    private const string ClassName = "ZDesk.NativeDesktopTopLevel";
    private static readonly ConcurrentDictionary<nint, NativeDesktopWindow> Windows = new();
    private static readonly WindowProc WindowProcedure = StaticWindowProcedure;
    private static int _classRegistered;

    private readonly int _cornerRadius;
    private readonly Dictionary<string, nint> _shellIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _shellIconOrder = new();
    private nint _handle;
    private nint _desktopBoundary;
    private nint _backgroundBrush;
    private NativeDropTarget? _dropTarget;
    private bool _dragDropRegistered;
    private bool _oleInitialized;
    private DesktopPresentationSnapshot? _snapshot;
    private bool _temporaryTopmost;
    private bool _restoringDesktopLayer;
    private bool _disposed;
    private int _scrollOffset;
    private string? _pressedPath;
    private int _pressedX;
    private int _pressedY;
    private bool _dragStarted;
    private NativeDesktopBounds _expandedBounds;
    private bool _edgeHidden;
    private nint _renameEdit;
    private string? _renamePath;
    private nint _oldRenameProc;
    private EditWindowProcDelegate? _renameProc;

    public NativeDesktopWindow(int cornerRadius)
    {
        _cornerRadius = Math.Clamp(cornerRadius, 0, 24);
        EnsureClassRegistered();
    }

    public nint Handle => _handle;
    public bool IsVisible => _handle != nint.Zero && IsWindowVisible(_handle);
    public bool IsTemporaryTopmost => _temporaryTopmost;
    public bool IsEdgeHidden => _edgeHidden;
    public DockEdge DockEdge { get; set; }
    public bool IsCursorInRevealZone(System.Drawing.Point cursorPixels)
    {
        if (_handle == nint.Zero || DockEdge == DockEdge.None) return false;
        var screen = System.Windows.Forms.Screen.FromHandle(_handle);
        var expanded = _expandedBounds.Width > 0 ? _expandedBounds : Bounds;
        return EdgeDockGeometry.IsCursorInRevealZone(
            DockEdge,
            screen.WorkingArea,
            new System.Drawing.Rectangle(expanded.X, expanded.Y, expanded.Width, expanded.Height),
            cursorPixels);
    }

    public bool IsCursorInExpandedBounds(System.Drawing.Point cursorPixels)
    {
        var expanded = _expandedBounds.Width > 0 ? _expandedBounds : Bounds;
        return new System.Drawing.Rectangle(expanded.X, expanded.Y, expanded.Width, expanded.Height).Contains(cursorPixels);
    }
    public NativeDesktopBounds Bounds
    {
        get
        {
            if (_handle == nint.Zero || !GetWindowRect(_handle, out var rect)) return default;
            return new NativeDesktopBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    public event EventHandler? BoundsChanged;
    public event EventHandler? InteractionRequested;
    public event Action<string, bool, bool>? ItemSelectionRequested;
    public event Action<string>? ItemActivated;
    public event Action<int, bool, bool>? NavigationRequested;
    public event Action<string, System.Drawing.Point>? ItemContextMenuRequested;
    public event Action<string[], System.Drawing.Point, bool>? DropRequested;
    public event Action<Guid>? TabActivated;
    public event Action<string, string>? RenameCommitted;
    public event Action<string>? ItemDragRequested;
    public event Action<System.Drawing.Point>? LayoutMenuRequested;

    public void Create(NativeDesktopBounds bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle != nint.Zero) return;

        var oleResult = OleInitialize(nint.Zero);
        if (oleResult < 0)
            throw new COMException("Could not initialize OLE for native desktop drag/drop.", oleResult);
        _oleInitialized = true;
        _backgroundBrush = CreateSolidBrush(unchecked((int)0x00292120));
        _handle = CreateWindowEx(
            WsExToolWindow,
            ClassName,
            string.Empty,
            WsPopup | WsThickFrame,
            bounds.X,
            bounds.Y,
            Math.Max(220, bounds.Width),
            Math.Max(36, bounds.Height),
            nint.Zero,
            nint.Zero,
            GetModuleHandle(null),
            nint.Zero);
        if (_handle == nint.Zero)
            throw new InvalidOperationException($"Could not create native desktop window. Win32 error: {Marshal.GetLastWin32Error()}.");

        Windows[_handle] = this;
        _dropTarget = new NativeDropTarget(this);
        var dragDropResult = RegisterDragDrop(_handle, _dropTarget);
        if (dragDropResult < 0)
        {
            Windows.TryRemove(_handle, out _);
            DestroyWindow(_handle);
            _handle = nint.Zero;
            OleUninitialize();
            _oleInitialized = false;
            throw new COMException("Could not register native desktop OLE drop target.", dragDropResult);
        }
        _dragDropRegistered = true;
        ApplyRegion();
        RestoreDesktopLayer();
    }

    public void Show()
    {
        EnsureCreated();
        ShowWindow(_handle, SwShowna);
        RestoreDesktopLayer();
    }

    public void Hide()
    {
        if (_handle != nint.Zero) ShowWindow(_handle, SwHide);
    }

    public void Update(DesktopPresentationSnapshot snapshot)
    {
        _snapshot = snapshot;
        TrimShellIconCache(snapshot);
        if (_handle != nint.Zero) InvalidateRect(_handle, nint.Zero, false);
    }

    public void SetBounds(NativeDesktopBounds bounds)
    {
        EnsureCreated();
        SetWindowPos(_handle, nint.Zero, bounds.X, bounds.Y, Math.Max(220, bounds.Width), Math.Max(36, bounds.Height),
            SwpNoActivate | SwpNoZOrder);
        ApplyRegion();
        if (!_edgeHidden) _expandedBounds = Bounds;
    }

    public void HideToEdge()
    {
        if (_handle == nint.Zero || DockEdge == DockEdge.None || _edgeHidden) return;
        _expandedBounds = Bounds;
        var area = System.Windows.Forms.Screen.FromHandle(_handle).WorkingArea;
        const int strip = 3;
        var current = _expandedBounds;
        var hidden = DockEdge switch
        {
            DockEdge.Left => new NativeDesktopBounds(area.Left - current.Width + strip, current.Y, current.Width, current.Height),
            DockEdge.Right => new NativeDesktopBounds(area.Right - strip, current.Y, current.Width, current.Height),
            DockEdge.Top => new NativeDesktopBounds(current.X, area.Top - current.Height + strip, current.Width, current.Height),
            _ => current
        };
        _edgeHidden = true;
        SetWindowPos(_handle, nint.Zero, hidden.X, hidden.Y, hidden.Width, hidden.Height, SwpNoActivate | SwpNoZOrder);
        ApplyRegion();
    }

    public void RevealFromEdge()
    {
        if (_handle == nint.Zero || !_edgeHidden) return;
        _edgeHidden = false;
        SetWindowPos(_handle, nint.Zero, _expandedBounds.X, _expandedBounds.Y, _expandedBounds.Width, _expandedBounds.Height, SwpNoActivate | SwpNoZOrder);
        ApplyRegion();
    }

    public void SetTemporaryTopmost(bool enabled)
    {
        _temporaryTopmost = enabled;
        if (_handle == nint.Zero) return;
        SetWindowPos(_handle, enabled ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
        if (!enabled) RestoreDesktopLayer();
    }

    public void RestoreDesktopLayer()
    {
        if (_handle == nint.Zero || _temporaryTopmost) return;
        _restoringDesktopLayer = true;
        try
        {
            EnsureDesktopBoundary();
            SetWindowPos(_handle, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            var insertAfter = GetDesktopInsertAfter();
            if (insertAfter != nint.Zero)
                SetWindowPos(_handle, insertAfter, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        finally
        {
            _restoringDesktopLayer = false;
        }
    }

    private void EnsureDesktopBoundary()
    {
        if (_desktopBoundary != nint.Zero && IsWindow(_desktopBoundary)) return;
        _desktopBoundary = WorkerWHostService.FindHost();
    }

    private nint GetDesktopInsertAfter()
    {
        EnsureDesktopBoundary();
        if (_desktopBoundary == nint.Zero) return nint.Zero;
        var insertAfter = GetWindow(_desktopBoundary, GwHwndPrev);
        return insertAfter == _handle ? GetWindow(_handle, GwHwndPrev) : insertAfter;
    }

    private void ApplyRegion()
    {
        if (_handle == nint.Zero || !GetWindowRect(_handle, out var bounds)) return;
        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var radius = Math.Clamp(_cornerRadius * 2, 0, Math.Min(width, height));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
        if (region == nint.Zero) return;
        if (SetWindowRgn(_handle, region, true) == 0) DeleteObject(region);
    }

    private nint ProcessWindowMessage(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmPaint:
                Paint();
                return nint.Zero;
            case WmMouseWheel when _snapshot is { } wheelSnapshot:
                GetClientRect(_handle, out var wheelClient);
                var delta = (short)(((long)wParam >> 16) & 0xFFFF);
                _scrollOffset = NativeDesktopLayout.ClampScrollOffset(
                    wheelSnapshot, _scrollOffset - Math.Sign(delta) * 66,
                    wheelClient.Right - wheelClient.Left, wheelClient.Bottom - wheelClient.Top);
                InvalidateRect(_handle, nint.Zero, false);
                return nint.Zero;
            case WmLeftButtonDown when _snapshot is { } selectionSnapshot:
                InteractionRequested?.Invoke(this, EventArgs.Empty);
                if (TryActivateTab(selectionSnapshot, GetX(lParam), GetY(lParam))) return nint.Zero;
                _pressedX = GetX(lParam);
                _pressedY = GetY(lParam);
                var pressedIndex = HitTestItem(selectionSnapshot, _pressedX, _pressedY);
                _pressedPath = pressedIndex >= 0 ? selectionSnapshot.Items[pressedIndex].FullPath : null;
                _dragStarted = false;
                SelectAt(selectionSnapshot, GetX(lParam), GetY(lParam), wParam);
                return nint.Zero;
            case WmMouseMove when _pressedPath is not null && !_dragStarted && (wParam.ToInt64() & MkLButton) != 0:
                if (Math.Abs(GetX(lParam) - _pressedX) >= 6 || Math.Abs(GetY(lParam) - _pressedY) >= 6)
                {
                    _dragStarted = true;
                    ItemDragRequested?.Invoke(_pressedPath);
                }
                return nint.Zero;
            case WmLeftButtonUp:
                _pressedPath = null;
                _dragStarted = false;
                return nint.Zero;
            case WmLeftButtonDblClk when _snapshot is { } activationSnapshot:
                ActivateAt(activationSnapshot, GetX(lParam), GetY(lParam));
                return nint.Zero;
            case WmRightButtonUp when _snapshot is { } contextSnapshot:
                if (HitTestItem(contextSnapshot, GetX(lParam), GetY(lParam)) < 0 && GetCursorPos(out var blankCursor))
                    LayoutMenuRequested?.Invoke(new System.Drawing.Point(blankCursor.X, blankCursor.Y));
                else
                    ShowItemContextMenu(contextSnapshot, GetX(lParam), GetY(lParam));
                return nint.Zero;
            case WmKeyDown:
                if ((int)wParam.ToInt64() == VkF2 && _snapshot is { } renameSnapshot)
                {
                    BeginRename(renameSnapshot);
                    return nint.Zero;
                }
                NavigationRequested?.Invoke((int)wParam.ToInt64(),
                    (GetKeyState(VkControl) & 0x8000) != 0,
                    (GetKeyState(VkShift) & 0x8000) != 0);
                return nint.Zero;
            case WmNCHitTest:
                return HitTestNonClient(GetX(lParam), GetY(lParam));
            case WmDpiChanged when lParam != nint.Zero:
                var suggestedBounds = Marshal.PtrToStructure<NativeRect>(lParam);
                SetWindowPos(_handle, nint.Zero,
                    suggestedBounds.Left,
                    suggestedBounds.Top,
                    Math.Max(220, suggestedBounds.Right - suggestedBounds.Left),
                    Math.Max(36, suggestedBounds.Bottom - suggestedBounds.Top),
                    SwpNoActivate | SwpNoZOrder);
                ApplyRegion();
                BoundsChanged?.Invoke(this, EventArgs.Empty);
                return nint.Zero;
            case WmMove:
            case WmSize:
                ApplyRegion();
                BoundsChanged?.Invoke(this, EventArgs.Empty);
                return nint.Zero;
            case WmWindowPosChanging when !_temporaryTopmost && !_restoringDesktopLayer && lParam != nint.Zero:
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                if ((position.Flags & SwpNoZOrder) == 0)
                {
                    var insertAfter = GetDesktopInsertAfter();
                    if (insertAfter != nint.Zero)
                    {
                        position.InsertAfter = insertAfter;
                        Marshal.StructureToPtr(position, lParam, false);
                    }
                }
                break;
        }
        return DefWindowProc(_handle, message, wParam, lParam);
    }

    private void Paint()
    {
        BeginPaint(_handle, out var paint);
        try
        {
            FillRect(paint.DeviceContext, ref paint.Paint, _backgroundBrush);
            if (_snapshot is not { } snapshot) return;
            GetClientRect(_handle, out var client);
            _scrollOffset = NativeDesktopLayout.ClampScrollOffset(
                snapshot, _scrollOffset, client.Right - client.Left, client.Bottom - client.Top);

            using var title = new NativeFont(14, bold: true);
            title.Use(paint.DeviceContext, () =>
            {
                if (snapshot.Tabs.Count <= 1)
                {
                    TextOut(paint.DeviceContext, 12, 11, snapshot.Title, snapshot.Title.Length);
                    return;
                }

                for (var tabIndex = 0; tabIndex < snapshot.Tabs.Count; tabIndex++)
                {
                    var tab = snapshot.Tabs[tabIndex];
                    var tabBounds = GetTabBounds(tabIndex);
                    var brush = CreateSolidBrush(tab.IsActive ? unchecked((int)0x004D7DBA) : unchecked((int)0x00353631));
                    FillRect(paint.DeviceContext, ref tabBounds, brush);
                    DeleteObject(brush);
                    TextOut(paint.DeviceContext, tabBounds.Left + 7, 11, tab.Title, tab.Title.Length);
                }
            });
            using var itemFont = new NativeFont(12, bold: false);
            itemFont.Use(paint.DeviceContext, () =>
            {
                for (var index = 0; index < snapshot.Items.Count; index++)
                {
                    var item = snapshot.Items[index];
                    var itemRect = NativeDesktopLayout.GetItemRect(snapshot, index, client.Right - client.Left);
                    var top = itemRect.Top - _scrollOffset;
                    var bottom = itemRect.Bottom - _scrollOffset;
                    if (bottom < NativeDesktopLayout.HeaderHeight || top > client.Bottom) continue;
                    if (item.IsSelected)
                    {
                        var selectionBrush = CreateSolidBrush(unchecked((int)0x00B66635));
                        var selection = new NativeRect(itemRect.Left, top, itemRect.Right, bottom);
                        FillRect(paint.DeviceContext, ref selection, selectionBrush);
                        DeleteObject(selectionBrush);
                    }
                    var icon = GetShellIcon(item.FullPath);
                    if (icon != nint.Zero)
                    {
                        DrawIconEx(paint.DeviceContext, itemRect.Left + 8, top + 7, icon, 24, 24, 0, nint.Zero, DiNormal);
                    }
                    TextOut(paint.DeviceContext, itemRect.Left + 38, top + 11, item.Name, item.Name.Length);
                }
            });
        }
        finally
        {
            EndPaint(_handle, ref paint);
        }
    }

    private void SelectAt(DesktopPresentationSnapshot snapshot, int x, int y, nint keys)
    {
        GetClientRect(_handle, out var client);
        var index = NativeDesktopLayout.HitTestIndex(snapshot, x, y + _scrollOffset, client.Right - client.Left);
        if (index < 0 || index >= snapshot.Items.Count) return;
        ItemSelectionRequested?.Invoke(snapshot.Items[index].FullPath, (keys.ToInt64() & MkControl) != 0, (keys.ToInt64() & MkShift) != 0);
    }

    private int HitTestItem(DesktopPresentationSnapshot snapshot, int x, int y)
    {
        GetClientRect(_handle, out var client);
        return NativeDesktopLayout.HitTestIndex(snapshot, x, y + _scrollOffset, client.Right - client.Left);
    }

    private bool TryActivateTab(DesktopPresentationSnapshot snapshot, int x, int y)
    {
        if (snapshot.Tabs.Count <= 1 || y < 0 || y >= NativeDesktopLayout.HeaderHeight) return false;
        for (var index = 0; index < snapshot.Tabs.Count; index++)
        {
            var bounds = GetTabBounds(index);
            if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom) continue;
            TabActivated?.Invoke(snapshot.Tabs[index].Id);
            return true;
        }
        return false;
    }

    private void ActivateAt(DesktopPresentationSnapshot snapshot, int x, int y)
    {
        GetClientRect(_handle, out var client);
        var index = NativeDesktopLayout.HitTestIndex(snapshot, x, y + _scrollOffset, client.Right - client.Left);
        if (index >= 0 && index < snapshot.Items.Count) ItemActivated?.Invoke(snapshot.Items[index].FullPath);
    }

    private void RaiseDrop(ComTypes.IDataObject data, NativePoint point, uint keyState)
    {
        var managed = new System.Windows.DataObject(data);
        if (!managed.GetDataPresent(System.Windows.DataFormats.FileDrop, true)) return;
        if (managed.GetData(System.Windows.DataFormats.FileDrop, true) is not string[] paths || paths.Length == 0) return;
        DropRequested?.Invoke(paths, new System.Drawing.Point(point.X, point.Y), (keyState & MkControl) != 0);
    }

    private void ShowItemContextMenu(DesktopPresentationSnapshot snapshot, int x, int y)
    {
        GetClientRect(_handle, out var client);
        var index = NativeDesktopLayout.HitTestIndex(snapshot, x, y + _scrollOffset, client.Right - client.Left);
        if (index < 0 || index >= snapshot.Items.Count || !GetCursorPos(out var cursor)) return;
        ItemContextMenuRequested?.Invoke(snapshot.Items[index].FullPath, new System.Drawing.Point(cursor.X, cursor.Y));
    }

    private void BeginRename(DesktopPresentationSnapshot snapshot)
    {
        var item = snapshot.Items.FirstOrDefault(item => item.IsSelected);
        if (item is null) return;
        GetClientRect(_handle, out var client);
        var index = 0;
        while (index < snapshot.Items.Count && !ReferenceEquals(snapshot.Items[index], item)) index++;
        if (index < 0) return;
        var itemBounds = NativeDesktopLayout.GetItemRect(snapshot, index, client.Right - client.Left);
        DestroyRenameEditor();
        _renamePath = item.FullPath;
        _renameEdit = CreateWindowEx(0, "EDIT", item.Name, WsChild | WsVisible | WsBorder,
            itemBounds.Left + 34, itemBounds.Top - _scrollOffset + 4,
            Math.Max(100, itemBounds.Right - itemBounds.Left - 40), 24,
            _handle, nint.Zero, GetModuleHandle(null), nint.Zero);
        _renameProc = RenameWindowProcedure;
        _oldRenameProc = SetWindowLongPtr(_renameEdit, GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_renameProc));
        SetFocus(_renameEdit);
        SendMessage(_renameEdit, EmSetSel, nint.Zero, new nint(-1));
    }

    private void DestroyRenameEditor()
    {
        if (_renameEdit != nint.Zero && _oldRenameProc != nint.Zero)
            SetWindowLongPtr(_renameEdit, GwlWndProc, _oldRenameProc);
        if (_renameEdit != nint.Zero) DestroyWindow(_renameEdit);
        _renameEdit = nint.Zero;
        _renamePath = null;
        _oldRenameProc = nint.Zero;
        _renameProc = null;
    }

    private nint RenameWindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmKeyDown && wParam.ToInt64() == VkEnter)
        {
            var length = GetWindowTextLength(hwnd);
            var text = new StringBuilder(length + 1);
            GetWindowText(hwnd, text, text.Capacity);
            RenameCommitted?.Invoke(_renamePath ?? string.Empty, text.ToString().Trim());
            DestroyRenameEditor();
            return nint.Zero;
        }
        if (message == WmKeyDown && wParam.ToInt64() == VkEscape)
        {
            DestroyRenameEditor();
            return nint.Zero;
        }
        return CallWindowProc(_oldRenameProc, hwnd, message, wParam, lParam);
    }

    private nint HitTestNonClient(int screenX, int screenY)
    {
        if (!GetWindowRect(_handle, out var bounds)) return HtClient;
        var x = screenX - bounds.Left;
        var y = screenY - bounds.Top;
        if (x >= bounds.Right - bounds.Left - ResizeGrip && y >= bounds.Bottom - bounds.Top - ResizeGrip)
            return HtBottomRight;
        return y < NativeDesktopLayout.HeaderHeight ? HtCaption : HtClient;
    }

    private static NativeRect GetTabBounds(int index)
    {
        const int width = 104;
        return new NativeRect(8 + index * width, 5, 8 + (index + 1) * width - 4, NativeDesktopLayout.HeaderHeight - 5);
    }

    private void EnsureCreated()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle == nint.Zero) throw new InvalidOperationException("The native desktop window has not been created.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeShellIconCache();
        if (_handle != nint.Zero)
        {
            Windows.TryRemove(_handle, out _);
            DestroyRenameEditor();
            if (_dragDropRegistered) RevokeDragDrop(_handle);
            DestroyWindow(_handle);
            _handle = nint.Zero;
        }
        _dropTarget = null;
        _dragDropRegistered = false;
        if (_oleInitialized) OleUninitialize();
        _oleInitialized = false;
        if (_backgroundBrush != nint.Zero) DeleteObject(_backgroundBrush);
        _backgroundBrush = nint.Zero;
    }

    private static nint StaticWindowProcedure(nint hwnd, uint message, nint wParam, nint lParam) =>
        Windows.TryGetValue(hwnd, out var window)
            ? window.ProcessWindowMessage(message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);

    private static void EnsureClassRegistered()
    {
        if (Volatile.Read(ref _classRegistered) != 0 || GetClassInfoEx(GetModuleHandle(null), ClassName, out _))
        {
            Volatile.Write(ref _classRegistered, 1);
            return;
        }
        var registration = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProcedure = WindowProcedure,
            Instance = GetModuleHandle(null),
            ClassName = ClassName,
            Cursor = LoadCursor(nint.Zero, new nint(32512))
        };
        if (RegisterClassEx(ref registration) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1410) throw new InvalidOperationException($"Could not register native desktop window class. Win32 error: {error}.");
        }
        Volatile.Write(ref _classRegistered, 1);
    }

    private sealed class NativeFont : IDisposable
    {
        private readonly nint _font;
        public NativeFont(int size, bool bold) => _font = CreateFont(-size, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        public void Use(nint deviceContext, Action action)
        {
            var previous = SelectObject(deviceContext, _font);
            try { action(); }
            finally { if (previous != nint.Zero) SelectObject(deviceContext, previous); }
        }
        public void Dispose() { if (_font != nint.Zero) DeleteObject(_font); }
    }

    [ComVisible(true)]
    private sealed class NativeDropTarget : INativeDropTarget
    {
        private readonly NativeDesktopWindow _owner;
        public NativeDropTarget(NativeDesktopWindow owner) => _owner = owner;
        public int DragEnter(ComTypes.IDataObject data, uint keyState, NativePoint point, ref uint effect)
        {
            effect = (keyState & MkControl) != 0 ? DropEffectCopy : DropEffectMove;
            return 0;
        }
        public int DragOver(uint keyState, NativePoint point, ref uint effect)
        {
            effect = (keyState & MkControl) != 0 ? DropEffectCopy : DropEffectMove;
            return 0;
        }
        public int DragLeave() => 0;
        public int Drop(ComTypes.IDataObject data, uint keyState, NativePoint point, ref uint effect)
        {
            _owner.RaiseDrop(data, point, keyState);
            effect = (keyState & MkControl) != 0 ? DropEffectCopy : DropEffectMove;
            return 0;
        }
    }

    private const uint WsPopup = 0x80000000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpNoZOrder = 0x0004;
    private const uint GwHwndPrev = 3;
    private const int SwHide = 0, SwShowna = 8;
    private const uint WmPaint = 0x000F, WmSize = 0x0005, WmMove = 0x0003, WmWindowPosChanging = 0x0046, WmNCHitTest = 0x0084, WmDpiChanged = 0x02E0;
    private const uint WmLeftButtonDown = 0x0201, WmLeftButtonDblClk = 0x0203, WmLeftButtonUp = 0x0202, WmMouseMove = 0x0200, WmRightButtonUp = 0x0205, WmMouseWheel = 0x020A, WmKeyDown = 0x0100;
    private const long MkShift = 0x0004, MkControl = 0x0008, MkLButton = 0x0001;
    private const uint DropEffectCopy = 1, DropEffectMove = 2;
    private const int HtClient = 1, HtCaption = 2, HtBottomRight = 17, ResizeGrip = 18, VkControl = 0x11, VkShift = 0x10, VkF2 = 0x71, VkEnter = 0x0D, VkEscape = 0x1B, GwlWndProc = -4;
    private const uint WsChild = 0x40000000, WsVisible = 0x10000000, WsBorder = 0x00800000;
    private const int EmSetSel = 0x00B1;
    private static readonly nint HwndTopmost = new(-1), HwndNoTopmost = new(-2);

    private static int GetX(nint value) => unchecked((short)((long)value & 0xFFFF));
    private static int GetY(nint value) => unchecked((short)(((long)value >> 16) & 0xFFFF));

    private static nint LoadShellIcon(string path)
    {
        var result = SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        return result == nint.Zero ? nint.Zero : info.Icon;
    }

    private nint GetShellIcon(string path)
    {
        if (_shellIconCache.TryGetValue(path, out var icon)) return icon;
        icon = LoadShellIcon(path);
        if (icon == nint.Zero) return nint.Zero;
        _shellIconCache[path] = icon;
        _shellIconOrder.Enqueue(path);
        while (_shellIconOrder.Count > 256)
        {
            var oldest = _shellIconOrder.Dequeue();
            if (_shellIconCache.Remove(oldest, out var oldIcon) && oldIcon != nint.Zero)
                DestroyIcon(oldIcon);
        }
        return icon;
    }

    private void TrimShellIconCache(DesktopPresentationSnapshot snapshot)
    {
        var paths = snapshot.Items.Select(item => item.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _shellIconCache.Keys.Where(path => !paths.Contains(path)).ToArray())
        {
            if (_shellIconCache.Remove(path, out var icon) && icon != nint.Zero)
                DestroyIcon(icon);
        }
        if (_shellIconOrder.Count > 256)
        {
            var retained = _shellIconOrder.Where(_shellIconCache.ContainsKey).Take(256).ToArray();
            _shellIconOrder.Clear();
            foreach (var path in retained) _shellIconOrder.Enqueue(path);
        }
    }

    private void DisposeShellIconCache()
    {
        foreach (var icon in _shellIconCache.Values)
            if (icon != nint.Zero) DestroyIcon(icon);
        _shellIconCache.Clear();
        _shellIconOrder.Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public WindowProc WindowProcedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint IconSmall; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { public nint Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; public NativeRect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; } }
    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct { public nint DeviceContext; public NativeRect Paint; public bool Erase, Restore, IncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[]? Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellFileInfo { public nint Icon; public int IconIndex; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName; }
    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    private delegate nint EditWindowProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [ComVisible(true)]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeDropTarget
    {
        int DragEnter(ComTypes.IDataObject data, uint keyState, NativePoint point, ref uint effect);
        int DragOver(uint keyState, NativePoint point, ref uint effect);
        int DragLeave();
        int Drop(ComTypes.IDataObject data, uint keyState, NativePoint point, ref uint effect);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetClassInfoEx(nint instance, string className, out WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint window, INativeDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint window);
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CallWindowProc(nint previous, nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint window, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rectangle, bool erase);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool FillRect(nint deviceContext, ref NativeRect rectangle, nint brush);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint cursor);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint deviceContext, nint value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(nint deviceContext, int x, int y, string text, int length);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint deviceContext, int x, int y, nint icon, int width, int height, uint stepIfAniCur, nint brush, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo fileInfo, uint size, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    private const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0, DiNormal = 0x3;
}

internal readonly record struct NativeDesktopBounds(int X, int Y, int Width, int Height);
