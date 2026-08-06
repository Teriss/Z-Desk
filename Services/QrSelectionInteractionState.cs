using System.Drawing;

namespace ZDesk.Services;

internal sealed class QrSelectionInteractionState
{
    private Point? _start;

    public QrSelectionInteractionMode Mode { get; private set; }

    public bool IsDragging => Mode == QrSelectionInteractionMode.Dragging;
    public bool IsExiting => Mode == QrSelectionInteractionMode.Exiting;

    public bool Begin(Point point)
    {
        if (Mode != QrSelectionInteractionMode.Idle) return false;
        _start = point;
        Mode = QrSelectionInteractionMode.Dragging;
        return true;
    }

    public Rectangle CurrentSelection(Point point) => _start is { } start
        ? QrSelectionGeometry.Normalize(start, point)
        : Rectangle.Empty;

    public Rectangle? Complete(Point point)
    {
        var selection = CurrentSelection(point);
        _start = null;
        Mode = QrSelectionInteractionMode.Idle;
        return QrSelectionGeometry.IsValid(selection) ? selection : null;
    }

    public bool BeginExit()
    {
        if (Mode == QrSelectionInteractionMode.Exiting) return false;
        _start = null;
        Mode = QrSelectionInteractionMode.Exiting;
        return true;
    }

    public bool CompleteExit()
    {
        if (!IsExiting) return false;
        Mode = QrSelectionInteractionMode.Idle;
        return true;
    }

    public void Reset()
    {
        _start = null;
        Mode = QrSelectionInteractionMode.Idle;
    }
}

internal enum QrSelectionInteractionMode
{
    Idle,
    Dragging,
    Exiting
}
