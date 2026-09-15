using ZDesk.Controls;
using ZDesk.Models;
using ZDesk.Services;
using ZDesk.Windows;
using ZXingCpp;

var testRoot = Path.Combine(Path.GetTempPath(), $"ZDesk-smoke-{Guid.NewGuid():N}");
var normalizedTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
var normalizedTestRoot = Path.GetFullPath(testRoot);

if (!normalizedTestRoot.StartsWith(normalizedTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Smoke test directory escaped the system temporary directory.");
}

try
{
    Assert(!DesktopRenderingOptions.UseNativeTopLevel,
        "production desktop rendering defaults to WPF");
    Assert(!DesktopRenderingOptions.UseNativeDesktop,
        "native desktop child renderer is disabled by default");
    await TestFileTransfersAsync(normalizedTestRoot);
    await TestShellFileOperationsAsync(normalizedTestRoot);
    await TestPreviewProvidersAsync(normalizedTestRoot);
    TestDesktopSelectionBoundary(normalizedTestRoot);
    TestLayoutRuleNotifications();
    TestHotKeyParser();
    TestQrCodeRecognition();
    TestQrSelectionGeometry();
    TestQrSelectionInteractionState();
    TestQrRecognitionFrameGeometry();
    TestEdgeDockGeometry();
    await TestRulesAsync(normalizedTestRoot);
    await TestLayoutPathMatchingAsync(normalizedTestRoot);
    TestLockedLayoutAssignment(normalizedTestRoot);
    TestLockedLayoutOptions();
    await TestLayoutItemStateRepairAsync(normalizedTestRoot);
    await TestHotKeyAndDockPersistenceAsync(normalizedTestRoot);
    await TestMemoSupportAsync(normalizedTestRoot);
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
    TestMemoryDiagnostics(normalizedTestRoot);
    TestNativeDesktopLayout();
    TestDesktopSelectionState();
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

    var unexpectedFailure = new QuickLookPreviewProvider(
        () => executableTarget,
        (_, _) => throw new MissingMemberException("preview test failure"));
    Assert(!await new FilePreviewService([unexpectedFailure]).TryPreviewAsync(previewFile),
        "unexpected QuickLook failure is contained");
    Assert(new Lazy<FilePreviewService>(() => new FilePreviewService()).Value is not null,
        "lazy preview service uses an explicit factory");

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
    var lastContainer = list.ItemContainerGenerator.ContainerFromIndex(499) as System.Windows.UIElement;
    Assert(lastContainer is not null,
        "virtualizing wrap panel realizes the last item after scrolling");
    var indexFromContainer = typeof(VirtualizingWrapPanel).GetMethod("IndexFromContainer",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Assert((int?)indexFromContainer?.Invoke(panel, [lastContainer]) == 499,
        "virtualizing wrap panel uses the real item index");

    var bottomOffset = panel.VerticalOffset;
    var beginMouseSelection = typeof(VirtualizingWrapPanel).GetMethod("BeginMouseSelection",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var endMouseSelection = typeof(VirtualizingWrapPanel).GetMethod("EndMouseSelectionAfterInput",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var bringIndexIntoView = typeof(VirtualizingWrapPanel).GetMethod("BringIndexIntoView",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    beginMouseSelection?.Invoke(panel, null);
    bringIndexIntoView?.Invoke(panel, [0]);
    panel.MakeVisible(lastContainer!, System.Windows.Rect.Empty);
    Assert(Math.Abs(panel.VerticalOffset - bottomOffset) < 0.1,
        "mouse selection keeps the current scroll offset");
    endMouseSelection?.Invoke(panel, null);
    PumpDispatcher(TimeSpan.FromMilliseconds(80));
    bringIndexIntoView?.Invoke(panel, [0]);
    Assert(panel.VerticalOffset < bottomOffset,
        "keyboard or programmatic selection can still scroll");
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

static async Task TestMemoSupportAsync(string root)
{
    var directory = Path.Combine(root, "memo-state");
    var store = new MemoNotebookStore(directory);
    var first = new MemoNoteMetadata { Title = "First", PreviewText = "first memo", SearchText = "first memo" };
    var second = new MemoNoteMetadata { Title = "Second", PreviewText = "second memo", SearchText = "second memo" };
    await store.SaveNoteAsync(first, "first memo"u8.ToArray(), null);
    await store.SaveIndexAsync(new MemoNotebookIndex { Notes = [first] });
    await store.SaveNoteAsync(first, "first memo updated"u8.ToArray(), null);
    first.PreviewText = "first memo updated";
    first.SearchText = first.PreviewText;
    first.CaretOffset = 7;
    first.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
    await store.SaveIndexAsync(new MemoNotebookIndex { Notes = [first] });
    var files = await store.LoadNoteAsync(first.Id);
    Assert(System.Text.Encoding.UTF8.GetString(files.Primary!) == "first memo updated", "memo primary note persists");
    Assert(System.Text.Encoding.UTF8.GetString(files.Backup!) == "first memo", "memo note backup persists");
    await store.SaveNoteAsync(second, "second memo"u8.ToArray(), null);
    second.UpdatedAtUtc = first.UpdatedAtUtc.AddMinutes(1);
    var index = new MemoNotebookIndex { Notes = [second, first] };
    await store.SaveIndexAsync(index);
    await store.SaveIndexAsync(index);
    var loadedIndex = await store.LoadAsync();
    Assert(loadedIndex.Index.Notes.Count == 2 && loadedIndex.Index.Notes[0].Id == second.Id && loadedIndex.Index.Notes[1].CaretOffset == 7, "memo notebook index persists, orders notes and restores caret metadata");
    await File.WriteAllTextAsync(store.IndexFile, "broken index");
    var recoveredIndex = await store.LoadAsync();
    Assert(recoveredIndex.RecoveredFromBackup && recoveredIndex.Index.Notes.Count == 2, "memo index backup recovery");
    await File.WriteAllTextAsync(store.IndexBackupFile, "broken backup index");
    var rebuiltIndex = await store.LoadAsync();
    Assert(rebuiltIndex.RebuiltFromPackages && rebuiltIndex.Index.Notes.Count == 2, "memo index rebuild from note packages");
    await File.WriteAllTextAsync(Path.Combine(store.MemoDirectory, first.PackageFileName), "corrupted package");
    await store.PreserveCorruptNoteAsync(first);
    Assert(Directory.EnumerateFiles(store.MemoDirectory, $"{first.Id:N}.broken-*.xamlpackage").Any(), "memo corrupt note is preserved");
    await store.DeleteNoteAsync(second);
    Assert(!File.Exists(Path.Combine(store.MemoDirectory, second.PackageFileName)), "memo note deletion removes body");

    var legacyStore = new MemoNotebookStore(Path.Combine(root, "memo-legacy-package"));
    Directory.CreateDirectory(legacyStore.StateDirectory);
    await File.WriteAllBytesAsync(legacyStore.LegacyDocumentFile, "legacy memo"u8.ToArray());
    var legacy = await legacyStore.LoadLegacyAsync();
    Assert(legacy.Primary is not null, "memo legacy package is discoverable");
    await legacyStore.ArchiveLegacyAsync();
    Assert(File.Exists(Path.Combine(legacyStore.StateDirectory, "memo.legacy.xamlpackage")), "memo legacy package archives after migration");

    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var text = MemoPasteService.CreateTextFragment("see https://example.test/a and `code`\n```\nhttps://example.test/in-code\n```");
            Assert(text.Blocks.OfType<System.Windows.Documents.Paragraph>().Any(paragraph => paragraph.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any()),
                "memo plain text recognizes links");
            Assert(text.Blocks.OfType<System.Windows.Documents.Paragraph>().Any(paragraph => paragraph.FontFamily.Source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase)),
                "memo plain text creates code paragraph");
            var unfinished = MemoPasteService.CreateTextFragment("```\nhttps://inside.example\n");
            Assert(new System.Windows.Documents.TextRange(unfinished.ContentStart, unfinished.ContentEnd).Text.Contains("```", StringComparison.Ordinal)
                && !unfinished.Blocks.OfType<System.Windows.Documents.Paragraph>()
                    .SelectMany(paragraph => paragraph.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Any(),
                "memo unfinished fence stays plain text");

            var html = MemoPasteService.CreateHtmlFragment("<p><strong>bold</strong> <a href='https://example.test'>link</a></p><script>bad()</script><a href='javascript:alert(1)'>unsafe</a>");
            var htmlText = new System.Windows.Documents.TextRange(html.ContentStart, html.ContentEnd).Text;
            Assert(htmlText.Contains("bold", StringComparison.Ordinal) && htmlText.Contains("unsafe", StringComparison.Ordinal),
                "memo HTML keeps safe text");
            Assert(!html.Blocks.OfType<System.Windows.Documents.Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Any(link => link.NavigateUri?.Scheme == "javascript"),
                "memo HTML rejects dangerous links");

            var memoWindow = new MemoWindow();
            Assert(memoWindow.Topmost && !memoWindow.ShowInTaskbar && memoWindow.MinWidth == 480 && memoWindow.MinHeight == 320,
                "memo window uses topmost utility-window bounds");
            var deleteWindow = new MemoDeleteConfirmWindow("First");
            Assert(deleteWindow.Topmost && !deleteWindow.ShowInTaskbar && deleteWindow.Background is not null,
                "memo delete confirmation uses the dark utility-window theme");
            var snapshotRange = new System.Windows.Documents.TextRange(memoWindow.EditorControl.Document.ContentStart, memoWindow.EditorControl.Document.ContentEnd);
            snapshotRange.Text = "First title\r\nA searchable summary";
            var snapshot = memoWindow.CaptureContentSnapshot();
            Assert(snapshot.Title == "First title" && snapshot.PreviewText.Contains("searchable", StringComparison.Ordinal),
                "memo card title and summary derive from document");
            memoWindow.SetNotes([new MemoNoteCard(first, null)]);
            memoWindow.ShowList();
            Assert(memoWindow.CurrentNoteId is null, "memo starts in list view");
            memoWindow.ShowDetail(first);
            Assert(memoWindow.CurrentNoteId == first.Id, "memo opens note detail in same window");
            memoWindow.ShowList();
            var clipboardData = new System.Windows.DataObject();
            clipboardData.SetData(System.Windows.DataFormats.UnicodeText, "paste https://example.test");
            Assert(MemoPasteService.TryPaste(memoWindow.EditorControl, clipboardData, out _), "memo text paste is handled");
            Assert(new System.Windows.Documents.TextRange(memoWindow.EditorControl.Document.ContentStart, memoWindow.EditorControl.Document.ContentEnd).Text.Contains("paste", StringComparison.Ordinal),
                "memo pasted text reaches editor");
            Assert(memoWindow.EditorControl.Document.Blocks.OfType<System.Windows.Documents.Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Any(),
                "memo pasted URL becomes hyperlink");
            var pixels = new byte[] { 0x40, 0x80, 0xC0, 0xFF };
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, 4);
            var imageData = new System.Windows.DataObject();
            imageData.SetData(System.Windows.DataFormats.Bitmap, bitmap);
            Assert(MemoPasteService.TryPaste(memoWindow.EditorControl, imageData, out _), "memo bitmap paste is handled");
            Assert(memoWindow.EditorControl.Document.Blocks.OfType<System.Windows.Documents.Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines.OfType<System.Windows.Documents.InlineUIContainer>()).Any(),
                "memo bitmap paste becomes embedded image");
            var package = memoWindow.SavePackage();
            Assert(package.Length > 0 && memoWindow.LoadPackage(package), "memo window package roundtrip");
            memoWindow.AllowClose = true;
            memoWindow.Close();

        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Memo formatting smoke test failed.", failure);

    var legacyDirectory = Path.Combine(root, "memo-legacy-state");
    Directory.CreateDirectory(legacyDirectory);
    await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "layout.json"), "{\"Version\":14,\"Settings\":{\"TopmostHotKeys\":[]}}");
    var migrated = await new LayoutStore(legacyDirectory).LoadAsync();
    Assert(migrated.Settings.MemoHotKey == "Ctrl+Alt+M", "legacy state gets default memo hotkey");

    var clearedDirectory = Path.Combine(root, "memo-cleared-state");
    var clearedStore = new LayoutStore(clearedDirectory);
    await clearedStore.SaveAsync(new AppState { Version = AppState.CurrentVersion, Settings = new AppSettings { MemoHotKey = string.Empty } });
    var cleared = await clearedStore.LoadAsync();
    Assert(cleared.Settings.MemoHotKey == string.Empty, "cleared memo hotkey remains disabled");
}

static void TestQrCodeRecognition()
{
    var single = CreateQrFrame("https://example.test/one", 180);
    Assert(QrCodeRecognitionService.Decode(single).Single().Text == "https://example.test/one", "single QR recognition");

    var first = CreateQrFrame("first QR", 180);
    var second = CreateQrFrame("second QR", 180);
    var canvas = CreateSolidFrame(first.Width + second.Width + 60, Math.Max(first.Height, second.Height) + 40, 255);
    CopyFrame(first, canvas, 20, 20);
    CopyFrame(second, canvas, first.Width + 40, 20);
    var multiple = QrCodeRecognitionService.Decode(canvas);
    Assert(multiple.Select(result => result.Text).SequenceEqual(["first QR", "second QR"]), "multiple QR recognition and ordering");

    var duplicateFirst = CreateQrFrame("duplicate QR", 180);
    var duplicateSecond = CreateQrFrame("duplicate QR", 180);
    var duplicateCanvas = CreateSolidFrame(
        duplicateFirst.Width + duplicateSecond.Width + 60,
        Math.Max(duplicateFirst.Height, duplicateSecond.Height) + 40,
        255);
    CopyFrame(duplicateFirst, duplicateCanvas, 20, 20);
    CopyFrame(duplicateSecond, duplicateCanvas, duplicateFirst.Width + 40, 20);
    Assert(QrCodeRecognitionService.Decode(duplicateCanvas).Count(result => result.Text == "duplicate QR") == 2,
        "separate QR codes with the same text are retained");

    var rotated = Rotate90(single);
    Assert(QrCodeRecognitionService.Decode(rotated).Single().Text == "https://example.test/one", "rotated QR recognition");

    var inverted = Invert(single);
    Assert(QrCodeRecognitionService.Decode(inverted).Single().Text == "https://example.test/one", "inverted QR recognition");

    var stylized = CreateStylizedQrFrame("styled QR", 360);
    Assert(QrCodeRecognitionService.Decode(stylized).Single().Text == "styled QR",
        "gradient rounded-module QR with a centered logo mask is recognized");

    Assert(QrCodeRecognitionService.Decode(CreateSolidFrame(180, 180, 255)).Count == 0, "empty QR recognition result");
}

static void TestQrSelectionGeometry()
{
    var desktop = CreateSolidFrame(20, 10, 255, new System.Drawing.Rectangle(-10, 0, 20, 10));
    for (var row = 0; row < desktop.Height; row++)
    {
        for (var column = 0; column < 10; column++)
        {
            Array.Fill(desktop.Pixels, (byte)20, row * desktop.Stride + column * 4, 4);
            Array.Fill(desktop.Pixels, (byte)220, row * desktop.Stride + (column + 10) * 4, 4);
        }
    }
    var selection = QrSelectionGeometry.Normalize(new System.Drawing.Point(-5, 0), new System.Drawing.Point(5, 10));
    Assert(QrSelectionGeometry.IsValid(selection), "cross-screen QR selection is valid");
    var composed = QrSelectionGeometry.ComposeSelection(desktop, selection);
    Assert(composed.Bounds == new System.Drawing.Rectangle(-5, 0, 10, 10), "cross-screen selection preserves physical coordinates");
    Assert(composed.Pixels[0] == 20 && composed.Pixels[5 * 4] == 220, "cross-screen selection composes display pixels");
    Assert(!QrSelectionGeometry.IsValid(new System.Drawing.Rectangle(0, 0, 7, 8)), "tiny QR selection rejected");
}

static void TestQrSelectionInteractionState()
{
    var interaction = new QrSelectionInteractionState();
    Assert(interaction.Mode == QrSelectionInteractionMode.Idle, "QR selection starts idle");
    Assert(interaction.BeginExit() && interaction.IsExiting, "idle right-click starts QR capture exit");
    Assert(interaction.CompleteExit() && interaction.Mode == QrSelectionInteractionMode.Idle,
        "right-button release completes QR capture exit");

    Assert(interaction.Begin(new System.Drawing.Point(10, 10)), "left-button starts QR selection");
    Assert(interaction.IsDragging && interaction.CurrentSelection(new System.Drawing.Point(20, 30)) ==
        new System.Drawing.Rectangle(10, 10, 10, 20), "QR selection tracks left-drag geometry");
    Assert(interaction.BeginExit() && interaction.IsExiting,
        "right-click during QR selection starts complete capture exit");
    Assert(interaction.CompleteExit() && interaction.Mode == QrSelectionInteractionMode.Idle,
        "right-button release exits after cancelling QR selection");

    Assert(interaction.Begin(new System.Drawing.Point(0, 0)), "selection restarts after cancelled exit");
    Assert(interaction.Complete(new System.Drawing.Point(7, 8)) is null && !interaction.IsDragging,
        "tiny QR selection clears without leaving capture mode");

    Assert(interaction.Begin(new System.Drawing.Point(20, 20)), "valid selection starts");
    var complete = interaction.Complete(new System.Drawing.Point(40, 50));
    Assert(complete == new System.Drawing.Rectangle(20, 20, 20, 30), "valid QR selection completes on left release");

    Assert(interaction.Begin(new System.Drawing.Point(5, 5)), "capture-loss selection starts");
    interaction.Reset();
    Assert(!interaction.IsDragging, "lost QR pointer capture resets interaction state");
}

static void TestQrRecognitionFrameGeometry()
{
    var displays = new[]
    {
        new System.Drawing.Rectangle(-1920, 0, 1920, 1080),
        new System.Drawing.Rectangle(0, 0, 2560, 1440)
    };
    var frame = QrRecognitionFrameGeometry.CreateDefault(new System.Drawing.Point(400, 400), displays);
    Assert(frame.Width == 720 && frame.Height == 480, "QR frame default size");
    var resized = QrRecognitionFrameGeometry.Resize(frame, -1000, -1000, ResizeHandle.Top | ResizeHandle.Left);
    Assert(resized.Width >= QrRecognitionFrameGeometry.MinimumWidthDip && resized.Height >= QrRecognitionFrameGeometry.MinimumHeightDip, "QR frame minimum size");
    var scaledMinimum = QrRecognitionFrameGeometry.Resize(frame, -1000, -1000, ResizeHandle.Top | ResizeHandle.Left, 360, 240);
    Assert(scaledMinimum.Width >= 360 && scaledMinimum.Height >= 240, "QR frame DPI-scaled minimum size");
    var moved = QrRecognitionFrameGeometry.ClampToWorkArea(new System.Drawing.Rectangle(5000, 5000, 720, 480), displays, new System.Drawing.Point(100, 100));
    Assert(displays.Any(display => display.IntersectsWith(moved)), "QR frame clamps to visible display");
    var layoutHeader = QrRecognitionFrameGeometry.HeaderHeightPixels(1.25);
    var outerLayout = QrRecognitionFrameGeometry.CalculateLayout(
        new System.Drawing.Rectangle(100, 1200, 720, 200),
        [new System.Drawing.Rectangle(0, 0, 1920, 1440)],
        new System.Drawing.Point(200, 1200), layoutHeader);
    Assert(outerLayout.WindowBounds.Top == outerLayout.CaptureBounds.Top - layoutHeader,
        "QR frame title bar sits above the capture region");
    Assert(outerLayout.WindowBounds.Height == outerLayout.CaptureBounds.Height + layoutHeader,
        "QR frame outer height includes title bar");

    var movedAcrossDisplays = QrRecognitionFrameGeometry.Move(frame, -2500, 80);
    Assert(movedAcrossDisplays.Left == frame.Left - 2500 && movedAcrossDisplays.Top == frame.Top + 80,
        "QR frame drag remains in physical coordinates across displays");
    var staggered = new[]
    {
        new System.Drawing.Rectangle(-1600, 120, 1600, 900),
        new System.Drawing.Rectangle(0, 0, 1920, 1080)
    };
    var spanning = new System.Drawing.Rectangle(-300, 700, 900, 300);
    var layout = QrRecognitionFrameGeometry.CalculateLayout(spanning, staggered, new System.Drawing.Point(20, 800), 42);
    Assert(layout.TargetWorkArea == staggered[1], "QR frame selects display with largest intersection");
    Assert(layout.WindowBounds.Top == layout.CaptureBounds.Top - 42, "QR frame outer bounds preserve header offset");
}

static QrCaptureFrame CreateQrFrame(string text, int size)
{
    using var creator = new BarcodeCreator(BarcodeFormat.QRCode)
    {
        Options = "EcLevel=H"
    };
    using var barcode = creator.From(text);
    using var image = barcode.ToImage(new WriterOptions { Scale = -size, AddQuietZones = true });
    var monochrome = image.ToArray();
    var pixels = new byte[image.Width * image.Height * 4];
    for (var index = 0; index < monochrome.Length; index++)
    {
        var destination = index * 4;
        pixels[destination] = monochrome[index];
        pixels[destination + 1] = monochrome[index];
        pixels[destination + 2] = monochrome[index];
        pixels[destination + 3] = 255;
    }
    return new QrCaptureFrame(new System.Drawing.Rectangle(0, 0, image.Width, image.Height), pixels, image.Width * 4);
}

static QrCaptureFrame CreateStylizedQrFrame(string text, int size)
{
    var source = CreateQrFrame(text, size);
    var pixels = source.Pixels.ToArray();
    for (var y = 0; y < source.Height; y++)
    {
        for (var x = 0; x < source.Width; x++)
        {
            var index = y * source.Stride + x * 4;
            var dark = source.Pixels[index] < 128;
            var edge = dark && (x == 0 || y == 0 || x == source.Width - 1 || y == source.Height - 1 ||
                source.Pixels[y * source.Stride + Math.Max(0, x - 1) * 4] >= 128 ||
                source.Pixels[y * source.Stride + Math.Min(source.Width - 1, x + 1) * 4] >= 128 ||
                source.Pixels[Math.Max(0, y - 1) * source.Stride + x * 4] >= 128 ||
                source.Pixels[Math.Min(source.Height - 1, y + 1) * source.Stride + x * 4] >= 128);
            if (dark && !edge)
            {
                pixels[index] = (byte)(40 + x * 45 / source.Width);
                pixels[index + 1] = (byte)(20 + y * 35 / source.Height);
                pixels[index + 2] = (byte)(80 + (x + y) * 50 / (source.Width + source.Height));
            }
            else
            {
                var background = (byte)(230 + (x + y) * 25 / (source.Width + source.Height));
                pixels[index] = background;
                pixels[index + 1] = background;
                pixels[index + 2] = background;
            }
        }
    }

    var logoSize = Math.Max(16, source.Width / 10);
    var logoLeft = (source.Width - logoSize) / 2;
    var logoTop = (source.Height - logoSize) / 2;
    for (var y = logoTop; y < logoTop + logoSize; y++)
    {
        for (var x = logoLeft; x < logoLeft + logoSize; x++)
        {
            var index = y * source.Stride + x * 4;
            pixels[index] = 245;
            pixels[index + 1] = 245;
            pixels[index + 2] = 245;
        }
    }
    return new QrCaptureFrame(source.Bounds, pixels, source.Stride);
}

static QrCaptureFrame CreateSolidFrame(int width, int height, byte value, System.Drawing.Rectangle? bounds = null)
{
    var pixels = new byte[width * height * 4];
    Array.Fill(pixels, value);
    return new QrCaptureFrame(bounds ?? new System.Drawing.Rectangle(0, 0, width, height), pixels, width * 4);
}

static void CopyFrame(QrCaptureFrame source, QrCaptureFrame destination, int x, int y)
{
    for (var row = 0; row < source.Height; row++)
    {
        Buffer.BlockCopy(source.Pixels, row * source.Stride, destination.Pixels,
            (y + row) * destination.Stride + x * 4, source.Width * 4);
    }
}

static QrCaptureFrame Rotate90(QrCaptureFrame source)
{
    var rotated = CreateSolidFrame(source.Height, source.Width, 255);
    for (var y = 0; y < source.Height; y++)
    {
        for (var x = 0; x < source.Width; x++)
        {
            var destinationX = source.Height - 1 - y;
            var destinationY = x;
            Buffer.BlockCopy(source.Pixels, y * source.Stride + x * 4, rotated.Pixels,
                destinationY * rotated.Stride + destinationX * 4, 4);
        }
    }
    return rotated;
}

static QrCaptureFrame Invert(QrCaptureFrame source)
{
    var pixels = source.Pixels.ToArray();
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = (byte)(255 - pixels[index]);
        pixels[index + 1] = (byte)(255 - pixels[index + 1]);
        pixels[index + 2] = (byte)(255 - pixels[index + 2]);
    }
    return new QrCaptureFrame(source.Bounds, pixels, source.Stride);
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
    var newPath = Path.Combine(root, "new.txt");
    var newGamePath = Path.Combine(root, "new-game.url");
    File.WriteAllText(lockedPath, "locked");
    File.WriteAllText(newPath, "new");
    File.WriteAllText(newGamePath, "[InternetShortcut]\nURL=steam://rungameid/123\n");
    var lockedTab = new LayoutTab { Title = "locked", IsRuleLocked = true, PinnedPaths = [lockedPath] };
    var lockedGroup = new GroupDefinition { Tabs = [lockedTab] };
    var rule = new LayoutMatchRule { GroupId = lockedTab.Id.ToString(), Extensions = ".txt", Priority = 1 };
    var result = new LayoutAssignmentService().Preview([newPath], [lockedGroup], [rule]);
    Assert(result.Count == 1 && result[0].TabId == lockedTab.Id,
        "locked layout accepts a new rule target");
    var gameRule = new LayoutMatchRule
    {
        GroupId = lockedTab.Id.ToString(),
        Extensions = ".exe;.lnk;.url",
        PathContains = "steam:"
    };
    var gameResult = new LayoutAssignmentService().Preview([newGamePath], [lockedGroup], [gameRule]);
    Assert(gameResult.Count == 1 && gameResult[0].TabId == lockedTab.Id,
        "locked layout accepts a new Steam URL target");
    var lockedPaths = lockedTab.PinnedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var reapplyCandidates = new[] { lockedPath, newPath }.Where(path => !lockedPaths.Contains(path));
    var reapplyResult = new LayoutAssignmentService().Preview(reapplyCandidates, [lockedGroup], [rule]);
    Assert(reapplyResult.Count == 1 && reapplyResult[0].Path == newPath,
        "locked layout contents remain excluded from reassignment");
    var unlockedGroup = new GroupDefinition { Title = "unlocked" };
    var unlockedRule = new LayoutMatchRule { GroupId = unlockedGroup.Id.ToString(), Extensions = ".txt" };
    Assert(new LayoutAssignmentService().Preview([newPath], [unlockedGroup], [unlockedRule]).Count == 1,
        "unlocked layout remains a rule target");
    var lockedOrdinary = new GroupDefinition { IsRuleLocked = true };
    var ordinaryRule = new LayoutMatchRule { GroupId = lockedOrdinary.Id.ToString(), Extensions = ".txt" };
    Assert(new LayoutAssignmentService().Preview([newPath], [lockedOrdinary], [ordinaryRule]).Count == 1,
        "locked ordinary layout accepts a new rule target");

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
            MemoHotKey = "Ctrl+Alt+M",
            MemoWindowBounds = new MemoWindowBounds(-600, 140, 720, 560),
            MemoCaretOffset = 42,
            QrRecognitionHotKey = "Ctrl+Shift+Q",
            QrRecognitionFrameBounds = new QrRecognitionFrameBounds(-800, 120, 720, 480),
            TopmostHotKeys = [new TopmostHotKeyBinding { Gesture = "Ctrl+Alt+P", LayoutIds = [tab.Id] }]
        },
        Groups = [group]
    };
    var store = new LayoutStore(directory);
    await store.SaveAsync(state);
    var loaded = await store.LoadAsync();
    Assert(loaded.Settings.InteractionMode == LayoutInteractionMode.EdgeHide, "edge interaction mode persists");
    Assert(loaded.Settings.MemoHotKey == "Ctrl+Alt+M" && loaded.Settings.MemoWindowBounds?.Left == -600 && loaded.Settings.MemoCaretOffset == 42,
        "memo hotkey, window bounds and caret persist");
    Assert(loaded.Settings.QrRecognitionHotKey == "Ctrl+Shift+Q", "QR recognition hotkey persists");
    Assert(loaded.Settings.QrRecognitionFrameBounds?.Left == -800, "QR frame bounds persist");
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

    var legacyQrDirectory = Path.Combine(root, "legacy-qr-hotkey-state");
    Directory.CreateDirectory(legacyQrDirectory);
    await File.WriteAllTextAsync(Path.Combine(legacyQrDirectory, "layout.json"), "{\"Version\":12,\"Settings\":{\"TopmostHotKeys\":[]}}");
    var legacyQr = await new LayoutStore(legacyQrDirectory).LoadAsync();
    Assert(legacyQr.Settings.QrRecognitionHotKey == string.Empty, "missing QR hotkey migrates to disabled");
    Assert(legacyQr.Settings.QrRecognitionFrameBounds is null, "legacy QR frame bounds migrate to empty");
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
    var cache = ShellIconService.GetCacheDiagnostics();
    Assert(cache.EntryCount <= cache.MaxEntries, "shell icon cache entry limit");
    Assert(cache.EstimatedBytes <= cache.MaxEstimatedBytes, "shell icon cache byte limit");
}

static void TestMemoryDiagnostics(string root)
{
    var entryPath = Path.Combine(root, "memory-entry.txt");
    File.WriteAllText(entryPath, "memory");
    var entry = new FileEntry("memory-entry.txt", entryPath, false);
    var metadataTask = typeof(FileEntry).GetField("_metadataLoadTask",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    entry.EnsureIconLoadedOnly();
    Assert(metadataTask?.GetValue(entry) is null,
        "icon-only visible data loading does not start metadata work");
    entry.EnsureVisibleDataLoaded();
    Assert(metadataTask?.GetValue(entry) is not null,
        "full visible data loading retains metadata compatibility");
    var icon = ShellIconService.GetIcon(entryPath, isDirectory: false);
    var property = typeof(FileEntry).GetProperty(nameof(FileEntry.IconSource));
    property?.SetValue(entry, icon);
    entry.ReleaseVisibleIcon();
    Assert(entry.IconSource is null, "file entry releases recycled visible icon");
    property?.SetValue(entry, icon);
    entry.Dispose();
    Assert(entry.IconSource is null, "file entry dispose clears icon");

    var snapshot = MemoryDiagnosticsService.Capture("smoke", 2, 3);
    Assert(snapshot.WorkingSetBytes > 0 && snapshot.WorkingSetPrivateBytes > 0 && snapshot.PrivateMemoryBytes > 0,
        "memory snapshot process values");
    Assert(snapshot.WorkingSetPrivateBytes <= snapshot.WorkingSetBytes,
        "memory snapshot private working set is bounded by working set");
    Assert(snapshot.ManagedHeapBytes >= 0 && snapshot.GcCommittedBytes >= 0, "memory snapshot GC values");
    Assert(snapshot.DesktopPresentationMode is "Native" or "Wpf", "memory snapshot desktop presentation mode");
    Assert(snapshot.LayoutWindowCount == 2 && snapshot.FileEntryCount == 3, "memory snapshot layout counts");
    Assert(snapshot.IconCache.EntryCount <= snapshot.IconCache.MaxEntries, "memory snapshot cache count");
    Assert(snapshot.ToLogMessage().Contains("phase=smoke", StringComparison.Ordinal), "memory snapshot log format");
    Assert(!MemoryDiagnosticsService.TrimNativeDesktopWorkingSet(),
        "normal WPF presentation never performs native desktop working-set trim");
}

static void TestNativeDesktopLayout()
{
    var items = Enumerable.Range(0, 10)
        .Select(index => new DesktopPresentationItem(
            $"item-{index}", $"C:\\item-{index}", false, "File", "1 KB", "today", false))
        .ToArray();
    var snapshot = new DesktopPresentationSnapshot(
        Guid.NewGuid(), "native layout", GroupKind.Empty, LayoutViewMode.MediumIcons,
        false, 0, [], items);

    var first = NativeDesktopLayout.GetItemRect(snapshot, 0, 200);
    var third = NativeDesktopLayout.GetItemRect(snapshot, 2, 200);
    Assert(first.Top == NativeDesktopLayout.HeaderHeight, "native layout header offset");
    Assert(third.Top > first.Top, "native layout wraps items into following row");
    Assert(NativeDesktopLayout.HitTestIndex(snapshot, first.Left + 1, first.Top + 1, 200) == 0,
        "native layout hit test identifies item");
    Assert(NativeDesktopLayout.HitTestIndex(snapshot, 1, 1, 200) == -1,
        "native layout excludes title bar from item hit testing");
    var contentHeight = NativeDesktopLayout.GetContentHeight(snapshot, 200);
    Assert(NativeDesktopLayout.ClampScrollOffset(snapshot, int.MaxValue, 200, 100) == contentHeight - 100,
        "native layout clamps scroll offset to content boundary");
    Assert(NativeDesktopLayout.ClampScrollOffset(snapshot, -1, 200, 100) == 0,
        "native layout clamps negative scroll offset");
}

static void TestDesktopSelectionState()
{
    var state = new DesktopSelectionState();
    var paths = new[] { "C:\\one", "C:\\two", "C:\\three", "C:\\four" };
    var changes = 0;
    state.Changed += (_, _) => changes++;

    state.Select(paths[1], toggle: false, extend: false, paths);
    Assert(state.Paths.SetEquals([paths[1]]) && state.FocusedPath == paths[1],
        "selection state stores single selection and focus");
    state.Select(paths[3], toggle: false, extend: true, paths);
    Assert(state.Paths.SetEquals([paths[1], paths[2], paths[3]]) && state.FocusedPath == paths[3],
        "selection state creates shift range without WPF containers");
    state.Select(paths[2], toggle: true, extend: false, paths);
    Assert(!state.Paths.Contains(paths[2]) && state.Paths.Count == 2,
        "selection state toggles an item");
    state.Replace([paths[0]], paths[0]);
    Assert(state.Paths.SetEquals([paths[0]]) && changes >= 4,
        "selection state signals independent presentation updates");
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
            using (var nativeWindow = new NativeDesktopWindow(cornerRadius: 8))
            {
                string? selectedPath = null;
                int? navigationKey = null;
                string? draggedPath = null;
                var layoutMenuRequested = false;
                var boundsChanged = 0;
                var nativeDefinition = new GroupDefinition { Title = "native persistence" };
                using var nativeController = new GroupContainer(nativeDefinition, animationsEnabled: false);
                nativeWindow.Create(new NativeDesktopBounds(-12000, -12000, 360, 240));
                nativeWindow.Update(new DesktopPresentationSnapshot(
                    Guid.NewGuid(), "native smoke", GroupKind.Empty, LayoutViewMode.MediumIcons,
                    false, 0, [],
                    [new DesktopPresentationItem("item", "C:\\native-smoke", false, "File", "1 KB", "today", false)]));
                nativeWindow.ItemSelectionRequested += (path, _, _) => selectedPath = path;
                nativeWindow.NavigationRequested += (key, _, _) => navigationKey = key;
                nativeWindow.ItemDragRequested += path => draggedPath = path;
                nativeWindow.LayoutMenuRequested += _ => layoutMenuRequested = true;
                nativeWindow.BoundsChanged += (_, _) => boundsChanged++;
                nativeController.BindNativeDesktopWindow(nativeWindow);
                nativeWindow.Update(new DesktopPresentationSnapshot(
                    Guid.NewGuid(), "native smoke", GroupKind.Empty, LayoutViewMode.MediumIcons,
                    false, 0, [],
                    [new DesktopPresentationItem("item", "C:\\native-smoke", false, "File", "1 KB", "today", false)]));
                nativeWindow.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(40));
                Assert(nativeWindow.IsVisible && nativeWindow.Handle != nint.Zero,
                    "top-level native desktop window creates and shows its own HWND");
                var nativeStyle = TestNativeMethods.GetWindowLongPtr(nativeWindow.Handle, -20).ToInt64();
                Assert((nativeStyle & 0x00000080L) != 0 && (nativeStyle & 0x00000008L) == 0,
                    "top-level native desktop window remains a non-topmost tool window");
                TestNativeMethods.SendMessage(nativeWindow.Handle, 0x0201, nint.Zero, new nint((52 << 16) | 12));
                TestNativeMethods.SendMessage(nativeWindow.Handle, 0x0100, new nint(0x28), nint.Zero);
                Assert(selectedPath == "C:\\native-smoke" && navigationKey == 0x28,
                    "top-level native desktop window dispatches selection and keyboard navigation");
                TestNativeMethods.SendMessage(nativeWindow.Handle, 0x0201, nint.Zero, new nint((52 << 16) | 12));
                TestNativeMethods.SendMessage(nativeWindow.Handle, 0x0200, new nint(0x0001), new nint((68 << 16) | 30));
                Assert(draggedPath == "C:\\native-smoke", "top-level native desktop window dispatches item drag start");
                TestNativeMethods.SendMessage(nativeWindow.Handle, 0x0205, nint.Zero, new nint((20 << 16) | 320));
                Assert(layoutMenuRequested, "top-level native desktop window dispatches blank-area layout menu");
                var firstTab = Guid.NewGuid();
                var secondTab = Guid.NewGuid();
                Guid? activatedTab = null;
                nativeWindow.TabActivated += tab => activatedTab = tab;
                nativeWindow.Update(new DesktopPresentationSnapshot(
                    Guid.NewGuid(), "native tabs", GroupKind.Empty, LayoutViewMode.MediumIcons,
                    false, 0,
                    [new DesktopPresentationTab(firstTab, "first", true), new DesktopPresentationTab(secondTab, "second", false)],
                    []));
                TestNativeMethods.SendMessage(nativeWindow.Handle, 0x0201, nint.Zero, new nint((12 << 16) | 116));
                Assert(activatedTab == secondTab, "top-level native desktop window dispatches tab activation");
                nativeWindow.SetBounds(new NativeDesktopBounds(-12020, -12010, 380, 260));
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                Assert(nativeWindow.Bounds.Width == 380 && nativeWindow.Bounds.Height == 260 && boundsChanged > 0,
                    "top-level native desktop window reports persisted move and resize bounds");
                Assert(nativeDefinition.DesktopX == -12020 && nativeDefinition.DesktopY == -12010 &&
                    nativeDefinition.Width == 380 && nativeDefinition.Height == 260,
                    "native desktop window bounds flow through existing layout persistence model");
                nativeWindow.DockEdge = DockEdge.Left;
                nativeWindow.HideToEdge();
                Assert(nativeWindow.IsEdgeHidden, "top-level native desktop window hides to configured edge");
                nativeWindow.RevealFromEdge();
                Assert(!nativeWindow.IsEdgeHidden, "top-level native desktop window reveals from configured edge");
                var dpiSuggestedBounds = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int) * 4);
                try
                {
                    System.Runtime.InteropServices.Marshal.WriteInt32(dpiSuggestedBounds, 0, -12040);
                    System.Runtime.InteropServices.Marshal.WriteInt32(dpiSuggestedBounds, sizeof(int), -12030);
                    System.Runtime.InteropServices.Marshal.WriteInt32(dpiSuggestedBounds, sizeof(int) * 2, -11620);
                    System.Runtime.InteropServices.Marshal.WriteInt32(dpiSuggestedBounds, sizeof(int) * 3, -11750);
                    TestNativeMethods.SendMessage(nativeWindow.Handle, 0x02E0, nint.Zero, dpiSuggestedBounds);
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(dpiSuggestedBounds); }
                Assert(nativeWindow.Bounds.Width == 420 && nativeWindow.Bounds.Height == 280,
                    "top-level native desktop window applies WM_DPICHANGED suggested bounds");
                nativeWindow.Hide();
                Assert(!nativeWindow.IsVisible, "top-level native desktop window hides without WPF host");
            }
            var controllerDefinition = new GroupDefinition { Title = "native controller", Width = 320, Height = 200 };
            using (var controllerGroup = new ZDesk.Controls.GroupContainer(controllerDefinition, animationsEnabled: false))
            using (var controller = new ZDesk.Services.NativeDesktopWindowController(
                       controllerGroup, new ZDesk.Windows.NativeDesktopBounds(-12100, -12100, 320, 200)))
            {
                controller.Show();
                controller.Refresh();
                Assert(controller.Window.IsVisible && controller.Window.Handle != nint.Zero,
                    "native desktop controller owns and refreshes an independent window");
            }
            DesktopRenderingOptions.UseNativeTopLevel = true;
            try
            {
                using var detachedHostGroup = new ZDesk.Controls.GroupContainer(
                    new GroupDefinition { Title = "native detached host" }, animationsEnabled: false);
                var detachedList = (System.Windows.Controls.ListBox?)detachedHostGroup.FindName("FileList");
                Assert(detachedList is null || detachedList.ItemsSource is null,
                    "top-level native presentation does not bind the detached WPF file list");
                Assert(detachedList is null || detachedList.Parent is null,
                    "top-level native presentation does not mount the detached WPF list control");
            }
            finally
            {
                DesktopRenderingOptions.UseNativeTopLevel = false;
            }
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
            var qrHotKey = (System.Windows.Controls.TextBox)settingsWindow.FindName("QrRecognitionHotKeyTextBox");
            var topmostHotKeyCard = (System.Windows.Controls.Border)settingsWindow.FindName("HotKeySettingsCard");
            var applyButton = (System.Windows.Controls.Button)settingsWindow.FindName("ApplyButton");
            Assert(IsDarkSurface(standardMode.Background), "settings mode selector uses dark themed surface");
            Assert(IsDarkSurface(hotKeysGrid.Background), "settings hotkey grid does not fall back to white system surface");
            Assert(IsDarkSurface(hotKeyTargets.Background), "settings target list does not fall back to white system surface");
            Assert(IsDarkSurface(qrHotKey.Background), "settings QR hotkey editor does not fall back to white system surface");
            Assert(settingsWindow.NormalLayoutChoices.Count == 1 &&
                settingsWindow.NormalLayoutChoices[0].Id == definition.Id.ToString(),
                "folder mapping layouts are excluded from rule targets");
            Assert(settingsWindow.ResultLayoutRules[0].GroupId == definition.Id.ToString(),
                "invalid rule target is repaired to an ordinary layout");
            Assert(!applyButton.IsEnabled, "settings apply starts disabled");
            standardMode.IsChecked = false;
            ((System.Windows.Controls.RadioButton)settingsWindow.FindName("EdgeHideModeRadio")).IsChecked = true;
            Assert(applyButton.IsEnabled, "interaction mode changes mark settings dirty");
            Assert(!topmostHotKeyCard.IsEnabled && qrHotKey.IsEnabled, "QR hotkey stays available in edge-hide mode");
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

            var overlayFrame = CreateSolidFrame(120, 120, 255, new System.Drawing.Rectangle(-12500, -12500, 120, 120));
            var overlay = new QrSelectionOverlayWindow(overlayFrame.Bounds, ScreenCaptureService.ToBitmapSource(overlayFrame))
            {
                Opacity = 0
            };
            overlay.Show();
            overlay.ShowSelection(new System.Drawing.Rectangle(-12470, -12460, 50, 40));
            var mask = (System.Windows.Shapes.Rectangle)overlay.FindName("MaskLayer");
            var selectionSnapshot = (System.Windows.Controls.Image)overlay.FindName("SelectionSnapshot");
            var selectionBorder = (System.Windows.Controls.Border)overlay.FindName("SelectionBorder");
            Assert(mask.Fill is System.Windows.Media.SolidColorBrush { Color.A: 0x59 }, "QR capture mask uses 35 percent opacity");
            Assert(selectionSnapshot.Visibility == System.Windows.Visibility.Visible &&
                selectionSnapshot.Clip is System.Windows.Media.RectangleGeometry { Rect.Width: > 0, Rect.Height: > 0 },
                "QR capture selection reveals the frozen screenshot inside the border");
            Assert(selectionBorder.Visibility == System.Windows.Visibility.Visible, "QR capture selection border is visible");
            overlay.ClearSelection();
            Assert(selectionSnapshot.Visibility == System.Windows.Visibility.Collapsed && selectionSnapshot.Clip is null &&
                selectionBorder.Visibility == System.Windows.Visibility.Collapsed, "QR capture selection clears without closing the overlay");
            overlay.Close();

            var frameWindow = new QrRecognitionFrameWindow(new System.Drawing.Rectangle(-12600, -12600, 320, 240))
            {
                Opacity = 0
            };
            frameWindow.Show();
            var captureBounds = new System.Drawing.Rectangle(-12600, -12600, 320, 240);
            var outerBounds = new System.Drawing.Rectangle(-12600, -12634, 320, 274);
            frameWindow.SetFrameBounds(captureBounds, outerBounds, 34);
            var frameRoot = (System.Windows.Controls.Grid)frameWindow.FindName("Root");
            var titleBar = (System.Windows.Controls.Border)frameWindow.FindName("TitleBar");
            var captureRoot = (System.Windows.Controls.Grid)frameWindow.FindName("CaptureRoot");
            var border = captureRoot.Children.OfType<System.Windows.Controls.Border>().Single();
            var sizeText = (System.Windows.Controls.TextBlock)frameWindow.FindName("SizeText");
            var recognizeButton = (System.Windows.Controls.Button)frameWindow.FindName("RecognizeButton");
            var closeButton = (System.Windows.Controls.Button)frameWindow.FindName("CloseButton");
            Assert(titleBar.Background is System.Windows.Media.SolidColorBrush titleBrush && titleBrush.Color.R == 0x18,
                "QR frame uses a VS Code dark title bar");
            Assert(titleBar.Child is System.Windows.Controls.Grid &&
                ((System.Windows.Controls.Grid)titleBar.Child).Children.OfType<System.Windows.Controls.StackPanel>()
                    .SelectMany(panel => panel.Children.OfType<System.Windows.Controls.TextBlock>())
                    .Any(text => text.Text == "二维码识别"), "QR frame title is shown in the upper left");
            Assert(sizeText.Text == "320 × 240 px", "QR frame shows physical pixel dimensions");
            Assert(frameRoot.ActualWidth > 0 && frameRoot.ActualHeight > 0 && captureRoot.ActualWidth > 0,
                "single QR frame maintains outer and capture bounds");
            Assert(border.BorderThickness.Left == 1 && captureRoot.Children.OfType<System.Windows.Controls.Border>().Count() == 1,
                "QR frame uses one continuous border without corner blocks");
            Assert(recognizeButton.Width == 62 && closeButton.Width == 30,
                "single QR frame reserves primary and close-button space");
            Assert((string)recognizeButton.ToolTip == "识别二维码 (Enter)" && (string)closeButton.ToolTip == "退出取景框 (Esc)",
                "single QR frame exposes recognize and exit actions");
            Assert(frameWindow.WindowBounds.Top == frameWindow.FrameBounds.Top - 34,
                "single QR frame keeps title bar outside capture bounds");
            frameWindow.Close();

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
            selectionWindow.HideAnimated();
            PumpDispatcher(TimeSpan.FromMilliseconds(360));
            Assert(!selectionWindow.IsVisualTreeAttached && selectionWindow.Content is null,
                "hidden layout detaches its WPF visual tree");
            selectionWindow.ShowAnimated();
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            Assert(selectionWindow.IsVisualTreeAttached && ReferenceEquals(selectionWindow.Content, selectionWindow.Group),
                "shown layout reattaches its existing WPF visual tree");
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
