using System.Text.Json;
using System.IO;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class LayoutStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string StateDirectory { get; private set; }

    public string StateFile => Path.Combine(StateDirectory, "layout.json");
    public string BackupFile => Path.Combine(StateDirectory, "layout.backup.json");

    public LayoutStore(string? stateDirectory = null)
    {
        StateDirectory = stateDirectory ?? AppDataPathService.DataDirectory;
    }

    public bool HasState => File.Exists(StateFile);
    public void SetStateDirectory(string directory) => StateDirectory = AppDataPathService.Normalize(directory);

    public async Task<AppState> LoadAsync()
    {
        if (!File.Exists(StateFile))
        {
            return CreateDefaultState();
        }

        try
        {
            await using var stream = File.OpenRead(StateFile);
            var state = await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions);
            return Validate(state ?? CreateDefaultState());
        }
        catch (JsonException)
        {
            var backup = StateFile + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(StateFile, backup, overwrite: false);
            if (File.Exists(BackupFile))
            {
                try
                {
                    await using var backupStream = File.OpenRead(BackupFile);
                    var recovered = await JsonSerializer.DeserializeAsync<AppState>(backupStream, JsonOptions);
                    if (recovered is not null)
                    {
                        return Validate(recovered);
                    }
                }
                catch (JsonException)
                {
                    // Both copies are invalid; create a safe default layout.
                }
            }

            return CreateDefaultState();
        }
    }

    public async Task SaveAsync(AppState state)
    {
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var tempFile = StateFile + ".tmp";

            await using (var stream = File.Create(tempFile))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
            }

            if (File.Exists(StateFile))
            {
                File.Copy(StateFile, BackupFile, overwrite: true);
            }

            File.Move(tempFile, StateFile, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task<AppState?> LoadBackupAsync()
    {
        if (!File.Exists(BackupFile))
        {
            return null;
        }

        await using var stream = File.OpenRead(BackupFile);
        var state = await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions);
        return state is null ? null : Validate(state);
    }

    public async Task ExportAsync(AppState state, string destination)
    {
        state.Version = AppState.CurrentVersion;
        await using var stream = File.Create(destination);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
    }

    public async Task<AppState> ImportAsync(string source)
    {
        await using var stream = File.OpenRead(source);
        var state = await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions)
            ?? throw new InvalidDataException("配置包中没有可读取的布局数据。");

        if (state.Version > AppState.CurrentVersion)
        {
            throw new InvalidDataException($"此配置由更新版本的 Z-Desk 创建（版本 {state.Version}）。");
        }

        return Validate(state);
    }

    private static AppState Validate(AppState state)
    {
        var migrateLegacyTabbedHosts = state.Version < 7;
        state.Settings ??= new AppSettings();
        state.Settings.DataDirectory = AppDataPathService.DataDirectory;
        state.Settings.LogDirectory = AppDataPathService.LogDirectory;
        state.Groups ??= [];
        state.Rules ??= [];
        state.LayoutMatchRules ??= LayoutMatchRule.CreateDefaults();
        state.DesktopIconPlacements ??= [];
        state.Settings.TopmostHotKeys ??= [];
        if (state.Settings.TopmostHotKeys.Count == 0 && !string.IsNullOrWhiteSpace(state.Settings.TopmostHotKey))
        {
            state.Settings.TopmostHotKeys.Add(new TopmostHotKeyBinding
            {
                Gesture = state.Settings.TopmostHotKey.Trim(),
                AllLayouts = true
            });
        }
        state.Settings.TopmostHotKey = null;
        state.Settings.ContainerOpacity = Math.Clamp(state.Settings.ContainerOpacity, 0.55, 1.0);
        state.Settings.ContainerCornerRadius = Math.Clamp(state.Settings.ContainerCornerRadius, 0, 24);
        state.Settings.IconSize = Math.Clamp(state.Settings.IconSize, 68, 112);
        state.Settings.AnimationSpeed = Math.Clamp(state.Settings.AnimationSpeed, 0.5, 2.0);
        state.Settings.RuleIntervalMinutes = Math.Clamp(state.Settings.RuleIntervalMinutes, 1, 1440);
        if (!Enum.IsDefined(state.Settings.InteractionMode)) state.Settings.InteractionMode = LayoutInteractionMode.Standard;
        foreach (var binding in state.Settings.TopmostHotKeys)
        {
            binding.Gesture ??= string.Empty;
            binding.LayoutIds ??= [];
        }

        foreach (var group in state.Groups)
        {
            group.Tabs ??= [];
            foreach (var tab in group.Tabs)
            {
                tab.Title = string.IsNullOrWhiteSpace(tab.Title) ? "未命名页签" : tab.Title.Trim();
                tab.PinnedPaths ??= [];
                tab.ItemOrder ??= [];
                if (!Enum.IsDefined(tab.ViewMode)) tab.ViewMode = LayoutViewMode.MediumIcons;
                if (!Enum.IsDefined(tab.SortProperty)) tab.SortProperty = LayoutSortProperty.Manual;
            }
            if (migrateLegacyTabbedHosts && group.Tabs.Count > 0 && group.Tabs.All(tab => tab.Id != group.Id))
            {
                group.Tabs[0].Id = group.Id;
                group.Id = Guid.NewGuid();
            }
            group.ActiveTabIndex = group.Tabs.Count == 0 ? 0 : Math.Clamp(group.ActiveTabIndex, 0, group.Tabs.Count - 1);
            group.Title = string.IsNullOrWhiteSpace(group.Title) ? "未命名分组" : group.Title.Trim();
            group.Width = Math.Clamp(group.Width, 220, 1600);
            group.Height = Math.Clamp(group.Height, 80, 1200);
            group.PinnedPaths ??= [];
            group.ItemOrder ??= [];
            if (!Enum.IsDefined(group.DockEdge)) group.DockEdge = DockEdge.None;
            if (!Enum.IsDefined(group.ViewMode)) group.ViewMode = LayoutViewMode.MediumIcons;
            if (!Enum.IsDefined(group.SortProperty)) group.SortProperty = LayoutSortProperty.Manual;
            LayoutItemStateService.Normalize(group);
        }

        foreach (var rule in state.Rules)
        {
            rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "未命名规则" : rule.Name.Trim();
            rule.Extensions ??= string.Empty;
            rule.NameContains ??= string.Empty;
            rule.ExcludeNameContains ??= string.Empty;
            rule.MinimumAgeDays = Math.Clamp(rule.MinimumAgeDays, 0, 36500);
        }

        foreach (var rule in state.LayoutMatchRules)
        {
            rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "未命名规则" : rule.Name.Trim();
            rule.Extensions ??= string.Empty;
            rule.PathContains ??= string.Empty;
            if (rule.ApplicationsOnly && string.IsNullOrWhiteSpace(rule.Extensions))
                rule.Extensions = ".exe;.lnk;.url";
        }

        state.Version = AppState.CurrentVersion;
        return state;
    }

    private static AppState CreateDefaultState()
    {
        var rules = LayoutMatchRule.CreateDefaults();
        var displayOrder = new[] { "文件夹", "音乐", "应用程序", "游戏", "图片", "视频", "压缩包", "文档", "其他文件" };
        var tabs = rules.OrderBy(rule =>
        {
            var index = Array.IndexOf(displayOrder, rule.Name);
            return index < 0 ? int.MaxValue : index;
        }).Select(rule =>
        {
            var tab = new LayoutTab { Title = rule.Name, Kind = GroupKind.Empty };
            rule.GroupId = tab.Id.ToString();
            return tab;
        }).ToList();
        var host = new GroupDefinition
        {
            Title = tabs[0].Title,
            X = 18,
            Y = 18,
            Width = 1280,
            Height = 640,
            Tabs = tabs,
            ActiveTabIndex = 0
        };
        host.ReloadActiveTab();
        return new AppState
        {
            Settings = new AppSettings
            {
                DataDirectory = AppDataPathService.DataDirectory,
                LogDirectory = AppDataPathService.LogDirectory,
                RunRulesOnFolderChanges = true,
                TopmostHotKeys =
                [
                    new TopmostHotKeyBinding { Gesture = "Ctrl+Alt+T", AllLayouts = true }
                ]
            },
            Groups = [host],
            LayoutMatchRules = rules
        };
    }
}
