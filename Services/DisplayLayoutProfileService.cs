using System.IO;
using System.Text.Json;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class DisplayLayoutProfileService
{
    private string _directory = Path.Combine(AppDataPathService.DataDirectory, "display-layouts");

    public void SetDataDirectory(string directory) => _directory = Path.Combine(AppDataPathService.Normalize(directory), "display-layouts");

    public string GetCurrentSignature() => string.Join("_", System.Windows.Forms.Screen.AllScreens
        .OrderBy(screen => screen.DeviceName)
        .Select(screen => $"{Sanitize(screen.DeviceName)}-{screen.Bounds.Width}x{screen.Bounds.Height}@{screen.Bounds.X},{screen.Bounds.Y}"));

    public async Task SaveAsync(string signature, IEnumerable<GroupDefinition> groups)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, $"{signature}.json"),
            JsonSerializer.Serialize(SnapshotService.CloneGroups(groups), new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<List<GroupDefinition>?> LoadAsync(string signature)
    {
        var path = Path.Combine(_directory, $"{signature}.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<List<GroupDefinition>>(await File.ReadAllTextAsync(path)); }
        catch (JsonException) { return null; }
    }

    private static string Sanitize(string text) => new(text.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
}
