using ZDesk.Controls;
using ZDesk.Models;
using ZDesk.Services;
using ZDesk.Windows;

var testRoot = Path.Combine(Path.GetTempPath(), $"ZDesk-smoke-{Guid.NewGuid():N}");
var normalizedTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
var normalizedTestRoot = Path.GetFullPath(testRoot);

if (!normalizedTestRoot.StartsWith(normalizedTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Smoke test directory escaped the system temporary directory.");
}

try
{
    await TestFileTransfersAsync(normalizedTestRoot);
    await TestShellFileOperationsAsync(normalizedTestRoot);
    await TestPreviewProvidersAsync(normalizedTestRoot);
    TestDesktopSelectionBoundary(normalizedTestRoot);
    TestLayoutRuleNotifications();
    TestHotKeyParser();
    TestEdgeDockGeometry();
    await TestRulesAsync(normalizedTestRoot);
    await TestLayoutPathMatchingAsync(normalizedTestRoot);
    TestLockedLayoutAssignment(normalizedTestRoot);
    TestLockedLayoutOptions();
    await TestLayoutItemStateRepairAsync(normalizedTestRoot);
    await TestHotKeyAndDockPersistenceAsync(normalizedTestRoot);
    await TestLayoutBackupAsync(normalizedTestRoot);
    await TestFirstRunPresetsAsync(normalizedTestRoot);
    await TestStorageMigrationAsync(normalizedTestRoot);
    await TestLayoutViewModesAsync(normalizedTestRoot);
    await TestTransferCancellationAsync(normalizedTestRoot);
    await TestConflictStrategiesAsync(normalizedTestRoot);
    await TestRulePerformanceAsync(normalizedTestRoot);
    await TestDiagnosticsAsync(normalizedTestRoot);
    TestUpdateManifestComparison();
    TestUpdateRollbackPreparation(normalizedTestRoot);
    TestShellIcons(normalizedTestRoot);
    TestDesktopWindowStyle(normalizedTestRoot);
    TestIncrementalLayoutItemSync(normalizedTestRoot);
    TestVirtualizingWrapPanel();
    Console.WriteLine("All Z-Desk smoke tests passed.");
}
finally
{
    if (Directory.Exists(normalizedTestRoot))
    {
        Directory.Delete(normalizedTestRoot, recursive: true);
    }
}

static async Task TestFileTransfersAsync(string root)
{
    var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    var destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
    await File.WriteAllTextAsync(Path.Combine(source, "alpha.txt"), "alpha");

    var sourceFolder = Directory.CreateDirectory(Path.Combine(source, "folder")).FullName;
    await File.WriteAllTextAsync(Path.Combine(sourceFolder, "nested.txt"), "nested");

    var service = new FileTransferService();
    var copy = await service.ExecuteAsync(
        [Path.Combine(source, "alpha.txt"), sourceFolder],
        destination,
        FileTransferMode.Copy);
    Assert(copy.Succeeded == 2 && !copy.HasIssues, "copy result");
    Assert(File.Exists(Path.Combine(destination, "alpha.txt")), "copied file");
    Assert(File.Exists(Path.Combine(destination, "folder", "nested.txt")), "recursive directory copy");

    var duplicate = await service.ExecuteAsync(
        [Path.Combine(source, "alpha.txt")],
        destination,
        FileTransferMode.Copy);
    Assert(duplicate.Succeeded == 1, "duplicate copy result");
    Assert(File.Exists(Path.Combine(destination, "alpha (2).txt")), "non-destructive duplicate naming");

    var moveSource = Path.Combine(source, "move.txt");
    await File.WriteAllTextAsync(moveSource, "move");
    var move = await service.ExecuteAsync([moveSource], destination, FileTransferMode.Move);
    Assert(move.Succeeded == 1 && !move.HasIssues, "move result");
    Assert(!File.Exists(moveSource), "move removed source");
    Assert(File.Exists(Path.Combine(destination, "move.txt")), "move created destination");

    var sameFolder = await service.ExecuteAsync(
        [Path.Combine(destination, "move.txt")],
        destination,
        FileTransferMode.Copy);
    Assert(sameFolder.Succeeded == 0 && sameFolder.HasIssues, "same-folder transfer rejected");
}

static async Task TestShellFileOperationsAsync(string root)
{
    var source = Directory.CreateDirectory(Path.Combine(root, "shell-source")).FullName;
    var destination = Directory.CreateDirectory(Path.Combine(root, "shell-destination")).FullName;
    var service = new ShellFileOperationService();

    var renameSource = Path.Combine(source, "before.txt");
    await File.WriteAllTextAsync(renameSource, "rename");
    var rename = await service.RenameAsync(renameSource, "after.txt", nint.Zero);
    var renamedPath = Path.Combine(source, "after.txt");
    Assert(rename.Succeeded && !rename.Aborted, "shell rename result");
    Assert(rename.ResultPaths?.Single() == renamedPath && File.Exists(renamedPath), "shell rename returns target path");

    var copySource = Path.Combine(source, "copy.txt");
    await File.WriteAllTextAsync(copySource, "copy");
    var copy = await service.CopyAsync([copySource], destination, nint.Zero);
    Assert(copy.Succeeded && File.Exists(Path.Combine(destination, "copy.txt")), "shell copy operation");

    var moveSource = Path.Combine(source, "move.txt");
    await File.WriteAllTextAsync(moveSource, "move");
    var move = await service.MoveAsync([moveSource], destination, nint.Zero);
    Assert(move.Succeeded && !File.Exists(moveSource) && File.Exists(Path.Combine(destination, "move.txt")),
        "shell move operation");

    var cancelled = new ShellOperationResult(false, true, unchecked((int)0x800704C7));
    Assert(cancelled.Aborted && !cancelled.Succeeded, "shell cancellation result contract");
}

static async Task TestPreviewProvidersAsync(string root)
{
    var previewFile = Path.Combine(root, "preview.txt");
    await File.WriteAllTextAsync(previewFile, "preview");
    QuickLookLaunchTarget? launchedTarget = null;
    string? launchedPath = null;
    var executableTarget = QuickLookLaunchTarget.ForExecutable(Path.Combine(root, "QuickLook.exe"));
    var provider = new QuickLookPreviewProvider(
        () => executableTarget,
        (target, path) =>
        {
            launchedTarget = target;
            launchedPath = path;
            return true;
        });
    Assert(await new FilePreviewService([provider]).TryPreviewAsync(previewFile), "QuickLook provider launches");
    Assert(launchedTarget == executableTarget && launchedPath == previewFile, "QuickLook receives real path");

    var missing = new QuickLookPreviewProvider(() => null, (_, _) => true);
    Assert(!await missing.TryPreviewAsync(previewFile), "QuickLook missing returns silently");
    var failing = new QuickLookPreviewProvider(
        () => executableTarget,
        (_, _) => throw new System.ComponentModel.Win32Exception(2));
    Assert(!await new FilePreviewService([failing]).TryPreviewAsync(previewFile),
        "QuickLook launch failure returns silently");

    var first = new FileEntry("first.txt", previewFile, false);
    var secondPath = Path.Combine(root, "preview-second.txt");
    await File.WriteAllTextAsync(secondPath, "preview");
    var second = new FileEntry("second.txt", secondPath, false);
    Assert(FilePreviewService.SelectPreviewEntry([first, second], secondPath) == second,
        "preview uses last focused selected item");
    Assert(FilePreviewService.SelectPreviewEntry([first, second], "missing") == first,
        "preview falls back to first selected item");
}

static void TestDesktopSelectionBoundary(string root)
{
    var mapped = Path.Combine(root, "mapped-selection.txt");
    File.WriteAllText(mapped, "mapped");
    Assert(!DesktopShellSelectionService.IsPhysicalDesktopPath(mapped), "mapped path is not physical desktop");
    Assert(!new DesktopShellSelectionService().TrySelect([mapped]),
        "mapped selection does not navigate or synchronize Explorer");
}

static void TestIncrementalLayoutItemSync(string root)
{
    var firstPath = Path.Combine(root, "incremental-first.txt");
    var secondPath = Path.Combine(root, "incremental-second.txt");
    var thirdPath = Path.Combine(root, "incremental-third.txt");
    File.WriteAllText(firstPath, "first");
    File.WriteAllText(secondPath, "second");
    File.WriteAllText(thirdPath, "third");

    Exception? failure = null;
    var thread = new Thread(() =>
    {
        ZDesk.App? app = null;
        var ownsApp = false;
        GroupContainer? container = null;
        try
        {
            app = System.Windows.Application.Current as ZDesk.App;
            if (app is null)
            {
                app = new ZDesk.App();
                app.InitializeComponent();
                ownsApp = true;
            }
            var definition = new GroupDefinition
            {
                Title = "incremental-sync",
                SortProperty = LayoutSortProperty.Manual,
                PinnedPaths = [firstPath, secondPath, thirdPath],
                ItemOrder = [firstPath, secondPath, thirdPath]
            };
            container = new GroupContainer(definition, animationsEnabled: false);
            var entriesField = typeof(GroupContainer).GetField("_files",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var entries = entriesField?.GetValue(container) as System.Collections.ObjectModel.ObservableCollection<FileEntry>;
            if (entries is null || entries.Count != 3)
                throw new InvalidOperationException("incremental sync test could not inspect initial entries");

            var second = entries.Single(entry => entry.FullPath == secondPath);
            var third = entries.Single(entry => entry.FullPath == thirdPath);
            definition.PinnedPaths.Remove(firstPath);
            definition.ItemOrder.Remove(firstPath);
            var synchronize = typeof(GroupContainer).GetMethod("SynchronizePinnedItems",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            synchronize?.Invoke(container, [null]);

            Assert(entries.Count == 2, "incremental sync removes only the moved item");
            Assert(ReferenceEquals(entries[0], second) && ReferenceEquals(entries[1], third),
                "incremental sync preserves unchanged FileEntry instances");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            container?.Dispose();
            if (ownsApp) app?.Shutdown();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Incremental layout item sync test failed.", failure);
}

static void TestVirtualizingWrapPanel()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try { TestVirtualizingWrapPanelCore(); }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Virtualizing wrap panel test failed.", failure);
}

static void TestVirtualizingWrapPanelCore()
{
    var panelFactory = new System.Windows.FrameworkElementFactory(typeof(VirtualizingWrapPanel));
    panelFactory.SetValue(VirtualizingWrapPanel.ItemWidthProperty, 80d);
    panelFactory.SetValue(VirtualizingWrapPanel.ItemHeightProperty, 32d);
    var list = new System.Windows.Controls.ListBox
    {
        Width = 320,
        Height = 160,
        ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(panelFactory),
        ItemsSource = Enumerable.Range(0, 500).ToArray()
    };
    var window = new System.Windows.Window
    {
        Content = list,
        Width = 320,
        Height = 160,
        ShowInTaskbar = false,
        WindowStyle = System.Windows.WindowStyle.None,
        Opacity = 0
    };
    window.Show();
    PumpDispatcher(TimeSpan.FromMilliseconds(120));
    var panel = FindVisualChild<VirtualizingWrapPanel>(list);
    Assert(panel is not null, "virtualizing wrap panel is created");
    var realized = Enumerable.Range(0, 500)
        .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
    Assert(realized < 500 && realized > 0, "virtualizing wrap panel limits realized containers");
    panel!.SetVerticalOffset(10_000);
    PumpDispatcher(TimeSpan.FromMilliseconds(80));
    Assert(list.ItemContainerGenerator.ContainerFromIndex(499) is not null,
        "virtualizing wrap panel realizes the last item after scrolling");
    window.Close();

    var collection = new ResettableObservableCollection<int> { 1, 2, 3 };
    var resets = 0;
    collection.CollectionChanged += (_, args) =>
    {
        if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
    };
    collection.ReplaceAll([3, 2, 1]);
    Assert(resets == 1, "batch collection emits one reset notification");

    using var desktopFiles = new DesktopFileService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
    var refreshes = 0;
    var fullRefresh = false;
    desktopFiles.Changed += (_, args) =>
    {
        refreshes++;
        fullRefresh |= args.RequiresFullRefresh;
    };
    desktopFiles.RequestFullRefresh();
    desktopFiles.RequestFullRefresh();
    PumpDispatcher(TimeSpan.FromMilliseconds(560));
    Assert(refreshes == 1 && fullRefresh, "desktop refresh requests are coalesced");
}

static void TestLayoutRuleNotifications()
{
    var changed = new List<string?>();
    var rule = new LayoutMatchRule
    {
        MatchType = LayoutRuleMatchType.Rule,
        Extensions = ".txt",
        PathContains = "reports"
    };
    var untouched = new LayoutMatchRule { Extensions = ".png", PathContains = "images" };
    rule.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
    rule.EditorMatchType = LayoutRuleMatchType.Folder;
    Assert(rule.Extensions.Length == 0 && rule.PathContains.Length == 0 && !rule.CanEditCriteria,
        "folder type clears only its editable criteria");
    Assert(untouched.Extensions == ".png" && untouched.PathContains == "images",
        "rule type change does not mutate another row");
    Assert(changed.Contains(nameof(LayoutMatchRule.EditorMatchType)) &&
           changed.Contains(nameof(LayoutMatchRule.CanEditCriteria)) &&
           changed.Contains(nameof(LayoutMatchRule.Extensions)) &&
           changed.Contains(nameof(LayoutMatchRule.PathContains)),
        "rule type emits targeted property notifications");
}

static void Assert(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Smoke test failed: {name}");
    }
}

static void TestHotKeyParser()
{
    Assert(
        HotKeyParser.TryParse("control + alt + t", out var standard, out _) &&
        standard?.DisplayText == "Ctrl+Alt+T" &&
        standard.VirtualKey == 'T',
        "standard hotkey parsing");

    Assert(
        HotKeyParser.TryParse("Win+Shift+F10", out var function, out _) &&
        function?.DisplayText == "Shift+Win+F10" &&
        function.VirtualKey == 0x79,
        "function hotkey parsing");

    Assert(!HotKeyParser.TryParse("T", out _, out _), "modifier-less hotkey rejected");
    Assert(!HotKeyParser.TryParse("Ctrl+Alt+T+Y", out _, out _), "multi-key hotkey rejected");
}

static void TestEdgeDockGeometry()
{
    var primary = new System.Drawing.Rectangle(0, 0, 1920, 1040);
    var leftLayout = new System.Drawing.Rectangle(0, 180, 520, 420);
    Assert(EdgeDockGeometry.IsCursorInRevealZone(DockEdge.Left, primary, leftLayout, new System.Drawing.Point(2, 300)),
        "left edge reveal zone");
    Assert(!EdgeDockGeometry.IsCursorInRevealZone(DockEdge.Left, primary, leftLayout, new System.Drawing.Point(2, 700)),
        "left edge rejects cursor outside layout span");

    var rightLayout = new System.Drawing.Rectangle(1400, 120, 520, 420);
    Assert(EdgeDockGeometry.IsCursorInRevealZone(DockEdge.Right, primary, rightLayout, new System.Drawing.Point(1919, 300)),
        "right edge reveal zone");
    Assert(!EdgeDockGeometry.IsCursorInRevealZone(DockEdge.Right, primary, rightLayout, new System.Drawing.Point(1900, 300)),
        "right edge rejects cursor away from edge");

    var secondary = new System.Drawing.Rectangle(-1600, 0, 1600, 900);
    var topLayout = new System.Drawing.Rectangle(-1200, 0, 600, 360);
    Assert(EdgeDockGeometry.IsCursorInRevealZone(DockEdge.Top, secondary, topLayout, new System.Drawing.Point(-900, 1)),
        "top edge supports negative multi-monitor coordinates");
    Assert(!EdgeDockGeometry.IsCursorInRevealZone(DockEdge.Top, secondary, topLayout, new System.Drawing.Point(-400, 1)),
        "top edge rejects cursor outside layout span");
}

static async Task TestRulesAsync(string root)
{
    var source = Directory.CreateDirectory(Path.Combine(root, "rule-source")).FullName;
    var target = Directory.CreateDirectory(Path.Combine(root, "rule-target")).FullName;
    await File.WriteAllTextAsync(Path.Combine(source, "report-final.pdf"), "pdf");
    await File.WriteAllTextAsync(Path.Combine(source, "report-draft.pdf"), "pdf");
    await File.WriteAllTextAsync(Path.Combine(source, "notes.txt"), "txt");
    await File.WriteAllTextAsync(Path.Combine(target, "report-final.pdf"), "existing");

    var rule = new ClassificationRule
    {
        Name = "final PDFs",
        SourceFolder = source,
        TargetFolder = target,
        Extensions = "pdf",
        NameContains = "final"
    };
    var engine = new RuleEngine();
    var preview = engine.Preview([rule]);
    Assert(preview.Count == 1, "rule preview count");
    Assert(Path.GetFileName(preview[0].TargetPath) == "report-final (2).pdf", "rule non-destructive naming");

    var result = await engine.ExecuteAsync(preview);
    Assert(result.Moved == 1 && result.Issues.Count == 0, "rule execution result");
    Assert(File.Exists(Path.Combine(target, "report-final (2).pdf")), "rule target exists");
    Assert(File.Exists(Path.Combine(source, "report-draft.pdf")), "rule non-match remains");
    Assert(File.Exists(Path.Combine(source, "notes.txt")), "rule extension non-match remains");
}

static async Task TestLayoutPathMatchingAsync(string root)
{
    var desktop = Directory.CreateDirectory(Path.Combine(root, "path-desktop")).FullName;
    var url = Path.Combine(desktop, "Steam Game.url");
    await File.WriteAllTextAsync(url, "[InternetShortcut]\nURL=steam://rungameid/123\n");
    var tab = new LayoutTab { Title = "游戏" };
    var host = new GroupDefinition { Tabs = [tab] };
    host.ReloadActiveTab();
    var rule = new LayoutMatchRule
    {
        GroupId = tab.Id.ToString(),
        Extensions = ".lnk;.url",
        PathContains = "steam;epic"
    };
    var assignment = new LayoutAssignmentService().Preview([url], [host], [rule]);
    Assert(assignment.Count == 1 && assignment[0].TabId == tab.Id, "layout rule extension and shortcut path AND match");

    var gameRule = new LayoutMatchRule
    {
        Name = "游戏",
        GroupId = tab.Id.ToString(),
        Extensions = ".exe;.lnk;.url",
        PathContains = "steam;epic;gog"
    };
    Assert(new LayoutAssignmentService().Preview([url], [host], [gameRule]).Count == 1,
        "game preset matches supported launcher path");

    var folder = Directory.CreateDirectory(Path.Combine(desktop, "folder-entry")).FullName;
    var folderRule = new LayoutMatchRule { GroupId = tab.Id.ToString(), MatchType = LayoutRuleMatchType.Folder };
    Assert(new LayoutAssignmentService().Preview([folder, url], [host], [folderRule]).Count == 1,
        "folder match type only matches directories");

    var otherRule = new LayoutMatchRule { GroupId = tab.Id.ToString(), MatchType = LayoutRuleMatchType.OtherFiles };
    Assert(new LayoutAssignmentService().Preview([folder, url], [host], [otherRule]).Count == 1,
        "other-files match type only matches files");

    var mismatch = new LayoutMatchRule
    {
        GroupId = tab.Id.ToString(),
        Extensions = ".lnk",
        PathContains = "steam"
    };
    Assert(new LayoutAssignmentService().Preview([url], [host], [mismatch]).Count == 0, "layout rule extension mismatch");
}

static void TestLockedLayoutAssignment(string root)
{
    var lockedPath = Path.Combine(root, "locked.txt");
    File.WriteAllText(lockedPath, "locked");
    var lockedTab = new LayoutTab { Title = "locked", IsRuleLocked = true, PinnedPaths = [lockedPath] };
    var lockedGroup = new GroupDefinition { Tabs = [lockedTab] };
    var rule = new LayoutMatchRule { GroupId = lockedTab.Id.ToString(), Extensions = ".txt", Priority = 1 };
    var result = new LayoutAssignmentService().Preview([lockedPath], [lockedGroup], [rule]);
    Assert(result.Count == 0, "locked layout is not a rule target");
    var unlockedGroup = new GroupDefinition { Title = "unlocked" };
    var unlockedRule = new LayoutMatchRule { GroupId = unlockedGroup.Id.ToString(), Extensions = ".txt" };
    Assert(new LayoutAssignmentService().Preview([lockedPath], [unlockedGroup], [unlockedRule]).Count == 1,
        "unlocked layout remains a rule target");
    var lockedOrdinary = new GroupDefinition { IsRuleLocked = true };
    var ordinaryRule = new LayoutMatchRule { GroupId = lockedOrdinary.Id.ToString(), Extensions = ".txt" };
    Assert(new LayoutAssignmentService().Preview([lockedPath], [lockedOrdinary], [ordinaryRule]).Count == 0,
        "locked ordinary layout is not a rule target");

    var clone = lockedTab.Clone();
    Assert(clone.IsRuleLocked, "locked tab state cloned");
    var groupClone = GroupDefinition.FromTab(lockedTab);
    Assert(groupClone.IsRuleLocked, "locked tab state preserved when detached");
    var snapshotClone = SnapshotService.CloneGroups([lockedGroup]).Single();
    Assert(snapshotClone.Tabs.Single().IsRuleLocked, "locked tab state preserved in snapshots");
}

static void TestLockedLayoutOptions()
{
    var ordinary = new GroupDefinition { Title = "普通" };
    var folder = new GroupDefinition { Title = "文件夹", Kind = GroupKind.Folder };
    var combo = new GroupDefinition
    {
        Title = "组合",
        Tabs =
        [
            new LayoutTab { Title = "普通页签", Kind = GroupKind.Empty },
            new LayoutTab { Title = "文件夹页签", Kind = GroupKind.Folder }
        ]
    };
    var options = LayoutLockWindow.CreateOptions([ordinary, folder, combo]).ToArray();
    Assert(options.Length == 2, "lock dialog only lists rule-eligible layouts");
    Assert(options.Any(option => option.Title == "普通"), "lock dialog lists ordinary layout");
    Assert(options.Any(option => option.Title == "组合 / 普通页签"), "lock dialog labels combo tab");
    Assert(options.All(option => option.Title != "文件夹" && option.Title != "组合 / 文件夹页签"),
        "lock dialog excludes folder mappings");
}

static async Task TestLayoutItemStateRepairAsync(string root)
{
    var first = Path.Combine(root, "layout-item-first.lnk");
    var second = Path.Combine(root, "layout-item-second.lnk");
    var orphan = Path.Combine(root, "layout-item-orphan.lnk");
    var group = new GroupDefinition
    {
        Title = "inconsistent",
        PinnedPaths = [first, first.ToUpperInvariant(), second],
        ItemOrder = [orphan, first, first.ToUpperInvariant()]
    };

    Assert(LayoutItemStateService.Normalize(group), "inconsistent layout state is repaired");
    Assert(group.PinnedPaths.SequenceEqual([first, second], StringComparer.OrdinalIgnoreCase),
        "layout pinned paths are unique");
    Assert(group.ItemOrder.SequenceEqual([first, second], StringComparer.OrdinalIgnoreCase),
        "layout order removes stale entries and appends missing pins");

    Assert(LayoutItemStateService.RemovePinnedPaths(group.PinnedPaths, group.ItemOrder, [first]),
        "cross-layout source removal updates state");
    Assert(!group.PinnedPaths.Contains(first, StringComparer.OrdinalIgnoreCase) &&
           !group.ItemOrder.Contains(first, StringComparer.OrdinalIgnoreCase),
        "cross-layout source removes ownership and order");
    Assert(LayoutItemStateService.AddPinnedPath(group.PinnedPaths, group.ItemOrder, first),
        "cross-layout target add updates state");
    Assert(group.PinnedPaths.Last() == first && group.ItemOrder.Last() == first,
        "cross-layout target adds ownership and order together");

    var stateDirectory = Path.Combine(root, "layout-item-state-repair");
    var tab = new LayoutTab
    {
        Title = "tab",
        PinnedPaths = [first, second],
        ItemOrder = [orphan, first, first]
    };
    var host = new GroupDefinition { Tabs = [tab], ActiveTabIndex = 0 };
    host.ReloadActiveTab();
    var store = new LayoutStore(stateDirectory);
    await store.SaveAsync(new AppState { Groups = [host] });
    var loaded = await store.LoadAsync();
    var loadedHost = loaded.Groups.Single();
    Assert(loadedHost.Tabs.Single().ItemOrder.SequenceEqual([first, second], StringComparer.OrdinalIgnoreCase),
        "layout load repairs tab order");
    Assert(loadedHost.ItemOrder.SequenceEqual(loadedHost.Tabs.Single().ItemOrder, StringComparer.OrdinalIgnoreCase),
        "layout load synchronizes repaired active tab");
}

static async Task TestHotKeyAndDockPersistenceAsync(string root)
{
    var directory = Path.Combine(root, "hotkey-state");
    var tab = new LayoutTab { Title = "图片" };
    var group = new GroupDefinition { Tabs = [tab], DockEdge = DockEdge.Right };
    group.ReloadActiveTab();
    var state = new AppState
    {
        Settings = new AppSettings
        {
            InteractionMode = LayoutInteractionMode.EdgeHide,
            TopmostHotKeys = [new TopmostHotKeyBinding { Gesture = "Ctrl+Alt+P", LayoutIds = [tab.Id] }]
        },
        Groups = [group]
    };
    var store = new LayoutStore(directory);
    await store.SaveAsync(state);
    var loaded = await store.LoadAsync();
    Assert(loaded.Settings.InteractionMode == LayoutInteractionMode.EdgeHide, "edge interaction mode persists");
    Assert(loaded.Settings.TopmostHotKeys.Single().LayoutIds.SequenceEqual([tab.Id]), "targeted hotkey layout persists");
    Assert(loaded.Groups.Single().DockEdge == DockEdge.Right, "dock edge persists");

    var legacyDirectory = Path.Combine(root, "legacy-hotkey-state");
    var legacyStore = new LayoutStore(legacyDirectory);
    await legacyStore.SaveAsync(new AppState
    {
        Settings = new AppSettings { TopmostHotKey = "Ctrl+Alt+L", TopmostHotKeys = [] }
    });
    var migrated = await legacyStore.LoadAsync();
    Assert(migrated.Settings.TopmostHotKeys.Single().Gesture == "Ctrl+Alt+L" && migrated.Settings.TopmostHotKeys.Single().AllLayouts,
        "legacy single hotkey migrates to all-layout binding");
}

static async Task TestLayoutBackupAsync(string root)
{
    var directory = Path.Combine(root, "state");
    var store = new LayoutStore(directory);
    var first = new AppState { Groups = [new GroupDefinition { Title = "first" }] };
    var second = new AppState { Groups = [new GroupDefinition { Title = "second" }] };
    await store.SaveAsync(first);
    await store.SaveAsync(second);
    var backup = await store.LoadBackupAsync();
    Assert(backup?.Groups.Single().Title == "first", "layout previous-version backup");

    await File.WriteAllTextAsync(store.StateFile, "{broken json");
    var recovered = await store.LoadAsync();
    Assert(recovered.Groups.Single().Title == "first", "corrupt layout backup recovery");
}

static async Task TestFirstRunPresetsAsync(string root)
{
    var directory = Path.Combine(root, "first-run-state");
    var store = new LayoutStore(directory);
    Assert(!store.HasState, "first run starts without a state file");
    var state = await store.LoadAsync();
    Assert(state.Groups.Count == 1, "first run creates one preset tab host");
    Assert(state.LayoutMatchRules.Count == 9, "first run creates nine preset rules");
    var host = state.Groups.Single();
    Assert(host.Tabs.Count == 9 && host.HasMultipleTabs, "first run combines presets into one tabbed layout");
    Assert(host.Width == 1280 && host.Height == 640, "first run uses the large tab-host size");
    Assert(host.Tabs.Select(tab => tab.Title).SequenceEqual(
        ["文件夹", "音乐", "应用程序", "游戏", "图片", "视频", "压缩包", "文档", "其他文件"]),
        "first run uses the preset tab order");
    Assert(state.LayoutMatchRules.All(rule => host.Tabs.Any(tab =>
        string.Equals(rule.GroupId, tab.Id.ToString(), StringComparison.OrdinalIgnoreCase))),
        "first run binds every preset rule to a tab");

    var image = Path.Combine(root, "first-run-image.png");
    await File.WriteAllTextAsync(image, "image");
    var assignment = new LayoutAssignmentService().Preview([image], state.Groups, state.LayoutMatchRules).Single();
    Assert(assignment.GroupId == host.Id && assignment.TabId is { } imageTabId &&
        host.Tabs.Single(tab => tab.Id == imageTabId).Title == "图片",
        "first run image rule targets the image layout");
}

static async Task TestStorageMigrationAsync(string root)
{
    var oldData = Path.Combine(root, "storage-old");
    var oldLogs = Path.Combine(oldData, "logs");
    var newData = Path.Combine(root, "storage-new");
    var newLogs = Path.Combine(root, "logs-new");
    Directory.CreateDirectory(oldLogs);
    await File.WriteAllTextAsync(Path.Combine(oldData, "layout.json"), "layout");
    await File.WriteAllTextAsync(Path.Combine(oldLogs, "zdesk-test.log"), "log");

    await AppDataPathService.MigrateDirectoryAsync(oldData, newData, oldLogs);
    Assert(File.Exists(Path.Combine(newData, "layout.json")), "data migration copies layout data");
    Assert(File.Exists(Path.Combine(oldData, "layout.json")), "data migration retains a source backup");
    Assert(File.Exists(Path.Combine(oldLogs, "zdesk-test.log")), "data migration excludes independent logs");

    await AppDataPathService.MigrateDirectoryAsync(oldLogs, newLogs);
    Assert(File.Exists(Path.Combine(newLogs, "zdesk-test.log")), "log migration moves existing logs");
    Assert(File.Exists(Path.Combine(oldLogs, "zdesk-test.log")), "log migration retains a source backup");
}

static async Task TestLayoutViewModesAsync(string root)
{
    var directory = Path.Combine(root, "view-mode-state");
    var store = new LayoutStore(directory);
    var group = new GroupDefinition
    {
        Title = "tab host",
        AutoCollapse = true,
        ViewMode = LayoutViewMode.Details,
        SortProperty = LayoutSortProperty.Modified,
        SortDescending = true,
        Tabs =
        [
            new LayoutTab { Title = "images", ViewMode = LayoutViewMode.ExtraLargeIcons, SortProperty = LayoutSortProperty.Name },
            new LayoutTab { Title = "documents", ViewMode = LayoutViewMode.Details, SortProperty = LayoutSortProperty.Modified, SortDescending = true }
        ],
        ActiveTabIndex = 1
    };
    await store.SaveAsync(new AppState { Groups = [group] });
    var loaded = await store.LoadAsync();
    var restored = loaded.Groups.Single();
    Assert(restored.AutoCollapse, "layout auto-collapse persisted");
    Assert(restored.ViewMode == LayoutViewMode.Details, "active layout view mode persisted");
    Assert(restored.SortProperty == LayoutSortProperty.Modified && restored.SortDescending,
        "active layout sort persisted");
    Assert(restored.Tabs.Select(tab => tab.ViewMode).SequenceEqual(
        [LayoutViewMode.ExtraLargeIcons, LayoutViewMode.Details]), "tab view modes persisted");

    var clone = SnapshotService.CloneGroups([restored]).Single();
    Assert(clone.AutoCollapse, "snapshot auto-collapse cloned");
    Assert(clone.ViewMode == LayoutViewMode.Details, "snapshot active view mode cloned");
    Assert(clone.Tabs[0].ViewMode == LayoutViewMode.ExtraLargeIcons, "snapshot tab view mode cloned");
    Assert(clone.SortProperty == LayoutSortProperty.Modified && clone.SortDescending,
        "snapshot active sort cloned");
    Assert(clone.Tabs[0].SortProperty == LayoutSortProperty.Name, "snapshot tab sort cloned");

    restored.ActivateTab(0);
    var renamedTabId = restored.Tabs[0].Id;
    restored.Title = "images renamed";
    restored.StoreActiveTab();
    restored.ActivateTab(1);
    restored.ActivateTab(0);
    Assert(restored.Tabs[0].Id == renamedTabId && restored.Title == "images renamed",
        "renamed tab remains switchable by identity");

    var dragPayload = new LayoutTabDragPayload(restored.ExportTabs()[0], 0, restored.AutoCollapse);
    var detached = GroupDefinition.FromTab(dragPayload.Tab);
    detached.AutoCollapse = dragPayload.AutoCollapse;
    Assert(detached.AutoCollapse, "detached tab inherits host auto-collapse");
}

static async Task TestTransferCancellationAsync(string root)
{
    var source = Directory.CreateDirectory(Path.Combine(root, "cancel-source")).FullName;
    var target = Directory.CreateDirectory(Path.Combine(root, "cancel-target")).FullName;
    for (var index = 0; index < 20; index++)
    {
        await File.WriteAllBytesAsync(Path.Combine(source, $"item-{index}.bin"), new byte[1024]);
    }
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await new FileTransferService().ExecuteAsync(
            Directory.EnumerateFiles(source), target, FileTransferMode.Copy, cancellationToken: cancellation.Token);
        Assert(false, "cancelled transfer throws");
    }
    catch (OperationCanceledException)
    {
        Assert(!Directory.EnumerateFileSystemEntries(target).Any(), "cancelled transfer does not start items");
    }
}

static async Task TestConflictStrategiesAsync(string root)
{
    var source = Directory.CreateDirectory(Path.Combine(root, "conflict-source")).FullName;
    var target = Directory.CreateDirectory(Path.Combine(root, "conflict-target")).FullName;
    var sourceFile = Path.Combine(source, "same.txt");
    var targetFile = Path.Combine(target, "same.txt");
    await File.WriteAllTextAsync(sourceFile, "new");
    await File.WriteAllTextAsync(targetFile, "old");
    var service = new FileTransferService();

    var skipped = await service.ExecuteAsync([sourceFile], target, FileTransferMode.Copy,
        conflictStrategy: FileConflictStrategy.Skip);
    Assert(skipped.Succeeded == 0 && await File.ReadAllTextAsync(targetFile) == "old", "conflict skip");

    var overwritten = await service.ExecuteAsync([sourceFile], target, FileTransferMode.Copy,
        conflictStrategy: FileConflictStrategy.Overwrite);
    Assert(overwritten.Succeeded == 1 && await File.ReadAllTextAsync(targetFile) == "new", "conflict overwrite");
    Assert(!Directory.EnumerateFileSystemEntries(target).Any(path => path.Contains(".zdesk-replaced-")), "overwrite backup cleaned");
}

static async Task TestRulePerformanceAsync(string root)
{
    var source = Directory.CreateDirectory(Path.Combine(root, "performance-source")).FullName;
    var target = Directory.CreateDirectory(Path.Combine(root, "performance-target")).FullName;
    for (var index = 0; index < 2_000; index++)
    {
        await File.WriteAllTextAsync(Path.Combine(source, $"item-{index:D4}.tmp"), string.Empty);
    }
    var rule = new ClassificationRule { SourceFolder = source, TargetFolder = target, Extensions = "tmp" };
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var preview = new RuleEngine().Preview([rule]);
    stopwatch.Stop();
    Assert(preview.Count == 2_000, "large rule preview count");
    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "large rule preview performance");
}

static async Task TestDiagnosticsAsync(string root)
{
    var stateDirectory = Path.Combine(root, "diagnostic-state");
    var output = Directory.CreateDirectory(Path.Combine(root, "diagnostic-output")).FullName;
    var store = new LayoutStore(stateDirectory);
    await store.SaveAsync(new AppState
    {
        Groups = [new GroupDefinition { Kind = GroupKind.Folder, FolderPath = @"C:\Users\Private\Documents" }]
    });
    var package = await new DiagnosticService(store).CreatePackageAsync(output);
    Assert(File.Exists(package), "diagnostic package exists");
    using var archive = System.IO.Compression.ZipFile.OpenRead(package);
    var entry = archive.GetEntry("layout.redacted.json");
    Assert(entry is not null, "diagnostic redacted layout entry");
    using var reader = new StreamReader(entry!.Open());
    var text = await reader.ReadToEndAsync();
    Assert(!text.Contains("Private", StringComparison.OrdinalIgnoreCase), "diagnostic private path redacted");
}

static void TestUpdateManifestComparison()
{
    Assert(UpdateService.IsNewer(new UpdateManifest { Version = "99.0.0" }), "newer update recognized");
    Assert(!UpdateService.IsNewer(new UpdateManifest { Version = "0.1.0" }), "older update rejected");
}

static void TestUpdateRollbackPreparation(string root)
{
    var updateDirectory = Directory.CreateDirectory(Path.Combine(root, "update")).FullName;
    var package = Path.Combine(updateDirectory, "ZDesk-new.exe.download");
    var application = Path.Combine(updateDirectory, "ZDesk.exe");
    File.WriteAllText(package, "new");
    File.WriteAllText(application, "old");
    var script = new UpdateApplyService().PrepareRollbackScript(package, application);
    Assert(File.Exists(script), "update rollback script created");
    var text = File.ReadAllText(script);
    Assert(text.Contains(":rollback") && text.Contains(".previous"), "update rollback path present");
}

static void TestShellIcons(string root)
{
    var iconFolder = Directory.CreateDirectory(Path.Combine(root, "icons")).FullName;
    var textFile = Path.Combine(iconFolder, "document.txt");
    var internetShortcut = Path.Combine(iconFolder, "website.url");
    var iconShortcut = Path.Combine(iconFolder, "icon-source.url");
    var iconSource = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Assets", "ZDesk.ico"));
    var bitmapFile = Path.Combine(iconFolder, "thumbnail.bmp");
    File.WriteAllText(textFile, "text");
    File.WriteAllText(internetShortcut, "[InternetShortcut]\nURL=https://example.com");
    File.WriteAllText(iconShortcut, $"[InternetShortcut]\nURL=steam://rungameid/1\nIconIndex=0\nIconFile=\"{iconSource}\"\n");
    WriteTestBitmap(bitmapFile, 64, 64);
    Assert(ShellIconService.GetIcon(iconFolder, isDirectory: true) is not null, "folder shell icon");
    Assert(ShellIconService.GetIcon(textFile, isDirectory: false) is not null, "file shell icon");
    Assert(ShellIconService.GetIcon(internetShortcut, isDirectory: false) is not null, "url shell icon");
    Assert(ShellIconService.GetDisplayImage(iconShortcut, isDirectory: false) is System.Windows.Media.Imaging.BitmapSource,
        "url custom icon source");
    Assert(ShellIconService.GetDisplayImage(textFile, isDirectory: false) is not null, "shell display image");
    foreach (var shortcut in new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AIRI.lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Ani.lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Bandizip.lnk")
    }.Where(File.Exists))
    {
        Assert(ShellIconService.GetDisplayImage(shortcut, isDirectory: false) is not null,
            $"shortcut shell icon: {Path.GetFileName(shortcut)}");
    }
    var thumbnail = ShellIconService.GetDisplayImage(bitmapFile, isDirectory: false);
    Assert(thumbnail is System.Windows.Media.Imaging.BitmapSource { PixelWidth: >= 64 },
        "native shell thumbnail resolution");
}

static void WriteTestBitmap(string path, int width, int height)
{
    var rowSize = (width * 3 + 3) & ~3;
    var imageSize = rowSize * height;
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0x4D42);
    writer.Write(54 + imageSize);
    writer.Write(0);
    writer.Write(54);
    writer.Write(40);
    writer.Write(width);
    writer.Write(height);
    writer.Write((ushort)1);
    writer.Write((ushort)24);
    writer.Write(0);
    writer.Write(imageSize);
    writer.Write(2835);
    writer.Write(2835);
    writer.Write(0);
    writer.Write(0);
    var padding = new byte[rowSize - width * 3];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            writer.Write((byte)(x * 255 / Math.Max(1, width - 1)));
            writer.Write((byte)(y * 255 / Math.Max(1, height - 1)));
            writer.Write((byte)180);
        }
        writer.Write(padding);
    }
}

static void TestDesktopWindowStyle(string root)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new ZDesk.App();
            app.InitializeComponent();
            var detectedDesktopHost = WorkerWHostService.FindHost();
            TestNativeMethods.GetWindowThreadProcessId(detectedDesktopHost, out var detectedHostProcessId);
            var detectedProcessName = detectedHostProcessId == 0
                ? "none"
                : System.Diagnostics.Process.GetProcessById((int)detectedHostProcessId).ProcessName;
            if (detectedDesktopHost == nint.Zero || detectedProcessName != "explorer")
                throw new InvalidOperationException(
                    $"WorkerW host detection returns Explorer window | host=0x{detectedDesktopHost.ToInt64():X} pid={detectedHostProcessId} process={detectedProcessName}");
            var definition = new GroupDefinition { Title = "style test", Height = 320 };
            var mappingDefinition = new GroupDefinition
            {
                Title = "mapped folder",
                Kind = GroupKind.Folder,
                FolderPath = Path.GetTempPath()
            };
            var window = new ZDesk.Windows.DesktopGroupWindow(definition)
            {
                Left = -10000,
                Top = -10000,
                Opacity = 0
            };
            window.Show();
            window.RestoreDesktopLayer();
            var settingsWindow = new ZDesk.Windows.SettingsWindow(
                new AppSettings
                {
                    DataDirectory = AppDataPathService.DataDirectory,
                    LogDirectory = AppDataPathService.LogDirectory
                },
                startupEnabled: false,
                [definition, mappingDefinition],
                [],
                LayoutMatchRule.CreateDefaults().Select((rule, index) =>
                {
                    if (index == 0) rule.GroupId = mappingDefinition.Id.ToString();
                    return rule;
                }))
            {
                Left = -11000,
                Top = -11000,
                Opacity = 0
            };
            settingsWindow.Show();
            var standardMode = (System.Windows.Controls.RadioButton)settingsWindow.FindName("StandardModeRadio");
            var hotKeysGrid = (System.Windows.Controls.DataGrid)settingsWindow.FindName("HotKeysGrid");
            var hotKeyTargets = (System.Windows.Controls.ListBox)settingsWindow.FindName("HotKeyTargetsList");
            var applyButton = (System.Windows.Controls.Button)settingsWindow.FindName("ApplyButton");
            Assert(IsDarkSurface(standardMode.Background), "settings mode selector uses dark themed surface");
            Assert(IsDarkSurface(hotKeysGrid.Background), "settings hotkey grid does not fall back to white system surface");
            Assert(IsDarkSurface(hotKeyTargets.Background), "settings target list does not fall back to white system surface");
            Assert(settingsWindow.NormalLayoutChoices.Count == 1 &&
                settingsWindow.NormalLayoutChoices[0].Id == definition.Id.ToString(),
                "folder mapping layouts are excluded from rule targets");
            Assert(settingsWindow.ResultLayoutRules[0].GroupId == definition.Id.ToString(),
                "invalid rule target is repaired to an ordinary layout");
            Assert(!applyButton.IsEnabled, "settings apply starts disabled");
            standardMode.IsChecked = false;
            ((System.Windows.Controls.RadioButton)settingsWindow.FindName("EdgeHideModeRadio")).IsChecked = true;
            Assert(applyButton.IsEnabled, "interaction mode changes mark settings dirty");
            Assert(window.IsEnabled, "non-modal settings keeps layouts interactive");
            var renamedDefinition = SnapshotService.CloneGroups([definition]).Single();
            renamedDefinition.Title = "renamed while settings open";
            settingsWindow.SynchronizeLayouts([renamedDefinition]);
            Assert(settingsWindow.NormalLayoutChoices.Single().Title == "renamed while settings open",
                "settings layout targets synchronize while open");
            Assert(settingsWindow.ResultLayoutRules[0].GroupId == definition.Id.ToString(),
                "renaming a layout preserves existing rule target id");
            var addedDefinition = new GroupDefinition { Title = "added while settings open" };
            settingsWindow.SynchronizeLayouts([renamedDefinition, addedDefinition]);
            Assert(settingsWindow.ResultLayoutRules[0].GroupId == definition.Id.ToString(),
                "adding a layout preserves existing rule target id");
            settingsWindow.Close();

            var manyRules = Enumerable.Range(0, 500).Select(index => new LayoutMatchRule
            {
                Name = $"rule-{index:D3}",
                Priority = (index + 1) * 10,
                GroupId = definition.Id.ToString(),
                Extensions = ".txt"
            }).ToArray();
            var originalRuleState = string.Join("|", manyRules.Select(rule =>
                $"{rule.Id:N}:{rule.Priority}:{rule.GroupId}:{rule.Extensions}"));
            var ruleWindow = new ZDesk.Windows.SettingsWindow(
                new AppSettings
                {
                    DataDirectory = AppDataPathService.DataDirectory,
                    LogDirectory = AppDataPathService.LogDirectory
                },
                startupEnabled: false,
                [definition],
                [],
                manyRules)
            {
                Left = -11500,
                Top = -11500,
                Opacity = 0
            };
            var ruleOpen = System.Diagnostics.Stopwatch.StartNew();
            ruleWindow.Show();
            var tabs = FindVisualChild<System.Windows.Controls.TabControl>(ruleWindow);
            Assert(tabs is not null, "settings navigation tab control exists");
            tabs!.SelectedIndex = 3;
            PumpDispatcher(TimeSpan.FromMilliseconds(120));
            ruleOpen.Stop();
            var ruleApply = (System.Windows.Controls.Button)ruleWindow.FindName("ApplyButton");
            Assert(ruleOpen.Elapsed < TimeSpan.FromSeconds(5), "500-rule page yields to dispatcher promptly");
            Assert(!ruleApply.IsEnabled, "opening rule page does not mark settings dirty");
            Assert(originalRuleState == string.Join("|", manyRules.Select(rule =>
                    $"{rule.Id:N}:{rule.Priority}:{rule.GroupId}:{rule.Extensions}")),
                "opening rule page does not mutate source model");
            ruleWindow.Close();

            var mappedFolder = Directory.CreateDirectory(Path.Combine(root, "mapped-ui-selection")).FullName;
            File.WriteAllText(Path.Combine(mappedFolder, "item.txt"), "item");
            var selectionDefinition = new GroupDefinition
            {
                Title = "mapped selection",
                Kind = GroupKind.Folder,
                FolderPath = mappedFolder
            };
            var selectionWindow = new ZDesk.Windows.DesktopGroupWindow(selectionDefinition)
            {
                Left = -11700,
                Top = -11700,
                Opacity = 0
            };
            var shellSelectionRequests = 0;
            selectionWindow.ShellSelectionRequested += _ => shellSelectionRequests++;
            selectionWindow.Show();
            var fileList = (System.Windows.Controls.ListBox)selectionWindow.Group.FindName("FileList");
            fileList.SelectAll();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(shellSelectionRequests == 0, "layout SelectAll stays inside the WPF selection model");
            selectionWindow.Close();

            var dockDefinition = new GroupDefinition
            {
                Title = "dock reset test",
                DesktopX = -9000,
                DesktopY = -9000,
                DockEdge = DockEdge.Left
            };
            var dockWindow = new ZDesk.Windows.DesktopGroupWindow(dockDefinition, animationsEnabled: false)
            {
                Left = -9000,
                Top = -9000,
                Opacity = 0
            };
            dockWindow.Show();
            dockWindow.HideToEdge(animate: false);
            Assert(dockWindow.IsEdgeHidden, "edge mode can hide docked window");
            dockWindow.SetInteractionMode(LayoutInteractionMode.Standard);
            Assert(!dockWindow.IsEdgeHidden && dockDefinition.DockEdge == DockEdge.None,
                "switching to standard mode clears hidden dock state");
            Assert(Math.Abs(dockWindow.Left - dockDefinition.DesktopX.Value) < 0.5 &&
                Math.Abs(dockWindow.Top - dockDefinition.DesktopY.Value) < 0.5,
                "switching to standard mode restores expanded position");
            dockWindow.Close();

            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var style = TestNativeMethods.GetWindowLongPtr(handle, -20).ToInt64();
            Assert((style & 0x00000080L) != 0, "desktop layout uses tool-window style");
            Assert((style & 0x00040000L) == 0, "desktop layout excludes app-window style");
            if ((style & 0x00000008L) != 0)
            {
                var boundaryPrevious = TestNativeMethods.GetWindow(detectedDesktopHost, 3);
                throw new InvalidOperationException(
                    $"desktop layout starts outside topmost band | layoutStyle=0x{style:X} boundaryZ={GetZIndex(detectedDesktopHost)} boundaryStyle=0x{TestNativeMethods.GetWindowLongPtr(detectedDesktopHost, -20).ToInt64():X} previousZ={GetZIndex(boundaryPrevious)} previousStyle=0x{TestNativeMethods.GetWindowLongPtr(boundaryPrevious, -20).ToInt64():X}");
            }
            Assert(TestNativeMethods.GetWindow(handle, 4) == nint.Zero,
                "desktop layout clears WPF hidden owner");
            var normalWindow = new System.Windows.Window
            {
                Width = 100,
                Height = 100,
                Left = -12000,
                Top = -12000,
                Opacity = 0,
                ShowInTaskbar = true
            };
            normalWindow.Show();
            var normalHandle = new System.Windows.Interop.WindowInteropHelper(normalWindow).Handle;
            TestNativeMethods.SetWindowPos(handle, nint.Zero, 0, 0, 0, 0, 0x0013);
            if (!IsBelow(handle, normalHandle))
            {
                var owner = TestNativeMethods.GetWindow(handle, 4);
                TestNativeMethods.GetWindowThreadProcessId(owner, out var ownerProcessId);
                throw new InvalidOperationException(
                    $"desktop layout rejects normal-mode raise requests | layoutZ={GetZIndex(handle)} normalZ={GetZIndex(normalHandle)} owner=0x{owner.ToInt64():X} ownerPid={ownerProcessId}");
            }
            var secondDefinition = new GroupDefinition { Title = "second style test", Height = 260 };
            var secondWindow = new ZDesk.Windows.DesktopGroupWindow(secondDefinition)
            {
                Left = -10500,
                Top = -10500,
                Opacity = 0
            };
            secondWindow.Show();
            secondWindow.RestoreDesktopLayer();
            var secondHandle = new System.Windows.Interop.WindowInteropHelper(secondWindow).Handle;
            window.BringToFrontWithin([window, secondWindow]);
            Assert(IsBelow(secondHandle, handle), "latest layout interaction raises first layout above sibling");
            Assert(IsBelow(handle, normalHandle), "raised layout remains below normal applications");
            secondWindow.BringToFrontWithin([window, secondWindow]);
            Assert(IsBelow(handle, secondHandle), "latest layout interaction raises second layout above sibling");
            Assert(IsBelow(secondHandle, normalHandle), "second raised layout remains below normal applications");
            secondWindow.Close();
            window.SetTemporaryTopmost(true);
            style = TestNativeMethods.GetWindowLongPtr(handle, -20).ToInt64();
            Assert((style & 0x00000008L) != 0, "desktop layout enters topmost mode");
            TestNativeMethods.SetWindowPos(normalHandle, nint.Zero, 0, 0, 0, 0, 0x0013);
            Assert(IsBelow(normalHandle, handle), "topmost layout stays above newly raised normal windows");
            window.SetTemporaryTopmost(false);
            style = TestNativeMethods.GetWindowLongPtr(handle, -20).ToInt64();
            Assert((style & 0x00000008L) == 0, "desktop layout leaves topmost mode");
            TestNativeMethods.SetWindowPos(handle, nint.Zero, 0, 0, 0, 0, 0x0013);
            Assert(IsBelow(handle, normalHandle), "restored layout remains below normal windows");
            normalWindow.Close();
            var collapseButton = (System.Windows.Controls.Button)window.Group.FindName("CollapseButton");
            for (var index = 0; index < 8; index++)
            {
                collapseButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpDispatcher(TimeSpan.FromMilliseconds(40));
            }
            PumpDispatcher(TimeSpan.FromMilliseconds(280));
            Assert(!definition.IsCollapsed, "rapid collapse sequence ends expanded");
            Assert(Math.Abs(definition.Height - 320) < 0.5, "rapid collapse preserves persisted height");
            Assert(Math.Abs(window.ActualHeight - 320) < 1.5, "rapid collapse restores actual height");

            definition.AutoCollapse = true;
            var scheduleAutoCollapse = typeof(ZDesk.Controls.GroupContainer).GetMethod(
                "ScheduleAutoCollapse",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var mouseEnter = typeof(ZDesk.Controls.GroupContainer).GetMethod(
                "Group_MouseEnter",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            scheduleAutoCollapse?.Invoke(window.Group, null);
            PumpDispatcher(TimeSpan.FromMilliseconds(560));
            Assert(definition.IsCollapsed, "auto-collapse folds after pointer leaves");
            Assert(Math.Abs(definition.Height - 320) < 0.5, "auto-collapse preserves persisted height");
            mouseEnter?.Invoke(window.Group,
                [window.Group, new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)]);
            PumpDispatcher(TimeSpan.FromMilliseconds(260));
            Assert(!definition.IsCollapsed, "auto-collapse expands on pointer enter");
            Assert(Math.Abs(window.ActualHeight - 320) < 1.5, "auto-expand restores actual height");
            window.Close();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Desktop window style test failed.", failure);
}

static bool IsDarkSurface(System.Windows.Media.Brush brush) =>
    brush is System.Windows.Media.SolidColorBrush solid &&
    (solid.Color.R + solid.Color.G + solid.Color.B) / 3 < 100;

static bool IsBelow(nint lowerWindow, nint upperWindow)
{
    var current = upperWindow;
    for (var index = 0; index < 10000 && current != nint.Zero; index++)
    {
        current = TestNativeMethods.GetWindow(current, 2);
        if (current == lowerWindow) return true;
    }
    return false;
}

static int GetZIndex(nint window)
{
    var current = TestNativeMethods.GetTopWindow(nint.Zero);
    for (var index = 0; index < 10000 && current != nint.Zero; index++)
    {
        if (current == window) return index;
        current = TestNativeMethods.GetWindow(current, 2);
    }
    return -1;
}

static void PumpDispatcher(TimeSpan duration)
{
    var frame = new System.Windows.Threading.DispatcherFrame();
    var timer = new System.Windows.Threading.DispatcherTimer(
        duration,
        System.Windows.Threading.DispatcherPriority.Background,
        (_, _) => frame.Continue = false,
        System.Windows.Threading.Dispatcher.CurrentDispatcher);
    timer.Start();
    System.Windows.Threading.Dispatcher.PushFrame(frame);
    timer.Stop();
}

static T? FindVisualChild<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
{
    for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
        if (child is T match) return match;
        if (FindVisualChild<T>(child) is { } nested) return nested;
    }
    return null;
}

static class TestNativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern nint GetWindow(nint window, uint command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern nint GetTopWindow(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
