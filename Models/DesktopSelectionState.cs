namespace ZDesk.Models;

/// <summary>
/// Presentation-independent file selection. WPF and native desktop hosts use
/// this as the single source of truth instead of retaining selection inside a
/// visual item container.
/// </summary>
internal sealed class DesktopSelectionState
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Paths => _paths;
    public string? FocusedPath { get; private set; }

    public event EventHandler? Changed;

    public void Replace(IEnumerable<string> paths, string? focusedPath = null)
    {
        var replacement = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_paths.SetEquals(replacement) && string.Equals(FocusedPath, focusedPath, StringComparison.OrdinalIgnoreCase)) return;
        _paths.Clear();
        _paths.UnionWith(replacement);
        FocusedPath = focusedPath is not null && replacement.Contains(focusedPath)
            ? focusedPath
            : replacement.FirstOrDefault();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Select(string path, bool toggle, bool extend, IReadOnlyList<string> orderedPaths)
    {
        if (extend && FocusedPath is not null)
        {
            var anchor = IndexOf(orderedPaths, FocusedPath);
            var target = IndexOf(orderedPaths, path);
            if (anchor >= 0 && target >= 0)
            {
                Replace(orderedPaths.Skip(Math.Min(anchor, target)).Take(Math.Abs(anchor - target) + 1), path);
                return;
            }
        }

        if (toggle)
        {
            var updated = _paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!updated.Add(path)) updated.Remove(path);
            Replace(updated, updated.Contains(path) ? path : updated.FirstOrDefault());
            return;
        }

        Replace([path], path);
    }

    public void Clear() => Replace([]);

    private static int IndexOf(IReadOnlyList<string> paths, string path)
    {
        for (var index = 0; index < paths.Count; index++)
            if (string.Equals(paths[index], path, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }
}
