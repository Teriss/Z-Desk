using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ZDesk.Services;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ZDesk.Windows;

public partial class QrRecognitionFrameWindow : Window
{
    private const int WmDpiChanged = 0x02E0;
    private const int WmNcHitTest = 0x0084;
    private static readonly nint HtTransparent = -1;
    private DrawingRectangle _captureBounds;
    private DrawingRectangle _windowBounds;
    private int _headerHeightPixels;
    private DrawingRectangle _dragStart;
    private DrawingPoint _dragCursor;
    private bool _draggingTitle;
    private bool _draggingResize;
    private HwndSource? _source;

    public event Action<DrawingRectangle>? BoundsChanged;
    public event Action<int, int>? MoveRequested;
    public event Action? BoundsChangeCompleted;
    public event Action? MoveCompleted;
    public event Action? RecognizeRequested;
    public event Action? CloseRequested;
    public event Action? FrameDpiChanged;
    public DrawingRectangle FrameBounds => _captureBounds;
    public DrawingRectangle WindowBounds => _windowBounds;
    public nint Handle => new WindowInteropHelper(this).Handle;

    public QrRecognitionFrameWindow(DrawingRectangle bounds)
    {
        InitializeComponent();
        _captureBounds = bounds;
        _windowBounds = bounds;
        _headerHeightPixels = 34;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(WndProc);
        RefreshVisuals();
    }

    public void SetFrameBounds(DrawingRectangle captureBounds, DrawingRectangle windowBounds, int headerHeightPixels)
    {
        _captureBounds = captureBounds;
        _windowBounds = windowBounds;
        _headerHeightPixels = Math.Max(1, headerHeightPixels);
        RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        if (!IsInitialized) return;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var scaleX = transform.M11 > 0 ? transform.M11 : 1;
        var scaleY = transform.M22 > 0 ? transform.M22 : 1;
        var headerDip = _headerHeightPixels / scaleY;
        TitleBar.Height = headerDip;
        CaptureRoot.Margin = new Thickness(0, headerDip, 0, 0);
        Root.Width = Math.Max(1, _windowBounds.Width / scaleX);
        Root.Height = Math.Max(1, _windowBounds.Height / scaleY);
        SizeText.Text = $"{_captureBounds.Width} × {_captureBounds.Height} px";
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: string value }) return;
        Activate();
        Focus();
        GetCursorPos(out var point);
        _dragStart = _captureBounds;
        _dragCursor = new DrawingPoint(point.X, point.Y);
        _draggingResize = true;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_draggingResize || sender is not Thumb { Tag: string value }) return;
        GetCursorPos(out var point);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var minimumWidth = Math.Max(1, (int)Math.Ceiling(QrRecognitionFrameGeometry.MinimumWidthDip * Math.Max(1, transform.M11)));
        var minimumHeight = Math.Max(1, (int)Math.Ceiling(QrRecognitionFrameGeometry.MinimumHeightDip * Math.Max(1, transform.M22)));
        var next = QrRecognitionFrameGeometry.Resize(_dragStart,
            point.X - _dragCursor.X, point.Y - _dragCursor.Y, ParseHandle(value), minimumWidth, minimumHeight);
        _captureBounds = next;
        BoundsChanged?.Invoke(next);
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_draggingResize) return;
        _draggingResize = false;
        BoundsChangeCompleted?.Invoke();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsButtonSource(e.OriginalSource as DependencyObject)) return;
        Activate();
        Focus();
        GetCursorPos(out var point);
        _dragCursor = new DrawingPoint(point.X, point.Y);
        _draggingTitle = true;
        TitleBar.CaptureMouse();
        TitleBar.MouseMove += TitleBar_MouseMove;
        TitleBar.MouseLeftButtonUp += TitleBar_MouseLeftButtonUp;
        TitleBar.LostMouseCapture += TitleBar_LostMouseCapture;
        e.Handled = true;
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingTitle || !TitleBar.IsMouseCaptured) return;
        GetCursorPos(out var point);
        var dx = point.X - _dragCursor.X;
        var dy = point.Y - _dragCursor.Y;
        _dragCursor = new DrawingPoint(point.X, point.Y);
        if (dx != 0 || dy != 0) MoveRequested?.Invoke(dx, dy);
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CompleteTitleDrag();
    private void TitleBar_LostMouseCapture(object sender, MouseEventArgs e) => CompleteTitleDrag();

    private void CompleteTitleDrag()
    {
        if (!_draggingTitle) return;
        _draggingTitle = false;
        if (TitleBar.IsMouseCaptured) TitleBar.ReleaseMouseCapture();
        TitleBar.MouseMove -= TitleBar_MouseMove;
        TitleBar.MouseLeftButtonUp -= TitleBar_MouseLeftButtonUp;
        TitleBar.LostMouseCapture -= TitleBar_LostMouseCapture;
        MoveCompleted?.Invoke();
    }

    private static bool IsButtonSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }

    private static ResizeHandle ParseHandle(string value) => value switch
    {
        "Left" => ResizeHandle.Left,
        "Right" => ResizeHandle.Right,
        "Top" => ResizeHandle.Top,
        "Bottom" => ResizeHandle.Bottom,
        "TopLeft" => ResizeHandle.Top | ResizeHandle.Left,
        "TopRight" => ResizeHandle.Top | ResizeHandle.Right,
        "BottomLeft" => ResizeHandle.Bottom | ResizeHandle.Left,
        _ => ResizeHandle.Bottom | ResizeHandle.Right
    };

    private void RecognizeButton_Click(object sender, RoutedEventArgs e) => RecognizeRequested?.Invoke();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RecognizeRequested?.Invoke();
        else if (e.Key == Key.Escape) CloseRequested?.Invoke();
        else return;
        e.Handled = true;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmNcHitTest)
        {
            var point = PointFromLParam(lParam);
            var localX = point.X - _windowBounds.Left;
            var localY = point.Y - _windowBounds.Top;
            if (!IsInteractivePoint(localX, localY))
            {
                handled = true;
                return HtTransparent;
            }
        }
        else if (message == WmDpiChanged)
        {
            // Keep the physical RECT owned by the controller; ignore the system's
            // suggested position, which may be expressed in the previous monitor's DPI.
            handled = true;
            Dispatcher.BeginInvoke(() => FrameDpiChanged?.Invoke(), DispatcherPriority.ContextIdle);
        }
        return nint.Zero;
    }

    private bool IsInteractivePoint(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _windowBounds.Width || y >= _windowBounds.Height) return false;
        if (y < _headerHeightPixels) return true;
        var cx = x;
        var cy = y - _headerHeightPixels;
        const int edgePixels = 12;
        const int cornerPixels = 18;
        return cx < edgePixels || cy < edgePixels || cx >= _captureBounds.Width - edgePixels || cy >= _captureBounds.Height - edgePixels ||
            (cx < cornerPixels && cy < cornerPixels) ||
            (cx >= _captureBounds.Width - cornerPixels && cy < cornerPixels) ||
            (cx < cornerPixels && cy >= _captureBounds.Height - cornerPixels) ||
            (cx >= _captureBounds.Width - cornerPixels && cy >= _captureBounds.Height - cornerPixels);
    }

    private static DrawingPoint PointFromLParam(nint value) => new(
        (short)((long)value & 0xFFFF), (short)(((long)value >> 16) & 0xFFFF));

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
}
