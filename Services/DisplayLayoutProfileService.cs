using System.IO;
using System.Text.Json;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class DisplayLayoutProfileService
{
    private string _directory = Path.Combine(AppDataPathService.DataDirectory, "display-layouts");
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public void SetDataDirectory(string directory) => _directory = Path.Combine(AppDataPathService.Normalize(directory), "display-layouts");

    public string GetCurrentSignature() => string.Join("_", System.Windows.Forms.Screen.AllScreens
        .OrderBy(screen => screen.DeviceName)
        .Select(screen => $"{Sanitize(screen.DeviceName)}-{screen.Bounds.Width}x{screen.Bounds.Height}@{screen.Bounds.X},{screen.Bounds.Y}"));

    public async Task SaveAsync(string signature, IEnumerable<GroupDefinition> groups)
    {
        var snapshot = SnapshotService.CloneGroups(groups);
        foreach (var group in snapshot) LayoutSizeService.Normalize(group);
        var path = Path.Combine(_directory, $"{signature}.json");
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally { _saveGate.Release(); }
    }

    public async Task<List<GroupDefinition>?> LoadAsync(string signature)
    {
        var path = Path.Combine(_directory, $"{signature}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var groups = JsonSerializer.Deserialize<List<GroupDefinition>>(await File.ReadAllTextAsync(path));
            if (groups is not null)
                foreach (var group in groups) LayoutSizeService.Normalize(group);
            return groups;
        }
        catch (JsonException) { return null; }
    }

    private static string Sanitize(string text) => new(text.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
}
