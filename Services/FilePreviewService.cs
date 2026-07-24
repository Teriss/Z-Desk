using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ZDesk.Models;

namespace ZDesk.Services;

public interface IFilePreviewProvider
{
    bool CanPreview(string path);
    Task<bool> TryPreviewAsync(string path);
}

public sealed class FilePreviewService
{
    private readonly IReadOnlyList<IFilePreviewProvider> _providers;

    public FilePreviewService(IEnumerable<IFilePreviewProvider>? providers = null)
    {
        _providers = (providers ?? [new QuickLookPreviewProvider()]).ToArray();
    }

    public async Task<bool> TryPreviewAsync(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        foreach (var provider in _providers.Where(provider => provider.CanPreview(path)))
        {
            try
            {
                if (await provider.TryPreviewAsync(path)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception or InvalidOperationException or COMException)
            {
                LogService.Warning($"Preview provider failed for {Path.GetFileName(path)}", ex);
            }
        }
        return false;
    }

    public static FileEntry? SelectPreviewEntry(IEnumerable<FileEntry> selectedEntries, string? focusedPath)
    {
        var selected = selectedEntries.ToArray();
        return selected.FirstOrDefault(entry => string.Equals(
                   entry.FullPath, focusedPath, StringComparison.OrdinalIgnoreCase))
               ?? selected.FirstOrDefault();
    }
}

public sealed class QuickLookPreviewProvider : IFilePreviewProvider
{
    private const string StoreAppUserModelId = "21090PaddyXu.QuickLook_egxr34yet59cg!App";
    private readonly Func<QuickLookLaunchTarget?> _targetResolver;
    private readonly Func<QuickLookLaunchTarget, string, bool> _launcher;

    public QuickLookPreviewProvider()
        : this(FindLaunchTarget, Launch) { }

    internal QuickLookPreviewProvider(
        Func<QuickLookLaunchTarget?> targetResolver,
        Func<QuickLookLaunchTarget, string, bool> launcher)
    {
        _targetResolver = targetResolver;
        _launcher = launcher;
    }

    public bool CanPreview(string path) => File.Exists(path) || Directory.Exists(path);

    public Task<bool> TryPreviewAsync(string path)
    {
        var target = _targetResolver();
        return Task.FromResult(target is not null && _launcher(target, path));
    }

    internal static QuickLookLaunchTarget? FindLaunchTarget()
    {
        var candidates = new List<string?>();
        foreach (var process in Process.GetProcessesByName("QuickLook"))
        {
            using (process)
            {
                try { candidates.Add(process.MainModule?.FileName); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }

        candidates.AddRange(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "QuickLook", "QuickLook.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickLook", "QuickLook.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "QuickLook.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "QuickLook", "QuickLook.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "QuickLook", "QuickLook.exe")
        ]);

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Default),
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32)
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\QuickLook.exe");
                candidates.Add((key?.GetValue(null) as string)?.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        }

        var executable = candidates.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        return !string.IsNullOrWhiteSpace(executable)
            ? QuickLookLaunchTarget.ForExecutable(executable)
            : QuickLookLaunchTarget.ForStoreApplication(StoreAppUserModelId);
    }

    private static bool Launch(QuickLookLaunchTarget target, string path)
    {
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = target.ExecutablePath,
                ArgumentList = { path },
                UseShellExecute = true
            }) is not null;
        }

        var activationManager = (IApplicationActivationManager)(object)new ApplicationActivationManager();
        try
        {
            var result = activationManager.ActivateApplication(
                target.AppUserModelId!, $"\"{path}\"", ActivateOptions.None, out _);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(activationManager);
        }
    }

    [Flags]
    private enum ActivateOptions : uint
    {
        None = 0
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private sealed class ApplicationActivationManager { }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);

        [PreserveSig]
        int ActivateForFile(nint appUserModelId, nint itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol(nint appUserModelId, nint itemArray, out uint processId);
    }
}

internal sealed record QuickLookLaunchTarget(string? ExecutablePath, string? AppUserModelId)
{
    public static QuickLookLaunchTarget ForExecutable(string path) => new(path, null);
    public static QuickLookLaunchTarget ForStoreApplication(string appUserModelId) => new(null, appUserModelId);
}
