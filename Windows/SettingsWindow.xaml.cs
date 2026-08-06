using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using ZDesk.Models;
using ZDesk.Services;

namespace ZDesk.Windows;

public partial class SettingsWindow : Window
{
    private const string LayoutRuleDragFormat = "ZDesk.LayoutRule.v1";
    private LayoutMatchRule? _draggedLayoutRule;
    private Point _layoutRuleDragStart;
    private DataGridRow? _layoutRuleDragRow;
    private int _layoutRuleDragSourceIndex = -1;
    private int _layoutRuleDragTargetIndex = -1;
    private double _layoutRuleDragOriginY;
    private double _layoutRuleDragPointerOffsetY;
    private bool _layoutRuleDropCommitted;
    private readonly Dictionary<Guid, double> _ruleRowPositions = [];
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class LayoutRow
    {
        public GroupDefinition Host { get; }
        public LayoutTab? Tab { get; }
        public Guid LayoutId => Tab?.Id ?? Host.Id;
        public GroupKind LayoutKind => Tab?.Kind ?? Host.Kind;
        public string Title
        {
            get => Tab?.Title ?? Host.Title;
            set
            {
                if (Tab is null) Host.Title = value;
                else
                {
                    Tab.Title = value;
                    if (Host.Tabs.ElementAtOrDefault(Host.ActiveTabIndex)?.Id == Tab.Id) Host.Title = value;
                }
            }
        }
        public string Kind => LayoutKind == GroupKind.Folder ? "映射" : "普通";
        public string Description => LayoutKind == GroupKind.Folder
            ? Tab?.FolderPath ?? Host.FolderPath ?? "未映射"
            : $"{(Tab?.PinnedPaths.Count ?? Host.PinnedPaths.Count)} 个引用";
        public bool IsRuleLocked
        {
            get => Tab?.IsRuleLocked ?? Host.IsRuleLocked;
            set { if (Tab is null) Host.IsRuleLocked = value; else Tab.IsRuleLocked = value; }
        }
        public LayoutRow(GroupDefinition host, LayoutTab? tab = null) { Host = host; Tab = tab; }
    }

    public sealed class LayoutRuleGroupChoice : INotifyPropertyChanged
    {
        public string Id { get; }
        private string _title;
        public string Title
        {
            get => _title;
            set
            {
                if (string.Equals(_title, value, StringComparison.Ordinal)) return;
                _title = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public LayoutRuleGroupChoice(string id, string title)
        {
            Id = id;
            _title = title;
        }
    }

    public sealed record LayoutRuleTypeChoice(LayoutRuleMatchType Value, string Title);

    public sealed class HotKeyTargetChoice
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public bool IsSelected { get; set; }
    }

    public AppSettings Result { get; private set; }
    public List<GroupDefinition> ResultGroups { get; private set; }
    public List<ClassificationRule> ResultRules { get; private set; }
    public ObservableCollection<LayoutMatchRule> ResultLayoutRules { get; private set; }
    public bool RestoreBackupRequested { get; private set; }
    public event Func<AppSettings, List<GroupDefinition>, List<ClassificationRule>, List<LayoutMatchRule>, Task>? ApplyRequested;
    public event Action<AppSettings>? AppearancePreviewChanged;
    public event Action? ExitApplicationRequested;
    public event Func<Task>? ReapplyLayoutRulesRequested;
    private bool _initializing = true;
    private bool _syncingHotKeyEditor;
    private readonly ObservableCollection<LayoutRow> _layoutRows;
    private string _layoutIdentity = string.Empty;
    public ObservableCollection<LayoutRuleGroupChoice> NormalLayoutChoices { get; } = [];
    public IReadOnlyList<LayoutRuleTypeChoice> LayoutRuleTypeChoices { get; } =
    [
        new(LayoutRuleMatchType.Rule, "规则"),
        new(LayoutRuleMatchType.Folder, "文件夹"),
        new(LayoutRuleMatchType.OtherFiles, "其他文件")
    ];
    public ObservableCollection<HotKeyTargetChoice> HotKeyTargetChoices { get; } = [];
    private TopmostHotKeyBinding? SelectedHotKey => HotKeysGrid?.SelectedItem as TopmostHotKeyBinding;

    public SettingsWindow(
        AppSettings settings,
        bool startupEnabled,
        IEnumerable<GroupDefinition> groups,
        IEnumerable<ClassificationRule> rules,
        IEnumerable<LayoutMatchRule> layoutRules)
    {
        InitializeComponent();
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(SettingsValue_Changed));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(SettingsValue_Changed));
        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(SettingsText_Changed));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(SettingsSelection_Changed));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(SettingsRange_Changed));
        Result = Clone(settings);
        ResultGroups = SnapshotService.CloneGroups(groups);
        ResultRules = rules.Select(CloneRule).ToList();
        ResultLayoutRules = new ObservableCollection<LayoutMatchRule>(
            layoutRules.Select(CloneLayoutRule).OrderBy(rule => rule.Priority));
        foreach (var rule in ResultLayoutRules) AttachLayoutRule(rule);
        NormalizeLayoutRulePriorities();
        _layoutRows = new ObservableCollection<LayoutRow>(CreateLayoutRows(ResultGroups));
        LayoutRulesGrid.ItemsSource = ResultLayoutRules;
        HotKeysGrid.ItemsSource = Result.TopmostHotKeys;
        HotKeysGrid.SelectedItem = Result.TopmostHotKeys.FirstOrDefault();
        RefreshNormalLayoutChoices();
        RefreshHotKeyTargets();
        UpdateLockedLayoutSummary();

        DoubleClickCheckBox.IsChecked = settings.DoubleClickHidesGroups;
        RememberHiddenCheckBox.IsChecked = settings.RememberGroupsHidden;
        DisplayProfilesCheckBox.IsChecked = settings.AutoSwitchDisplayLayouts;
        StandardModeRadio.IsChecked = settings.InteractionMode == LayoutInteractionMode.Standard;
        EdgeHideModeRadio.IsChecked = settings.InteractionMode == LayoutInteractionMode.EdgeHide;
        QrRecognitionHotKeyTextBox.Text = Result.QrRecognitionHotKey;
        UpdateHotKeyEditorState();
        AnimationsCheckBox.IsChecked = settings.EnableAnimations;
        OpacitySlider.Value = settings.ContainerOpacity * 100;
        CornerSlider.Value = settings.ContainerCornerRadius;
        IconSizeSlider.Value = settings.IconSize;
        AnimationSpeedSlider.Value = settings.AnimationSpeed;
        AutoRulesCheckBox.IsChecked = settings.AutoRunRules;
        WatchRulesCheckBox.IsChecked = settings.RunRulesOnFolderChanges;
        RuleIntervalTextBox.Text = settings.RuleIntervalMinutes.ToString();
        StartWithWindowsCheckBox.IsChecked = startupEnabled;
        DataDirectoryTextBox.Text = string.IsNullOrWhiteSpace(settings.DataDirectory)
            ? AppDataPathService.DataDirectory : settings.DataDirectory;
        LogDirectoryTextBox.Text = string.IsNullOrWhiteSpace(settings.LogDirectory)
            ? AppDataPathService.LogDirectory : settings.LogDirectory;
        _layoutIdentity = GetLayoutIdentity(ResultGroups);
        UpdateOpacityText();
        RefreshSelectedHotKeyEditor();
        _initializing = false;
        SetSettingsDirty(false);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareForApply(out var result)) return;
        if (!await TryRaiseApplyRequestedAsync(result)) return;
        SetSettingsDirty(false);
        Close();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareForApply(out var result)) return;
        if (!await TryRaiseApplyRequestedAsync(result)) return;
        SetSettingsDirty(false);
        GeneralErrorText.Text = "设置已应用";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task<bool> TryRaiseApplyRequestedAsync(AppSettings result)
    {
        try
        {
            await RaiseApplyRequestedAsync(result);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            GeneralErrorText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "应用设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void SettingsValue_Changed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is RadioButton)
            UpdateHotKeyEditorState();
        if (e.OriginalSource is CheckBox or RadioButton && !IsLayoutRuleEditor(e.OriginalSource))
            MarkSettingsDirty();
    }

    private void UpdateHotKeyEditorState() =>
        HotKeySettingsCard.IsEnabled = StandardModeRadio.IsChecked == true;
    private void SettingsText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLayoutRuleEditor(e.OriginalSource)) MarkSettingsDirty();
    }
    private void SettingsSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is ComboBox && !IsLayoutRuleEditor(e.OriginalSource)) MarkSettingsDirty();
    }

    private static bool IsLayoutRuleEditor(object? source) =>
        source is FrameworkElement { DataContext: LayoutMatchRule };

    private void SettingsRange_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.OriginalSource is Slider) MarkSettingsDirty();
    }

    private void MarkSettingsDirty()
    {
        if (_initializing || _syncingHotKeyEditor) return;
        SetSettingsDirty(true);
    }

    private void SetSettingsDirty(bool dirty)
    {
        if (ApplyButton is not null) ApplyButton.IsEnabled = dirty;
    }

    private void AttachLayoutRule(LayoutMatchRule rule) => rule.PropertyChanged += LayoutRule_PropertyChanged;
    private void DetachLayoutRule(LayoutMatchRule rule) => rule.PropertyChanged -= LayoutRule_PropertyChanged;
    private void LayoutRule_PropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkSettingsDirty();

    private void AddHotKey_Click(object sender, RoutedEventArgs e)
    {
        var binding = new TopmostHotKeyBinding { AllLayouts = true };
        Result.TopmostHotKeys.Add(binding);
        HotKeysGrid.Items.Refresh();
        HotKeysGrid.SelectedItem = binding;
        MarkSettingsDirty();
    }

    private void DeleteHotKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHotKey is not { } binding) return;
        Result.TopmostHotKeys.Remove(binding);
        HotKeysGrid.Items.Refresh();
        HotKeysGrid.SelectedItem = Result.TopmostHotKeys.FirstOrDefault();
        MarkSettingsDirty();
    }

    private void HotKeysGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelectedHotKeyEditor();
    }

    private void RefreshSelectedHotKeyEditor()
    {
        _syncingHotKeyEditor = true;
        try
        {
            var binding = SelectedHotKey;
            if (binding is null)
            {
                HotKeyTextBox.Text = string.Empty;
                HotKeyTextBox.IsEnabled = false;
                HotKeyAllLayoutsCheckBox.IsChecked = false;
                HotKeyAllLayoutsCheckBox.IsEnabled = false;
                HotKeyTargetSelectionPanel.Visibility = Visibility.Collapsed;
                HotKeyTargetChoices.Clear();
                return;
            }

            HotKeyTextBox.IsEnabled = true;
            HotKeyAllLayoutsCheckBox.IsEnabled = true;
            HotKeyTextBox.Text = binding.Gesture;
            HotKeyAllLayoutsCheckBox.IsChecked = binding.AllLayouts;
            HotKeyTargetSelectionPanel.Visibility = binding.AllLayouts ? Visibility.Collapsed : Visibility.Visible;
            HotKeyTargetChoices.Clear();
            var layoutRows = CreateLayoutRows(ResultGroups).ToArray();
            var availableIds = layoutRows.Select(row => row.LayoutId).ToHashSet();
            binding.LayoutIds = binding.LayoutIds.Where(availableIds.Contains).ToList();
            foreach (var row in layoutRows)
                HotKeyTargetChoices.Add(new HotKeyTargetChoice
                {
                    Id = row.LayoutId,
                    Title = row.Title,
                    IsSelected = binding.LayoutIds.Contains(row.LayoutId)
                });
            HotKeyTargetsList.ItemsSource = HotKeyTargetChoices;
        }
        finally
        {
            _syncingHotKeyEditor = false;
        }
    }

    private void RefreshHotKeyTargets() => RefreshSelectedHotKeyEditor();

    private void HotKeyTargetMode_Changed(object sender, RoutedEventArgs e)
    {
        if (SelectedHotKey is not { } binding || _initializing || _syncingHotKeyEditor) return;
        binding.AllLayouts = HotKeyAllLayoutsCheckBox.IsChecked == true;
        HotKeysGrid.Items.Refresh();
        HotKeyTargetSelectionPanel.Visibility = binding.AllLayouts ? Visibility.Collapsed : Visibility.Visible;
        MarkSettingsDirty();
    }

    private void HotKeyTarget_Changed(object sender, RoutedEventArgs e)
    {
        if (SelectedHotKey is not { } binding || binding.AllLayouts || _initializing || _syncingHotKeyEditor) return;
        binding.LayoutIds = HotKeyTargetChoices.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToList();
        MarkSettingsDirty();
    }

    private bool PrepareForApply(out AppSettings result)
    {
        if (!TryBuildResult(out result)) return false;
        LayoutRulesGrid.CommitEdit();
        NormalizeLayoutRulePriorities();
        Result = result;
        return true;
    }

    private async Task RaiseApplyRequestedAsync(AppSettings result)
    {
        if (ApplyRequested is null) return;
        foreach (var handler in ApplyRequested.GetInvocationList().Cast<Func<AppSettings, List<GroupDefinition>, List<ClassificationRule>, List<LayoutMatchRule>, Task>>())
        {
            await handler(result, SnapshotService.CloneGroups(ResultGroups), ResultRules.Select(CloneRule).ToList(), ResultLayoutRules.Select(CloneLayoutRule).ToList());
        }
    }

    private bool TryBuildResult(out AppSettings result)
    {
        result = Result;
        if (SelectedHotKey is { } selected && !selected.AllLayouts)
            selected.LayoutIds = HotKeyTargetChoices.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToList();
        var gestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Result.QrRecognitionHotKey))
        {
            if (!HotKeyParser.TryParse(Result.QrRecognitionHotKey, out var qrGesture, out var error) || qrGesture is null)
            {
                HotKeyErrorText.Text = $"二维码快捷键配置无效：{error}";
                return false;
            }
            gestures.Add(qrGesture.DisplayText);
        }
        foreach (var binding in Result.TopmostHotKeys.Where(binding => binding.Enabled &&
                     (EdgeHideModeRadio.IsChecked != true)))
        {
            if (!HotKeyParser.TryParse(binding.Gesture, out _, out var error) || string.IsNullOrWhiteSpace(binding.Gesture))
            {
                HotKeyErrorText.Text = $"快捷键配置无效：{error}";
                return false;
            }
            if (!binding.AllLayouts && binding.LayoutIds.Count == 0)
            {
                HotKeyErrorText.Text = "每条启用的快捷键至少要选择一个布局。";
                return false;
            }
            if (!HotKeyParser.TryParse(binding.Gesture, out var gesture, out _) || gesture is null || !gestures.Add(gesture.DisplayText))
            {
                HotKeyErrorText.Text = "二维码识别和布局置顶快捷键不能重复。";
                return false;
            }
        }

        if (!int.TryParse(RuleIntervalTextBox.Text, out var interval) || interval is < 1 or > 1440)
        {
            GeneralErrorText.Text = "规则执行间隔必须是 1 到 1440 分钟。";
            return false;
        }

        HotKeyErrorText.Text = string.Empty;
        string dataDirectory;
        string logDirectory;
        try
        {
            dataDirectory = AppDataPathService.Normalize(DataDirectoryTextBox.Text);
            logDirectory = AppDataPathService.Normalize(LogDirectoryTextBox.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            GeneralErrorText.Text = ex.Message;
            return false;
        }
        result = new AppSettings
        {
            DataDirectory = dataDirectory,
            LogDirectory = logDirectory,
            DoubleClickHidesGroups = DoubleClickCheckBox.IsChecked == true,
            StartMaximized = false,
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            StartInDesktopMode = true,
            RememberDesktopMode = true,
            WasInDesktopMode = true,
            RememberGroupsHidden = RememberHiddenCheckBox.IsChecked == true,
            GroupsHidden = Result.GroupsHidden,
            RememberTopmost = false,
            AutoSwitchDisplayLayouts = DisplayProfilesCheckBox.IsChecked == true,
            InteractionMode = EdgeHideModeRadio.IsChecked == true ? LayoutInteractionMode.EdgeHide : LayoutInteractionMode.Standard,
            IsTopmost = false,
            EnableAnimations = AnimationsCheckBox.IsChecked == true,
            ContainerOpacity = OpacitySlider.Value / 100,
            ContainerCornerRadius = CornerSlider.Value,
            IconSize = IconSizeSlider.Value,
            AnimationSpeed = AnimationSpeedSlider.Value,
            AutoRunRules = AutoRulesCheckBox.IsChecked == true,
            RunRulesOnFolderChanges = WatchRulesCheckBox.IsChecked == true,
            RuleIntervalMinutes = interval,
        QrRecognitionHotKey = Result.QrRecognitionHotKey.Trim(),
            QrRecognitionFrameBounds = Result.QrRecognitionFrameBounds,
            TopmostHotKeys = Result.TopmostHotKeys.Select(CloneHotKeyBinding).ToList(),
            TopmostHotKey = null,
        };
        return true;
    }

    private void BrowseDataDirectory_Click(object sender, RoutedEventArgs e) => BrowseDirectory(DataDirectoryTextBox, "选择应用数据目录");
    private void BrowseLogDirectory_Click(object sender, RoutedEventArgs e) => BrowseDirectory(LogDirectoryTextBox, "选择日志目录");

    private void BrowseDirectory(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    public void SynchronizeLayouts(IEnumerable<GroupDefinition> groups)
    {
        var cloned = SnapshotService.CloneGroups(groups);
        var identity = GetLayoutIdentity(cloned);
        if (string.Equals(identity, _layoutIdentity, StringComparison.Ordinal)) return;
        ResultGroups = cloned;
        _layoutIdentity = identity;
        RebuildLayoutRows();
        RefreshNormalLayoutChoices();
        RefreshHotKeyTargets();
        UpdateLockedLayoutSummary();
    }

    private static string GetLayoutIdentity(IEnumerable<GroupDefinition> groups) => string.Join('|', groups.SelectMany(group =>
        group.Tabs.Count == 0
            ? [$"{group.Id:N}:{group.Title}:{group.Kind}"]
            : group.Tabs.Select(tab => $"{tab.Id:N}:{tab.Title}:{tab.Kind}")));

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityText();
        RaiseAppearancePreview();
    }

    private void AppearanceControl_Changed(object sender, RoutedEventArgs e) => RaiseAppearancePreview();

    private void RaiseAppearancePreview()
    {
        if (_initializing) return;
        MarkSettingsDirty();
        var preview = Clone(Result);
        preview.EnableAnimations = AnimationsCheckBox.IsChecked == true;
        preview.ContainerOpacity = OpacitySlider.Value / 100;
        preview.ContainerCornerRadius = CornerSlider.Value;
        preview.IconSize = IconSizeSlider.Value;
        preview.AnimationSpeed = AnimationSpeedSlider.Value;
        AppearancePreviewChanged?.Invoke(preview);
    }

    private void HotKeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!TryRecordHotKey(e, out var gesture)) return;
        HotKeyTextBox.Text = gesture;
        if (SelectedHotKey is { } binding)
        {
            binding.Gesture = HotKeyTextBox.Text;
            HotKeysGrid.Items.Refresh();
        }
        HotKeyErrorText.Text = string.Empty;
        MarkSettingsDirty();
    }

    private void QrRecognitionHotKeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!TryRecordHotKey(e, out var gesture)) return;
        Result.QrRecognitionHotKey = gesture;
        QrRecognitionHotKeyTextBox.Text = gesture;
        HotKeyErrorText.Text = string.Empty;
        MarkSettingsDirty();
    }

    private void ClearQrRecognitionHotKey_Click(object sender, RoutedEventArgs e)
    {
        Result.QrRecognitionHotKey = string.Empty;
        QrRecognitionHotKeyTextBox.Text = string.Empty;
        HotKeyErrorText.Text = string.Empty;
        MarkSettingsDirty();
    }

    private bool TryRecordHotKey(KeyEventArgs e, out string gesture)
    {
        gesture = string.Empty;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;

        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0)
        {
            HotKeyErrorText.Text = "请同时按住至少一个修饰键。";
            return false;
        }

        var keyName = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Space => "Space",
            Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Insert or Key.Delete => key.ToString(),
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(keyName))
        {
            HotKeyErrorText.Text = "这个按键暂不支持录制。";
            return false;
        }
        parts.Add(keyName);
        gesture = string.Join('+', parts);
        return true;
    }

    private void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "恢复上一份布局并关闭设置窗口？\n当前真实文件不会被修改。",
            "恢复布局备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            RestoreBackupRequested = true;
            Close();
        }
    }

    private void ExitApplication_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "退出 Z-Desk 并关闭所有桌面布局？", "退出 Z-Desk",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ExitApplicationRequested?.Invoke();
        Close();
    }

    private void AddFolderLayout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择布局映射文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var group = new GroupDefinition
        {
            Title = Path.GetFileName(dialog.FolderName),
            Kind = GroupKind.Folder,
            FolderPath = dialog.FolderName,
            DesktopX = 40 + _layoutRows.Count * 18,
            DesktopY = 40 + _layoutRows.Count * 18
        };
        ResultGroups.Add(group);
        _layoutRows.Add(new LayoutRow(group));
        MarkSettingsDirty();
        RefreshNormalLayoutChoices();
    }

    private void AddReferenceLayout_Click(object sender, RoutedEventArgs e)
    {
        var group = new GroupDefinition
        {
            Title = "新引用布局",
            Kind = GroupKind.Empty,
            DesktopX = 40 + _layoutRows.Count * 18,
            DesktopY = 40 + _layoutRows.Count * 18
        };
        ResultGroups.Add(group);
        _layoutRows.Add(new LayoutRow(group));
        MarkSettingsDirty();
        RefreshNormalLayoutChoices();
    }

    private void DeleteLayout_Click(object sender, RoutedEventArgs e)
    {
        return;
    }

    private void LockLayouts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LayoutLockWindow(ResultGroups) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        UpdateLockedLayoutSummary();
        MarkSettingsDirty();
    }

    private void UpdateLockedLayoutSummary()
    {
        if (LockedLayoutSummaryText is null) return;
        var count = LayoutLockWindow.CreateOptions(ResultGroups).Count(option => option.IsLocked);
        LockedLayoutSummaryText.Text = $"已锁定 {count} 个";
    }

    private void AddBlankLayoutRule_Click(object sender, RoutedEventArgs e) => AddLayoutRule(string.Empty);

    private void AddLayoutRulePresetMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        AddPresetMenuItem(menu, "应用", "application");
        AddPresetMenuItem(menu, "游戏", "game");
        AddPresetMenuItem(menu, "目录", "folder");
        AddPresetMenuItem(menu, "文档", "document");
        AddPresetMenuItem(menu, "图片", "image");
        AddPresetMenuItem(menu, "压缩包", "archive");
        AddPresetMenuItem(menu, "视频", "video");
        menu.IsOpen = true;
    }

    private void AddPresetMenuItem(ContextMenu menu, string title, string tag)
    {
        var item = new MenuItem { Header = title, Tag = tag };
        item.Click += AddLayoutRulePreset_Click;
        menu.Items.Add(item);
    }

    private void AddLayoutRulePreset_Click(object sender, RoutedEventArgs e) =>
        AddLayoutRule((sender as FrameworkElement)?.Tag as string ?? string.Empty);

    private void AddLayoutRule(string kind)
    {
        var rule = kind switch
        {
            "application" => new LayoutMatchRule { Name = "应用", MatchType = LayoutRuleMatchType.Rule, ApplicationsOnly = true, Extensions = ".exe;.lnk;.url", Priority = 20 },
            "game" => new LayoutMatchRule { Name = "游戏", MatchType = LayoutRuleMatchType.Rule, Extensions = ".exe;.lnk;.url", PathContains = "steam;steamapps;epic;gog;riot;ubisoft;ea app;battle.net", Priority = 25 },
            "folder" => new LayoutMatchRule { Name = "目录", MatchType = LayoutRuleMatchType.Folder, FoldersOnly = true, Priority = 10 },
            "document" => new LayoutMatchRule { Name = "文档", MatchType = LayoutRuleMatchType.Rule, Extensions = ".doc;.docx;.xls;.xlsx;.ppt;.pptx;.pdf;.txt;.md;.rtf", Priority = 40 },
            "image" => new LayoutMatchRule { Name = "图片", MatchType = LayoutRuleMatchType.Rule, Extensions = ".png;.jpg;.jpeg;.gif;.bmp;.webp;.svg;.heic", Priority = 30 },
            "archive" => new LayoutMatchRule { Name = "压缩包", MatchType = LayoutRuleMatchType.Rule, Extensions = ".zip;.7z;.rar;.tar;.gz;.bz2;.xz", Priority = 70 },
            "video" => new LayoutMatchRule { Name = "视频", MatchType = LayoutRuleMatchType.Rule, Extensions = ".mp4;.mkv;.avi;.mov;.wmv;.webm;.flv", Priority = 60 },
            _ => new LayoutMatchRule { Name = "新规则", MatchType = LayoutRuleMatchType.Rule, Priority = 500 }
        };
        rule.GroupId = NormalLayoutChoices.FirstOrDefault()?.Id ?? string.Empty;
        AttachLayoutRule(rule);
        ResultLayoutRules.Add(rule);
        NormalizeLayoutRulePriorities();
        LayoutRulesGrid.SelectedItem = rule;
        LayoutRulesGrid.ScrollIntoView(rule);
        MarkSettingsDirty();
    }

    private void DeleteLayoutRule_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutRulesGrid.SelectedItem is LayoutMatchRule rule)
        {
            DetachLayoutRule(rule);
            ResultLayoutRules.Remove(rule);
            NormalizeLayoutRulePriorities();
            MarkSettingsDirty();
        }
    }

    private void MoveLayoutRuleUp_Click(object sender, RoutedEventArgs e) => MoveSelectedLayoutRule(-1);

    private void MoveLayoutRuleDown_Click(object sender, RoutedEventArgs e) => MoveSelectedLayoutRule(1);

    private void MoveSelectedLayoutRule(int offset)
    {
        if (LayoutRulesGrid.SelectedItem is not LayoutMatchRule rule) return;
        MoveLayoutRule(rule, ResultLayoutRules.IndexOf(rule) + offset);
    }

    private void RuleDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayoutMatchRule rule }) return;
        _draggedLayoutRule = rule;
        _layoutRuleDragStart = e.GetPosition(LayoutRulesGrid);
    }

    private void RuleDragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedLayoutRule is null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(LayoutRulesGrid);
        if (Math.Abs(position.X - _layoutRuleDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _layoutRuleDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var source = _draggedLayoutRule;
        _draggedLayoutRule = null;
        var row = ItemsControl.ContainerFromElement(LayoutRulesGrid, sender as DependencyObject) as DataGridRow;
        if (row is null) return;
        _layoutRuleDragRow = row;
        _layoutRuleDragSourceIndex = ResultLayoutRules.IndexOf(source);
        _layoutRuleDragTargetIndex = _layoutRuleDragSourceIndex;
        _layoutRuleDragOriginY = row.TransformToAncestor(LayoutRulesGrid).Transform(new Point()).Y;
        _layoutRuleDragPointerOffsetY = position.Y - _layoutRuleDragOriginY;
        _layoutRuleDropCommitted = false;
        row.IsHitTestVisible = false;
        Panel.SetZIndex(row, 100);
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(LayoutRuleDragFormat, source), DragDropEffects.Move);
        }
        finally
        {
            if (!_layoutRuleDropCommitted) ResetLayoutRuleDragVisuals();
        }
    }

    private void LayoutRulesGrid_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(LayoutRuleDragFormat) is not LayoutMatchRule source)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(LayoutRulesGrid);
        UpdateDirectLayoutRuleDrag(source, e.OriginalSource as DependencyObject, position);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void LayoutRulesGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(LayoutRuleDragFormat) is not LayoutMatchRule source) return;
        UpdateDirectLayoutRuleDrag(source, e.OriginalSource as DependencyObject, e.GetPosition(LayoutRulesGrid));
        var sourceIndex = ResultLayoutRules.IndexOf(source);
        var targetIndex = Math.Clamp(_layoutRuleDragTargetIndex, 0, ResultLayoutRules.Count - 1);
        ResetLayoutRuleDragVisuals();
        if (sourceIndex >= 0 && sourceIndex != targetIndex)
        {
            ResultLayoutRules.Move(sourceIndex, targetIndex);
            NormalizeLayoutRulePriorities();
            LayoutRulesGrid.SelectedItem = source;
            LayoutRulesGrid.ScrollIntoView(source);
            MarkSettingsDirty();
        }
        _layoutRuleDropCommitted = true;
        e.Handled = true;
    }

    private void UpdateDirectLayoutRuleDrag(LayoutMatchRule source, DependencyObject? originalSource, Point pointer)
    {
        if (_layoutRuleDragRow is null || _layoutRuleDragSourceIndex < 0) return;
        var rowHeight = Math.Max(1, _layoutRuleDragRow.ActualHeight);
        var draggedTop = pointer.Y - _layoutRuleDragPointerOffsetY;
        var draggedCenter = draggedTop + rowHeight / 2;
        var targetIndex = _layoutRuleDragSourceIndex;
        if (draggedCenter > _layoutRuleDragOriginY + rowHeight / 2)
        {
            for (var index = _layoutRuleDragSourceIndex + 1; index < ResultLayoutRules.Count; index++)
            {
                var threshold = _layoutRuleDragOriginY + (index - _layoutRuleDragSourceIndex) * rowHeight + rowHeight / 2;
                if (draggedCenter >= threshold) targetIndex = index;
                else break;
            }
        }
        else
        {
            for (var index = _layoutRuleDragSourceIndex - 1; index >= 0; index--)
            {
                var threshold = _layoutRuleDragOriginY + (index - _layoutRuleDragSourceIndex) * rowHeight + rowHeight / 2;
                if (draggedCenter <= threshold) targetIndex = index;
                else break;
            }
        }
        var targetChanged = targetIndex != _layoutRuleDragTargetIndex;
        _layoutRuleDragTargetIndex = targetIndex;

        var sourceOffset = draggedTop - _layoutRuleDragOriginY;
        var sourceTransform = _layoutRuleDragRow.RenderTransform as TranslateTransform ?? new TranslateTransform();
        _layoutRuleDragRow.RenderTransform = sourceTransform;
        sourceTransform.BeginAnimation(TranslateTransform.YProperty, null);
        sourceTransform.Y = sourceOffset;

        if (!targetChanged) return;
        for (var index = 0; index < ResultLayoutRules.Count; index++)
        {
            if (index == _layoutRuleDragSourceIndex) continue;
            if (LayoutRulesGrid.ItemContainerGenerator.ContainerFromItem(ResultLayoutRules[index]) is not DataGridRow otherRow) continue;
            var offset = 0d;
            if (_layoutRuleDragSourceIndex < _layoutRuleDragTargetIndex && index > _layoutRuleDragSourceIndex && index <= _layoutRuleDragTargetIndex) offset = -rowHeight;
            else if (_layoutRuleDragTargetIndex < _layoutRuleDragSourceIndex && index >= _layoutRuleDragTargetIndex && index < _layoutRuleDragSourceIndex) offset = rowHeight;
            var transform = otherRow.RenderTransform as TranslateTransform ?? new TranslateTransform();
            otherRow.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(transform.Y, offset,
                TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void ResetLayoutRuleDragVisuals()
    {
        foreach (var rule in ResultLayoutRules)
        {
            if (LayoutRulesGrid.ItemContainerGenerator.ContainerFromItem(rule) is not DataGridRow row) continue;
            row.IsHitTestVisible = true;
            Panel.SetZIndex(row, 0);
            if (row.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = 0;
            }
        }
        _layoutRuleDragRow = null;
        _layoutRuleDragSourceIndex = -1;
        _layoutRuleDragTargetIndex = -1;
    }

    private void MoveLayoutRule(LayoutMatchRule rule, int targetIndex)
    {
        var sourceIndex = ResultLayoutRules.IndexOf(rule);
        if (sourceIndex < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, ResultLayoutRules.Count - 1);
        MoveLayoutRuleToInsertionIndex(rule, targetIndex > sourceIndex ? targetIndex + 1 : targetIndex, refresh: true);
    }

    private void MoveLayoutRuleToInsertionIndex(LayoutMatchRule rule, int insertionIndex, bool refresh)
    {
        var sourceIndex = ResultLayoutRules.IndexOf(rule);
        if (sourceIndex < 0 || ResultLayoutRules.Count < 2) return;
        insertionIndex = Math.Clamp(insertionIndex, 0, ResultLayoutRules.Count);
        if (sourceIndex < insertionIndex) insertionIndex--;
        if (sourceIndex == insertionIndex) return;
        CaptureLayoutRuleRowPositions();
        ResultLayoutRules.Move(sourceIndex, insertionIndex);
        NormalizeLayoutRulePriorities();
        MarkSettingsDirty();
        if (refresh) AnimateLayoutRuleRowsFromCapturedPositions();
        LayoutRulesGrid.SelectedItem = rule;
        LayoutRulesGrid.ScrollIntoView(rule);
    }

    private void CaptureLayoutRuleRowPositions()
    {
        _ruleRowPositions.Clear();
        foreach (var rule in ResultLayoutRules)
        {
            if (LayoutRulesGrid.ItemContainerGenerator.ContainerFromItem(rule) is not DataGridRow row) continue;
            _ruleRowPositions[rule.Id] = row.TransformToAncestor(LayoutRulesGrid).Transform(new Point()).Y;
        }

    }

    private void AnimateLayoutRuleRowsFromCapturedPositions()
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var rule in ResultLayoutRules)
            {
                if (!_ruleRowPositions.TryGetValue(rule.Id, out var previousY)) continue;
                if (LayoutRulesGrid.ItemContainerGenerator.ContainerFromItem(rule) is not DataGridRow row) continue;
                var currentY = row.TransformToAncestor(LayoutRulesGrid).Transform(new Point()).Y;
                var offset = previousY - currentY;
                if (Math.Abs(offset) < 0.5) continue;
                var translate = new TranslateTransform(0, offset);
                row.RenderTransform = translate;
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0,
                    TimeSpan.FromMilliseconds(145)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void NormalizeLayoutRulePriorities()
    {
        for (var index = 0; index < ResultLayoutRules.Count; index++)
            ResultLayoutRules[index].Priority = (index + 1) * 10;
    }

    private async void ReapplyLayoutRules_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareForApply(out var result)) return;
        if (!await TryRaiseApplyRequestedAsync(result)) return;
        if (ReapplyLayoutRulesRequested is not null)
        {
            foreach (var handler in ReapplyLayoutRulesRequested.GetInvocationList().Cast<Func<Task>>()) await handler();
        }
        SetSettingsDirty(false);
        GeneralErrorText.Text = "已按当前规则重新归类桌面图标。";
    }

    private void RefreshNormalLayoutChoices()
    {
        var originalTargets = ResultLayoutRules.ToDictionary(
            rule => rule.Id,
            rule => rule.GroupId ?? string.Empty);
        var desired = _layoutRows
            .Where(row => row.LayoutKind == GroupKind.Empty)
            .Select(row => new LayoutRuleGroupChoice(row.LayoutId.ToString(), row.Title))
            .ToList();

        // Keep the collection and choice objects stable where possible. Clearing
        // an ItemsSource with a TwoWay SelectedValue binding temporarily writes
        // empty GroupId values into every rule, which used to break all targets
        // on add/rename. Only remove an item when that layout truly disappeared.
        for (var index = 0; index < desired.Count; index++)
        {
            var wanted = desired[index];
            var existingIndex = NormalLayoutChoices
                .Select((choice, choiceIndex) => (choice, choiceIndex))
                .FirstOrDefault(item => string.Equals(item.choice.Id, wanted.Id, StringComparison.OrdinalIgnoreCase))
                .choiceIndex;
            if (existingIndex < 0 || existingIndex >= NormalLayoutChoices.Count ||
                !string.Equals(NormalLayoutChoices[existingIndex].Id, wanted.Id, StringComparison.OrdinalIgnoreCase))
            {
                NormalLayoutChoices.Insert(index, wanted);
            }
            else
            {
                if (existingIndex != index) NormalLayoutChoices.Move(existingIndex, index);
                NormalLayoutChoices[index].Title = wanted.Title;
            }
        }
        while (NormalLayoutChoices.Count > desired.Count)
            NormalLayoutChoices.RemoveAt(NormalLayoutChoices.Count - 1);

        var validIds = NormalLayoutChoices.Select(choice => choice.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallback = NormalLayoutChoices.FirstOrDefault()?.Id ?? string.Empty;
        var repaired = false;
        foreach (var rule in ResultLayoutRules)
        {
            var original = originalTargets.TryGetValue(rule.Id, out var target)
                ? target
                : rule.GroupId ?? string.Empty;
            var next = validIds.Contains(original) ? original : fallback;
            if (!string.Equals(original, next, StringComparison.OrdinalIgnoreCase)) repaired = true;
            rule.GroupId = next;
        }

        if (repaired)
        {
            MarkSettingsDirty();
        }
    }

    private static IEnumerable<LayoutRow> CreateLayoutRows(IEnumerable<GroupDefinition> groups)
    {
        foreach (var group in groups)
        {
            if (group.Tabs.Count == 0) yield return new LayoutRow(group);
            else foreach (var tab in group.Tabs) yield return new LayoutRow(group, tab);
        }
    }

    private void RebuildLayoutRows()
    {
        _layoutRows.Clear();
        foreach (var row in CreateLayoutRows(ResultGroups)) _layoutRows.Add(row);
        UpdateLockedLayoutSummary();
    }

    private void UpdateOpacityText()
    {
        if (OpacityValueText is not null)
        {
            OpacityValueText.Text = $"{OpacitySlider.Value:0}%";
        }
    }

    private static AppSettings Clone(AppSettings settings) => new()
    {
        DataDirectory = settings.DataDirectory,
        LogDirectory = settings.LogDirectory,
        DoubleClickHidesGroups = settings.DoubleClickHidesGroups,
        StartMaximized = settings.StartMaximized,
        StartWithWindows = settings.StartWithWindows,
        StartInDesktopMode = settings.StartInDesktopMode,
        RememberDesktopMode = settings.RememberDesktopMode,
        WasInDesktopMode = settings.WasInDesktopMode,
        RememberGroupsHidden = settings.RememberGroupsHidden,
        GroupsHidden = settings.GroupsHidden,
        RememberTopmost = false,
        AutoSwitchDisplayLayouts = settings.AutoSwitchDisplayLayouts,
        QrRecognitionHotKey = settings.QrRecognitionHotKey,
        QrRecognitionFrameBounds = settings.QrRecognitionFrameBounds,
        IsTopmost = false,
        EnableAnimations = settings.EnableAnimations,
        ContainerOpacity = settings.ContainerOpacity,
        ContainerCornerRadius = settings.ContainerCornerRadius,
        IconSize = settings.IconSize,
        AnimationSpeed = settings.AnimationSpeed,
        AutoRunRules = settings.AutoRunRules,
        RunRulesOnFolderChanges = settings.RunRulesOnFolderChanges,
        RuleIntervalMinutes = settings.RuleIntervalMinutes,
        TopmostHotKeys = settings.TopmostHotKeys.Select(binding => new TopmostHotKeyBinding
        {
            Id = binding.Id,
            Enabled = binding.Enabled,
            Gesture = binding.Gesture,
            AllLayouts = binding.AllLayouts,
            LayoutIds = [.. binding.LayoutIds]
        }).ToList(),
        InteractionMode = settings.InteractionMode,
        TopmostHotKey = settings.TopmostHotKey
    };

    private static ClassificationRule CloneRule(ClassificationRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Enabled = rule.Enabled,
        SourceFolder = rule.SourceFolder,
        TargetFolder = rule.TargetFolder,
        Extensions = rule.Extensions,
        NameContains = rule.NameContains,
        ExcludeNameContains = rule.ExcludeNameContains,
        MinimumAgeDays = rule.MinimumAgeDays
    };

    private static TopmostHotKeyBinding CloneHotKeyBinding(TopmostHotKeyBinding binding) => new()
    {
        Id = binding.Id,
        Enabled = binding.Enabled,
        Gesture = binding.Gesture,
        AllLayouts = binding.AllLayouts,
        LayoutIds = [.. binding.LayoutIds]
    };

    private static LayoutMatchRule CloneLayoutRule(LayoutMatchRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Enabled = rule.Enabled,
        Priority = rule.Priority,
        GroupId = rule.GroupId,
        Extensions = rule.Extensions,
        PathContains = rule.PathContains,
        FoldersOnly = rule.FoldersOnly,
        ApplicationsOnly = rule.ApplicationsOnly,
        MatchType = rule.MatchType
    };
}
