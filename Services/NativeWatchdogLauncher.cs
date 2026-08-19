using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ZDesk.Services;

internal static class NativeWatchdogLauncher
{
    private const string ResourceName = "ZDesk.Native.ZDeskWatchdog.exe";

    public static bool TryStart(int processId)
    {
        try
        {
            var assembly = typeof(NativeWatchdogLauncher).Assembly;
            using var resource = assembly.GetManifestResourceStream(ResourceName);
            if (resource is null) return false;

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var payload = buffer.ToArray();
            if (payload.Length == 0) return false;

            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()[..16];
            var directory = Path.Combine(Path.GetTempPath(), "ZDesk", "watchdog");
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, $"watchdog-{hash}.exe");
            if (!File.Exists(executable))
            {
                var temporary = executable + $".{Environment.ProcessId}.tmp";
                File.WriteAllBytes(temporary, payload);
                File.Move(temporary, executable, overwrite: false);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"--explorer-icon-watchdog {processId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            CleanupOldPayloads(directory, executable);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupOldPayloads(string directory, string current)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        foreach (var file in Directory.EnumerateFiles(directory, "watchdog-*.exe"))
        {
            if (string.Equals(file, current, StringComparison.OrdinalIgnoreCase) ||
                File.GetLastWriteTimeUtc(file) >= cutoff) continue;
            try { File.Delete(file); } catch { }
        }
    }
}
