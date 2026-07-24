using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using System.Windows.Threading;

namespace ZDesk.Services;

/// <summary>Registers a per-user Explorer desktop background command while Z-Desk is running.</summary>
public static class DesktopContextMenuRegistrationService
{
    private const string KeyPath = "Software\\Classes\\DesktopBackground\\Shell\\ZDesk.CreateLayout";
    private const string ClassId = "{2A10D2EE-E9C6-4A2A-8B47-203BF9C1A201}";
    private static DispatcherTimer? _explorerMonitor;
    private static int _explorerProcessId;
    private static string? _executablePath;

    public static void Register(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return;
        try
        {
            using var application = Registry.CurrentUser.CreateSubKey("Software\\ZDesk");
            application?.SetValue("ExecutablePath", executablePath);
            // Remove the unsupported raw registration. The modern command is
            // installed only through the signed sparse package manifest.
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($"Software\\Classes\\CLSID\\{ClassId}", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogService.Warning("Could not register desktop background command", ex);
        }
    }

    public static void Unregister()
    {
        _explorerMonitor?.Stop();
        _explorerMonitor = null;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($"Software\\Classes\\CLSID\\{ClassId}", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogService.Warning("Could not remove desktop background command", ex);
        }
    }

    private static void StartExplorerMonitor(string executablePath)
    {
        _executablePath = executablePath;
        _explorerProcessId = GetExplorerProcessId();
        _explorerMonitor?.Stop();
        _explorerMonitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _explorerMonitor.Tick += (_, _) =>
        {
            var current = GetExplorerProcessId();
            if (_explorerProcessId == 0 || current == 0) { _explorerProcessId = current; return; }
            if (current == _explorerProcessId) return;
            LogService.Error("Explorer restarted while the modern context menu was active; disabling the native handler.");
            _explorerMonitor?.Stop();
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($"Software\\Classes\\CLSID\\{ClassId}", throwOnMissingSubKey: false);
            if (!string.IsNullOrWhiteSpace(_executablePath)) RegisterLegacyCommand(_executablePath);
        };
        _explorerMonitor.Start();
    }

    private static int GetExplorerProcessId() => Process.GetProcessesByName("explorer").FirstOrDefault()?.Id ?? 0;

    private static void RegisterLegacyCommand(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key is null) return;
        key.SetValue(null, "创建 Z-Desk 布局");
        key.SetValue("Icon", executablePath);
        using var command = key.CreateSubKey("command");
        command?.SetValue(null, $"\"{executablePath}\" --create-layout=empty");
    }
}
