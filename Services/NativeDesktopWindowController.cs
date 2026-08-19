using ZDesk.Controls;
using ZDesk.Models;
using ZDesk.Windows;

namespace ZDesk.Services;

/// <summary>
/// Owns the lifecycle of an independent native desktop window and its model
/// bridge. Keeping this boundary outside MainWindow makes the eventual native
/// presentation switch explicit and prevents accidental WPF Window ownership.
/// </summary>
internal sealed class NativeDesktopWindowController : IDisposable
{
    private readonly GroupContainer _group;
    private readonly NativeDesktopWindow _window;
    private bool _disposed;

    public NativeDesktopWindowController(GroupContainer group, NativeDesktopBounds bounds, int cornerRadius = 11)
    {
        _group = group ?? throw new ArgumentNullException(nameof(group));
        _window = new NativeDesktopWindow(cornerRadius);
        _window.Create(bounds);
        _group.BindNativeDesktopWindow(_window);
        _window.BoundsChanged += Window_BoundsChanged;
    }

    public NativeDesktopWindow Window => _window;
    public GroupContainer Group => _group;
    public event EventHandler? LayoutChanged;

    public void Show() => _window.Show();
    public void Hide() => _window.Hide();
    public void Refresh() => _window.Update(_group.CreatePresentationSnapshot());

    public void ApplyDefinition()
    {
        var definition = _group.Definition;
        var bounds = new NativeDesktopBounds(
            definition.DesktopX is { } x ? (int)Math.Round(x) : (int)Math.Round(definition.X),
            definition.DesktopY is { } y ? (int)Math.Round(y) : (int)Math.Round(definition.Y),
            Math.Max(220, (int)Math.Round(definition.Width)),
            Math.Max(36, (int)Math.Round(definition.IsCollapsed ? _group.CurrentHeaderHeight : definition.Height)));
        _window.SetBounds(bounds);
        _window.DockEdge = definition.DockEdge;
        Refresh();
    }

    private void Window_BoundsChanged(object? sender, EventArgs e)
    {
        var bounds = _window.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var definition = _group.Definition;
        definition.DesktopX = bounds.X;
        definition.DesktopY = bounds.Y;
        definition.Width = bounds.Width;
        if (!definition.IsCollapsed) definition.Height = bounds.Height;
        definition.DisplayDeviceName = System.Windows.Forms.Screen.FromHandle(_window.Handle).DeviceName;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.BoundsChanged -= Window_BoundsChanged;
        _group.UnbindNativeDesktopWindow();
        _window.Dispose();
    }
}
