using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZDesk.Models;

public sealed class AppState
{
    public const int CurrentVersion = 12;

    public int Version { get; set; } = CurrentVersion;
    public AppSettings Settings { get; set; } = new();
    public List<GroupDefinition> Groups { get; set; } = [];
    public List<ClassificationRule> Rules { get; set; } = [];
    public List<LayoutMatchRule> LayoutMatchRules { get; set; } = LayoutMatchRule.CreateDefaults();
    public List<DesktopIconPlacement> DesktopIconPlacements { get; set; } = [];
}

public enum LayoutRuleMatchType
{
    Rule = 0,
    Folder = 1,
    OtherFiles = 2
}

public sealed class LayoutMatchRule : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = "其他文件";
    private bool _enabled = true;
    private int _priority;
    private string _groupId = string.Empty;
    private string _extensions = string.Empty;
    private string _pathContains = string.Empty;
    private bool _foldersOnly;
    private bool _applicationsOnly;
    private LayoutRuleMatchType? _matchType;

    public string Name { get => _name; set => SetField(ref _name, value); }
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }
    public int Priority { get => _priority; set => SetField(ref _priority, value); }
    public string GroupId { get => _groupId; set => SetField(ref _groupId, value); }
    public string Extensions { get => _extensions; set => SetField(ref _extensions, value); }
    public string PathContains { get => _pathContains; set => SetField(ref _pathContains, value); }
    // Kept for backward-compatible deserialization. The settings UI no longer exposes
    // these flags; directory rules come from the directory preset and applications are
    // inferred from their extension list.
    public bool FoldersOnly
    {
        get => _foldersOnly;
        set
        {
            if (!SetField(ref _foldersOnly, value)) return;
            NotifyEditorState();
        }
    }
    public bool ApplicationsOnly
    {
        get => _applicationsOnly;
        set
        {
            if (!SetField(ref _applicationsOnly, value)) return;
            NotifyEditorState();
        }
    }

    /// <summary>Explicit editor type. Null keeps compatibility with older state files.</summary>
    public LayoutRuleMatchType? MatchType
    {
        get => _matchType;
        set
        {
            if (!SetField(ref _matchType, value)) return;
            NotifyEditorState();
        }
    }

    [JsonIgnore]
    public LayoutRuleMatchType EditorMatchType
    {
        get => MatchType ?? (FoldersOnly
            ? LayoutRuleMatchType.Folder
            : (!ApplicationsOnly && string.IsNullOrWhiteSpace(Extensions) && string.IsNullOrWhiteSpace(PathContains)
                ? LayoutRuleMatchType.OtherFiles
                : LayoutRuleMatchType.Rule));
        set
        {
            if (MatchType == value && EditorMatchType == value) return;
            MatchType = value;
            FoldersOnly = value == LayoutRuleMatchType.Folder;
            ApplicationsOnly = false;
            if (value is LayoutRuleMatchType.Folder or LayoutRuleMatchType.OtherFiles)
            {
                Extensions = string.Empty;
                PathContains = string.Empty;
            }
        }
    }

    [JsonIgnore]
    public bool CanEditCriteria => EditorMatchType == LayoutRuleMatchType.Rule;

    [JsonIgnore]
    public string MatchKind => EditorMatchType switch
    {
        LayoutRuleMatchType.Folder => "文件夹",
        LayoutRuleMatchType.OtherFiles => "其他文件",
        _ => "规则"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void NotifyEditorState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditorMatchType)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditCriteria)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchKind)));
    }

    public static List<LayoutMatchRule> CreateDefaults() =>
    [
        new() { Name = "文件夹", MatchType = LayoutRuleMatchType.Folder, FoldersOnly = true, Priority = 10 },
        new() { Name = "应用程序", MatchType = LayoutRuleMatchType.Rule, ApplicationsOnly = true, Extensions = ".exe;.lnk;.url", Priority = 20 },
        new() { Name = "游戏", MatchType = LayoutRuleMatchType.Rule, Extensions = ".exe;.lnk;.url", PathContains = "steam;steamapps;epic;gog;riot;ubisoft;ea app;battle.net", Priority = 25 },
        new() { Name = "图片", MatchType = LayoutRuleMatchType.Rule, Extensions = ".png;.jpg;.jpeg;.gif;.bmp;.webp;.svg", Priority = 30 },
        new() { Name = "文档", MatchType = LayoutRuleMatchType.Rule, Extensions = ".doc;.docx;.xls;.xlsx;.ppt;.pptx;.pdf;.txt;.md", Priority = 40 },
        new() { Name = "音乐", MatchType = LayoutRuleMatchType.Rule, Extensions = ".mp3;.wav;.flac;.m4a;.aac", Priority = 50 },
        new() { Name = "视频", MatchType = LayoutRuleMatchType.Rule, Extensions = ".mp4;.mkv;.avi;.mov;.wmv", Priority = 60 },
        new() { Name = "压缩包", MatchType = LayoutRuleMatchType.Rule, Extensions = ".zip;.7z;.rar;.tar;.gz", Priority = 70 },
        new() { Name = "其他文件", MatchType = LayoutRuleMatchType.OtherFiles, Priority = 1000 }
    ];
}

public sealed class AppSettings
{
    public string DataDirectory { get; set; } = string.Empty;
    public string LogDirectory { get; set; } = string.Empty;
    public bool DoubleClickHidesGroups { get; set; } = true;
    public bool StartMaximized { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartInDesktopMode { get; set; }
    public bool RememberDesktopMode { get; set; } = true;
    public bool WasInDesktopMode { get; set; }
    public bool RememberGroupsHidden { get; set; } = true;
    public bool GroupsHidden { get; set; }
    public bool RememberTopmost { get; set; } = true;
    public bool IsTopmost { get; set; }
    public bool EnableAnimations { get; set; } = true;
    public double ContainerOpacity { get; set; } = 0.92;
    public double ContainerCornerRadius { get; set; } = 11;
    public double IconSize { get; set; } = 88;
    public double AnimationSpeed { get; set; } = 1.0;
    public bool AutoRunRules { get; set; }
    public bool RunRulesOnFolderChanges { get; set; }
    public int RuleIntervalMinutes { get; set; } = 30;
    public bool AutoSwitchDisplayLayouts { get; set; }
    public List<TopmostHotKeyBinding> TopmostHotKeys { get; set; } = [];
    public LayoutInteractionMode InteractionMode { get; set; } = LayoutInteractionMode.Standard;
    // Legacy field is read during migration and cleared before the next save.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TopmostHotKey { get; set; }
}

public sealed class TopmostHotKeyBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Gesture { get; set; } = string.Empty;
    public bool AllLayouts { get; set; }
    public List<Guid> LayoutIds { get; set; } = [];
}

public enum LayoutInteractionMode
{
    Standard,
    EdgeHide
}

public enum DockEdge
{
    None,
    Left,
    Right,
    Top
}

public sealed class GroupDefinition
{
    public const double DefaultWidth = 720;
    public const double DefaultHeight = 440;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "新分组";
    public GroupKind Kind { get; set; } = GroupKind.Empty;
    public string? FolderPath { get; set; }
    public double X { get; set; } = 32;
    public double Y { get; set; } = 32;
    public double Width { get; set; } = DefaultWidth;
    public double Height { get; set; } = DefaultHeight;
    public bool IsCollapsed { get; set; }
    public bool AutoCollapse { get; set; }
    public List<string> PinnedPaths { get; set; } = [];
    public List<string> ItemOrder { get; set; } = [];
    public LayoutViewMode ViewMode { get; set; } = LayoutViewMode.MediumIcons;
    public LayoutSortProperty SortProperty { get; set; } = LayoutSortProperty.Manual;
    public bool SortDescending { get; set; }
    public bool IsRuleLocked { get; set; }
    public double? DesktopX { get; set; }
    public double? DesktopY { get; set; }
    public string? DisplayDeviceName { get; set; }
    public DockEdge DockEdge { get; set; }
    public List<LayoutTab> Tabs { get; set; } = [];
    public int ActiveTabIndex { get; set; }

    public bool HasMultipleTabs => Tabs.Count > 1;

    public void EnsureTabs()
    {
        if (Tabs.Count > 0) return;
        var originalLayoutId = Id;
        Tabs.Add(CaptureCurrentTab(originalLayoutId));
        // Once pages exist this definition is only the desktop host/container.
        Id = Guid.NewGuid();
        ActiveTabIndex = 0;
    }

    public void StoreActiveTab()
    {
        if (Tabs.Count == 0) return;
        ActiveTabIndex = Math.Clamp(ActiveTabIndex, 0, Tabs.Count - 1);
        ApplyCurrentToTab(Tabs[ActiveTabIndex]);
    }

    public void ActivateTab(int index)
    {
        if (Tabs.Count == 0) return;
        StoreActiveTab();
        ActiveTabIndex = Math.Clamp(index, 0, Tabs.Count - 1);
        ApplyTabToCurrent(Tabs[ActiveTabIndex]);
    }

    public void ReloadActiveTab()
    {
        if (Tabs.Count == 0) return;
        ActiveTabIndex = Math.Clamp(ActiveTabIndex, 0, Tabs.Count - 1);
        ApplyTabToCurrent(Tabs[ActiveTabIndex]);
    }

    public void AddTab(LayoutTab tab, bool activate = true)
    {
        EnsureTabs();
        InsertTab(tab, Tabs.Count, activate);
    }

    public void InsertTab(LayoutTab tab, int index, bool activate = true)
    {
        EnsureTabs();
        StoreActiveTab();
        index = Math.Clamp(index, 0, Tabs.Count);
        var previousActiveIndex = ActiveTabIndex;
        Tabs.Insert(index, tab.Clone());
        // The current page data still belongs to the same pre-insert tab.
        // Shift its index before ActivateTab stores it again, otherwise inserting
        // before the active tab overwrites the inserted page (A/B -> A/A).
        ActiveTabIndex = index <= previousActiveIndex ? previousActiveIndex + 1 : previousActiveIndex;
        if (activate) ActivateTab(index);
    }

    public LayoutTab RemoveTab(Guid tabId)
    {
        EnsureTabs();
        StoreActiveTab();
        var index = Tabs.FindIndex(tab => tab.Id == tabId);
        if (index < 0) throw new InvalidOperationException("The requested layout tab does not exist.");
        var removed = Tabs[index].Clone();
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0) throw new InvalidOperationException("A tabbed layout must retain at least one page.");

        ActiveTabIndex = Math.Clamp(index, 0, Tabs.Count - 1);
        ApplyTabToCurrent(Tabs[ActiveTabIndex]);
        if (Tabs.Count == 1)
        {
            // A single remaining page is an ordinary layout again; its data lives on the group.
            Id = Tabs[0].Id;
            Tabs.Clear();
            ActiveTabIndex = 0;
        }
        return removed;
    }

    public IReadOnlyList<LayoutTab> ExportTabs()
    {
        if (Tabs.Count == 0) return [CaptureCurrentTab(Id)];
        StoreActiveTab();
        return Tabs.Select(tab => tab.Clone()).ToArray();
    }

    public static GroupDefinition FromTab(LayoutTab tab) => new()
    {
        Id = tab.Id,
        Title = tab.Title,
        Kind = tab.Kind,
        FolderPath = tab.FolderPath,
        PinnedPaths = [.. tab.PinnedPaths],
        ItemOrder = [.. tab.ItemOrder],
        ViewMode = tab.ViewMode,
        SortProperty = tab.SortProperty,
        SortDescending = tab.SortDescending,
        IsRuleLocked = tab.IsRuleLocked
    };

    private LayoutTab CaptureCurrentTab(Guid layoutId) => new()
    {
        Id = layoutId,
        Title = Title,
        Kind = Kind,
        FolderPath = FolderPath,
        PinnedPaths = [.. PinnedPaths],
        ItemOrder = [.. ItemOrder],
        ViewMode = ViewMode,
        SortProperty = SortProperty,
        SortDescending = SortDescending,
        IsRuleLocked = IsRuleLocked
    };

    private void ApplyCurrentToTab(LayoutTab tab)
    {
        tab.Title = Title;
        tab.Kind = Kind;
        tab.FolderPath = FolderPath;
        tab.PinnedPaths = [.. PinnedPaths];
        tab.ItemOrder = [.. ItemOrder];
        tab.ViewMode = ViewMode;
        tab.SortProperty = SortProperty;
        tab.SortDescending = SortDescending;
        tab.IsRuleLocked = IsRuleLocked;
    }

    private void ApplyTabToCurrent(LayoutTab tab)
    {
        Title = tab.Title;
        Kind = tab.Kind;
        FolderPath = tab.FolderPath;
        PinnedPaths = [.. tab.PinnedPaths];
        ItemOrder = [.. tab.ItemOrder];
        ViewMode = tab.ViewMode;
        SortProperty = tab.SortProperty;
        SortDescending = tab.SortDescending;
        IsRuleLocked = tab.IsRuleLocked;
    }
}

public sealed class LayoutTab
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "页签";
    public GroupKind Kind { get; set; } = GroupKind.Empty;
    public string? FolderPath { get; set; }
    public List<string> PinnedPaths { get; set; } = [];
    public List<string> ItemOrder { get; set; } = [];
    public LayoutViewMode ViewMode { get; set; } = LayoutViewMode.MediumIcons;
    public LayoutSortProperty SortProperty { get; set; } = LayoutSortProperty.Manual;
    public bool SortDescending { get; set; }
    public bool IsRuleManaged { get; set; }
    public bool IsRuleLocked { get; set; }

    public LayoutTab Clone() => new()
    {
        Id = Id,
        Title = Title,
        Kind = Kind,
        FolderPath = FolderPath,
        PinnedPaths = [.. PinnedPaths],
        ItemOrder = [.. ItemOrder],
        ViewMode = ViewMode,
        SortProperty = SortProperty,
        SortDescending = SortDescending,
        IsRuleManaged = IsRuleManaged,
        IsRuleLocked = IsRuleLocked
    };
}

public sealed record LayoutTabDragPayload(LayoutTab Tab, int OriginalIndex, bool AutoCollapse);

public enum GroupKind
{
    Empty,
    Folder
}

public enum LayoutViewMode
{
    ExtraLargeIcons = 1,
    LargeIcons = 2,
    MediumIcons = 3,
    SmallIcons = 4,
    List = 5,
    Details = 6,
    Tiles = 7,
    Content = 8
}

public enum LayoutSortProperty
{
    Manual,
    Name,
    Modified,
    Type,
    Size
}

public sealed class ClassificationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新规则";
    public bool Enabled { get; set; } = true;
    public string SourceFolder { get; set; } = string.Empty;
    public string TargetFolder { get; set; } = string.Empty;
    public string Extensions { get; set; } = string.Empty;
    public string NameContains { get; set; } = string.Empty;
    public int MinimumAgeDays { get; set; }
    public string ExcludeNameContains { get; set; } = string.Empty;
}

public sealed class DesktopIconPlacement
{
    public string Path { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public string? DisplayDeviceName { get; set; }
}
