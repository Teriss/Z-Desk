using System.Runtime.InteropServices;

namespace ZDesk.Services;

/// <summary>Locates the Explorer WorkerW desktop host without injecting into Explorer.</summary>
public static class WorkerWHostService
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private static nint _cachedHost;

    public static nint FindHost()
    {
        var progman = GetShellWindow();
        if (progman == nint.Zero) return nint.Zero;
        GetWindowThreadProcessId(progman, out var explorerProcessId);
        if (_cachedHost != nint.Zero && IsWindow(_cachedHost))
        {
            GetWindowThreadProcessId(_cachedHost, out var cachedProcessId);
            if (cachedProcessId == explorerProcessId) return _cachedHost;
        }
        _cachedHost = nint.Zero;
        SendMessageTimeout(progman, 0x052C, nint.Zero, nint.Zero, 0, 1000, out _);
        nint worker = nint.Zero;
        EnumWindows((window, _) =>
        {
            var shell = FindWindowEx(window, nint.Zero, "SHELLDLL_DefView", null);
            if (shell != nint.Zero)
            {
                worker = FindExplorerWorkerAfter(window, explorerProcessId);
                return false;
            }
            return true;
        }, nint.Zero);
        if (worker != nint.Zero) return _cachedHost = worker;
        worker = FindExplorerWorkerAfter(nint.Zero, explorerProcessId);
        return _cachedHost = worker != nint.Zero ? worker : progman;
    }

    private static nint FindExplorerWorkerAfter(nint after, uint explorerProcessId)
    {
        for (var candidate = FindWindowEx(nint.Zero, after, "WorkerW", null);
             candidate != nint.Zero;
             candidate = FindWindowEx(nint.Zero, candidate, "WorkerW", null))
        {
            GetWindowThreadProcessId(candidate, out var processId);
            var style = GetWindowLongPtr(candidate, GwlExStyle).ToInt64();
            if (processId == explorerProcessId && (style & WsExTopmost) == 0) return candidate;
        }
        return nint.Zero;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
