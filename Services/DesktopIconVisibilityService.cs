using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ZDesk.Services;

public sealed class DesktopIconVisibilityService : IDisposable
{
    private const uint Synchronize = 0x00100000;
    private const uint Infinite = 0xFFFFFFFF;
    private readonly DispatcherTimer _monitorTimer;
    private bool _ownsHiddenState;

    public DesktopIconVisibilityService(Dispatcher dispatcher)
    {
        _monitorTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _monitorTimer.Tick += (_, _) => EnsureHidden();
    }

    public bool HideAndGuard()
    {
        var listView = FindDesktopListView();
        if (listView == nint.Zero) return false;
        _ownsHiddenState = true;
        if (IsWindowVisible(listView)) ShowWindow(listView, 0);

        // The watchdog is required even when Explorer was already hidden: it
        // owns recovery after an ungraceful Z-Desk termination.
        StartWatchdog();
        _monitorTimer.Start();
        return true;
    }

    public void Restore()
    {
        _monitorTimer.Stop();
        if (!_ownsHiddenState) return;
        var listView = FindDesktopListView();
        if (listView != nint.Zero) ShowWindow(listView, 5);
        _ownsHiddenState = false;
    }

    public void Dispose() => Restore();

    public static bool TryRunWatchdog(string[] args)
    {
        if (args.Length < 2 || !args[0].Equals("--explorer-icon-watchdog", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(args[1], out var processId)) return false;

        var process = OpenProcess(Synchronize, false, (uint)processId);
        if (process != nint.Zero)
        {
            WaitForSingleObject(process, Infinite);
            CloseHandle(process);
        }

        var listView = FindDesktopListView();
        if (listView != nint.Zero) ShowWindow(listView, 5);
        return true;
    }

    private void EnsureHidden()
    {
        if (!_ownsHiddenState) return;
        var listView = FindDesktopListView();
        if (listView != nint.Zero && IsWindowVisible(listView)) ShowWindow(listView, 0);
    }

    private static void StartWatchdog()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"--explorer-icon-watchdog {Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static nint FindDesktopListView()
    {
        var progman = FindWindow("Progman", null);
        var view = FindWindowEx(progman, nint.Zero, "SHELLDLL_DefView", null);
        if (view != nint.Zero) return FindWindowEx(view, nint.Zero, "SysListView32", "FolderView");

        nint result = nint.Zero;
        EnumWindows((window, _) =>
        {
            view = FindWindowEx(window, nint.Zero, "SHELLDLL_DefView", null);
            if (view == nint.Zero) return true;
            result = FindWindowEx(view, nint.Zero, "SysListView32", "FolderView");
            return result == nint.Zero;
        }, nint.Zero);
        return result;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
