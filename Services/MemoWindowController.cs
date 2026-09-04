using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;
using ZDesk.Models;
using ZDesk.Windows;

namespace ZDesk.Services;

/// <summary>Lazy lifecycle, notebook navigation and persistence coordinator.</summary>
public sealed class MemoWindowController : IDisposable
{
    private readonly Window _owner;
    private readonly MemoNotebookStore _store;
    private readonly Func<MemoWindowBounds?> _loadBounds;
    private readonly Action<MemoWindowBounds> _saveBounds;
    private readonly Func<int> _loadLegacyCaretOffset;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private MemoWindow? _window;
    private DispatcherTimer? _saveTimer;
    private MemoNotebookIndex? _index;
    private MemoNoteMetadata? _currentNote;
    private bool _notebookLoaded;
    private bool _noteLoaded;
    private bool _dirty;
    private bool _settingInitialBounds;
    private bool _disposing;
    private long _changeVersion;
    private MemoWindowBounds? _currentBounds;

    public bool IsVisible => _window?.IsVisible == true;
    public bool IsActive => _window is not null;

    public MemoWindowController(
        Window owner,
        MemoNotebookStore store,
        Func<MemoWindowBounds?> loadBounds,
        Action<MemoWindowBounds> saveBounds,
        Func<int> loadLegacyCaretOffset)
    {
        _owner = owner;
        _store = store;
        _loadBounds = loadBounds;
        _saveBounds = saveBounds;
        _loadLegacyCaretOffset = loadLegacyCaretOffset;
    }

    public async Task ToggleAsync()
    {
        if (_disposing) return;
        if (IsVisible)
        {
            await HideAsync();
            return;
        }
        await ShowAsync();
    }

    public async Task ShowAsync()
    {
        if (_disposing) return;
        EnsureWindow();
        if (!_window!.IsVisible)
        {
            var bounds = _currentBounds ?? LoadOrCreateBounds();
            _settingInitialBounds = true;
            try { _window.Show(); }
            finally { _settingInitialBounds = false; }
            var clamped = ClampToWorkArea(bounds.ToRectangle());
            _currentBounds = new MemoWindowBounds(clamped.Left, clamped.Top, clamped.Width, clamped.Height);
            _window.SetPhysicalBounds(clamped);
        }

        await EnsureNotebookLoadedAsync();
        _window.ShowList(clearSearch: true);
        _window.Topmost = true;
        _window.Activate();
        _window.FocusList();
    }

    public async Task HideAsync()
    {
        if (_window is null) return;
        await FlushAsync();
        // The list cards are an in-memory projection of the index. Refresh it
        // before hiding so the next shortcut invocation immediately shows the
        // title, summary, timestamp and thumbnail produced by the latest save.
        await RefreshCardsAsync();
        _currentNote = null;
        _noteLoaded = false;
        _window.ShowList(clearSearch: true);
        SaveBounds();
        if (_window.IsVisible) _window.Hide();
    }

    public async Task FlushAsync()
    {
        _saveTimer?.Stop();
        await _flushGate.WaitAsync();
        try
        {
            if (_window is null || !_noteLoaded || _currentNote is null || !_dirty) return;
            await SaveCurrentCoreAsync();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void SetDataDirectory(string directory) => _store.SetDataDirectory(directory);

    public void RefreshDisplayLayout()
    {
        if (_window is null) return;
        var current = _window.GetPhysicalBounds().ToRectangle();
        var clamped = ClampToWorkArea(current);
        _currentBounds = new MemoWindowBounds(clamped.Left, clamped.Top, clamped.Width, clamped.Height);
        _window.SetPhysicalBounds(clamped);
        SaveBounds();
    }

    private async Task EnsureNotebookLoadedAsync()
    {
        if (_notebookLoaded) return;
        var result = await _store.LoadAsync();
        _index = result.Index;
        _notebookLoaded = true;

        if (result.RebuiltFromPackages)
            await PopulateRebuiltMetadataAsync();
        if (_index.Notes.Count == 0)
            await TryMigrateLegacyAsync();

        if (result.RebuiltFromPackages || result.RecoveredFromBackup || _index.Notes.Count > 0 && !File.Exists(_store.IndexFile))
            await _store.SaveIndexAsync(_index, createBackup: !result.RebuiltFromPackages && !result.RecoveredFromBackup);

        await RefreshCardsAsync();
        if (!string.IsNullOrWhiteSpace(result.Warning)) _window?.SetStatus(result.Warning);
    }

    private async Task TryMigrateLegacyAsync()
    {
        if (_index is null || _window is null) return;
        var legacy = await _store.LoadLegacyAsync();
        if (legacy.Primary is null && legacy.Backup is null) return;

        var package = legacy.Primary;
        _window.ResetDocument();
        var loaded = package is not null && _window.LoadPackage(package);
        if (!loaded && package is not null)
        {
            await _store.PreserveCorruptLegacyAsync();
        }
        if (!loaded && legacy.Backup is not null)
        {
            package = legacy.Backup;
            _window.ResetDocument();
            loaded = _window.LoadPackage(package);
        }
        if (!loaded || package is null)
        {
            _window.ResetDocument();
            await _store.ArchiveLegacyAsync();
            _window.SetStatus("旧版备忘录无法读取，已保留原文件");
            return;
        }

        var snapshot = _window.CaptureContentSnapshot();
        var note = new MemoNoteMetadata
        {
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            CaretOffset = Math.Max(0, _loadLegacyCaretOffset()),
            Title = snapshot.Title,
            PreviewText = snapshot.PreviewText,
            SearchText = snapshot.SearchText,
            HasPreviewImage = snapshot.PreviewImagePng is not null
        };
        await _store.SaveNoteAsync(note, package, snapshot.PreviewImagePng);
        _index.Notes.Add(note);
        await _store.SaveIndexAsync(_index);
        await _store.ArchiveLegacyAsync();
        _window.ResetDocument();
    }

    private async Task PopulateRebuiltMetadataAsync()
    {
        if (_index is null || _window is null) return;
        var validNotes = new List<MemoNoteMetadata>(_index.Notes.Count);
        foreach (var note in _index.Notes)
        {
            _window.ResetDocument();
            var packages = await _store.LoadNoteAsync(note.Id);
            var loaded = packages.Primary is not null && _window.LoadPackage(packages.Primary);
            if (!loaded && packages.Primary is not null)
                await _store.PreserveCorruptNoteAsync(note);
            if (!loaded && packages.Backup is not null)
            {
                _window.ResetDocument();
                loaded = _window.LoadPackage(packages.Backup);
            }
            if (!loaded) continue;
            var snapshot = _window.CaptureContentSnapshot();
            note.Title = snapshot.Title;
            note.PreviewText = snapshot.PreviewText;
            note.SearchText = snapshot.SearchText;
            note.HasPreviewImage = snapshot.PreviewImagePng is not null;
            validNotes.Add(note);
        }
        _index.Notes = validNotes.OrderByDescending(note => note.UpdatedAtUtc).ToList();
        _window.ResetDocument();
    }

    private async Task RefreshCardsAsync()
    {
        if (_window is null || _index is null) return;
        var cards = new List<MemoNoteCard>(_index.Notes.Count);
        foreach (var note in _index.Notes.OrderByDescending(note => note.UpdatedAtUtc))
        {
            var bytes = note.HasPreviewImage ? await _store.LoadPreviewAsync(note) : null;
            cards.Add(new MemoNoteCard(note, DecodePreview(bytes)));
        }
        _window.SetNotes(cards);
    }

    private async void Window_NewNoteRequested()
    {
        try { await CreateNoteAsync(); }
        catch (Exception ex) when (IsExpectedPersistenceFailure(ex)) { HandlePersistenceError("新建便笺失败", ex); }
    }

    private async Task CreateNoteAsync()
    {
        if (_index is null || _window is null) return;
        await FlushAsync();
        var now = DateTime.UtcNow;
        var note = new MemoNoteMetadata { CreatedAtUtc = now, UpdatedAtUtc = now };
        _index.Notes.Insert(0, note);
        _currentNote = note;
        _noteLoaded = true;
        _dirty = true;
        _window.ResetDocument();
        _window.ShowDetail(note);
        _window.FocusEditor();
        await SaveCurrentCoreAsync();
        await RefreshCardsAsync();
    }

    private async void Window_NoteSelected(Guid id)
    {
        try { await OpenNoteAsync(id); }
        catch (Exception ex) when (IsExpectedPersistenceFailure(ex)) { HandlePersistenceError("打开便笺失败", ex); }
    }

    private async Task OpenNoteAsync(Guid id)
    {
        if (_index is null || _window is null) return;
        var note = _index.Notes.FirstOrDefault(candidate => candidate.Id == id);
        if (note is null) return;
        if (_currentNote?.Id == id && _noteLoaded) return;
        await FlushAsync();

        var package = await _store.LoadNoteAsync(id);
        _window.ResetDocument();
        var loaded = package.Primary is not null && _window.LoadPackage(package.Primary);
        var recovered = false;
        if (!loaded && package.Primary is not null)
        {
            await _store.PreserveCorruptNoteAsync(note);
        }
        if (!loaded && package.Backup is not null)
        {
            _window.ResetDocument();
            loaded = _window.LoadPackage(package.Backup);
            recovered = loaded;
        }
        if (!loaded)
        {
            _window.ResetDocument();
            _window.SetStatus("便笺正文无法读取，已打开空白内容");
        }

        _currentNote = note;
        _noteLoaded = true;
        _dirty = recovered || !loaded;
        _window.ShowDetail(note);
        _window.FocusEditor(note.CaretOffset);
        if (recovered)
        {
            await SaveCurrentCoreAsync(createBackup: false);
            _window.SetStatus("主正文损坏，已从备份恢复");
        }
    }

    private async void Window_BackRequested()
    {
        try { await BackToListAsync(); }
        catch (Exception ex) when (IsExpectedPersistenceFailure(ex)) { HandlePersistenceError("返回笔记列表失败", ex); }
    }

    private async Task BackToListAsync()
    {
        await FlushAsync();
        _currentNote = null;
        _noteLoaded = false;
        _window?.ShowList();
        await RefreshCardsAsync();
    }

    private async void Window_NoteDeleteRequested(Guid id)
    {
        try { await DeleteNoteAsync(id); }
        catch (Exception ex) when (IsExpectedPersistenceFailure(ex)) { HandlePersistenceError("删除便笺失败", ex); }
    }

    private async Task DeleteNoteAsync(Guid id)
    {
        if (_index is null || _window is null) return;
        var note = _index.Notes.FirstOrDefault(candidate => candidate.Id == id);
        if (note is null) return;
        var title = string.IsNullOrWhiteSpace(note.Title) ? "无标题便笺" : note.Title;
        var confirmation = new MemoDeleteConfirmWindow(title) { Owner = _window };
        if (confirmation.ShowDialog() != true) return;

        if (_currentNote?.Id == id) await FlushAsync();
        _index.Notes.Remove(note);
        await _store.SaveIndexAsync(_index);
        await _store.DeleteNoteAsync(note);
        _currentNote = null;
        _noteLoaded = false;
        _dirty = false;
        _window.ShowList();
        await RefreshCardsAsync();
        _window.SetStatus("便笺已永久删除");
    }

    private void Window_HideRequested()
    {
        _ = HideFromWindowAsync();
    }

    private async Task HideFromWindowAsync()
    {
        try { await HideAsync(); }
        catch (Exception ex) when (IsExpectedPersistenceFailure(ex)) { HandlePersistenceError("保存便笺失败", ex); }
    }

    private void Window_DocumentChanged()
    {
        if (_disposing || _currentNote is null) return;
        _changeVersion++;
        _dirty = true;
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTimer_Tick;
        _saveTimer.Tick += SaveTimer_Tick;
        _saveTimer.Start();
        _window?.SetStatus("有未保存的更改…");
    }

    private async void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        try { await FlushAsync(); }
        catch (Exception ex) when (IsExpectedPersistenceFailure(ex)) { HandlePersistenceError("自动保存失败", ex); }
    }

    private async Task SaveCurrentCoreAsync(bool createBackup = true)
    {
        if (_window is null || _currentNote is null || !_noteLoaded || _index is null) return;
        var version = _changeVersion;
        try
        {
            var snapshot = _window.CaptureContentSnapshot();
            _currentNote.Title = snapshot.Title;
            _currentNote.PreviewText = snapshot.PreviewText;
            _currentNote.SearchText = snapshot.SearchText;
            _currentNote.CaretOffset = snapshot.CaretOffset;
            _currentNote.HasPreviewImage = snapshot.PreviewImagePng is not null;
            _currentNote.UpdatedAtUtc = DateTime.UtcNow;
            await _store.SaveNoteAsync(_currentNote, _window.SavePackage(), snapshot.PreviewImagePng, createBackup);
            await _store.SaveIndexAsync(_index);
            if (_changeVersion == version) _dirty = false;
            _window.UpdateDetailTitle(_currentNote.Title);
            _window.SetStatus("已保存");
        }
        catch
        {
            _dirty = true;
            throw;
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;
        _window = new MemoWindow { Owner = _owner };
        _window.DocumentChanged += Window_DocumentChanged;
        _window.BoundsChanged += Window_BoundsChanged;
        _window.CaretChanged += Window_CaretChanged;
        _window.HideRequested += Window_HideRequested;
        _window.NewNoteRequested += Window_NewNoteRequested;
        _window.NoteSelected += Window_NoteSelected;
        _window.NoteDeleteRequested += Window_NoteDeleteRequested;
        _window.BackRequested += Window_BackRequested;
        _window.Closed += (_, _) => SaveBounds();
    }

    private void Window_BoundsChanged(MemoWindowBounds bounds)
    {
        if (_settingInitialBounds) return;
        _currentBounds = bounds;
        SaveBounds();
    }

    private void Window_CaretChanged(int offset)
    {
        if (_currentNote is null) return;
        var normalized = Math.Max(0, offset);
        if (_currentNote.CaretOffset == normalized) return;
        _currentNote.CaretOffset = normalized;
        // Caret context is part of the persisted note metadata even when the
        // document text itself did not change.
        Window_DocumentChanged();
    }

    private void SaveBounds()
    {
        if (_window is null) return;
        _currentBounds ??= _window.GetPhysicalBounds();
        _saveBounds(_currentBounds);
    }

    private MemoWindowBounds LoadOrCreateBounds()
    {
        var saved = _loadBounds();
        if (saved is { Width: >= 480, Height: >= 320 }) return saved;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var width = Math.Min(720, area.Width - 24);
        var height = Math.Min(560, area.Height - 24);
        return new MemoWindowBounds(area.Left + Math.Max(12, (area.Width - width) / 2), area.Top + Math.Max(12, (area.Height - height) / 2), width, height);
    }

    private static BitmapSource? DecodePreview(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static Rectangle ClampToWorkArea(Rectangle bounds)
    {
        var areas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
        if (areas.Length == 0) return bounds;
        var target = areas.OrderByDescending(area => IntersectionArea(area, bounds)).First();
        var width = Math.Min(Math.Max(480, bounds.Width), Math.Max(480, target.Width - 8));
        var height = Math.Min(Math.Max(320, bounds.Height), Math.Max(320, target.Height - 8));
        var left = Math.Clamp(bounds.Left, target.Left, target.Right - width);
        var top = Math.Clamp(bounds.Top, target.Top, target.Bottom - height);
        return new Rectangle(left, top, width, height);
    }

    private static long IntersectionArea(Rectangle left, Rectangle right)
    {
        var width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        var height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        return (long)width * height;
    }

    private void HandlePersistenceError(string prefix, Exception ex)
    {
        LogService.Warning($"{prefix} | {ex.Message}", ex);
        _window?.SetStatus($"{prefix}：{ex.Message}");
    }

    private static bool IsExpectedPersistenceFailure(Exception ex) => ex is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or NotSupportedException
        or ArgumentException
        or System.Xml.XmlException
        or System.Windows.Markup.XamlParseException;

    public void Dispose()
    {
        _disposing = true;
        _saveTimer?.Stop();
        SaveBounds();
        if (_window is null) return;
        _window.AllowClose = true;
        _window.Close();
        _window = null;
    }
}
