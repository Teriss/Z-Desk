namespace ZDesk.Models;

public sealed class LayoutSnapshot
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public List<GroupDefinition> Groups { get; set; } = [];
}
