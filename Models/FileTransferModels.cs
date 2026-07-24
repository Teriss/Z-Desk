namespace ZDesk.Models;

public enum FileTransferMode
{
    Copy,
    Move
}

public enum FileConflictStrategy
{
    Rename,
    Skip,
    Overwrite
}

public sealed record CompletedFileTransfer(string SourcePath, string DestinationPath, bool SourceWasDirectory);

public sealed record FileTransferProgress(int Completed, int Total, string CurrentName);

public sealed record FileTransferResult(
    FileTransferMode Mode,
    int Succeeded,
    int Total,
    IReadOnlyList<string> Issues,
    IReadOnlyList<CompletedFileTransfer> Completed)
{
    public bool HasIssues => Issues.Count > 0;

    public string Summary => Mode switch
    {
        FileTransferMode.Copy => $"已复制 {Succeeded}/{Total} 项",
        FileTransferMode.Move => $"已移动 {Succeeded}/{Total} 项",
        _ => $"已处理 {Succeeded}/{Total} 项"
    };
}
