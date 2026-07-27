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
    private const uint DesktopEvents = 0x0000003F | 0x00001000 | 0x00002000;
    private readonly List<uint> _registrations = [];
    private HwndSource? _source;
    private readonly List<nint> _registeredPidls = [];

    public event EventHandler? Changed;

    public bool Start(Window window, IEnumerable<string> folders)
    {
        if (_registrations.Count > 0) return true;
        var handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(handle);
        if (handle == nint.Zero || _source is null) return false;
        _source.AddHook(WindowProc);
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SHParseDisplayName(folder, nint.Zero, out var pidl, 0, out _) == 0)
                _registeredPidls.Add(pidl);
        }
        if (_registeredPidls.Count == 0)
        {
            _source.RemoveHook(WindowProc);
            _source = null;
            return false;
        }
        foreach (var pidl in _registeredPidls)
        {
            var entry = new ShellChangeNotifyEntry { Pidl = pidl, Recursive = false };
            var registration = SHChangeNotifyRegister(handle, ShellLevel | InterruptLevel, DesktopEvents,
                NotificationMessage, 1, ref entry);
            if (registration != 0) _registrations.Add(registration);
        }
        if (_registrations.Count > 0) return true;
        _source.RemoveHook(WindowProc);
        _source = null;
        foreach (var pidl in _registeredPidls) Marshal.FreeCoTaskMem(pidl);
        _registeredPidls.Clear();
        return false;
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) SHChangeNotifyDeregister(registration);
        _registrations.Clear();
        _source?.RemoveHook(WindowProc);
        _source = null;
        foreach (var pidl in _registeredPidls) Marshal.FreeCoTaskMem(pidl);
        _registeredPidls.Clear();
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint itemIdList,
        uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotifyDeregister(uint registration);
}
