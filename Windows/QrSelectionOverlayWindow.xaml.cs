using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ZDesk.Models;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ZDesk.Windows;

public partial class QrSelectionOverlayWindow : Window
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private readonly DrawingRectangle _bounds;
    private bool _pointerSelecting;
    private bool _rightButtonExitPending;

    public event Action<QrSelectionOverlayWindow, DrawingPoint>? PointerDown;
    public event Action<QrSelectionOverlayWindow, DrawingPoint>? PointerMoved;
    public event Action<QrSelectionOverlayWindow, DrawingPoint>? PointerUp;
    public event Action<QrSelectionOverlayWindow>? RightButtonExitStarted;
    public event Action<QrSelectionOverlayWindow>? RightButtonExitCompleted;
    public event Action<QrSelectionOverlayWindow>? ExitRequested;
    public event Action<QrSelectionOverlayWindow>? PointerCaptureLost;

    public QrSelectionOverlayWindow(DrawingRectangle bounds, ImageSource snapshot)
    {
        InitializeComponent();
        _bounds = bounds;
        SnapshotImage.Source = snapshot;
        SelectionSnapshot.Source = snapshot;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(
            handle,
            nint.Zero,
            _bounds.Left,
            _bounds.Top,
            _bounds.Width,
            _bounds.Height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public void ShowSelection(DrawingRectangle selection)
    {
        var visible = DrawingRectangle.Intersect(selection, _bounds);
        if (visible.IsEmpty)
        {
            ClearSelection();
            return;
        }

        var topLeft = PointFromScreen(new System.Windows.Point(visible.Left, visible.Top));
        var bottomRight = PointFromScreen(new System.Windows.Point(visible.Right, visible.Bottom));
        SelectionBorder.Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
        SelectionBorder.Width = Math.Max(0, bottomRight.X - topLeft.X);
        SelectionBorder.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
        SelectionSnapshot.Clip = new RectangleGeometry(new Rect(
            topLeft.X,
            topLeft.Y,
            SelectionBorder.Width,
            SelectionBorder.Height));
        SelectionSnapshot.Visibility = Visibility.Visible;
        SelectionBorder.Visibility = Visibility.Visible;
    }

    public void ClearSelection()
    {
        SelectionSnapshot.Clip = null;
        SelectionSnapshot.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Collapsed;
    }

    public void CancelPointerSelection()
    {
        _pointerSelecting = false;
        if (Mouse.Captured == OverlaySurface) OverlaySurface.ReleaseMouseCapture();
    }

    public bool ContainsScreenPoint(DrawingPoint point) => _bounds.Contains(point);

    public void FocusForInput()
    {
        Activate();
        OverlaySurface.Focus();
        Keyboard.Focus(OverlaySurface);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_rightButtonExitPending)
        {
            e.Handled = true;
            return;
        }
        _pointerSelecting = true;
        OverlaySurface.CaptureMouse();
        PointerDown?.Invoke(this, GetCursorPosition());
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerSelecting || _rightButtonExitPending) return;
        PointerMoved?.Invoke(this, GetCursorPosition());
        e.Handled = true;
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerSelecting || _rightButtonExitPending)
        {
            e.Handled = _rightButtonExitPending;
            return;
        }
        _pointerSelecting = false;
        if (Mouse.Captured == OverlaySurface) OverlaySurface.ReleaseMouseCapture();
        PointerUp?.Invoke(this, GetCursorPosition());
        e.Handled = true;
    }

    private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pointerSelecting = false;
        _rightButtonExitPending = true;
        OverlaySurface.CaptureMouse();
        RightButtonExitStarted?.Invoke(this);
        e.Handled = true;
    }

    private void Surface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rightButtonExitPending) return;
        _rightButtonExitPending = false;
        if (Mouse.Captured == OverlaySurface) OverlaySurface.ReleaseMouseCapture();
        RightButtonExitCompleted?.Invoke(this);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ExitRequested?.Invoke(this);
        e.Handled = true;
    }

    private void Surface_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_pointerSelecting) return;
        _pointerSelecting = false;
        PointerCaptureLost?.Invoke(this);
    }

    private static DrawingPoint GetCursorPosition()
    {
        GetCursorPos(out var point);
        return new DrawingPoint(point.X, point.Y);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
