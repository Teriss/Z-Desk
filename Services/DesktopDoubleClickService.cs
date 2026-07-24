using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ZDesk.Services;

public sealed class DesktopDoubleClickService : IDisposable
{
    private const int WhMouseLowLevel = 14;
    private const int WmLeftButtonDown = 0x0201;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const uint GaRoot = 2;
    private const uint LvmFirst = 0x1000;
    private const uint LvmHitTest = LvmFirst + 18;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelMouseProc _hookProc;
    private nint _hook;
    private uint _lastBlankClickTime;
    private Point _lastBlankClickPoint;

    public event EventHandler? DesktopBlankDoubleClicked;

    public DesktopDoubleClickService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hookProc = HookCallback;
    }

    public bool Start()
    {
        if (_hook != nint.Zero)
        {
            return true;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module is null ? nint.Zero : GetModuleHandle(module.ModuleName);
        _hook = SetWindowsHookEx(WhMouseLowLevel, _hookProc, moduleHandle, 0);
        return _hook != nint.Zero;
    }

    public void Dispose()
    {
        if (_hook != nint.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = nint.Zero;
        }
    }

    private nint HookCallback(int code, nint message, nint data)
    {
        if (code >= 0 && message.ToInt32() == WmLeftButtonDown)
        {
            var mouse = Marshal.PtrToStructure<MouseHookData>(data);
            _dispatcher.BeginInvoke(() => ProcessClick(mouse.Position, mouse.Time), DispatcherPriority.Input);
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private void ProcessClick(Point point, uint time)
    {
        if (!IsDesktopBlank(point))
        {
            _lastBlankClickTime = 0;
            return;
        }

        var maxDelay = GetDoubleClickTime();
        var maxX = Math.Max(2, GetSystemMetrics(SmCxDoubleClick));
        var maxY = Math.Max(2, GetSystemMetrics(SmCyDoubleClick));
        var elapsed = unchecked(time - _lastBlankClickTime);

        if (_lastBlankClickTime != 0 && elapsed <= maxDelay &&
            Math.Abs(point.X - _lastBlankClickPoint.X) <= maxX &&
            Math.Abs(point.Y - _lastBlankClickPoint.Y) <= maxY)
        {
            _lastBlankClickTime = 0;
            DesktopBlankDoubleClicked?.Invoke(this, EventArgs.Empty);
            return;
        }

        _lastBlankClickTime = time;
        _lastBlankClickPoint = point;
    }

    private static bool IsDesktopBlank(Point point)
    {
        var target = WindowFromPoint(point);
        if (target == nint.Zero)
        {
            return false;
        }

        var root = GetAncestor(target, GaRoot);
        if (root == nint.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(root, out var targetProcessId);
        if (targetProcessId == (uint)Environment.ProcessId)
        {
            return false;
        }

        var shellWindow = GetShellWindow();
        GetWindowThreadProcessId(shellWindow, out var explorerProcessId);
        if (explorerProcessId == 0 || targetProcessId != explorerProcessId || !IsExplorerDesktopRoot(root))
        {
            return false;
        }

        var listView = FindDesktopListView();
        if (listView != nint.Zero && (target == listView || IsChild(listView, target)))
        {
            return HitTestDesktopListView(listView, point) < 0;
        }

        return true;
    }

    private static bool IsExplorerDesktopRoot(nint root)
    {
        var className = GetWindowClassName(root);
        if (className == "Progman") return true;
        if (className != "WorkerW") return false;

        if (root == WorkerWHostService.FindHost()) return true;
        return FindWindowEx(root, nint.Zero, "SHELLDLL_DefView", null) != nint.Zero;
    }

    private static int HitTestDesktopListView(nint listView, Point screenPoint)
    {
        GetWindowThreadProcessId(listView, out var processId);
        var process = OpenProcess(
            ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
            false,
            processId);
        if (process == nint.Zero)
        {
            return 0;
        }

        var remoteMemory = nint.Zero;
        try
        {
            var info = new ListViewHitTestInfo { Position = screenPoint, Item = -1, SubItem = -1, Group = -1 };
            ScreenToClient(listView, ref info.Position);
            var size = (nuint)Marshal.SizeOf<ListViewHitTestInfo>();
            remoteMemory = VirtualAllocEx(process, nint.Zero, size, MemCommit | MemReserve, PageReadWrite);
            if (remoteMemory == nint.Zero || !WriteProcessMemory(process, remoteMemory, ref info, size, out _))
            {
                return 0;
            }

            var sent = SendMessageTimeout(
                listView,
                LvmHitTest,
                nint.Zero,
                remoteMemory,
                SmtoBlock | SmtoAbortIfHung,
                100,
                out var hitResult);
            return sent == nint.Zero ? 0 : hitResult.ToInt32();
        }
        finally
        {
            if (remoteMemory != nint.Zero)
            {
                VirtualFreeEx(process, remoteMemory, 0, MemRelease);
            }

            CloseHandle(process);
        }
    }

    private static nint FindDesktopListView()
    {
        var progman = FindWindow("Progman", null);
        var shellView = FindWindowEx(progman, nint.Zero, "SHELLDLL_DefView", null);
        if (shellView != nint.Zero)
        {
            return FindWindowEx(shellView, nint.Zero, "SysListView32", "FolderView");
        }

        nint result = nint.Zero;
        EnumWindows((window, _) =>
        {
            var view = FindWindowEx(window, nint.Zero, "SHELLDLL_DefView", null);
            if (view == nint.Zero)
            {
                return true;
            }

            result = FindWindowEx(view, nint.Zero, "SysListView32", "FolderView");
            return result == nint.Zero;
        }, nint.Zero);
        return result;
    }

    private static string GetWindowClassName(nint window)
    {
        var buffer = new char[128];
        var length = GetClassName(window, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private delegate nint LowLevelMouseProc(int code, nint message, nint data);
    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public Point Position;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ListViewHitTestInfo
    {
        public Point Position;
        public uint Flags;
        public int Item;
        public int SubItem;
        public int Group;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelMouseProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, char[] className, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        nint process,
        nint baseAddress,
        ref ListViewHitTestInfo buffer,
        nuint size,
        out nuint bytesWritten);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
