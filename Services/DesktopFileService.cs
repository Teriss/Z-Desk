using System.IO;
using System.Windows.Threading;

namespace ZDesk.Services;

public sealed class DesktopFileService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, DesktopFileChange> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    public string UserDesktop { get; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string CommonDesktop { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    public event EventHandler<DesktopFilesChangedEventArgs>? Changed;

    public DesktopFileService(Dispatcher dispatcher)
    {
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            DesktopFileChange[] changes;
            lock (_pendingChanges)
            {
                changes = _pendingChanges.Values.ToArray();
                _pendingChanges.Clear();
            }
            Changed?.Invoke(this, new DesktopFilesChangedEventArgs(changes));
        };
        StartWatcher(UserDesktop);
        if (!string.Equals(UserDesktop, CommonDesktop, StringComparison.OrdinalIgnoreCase)) StartWatcher(CommonDesktop);
    }

    public IReadOnlyList<string> EnumerateItems()
    {
        var results = new List<string>();
        AddDirectoryItems(UserDesktop, results);
        if (!string.Equals(UserDesktop, CommonDesktop, StringComparison.OrdinalIgnoreCase)) AddDirectoryItems(CommonDesktop, results);
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool IsDesktopPath(string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return string.Equals(parent, UserDesktop, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parent, CommonDesktop, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }

    private static void AddDirectoryItems(string folder, ICollection<string> results)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder)) results.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"Could not enumerate desktop folder: {folder}", ex);
        }
    }

    private void StartWatcher(string folder)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"Could not watch desktop folder: {folder}", ex);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var change = e is RenamedEventArgs renamed
            ? new DesktopFileChange(WatcherChangeTypes.Renamed, renamed.FullPath, renamed.OldFullPath)
            : new DesktopFileChange(e.ChangeType, e.FullPath);
        lock (_pendingChanges) _pendingChanges[change.FullPath] = change;
        _ = _refreshTimer.Dispatcher.BeginInvoke(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }
}

public sealed record DesktopFileChange(WatcherChangeTypes ChangeType, string FullPath, string? OldFullPath = null);
public sealed class DesktopFilesChangedEventArgs(IReadOnlyList<DesktopFileChange> changes) : EventArgs
{
    public IReadOnlyList<DesktopFileChange> Changes { get; } = changes;
}
