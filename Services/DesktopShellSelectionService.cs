using System.IO;
using System.Windows.Automation;

namespace ZDesk.Services;

/// <summary>Mirrors physical desktop selections into Explorer's hidden desktop view.</summary>
public sealed class DesktopShellSelectionService
{
    private static readonly string UserDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string CommonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public bool TrySelect(IEnumerable<string> paths)
    {
        var requested = paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        var names = requested.Select(path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (names.Count == 0) return false;

        if (!requested.All(IsPhysicalDesktopPath)) return false;

        var listView = DesktopIconVisibilityService.FindDesktopListView();
        if (listView != nint.Zero)
        {
            try
            {
                var root = AutomationElement.FromHandle(listView);
                var items = root.FindAll(TreeScope.Children, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Where(item => names.Contains(item.Current.Name))
                    .ToArray();
                if (items.Length > 0)
                {
                    for (var index = 0; index < items.Length; index++)
                    {
                        if (!items[index].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) continue;
                        var selection = (SelectionItemPattern)pattern;
                        if (index == 0) selection.Select();
                        else selection.AddToSelection();
                    }
                    return true;
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        return false;
    }

    public static bool IsPhysicalDesktopPath(string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return string.Equals(parent, UserDesktop, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parent, CommonDesktop, StringComparison.OrdinalIgnoreCase);
    }

}
