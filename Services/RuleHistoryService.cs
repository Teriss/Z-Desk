using System.IO;
using System.Text.Json;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed record RuleHistoryEntry(DateTimeOffset ExecutedAt, int Moved, int Total, int Failed);

public sealed class RuleHistoryService
{
    private readonly string _file;
    public RuleHistoryService(string? directory = null)
    {
        directory ??= AppDataPathService.DataDirectory;
        _file = Path.Combine(directory, "rule-history.json");
    }
    public async Task AppendAsync(RuleExecutionResult result)
    {
        var history = (await LoadAsync()).ToList();
        history.Insert(0, new RuleHistoryEntry(DateTimeOffset.Now, result.Moved, result.Total, result.Issues.Count));
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        await File.WriteAllTextAsync(_file, JsonSerializer.Serialize(history.Take(100), new JsonSerializerOptions { WriteIndented = true }));
    }
    public async Task<IReadOnlyList<RuleHistoryEntry>> LoadAsync()
    {
        if (!File.Exists(_file)) return [];
        try { return JsonSerializer.Deserialize<List<RuleHistoryEntry>>(await File.ReadAllTextAsync(_file)) ?? []; }
        catch (JsonException) { return []; }
    }
}
