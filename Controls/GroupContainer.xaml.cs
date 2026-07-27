using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZDesk.Models;
using ZDesk.Services;
using ZDesk.Windows;

namespace ZDesk.Controls;

public partial class GroupContainer : System.Windows.Controls.UserControl, IDisposable
{
    private const double SingleHeaderHeight = 36;
    private const double TabbedHeaderHeight = 60;
    private const string InternalDragFormat = "ZDesk.InternalLayoutItems";
    private sealed record InternalDragPayload(Guid GroupId, string[] Paths);
    public static readonly DependencyProperty IconTileWidthProperty = DependencyProperty.Register(
        nameof(IconTileWidth), typeof(double), typeof(GroupContainer), new PropertyMetadata(88.0));
    public static readonly DependencyProperty ViewItemWidthProperty = DependencyProperty.Register(
        nameof(ViewItemWidth), typeof(double), typeof(GroupContainer), new PropertyMetadata(88.0));
    public static readonly DependencyProperty ViewItemHeightProperty = DependencyProperty.Register(
        nameof(ViewItemHeight), typeof(double), typeof(GroupContainer), new PropertyMetadata(76.0));
    public static readonly DependencyProperty ViewIconSizeProperty = DependencyProperty.Register(
        nameof(ViewIconSize), typeof(double), typeof(GroupContainer), new PropertyMetadata(34.0));
    private readonly ObservableCollection<FileEntry> _files = [];
    private readonly IShellFileOperationService _shellFileOperations = new ShellFileOperationService();
    private readonly FilePreviewService _filePreviewService = new();
    private readonly DispatcherTimer _folderRefreshTimer;
    private readonly DispatcherTimer _offlineRetryTimer;
    private readonly DispatcherTimer _autoCollapseTimer;
    private FileSystemWatcher? _watcher;
    private System.Windows.Point _dragStart;
    private System.Windows.Point _fileDragStart;
    private double _startLeft;
    private double _startTop;
    private Point _desktopDragCursorStart;
    private double _desktopDragStartLeft;
    private double _desktopDragStartTop;
    private bool _desktopDragMoved;
    private bool _edgeHideMode;
    private readonly bool _desktopHosted;
    private int _visibilityAnimationVersion;
    private int _sizeAnimationVersion;
    private bool _isSizeTransitionActive;
    private bool _isTransferring;
    private bool _internalReorderCompleted;
    private bool _committingInlineRename;
    private bool _committingLayoutRename;
    private bool _fileDragArmed;
    private bool _internalDragInProgress;
    private string[] _armedDragPaths = [];
    private string? _focusedEntryPath;
    private bool _boxSelecting;
    private Point _boxSelectionStart;
    private Point _tabDragStart;
    private Guid? _draggedTabId;
    private Guid? _renamingTabId;
    private TextBox? _tabRenameEditor;
    private bool _committingTabRename;
    private int _sortVersion;
    private string _folderSignature = string.Empty;
    private string _pendingFolderChangeDetail = "none";
    private readonly HashSet<string> _pendingFolderChangePaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _folderRefreshRunning;
    private bool _folderRefreshPending;
    private int _folderRefreshVersion;
    private bool _layoutMenuOpen;
    private bool _collapsedByAuto;

    public GroupDefinition Definition { get; }

    public event EventHandler? LayoutChanged;
    public event EventHandler? RemoveRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event Action<GroupKind>? CreateLayoutRequested;
    public event EventHandler<string>? StatusChanged;
    public event Action<Point>? HeaderDragCompleted;

    public bool IsInteractionBusy => _layoutMenuOpen || _isTransferring || _internalDragInProgress ||
        _boxSelecting || _isSizeTransitionActive || _tabRenameEditor is not null ||
        TitleRenameBox.Visibility == Visibility.Visible || Mouse.LeftButton == MouseButtonState.Pressed;

    public void SetEdgeHideMode(bool enabled)
    {
        _edgeHideMode = enabled;
        if (enabled) _autoCollapseTimer.Stop();
        else ScheduleAutoCollapse();
    }
    public event Action<LayoutTabDragPayload, Point>? TabDragStarted;

    public bool AnimationsEnabled { get; set; }
    public double AnimationSpeed { get; set; } = 1.0;
    public double IconTileWidth { get => (double)GetValue(IconTileWidthProperty); set => SetValue(IconTileWidthProperty, value); }
    public double ViewItemWidth { get => (double)GetValue(ViewItemWidthProperty); set => SetValue(ViewItemWidthProperty, value); }
    public double ViewItemHeight { get => (double)GetValue(ViewItemHeightProperty); set => SetValue(ViewItemHeightProperty, value); }
    public double ViewIconSize { get => (double)GetValue(ViewIconSizeProperty); set => SetValue(ViewIconSizeProperty, value); }
    public double CurrentHeaderHeight => Definition.HasMultipleTabs ? TabbedHeaderHeight : SingleHeaderHeight;
    public bool IsSizeTransitionActive => _isSizeTransitionActive;

    public GroupContainer(
        GroupDefinition definition,
        bool desktopHosted = false,
        bool animationsEnabled = true,
        double containerOpacity = 0.92,
        double cornerRadius = 11,
        double iconSize = 88,
        double animationSpeed = 1.0)
    {
        InitializeComponent();
        Definition = definition;
        _collapsedByAuto = definition.AutoCollapse && definition.IsCollapsed;
        _desktopHosted = desktopHosted;
        AnimationsEnabled = animationsEnabled;
        RootBorder.Opacity = Math.Clamp(containerOpacity, 0.55, 1.0);
        RootBorder.CornerRadius = new CornerRadius(Math.Clamp(cornerRadius, 0, 24));
        IconTileWidth = Math.Clamp(iconSize, 68, 112);
        AnimationSpeed = Math.Clamp(animationSpeed, 0.5, 2.0);
        ApplyViewMode();
        _folderRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _folderRefreshTimer.Tick += FolderRefreshTimer_Tick;
        _offlineRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _offlineRetryTimer.Tick += (_, _) =>
        {
            if (Definition.FolderPath is not null && Directory.Exists(Definition.FolderPath))
            {
                _offlineRetryTimer.Stop();
                LoadFolder();
                StatusChanged?.Invoke(this, $"映射已恢复：{Definition.Title}");
            }
        };
        _autoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _autoCollapseTimer.Tick += (_, _) =>
        {
            _autoCollapseTimer.Stop();
            if (!Definition.AutoCollapse || IsMouseOver) return;
            if (!CanAutoCollapse())
            {
                _autoCollapseTimer.Start();
                return;
            }

            _collapsedByAuto = true;
            SetCollapsed(true, notifyLayoutChanged: false);
        };
        FileList.ItemsSource = _files;
        ApplyDefinition();
        LoadFolder();
        Loaded += (_, _) => ScheduleAutoCollapse();
    }

    public void ApplyAppearance(bool animationsEnabled, double containerOpacity, double cornerRadius, double iconSize, double animationSpeed)
    {
        AnimationsEnabled = animationsEnabled;
        RootBorder.Opacity = Math.Clamp(containerOpacity, 0.55, 1.0);
        RootBorder.CornerRadius = new CornerRadius(Math.Clamp(cornerRadius, 0, 24));
        IconTileWidth = Math.Clamp(iconSize, 68, 112);
        AnimationSpeed = Math.Clamp(animationSpeed, 0.5, 2.0);
        ApplyViewMode();
    }

    /// <summary>Updates title and other chrome without rebuilding the item list.</summary>
    public void RefreshDefinitionChrome() => ApplyDefinition();

    public void Dispose()
    {
        _folderRefreshTimer.Stop();
        _offlineRetryTimer.Stop();
        _autoCollapseTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        DisposeFileEntries();
    }

    private void ApplyDefinition()
    {
        if (_desktopHosted)
        {
            Width = double.NaN;
            Height = double.NaN;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            Width = Definition.Width;
            Height = Definition.IsCollapsed ? CurrentHeaderHeight : Definition.Height;
        }

        HeaderRow.Height = new GridLength(CurrentHeaderHeight);
        if (_desktopHosted && Definition.IsCollapsed && Window.GetWindow(this) is DesktopGroupWindow hostWindow)
            hostWindow.Height = CurrentHeaderHeight;
        TitleText.Text = Definition.Title;
        PathText.Text = Definition.Kind == GroupKind.Folder ? Definition.FolderPath : "普通布局";
        RenderTabs();
        ApplyViewMode();
        OpenButton.Visibility = Definition.Kind == GroupKind.Folder ? Visibility.Visible : Visibility.Collapsed;
        ContentPanel.Visibility = Definition.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ContentPanel.Opacity = Definition.IsCollapsed ? 0 : 1;
        ContentRow.Height = Definition.IsCollapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        UpdateHeaderGlyphs();
        ResizeThumb.Visibility = Definition.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ResizeThumb.IsEnabled = true;
    }

    private void RenderTabs()
    {
        TabStrip.Children.Clear();
        var showTabs = Definition.HasMultipleTabs;
        TabStrip.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        if (!showTabs) return;

        for (var index = 0; index < Definition.Tabs.Count; index++)
        {
            var tab = Definition.Tabs[index];
            var active = index == Definition.ActiveTabIndex;
            var button = new Button
            {
                Content = tab.Title,
                Tag = tab.Id,
                Style = (Style)FindResource(active
                    ? "GroupTabButtonActiveStyle"
                    : "GroupTabButtonStyle"),
                ToolTip = "单击切换；拖出后拆分为独立布局"
            };
            button.Click += TabButton_Click;
            button.PreviewMouseLeftButtonDown += TabButton_PreviewMouseLeftButtonDown;
            button.PreviewMouseMove += TabButton_PreviewMouseMove;
            button.PreviewMouseDoubleClick += TabButton_PreviewMouseDoubleClick;
            button.PreviewMouseRightButtonUp += TabButton_PreviewMouseRightButtonUp;
            TabStrip.Children.Add(button);
        }
    }

    private void ApplyViewMode()
    {
        var (templateKey, panelKey, itemWidth, itemHeight, iconSize) = Definition.ViewMode switch
        {
            LayoutViewMode.ExtraLargeIcons => ("IconFileTemplate", "WrapFileItemsPanel", 132d, 116d, 64d),
            LayoutViewMode.LargeIcons => ("IconFileTemplate", "WrapFileItemsPanel", 112d, 96d, 48d),
            LayoutViewMode.MediumIcons => ("IconFileTemplate", "WrapFileItemsPanel", IconTileWidth, 76d, 34d),
            LayoutViewMode.SmallIcons => ("CompactFileTemplate", "WrapFileItemsPanel", 156d, 32d, 18d),
            LayoutViewMode.List => ("CompactFileTemplate", "StackFileItemsPanel", double.NaN, 32d, 18d),
            LayoutViewMode.Details => ("DetailsFileTemplate", "StackFileItemsPanel", double.NaN, 30d, 18d),
            LayoutViewMode.Tiles => ("TileFileTemplate", "WrapFileItemsPanel", 220d, 64d, 38d),
            LayoutViewMode.Content => ("ContentFileTemplate", "StackFileItemsPanel", double.NaN, 58d, 32d),
            _ => ("IconFileTemplate", "WrapFileItemsPanel", IconTileWidth, 76d, 34d)
        };

        ViewItemWidth = itemWidth;
        ViewItemHeight = itemHeight;
        ViewIconSize = iconSize;
        FileList.ItemTemplate = (DataTemplate)FindResource(templateKey);
        FileList.ItemsPanel = (ItemsPanelTemplate)FindResource(panelKey);
        DetailsHeader.Visibility = Definition.ViewMode == LayoutViewMode.Details
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSortHeaders();
    }

    private void SetViewMode(LayoutViewMode mode)
    {
        if (Definition.ViewMode == mode) return;
        Definition.ViewMode = mode;
        Definition.StoreActiveTab();
        ApplyViewMode();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, $"视图已切换为：{ViewModeLabel(mode)}");
    }

    private async void DetailsHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string propertyName } ||
            !Enum.TryParse<LayoutSortProperty>(propertyName, out var property)) return;

        if (Definition.SortProperty == property)
            Definition.SortDescending = !Definition.SortDescending;
        else
        {
            Definition.SortProperty = property;
            Definition.SortDescending = false;
        }

        Definition.StoreActiveTab();
        UpdateSortHeaders();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        await SortCurrentItemsAsync(notify: true);
    }

    private void UpdateSortHeaders()
    {
        SetSortHeader(NameSortButton, "名称", LayoutSortProperty.Name);
        SetSortHeader(ModifiedSortButton, "修改日期", LayoutSortProperty.Modified);
        SetSortHeader(TypeSortButton, "类型", LayoutSortProperty.Type);
        SetSortHeader(SizeSortButton, "大小", LayoutSortProperty.Size);
    }

    private void SetSortHeader(Button button, string label, LayoutSortProperty property)
    {
        var active = Definition.SortProperty == property;
        button.Content = active ? $"{label} {(Definition.SortDescending ? "↓" : "↑")}" : label;
        button.Foreground = active
            ? new SolidColorBrush(Color.FromRgb(245, 247, 250))
            : new SolidColorBrush(Color.FromArgb(175, 192, 199, 208));
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void QueueSavedSort()
    {
        if (Definition.SortProperty != LayoutSortProperty.Manual)
            _ = SortCurrentItemsAsync(notify: true);
    }

    private async Task SortCurrentItemsAsync(bool notify)
    {
        if (Definition.SortProperty == LayoutSortProperty.Manual || _files.Count < 2) return;
        var requestVersion = ++_sortVersion;
        var sortProperty = Definition.SortProperty;
        var sortDescending = Definition.SortDescending;
        var entries = _files.ToArray();
        if (requestVersion != _sortVersion) return;

        IOrderedEnumerable<FileEntry> ordered = entries.OrderByDescending(entry => entry.IsDirectory);
        ordered = sortProperty switch
        {
            LayoutSortProperty.Name => sortDescending
                ? ordered.ThenByDescending(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                : ordered.ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase),
            LayoutSortProperty.Modified => sortDescending
                ? ordered.ThenByDescending(entry => entry.ModifiedTime)
                : ordered.ThenBy(entry => entry.ModifiedTime),
            LayoutSortProperty.Type => sortDescending
                ? ordered.ThenByDescending(entry => entry.TypeName, StringComparer.CurrentCultureIgnoreCase)
                : ordered.ThenBy(entry => entry.TypeName, StringComparer.CurrentCultureIgnoreCase),
            LayoutSortProperty.Size => sortDescending
                ? ordered.ThenByDescending(entry => entry.SizeBytes ?? -1)
                : ordered.ThenBy(entry => entry.SizeBytes ?? -1),
            _ => ordered.ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        if (sortProperty != LayoutSortProperty.Name)
            ordered = ordered.ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase);

        var sorted = ordered.ToArray();
        if (entries.Select(entry => entry.FullPath).SequenceEqual(
                sorted.Select(entry => entry.FullPath), StringComparer.OrdinalIgnoreCase)) return;

        var selectedPaths = SelectedEntries().Select(entry => entry.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplyEntryOrder(sorted);
        foreach (var entry in sorted.Where(entry => selectedPaths.Contains(entry.FullPath)))
            FileList.SelectedItems.Add(entry);

        PersistCurrentOrder();
        Definition.StoreActiveTab();
        if (notify) LayoutChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this,
            $"已按{SortPropertyLabel(sortProperty)}{(sortDescending ? "降序" : "升序")}排列");
    }

    private static string SortPropertyLabel(LayoutSortProperty property) => property switch
    {
        LayoutSortProperty.Name => "名称",
        LayoutSortProperty.Modified => "修改日期",
        LayoutSortProperty.Type => "类型",
        LayoutSortProperty.Size => "大小",
        _ => "手动顺序"
    };

    private static string ViewModeLabel(LayoutViewMode mode) => mode switch
    {
        LayoutViewMode.ExtraLargeIcons => "超大图标",
        LayoutViewMode.LargeIcons => "大图标",
        LayoutViewMode.MediumIcons => "中等图标",
        LayoutViewMode.SmallIcons => "小图标",
        LayoutViewMode.List => "列表",
        LayoutViewMode.Details => "详细信息",
        LayoutViewMode.Tiles => "平铺",
        LayoutViewMode.Content => "内容",
        _ => "中等图标"
    };

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid tabId }) return;
        ActivateTab(tabId);
    }

    private void ActivateTab(Guid tabId)
    {
        var index = Definition.Tabs.FindIndex(tab => tab.Id == tabId);
        if (index < 0 || index == Definition.ActiveTabIndex) return;
        Definition.ActivateTab(index);
        ApplyDefinition();
        LoadFolder();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TabButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid tabId })
        {
            if (_tabRenameEditor is not null)
            {
                FinishTabInlineRename(commit: true);
                Dispatcher.BeginInvoke(() => ActivateTab(tabId), DispatcherPriority.Input);
                e.Handled = true;
                return;
            }
            _draggedTabId = tabId;
            _tabDragStart = e.GetPosition(this);
        }
    }

    private void TabButton_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid tabId })
        {
            _draggedTabId = null;
            Dispatcher.BeginInvoke(() => StartTabInlineRename(tabId), DispatcherPriority.ApplicationIdle);
        }
        e.Handled = true;
    }

    private void TabButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid tabId }) return;
        if (_tabRenameEditor is not null) FinishTabInlineRename(commit: true);
        ActivateTab(tabId);
        ShowLayoutMenu(e.GetPosition(this));
        e.Handled = true;
    }

    private void TabButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedTabId is not Guid tabId || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _draggedTabId = null;
        var originalIndex = Definition.Tabs.FindIndex(tab => tab.Id == tabId);
        if (originalIndex < 0) return;
        if (originalIndex != Definition.ActiveTabIndex)
        {
            Definition.ActivateTab(originalIndex);
            ApplyDefinition();
            LoadFolder();
        }
        var page = Definition.RemoveTab(tabId);
        ApplyDefinition();
        LoadFolder();
        var payload = new LayoutTabDragPayload(page, originalIndex, Definition.AutoCollapse);
        TabDragStarted?.Invoke(payload, GetCursorScreenPosition());
    }

    private Point GetCursorScreenPosition()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var physical = new Point(cursor.X, cursor.Y);
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(physical) ?? physical;
    }

    private void LoadFolder()
    {
        ++_sortVersion;
        _watcher?.Dispose();
        _watcher = null;
        if (Definition.Kind != GroupKind.Folder || string.IsNullOrWhiteSpace(Definition.FolderPath))
        {
            LayoutItemStateService.Normalize(Definition.PinnedPaths, Definition.ItemOrder);
            SynchronizePinnedItems();
            EmptyMessage.Text = _files.Count == 0 ? "将文件或文件夹拖到这里固定引用" : string.Empty;
            EmptyMessagePanel.Visibility = Visibility.Collapsed;
            QueueSavedSort();
            return;
        }

        var folder = Definition.FolderPath;
        if (!Directory.Exists(folder))
        {
            DisposeFileEntries();
            _folderSignature = string.Empty;
            EmptyMessage.Text = "映射目录当前不可用";
            EmptyMessagePanel.Visibility = Visibility.Visible;
            _offlineRetryTimer.Start();
            return;
        }

        _offlineRetryTimer.Stop();

        try
        {
            var paths = Directory.EnumerateFileSystemEntries(folder).Take(500).ToArray();
            ApplyFolderPaths(paths);
            _folderSignature = BuildFolderSignature(paths);
            PersistCurrentOrder();
            QueueSavedSort();

            EmptyMessage.Text = _files.Count == 0 ? "这个文件夹是空的" : string.Empty;
            EmptyMessagePanel.Visibility = Visibility.Collapsed;

            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _watcher.Changed += Folder_Changed;
            _watcher.Created += Folder_Changed;
            _watcher.Deleted += Folder_Changed;
            _watcher.Renamed += Folder_Changed;
        }
        catch (UnauthorizedAccessException)
        {
            EmptyMessage.Text = "没有权限读取这个文件夹";
            EmptyMessagePanel.Visibility = Visibility.Visible;
        }
        catch (IOException)
        {
            EmptyMessage.Text = "读取文件夹时发生错误";
            EmptyMessagePanel.Visibility = Visibility.Visible;
        }
    }

    public void RefreshItems()
    {
        if (Definition.Kind == GroupKind.Empty)
        {
            SynchronizePinnedItems();
            return;
        }
        LoadFolder();
    }

    public void ApplyDesktopFileChanges(IReadOnlyList<DesktopFileChange> changes)
    {
        if (Definition.Kind != GroupKind.Empty) return;
        var selected = SelectedEntries().Select(entry => entry.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rename in changes.Where(change =>
                     change.ChangeType == WatcherChangeTypes.Renamed && change.OldFullPath is not null))
        {
            var entry = _files.FirstOrDefault(item => string.Equals(
                item.FullPath, rename.OldFullPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null || (!File.Exists(rename.FullPath) && !Directory.Exists(rename.FullPath))) continue;
            var index = _files.IndexOf(entry);
            _files[index] = CreateEntry(rename.FullPath);
            entry.Dispose();
            if (selected.Remove(rename.OldFullPath!)) selected.Add(rename.FullPath);
            if (string.Equals(_focusedEntryPath, rename.OldFullPath, StringComparison.OrdinalIgnoreCase))
                _focusedEntryPath = rename.FullPath;
        }

        SynchronizePinnedItems(selected);
    }

    private void SynchronizePinnedItems(HashSet<string>? selected = null)
    {
        selected ??= SelectedEntries().Select(entry => entry.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = OrderPaths(Definition.PinnedPaths
                .Where(path => File.Exists(path) || Directory.Exists(path)))
            .ToArray();
        var existing = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        for (var index = _files.Count - 1; index >= 0; index--)
        {
            var entry = _files[index];
            if (!existing.TryAdd(entry.FullPath, entry))
            {
                _files.RemoveAt(index);
                entry.Dispose();
            }
        }

        for (var targetIndex = 0; targetIndex < desired.Length; targetIndex++)
        {
            var path = desired[targetIndex];
            if (!existing.TryGetValue(path, out var entry))
            {
                entry = CreateEntry(path);
                existing[path] = entry;
            }

            var currentIndex = _files.IndexOf(entry);
            if (currentIndex < 0) _files.Insert(targetIndex, entry);
            else if (currentIndex != targetIndex) _files.Move(currentIndex, targetIndex);
        }

        while (_files.Count > desired.Length)
        {
            var entry = _files[^1];
            _files.RemoveAt(_files.Count - 1);
            entry.Dispose();
        }

        FileList.SelectedItems.Clear();
        foreach (var entry in _files.Where(entry => selected.Contains(entry.FullPath)))
            FileList.SelectedItems.Add(entry);
        EmptyMessage.Text = _files.Count == 0 ? "将文件或文件夹拖到这里固定引用" : string.Empty;
        if (Definition.SortProperty != LayoutSortProperty.Manual) _ = SortCurrentItemsAsync(notify: false);
    }

    private void ApplyFolderPaths(IReadOnlyList<string> paths, IReadOnlySet<string>? changedPaths = null)
    {
        var order = LayoutItemStateService.BuildOrderIndex(Definition.ItemOrder);
        var desired = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => order.TryGetValue(path, out var index) ? 0 : 1)
            .ThenBy(path => order.TryGetValue(path, out var index) ? index : int.MaxValue)
            .ThenByDescending(path => Directory.Exists(path))
            .ThenBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var desiredSet = desired.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);

        for (var index = _files.Count - 1; index >= 0; index--)
        {
            var entry = _files[index];
            if (!desiredSet.Contains(entry.FullPath) || !existing.TryAdd(entry.FullPath, entry))
            {
                _files.RemoveAt(index);
                entry.Dispose();
            }
        }

        for (var targetIndex = 0; targetIndex < desired.Length; targetIndex++)
        {
            var path = desired[targetIndex];
            if (!existing.TryGetValue(path, out var entry) ||
                (changedPaths?.Contains(path) == true))
            {
                var replacement = CreateEntry(path);
                if (entry is not null)
                {
                    var oldIndex = _files.IndexOf(entry);
                    if (oldIndex >= 0) _files[oldIndex] = replacement;
                    entry.Dispose();
                }
                existing[path] = replacement;
                entry = replacement;
            }

            var currentIndex = _files.IndexOf(entry);
            if (currentIndex < 0) _files.Insert(targetIndex, entry);
            else if (currentIndex != targetIndex) _files.Move(currentIndex, targetIndex);
        }

        while (_files.Count > desired.Length)
        {
            var entry = _files[^1];
            _files.RemoveAt(_files.Count - 1);
            entry.Dispose();
        }
    }

    private void DisposeFileEntries()
    {
        foreach (var entry in _files) entry.Dispose();
        _files.Clear();
    }

    private IEnumerable<string> OrderPaths(IEnumerable<string> paths)
    {
        var order = LayoutItemStateService.BuildOrderIndex(Definition.ItemOrder);
        return paths.OrderBy(path => order.TryGetValue(path, out var index) ? index : int.MaxValue);
    }

    private void PersistCurrentOrder()
    {
        Definition.ItemOrder = _files.Select(entry => entry.FullPath).ToList();
        if (Definition.Kind == GroupKind.Empty)
        {
            Definition.PinnedPaths = [.. Definition.ItemOrder];
        }
    }

    private void Folder_Changed(object sender, FileSystemEventArgs e)
    {
        _pendingFolderChangeDetail = $"{e.ChangeType}:{e.FullPath}";
        lock (_pendingFolderChangePaths)
        {
            _pendingFolderChangePaths.Add(e.FullPath);
            if (e is RenamedEventArgs renamed) _pendingFolderChangePaths.Add(renamed.OldFullPath);
        }
        Dispatcher.BeginInvoke(() =>
        {
            _folderRefreshTimer.Stop();
            _folderRefreshTimer.Start();
        }, DispatcherPriority.Background);
    }

    private async void FolderRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _folderRefreshTimer.Stop();
        if (_folderRefreshRunning)
        {
            _folderRefreshPending = true;
            return;
        }

        _folderRefreshRunning = true;
        try
        {
            var folder = Definition.FolderPath;
            if (Definition.Kind != GroupKind.Folder || string.IsNullOrWhiteSpace(folder))
            {
                LoadFolder();
                return;
            }

            var version = ++_folderRefreshVersion;
            var snapshot = await Task.Run(() => CaptureFolderSnapshot(folder));
            if (version != _folderRefreshVersion || snapshot is null) return;
            if (!string.Equals(_folderSignature, snapshot.Signature, StringComparison.Ordinal))
                await ReconcileFolderItemsAsync(snapshot.Paths, snapshot.Signature);
        }
        catch (Exception ex)
        {
            LogService.Warning($"Folder refresh failed: {Definition.FolderPath}", ex);
        }
        finally
        {
            _folderRefreshRunning = false;
            if (_folderRefreshPending)
            {
                _folderRefreshPending = false;
                _folderRefreshTimer.Start();
            }
        }
    }

    private bool FolderSnapshotChanged()
    {
        if (Definition.Kind != GroupKind.Folder || string.IsNullOrWhiteSpace(Definition.FolderPath) ||
            !Directory.Exists(Definition.FolderPath)) return true;
        try
        {
            var paths = Directory.EnumerateFileSystemEntries(Definition.FolderPath).Take(500).ToArray();
            return !string.Equals(_folderSignature, BuildFolderSignature(paths), StringComparison.Ordinal);
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string BuildFolderSignature(IEnumerable<string> paths) => string.Join("|", paths
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path =>
        {
            var isDirectory = Directory.Exists(path);
            var stamp = isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            var size = isDirectory ? 0 : new FileInfo(path).Length;
            return $"{path}\u001f{stamp.Ticks}\u001f{size}";
        }));

    private sealed record FolderSnapshot(string[] Paths, string Signature);

    private static FolderSnapshot? CaptureFolderSnapshot(string folder)
    {
        try
        {
            var paths = Directory.EnumerateFileSystemEntries(folder).Take(500).ToArray();
            return new FolderSnapshot(paths, BuildFolderSignature(paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task ReconcileFolderItemsAsync(IReadOnlyList<string>? snapshotPaths = null, string? snapshotSignature = null)
    {
        if (Definition.Kind != GroupKind.Folder || string.IsNullOrWhiteSpace(Definition.FolderPath) ||
            !Directory.Exists(Definition.FolderPath))
        {
            LoadFolder();
            return;
        }

        try
        {
            var snapshot = snapshotPaths is null
                ? await Task.Run(() => CaptureFolderSnapshot(Definition.FolderPath!))
                : new FolderSnapshot(snapshotPaths.ToArray(), snapshotSignature ?? BuildFolderSignature(snapshotPaths));
            if (snapshot is null) return;
            var paths = snapshot.Paths;
            HashSet<string> changed;
            lock (_pendingFolderChangePaths)
            {
                changed = new HashSet<string>(_pendingFolderChangePaths, StringComparer.OrdinalIgnoreCase);
                _pendingFolderChangePaths.Clear();
            }

            var selected = SelectedEntries().Select(entry => entry.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ApplyFolderPaths(paths, changed);

            FileList.SelectedItems.Clear();
            foreach (var entry in _files.Where(entry => selected.Contains(entry.FullPath)))
                FileList.SelectedItems.Add(entry);

            _folderSignature = snapshot.Signature;
            PersistCurrentOrder();
            Definition.StoreActiveTab();
            if (Definition.SortProperty != LayoutSortProperty.Manual)
                await SortCurrentItemsAsync(notify: false);
            EmptyMessagePanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"Could not reconcile mapped folder: {Definition.FolderPath}", ex);
        }
    }

    private static FileEntry CreateEntry(string path) => new(
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, Directory.Exists(path));

    private void RefreshFolderSignature()
    {
        if (Definition.Kind != GroupKind.Folder || string.IsNullOrWhiteSpace(Definition.FolderPath) ||
            !Directory.Exists(Definition.FolderPath)) return;
        try
        {
            _folderSignature = BuildFolderSignature(
                Directory.EnumerateFileSystemEntries(Definition.FolderPath).Take(500));
            lock (_pendingFolderChangePaths) _pendingFolderChangePaths.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
        {
            return;
        }

        var hostWindow = Window.GetWindow(this);
        if (hostWindow is DesktopGroupWindow desktopWindow)
        {
            desktopWindow.RevealFromEdge(animate: false);
            _desktopDragCursorStart = GetCursorScreenPosition();
            _desktopDragStartLeft = hostWindow.Left;
            _desktopDragStartTop = hostWindow.Top;
            _desktopDragMoved = false;
            DragHeader.CaptureMouse();
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition(Parent as UIElement);
        _startLeft = Canvas.GetLeft(this);
        _startTop = Canvas.GetTop(this);
        DragHeader.CaptureMouse();
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button) return true;
        }
        return false;
    }

    private void Header_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!DragHeader.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (Window.GetWindow(this) is DesktopGroupWindow desktopWindow)
        {
            var cursor = GetCursorScreenPosition();
            var deltaX = cursor.X - _desktopDragCursorStart.X;
            var deltaY = cursor.Y - _desktopDragCursorStart.Y;
            var position = SnapToDesktopEdges(
                _desktopDragStartLeft + deltaX,
                _desktopDragStartTop + deltaY,
                desktopWindow.ActualWidth,
                desktopWindow.ActualHeight);
            desktopWindow.Left = position.X;
            desktopWindow.Top = position.Y;
            _desktopDragMoved |= Math.Abs(deltaX) >= 2 || Math.Abs(deltaY) >= 2;
            e.Handled = true;
            return;
        }

        var current = e.GetPosition(Parent as UIElement);
        var left = Math.Max(0, _startLeft + current.X - _dragStart.X);
        var top = Math.Max(0, _startTop + current.Y - _dragStart.Y);
        const double grid = 12;
        const double threshold = 5;
        var snappedLeft = Math.Round(left / grid) * grid;
        var snappedTop = Math.Round(top / grid) * grid;
        Canvas.SetLeft(this, Math.Abs(snappedLeft - left) <= threshold ? snappedLeft : left);
        Canvas.SetTop(this, Math.Abs(snappedTop - top) <= threshold ? snappedTop : top);
    }

    private Point SnapToDesktopEdges(double left, double top, double width, double height)
    {
        const double threshold = 14;
        var cursor = System.Windows.Forms.Cursor.Position;
        var workingArea = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(workingArea.Left, workingArea.Top));
        var bottomRight = transform.Transform(new Point(workingArea.Right, workingArea.Bottom));

        if (Math.Abs(left - topLeft.X) <= threshold) left = topLeft.X;
        if (Math.Abs(left + width - bottomRight.X) <= threshold) left = bottomRight.X - width;
        if (Math.Abs(top - topLeft.Y) <= threshold) top = topLeft.Y;
        if (Math.Abs(top + height - bottomRight.Y) <= threshold) top = bottomRight.Y - height;

        // Keep the complete layout inside the display under the pointer. Once the
        // pointer enters another display, that display becomes the new boundary,
        // so clamping does not prevent intentional cross-monitor dragging.
        var maximumLeft = Math.Max(topLeft.X, bottomRight.X - width);
        var maximumTop = Math.Max(topLeft.Y, bottomRight.Y - height);
        left = Math.Clamp(left, topLeft.X, maximumLeft);
        top = Math.Clamp(top, topLeft.Y, maximumTop);
        return new Point(left, top);
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!DragHeader.IsMouseCaptured)
        {
            return;
        }

        DragHeader.ReleaseMouseCapture();
        if (Window.GetWindow(this) is DesktopGroupWindow desktopWindow)
        {
            Definition.DesktopX = desktopWindow.Left;
            Definition.DesktopY = desktopWindow.Top;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            if (_desktopDragMoved) HeaderDragCompleted?.Invoke(GetCursorScreenPosition());
            _desktopDragMoved = false;
            e.Handled = true;
            return;
        }
        Definition.X = Canvas.GetLeft(this);
        Definition.Y = Canvas.GetTop(this);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void Header_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowLayoutMenu(e.GetPosition(this));
        e.Handled = true;
    }

    private void ResizeGrip_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        StopSizeAnimation();
    }

    private void ResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (Window.GetWindow(this) is DesktopGroupWindow desktopWindow)
        {
            desktopWindow.Width = Math.Round(Math.Max(MinWidth, desktopWindow.ActualWidth + e.HorizontalChange));
            if (!Definition.IsCollapsed)
            {
                desktopWindow.Height = Math.Round(Math.Max(140, desktopWindow.ActualHeight + e.VerticalChange));
            }

            return;
        }

        Width = Math.Round(Math.Max(MinWidth, ActualWidth + e.HorizontalChange));
        if (!Definition.IsCollapsed)
        {
            Height = Math.Round(Math.Max(140, ActualHeight + e.VerticalChange));
        }
    }

    private void ResizeGrip_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var desktopWindow = Window.GetWindow(this) as DesktopGroupWindow;
        Definition.Width = Math.Round(desktopWindow?.ActualWidth ?? ActualWidth);
        if (!Definition.IsCollapsed)
        {
            Definition.Height = Math.Round(desktopWindow?.ActualHeight ?? ActualHeight);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _collapsedByAuto = false;
        SetCollapsed(!Definition.IsCollapsed);
    }

    private void SetCollapsed(bool collapse, bool notifyLayoutChanged = true)
    {
        if (Definition.IsCollapsed == collapse) return;
        var resizeTarget = Window.GetWindow(this) is DesktopGroupWindow desktopWindow
            ? (FrameworkElement)desktopWindow
            : this;

        StopSizeAnimation();
        var animationVersion = ++_sizeAnimationVersion;
        if (!collapse)
        {
            ContentPanel.Visibility = Visibility.Visible;
            ContentPanel.IsHitTestVisible = false;
            ContentRow.Height = new GridLength(1, GridUnitType.Star);
            ResizeThumb.Visibility = Visibility.Visible;
        }

        Definition.IsCollapsed = collapse;
        UpdateHeaderGlyphs();

        if (!AnimationsEnabled)
        {
            _isSizeTransitionActive = false;
            resizeTarget.Height = collapse ? CurrentHeaderHeight : Definition.Height;
            ContentPanel.Opacity = collapse ? 0 : 1;
            ContentPanel.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            ContentPanel.IsHitTestVisible = !collapse;
            ContentRow.Height = collapse ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            ResizeThumb.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            if (notifyLayoutChanged) LayoutChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var targetHeight = collapse ? CurrentHeaderHeight : Definition.Height;
        _isSizeTransitionActive = true;
        var duration = ScaledDuration(collapse ? 155 : 210);
        var heightAnimation = new DoubleAnimation
        {
            From = resizeTarget.ActualHeight,
            To = targetHeight,
            Duration = duration,
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        heightAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _sizeAnimationVersion) return;
            _isSizeTransitionActive = false;
            resizeTarget.BeginAnimation(HeightProperty, null);
            resizeTarget.Height = targetHeight;
            ContentPanel.IsHitTestVisible = !collapse;
            if (collapse)
            {
                ContentPanel.Visibility = Visibility.Collapsed;
                ContentRow.Height = new GridLength(0);
                ResizeThumb.Visibility = Visibility.Collapsed;
            }

            if (notifyLayoutChanged) LayoutChanged?.Invoke(this, EventArgs.Empty);
        };

        var contentAnimation = new DoubleAnimation
        {
            To = collapse ? 0 : 1,
            Duration = ScaledDuration(collapse ? 100 : 185),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        resizeTarget.BeginAnimation(HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
        ContentPanel.BeginAnimation(OpacityProperty, contentAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void Group_MouseEnter(object sender, MouseEventArgs e)
    {
        _autoCollapseTimer.Stop();
        if (!Definition.AutoCollapse || !Definition.IsCollapsed) return;
        _collapsedByAuto = false;
        SetCollapsed(false, notifyLayoutChanged: false);
    }

    private void Group_MouseLeave(object sender, MouseEventArgs e) => ScheduleAutoCollapse();

    private void ScheduleAutoCollapse()
    {
        _autoCollapseTimer.Stop();
        if (!_edgeHideMode && Definition.AutoCollapse && !IsMouseOver) _autoCollapseTimer.Start();
    }

    private bool CanAutoCollapse() =>
        !_edgeHideMode &&
        !_layoutMenuOpen &&
        !_isTransferring &&
        !_internalDragInProgress &&
        !_boxSelecting &&
        !_isSizeTransitionActive &&
        _tabRenameEditor is null &&
        TitleRenameBox.Visibility != Visibility.Visible &&
        Mouse.LeftButton != MouseButtonState.Pressed;

    public void AnimateIn()
    {
        if (!AnimationsEnabled)
        {
            ++_visibilityAnimationVersion;
            BeginAnimation(OpacityProperty, null);
            VisibilityScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            VisibilityScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            VisibilityScale.ScaleX = 1;
            VisibilityScale.ScaleY = 1;
            Opacity = 1;
            Visibility = Visibility.Visible;
            return;
        }

        var version = ++_visibilityAnimationVersion;
        Visibility = Visibility.Visible;
        Opacity = 0;
        VisibilityScale.ScaleX = 0.965;
        VisibilityScale.ScaleY = 0.965;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation(1, ScaledDuration(170))
        {
            EasingFunction = easing
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (version == _visibilityAnimationVersion)
            {
                Opacity = 1;
            }
        };

        BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        VisibilityScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, ScaledDuration(205)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        VisibilityScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, ScaledDuration(205)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    public void AnimateOut(Action completed)
    {
        if (!AnimationsEnabled)
        {
            ++_visibilityAnimationVersion;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            completed();
            return;
        }

        var version = ++_visibilityAnimationVersion;
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacityAnimation = new DoubleAnimation(0, ScaledDuration(115))
        {
            EasingFunction = easing
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (version == _visibilityAnimationVersion)
            {
                completed();
            }
        };

        BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        VisibilityScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.975, ScaledDuration(115)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        VisibilityScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.975, ScaledDuration(115)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StopSizeAnimation()
    {
        ++_sizeAnimationVersion;
        _isSizeTransitionActive = false;
        var resizeTarget = Window.GetWindow(this) is DesktopGroupWindow desktopWindow
            ? (FrameworkElement)desktopWindow
            : this;
        var currentHeight = resizeTarget.ActualHeight;
        var currentOpacity = ContentPanel.Opacity;
        resizeTarget.BeginAnimation(HeightProperty, null);
        resizeTarget.Height = currentHeight;
        ContentPanel.BeginAnimation(OpacityProperty, null);
        ContentPanel.Opacity = currentOpacity;
    }

    private TimeSpan ScaledDuration(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds / AnimationSpeed);

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        OpenPath(Definition.FolderPath);
    }

    private void UpdateHeaderGlyphs()
    {
        CollapseGlyph.Data = Geometry.Parse(Definition.IsCollapsed
            ? "M2,8 L6.5,4 L11,8 L12,7 L6.5,2 L1,7 Z"
            : "M2,5 L6.5,9 L11,5 L12,6 L6.5,11 L1,6 Z");
    }

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _fileDragStart = e.GetPosition(FileList);
        var itemContainer = ItemsControl.ContainerFromElement(FileList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (itemContainer?.DataContext is FileEntry focusedEntry) _focusedEntryPath = focusedEntry.FullPath;
        _fileDragArmed = itemContainer is not null;
        _armedDragPaths = itemContainer?.IsSelected == true && FileList.SelectedItems.Count > 1
            ? SelectedEntries().Select(entry => entry.FullPath).ToArray()
            : [];
        if (_armedDragPaths.Length > 1) e.Handled = true;
        if (!_fileDragArmed)
        {
            _boxSelecting = true;
            _boxSelectionStart = _fileDragStart;
            FileList.SelectedItems.Clear();
            FileList.CaptureMouse();
            e.Handled = true;
        }
        Dispatcher.BeginInvoke(() =>
        {
            FileList.Focus();
        }, DispatcherPriority.Input);
    }

    private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _fileDragArmed = false;
        _armedDragPaths = [];
        if (!_boxSelecting) return;
        _boxSelecting = false;
        IconSelectionRectangle.Visibility = Visibility.Collapsed;
        FileList.ReleaseMouseCapture();
        FileList.Focus();
        e.Handled = true;
    }
    private void FileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) => _fileDragArmed = false;

    private void FileList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_boxSelecting)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _boxSelecting = false;
                IconSelectionRectangle.Visibility = Visibility.Collapsed;
                FileList.ReleaseMouseCapture();
                return;
            }
            UpdateBoxSelection(e.GetPosition(FileList));
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _fileDragArmed = false;
            _armedDragPaths = [];
            return;
        }
        if (!_fileDragArmed || _isTransferring)
        {
            return;
        }

        var current = e.GetPosition(FileList);
        if (Math.Abs(current.X - _fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var paths = _armedDragPaths.Length > 0
            ? _armedDragPaths
            : SelectedEntries().Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0)
        {
            return;
        }
        LogService.Info($"Drag start | group={Definition.Title} | count={paths.Length} | paths={string.Join(",", paths.Select(Path.GetFileName))}");

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        data.SetData(InternalDragFormat, new InternalDragPayload(Definition.Id, paths));
        _internalDragInProgress = true;
        DragDropEffects effect;
        try { effect = System.Windows.DragDrop.DoDragDrop(FileList, data, DragDropEffects.Move); }
        finally
        {
            _internalDragInProgress = false;
            _fileDragArmed = false;
            _armedDragPaths = [];
        }

        if (effect == DragDropEffects.Move && Definition.Kind == GroupKind.Empty && !_internalReorderCompleted)
        {
            LayoutItemStateService.RemovePinnedPaths(Definition.PinnedPaths, Definition.ItemOrder, paths);
            SynchronizePinnedItems();
            Definition.StoreActiveTab();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        _internalReorderCompleted = false;
    }

    private void Group_PreviewDragEnter(object sender, DragEventArgs e) => UpdateDropPreview(e);

    private void Group_PreviewDragOver(object sender, DragEventArgs e) => UpdateDropPreview(e);

    private void Group_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!_isTransferring)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateDropPreview(DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(InternalDragFormat) is InternalDragPayload payload && payload.GroupId == Definition.Id)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        if (!CanAcceptDrop(e.Data))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Effects = Definition.Kind == GroupKind.Folder
            ? GetShellDropEffect(e)
            : DragDropEffects.Move;
        e.Handled = true;
    }

    private async void Group_Drop(object sender, DragEventArgs e)
    {
        if (_internalDragInProgress && e.Data.GetData(InternalDragFormat) is InternalDragPayload internalDrag &&
            internalDrag.GroupId == Definition.Id)
        {
            ReorderItems(internalDrag.Paths, GetDropIndex(e.GetPosition(FileList)));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (Definition.Kind == GroupKind.Empty && e.Data.GetData(DataFormats.FileDrop, true) is string[] pinned)
        {
            foreach (var path in pinned.Where(path => File.Exists(path) || Directory.Exists(path)))
            {
                LayoutItemStateService.AddPinnedPath(Definition.PinnedPaths, Definition.ItemOrder, path);
            }
            DropOverlay.Visibility = Visibility.Collapsed;
            SynchronizePinnedItems();
            Definition.StoreActiveTab();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, $"已将 {pinned.Length} 项移入布局");
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        if (!CanAcceptDrop(e.Data) || Definition.FolderPath is null)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Handled = true;
        var paths = e.Data.GetData(DataFormats.FileDrop, autoConvert: true) as string[] ?? [];
        if (paths.Length == 0)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        _isTransferring = true;
        try
        {
            var effect = GetShellDropEffect(e);
            var result = effect == DragDropEffects.Copy
                ? await _shellFileOperations.CopyAsync(paths, Definition.FolderPath, GetOwnerHandle())
                : await _shellFileOperations.MoveAsync(paths, Definition.FolderPath, GetOwnerHandle());
            StatusChanged?.Invoke(this, result.Aborted ? "文件移动已取消" :
                result.Succeeded ? $"已{(effect == DragDropEffects.Copy ? "复制" : "移动")} {paths.Length} 项" :
                $"文件操作失败：{result.ErrorMessage}");
            if (result.Succeeded) await ReconcileFolderItemsAsync();
        }
        finally
        {
            _isTransferring = false;
            TransferProgress.Visibility = Visibility.Collapsed;
            CancelTransferButton.Visibility = Visibility.Collapsed;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelTransferButton_Click(object sender, RoutedEventArgs e)
    {
        // IFileOperation owns its native progress dialog and cancellation UI.
    }

    private bool CanAcceptDrop(IDataObject data) =>
        !_isTransferring &&
        (Definition.Kind == GroupKind.Empty || Definition.Kind == GroupKind.Folder) &&
        (Definition.Kind != GroupKind.Folder ||
        !string.IsNullOrWhiteSpace(Definition.FolderPath) &&
        Directory.Exists(Definition.FolderPath)) &&
        data.GetDataPresent(DataFormats.FileDrop, autoConvert: true);

    private DragDropEffects GetShellDropEffect(DragEventArgs e)
    {
        if (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) && e.AllowedEffects.HasFlag(DragDropEffects.Copy))
            return DragDropEffects.Copy;
        if (e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey) && e.AllowedEffects.HasFlag(DragDropEffects.Move))
            return DragDropEffects.Move;
        if (!e.AllowedEffects.HasFlag(DragDropEffects.Move)) return DragDropEffects.Copy;

        var paths = e.Data.GetData(DataFormats.FileDrop, true) as string[] ?? [];
        var destinationRoot = Definition.FolderPath is null ? null : Path.GetPathRoot(Definition.FolderPath);
        return paths.Length > 0 && paths.All(path => string.Equals(
            Path.GetPathRoot(path), destinationRoot, StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
    }

    private void ReorderItems(IEnumerable<string> paths, int targetIndex)
    {
        LogService.Info($"Internal reorder | group={Definition.Title} | paths={string.Join(",", paths.Select(Path.GetFileName))} | target={targetIndex}");
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = _files.ToList();
        var moving = ordered.Where(entry => selected.Contains(entry.FullPath)).ToList();
        var beforeTarget = ordered.Take(Math.Clamp(targetIndex, 0, ordered.Count))
            .Count(entry => selected.Contains(entry.FullPath));
        ordered.RemoveAll(entry => selected.Contains(entry.FullPath));
        targetIndex = Math.Clamp(targetIndex - beforeTarget, 0, ordered.Count);
        ordered.InsertRange(targetIndex, moving);
        Definition.SortProperty = LayoutSortProperty.Manual;
        Definition.SortDescending = false;
        ApplyEntryOrder(ordered);
        PersistCurrentOrder();
        Definition.StoreActiveTab();
        UpdateSortHeaders();
        _internalReorderCompleted = true;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyEntryOrder(IReadOnlyList<FileEntry> ordered)
    {
        for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var entry = ordered[targetIndex];
            var currentIndex = _files.IndexOf(entry);
            if (currentIndex < 0) _files.Insert(targetIndex, entry);
            else if (currentIndex != targetIndex) _files.Move(currentIndex, targetIndex);
        }
    }

    private int GetDropIndex(Point position)
    {
        var element = FileList.InputHitTest(position) as DependencyObject;
        if (element is null) return _files.Count;
        var container = ItemsControl.ContainerFromElement(FileList, element) as ListBoxItem;
        return container is null ? _files.Count : FileList.ItemContainerGenerator.IndexFromContainer(container);
    }

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) &&
            TryGetViewModeShortcut(e.Key, out var viewMode))
        {
            SetViewMode(viewMode);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            FileList.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && FocusedSelectedEntry() is { } openEntry)
        {
            OpenPath(openEntry.FullPath);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F2 && SelectedEntries() is [var entry])
        {
            StartInlineRename(entry);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space)
        {
            var previewEntry = FilePreviewService.SelectPreviewEntry(SelectedEntries(), _focusedEntryPath);
            if (previewEntry is null) return;
            _ = _filePreviewService.TryPreviewAsync(previewEntry.FullPath);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F2 && SelectedEntries().Length == 0)
        {
            StartLayoutInlineRename();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Delete) return;
        ContextDelete_Click(sender, e);
        e.Handled = true;
    }

    private static bool TryGetViewModeShortcut(Key key, out LayoutViewMode mode)
    {
        mode = key switch
        {
            Key.D1 or Key.NumPad1 => LayoutViewMode.ExtraLargeIcons,
            Key.D2 or Key.NumPad2 => LayoutViewMode.LargeIcons,
            Key.D3 or Key.NumPad3 => LayoutViewMode.MediumIcons,
            Key.D4 or Key.NumPad4 => LayoutViewMode.SmallIcons,
            Key.D5 or Key.NumPad5 => LayoutViewMode.List,
            Key.D6 or Key.NumPad6 => LayoutViewMode.Details,
            Key.D7 or Key.NumPad7 => LayoutViewMode.Tiles,
            Key.D8 or Key.NumPad8 => LayoutViewMode.Content,
            _ => default
        };
        return key is >= Key.D1 and <= Key.D8 or >= Key.NumPad1 and <= Key.NumPad8;
    }

    private FileEntry[] SelectedEntries() => FileList.SelectedItems.OfType<FileEntry>().ToArray();

    private FileEntry? FocusedSelectedEntry()
    {
        var selected = SelectedEntries();
        return selected.FirstOrDefault(entry => string.Equals(
                   entry.FullPath, _focusedEntryPath, StringComparison.OrdinalIgnoreCase))
               ?? selected.FirstOrDefault();
    }

    private void UpdateBoxSelection(Point current)
    {
        var rectangle = new Rect(
            Math.Min(_boxSelectionStart.X, current.X),
            Math.Min(_boxSelectionStart.Y, current.Y),
            Math.Abs(_boxSelectionStart.X - current.X),
            Math.Abs(_boxSelectionStart.Y - current.Y));
        IconSelectionRectangle.Visibility = Visibility.Visible;
        IconSelectionRectangle.Margin = new Thickness(rectangle.X, rectangle.Y, 0, 0);
        IconSelectionRectangle.Width = rectangle.Width;
        IconSelectionRectangle.Height = rectangle.Height;
        foreach (var item in _files)
        {
            var container = FileList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
            if (container is null) continue;
            var bounds = container.TransformToAncestor(FileList)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            container.IsSelected = rectangle.IntersectsWith(bounds);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<FileEntry>().LastOrDefault() is { } added &&
            (string.IsNullOrWhiteSpace(_focusedEntryPath) ||
             !SelectedEntries().Any(entry => string.Equals(entry.FullPath, _focusedEntryPath, StringComparison.OrdinalIgnoreCase))))
        {
            _focusedEntryPath = added.FullPath;
        }
    }

    private void FileList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = ItemsControl.ContainerFromElement(FileList, source) as ListBoxItem;
        if (container is null)
        {
            ShowLayoutMenu(e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (!container.IsSelected)
        {
            FileList.SelectedItems.Clear();
            container.IsSelected = true;
        }

        var paths = SelectedEntries().Select(entry => entry.FullPath).ToArray();
        var host = Window.GetWindow(this);
        if (host is null || paths.Length == 0) return;
        var screen = PointToScreen(e.GetPosition(this));
        var handle = new System.Windows.Interop.WindowInteropHelper(host).Handle;
        LogService.Info($"Item context menu open | group={Definition.Title} | selectedCount={paths.Length}");
        _layoutMenuOpen = true;
        var menuShown = false;
        try
        {
            menuShown = ShellContextMenuService.Show(handle, paths, (int)screen.X, (int)screen.Y,
                paths.Length == 1 ? () => ContextRename_Click(this, new RoutedEventArgs()) : null);
        }
        finally
        {
            _layoutMenuOpen = false;
            ScheduleAutoCollapse();
        }
        if (menuShown)
        {
            LogService.Info($"Item context menu closed | group={Definition.Title}");
            e.Handled = true;
        }
    }

    private void ShowLayoutMenu(Point position)
    {
        var menu = new ContextMenu();
        _layoutMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _layoutMenuOpen = false;
            ScheduleAutoCollapse();
        };
        AddViewMenu(menu);
        menu.Items.Add(new Separator());
        var createMenu = new MenuItem { Header = "新建布局" };
        AddSubMenuItem(createMenu, "普通布局", () => CreateLayoutRequested?.Invoke(GroupKind.Empty));
        AddSubMenuItem(createMenu, "映射布局", () => CreateLayoutRequested?.Invoke(GroupKind.Folder));
        menu.Items.Add(createMenu);
        menu.Items.Add(new Separator());
        if (Definition.Kind == GroupKind.Folder && !string.IsNullOrWhiteSpace(Definition.FolderPath))
        {
            AddBlankMenuItem(menu, "文件夹菜单...", () => ShowMappedFolderShellMenu(position));
            menu.Items.Add(new Separator());
        }

        var autoCollapseItem = new MenuItem
        {
            Header = "自动折叠",
            IsCheckable = true,
            IsChecked = Definition.AutoCollapse
        };
        autoCollapseItem.Click += (_, _) =>
        {
            Definition.AutoCollapse = autoCollapseItem.IsChecked;
            if (!Definition.AutoCollapse && _collapsedByAuto)
            {
                _collapsedByAuto = false;
                SetCollapsed(false, notifyLayoutChanged: false);
            }
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };
        menu.Items.Add(autoCollapseItem);
        menu.Items.Add(new Separator());
        AddBlankMenuItem(menu, Definition.HasMultipleTabs ? "重命名当前页签" : "重命名布局", () => RenameLayoutRequested());
        AddBlankMenuItem(menu, "删除布局", () => RemoveRequested?.Invoke(this, EventArgs.Empty));
        AddBlankMenuItem(menu, "设置", () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        AddBlankMenuItem(menu, "退出 Z-Desk", () => ExitRequested?.Invoke(this, EventArgs.Empty));
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void AddViewMenu(ContextMenu menu)
    {
        var viewMenu = new MenuItem { Header = "查看" };
        foreach (var mode in Enum.GetValues<LayoutViewMode>())
        {
            var capturedMode = mode;
            var item = new MenuItem
            {
                Header = ViewModeLabel(mode),
                InputGestureText = $"Ctrl+Shift+{(int)mode}",
                IsCheckable = true,
                IsChecked = Definition.ViewMode == mode
            };
            item.Click += (_, _) => SetViewMode(capturedMode);
            viewMenu.Items.Add(item);
        }
        menu.Items.Add(viewMenu);
    }

    private void ShowMappedFolderShellMenu(Point position)
    {
        if (string.IsNullOrWhiteSpace(Definition.FolderPath)) return;
        var host = Window.GetWindow(this);
        var handle = host is null ? nint.Zero : new System.Windows.Interop.WindowInteropHelper(host).Handle;
        if (handle == nint.Zero) return;
        var screen = PointToScreen(position);
        _layoutMenuOpen = true;
        try
        {
            ShellContextMenuService.Show(handle, [Definition.FolderPath], (int)screen.X, (int)screen.Y);
        }
        finally
        {
            _layoutMenuOpen = false;
            ScheduleAutoCollapse();
        }
    }

    private void AddBlankMenuItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void AddSubMenuItem(MenuItem parent, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
    }

    private void RenameLayoutRequested()
    {
        Dispatcher.BeginInvoke(StartLayoutInlineRename, DispatcherPriority.Input);
    }

    private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        StartLayoutInlineRename();
        e.Handled = true;
    }

    private void StartLayoutInlineRename()
    {
        if (Definition.HasMultipleTabs &&
            Definition.Tabs.ElementAtOrDefault(Definition.ActiveTabIndex) is { } activeTab)
        {
            StartTabInlineRename(activeTab.Id);
            return;
        }
        TitleText.Visibility = Visibility.Collapsed;
        TitleRenameBox.Text = Definition.Title;
        TitleRenameBox.Visibility = Visibility.Visible;
        TitleRenameBox.Focus();
        TitleRenameBox.SelectAll();
    }

    private void StartTabInlineRename(Guid tabId)
    {
        if (_tabRenameEditor is not null)
        {
            _tabRenameEditor.Focus();
            return;
        }
        var tab = Definition.Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
        var button = TabStrip.Children.OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag is Guid id && id == tabId);
        if (tab is null || button is null) return;
        var tabIndex = TabStrip.Children.IndexOf(button);

        var editor = new TextBox
        {
            Text = tab.Title,
            MinWidth = 0,
            Margin = new Thickness(2, 1, 2, 2),
            Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 11,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(43, 45, 53)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(111, 156, 221)),
            BorderThickness = new Thickness(1)
        };
        _renamingTabId = tabId;
        _tabRenameEditor = editor;
        TabStrip.Children.RemoveAt(tabIndex);
        TabStrip.Children.Insert(tabIndex, editor);
        editor.KeyDown += TabRenameBox_KeyDown;
        editor.LostKeyboardFocus += TabRenameBox_LostKeyboardFocus;
        Dispatcher.BeginInvoke(() =>
        {
            editor.Focus();
            editor.SelectAll();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void TabRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FinishTabInlineRename(commit: false);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        FinishTabInlineRename(commit: true);
        e.Handled = true;
    }

    private void TabRenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _tabRenameEditor)) FinishTabInlineRename(commit: true);
    }

    private void FinishTabInlineRename(bool commit)
    {
        if (_committingTabRename || _renamingTabId is not Guid tabId || _tabRenameEditor is null) return;
        _committingTabRename = true;
        try
        {
            var title = _tabRenameEditor.Text.Trim();
            var tab = Definition.Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            var changed = commit && tab is not null && !string.IsNullOrWhiteSpace(title) &&
                          !string.Equals(tab.Title, title, StringComparison.Ordinal);
            _tabRenameEditor = null;
            _renamingTabId = null;
            if (changed && tab is not null)
            {
                tab.Title = title;
                if (Definition.Tabs.ElementAtOrDefault(Definition.ActiveTabIndex)?.Id == tabId)
                {
                    Definition.Title = title;
                    TitleText.Text = title;
                }
            }
            RenderTabs();
            if (!changed) return;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, $"已重命名页签：{title}");
        }
        finally
        {
            _committingTabRename = false;
        }
    }

    private void TitleRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseLayoutInlineRename();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        CommitLayoutInlineRename();
        e.Handled = true;
    }

    private void TitleRenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TitleRenameBox.Visibility == Visibility.Visible) CommitLayoutInlineRename();
    }

    private void CommitLayoutInlineRename()
    {
        if (_committingLayoutRename) return;
        _committingLayoutRename = true;
        try
        {
            var title = TitleRenameBox.Text.Trim();
            CloseLayoutInlineRename();
            if (string.IsNullOrWhiteSpace(title) || string.Equals(title, Definition.Title, StringComparison.Ordinal)) return;
            Definition.Title = title;
            if (Definition.Tabs.ElementAtOrDefault(Definition.ActiveTabIndex) is { } activeTab) activeTab.Title = title;
            ApplyDefinition();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, $"已重命名布局：{Definition.Title}");
        }
        finally { _committingLayoutRename = false; }
    }

    private void CloseLayoutInlineRename()
    {
        TitleRenameBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntries().FirstOrDefault() is { } entry) OpenPath(entry.FullPath);
    }

    private void ContextCopy_Click(object sender, RoutedEventArgs e) => PutSelectionOnClipboard(cut: false);
    private void ContextCut_Click(object sender, RoutedEventArgs e) => PutSelectionOnClipboard(cut: true);

    private void PutSelectionOnClipboard(bool cut)
    {
        var paths = SelectedEntries().Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        var effect = cut ? DragDropEffects.Move : DragDropEffects.Copy;
        var stream = new MemoryStream(BitConverter.GetBytes((int)effect));
        data.SetData("Preferred DropEffect", stream);
        Clipboard.SetDataObject(data, true);
        StatusChanged?.Invoke(this, cut ? $"已剪切 {paths.Length} 项" : $"已复制 {paths.Length} 项到剪贴板");
    }

    private async void ContextPaste_Click(object sender, RoutedEventArgs e)
    {
        if (Definition.Kind != GroupKind.Folder || Definition.FolderPath is null ||
            !Clipboard.ContainsFileDropList()) return;
        var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
        var move = ClipboardPrefersMove();
        var result = move
            ? await _shellFileOperations.MoveAsync(paths, Definition.FolderPath, GetOwnerHandle())
            : await _shellFileOperations.CopyAsync(paths, Definition.FolderPath, GetOwnerHandle());
        StatusChanged?.Invoke(this, result.Aborted ? "粘贴已取消" :
            result.Succeeded ? $"已{(move ? "移动" : "复制")} {paths.Length} 项" : $"粘贴失败：{result.ErrorMessage}");
        if (result.Succeeded)
        {
            if (move) Clipboard.Clear();
            await ReconcileFolderItemsAsync();
        }
    }

    private void ContextRename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntries() is [var entry]) StartInlineRename(entry);
    }

    private void StartInlineRename(FileEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var container = FileList.ItemContainerGenerator.ContainerFromItem(entry) as ListBoxItem;
            if (container is null) return;
            var editor = FindDescendant<TextBox>(container, "RenameBox");
            var label = FindDescendant<TextBlock>(container, "NameText");
            if (editor is null || label is null) return;
            label.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            editor.Text = entry.Name;
            var selectionLength = entry.IsDirectory ? entry.Name.Length : Path.GetFileNameWithoutExtension(entry.Name).Length;
            editor.Focus();
            editor.Select(0, selectionLength);
        }, DispatcherPriority.ApplicationIdle);
    }

    private async void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor) return;
        if (e.Key == Key.Escape)
        {
            CloseInlineRename(editor);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        await CommitInlineRenameAsync(editor);
        e.Handled = true;
    }

    private async void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor && editor.Visibility == Visibility.Visible)
            await CommitInlineRenameAsync(editor);
    }

    private async Task CommitInlineRenameAsync(TextBox editor)
    {
        if (_committingInlineRename || editor.DataContext is not FileEntry entry) return;
        _committingInlineRename = true;
        try
        {
            var newName = editor.Text.Trim();
            CloseInlineRename(editor);
            if (!string.Equals(newName, entry.Name, StringComparison.Ordinal))
                await RenameEntryAsync(entry, newName);
        }
        finally { _committingInlineRename = false; }
    }

    private void CloseInlineRename(TextBox editor)
    {
        editor.Visibility = Visibility.Collapsed;
        var container = FileList.ItemContainerGenerator.ContainerFromItem(editor.DataContext) as ListBoxItem;
        if (container is not null && FindDescendant<TextBlock>(container, "NameText") is { } label)
            label.Visibility = Visibility.Visible;
    }

    private async Task RenameEntryAsync(FileEntry entry, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        var extension = Path.GetExtension(entry.FullPath);
        if ((extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".url", StringComparison.OrdinalIgnoreCase)) &&
            !newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            newName += extension;
        }
        var result = await _shellFileOperations.RenameAsync(entry.FullPath, newName, GetOwnerHandle());
        if (result.Succeeded)
        {
            var destination = result.ResultPaths?.FirstOrDefault() ??
                              Path.Combine(Path.GetDirectoryName(entry.FullPath)!, newName);
            for (var index = 0; index < Definition.PinnedPaths.Count; index++)
            {
                if (string.Equals(Definition.PinnedPaths[index], entry.FullPath, StringComparison.OrdinalIgnoreCase))
                    Definition.PinnedPaths[index] = destination;
            }
            for (var index = 0; index < Definition.ItemOrder.Count; index++)
            {
                if (string.Equals(Definition.ItemOrder[index], entry.FullPath, StringComparison.OrdinalIgnoreCase))
                    Definition.ItemOrder[index] = destination;
            }
            var itemIndex = _files.IndexOf(entry);
            if (itemIndex >= 0)
            {
                var replacement = CreateEntry(destination);
                _files[itemIndex] = replacement;
                FileList.SelectedItem = replacement;
                _focusedEntryPath = destination;
            }
            Definition.StoreActiveTab();
            RefreshFolderSignature();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (!result.Aborted)
            MessageBox.Show(result.ErrorMessage ?? "Windows Shell 未能完成重命名。", "重命名失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal)) return match;
            if (FindDescendant<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private async void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        var entries = SelectedEntries();
        if (entries.Length == 0) return;
        var result = await _shellFileOperations.DeleteAsync(
            entries.Select(entry => entry.FullPath).ToArray(), GetOwnerHandle());
        if (!result.Succeeded) return;
        var removed = entries.Select(entry => entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            Definition.PinnedPaths.RemoveAll(path => string.Equals(path, entry.FullPath, StringComparison.OrdinalIgnoreCase));
        }
        Definition.ItemOrder.RemoveAll(path => removed.Contains(path));
        foreach (var entry in entries) _files.Remove(entry);
        if (_focusedEntryPath is not null && removed.Contains(_focusedEntryPath)) _focusedEntryPath = null;
        Definition.StoreActiveTab();
        RefreshFolderSignature();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private nint GetOwnerHandle()
    {
        var owner = Window.GetWindow(this);
        return owner is null ? nint.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
    }

    private static bool ClipboardPrefersMove()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetData("Preferred DropEffect") is MemoryStream stream)
            {
                stream.Position = 0;
                using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                return ((DragDropEffects)reader.ReadInt32()).HasFlag(DragDropEffects.Move);
            }
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
        return false;
    }

    private async void ContextUndo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await OperationHistoryService.Shared.UndoAsync()) LoadFolder();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "撤销失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContextProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntries().FirstOrDefault() is not { } entry) return;
        try { ShellFileService.ShowProperties(entry.FullPath); }
        catch (System.ComponentModel.Win32Exception ex)
        { MessageBox.Show(ex.Message, "无法显示属性", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is FileEntry entry)
        {
            OpenPath(entry.FullPath);
        }
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            ShellFileService.Open(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"无法打开：{ex.Message}", "Z-Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
