using ZDesk.Models;

namespace ZDesk.Services;

public static class LayoutItemStateService
{
    public static bool Normalize(GroupDefinition group)
    {
        if (group.Tabs.Count == 0)
            return group.Kind == GroupKind.Empty && Normalize(group.PinnedPaths, group.ItemOrder);

        var changed = false;
        foreach (var tab in group.Tabs.Where(tab => tab.Kind == GroupKind.Empty))
            changed |= Normalize(tab.PinnedPaths, tab.ItemOrder);
        group.ReloadActiveTab();
        return changed;
    }

    public static bool Normalize(List<string> pinnedPaths, List<string> itemOrder)
    {
        var normalizedPinned = pinnedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pinned = normalizedPinned.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedOrder = itemOrder
            .Where(pinned.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ordered = normalizedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        normalizedOrder.AddRange(normalizedPinned.Where(ordered.Add));

        var changed = !pinnedPaths.SequenceEqual(normalizedPinned, StringComparer.OrdinalIgnoreCase) ||
                      !itemOrder.SequenceEqual(normalizedOrder, StringComparer.OrdinalIgnoreCase);
        if (!changed) return false;
        pinnedPaths.Clear();
        pinnedPaths.AddRange(normalizedPinned);
        itemOrder.Clear();
        itemOrder.AddRange(normalizedOrder);
        return true;
    }

    public static bool AddPinnedPath(List<string> pinnedPaths, List<string> itemOrder, string path)
    {
        Normalize(pinnedPaths, itemOrder);
        if (pinnedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;
        pinnedPaths.Add(path);
        itemOrder.Add(path);
        return true;
    }

    public static bool RemovePinnedPaths(List<string> pinnedPaths, List<string> itemOrder, IEnumerable<string> paths)
    {
        var removing = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pinnedRemoved = pinnedPaths.RemoveAll(removing.Contains);
        var orderRemoved = itemOrder.RemoveAll(removing.Contains);
        return pinnedRemoved > 0 || orderRemoved > 0;
    }

    public static IReadOnlyDictionary<string, int> BuildOrderIndex(IEnumerable<string> itemOrder)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var path in itemOrder)
        {
            result.TryAdd(path, index);
            index++;
        }
        return result;
    }
}
