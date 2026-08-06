using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ZDesk.Controls;
using ZDesk.Models;
using ZDesk.Services;
using ZDesk.Windows;

namespace ZDesk;

public partial class MainWindow : Window
{
    private const int QrRecognitionHotKeyBindingId = int.MinValue + 1;
    private readonly RecoveryService _recoveryService;
    private readonly LayoutStore _layoutStore = new();
    private readonly GlobalHotKeyService _hotKeyService = new();
    private readonly QrRecognitionFrameController _qrFrameController;
    private readonly DispatcherTimer _saveTimer;
    private readonly DesktopDoubleClickService _desktopDoubleClickService;
    private readonly TrayIconService _trayIconService;
    private readonly StartupService _startupService = new();
    private readonly SnapshotService _snapshotService = new();
    private readonly DiagnosticService _diagnosticService;
    private readonly DisplayLayoutProfileService _displayProfiles = new();
    private readonly DesktopFileService _desktopFiles;
    private readonly DesktopIconVisibilityService _explorerIconVisibility;
    private readonly ShellChangeNotificationService _shellChangeNotifications = new();
    private DesktopSurfaceWindow? _desktopSurface;
    private readonly List<DesktopGroupWindow> _desktopWindows = [];
    private AppState _state = new();
    private bool _groupsHidden;
    private bool _isLoaded;
    private bool _desktopMode;
    private bool _isTopmost;
    private readonly HashSet<Guid> _activeHotKeyBindings = [];
    private readonly Dictionary<int, Guid> _hotKeyBindingKeys = [];
    private QrRecognitionResultsWindow? _qrResultsWindow;
    private bool _qrRecognitionRunning;
    private bool _temporarilyRevealed;
    private bool _topmostFromHotKey;
    private bool _shutdownInProgress;
    private bool _shutdownReady;
    private bool _applicationExitRequested;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _ruleTimer;
    private readonly DispatcherTimer _edgeTimer;
    private bool _rulesRunning;
    private bool _saveRunning;
    private bool _savePending;
    private string _displaySignature = string.Empty;
    private SettingsWindow? _settingsWindow;
    private string? _pendingDesktopMenuCreateKind;
    private Point? _pendingDesktopMenuPoint;

    public MainWindow(RecoveryService recoveryService)
    {
        InitializeComponent();
        _qrFrameController = new QrRecognitionFrameController(
            this,
            () => _state.Settings.QrRecognitionFrameBounds,
            bounds =>
            {
                _state.Settings.QrRecognitionFrameBounds = bounds;
                if (_isLoaded) ScheduleSave();
            });
        _qrFrameController.RecognitionRequested += QrFrame_RecognitionRequested;
        _recoveryService = recoveryService;
        _diagnosticService = new DiagnosticService(_layoutStore);
        _desktopFiles = new DesktopFileService(Dispatcher);
        _desktopFiles.Changed += DesktopFiles_Changed;
        _shellChangeNotifications.Changed += ShellChangeNotifications_Changed;
        _explorerIconVisibility = new DesktopIconVisibilityService(Dispatcher);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += SaveTimer_Tick;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += SearchTimer_Tick;
        _ruleTimer = new DispatcherTimer();
        _ruleTimer.Tick += LayoutRuleTimer_Tick;
        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _edgeTimer.Tick += (_, _) => UpdateEdgeVisibility();
        _desktopDoubleClickService = new DesktopDoubleClickService(Dispatcher);
        _desktopDoubleClickService.DesktopBlankDoubleClicked += (_, _) =>
        {
            if (_desktopMode && _state.Settings.DoubleClickHidesGroups)
            {
                ToggleGroupsVisibility();
            }
        };
        _desktopDoubleClickService.LeftButtonClicked += point =>
        {
            if (_topmostFromHotKey && _isTopmost && !_desktopWindows.Any(window => window.IsVisible && window.IsPointWithinWindow(point)))
            {
                _activeHotKeyBindings.Clear();
                EndTopmostMode();
            }
        };

        _trayIconService = new TrayIconService();
        _trayIconService.OpenManagerRequested += (_, _) => OpenManager();
        _trayIconService.ToggleGroupsRequested += (_, _) => ToggleGroupsVisibility();
        _trayIconService.ExitApplicationRequested += (_, _) => RequestApplicationExit();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        _hotKeyService.BindingPressed += HotKeyBindingPressed;
    }

    private void DesktopFiles_Changed(object? sender, DesktopFilesChangedEventArgs e)
    {
        if (!_isLoaded) return;
        try { ReconcileDesktopGroups(e.Changes, e.RequiresFullRefresh); }
        catch (Exception ex) { LogService.Warning("Desktop file reconciliation failed", ex); }
    }

    private void ShellChangeNotifications_Changed(object? sender, EventArgs e)
    {
        if (!_isLoaded) return;
        _desktopFiles.RequestFullRefresh();
    }

    private void ReconcileDesktopGroups() => ReconcileDesktopGroups([], fullRefresh: true);

    private void ReconcileDesktopGroups(IReadOnlyList<DesktopFileChange> changes, bool fullRefresh = false)
    {
        if (!_isLoaded) return;
        if (changes.Count > 0 || fullRefresh)
            LogService.Info($"Desktop reconciliation | full={fullRefresh} | changes={string.Join(",", changes.Select(change => $"{change.ChangeType}:{Path.GetFileName(change.FullPath)}"))}");
        var changed = false;
        var affectedGroups = new HashSet<Guid>();
        foreach (var rename in changes.Where(change => change.ChangeType == WatcherChangeTypes.Renamed && change.OldFullPath is not null))
        {
            foreach (var group in _state.Groups)
            {
                var groupChanged = false;
                var activeChanged = false;
                if (group.Tabs.Count == 0 && group.Kind == GroupKind.Empty)
                {
                    groupChanged = ReplacePath(group.PinnedPaths, rename.OldFullPath!, rename.FullPath);
                    groupChanged |= ReplacePath(group.ItemOrder, rename.OldFullPath!, rename.FullPath);
                    activeChanged = groupChanged;
                }
                else if (group.Tabs.Count > 0)
                {
                    group.StoreActiveTab();
                    for (var index = 0; index < group.Tabs.Count; index++)
                    {
                        var tab = group.Tabs[index];
                        if (tab.Kind != GroupKind.Empty) continue;
                        var tabChanged = ReplacePath(tab.PinnedPaths, rename.OldFullPath!, rename.FullPath);
                        tabChanged |= ReplacePath(tab.ItemOrder, rename.OldFullPath!, rename.FullPath);
                        groupChanged |= tabChanged;
                        activeChanged |= tabChanged && index == group.ActiveTabIndex;
                    }
                    if (activeChanged) group.ReloadActiveTab();
                }
                if (!groupChanged) continue;
                changed = true;
                if (activeChanged) affectedGroups.Add(group.Id);
            }
        }

        var desktopItems = _desktopFiles.EnumerateItems().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasExplicitDeletion = fullRefresh || changes.Any(change => change.ChangeType == WatcherChangeTypes.Deleted);
        if (hasExplicitDeletion)
        {
            foreach (var group in _state.Groups)
            {
                var groupChanged = false;
                var activeChanged = false;
                if (group.Tabs.Count == 0 && group.Kind == GroupKind.Empty)
                {
                    var removed = group.PinnedPaths.Where(_desktopFiles.IsDesktopPath).Where(path => !desktopItems.Contains(path)).ToArray();
                    group.PinnedPaths.RemoveAll(removed.Contains);
                    group.ItemOrder.RemoveAll(removed.Contains);
                    groupChanged = activeChanged = removed.Length > 0;
                }
                else if (group.Tabs.Count > 0)
                {
                    group.StoreActiveTab();
                    for (var index = 0; index < group.Tabs.Count; index++)
                    {
                        var tab = group.Tabs[index];
                        if (tab.Kind != GroupKind.Empty) continue;
                        var removed = tab.PinnedPaths.Where(_desktopFiles.IsDesktopPath).Where(path => !desktopItems.Contains(path)).ToArray();
                        tab.PinnedPaths.RemoveAll(removed.Contains);
                        tab.ItemOrder.RemoveAll(removed.Contains);
                        groupChanged |= removed.Length > 0;
                        activeChanged |= removed.Length > 0 && index == group.ActiveTabIndex;
                    }
                    if (activeChanged) group.ReloadActiveTab();
                }
                if (!groupChanged) continue;
                changed = true;
                if (activeChanged) affectedGroups.Add(group.Id);
            }
        }

        // Folder-change classification is controlled by its own switch. The
        // periodic AutoRunRules option must not gate a newly-created desktop item.
        if (_state.Settings.RunRulesOnFolderChanges && _state.LayoutMatchRules.Count > 0)
        {
            var service = new LayoutAssignmentService();
            var assignedPaths = GetAssignedDesktopPaths();
            var assigned = service.Preview(
                desktopItems.Where(path => !assignedPaths.Contains(path)),
                _state.Groups,
                _state.LayoutMatchRules);
            foreach (var (path, groupId, tabId) in assigned)
            {
                var group = _state.Groups.FirstOrDefault(g => g.Id == groupId);
                if (group is null || !AddPathToLayout(group, tabId, path)) continue;
                changed = true;
                if (tabId is null || group.Tabs.ElementAtOrDefault(group.ActiveTabIndex)?.Id == tabId)
                    affectedGroups.Add(group.Id);
            }
        }

        if (changed)
        {
            LogService.Info($"Desktop reconciliation refresh | groups={string.Join(",", affectedGroups)}");
            foreach (var window in _desktopWindows.Where(window => affectedGroups.Contains(window.Group.Definition.Id)))
                window.ApplyDesktopFileChanges(changes);
            _desktopSurface?.RefreshItems();
            ScheduleSave();
        }
    }

    private static bool ReplacePath(List<string> paths, string oldPath, string newPath)
    {
        var index = paths.FindIndex(path => string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        paths[index] = newPath;
        return true;
    }

    private static bool AddPathToLayout(GroupDefinition group, Guid? tabId, string path)
    {
        if (tabId is null)
        {
            return LayoutItemStateService.AddPinnedPath(group.PinnedPaths, group.ItemOrder, path);
        }

        group.StoreActiveTab();
        var tab = group.Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
        if (tab is null || !LayoutItemStateService.AddPinnedPath(tab.PinnedPaths, tab.ItemOrder, path)) return false;
        if (group.Tabs.ElementAtOrDefault(group.ActiveTabIndex)?.Id == tabId)
            group.ReloadActiveTab();
        return true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        if (SearchBox.Text.Trim().Length < 2) { SearchPopup.IsOpen = false; return; }
        _searchTimer.Start();
    }

    private async void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        try
        {
        var query = SearchBox.Text.Trim();
        var folders = _state.Groups.Where(group => group.Kind == GroupKind.Folder && Directory.Exists(group.FolderPath))
            .Select(group => group.FolderPath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = await Task.Run(() => folders.SelectMany(folder =>
        {
            try { return Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }).Where(path => Path.GetFileName(path).Contains(query, StringComparison.CurrentCultureIgnoreCase))
          .Take(100).Select(path => new FileEntry(Path.GetFileName(path), path, Directory.Exists(path))).ToArray());
        if (query != SearchBox.Text.Trim()) return;
        SearchResults.ItemsSource = results;
        SearchPopup.IsOpen = results.Length > 0;
        }
        catch (Exception ex) { LogService.Warning("Search failed", ex); }
    }

    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is FileEntry entry)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
            SearchPopup.IsOpen = false;
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(async () =>
        {
            CaptureCurrentLayout();
            if (_state.Settings.AutoSwitchDisplayLayouts)
            {
                await _displayProfiles.SaveAsync(_displaySignature, _state.Groups);
                var newSignature = _displayProfiles.GetCurrentSignature();
                var profile = await _displayProfiles.LoadAsync(newSignature);
                _displaySignature = newSignature;
                if (profile is not null)
                {
                    _state.Groups = profile;
                    RecreateDesktopGroups();
                }
            }
            foreach (var window in _desktopWindows)
            {
                window.EnsureVisibleOnCurrentDisplays();
            }
            _qrFrameController.RefreshDisplayLayout();
            _desktopSurface?.EnsureVisibleBounds();

            StatusText.Text = "显示器配置已变化，桌面分组位置已重新校验";
            ScheduleSave();
        }, DispatcherPriority.Background);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var firstRun = !_layoutStore.HasState;
        _state = await _layoutStore.LoadAsync();
        _displaySignature = _displayProfiles.GetCurrentSignature();
        // The primary layout is authoritative at startup. Loading a stale
        // same-display profile here used to overwrite changes that had already
        // been persisted to layout.json.
        _groupsHidden = _state.Settings.RememberGroupsHidden && _state.Settings.GroupsHidden;
        _isTopmost = false;
        _desktopMode = true;
        _state.Settings.WasInDesktopMode = true;
        // WorkerW refactor: keep Explorer as the owner of the desktop surface.
        // The previous full-screen transparent WPF surface intercepted desktop
        // input and is intentionally no longer created during startup.
        RecreateDesktopGroups();
        var explorerIconsHidden = _explorerIconVisibility.HideAndGuard();
        UpdateToolbarState();
        _hotKeyService.Attach(this);
            var hotKeyRegistered = TryRegisterConfiguredHotKeys(_state.Settings, out var hotKeyError);
        var desktopDoubleClickReady = _desktopDoubleClickService.Start();
        StatusText.Text = GetStartupStatus(hotKeyRegistered, desktopDoubleClickReady, hotKeyError);
        _isLoaded = true;
        if (firstRun)
        {
            await ReapplyLayoutRulesAsync();
            StatusText.Text = "已创建预设布局并完成首次桌面归类";
        }
        if (_pendingDesktopMenuCreateKind is { } pendingCreateKind)
        {
            _pendingDesktopMenuCreateKind = null;
            var pendingPoint = _pendingDesktopMenuPoint;
            _pendingDesktopMenuPoint = null;
            CreateLayoutFromDesktopMenu(pendingCreateKind, screenPoint: pendingPoint);
        }
        _shellChangeNotifications.Start(this, [_desktopFiles.UserDesktop, _desktopFiles.CommonDesktop]);
        _ = ShellContextMenuService.WarmUpAsync(_desktopFiles.EnumerateItems());
        ConfigureRuleTimer();
        ConfigureEdgeMode();
        Opacity = 1;
        Hide();
        if (!explorerIconsHidden)
            LogService.Warning("Could not hide the Explorer desktop icon layer.");

        if (_recoveryService.PreviousSessionEndedUnexpectedly)
        {
            StatusText.Text = "检测到上次异常退出，已载入最后一次有效布局；可在设置中恢复备份。";
        }

    }

    public void ActivateFromSecondInstance()
    {
        OpenManager();
        StatusText.Text = "已阻止重复启动并唤醒当前实例";
    }

    public async void CreateLayoutFromDesktopMenu(string kind = "empty", Window? dialogOwner = null, Point? screenPoint = null)
    {
        LogService.Info($"Desktop menu create requested | kind={kind} | loaded={_isLoaded}");
        var requestedPoint = screenPoint ?? CaptureCursorLayoutPoint(GroupDefinition.DefaultWidth, GroupDefinition.DefaultHeight);
        if (!_isLoaded)
        {
            _pendingDesktopMenuCreateKind = kind;
            _pendingDesktopMenuPoint = requestedPoint;
            return;
        }
        _pendingDesktopMenuPoint = null;
        var definition = CreateGroupDefinition("新布局", GroupKind.Empty);
        if (kind == "folder")
        {
            var dialog = new OpenFolderDialog { Title = "选择要映射到新布局的文件夹", Multiselect = false };
            if (dialog.ShowDialog(dialogOwner ?? this) != true) return;
            definition.Kind = GroupKind.Folder;
            definition.FolderPath = dialog.FolderName;
            definition.Title = Path.GetFileName(Path.TrimEndingDirectorySeparator(dialog.FolderName));
        }
        var placement = CaptureCursorLayoutPoint(definition.Width, definition.Height, requestedPoint);
        definition.DesktopX = placement.X;
        definition.DesktopY = placement.Y;
        _state.Groups.Add(definition);
        var window = AddDesktopGroupWindow(definition);
        BringLayoutToFront(window);
        await SaveNowAsync();
        LogService.Info($"Desktop menu layout created | kind={kind} | group={definition.Id}");
        StatusText.Text = $"已从桌面菜单创建{(kind == "folder" ? "映射" : "普通")}布局";
    }

    private static string GetStartupStatus(bool hotKeyRegistered, bool desktopDoubleClickReady, string hotKeyError)
    {
        if (!hotKeyRegistered)
        {
            return $"布局已载入 · {hotKeyError}";
        }

        return desktopDoubleClickReady
            ? "布局已载入 · 桌面双击监听可用"
            : "布局已载入 · 桌面双击监听不可用";
    }

    private void RecreateDesktopGroups()
    {
        foreach (var existing in _desktopWindows.ToArray()) existing.Close();
        _desktopWindows.Clear();
        foreach (var definition in _state.Groups) AddDesktopGroupWindow(definition);
        _desktopSurface?.RefreshItems();
    }

    /// <summary>
    /// Applies the editable settings-page fields without destroying unrelated desktop windows.
    /// Geometry and item state belong to the live window and must survive a settings apply.
    /// </summary>
    private void SynchronizeDesktopGroups(IEnumerable<GroupDefinition> requestedGroups)
    {
        var requested = requestedGroups.ToArray();
        var requestedIds = requested.Select(group => group.Id).ToHashSet();
        var existingById = _state.Groups.ToDictionary(group => group.Id);

        foreach (var window in _desktopWindows.Where(window => !requestedIds.Contains(window.Group.Definition.Id)).ToArray())
        {
            _desktopWindows.Remove(window);
            window.Close();
        }

        var synchronized = new List<GroupDefinition>(requested.Length);
        foreach (var desired in requested)
        {
            if (!existingById.TryGetValue(desired.Id, out var live))
            {
                live = SnapshotService.CloneGroups([desired]).Single();
                AddDesktopGroupWindow(live);
            }
            else
            {
                // The settings page only edits these fields. Keeping the live model prevents
                // a stale settings snapshot from resetting position, size, ordering or selection.
                live.Title = desired.Title;
                live.IsRuleLocked = desired.IsRuleLocked;
                foreach (var desiredTab in desired.Tabs)
                {
                    var liveTab = live.Tabs.FirstOrDefault(tab => tab.Id == desiredTab.Id);
                    if (liveTab is not null)
                    {
                        liveTab.Title = desiredTab.Title;
                        liveTab.IsRuleLocked = desiredTab.IsRuleLocked;
                    }
                }
                if (live.Tabs.ElementAtOrDefault(live.ActiveTabIndex) is { } currentTab)
                    live.IsRuleLocked = currentTab.IsRuleLocked;
                if (live.Tabs.ElementAtOrDefault(live.ActiveTabIndex) is { } activeTab)
                    live.Title = activeTab.Title;
                _desktopWindows.FirstOrDefault(window => window.Group.Definition.Id == live.Id)
                    ?.Group.RefreshDefinitionChrome();
            }
            synchronized.Add(live);
        }

        _state.Groups = synchronized;
        _desktopSurface?.RefreshItems();
    }

    private void CreateDesktopSurface()
    {
        // Deprecated during WorkerW migration. Explorer owns the desktop
        // surface; layout windows are kept independent until they are hosted
        // by the native WorkerW container.
        _desktopSurface?.Close();
        _desktopSurface = null;
    }

    private HashSet<string> GetAssignedDesktopPaths() => _state.Groups
        .SelectMany(group => group.Tabs.Count == 0
            ? group.PinnedPaths
            : group.Tabs.SelectMany(tab => tab.PinnedPaths))
        .Where(_desktopFiles.IsDesktopPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void UnassignDesktopPaths(IReadOnlyList<string> paths)
    {
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _state.Groups)
        {
            group.PinnedPaths.RemoveAll(path => set.Contains(path));
            group.ItemOrder.RemoveAll(path => set.Contains(path));
            foreach (var tab in group.Tabs)
            {
                tab.PinnedPaths.RemoveAll(path => set.Contains(path));
                tab.ItemOrder.RemoveAll(path => set.Contains(path));
            }
            group.ReloadActiveTab();
        }
        RecreateDesktopGroups();
        ScheduleSave();
    }

    private void CreateLayoutFromDesktop(GroupKind requestedKind, Rect bounds, IReadOnlyList<string> selectedPaths)
    {
        var definition = new GroupDefinition
        {
            Title = selectedPaths.Count > 0 ? "所选桌面图标" : "新布局",
            Kind = GroupKind.Empty,
            DesktopX = SystemParameters.VirtualScreenLeft + Math.Max(0, bounds.X),
            DesktopY = SystemParameters.VirtualScreenTop + Math.Max(0, bounds.Y),
            Width = Math.Max(GroupDefinition.DefaultWidth, bounds.Width),
            Height = Math.Max(GroupDefinition.DefaultHeight, bounds.Height),
            PinnedPaths = [.. selectedPaths],
            ItemOrder = [.. selectedPaths]
        };

        if (requestedKind == GroupKind.Folder && selectedPaths.Count == 1 && Directory.Exists(selectedPaths[0]))
        {
            definition.Kind = GroupKind.Folder;
            definition.Title = Path.GetFileName(Path.TrimEndingDirectorySeparator(selectedPaths[0]));
            definition.FolderPath = selectedPaths[0];
            definition.PinnedPaths.Clear();
            definition.ItemOrder.Clear();
        }

        _state.Groups.Add(definition);
        var window = AddDesktopGroupWindow(definition);
        BringLayoutToFront(window);
        _desktopSurface?.RefreshItems();
        ScheduleSave();
    }

    private Point CaptureCursorLayoutPoint(double width, double height, Point? requestedPoint = null)
    {
        var point = requestedPoint ?? GetCursorScreenPosition(this);
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));
        return new Point(
            Math.Clamp(point.X, topLeft.X, Math.Max(topLeft.X, bottomRight.X - width)),
            Math.Clamp(point.Y, topLeft.Y, Math.Max(topLeft.Y, bottomRight.Y - height)));
    }

    private DesktopGroupWindow AddDesktopGroupWindow(GroupDefinition definition, bool animate = true)
    {
        var window = new DesktopGroupWindow(
            definition,
            _state.Settings.EnableAnimations,
            _state.Settings.ContainerOpacity,
            _state.Settings.ContainerCornerRadius,
            _state.Settings.IconSize,
            _state.Settings.AnimationSpeed)
        {
            Owner = _desktopSurface
        };
        window.LayoutChanged += (_, _) =>
        {
            _desktopSurface?.RefreshItems();
            ScheduleSave();
        };
        window.RemoveRequested += DesktopGroup_RemoveRequested;
        window.SettingsRequested += (_, _) => SettingsButton_Click(window, new RoutedEventArgs());
        window.ExitRequested += (_, _) => RequestApplicationExit();
        window.Group.CreateLayoutRequested += kind => CreateLayoutFromDesktopMenu(
            kind == GroupKind.Folder ? "folder" : "empty", window);
        window.InteractionRequested += (_, _) => BringLayoutToFront(window);
        window.Group.StatusChanged += (_, message) => StatusText.Text = message;
        window.Group.HeaderDragCompleted += screenPoint => HandleHeaderDragCompleted(window, screenPoint);
        window.Group.TabDragStarted += (payload, screenPoint) => BeginDetachedTabDrag(window, payload, screenPoint);
        _desktopWindows.Add(window);
        if (!_groupsHidden || _isTopmost)
        {
            if (animate) window.ShowAnimated();
            else window.Show();
        }
        window.SetInteractionMode(_state.Settings.InteractionMode);
        if (IsTopmostRequested(window)) window.SetTemporaryTopmost(true);
        else window.RestoreDesktopLayer();
        return window;
    }

    private void BringLayoutToFront(DesktopGroupWindow window)
    {
        if (!_desktopWindows.Contains(window) || !window.IsVisible) return;
        _desktopWindows.Remove(window);
        _desktopWindows.Add(window);
        window.BringToFrontWithin(_desktopWindows);
    }

    private void MergeLayoutAtDropTarget(DesktopGroupWindow source, Point screenPoint)
    {
        if (!_desktopWindows.Contains(source)) return;
        var target = _desktopWindows.FirstOrDefault(window => window != source && window.IsVisible && !window.IsEdgeHidden &&
            screenPoint.X >= window.Left && screenPoint.X <= window.Left + window.ActualWidth &&
            screenPoint.Y >= window.Top && screenPoint.Y <= window.Top + window.ActualHeight);
        if (target is null) return;

        var sourcePages = source.Group.Definition.ExportTabs();
        foreach (var page in sourcePages) target.Group.Definition.AddTab(page, activate: false);
        target.Group.RefreshDefinitionChrome();

        _desktopWindows.Remove(source);
        _state.Groups.Remove(source.Group.Definition);
        source.Close();
        StatusText.Text = $"已将布局合并为 {target.Group.Definition.Tabs.Count} 个页签";
        ScheduleSave();
    }

    private void HandleHeaderDragCompleted(DesktopGroupWindow source, Point screenPoint)
    {
        var target = _desktopWindows.FirstOrDefault(window => window != source && window.IsVisible && !window.IsEdgeHidden &&
            screenPoint.X >= window.Left && screenPoint.X <= window.Left + window.ActualWidth &&
            screenPoint.Y >= window.Top && screenPoint.Y <= window.Top + window.ActualHeight);
        if (target is not null)
        {
            MergeLayoutAtDropTarget(source, screenPoint);
            return;
        }
        source.UpdateDockFromCurrentPosition(_state.Settings.InteractionMode);
        ScheduleSave();
    }

    private void BeginDetachedTabDrag(DesktopGroupWindow originalHost, LayoutTabDragPayload payload, Point screenPoint)
    {
        var detached = GroupDefinition.FromTab(payload.Tab);
        detached.AutoCollapse = payload.AutoCollapse;
        detached.Width = originalHost.ActualWidth;
        detached.Height = originalHost.ActualHeight;
        detached.DesktopX = screenPoint.X - detached.Width / 2;
        detached.DesktopY = screenPoint.Y - 24;
        _state.Groups.Add(detached);
        var draggedWindow = AddDesktopGroupWindow(detached, animate: false);
        draggedWindow.Left = detached.DesktopX.Value;
        draggedWindow.Top = detached.DesktopY.Value;
        StatusText.Text = $"已将“{payload.Tab.Title}”拆分为独立布局";
        ScheduleSave();

        var pointerOffsetX = detached.Width / 2;
        const double pointerOffsetY = 24;
        PreProcessInputEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.StagingItem.Input is not MouseEventArgs mouseArgs) return;
            var cursor = GetCursorScreenPosition(draggedWindow);
            draggedWindow.Left = cursor.X - pointerOffsetX;
            draggedWindow.Top = cursor.Y - pointerOffsetY;
            if (mouseArgs is not MouseButtonEventArgs { ChangedButton: MouseButton.Left, ButtonState: MouseButtonState.Released }) return;

            InputManager.Current.PreProcessInput -= handler;
            Mouse.Capture(null);
            FinishDetachedTabDrag(draggedWindow, originalHost, payload, cursor);
        };
        InputManager.Current.PreProcessInput += handler;
        Mouse.Capture(draggedWindow.Group, CaptureMode.SubTree);
    }

    private void FinishDetachedTabDrag(DesktopGroupWindow draggedWindow, DesktopGroupWindow originalHost, LayoutTabDragPayload payload, Point screenPoint)
    {
        var target = _desktopWindows.FirstOrDefault(window => window != draggedWindow && window.IsVisible && !window.IsEdgeHidden &&
            screenPoint.X >= window.Left && screenPoint.X <= window.Left + window.ActualWidth &&
            screenPoint.Y >= window.Top && screenPoint.Y <= window.Top + window.ActualHeight);
        if (target is null)
        {
            ScheduleSave();
            return;
        }

        if (target == originalHost && _desktopWindows.Contains(originalHost))
        {
            originalHost.Group.Definition.InsertTab(payload.Tab, payload.OriginalIndex, activate: true);
            originalHost.Group.RefreshDefinitionChrome();
            originalHost.RefreshContents();
            _desktopWindows.Remove(draggedWindow);
            _state.Groups.Remove(draggedWindow.Group.Definition);
            draggedWindow.Close();
            StatusText.Text = $"已将“{payload.Tab.Title}”放回原页签位置";
            ScheduleSave();
            return;
        }

        MergeLayoutAtDropTarget(draggedWindow, screenPoint);
    }

    private static Point GetCursorScreenPosition(Window reference)
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var physical = new Point(cursor.X, cursor.Y);
        var source = PresentationSource.FromVisual(reference);
        return source?.CompositionTarget?.TransformFromDevice.Transform(physical) ?? physical;
    }

    private void OpenManager()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        SettingsButton_Click(this, new RoutedEventArgs());
    }

    private async void DesktopGroup_RemoveRequested(object? sender, EventArgs e)
    {
        if (sender is not DesktopGroupWindow window)
        {
            return;
        }

        var result = MessageBox.Show(
            $"移除分组“{window.Group.Definition.Title}”？\n不会删除映射文件夹中的任何文件。",
            "移除分组",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _desktopWindows.Remove(window);
        _state.Groups.Remove(window.Group.Definition);
        window.Close();
        _desktopSurface?.RefreshItems();
        await SaveNowAsync();
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择需要映射的文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var definition = CreateGroupDefinition(Path.GetFileName(dialog.FolderName), GroupKind.Folder);
        definition.FolderPath = dialog.FolderName;
        _state.Groups.Add(definition);
        AddDesktopGroupWindow(definition);
        await SaveNowAsync();
        StatusText.Text = $"已映射：{dialog.FolderName}";
    }

    private async void AddEmptyButton_Click(object sender, RoutedEventArgs e)
    {
        var definition = CreateGroupDefinition("新分组", GroupKind.Empty);
        _state.Groups.Add(definition);
        AddDesktopGroupWindow(definition);
        await SaveNowAsync();
        StatusText.Text = "已创建空分组";
    }

    private GroupDefinition CreateGroupDefinition(string title, GroupKind kind)
    {
        var offset = 28 * (_state.Groups.Count % 9);
        return new GroupDefinition
        {
            Title = string.IsNullOrWhiteSpace(title) ? "未命名分组" : title,
            Kind = kind,
            X = 32 + offset,
            Y = 32 + offset,
            Width = GroupDefinition.DefaultWidth,
            Height = GroupDefinition.DefaultHeight
        };
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => ToggleGroupsVisibility();

    private void ToggleGroupsVisibility()
    {
        _groupsHidden = !_groupsHidden;
        foreach (var window in _desktopWindows)
        {
            if (_groupsHidden)
            {
                window.HideAnimated();
            }
            else
            {
                window.ShowAnimated();
            }
        }

        HideButton.Content = _groupsHidden ? "显示分组" : "隐藏分组";
        StatusText.Text = _groupsHidden ? "分组已隐藏" : "分组已显示";
        if (_state.Settings.RememberGroupsHidden)
        {
            _state.Settings.GroupsHidden = _groupsHidden;
            ScheduleSave();
        }
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e) =>
        ToggleTopmost();

    private bool TryRegisterConfiguredHotKeys(AppSettings settings, out string error)
    {
        error = string.Empty;
        _hotKeyBindingKeys.Clear();
        var registrations = new List<(int BindingId, HotKeyGesture Gesture)>();
        if (!string.IsNullOrWhiteSpace(settings.QrRecognitionHotKey))
        {
            if (!HotKeyParser.TryParse(settings.QrRecognitionHotKey, out var gesture, out error) || gesture is null)
                return false;
            registrations.Add((QrRecognitionHotKeyBindingId, gesture));
        }

        if (settings.InteractionMode == LayoutInteractionMode.Standard)
        {
            foreach (var binding in settings.TopmostHotKeys.Where(binding => binding.Enabled))
            {
                if (!HotKeyParser.TryParse(binding.Gesture, out var gesture, out error) || gesture is null)
                    return false;
                var key = binding.Id.GetHashCode();
                while (key == QrRecognitionHotKeyBindingId || _hotKeyBindingKeys.ContainsKey(key)) key++;
                _hotKeyBindingKeys[key] = binding.Id;
                registrations.Add((key, gesture));
            }
        }
        return _hotKeyService.ReplaceAll(registrations, out error);
    }

    private void HotKeyBindingPressed(int key)
    {
        if (key == QrRecognitionHotKeyBindingId)
        {
            StartQrRecognition();
            return;
        }
        if (_state.Settings.InteractionMode != LayoutInteractionMode.Standard) return;
        if (!_hotKeyBindingKeys.TryGetValue(key, out var bindingId)) return;
        var binding = _state.Settings.TopmostHotKeys.FirstOrDefault(item => item.Id == bindingId);
        if (binding is null || !binding.Enabled) return;
        if (!_activeHotKeyBindings.Add(bindingId)) _activeHotKeyBindings.Remove(bindingId);
        _topmostFromHotKey = _activeHotKeyBindings.Count > 0;
        ApplyActiveTopmostState();
    }

    private void StartQrRecognition()
    {
        if (!_isLoaded || _qrRecognitionRunning) return;
        _qrFrameController.Show();
        StatusText.Text = "二维码取景框已打开，可移动或缩放后点击识别";
    }

    private async void QrFrame_RecognitionRequested(System.Drawing.Rectangle bounds)
    {
        if (!_isLoaded || _qrRecognitionRunning) return;
        _qrRecognitionRunning = true;
        try
        {
            if (_qrResultsWindow is not null)
            {
                _qrResultsWindow.Hide();
                _qrResultsWindow.Close();
                _qrResultsWindow = null;
            }

            var capture = await Task.Run(() => ScreenCaptureService.CaptureRegion(bounds));
            if (capture is null)
            {
                _qrFrameController.RestoreAfterFailure();
                StatusText.Text = "未检测到可用显示器";
                return;
            }

            var captured = capture!;
            var results = await Task.Run(() => QrCodeRecognitionService.Decode(captured));
            _qrResultsWindow = new QrRecognitionResultsWindow(results);
            _qrResultsWindow.Closed += (_, _) => _qrResultsWindow = null;
            _qrResultsWindow.Show();
            _qrResultsWindow.Activate();
            StatusText.Text = results.Count == 0 ? "选区内未识别到二维码" : $"已识别 {results.Count} 个二维码";
        }
        catch (Exception ex)
        {
            LogService.Warning("QR code recognition failed", ex);
            _qrFrameController.RestoreAfterFailure();
            StatusText.Text = "二维码识别失败，请重试";
        }
        finally
        {
            _qrRecognitionRunning = false;
        }
    }

    private void ApplyActiveTopmostState()
    {
        if (_state.Settings.InteractionMode == LayoutInteractionMode.EdgeHide)
        {
            _activeHotKeyBindings.Clear();
            _isTopmost = true;
            foreach (var window in _desktopWindows.ToArray())
            {
                if (!window.IsVisible && !_groupsHidden) window.ShowAnimated();
                window.SetTemporaryTopmost(true);
            }
            UpdateToolbarState();
            ScheduleSave();
            return;
        }

        var targets = new HashSet<Guid>();
        var hasAllLayoutsBinding = false;
        foreach (var binding in _state.Settings.TopmostHotKeys.Where(binding => _activeHotKeyBindings.Contains(binding.Id)))
        {
            if (binding.AllLayouts)
            {
                hasAllLayoutsBinding = true;
                foreach (var layout in EnumerateLayoutIds()) targets.Add(layout);
            }
            else
            {
                targets.UnionWith(binding.LayoutIds);
            }
        }

        _isTopmost = targets.Count > 0;
        foreach (var window in _desktopWindows.ToArray())
        {
            var matchingTabs = window.Group.Definition.Tabs
                .Select((tab, index) => (tab, index))
                .Where(item => targets.Contains(item.tab.Id))
                .ToArray();
            var isTargeted = window.Group.Definition.Tabs.Count == 0
                ? targets.Contains(window.Group.Definition.Id)
                : matchingTabs.Length > 0;
            if (!hasAllLayoutsBinding && isTargeted && matchingTabs.Length > 0 && window.Group.Definition.ActiveTabIndex != matchingTabs[0].index)
            {
                window.Group.Definition.ActivateTab(matchingTabs[0].index);
                window.Group.RefreshDefinitionChrome();
                window.RefreshContents();
            }
            if (isTargeted)
            {
                if (!window.IsVisible) window.ShowAnimated();
                if (window.IsEdgeHidden) window.RevealFromEdge(animate: true);
                window.SetTemporaryTopmost(true);
            }
            else
            {
                window.SetTemporaryTopmost(false);
                if (_groupsHidden) window.HideAnimated();
                else window.RestoreDesktopLayer();
            }
        }
        UpdateToolbarState();
        ScheduleSave();
    }

    private IEnumerable<Guid> EnumerateLayoutIds() => _state.Groups.SelectMany(group =>
        group.Tabs.Count == 0 ? [group.Id] : group.Tabs.Select(tab => tab.Id));

    private void ToggleTopmost()
    {
        if (_state.Settings.InteractionMode != LayoutInteractionMode.Standard) return;
        if (_isTopmost) EndTopmostMode();
        else BeginTopmostMode();
    }

    private void BeginTopmostMode()
    {
        if (_isTopmost) return;
        _isTopmost = true;
        _topmostFromHotKey = false;
        _temporarilyRevealed = _groupsHidden;
        var windows = _desktopWindows.ToArray();
        foreach (var window in windows)
        {
            if (!window.IsVisible) window.ShowAnimated();
            window.SetTemporaryTopmost(true);
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isTopmost) return;
            foreach (var window in _desktopWindows.ToArray())
            {
                if (!window.IsVisible) window.Show();
                window.SetTemporaryTopmost(true);
            }
        }, DispatcherPriority.ContextIdle);

        UpdateToolbarState();
        StatusText.Text = "全部布局已置顶；再次按快捷键恢复桌面底层";
    }

    private void EndTopmostMode()
    {
        if (!_isTopmost) return;
        _activeHotKeyBindings.Clear();
        _isTopmost = false;
        _topmostFromHotKey = false;
        var hideAfterRelease = _temporarilyRevealed && _groupsHidden;
        _temporarilyRevealed = false;
        foreach (var window in _desktopWindows.ToArray())
        {
            window.SetTemporaryTopmost(false);
            if (hideAfterRelease) window.HideAnimated();
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_isTopmost) return;
            foreach (var window in _desktopWindows.ToArray()) window.RestoreDesktopLayer();
        }, DispatcherPriority.ContextIdle);
        UpdateToolbarState();
        StatusText.Text = "置顶已关闭；全部布局已恢复到桌面底层";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var startupEnabled = _startupService.IsEnabled();
        var dialog = new SettingsWindow(_state.Settings, startupEnabled, _state.Groups, _state.Rules, _state.LayoutMatchRules);
        _settingsWindow = dialog;
        dialog.AppearancePreviewChanged += preview => ApplyAppearancePreview(preview);
        dialog.ApplyRequested += async (requested, groups, rules, layoutRules) =>
            await ApplySettingsAsync(requested, groups, rules, layoutRules, dialog);
        dialog.ReapplyLayoutRulesRequested += ReapplyLayoutRulesAsync;
        dialog.ExitApplicationRequested += () =>
        {
            _applicationExitRequested = true;
            _ = Dispatcher.BeginInvoke(RequestApplicationExit);
        };
        dialog.Closed += async (_, _) =>
        {
            if (_settingsWindow == dialog) _settingsWindow = null;
            ApplySettingsToCurrentGroups();
            if (!dialog.RestoreBackupRequested) return;

            var backup = await _layoutStore.LoadBackupAsync();
            if (backup is null)
            {
                MessageBox.Show("当前没有可恢复的布局备份。", "恢复布局", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _state = backup;
            _groupsHidden = _state.Settings.RememberGroupsHidden && _state.Settings.GroupsHidden;
            _isTopmost = false;
            CreateDesktopSurface();
            RecreateDesktopGroups();
            ApplySettingsToCurrentGroups();
            UpdateToolbarState();
            await SaveNowAsync();
            StatusText.Text = "已恢复上一份有效布局";
        };
        dialog.Show();
    }

    private void ApplyAppearancePreview(AppSettings preview)
    {
        foreach (var window in _desktopWindows)
        {
            window.Group.ApplyAppearance(
                preview.EnableAnimations,
                preview.ContainerOpacity,
                preview.ContainerCornerRadius,
                preview.IconSize,
                preview.AnimationSpeed);
        }
    }

    private async Task ApplySettingsAsync(
        AppSettings requested,
        List<GroupDefinition> groups,
        List<ClassificationRule> rules,
        List<LayoutMatchRule> layoutRules,
        Window owner)
    {
        var previousSettings = _state.Settings;
        await ApplyStoragePathsAsync(requested);
        if (!TryRegisterConfiguredHotKeys(requested, out var hotKeyError))
        {
            MessageBox.Show(
                owner,
                hotKeyError,
                "快捷键不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            requested.TopmostHotKeys = previousSettings.TopmostHotKeys.Select(binding => new TopmostHotKeyBinding
            {
                Id = binding.Id,
                Enabled = binding.Enabled,
                Gesture = binding.Gesture,
                AllLayouts = binding.AllLayouts,
                LayoutIds = [.. binding.LayoutIds]
            }).ToList();
            requested.QrRecognitionHotKey = previousSettings.QrRecognitionHotKey;
            TryRegisterConfiguredHotKeys(previousSettings, out _);
        }
        _activeHotKeyBindings.Clear();

        try
        {
            _startupService.SetEnabled(requested.StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(owner, ex.Message, "开机启动设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            requested.StartWithWindows = previousSettings.StartWithWindows;
        }

        requested.GroupsHidden = _groupsHidden;
        requested.IsTopmost = false;
        requested.WasInDesktopMode = _desktopMode;
        requested.QrRecognitionFrameBounds = _state.Settings.QrRecognitionFrameBounds ?? requested.QrRecognitionFrameBounds;
        _state.Settings = requested;
        SynchronizeDesktopGroups(groups);
        _state.Rules = rules;
        _state.LayoutMatchRules = layoutRules;
        if (_state.Settings.AutoSwitchDisplayLayouts)
        {
            await _displayProfiles.SaveAsync(_displaySignature, _state.Groups);
        }
        ConfigureRuleTimer();
        ApplySettingsToCurrentGroups();
        ApplyActiveTopmostState();
        UpdateToolbarState();
        await SaveNowAsync();
        StatusText.Text = "设置已保存并立即生效";
    }

    private async Task ApplyStoragePathsAsync(AppSettings requested)
    {
        var currentData = string.IsNullOrWhiteSpace(_state.Settings.DataDirectory)
            ? AppDataPathService.DataDirectory : _state.Settings.DataDirectory;
        var currentLogs = string.IsNullOrWhiteSpace(_state.Settings.LogDirectory)
            ? AppDataPathService.LogDirectory : _state.Settings.LogDirectory;
        var newData = AppDataPathService.Normalize(requested.DataDirectory);
        var newLogs = AppDataPathService.Normalize(requested.LogDirectory);
        if (string.Equals(currentData, newData, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentLogs, newLogs, StringComparison.OrdinalIgnoreCase)) return;

        await AppDataPathService.MigrateAndConfigureAsync(currentData, currentLogs, newData, newLogs);
        _layoutStore.SetStateDirectory(newData);
        _snapshotService.SetDataDirectory(newData);
        _displayProfiles.SetDataDirectory(newData);
        _recoveryService.SetDataDirectory(newData);
        requested.DataDirectory = newData;
        requested.LogDirectory = newLogs;
    }

    private async Task ReapplyLayoutRulesAsync()
    {
        var desktopItems = _desktopFiles.EnumerateItems()
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalGroups = _state.Groups.Where(group => group.Tabs.Count == 0
            ? group.Kind == GroupKind.Empty
            : group.Tabs.Any(tab => tab.Kind == GroupKind.Empty)).ToArray();
        var released = 0;

        foreach (var group in normalGroups)
        {
            if (group.Tabs.Count == 0)
            {
                    if (!group.IsRuleLocked)
                    {
                        released += group.PinnedPaths.RemoveAll(_desktopFiles.IsDesktopPath);
                        group.ItemOrder.RemoveAll(_desktopFiles.IsDesktopPath);
                    }
                continue;
            }
            group.StoreActiveTab();
            foreach (var tab in group.Tabs.Where(tab => tab.Kind == GroupKind.Empty))
            {
                if (!tab.IsRuleLocked)
                {
                    released += tab.PinnedPaths.RemoveAll(_desktopFiles.IsDesktopPath);
                    tab.ItemOrder.RemoveAll(_desktopFiles.IsDesktopPath);
                }
            }
            group.ReloadActiveTab();
        }

        var lockedPaths = normalGroups.SelectMany(group => group.Tabs.Count == 0
            ? (group.IsRuleLocked ? group.PinnedPaths : [])
            : group.Tabs.Where(tab => tab.IsRuleLocked).SelectMany(tab => tab.PinnedPaths))
            .Where(_desktopFiles.IsDesktopPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = new LayoutAssignmentService().Preview(desktopItems.Where(path => !lockedPaths.Contains(path)), normalGroups, _state.LayoutMatchRules);
        foreach (var (path, groupId, tabId) in assignments)
        {
            var group = normalGroups.First(group => group.Id == groupId);
            AddPathToLayout(group, tabId, path);
        }

        foreach (var window in _desktopWindows.Where(window => normalGroups.Any(group => group.Id == window.Group.Definition.Id)))
            window.RefreshContents();

        await SaveNowAsync();
        StatusText.Text = $"已按规则重新归类 {assignments.Count} 个桌面图标" + (released > assignments.Count ? $"，释放 {released - assignments.Count} 个未匹配图标" : string.Empty);
    }

    private void ConfigureRuleTimer()
    {
        _ruleTimer.Stop();
        if (_state.Settings.AutoRunRules && _state.LayoutMatchRules.Count > 0)
        {
            _ruleTimer.Interval = TimeSpan.FromMinutes(_state.Settings.RuleIntervalMinutes);
            _ruleTimer.Start();
        }
    }

    private async void LayoutRuleTimer_Tick(object? sender, EventArgs e)
    {
        if (_rulesRunning) return;
        _rulesRunning = true;
        try
        {
            await ReapplyLayoutRulesAsync();
        }
        catch (Exception ex)
        {
            LogService.Warning("Scheduled layout reassignment failed", ex);
        }
        finally
        {
            _rulesRunning = false;
        }
    }

    private async void RulesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RulesWindow(_state.Rules) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _state.Rules = dialog.Rules.ToList();
        ConfigureRuleTimer();
        await SaveNowAsync();
        StatusText.Text = $"已保存 {_state.Rules.Count} 条自动分类规则";
    }

    private async void SnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureCurrentLayout();
        var dialog = new SnapshotsWindow(_snapshotService, _state.Groups) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedSnapshot is null) return;
        _state.Groups = SnapshotService.CloneGroups(dialog.SelectedSnapshot.Groups);
        RecreateDesktopGroups();
        await SaveNowAsync();
        StatusText.Text = $"已恢复布局快照：{dialog.SelectedSnapshot.Name}";
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择诊断包保存目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var path = await _diagnosticService.CreatePackageAsync(dialog.FolderName);
            StatusText.Text = $"诊断包已生成：{path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "生成诊断包失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AlignButton_Click(object sender, RoutedEventArgs e)
    {
        const double margin = 24;
        const double gap = 18;
        var workArea = SystemParameters.WorkArea;
        var x = workArea.Left + margin;
        var y = workArea.Top + margin;
        var rowHeight = 0.0;
        foreach (var window in _desktopWindows)
        {
            if (x + window.ActualWidth > workArea.Right - margin)
            {
                x = workArea.Left + margin;
                y += rowHeight + gap;
                rowHeight = 0;
            }
            window.Left = x;
            window.Top = y;
            window.Group.Definition.DesktopX = x;
            window.Group.Definition.DesktopY = y;
            x += window.ActualWidth + gap;
            rowHeight = Math.Max(rowHeight, window.ActualHeight);
        }
        ScheduleSave();
        StatusText.Text = "已按网格对齐布局";
    }

    private void ApplySettingsToCurrentGroups()
    {
        foreach (var window in _desktopWindows)
        {
            window.Group.ApplyAppearance(_state.Settings.EnableAnimations, _state.Settings.ContainerOpacity,
                _state.Settings.ContainerCornerRadius, _state.Settings.IconSize, _state.Settings.AnimationSpeed);
            window.SetInteractionMode(_state.Settings.InteractionMode);
            if (_state.Settings.InteractionMode == LayoutInteractionMode.EdgeHide)
                window.UpdateDockFromCurrentPosition(_state.Settings.InteractionMode);
        }
        ConfigureEdgeMode();
    }

    private void ConfigureEdgeMode()
    {
        if (_state.Settings.InteractionMode == LayoutInteractionMode.EdgeHide) _edgeTimer.Start();
        else _edgeTimer.Stop();
    }

    private void UpdateEdgeVisibility()
    {
        if (_state.Settings.InteractionMode != LayoutInteractionMode.EdgeHide || _groupsHidden) return;
        foreach (var window in _desktopWindows.ToArray())
        {
            if (window.DockEdge == DockEdge.None) continue;
            var cursor = System.Windows.Forms.Cursor.Position;
            var inRevealZone = window.IsCursorInRevealZone(cursor);
            if (window.IsEdgeHidden)
            {
                if (inRevealZone)
                {
                    window.RevealFromEdge(animate: true);
                    window.SetTemporaryTopmost(true);
                }
                continue;
            }

            var inExpandedBounds = window.IsCursorInExpandedBounds(cursor);
            if (inRevealZone || inExpandedBounds)
            {
                // A layout can receive a normal Z-order request while the
                // pointer is entering it. Keep the edge-revealed state truly
                // topmost until the pointer leaves the activation/contents area.
                if (!window.IsTemporaryTopmost)
                    window.SetTemporaryTopmost(true);
                continue;
            }

            if (!window.IsInteractionBusy)
            {
                window.HideToEdge(animate: true);
            }
        }
    }

    private bool IsTopmostRequested(DesktopGroupWindow window)
    {
        if (_state.Settings.InteractionMode == LayoutInteractionMode.EdgeHide) return true;
        var ids = new HashSet<Guid>();
        foreach (var binding in _state.Settings.TopmostHotKeys.Where(binding => _activeHotKeyBindings.Contains(binding.Id)))
        {
            if (binding.AllLayouts) ids.UnionWith(EnumerateLayoutIds());
            else ids.UnionWith(binding.LayoutIds);
        }
        if (window.Group.Definition.Tabs.Count == 0) return ids.Contains(window.Group.Definition.Id);
        return window.Group.Definition.Tabs.Any(tab => ids.Contains(tab.Id));
    }

    private void UpdateToolbarState()
    {
        var hotKey = _state.Settings.TopmostHotKeys.FirstOrDefault(binding => binding.Enabled)?.Gesture ?? "未设置";
        HideButton.Content = _groupsHidden ? "显示分组" : "隐藏分组";
        if (_state.Settings.InteractionMode == LayoutInteractionMode.EdgeHide)
        {
            TopmostButton.Content = "QQ模式：全部置顶";
            TopmostButton.IsEnabled = false;
        }
        else
        {
            TopmostButton.Content = _isTopmost ? $"取消置顶 {hotKey}" : $"置顶 {hotKey}";
            TopmostButton.IsEnabled = true;
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureCurrentLayout();
        var dialog = new SaveFileDialog
        {
            Title = "导出 Z-Desk 布局和设置",
            Filter = "Z-Desk 配置包 (*.zdesk)|*.zdesk|JSON 文件 (*.json)|*.json",
            FileName = $"ZDesk-{DateTime.Now:yyyyMMdd-HHmm}.zdesk",
            AddExtension = true,
            DefaultExt = ".zdesk"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _layoutStore.ExportAsync(_state, dialog.FileName);
            StatusText.Text = $"已导出：{dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 Z-Desk 布局和设置",
            Filter = "Z-Desk 配置包 (*.zdesk;*.json)|*.zdesk;*.json",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = await _layoutStore.ImportAsync(dialog.FileName);
            var result = MessageBox.Show(
                $"配置包包含 {imported.Groups.Count} 个分组。\n\n是：替换当前布局\n否：合并到当前布局",
                "导入方式",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                _state = imported;
                CreateDesktopSurface();
            }
            else
            {
                foreach (var group in imported.Groups)
                {
                    group.Id = Guid.NewGuid();
                    group.X += 24;
                    group.Y += 24;
                    _state.Groups.Add(group);
                }
            }

            RecreateDesktopGroups();
            await SaveNowAsync();
            StatusText.Text = "布局和设置导入完成";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ScheduleSave()
    {
        if (!_isLoaded)
        {
            return;
        }

        CaptureCurrentLayout();
        SynchronizeOpenSettingsLayouts();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_saveRunning)
        {
            _savePending = true;
            return;
        }

        _saveRunning = true;
        try { await SaveNowAsync(); }
        catch (Exception ex) { LogService.Warning("Scheduled layout save failed", ex); }
        finally
        {
            _saveRunning = false;
            if (_savePending)
            {
                _savePending = false;
                _saveTimer.Start();
            }
        }
    }

    private async Task SaveNowAsync()
    {
        CaptureCurrentLayout();
        SynchronizeOpenSettingsLayouts();
        await _layoutStore.SaveAsync(_state);
        if (_state.Settings.AutoSwitchDisplayLayouts)
            await _displayProfiles.SaveAsync(_displaySignature, _state.Groups);
    }

    private void SynchronizeOpenSettingsLayouts() => _settingsWindow?.SynchronizeLayouts(_state.Groups);

    private void CaptureCurrentLayout()
    {
        foreach (var window in _desktopWindows)
        {
            var definition = window.Group.Definition;
            definition.StoreActiveTab();
            // Hidden edge-dock windows intentionally sit outside the visible
            // desktop. Their persisted desktop position must remain the last
            // expanded position, otherwise an unrelated layout move makes them
            // impossible to reveal or restore to standard mode.
            if (!window.IsEdgeHidden)
            {
                definition.DesktopX = window.Left;
                definition.DesktopY = window.Top;
            }
            definition.Width = window.ActualWidth;
            if (!definition.IsCollapsed && !window.Group.IsSizeTransitionActive)
            {
                definition.Height = window.ActualHeight;
            }
        }
    }

    private void ExitApplicationButton_Click(object sender, RoutedEventArgs e) => RequestApplicationExit();

    public void RequestApplicationExit()
    {
        _applicationExitRequested = true;
        Close();
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_applicationExitRequested && !_shutdownReady)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_shutdownReady)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        _isLoaded = false;
        _saveTimer.Stop();
        try
        {
            await SaveNowAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"退出前保存失败：{ex.Message}";
        }

        foreach (var window in _desktopWindows.ToArray())
        {
            window.Close();
        }

        _desktopWindows.Clear();
        _explorerIconVisibility.Restore();
        _desktopSurface?.Close();
        _desktopSurface = null;
        _qrFrameController.Dispose();
        _qrResultsWindow?.Close();
        _qrResultsWindow = null;
        _desktopFiles.Dispose();
        _desktopFiles.Changed -= DesktopFiles_Changed;
        _shellChangeNotifications.Dispose();
        _explorerIconVisibility.Dispose();

        _hotKeyService.Dispose();
        _desktopDoubleClickService.Dispose();
        _trayIconService.Dispose();
        _searchTimer.Stop();
        _ruleTimer.Stop();
        _edgeTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        _recoveryService.MarkSessionCompleted();

        _shutdownReady = true;
        Application.Current.Shutdown(0);
    }
}
