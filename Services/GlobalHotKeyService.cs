using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    private const int HotKeyIdBase = 0x5A44;
    private const int WmHotKey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private HwndSource? _source;
    private nint _windowHandle;
    private readonly Dictionary<int, (int BindingId, HotKeyGesture Gesture)> _registered = [];

    public event Action<int>? BindingPressed;
    public event EventHandler? Pressed;
    public IReadOnlyDictionary<int, (int BindingId, HotKeyGesture Gesture)> Registered => _registered;

    public void Attach(Window window)
    {
        if (_windowHandle != nint.Zero) return;
        _windowHandle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowMessageHook);
    }

    public bool TryRegister(string text, out string error)
    {
        if (!HotKeyParser.TryParse(text, out var gesture, out error) || gesture is null) return false;
        return ReplaceAll([(0, gesture)], out error);
    }

    public bool ReplaceAll(IReadOnlyList<(int BindingId, HotKeyGesture Gesture)> bindings, out string error)
    {
        error = string.Empty;
        if (_windowHandle == nint.Zero)
        {
            error = "主窗口尚未准备好。";
            return false;
        }

        if (bindings.GroupBy(item => item.Gesture.DisplayText, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            error = "快捷键不能重复。";
            return false;
        }

        var previous = _registered.ToArray();
        UnregisterCurrent();
        var registered = new Dictionary<int, (int BindingId, HotKeyGesture Gesture)>();
        foreach (var (bindingId, gesture) in bindings)
        {
            var nativeId = HotKeyIdBase + (int)(Math.Abs((long)bindingId.GetHashCode()) % 100000);
            while (registered.ContainsKey(nativeId)) nativeId++;
            if (!RegisterGesture(nativeId, gesture))
            {
                foreach (var id in registered.Keys) UnregisterHotKey(_windowHandle, id);
                foreach (var (oldId, oldBinding) in previous) RegisterGesture(oldId, oldBinding.Gesture);
                _registered.Clear();
                foreach (var (oldId, oldBinding) in previous) _registered[oldId] = oldBinding;
                error = $"快捷键“{gesture.DisplayText}”已被其他程序占用。";
                return false;
            }
            registered[nativeId] = (bindingId, gesture);
        }

        foreach (var item in registered) _registered[item.Key] = item.Value;
        return true;
    }

    public void Dispose()
    {
        UnregisterCurrent();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        _windowHandle = nint.Zero;
    }

    private bool RegisterGesture(int id, HotKeyGesture gesture) => RegisterHotKey(
        _windowHandle,
        id,
        (uint)gesture.Modifiers | ModNoRepeat,
        gesture.VirtualKey);

    private void UnregisterCurrent()
    {
        if (_windowHandle != nint.Zero)
            foreach (var id in _registered.Keys.ToArray()) UnregisterHotKey(_windowHandle, id);
        _registered.Clear();
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotKey && _registered.ContainsKey(wParam.ToInt32()))
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            BindingPressed?.Invoke(_registered[wParam.ToInt32()].BindingId);
            handled = true;
        }
        return nint.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
