using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using ZDesk.Models;

namespace ZDesk.Services;

public static class MemoryDiagnosticsService
{
    /// <summary>
    /// Applies a one-shot idle working-set trim for the native desktop path.
    /// The shared implementation is also used by the normal WPF idle path.
    /// </summary>
    public static bool TrimNativeDesktopWorkingSet()
    {
        if (!DesktopRenderingOptions.UseNativeTopLevel) return false;
        return TrimIdleWorkingSet();
    }

    /// <summary>Performs a one-shot idle trim for either presentation mode.
    /// It is called only after the existing cancellation-based idle delay, so
    /// active interaction never pays the full GC/working-set cost.</summary>
    public static bool TrimIdleWorkingSet()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            using var process = Process.GetCurrentProcess();
            return EmptyWorkingSet(process.Handle);
        }
        catch (Exception ex)
        {
            LogService.Warning("Idle working-set trim failed", ex);
            return false;
        }
    }
    public static MemorySnapshot Capture(string phase, int layoutWindowCount, int fileEntryCount)
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var renderMode = RenderOptions.ProcessRenderMode.ToString();
        var renderTier = RenderCapability.Tier >> 16;
        var workingSetPrivate = GetWorkingSetPrivate(process);
        return new MemorySnapshot(
            DateTimeOffset.Now,
            phase,
            process.WorkingSet64,
            workingSetPrivate,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false),
            gc.TotalCommittedBytes,
            renderMode,
            "Wpf",
            renderTier,
            layoutWindowCount,
            fileEntryCount,
            ShellIconService.GetCacheDiagnostics());
    }

    private static long GetWorkingSetPrivate(Process process)
    {
        try
        {
            var pageSize = Math.Max(1, Environment.SystemPageSize);
            var estimatedEntries = checked((int)Math.Max(1024, process.WorkingSet64 / pageSize + 1024));
            var handle = OpenProcess(QueryInformation | VmRead, false, (uint)process.Id);
            if (handle == nint.Zero) return process.WorkingSet64;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var bufferSize = checked(IntPtr.Size + estimatedEntries * IntPtr.Size);
                var buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    if (!QueryWorkingSet(handle, buffer, bufferSize))
                    {
                        estimatedEntries *= 2;
                        continue;
                    }

                    var entryCount = checked((long)Marshal.ReadIntPtr(buffer).ToInt64());
                    if (entryCount <= 0 || entryCount > estimatedEntries)
                    {
                        estimatedEntries = checked((int)Math.Clamp(entryCount * 2, 1024, 2_000_000));
                        continue;
                    }

                    var privateEntries = 0L;
                    for (var index = 0; index < entryCount; index++)
                    {
                        var flags = unchecked((ulong)Marshal.ReadIntPtr(buffer, IntPtr.Size + checked((int)index * IntPtr.Size)).ToInt64());
                        // PSAPI_WORKING_SET_BLOCK.Shared occupies bit 8.
                        if ((flags & (1UL << 8)) == 0) privateEntries++;
                    }

                    var result = checked(privateEntries * pageSize);
                    CloseHandle(handle);
                    return result;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            CloseHandle(handle);
        }
        catch
        {
            // Diagnostics must never affect application startup or shutdown.
        }

        return process.WorkingSet64;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSet(
        nint process,
        nint workingSet,
        int size);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint process);

    private const uint QueryInformation = 0x0400;
    private const uint VmRead = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    public static MemorySnapshot Log(string phase, int layoutWindowCount, int fileEntryCount)
    {
        var snapshot = Capture(phase, layoutWindowCount, fileEntryCount);
        LogService.Info(snapshot.ToLogMessage());
        return snapshot;
    }
}
