namespace ZDesk.Models;

public sealed record RuleMatch(Guid RuleId, string RuleName, string SourcePath, string TargetPath);

public sealed record RuleExecutionResult(int Moved, int Total, IReadOnlyList<string> Issues);
