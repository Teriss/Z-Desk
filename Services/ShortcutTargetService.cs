using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ZDesk.Services;

/// <summary>Resolves shortcut destinations without launching the target.</summary>
public sealed class ShortcutTargetService
{
    private const int MaxCacheEntries = 512;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();

    public IReadOnlyList<string> ResolveCandidates(string path)
    {
        var stamp = GetLastWriteStamp(path);
        var cacheKey = $"{path}:{stamp}";
        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var candidates = new List<string> { path };
        object? shell = null;
        object? shortcut = null;
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
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
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

        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }

        var result = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
            Cache[cacheKey] = result;
            CacheOrder.Enqueue(cacheKey);
            while (Cache.Count > MaxCacheEntries && CacheOrder.TryDequeue(out var oldest))
                Cache.Remove(oldest);
        }
        return result;
    }

    private static long GetLastWriteStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return 0; }
    }
}
