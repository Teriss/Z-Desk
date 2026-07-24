namespace ZDesk.Services;

public sealed class OperationHistoryService
{
    private readonly Stack<(string Description, Func<Task> Undo)> _undoStack = [];
    public static OperationHistoryService Shared { get; } = new();
    public string? NextUndoDescription => _undoStack.TryPeek(out var item) ? item.Description : null;

    public void Record(string description, Func<Task> undo) => _undoStack.Push((description, undo));

    public async Task<bool> UndoAsync()
    {
        if (!_undoStack.TryPop(out var item)) return false;
        await item.Undo();
        return true;
    }
}
