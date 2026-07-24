using System.IO;
using System.Windows.Threading;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class RuleWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _debounceTimer;
    public event EventHandler? RulesTriggered;

    public RuleWatcherService(Dispatcher dispatcher)
    {
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); RulesTriggered?.Invoke(this, EventArgs.Empty); };
    }

    public void Configure(IEnumerable<ClassificationRule> rules, bool enabled)
    {
        ClearWatchers();
        if (!enabled) return;
        foreach (var folder in rules.Where(rule => rule.Enabled && Directory.Exists(rule.SourceFolder))
                     .Select(rule => rule.SourceFolder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime, EnableRaisingEvents = true };
                watcher.Created += OnChanged;
                watcher.Renamed += OnChanged;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogService.Warning($"Could not watch rule source: {folder}", ex); }
        }
    }

    public void Dispose() { _debounceTimer.Stop(); ClearWatchers(); }
    private void OnChanged(object sender, FileSystemEventArgs e) => _ = _debounceTimer.Dispatcher.BeginInvoke(() => { _debounceTimer.Stop(); _debounceTimer.Start(); });
    private void ClearWatchers() { foreach (var watcher in _watchers) watcher.Dispose(); _watchers.Clear(); }
}
