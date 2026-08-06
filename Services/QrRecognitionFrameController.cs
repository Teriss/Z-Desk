using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using ZDesk.Models;
using ZDesk.Windows;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ZDesk.Services;

public sealed class QrRecognitionFrameController : IDisposable
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;
    private readonly Window _owner;
    private readonly Func<QrRecognitionFrameBounds?> _loadBounds;
    private readonly Action<QrRecognitionFrameBounds> _saveBounds;
    private QrRecognitionFrameWindow? _frameWindow;
    private DrawingRectangle _frameBounds;
    private QrRecognitionFrameLayout? _lastLayout;
    private bool _boundsLoaded;
    private bool _disposing;

    public bool IsVisible => _frameWindow?.IsVisible == true;
    public bool IsActive => _frameWindow is not null;
    public event Action<DrawingRectangle>? RecognitionRequested;

    public QrRecognitionFrameController(Window owner, Func<QrRecognitionFrameBounds?> loadBounds, Action<QrRecognitionFrameBounds> saveBounds)
    {
        _owner = owner;
        _loadBounds = loadBounds;
        _saveBounds = saveBounds;
    }

    public void Show()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;
        var cursor = Cursor.Position;
        var displayBounds = screens.Select(screen => screen.Bounds).ToArray();
        var workAreas = screens.Select(screen => screen.WorkingArea).ToArray();
        if (!_boundsLoaded)
        {
            var saved = _loadBounds()?.ToRectangle();
            var scale = GetMonitorScale(cursor);
            var header = QrRecognitionFrameGeometry.HeaderHeightPixels(scale);
            _frameBounds = saved is { } frame
                ? QrRecognitionFrameGeometry.ClampToWorkArea(frame, workAreas, cursor, header)
                : CreateDefaultBounds(cursor, displayBounds, scale);
            _boundsLoaded = true;
        }

        EnsureWindow();
        _frameWindow!.Show();
        ApplyLayout(forceResize: true);
        _frameWindow.Activate();
        _frameWindow.Focus();
    }

    public void Hide()
    {
        if (_frameWindow is null) return;
        SaveBounds(_frameBounds);
        _frameWindow.Hide();
        _lastLayout = null;
    }

    public void HideForCapture()
    {
        _frameWindow?.Hide();
        _lastLayout = null;
    }

    public void RestoreAfterFailure() => Show();

    public void RefreshDisplayLayout()
    {
        if (_frameWindow is null) return;
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;
        var workAreas = screens.Select(screen => screen.WorkingArea).ToArray();
        var header = QrRecognitionFrameGeometry.HeaderHeightPixels(GetMonitorScale(_frameBounds.Location));
        _frameBounds = QrRecognitionFrameGeometry.ClampToWorkArea(_frameBounds, workAreas, Cursor.Position, header);
        ApplyLayout(forceResize: true);
        SaveBounds(_frameBounds);
    }

    private void EnsureWindow()
    {
        if (_frameWindow is not null) return;
        _frameWindow = new QrRecognitionFrameWindow(_frameBounds) { Owner = _owner };
        _frameWindow.BoundsChanged += FrameWindow_BoundsChanged;
        _frameWindow.BoundsChangeCompleted += SaveCurrentBounds;
        _frameWindow.MoveRequested += MoveFrame;
        _frameWindow.MoveCompleted += SaveCurrentBounds;
        _frameWindow.RecognizeRequested += FrameWindow_RecognizeRequested;
        _frameWindow.CloseRequested += Hide;
        _frameWindow.FrameDpiChanged += () => ApplyLayout(forceResize: true);
        _frameWindow.Closed += Window_Closed;
    }

    private void FrameWindow_BoundsChanged(DrawingRectangle bounds)
    {
        var sizeChanged = _frameBounds.Size != bounds.Size;
        _frameBounds = bounds;
        ApplyLayout(forceResize: sizeChanged);
    }

    private void MoveFrame(int dx, int dy)
    {
        _frameBounds = QrRecognitionFrameGeometry.Move(_frameBounds, dx, dy);
        ApplyLayout();
    }

    private void SaveCurrentBounds() => SaveBounds(_frameBounds);

    private void FrameWindow_RecognizeRequested()
    {
        SaveBounds(_frameBounds);
        HideForCapture();
        var bounds = _frameBounds;
        _owner.Dispatcher.BeginInvoke(new Action(() => RecognitionRequested?.Invoke(bounds)), DispatcherPriority.Render);
    }

    private void ApplyLayout(bool forceResize = false)
    {
        if (_frameWindow is null || _frameWindow.Handle == nint.Zero) return;
        var workAreas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
        if (workAreas.Length == 0) return;
        var target = QrRecognitionFrameGeometry.SelectTargetWorkArea(_frameBounds, workAreas, Cursor.Position);
        var scale = GetMonitorScale(new DrawingPoint(
            target.IsEmpty ? _frameBounds.Left : target.Left + target.Width / 2,
            target.IsEmpty ? _frameBounds.Top : target.Top + target.Height / 2));
        var header = QrRecognitionFrameGeometry.HeaderHeightPixels(scale);
        var layout = QrRecognitionFrameGeometry.CalculateLayout(_frameBounds, workAreas, Cursor.Position, header);
        var resize = forceResize || _lastLayout is null || _lastLayout.WindowBounds.Size != layout.WindowBounds.Size;
        _frameWindow.SetFrameBounds(layout.CaptureBounds, layout.WindowBounds, layout.HeaderHeightPixels);
        var flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging;
        if (!resize) flags |= 0x0001; // SWP_NOSIZE during title-bar movement.
        SetWindowPos(_frameWindow.Handle, nint.Zero,
            layout.WindowBounds.Left, layout.WindowBounds.Top,
            layout.WindowBounds.Width, layout.WindowBounds.Height, flags);
        _lastLayout = layout;
    }

    private DrawingRectangle CreateDefaultBounds(DrawingPoint cursor, IReadOnlyList<DrawingRectangle> displays, double scale)
        => QrRecognitionFrameGeometry.CreateDefault(cursor, displays,
            (int)Math.Round(QrRecognitionFrameGeometry.DefaultWidthDip * scale),
            (int)Math.Round(QrRecognitionFrameGeometry.DefaultHeightDip * scale));

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_disposing) return;
        _frameWindow?.Hide();
    }

    private void SaveBounds(DrawingRectangle bounds) =>
        _saveBounds(new QrRecognitionFrameBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height));

    public void Dispose()
    {
        _disposing = true;
        if (_frameWindow is not null) SaveBounds(_frameBounds);
        _frameWindow?.Close();
        _frameWindow = null;
    }

    private static double GetMonitorScale(DrawingPoint point)
    {
        var monitor = MonitorFromPoint(new NativePoint(point.X, point.Y), 2);
        return monitor != nint.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
            ? dpiX / 96.0 : 1.0;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, uint type, out uint dpiX, out uint dpiY);
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativePoint
    {
        public NativePoint(int x, int y) { X = x; Y = y; }
        public readonly int X;
        public readonly int Y;
    }
}
