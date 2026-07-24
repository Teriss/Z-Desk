using System.IO;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class RuleEngine
{
    public IReadOnlyList<RuleMatch> Preview(IEnumerable<ClassificationRule> rules)
    {
        var results = new List<RuleMatch>();
        var claimedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules.Where(rule => rule.Enabled))
        {
            if (!Directory.Exists(rule.SourceFolder) || string.IsNullOrWhiteSpace(rule.TargetFolder)) continue;
            if (PathsEqual(rule.SourceFolder, rule.TargetFolder)) continue;
            var extensions = ParseExtensions(rule.Extensions);
            foreach (var source in Directory.EnumerateFiles(rule.SourceFolder, "*", SearchOption.TopDirectoryOnly))
            {
                if (!Matches(source, extensions, rule.NameContains, rule.ExcludeNameContains, rule.MinimumAgeDays) || !claimedSources.Add(source)) continue;
                var target = CreateUniqueDestination(rule.TargetFolder, Path.GetFileName(source), results.Select(x => x.TargetPath));
                results.Add(new RuleMatch(rule.Id, rule.Name, source, target));
            }
        }
        return results;
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    public Task<RuleExecutionResult> ExecuteAsync(IReadOnlyList<RuleMatch> preview) => Task.Run(() =>
    {
        var issues = new List<string>();
        var moved = 0;
        foreach (var match in preview)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(match.TargetPath)!);
                File.Move(match.SourcePath, match.TargetPath);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add($"{Path.GetFileName(match.SourcePath)}：{ex.Message}");
            }
        }
        return new RuleExecutionResult(moved, preview.Count, issues);
    });

    private static bool Matches(string path, HashSet<string> extensions, string nameContains, string exclude, int minimumAgeDays)
    {
        if (extensions.Count > 0 && !extensions.Contains(Path.GetExtension(path))) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(nameContains) && !name.Contains(nameContains.Trim(), StringComparison.CurrentCultureIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(exclude) && name.Contains(exclude.Trim(), StringComparison.CurrentCultureIgnoreCase)) return false;
        return minimumAgeDays <= 0 || File.GetCreationTimeUtc(path) <= DateTime.UtcNow.AddDays(-minimumAgeDays);
    }

    private static HashSet<string> ParseExtensions(string text) => text
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string CreateUniqueDestination(string folder, string name, IEnumerable<string> reserved)
    {
        var reservedSet = reserved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate) && !reservedSet.Contains(candidate)) return candidate;
        var baseName = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(folder, $"{baseName} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !reservedSet.Contains(candidate)) return candidate;
        }
        throw new IOException("无法生成不冲突的规则目标名称。");
    }
}
