using System.IO;
using ZDesk.Models;

namespace ZDesk.Services;

/// <summary>Classifies desktop entries into existing layouts without moving files.</summary>
public sealed class LayoutAssignmentService
{
    private readonly ShortcutTargetService _shortcutTargets = new();
    public IReadOnlyList<(string Path, Guid GroupId, Guid? TabId)> Preview(
        IEnumerable<string> paths,
        IEnumerable<GroupDefinition> groups,
        IEnumerable<LayoutMatchRule> rules)
    {
        var groupList = new Dictionary<string, (Guid GroupId, Guid? TabId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group.Tabs.Count == 0)
            {
                if (group.Kind == GroupKind.Empty && !group.IsRuleLocked) groupList[group.Id.ToString()] = (group.Id, null);
                continue;
            }
            foreach (var tab in group.Tabs.Where(tab => tab.Kind == GroupKind.Empty && !tab.IsRuleLocked))
                groupList[tab.Id.ToString()] = (group.Id, tab.Id);
        }
        var ordered = rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ToArray();
        var result = new List<(string, Guid, Guid?)>();
        foreach (var path in paths)
        {
            var rule = ordered.FirstOrDefault(r => Matches(path, r));
            if (rule is null || !groupList.TryGetValue(rule.GroupId, out var target)) continue;
            result.Add((path, target.GroupId, target.TabId));
        }
        return result;
    }

    private bool Matches(string path, LayoutMatchRule rule)
    {
        var isFolder = Directory.Exists(path);
        var matchType = rule.EditorMatchType;
        if (matchType == LayoutRuleMatchType.Folder) return isFolder;
        if (isFolder) return false;
        if (matchType == LayoutRuleMatchType.OtherFiles) return true;

        var extensions = rule.Extensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (extensions.Length > 0 && !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return false;

        var pathTerms = rule.PathContains.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathTerms.Length == 0) return true;
        var candidates = _shortcutTargets.ResolveCandidates(path);
        return pathTerms.Any(term => candidates.Any(candidate =>
            candidate.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}
