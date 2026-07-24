using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZDesk.Services;

/// <summary>Receives Shell-level change notifications without loading code into Explorer.</summary>
public sealed class ShellChangeNotificationService : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint NotificationMessage = WM_APP + 0x4D;
    private const int ShellLevel = 0x0002;
    private const int InterruptLevel = 0x0001;
    private const uint AllEvents = 0x7FFFFFFF;
    private uint _registration;
    private HwndSource? _source;

    public event EventHandler? Changed;

    public bool Start(Window window)
    {
        if (_registration != 0) return true;
        var handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(handle);
        if (handle == nint.Zero || _source is null) return false;
        _source.AddHook(WindowProc);
        var entry = new ShellChangeNotifyEntry { Pidl = nint.Zero, Recursive = true };
        _registration = SHChangeNotifyRegister(handle, ShellLevel | InterruptLevel, AllEvents,
            NotificationMessage, 1, ref entry);
        if (_registration != 0) return true;
        _source.RemoveHook(WindowProc);
        _source = null;
        return false;
    }

    public void Dispose()
    {
        if (_registration != 0) SHChangeNotifyDeregister(_registration);
        _registration = 0;
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NotificationMessage) return nint.Zero;
        Changed?.Invoke(this, EventArgs.Empty);
        return nint.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellChangeNotifyEntry
    {
        public nint Pidl;
        [MarshalAs(UnmanagedType.Bool)] public bool Recursive;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern uint SHChangeNotifyRegister(nint hwnd, int sources, uint events, uint message,
        int entries, ref ShellChangeNotifyEntry entry);
    [DllImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotifyDeregister(uint registration);
}
