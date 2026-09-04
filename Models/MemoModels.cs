using System.Text.Json.Serialization;

namespace ZDesk.Models;

public sealed class MemoNotebookIndex
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<MemoNoteMetadata> Notes { get; set; } = [];
}

public sealed class MemoNoteMetadata
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int CaretOffset { get; set; }
    public string Title { get; set; } = "无标题便笺";
    public string PreviewText { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public bool HasPreviewImage { get; set; }

    [JsonIgnore]
    public string PackageFileName => $"{Id:N}.xamlpackage";

    [JsonIgnore]
    public string BackupFileName => $"{Id:N}.backup.xamlpackage";

    [JsonIgnore]
    public string PreviewFileName => $"{Id:N}.preview.png";
}

public sealed record MemoContentSnapshot(
    string Title,
    string PreviewText,
    string SearchText,
    byte[]? PreviewImagePng,
    int CaretOffset);

public sealed record MemoNotePackageLoadResult(byte[]? Primary, byte[]? Backup);

public sealed record MemoNotebookLoadResult(
    MemoNotebookIndex Index,
    bool RecoveredFromBackup,
    bool RebuiltFromPackages,
    string? Warning);
