namespace ZDesk.Models;

/// <summary>
/// Immutable data passed from the desktop layout model to a renderer. The
/// presentation layer must not retain WPF controls or ImageSource instances.
/// </summary>
public sealed record DesktopPresentationSnapshot(
    Guid GroupId,
    string Title,
    GroupKind Kind,
    LayoutViewMode ViewMode,
    bool IsCollapsed,
    int ActiveTabIndex,
    IReadOnlyList<DesktopPresentationTab> Tabs,
    IReadOnlyList<DesktopPresentationItem> Items);

public sealed record DesktopPresentationTab(Guid Id, string Title, bool IsActive);

public sealed record DesktopPresentationItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    string TypeName,
    string SizeText,
    string ModifiedText,
    bool IsSelected);
