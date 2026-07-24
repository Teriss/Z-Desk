using Microsoft.Win32;
using System.IO;

namespace ZDesk.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Z-Desk";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动设置。 ");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, BuildStartupCommand(), RegistryValueKind.String);
    }

    public string BuildStartupCommand()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 Z-Desk 的启动路径。 ");

        return $"\"{processPath}\" --startup";
    }
}
