using Microsoft.Win32;
using System.IO;

namespace ZDesk.Services;

public static class AppDataPathService
{
    private const string RegistryPath = @"Software\ZDesk";
    private const string DataValueName = "DataDirectory";
    private const string LogValueName = "LogDirectory";

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZDesk");

    public static string DataDirectory => ReadPath(DataValueName, DefaultDataDirectory);
    public static string LogDirectory => ReadPath(LogValueName, Path.Combine(DataDirectory, "logs"));

    public static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (string.IsNullOrWhiteSpace(expanded)) throw new InvalidDataException("存储路径不能为空。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    public static async Task MigrateAndConfigureAsync(
        string currentDataDirectory,
        string currentLogDirectory,
        string requestedDataDirectory,
        string requestedLogDirectory)
    {
        var oldData = Normalize(currentDataDirectory);
        var oldLogs = Normalize(currentLogDirectory);
        var newData = Normalize(requestedDataDirectory);
        var newLogs = Normalize(requestedLogDirectory);
        ValidateUnnested(oldData, newData, "数据");
        ValidateUnnested(oldLogs, newLogs, "日志");

        if (!SamePath(oldData, newData))
            await MigrateDirectoryAsync(oldData, newData, IsInside(oldLogs, oldData) ? oldLogs : null);
        if (!SamePath(oldLogs, newLogs))
            await MigrateDirectoryAsync(oldLogs, newLogs);

        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath)
            ?? throw new UnauthorizedAccessException("无法保存应用存储路径。");
        key.SetValue(DataValueName, newData, RegistryValueKind.String);
        key.SetValue(LogValueName, newLogs, RegistryValueKind.String);
        LogService.Configure(newLogs);
    }

    public static async Task MigrateDirectoryAsync(string source, string destination, string? excludedDirectory = null)
    {
        source = Normalize(source);
        destination = Normalize(destination);
        if (SamePath(source, destination) || !Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (excludedDirectory is not null && IsInside(file, excludedDirectory)) continue;
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output);
        }
    }

    private static string ReadPath(string name, string fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)
                ? Normalize(value)
                : Normalize(fallback);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Normalize(fallback);
        }
    }

    private static void ValidateUnnested(string source, string destination, string label)
    {
        if (SamePath(source, destination)) return;
        if (IsInside(destination, source) || IsInside(source, destination))
            throw new InvalidDataException($"新的{label}目录不能与旧目录互相嵌套。");
    }

    private static bool IsInside(string candidate, string parent)
    {
        var normalizedCandidate = Normalize(candidate) + Path.DirectorySeparatorChar;
        var normalizedParent = Normalize(parent) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
}
