using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class DiagnosticService
{
    private readonly LayoutStore _layoutStore;
    public DiagnosticService(LayoutStore layoutStore) => _layoutStore = layoutStore;

    public async Task<string> CreatePackageAsync(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory, $"ZDesk-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var systemEntry = archive.CreateEntry("system.txt");
        await using (var writer = new StreamWriter(systemEntry.Open(), Encoding.UTF8))
        {
            await writer.WriteLineAsync($"Z-Desk version: {typeof(DiagnosticService).Assembly.GetName().Version}");
            await writer.WriteLineAsync($"OS: {Environment.OSVersion}");
            await writer.WriteLineAsync($"Runtime: {Environment.Version}");
            await writer.WriteLineAsync($"64-bit: {Environment.Is64BitProcess}");
            await writer.WriteLineAsync($"Displays: {System.Windows.Forms.Screen.AllScreens.Length}");
        }

        await AddRedactedLayoutAsync(archive, _layoutStore.StateFile, "layout.redacted.json");
        await AddRedactedLayoutAsync(archive, _layoutStore.BackupFile, "layout.backup.redacted.json");
        AddFileIfPresent(archive, LogService.CurrentLogFile, "zdesk.log");
        return path;
    }

    private static async Task AddRedactedLayoutAsync(ZipArchive archive, string source, string name)
    {
        if (!File.Exists(source)) return;
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(source));
            if (state is null) return;
            foreach (var group in state.Groups)
            {
                group.FolderPath = RedactPath(group.FolderPath);
            }
            foreach (var rule in state.Rules)
            {
                rule.SourceFolder = RedactPath(rule.SourceFolder) ?? string.Empty;
                rule.TargetFolder = RedactPath(rule.TargetFolder) ?? string.Empty;
            }
            var entry = archive.CreateEntry(name);
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync(JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException) { }
    }

    private static string? RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return Path.Combine(root, "<redacted>", Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
    }

    private static void AddFileIfPresent(ZipArchive archive, string source, string name)
    {
        if (File.Exists(source)) archive.CreateEntryFromFile(source, name, CompressionLevel.Fastest);
    }
}
