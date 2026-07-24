using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ZDesk.Services;

/// <summary>Resolves shortcut destinations without launching the target.</summary>
public sealed class ShortcutTargetService
{
    public IReadOnlyList<string> ResolveCandidates(string path)
    {
        var candidates = new List<string> { path };
        try
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(line[4..].Trim());
                }
            }
            else if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return candidates;
                var shell = Activator.CreateInstance(shellType);
                var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
                if (shortcut is null) return candidates;
                foreach (var property in new[] { "TargetPath", "Arguments", "WorkingDirectory" })
                {
                    var value = shortcut.GetType().InvokeMember(property, BindingFlags.GetProperty, null, shortcut, null) as string;
                    if (!string.IsNullOrWhiteSpace(value)) candidates.Add(value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or TargetInvocationException)
        {
            LogService.Warning($"Shortcut target resolution failed | path={path} | error={ex.Message}");
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
