using System.IO;
using System.Text.Json;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class SnapshotService
{
    private string _directory = Path.Combine(AppDataPathService.DataDirectory, "snapshots");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public void SetDataDirectory(string directory) => _directory = Path.Combine(AppDataPathService.Normalize(directory), "snapshots");

    public async Task SaveAsync(string name, IEnumerable<GroupDefinition> groups)
    {
        Directory.CreateDirectory(_directory);
        var snapshot = new LayoutSnapshot
        {
            Name = name,
            CreatedAt = DateTimeOffset.Now,
            Groups = CloneGroups(groups)
        };
        var path = Path.Combine(_directory, $"{Sanitize(name)}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, Options));
        Prune();
    }

    public async Task<IReadOnlyList<LayoutSnapshot>> LoadAllAsync()
    {
        if (!Directory.Exists(_directory)) return [];
        var snapshots = new List<LayoutSnapshot>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<LayoutSnapshot>(await File.ReadAllTextAsync(file), Options);
                if (snapshot is not null) snapshots.Add(snapshot);
            }
            catch (JsonException) { }
        }
        return snapshots;
    }

    public static List<GroupDefinition> CloneGroups(IEnumerable<GroupDefinition> groups) => groups.Select(group => new GroupDefinition
    {
        Id = group.Id,
        Title = group.Title,
        Kind = group.Kind,
        FolderPath = group.FolderPath,
        X = group.X,
        Y = group.Y,
        Width = group.Width,
        Height = group.Height,
        IsCollapsed = group.IsCollapsed,
        AutoCollapse = group.AutoCollapse,
        PinnedPaths = [.. group.PinnedPaths],
        ItemOrder = [.. group.ItemOrder],
        ViewMode = group.ViewMode,
        SortProperty = group.SortProperty,
        SortDescending = group.SortDescending,
        IsRuleLocked = group.IsRuleLocked,
        DesktopX = group.DesktopX,
        DesktopY = group.DesktopY,
        DisplayDeviceName = group.DisplayDeviceName,
        DockEdge = group.DockEdge,
        Tabs = group.Tabs.Select(tab => tab.Clone()).ToList(),
        ActiveTabIndex = group.ActiveTabIndex
    }).ToList();

    private void Prune()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(30)) File.Delete(file);
    }
    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(character => !Path.GetInvalidFileNameChars().Contains(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "snapshot" : cleaned;
    }
}
