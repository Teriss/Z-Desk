using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZDesk.Models;

namespace ZDesk.Services;

/// <summary>Persists the notebook index and one XAML Package per memo note.</summary>
public sealed class MemoNotebookStore
{
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string StateDirectory { get; private set; }
    public string MemoDirectory => Path.Combine(StateDirectory, "memo");
    public string IndexFile => Path.Combine(MemoDirectory, "index.json");
    public string IndexBackupFile => Path.Combine(MemoDirectory, "index.backup.json");
    public string LegacyDocumentFile => Path.Combine(StateDirectory, "memo.xamlpackage");
    public string LegacyBackupFile => Path.Combine(StateDirectory, "memo.backup.xamlpackage");

    public MemoNotebookStore(string? stateDirectory = null)
    {
        StateDirectory = AppDataPathService.Normalize(stateDirectory ?? AppDataPathService.DataDirectory);
    }

    public void SetDataDirectory(string directory) => StateDirectory = AppDataPathService.Normalize(directory);

    public async Task<MemoNotebookLoadResult> LoadAsync()
    {
        Directory.CreateDirectory(MemoDirectory);
        var primary = await ReadIfPresentAsync(IndexFile);
        var backup = await ReadIfPresentAsync(IndexBackupFile);

        if (TryDeserializeIndex(primary, out var index))
            return new MemoNotebookLoadResult(index, false, false, null);

        if (primary is not null)
            await PreserveFileAsync(IndexFile, "index.broken", ".json");

        if (TryDeserializeIndex(backup, out index))
            return new MemoNotebookLoadResult(index, true, false, "备忘录索引损坏，已从备份恢复");

        if (backup is not null)
            await PreserveFileAsync(IndexBackupFile, "index.backup-broken", ".json");

        var rebuilt = await RebuildIndexFromPackagesAsync();
        var warning = primary is not null || backup is not null
            ? "备忘录索引无法读取，已根据正文包重建"
            : null;
        return new MemoNotebookLoadResult(rebuilt, false, true, warning);
    }

    public async Task<MemoNotePackageLoadResult> LoadNoteAsync(Guid id)
    {
        var note = new MemoNoteMetadata { Id = id };
        var primary = await ReadIfPresentAsync(GetPackagePath(note));
        var backup = await ReadIfPresentAsync(GetBackupPath(note));
        return new MemoNotePackageLoadResult(primary, backup);
    }

    public async Task<byte[]?> LoadPreviewAsync(MemoNoteMetadata note) => await ReadIfPresentAsync(GetPreviewPath(note));

    public async Task<MemoNotePackageLoadResult> LoadLegacyAsync()
    {
        return new MemoNotePackageLoadResult(
            await ReadIfPresentAsync(LegacyDocumentFile),
            await ReadIfPresentAsync(LegacyBackupFile));
    }

    public async Task SaveNoteAsync(MemoNoteMetadata note, ReadOnlyMemory<byte> package, byte[]? previewPng, bool createBackup = true)
    {
        await _ioGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(MemoDirectory);
            var packagePath = GetPackagePath(note);
            var temporary = packagePath + ".tmp";
            await WriteFileAsync(temporary, package);
            if (createBackup && File.Exists(packagePath)) File.Copy(packagePath, GetBackupPath(note), overwrite: true);
            File.Move(temporary, packagePath, overwrite: true);

            var previewPath = GetPreviewPath(note);
            if (previewPng is null)
            {
                if (File.Exists(previewPath)) File.Delete(previewPath);
            }
            else
            {
                var previewTemporary = previewPath + ".tmp";
                await WriteFileAsync(previewTemporary, previewPng);
                File.Move(previewTemporary, previewPath, overwrite: true);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveIndexAsync(MemoNotebookIndex index, bool createBackup = true)
    {
        await _ioGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(MemoDirectory);
            NormalizeIndex(index);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
            var temporary = IndexFile + ".tmp";
            await WriteFileAsync(temporary, bytes);
            if (createBackup && File.Exists(IndexFile)) File.Copy(IndexFile, IndexBackupFile, overwrite: true);
            File.Move(temporary, IndexFile, overwrite: true);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task DeleteNoteAsync(MemoNoteMetadata note)
    {
        await _ioGate.WaitAsync();
        try
        {
            foreach (var path in new[] { GetPackagePath(note), GetBackupPath(note), GetPreviewPath(note), GetPackagePath(note) + ".tmp", GetPreviewPath(note) + ".tmp" })
            {
                if (File.Exists(path)) File.Delete(path);
            }

            var prefix = note.Id.ToString("N");
            foreach (var path in Directory.EnumerateFiles(MemoDirectory, $"{prefix}.broken-*.xamlpackage"))
                File.Delete(path);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task PreserveCorruptNoteAsync(MemoNoteMetadata note)
    {
        await PreserveFileAsync(GetPackagePath(note), $"{note.Id:N}.broken", ".xamlpackage");
    }

    public async Task PreserveCorruptLegacyAsync() => await PreserveFileAsync(LegacyDocumentFile, "memo.broken", ".xamlpackage");

    public async Task ArchiveLegacyAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            await ArchiveFileAsync(LegacyDocumentFile, "memo.legacy.xamlpackage");
            await ArchiveFileAsync(LegacyBackupFile, "memo.legacy.backup.xamlpackage");
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<MemoNotebookIndex> RebuildIndexFromPackagesAsync()
    {
        var index = new MemoNotebookIndex();
        foreach (var path in Directory.EnumerateFiles(MemoDirectory, "*.xamlpackage"))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Contains(".backup", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(".broken", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var idText = Path.GetFileNameWithoutExtension(fileName);
            if (!Guid.TryParseExact(idText, "N", out var id)) continue;
            var updated = File.GetLastWriteTimeUtc(path);
            index.Notes.Add(new MemoNoteMetadata
            {
                Id = id,
                CreatedAtUtc = updated,
                UpdatedAtUtc = updated,
                Title = "无标题便笺",
                HasPreviewImage = File.Exists(Path.Combine(MemoDirectory, $"{id:N}.preview.png"))
            });
        }
        index.Notes = index.Notes.OrderByDescending(note => note.UpdatedAtUtc).ToList();
        return index;
    }

    private static bool TryDeserializeIndex(byte[]? bytes, out MemoNotebookIndex index)
    {
        index = new MemoNotebookIndex();
        if (bytes is null || bytes.Length == 0) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<MemoNotebookIndex>(bytes, JsonOptions);
            if (parsed is null || parsed.Version > MemoNotebookIndex.CurrentVersion) return false;
            NormalizeIndex(parsed);
            index = parsed;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static void NormalizeIndex(MemoNotebookIndex index)
    {
        index.Version = MemoNotebookIndex.CurrentVersion;
        index.Notes ??= [];
        index.Notes = index.Notes
            .Where(note => note is not null && note.Id != Guid.Empty)
            .GroupBy(note => note.Id)
            .Select(group => group.OrderByDescending(note => note.UpdatedAtUtc).First())
            .Select(note =>
            {
                note.Title ??= "无标题便笺";
                note.PreviewText ??= string.Empty;
                note.SearchText ??= string.Empty;
                note.CaretOffset = Math.Max(0, note.CaretOffset);
                if (note.CreatedAtUtc == default) note.CreatedAtUtc = note.UpdatedAtUtc == default ? DateTime.UtcNow : note.UpdatedAtUtc;
                if (note.UpdatedAtUtc == default) note.UpdatedAtUtc = note.CreatedAtUtc;
                return note;
            })
            .OrderByDescending(note => note.UpdatedAtUtc)
            .ToList();
    }

    private string GetPackagePath(MemoNoteMetadata note) => Path.Combine(MemoDirectory, note.PackageFileName);
    private string GetBackupPath(MemoNoteMetadata note) => Path.Combine(MemoDirectory, note.BackupFileName);
    private string GetPreviewPath(MemoNoteMetadata note) => Path.Combine(MemoDirectory, note.PreviewFileName);

    private static async Task WriteFileAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }

    private static async Task<byte[]?> ReadIfPresentAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"Memo notebook read failed | file={Path.GetFileName(path)}", ex);
            return null;
        }
    }

    private async Task PreserveFileAsync(string path, string prefix, string extension)
    {
        if (!File.Exists(path)) return;
        var directory = Path.GetDirectoryName(path) ?? StateDirectory;
        var broken = Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmssfff}{extension}");
        try
        {
            await Task.Run(() => File.Copy(path, broken, overwrite: false));
            LogService.Warning($"Memo damaged file preserved | file={Path.GetFileName(broken)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("Memo damaged file preservation failed", ex);
        }
    }

    private static async Task ArchiveFileAsync(string source, string destinationName)
    {
        if (!File.Exists(source)) return;
        var directory = Path.GetDirectoryName(source) ?? AppDataPathService.DataDirectory;
        var destination = Path.Combine(directory, destinationName);
        if (File.Exists(destination))
            destination = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(destinationName)}-{DateTime.Now:yyyyMMdd-HHmmssfff}{Path.GetExtension(destinationName)}");
        await Task.Run(() => File.Move(source, destination));
    }
}
