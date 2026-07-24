using System.IO;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class FileTransferService
{
    public Task<FileTransferResult> ExecuteAsync(
        IEnumerable<string> sourcePaths,
        string destinationFolder,
        FileTransferMode mode,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        FileConflictStrategy conflictStrategy = FileConflictStrategy.Rename)
    {
        var sources = NormalizeSources(sourcePaths);
        var destination = Path.GetFullPath(destinationFolder);
        return Task.Run(() => Execute(sources, destination, mode, progress, cancellationToken, conflictStrategy), cancellationToken);
    }

    private static FileTransferResult Execute(
        IReadOnlyList<string> sources,
        string destinationFolder,
        FileTransferMode mode,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken,
        FileConflictStrategy conflictStrategy)
    {
        Directory.CreateDirectory(destinationFolder);
        var issues = new List<string>();
        var succeeded = 0;
        var completed = new List<CompletedFileTransfer>();

        for (var index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[index];
            var name = GetEntryName(source);
            progress?.Report(new FileTransferProgress(index, sources.Count, name));

            try
            {
                if (!File.Exists(source) && !Directory.Exists(source))
                {
                    throw new FileNotFoundException("源文件或目录已经不存在。", source);
                }

                var sourceParent = Path.GetDirectoryName(source);
                if (sourceParent is not null && PathsEqual(sourceParent, destinationFolder))
                {
                    throw new IOException("源项目已经位于该映射文件夹中。");
                }

                var sourceIsDirectory = Directory.Exists(source);
                var destination = ResolveDestination(destinationFolder, name, sourceIsDirectory, conflictStrategy);
                if (destination is null)
                {
                    issues.Add($"{name}：因同名冲突已跳过。");
                    continue;
                }
                if (Directory.Exists(source) && IsSubPath(destination, source))
                {
                    throw new IOException("不能将目录复制或移动到它自己的子目录中。");
                }

                var replacedBackup = conflictStrategy == FileConflictStrategy.Overwrite
                    ? BackupExistingDestination(destination)
                    : null;
                try
                {
                    if (mode == FileTransferMode.Copy)
                    {
                        CopyEntry(source, destination, cancellationToken);
                    }
                    else
                    {
                        MoveEntry(source, destination, issues, cancellationToken);
                    }

                }
                catch
                {
                    if (replacedBackup is not null)
                    {
                        if (File.Exists(destination) || Directory.Exists(destination)) DeleteEntry(destination);
                        RestoreBackup(replacedBackup, destination);
                    }
                    throw;
                }

                if (replacedBackup is not null)
                {
                    try
                    {
                        DeleteEntry(replacedBackup);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        issues.Add($"{name}：覆盖已完成，但旧版本临时备份清理失败（{ex.Message}）。");
                    }
                }

                succeeded++;
                completed.Add(new CompletedFileTransfer(source, destination, sourceIsDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                issues.Add($"{name}：{ex.Message}");
            }
        }

        progress?.Report(new FileTransferProgress(sources.Count, sources.Count, string.Empty));
        return new FileTransferResult(mode, succeeded, sources.Count, issues, completed);
    }

    private static IReadOnlyList<string> NormalizeSources(IEnumerable<string> sourcePaths)
    {
        var candidates = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates
            .Where(candidate => !candidates.Any(parent =>
                !PathsEqual(parent, candidate) && Directory.Exists(parent) && IsSubPath(candidate, parent)))
            .ToList();
    }

    private static void CopyEntry(string source, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: false);
            return;
        }

        CopyDirectory(source, destination, cancellationToken);
    }

    private static void MoveEntry(string source, string destination, ICollection<string> issues, CancellationToken cancellationToken)
    {
        var crossesVolume = !string.Equals(
            Path.GetPathRoot(source),
            Path.GetPathRoot(destination),
            StringComparison.OrdinalIgnoreCase);

        if (!crossesVolume)
        {
            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
            else
            {
                Directory.Move(source, destination);
            }

            return;
        }

        CopyEntry(source, destination, cancellationToken);
        try
        {
            if (File.Exists(source))
            {
                File.Delete(source);
            }
            else
            {
                Directory.Delete(source, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add($"{GetEntryName(source)}：已复制到目标，但无法删除源项目（{ex.Message}）");
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new NotSupportedException("暂不支持复制目录链接或目录联接点。");
        }

        Directory.CreateDirectory(destination);
        try
        {
            foreach (var file in sourceInfo.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.CopyTo(Path.Combine(destination, file.Name), overwrite: false);
            }

            foreach (var directory in sourceInfo.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyDirectory(directory.FullName, Path.Combine(destination, directory.Name), cancellationToken);
            }
        }
        catch
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            throw;
        }
    }

    private static string? ResolveDestination(
        string folder,
        string name,
        bool isDirectory,
        FileConflictStrategy strategy)
    {
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        if (strategy == FileConflictStrategy.Skip) return null;
        if (strategy == FileConflictStrategy.Overwrite) return candidate;

        var baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(folder, $"{baseName} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法生成不冲突的目标名称。");
    }

    private static string? BackupExistingDestination(string destination)
    {
        if (!File.Exists(destination) && !Directory.Exists(destination)) return null;
        var backup = destination + $".zdesk-replaced-{Guid.NewGuid():N}";
        if (Directory.Exists(destination)) Directory.Move(destination, backup);
        else File.Move(destination, backup);
        return backup;
    }

    private static void RestoreBackup(string backup, string destination)
    {
        if (Directory.Exists(backup)) Directory.Move(backup, destination);
        else if (File.Exists(backup)) File.Move(backup, destination);
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static string GetEntryName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return Path.GetFileName(trimmed);
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    private static bool IsSubPath(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}
