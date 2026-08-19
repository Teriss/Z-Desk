namespace ZDesk.Models;

public sealed record ShellIconCacheDiagnostics(
    int EntryCount,
    long EstimatedBytes,
    int MaxEntries,
    long MaxEstimatedBytes);

public sealed record MemorySnapshot(
    DateTimeOffset Timestamp,
    string Phase,
    long WorkingSetBytes,
    long WorkingSetPrivateBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    long GcCommittedBytes,
    string RenderMode,
    string DesktopPresentationMode,
    int RenderTier,
    int LayoutWindowCount,
    int FileEntryCount,
    ShellIconCacheDiagnostics IconCache)
{
    public string ToLogMessage() =>
        $"Memory snapshot | phase={Phase} | workingSet={WorkingSetBytes} | workingSetPrivate={WorkingSetPrivateBytes} " +
        $"| private={PrivateMemoryBytes} " +
        $"| managedHeap={ManagedHeapBytes} | gcCommitted={GcCommittedBytes} | renderMode={RenderMode} " +
        $"| desktopPresentation={DesktopPresentationMode} | renderTier={RenderTier} | layoutWindows={LayoutWindowCount} | fileEntries={FileEntryCount} " +
        $"| iconCacheEntries={IconCache.EntryCount}/{IconCache.MaxEntries} " +
        $"| iconCacheBytes={IconCache.EstimatedBytes}/{IconCache.MaxEstimatedBytes}";
}
