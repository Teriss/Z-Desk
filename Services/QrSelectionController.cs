using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZDesk.Models;
using ZDesk.Windows;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ZDesk.Services;

public sealed class QrSelectionController : IDisposable
{
    private readonly Window _owner;
    private readonly List<QrSelectionOverlayWindow> _overlays = [];
    private readonly QrSelectionInteractionState _interaction = new();
    private TaskCompletionSource<DrawingRectangle?>? _completion;
    private QrSelectionOverlayWindow? _captureOwner;
    private bool _finishing;

    public QrSelectionController(Window owner) => _owner = owner;

    public bool IsSelecting => _completion is not null;

    public Task<DrawingRectangle?> SelectAsync(QrDesktopCapture capture)
    {
        if (IsSelecting) throw new InvalidOperationException("A QR selection is already active.");
        if (capture.DisplayBounds.Count == 0) return Task.FromResult<DrawingRectangle?>(null);

        _interaction.Reset();
        _completion = new TaskCompletionSource<DrawingRectangle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = ScreenCaptureService.ToBitmapSource(capture.Frame);
        foreach (var displayBounds in capture.DisplayBounds)
        {
            var croppedSnapshot = new CroppedBitmap(snapshot, new Int32Rect(
                displayBounds.Left - capture.Frame.Bounds.Left,
                displayBounds.Top - capture.Frame.Bounds.Top,
                displayBounds.Width,
                displayBounds.Height));
            croppedSnapshot.Freeze();
            var overlay = new QrSelectionOverlayWindow(displayBounds, croppedSnapshot)
            {
                Owner = _owner
            };
            overlay.PointerDown += Overlay_PointerDown;
            overlay.PointerMoved += Overlay_PointerMoved;
            overlay.PointerUp += Overlay_PointerUp;
            overlay.RightButtonExitStarted += Overlay_RightButtonExitStarted;
            overlay.RightButtonExitCompleted += Overlay_RightButtonExitCompleted;
            overlay.ExitRequested += Overlay_ExitRequested;
            overlay.PointerCaptureLost += Overlay_PointerCaptureLost;
            overlay.Closed += Overlay_Closed;
            _overlays.Add(overlay);
            overlay.Show();
        }

        var cursor = System.Windows.Forms.Cursor.Position;
        (_overlays.FirstOrDefault(overlay => overlay.ContainsScreenPoint(cursor)) ?? _overlays[0]).FocusForInput();
        return _completion.Task;
    }

    public void Dispose() => Finish(null);

    private void Overlay_PointerDown(QrSelectionOverlayWindow overlay, DrawingPoint point)
    {
        if (!IsSelecting) return;
        if (!_interaction.Begin(point)) return;
        _captureOwner = overlay;
        foreach (var item in _overlays) item.ClearSelection();
    }

    private void Overlay_PointerMoved(QrSelectionOverlayWindow overlay, DrawingPoint point)
    {
        if (!_interaction.IsDragging || !ReferenceEquals(overlay, _captureOwner)) return;
        ShowSelection(_interaction.CurrentSelection(point));
    }

    private void Overlay_PointerUp(QrSelectionOverlayWindow overlay, DrawingPoint point)
    {
        if (!_interaction.IsDragging || !ReferenceEquals(overlay, _captureOwner)) return;
        var selection = _interaction.Complete(point);
        _captureOwner = null;
        if (selection is { } completed)
        {
            Finish(completed);
            return;
        }
        ClearSelection();
    }

    private void Overlay_RightButtonExitStarted(QrSelectionOverlayWindow overlay)
    {
        if (!_interaction.BeginExit()) return;
        _captureOwner = null;
        ClearSelection();
    }

    private void Overlay_RightButtonExitCompleted(QrSelectionOverlayWindow overlay)
    {
        if (!_interaction.CompleteExit()) return;
        overlay.Dispatcher.BeginInvoke(() => Finish(null), DispatcherPriority.Input);
    }

    private void Overlay_ExitRequested(QrSelectionOverlayWindow overlay) => Finish(null);

    private void Overlay_PointerCaptureLost(QrSelectionOverlayWindow overlay)
    {
        if (_interaction.IsExiting || !ReferenceEquals(overlay, _captureOwner)) return;
        _interaction.Reset();
        _captureOwner = null;
        ClearSelection();
    }

    private void Overlay_Closed(object? sender, EventArgs e)
    {
        if (!_finishing && IsSelecting) Finish(null);
    }

    private void ShowSelection(DrawingRectangle selection)
    {
        foreach (var overlay in _overlays) overlay.ShowSelection(selection);
    }

    private void ClearSelection()
    {
        foreach (var overlay in _overlays) overlay.ClearSelection();
    }

    private void Finish(DrawingRectangle? selection)
    {
        if (_finishing || _completion is null) return;
        _finishing = true;
        var completion = _completion;
        _completion = null;
        _interaction.Reset();
        _captureOwner?.CancelPointerSelection();
        _captureOwner = null;

        foreach (var overlay in _overlays.ToArray())
        {
            overlay.PointerDown -= Overlay_PointerDown;
            overlay.PointerMoved -= Overlay_PointerMoved;
            overlay.PointerUp -= Overlay_PointerUp;
            overlay.RightButtonExitStarted -= Overlay_RightButtonExitStarted;
            overlay.RightButtonExitCompleted -= Overlay_RightButtonExitCompleted;
            overlay.ExitRequested -= Overlay_ExitRequested;
            overlay.PointerCaptureLost -= Overlay_PointerCaptureLost;
            overlay.Closed -= Overlay_Closed;
            overlay.Close();
        }
        _overlays.Clear();
        _finishing = false;
        completion.TrySetResult(selection);
    }
}
