using System.Runtime.InteropServices;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace ZDesk.Services;

public sealed record ShellOperationResult(
    bool Succeeded,
    bool Aborted,
    int HResult,
    string? ErrorMessage = null,
    IReadOnlyList<string>? ResultPaths = null);

public interface IShellFileOperationService
{
    Task<ShellOperationResult> RenameAsync(string sourcePath, string newName, nint owner);
    Task<ShellOperationResult> CopyAsync(IReadOnlyList<string> sourcePaths, string destinationFolder, nint owner);
    Task<ShellOperationResult> MoveAsync(IReadOnlyList<string> sourcePaths, string destinationFolder, nint owner);
    Task<ShellOperationResult> DeleteAsync(IReadOnlyList<string> paths, nint owner);
}

public sealed class ShellFileOperationService : IShellFileOperationService
{
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmMakeDirectory = 0x0200;
    private const uint FofxShowElevationPrompt = 0x00040000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint FofxAddUndoRecord = 0x20000000;
    private const uint DefaultFlags = FofAllowUndo | FofNoConfirmMakeDirectory |
                                      FofxShowElevationPrompt | FofxAddUndoRecord;

    public async Task<ShellOperationResult> RenameAsync(string sourcePath, string newName, nint owner)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sourcePath))
                     ?? throw new ArgumentException("The source path has no parent directory.", nameof(sourcePath));
        var expectedPath = Path.Combine(parent, newName);
        var before = EnumerateEntries(parent);
        var result = await RunStaAsync(operation =>
        {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(DefaultFlags);
            var item = CreateShellItem(sourcePath);
            try { return operation.RenameItem(item, newName, nint.Zero); }
            finally { Marshal.FinalReleaseComObject(item); }
        });
        if (!result.Succeeded) return result;

        var resolvedPath = ResolveRenamePath(sourcePath, expectedPath, parent, before);
        return result with { ResultPaths = [resolvedPath] };
    }

    public Task<ShellOperationResult> CopyAsync(IReadOnlyList<string> sourcePaths, string destinationFolder, nint owner) =>
        TransferAsync(sourcePaths, destinationFolder, owner, move: false);

    public Task<ShellOperationResult> MoveAsync(IReadOnlyList<string> sourcePaths, string destinationFolder, nint owner) =>
        TransferAsync(sourcePaths, destinationFolder, owner, move: true);

    public Task<ShellOperationResult> DeleteAsync(IReadOnlyList<string> paths, nint owner) =>
        RunStaAsync(operation =>
        {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(DefaultFlags | FofxRecycleOnDelete);
            foreach (var path in Existing(paths))
            {
                var item = CreateShellItem(path);
                try
                {
                    var result = operation.DeleteItem(item, nint.Zero);
                    if (result < 0) return result;
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            return 0;
        });

    private static Task<ShellOperationResult> TransferAsync(
        IReadOnlyList<string> sourcePaths, string destinationFolder, nint owner, bool move) =>
        RunStaAsync(operation =>
        {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(DefaultFlags);
            var destination = CreateShellItem(destinationFolder);
            try
            {
                foreach (var path in Existing(sourcePaths))
                {
                    var source = CreateShellItem(path);
                    try
                    {
                        var result = move
                            ? operation.MoveItem(source, destination, null, nint.Zero)
                            : operation.CopyItem(source, destination, null, nint.Zero);
                        if (result < 0) return result;
                    }
                    finally { Marshal.FinalReleaseComObject(source); }
                }
                return 0;
            }
            finally { Marshal.FinalReleaseComObject(destination); }
        });

    private static IEnumerable<string> Existing(IEnumerable<string> paths) =>
        paths.Where(path => File.Exists(path) || Directory.Exists(path));

    private static HashSet<string> EnumerateEntries(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ResolveRenamePath(
        string sourcePath,
        string expectedPath,
        string parent,
        IReadOnlySet<string> before)
    {
        if (!before.Contains(expectedPath) && (File.Exists(expectedPath) || Directory.Exists(expectedPath)))
            return expectedPath;

        var added = EnumerateEntries(parent)
            .Where(path => !before.Contains(path))
            .ToArray();
        if (added.Length == 1) return added[0];
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath) &&
            (File.Exists(expectedPath) || Directory.Exists(expectedPath))) return expectedPath;
        return expectedPath;
    }

    private static Task<ShellOperationResult> RunStaAsync(Func<IFileOperation, int> queue)
    {
        var completion = new TaskCompletionSource<ShellOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IFileOperation? operation = null;
            try
            {
                operation = (IFileOperation)(object)new FileOperationComObject();
                var result = queue(operation);
                if (result >= 0) result = operation.PerformOperations();
                var abortedResult = operation.GetAnyOperationsAborted(out var aborted);
                if (result >= 0 && abortedResult < 0) result = abortedResult;
                completion.TrySetResult(new ShellOperationResult(result >= 0 && !aborted, aborted, result,
                    result < 0 ? Marshal.GetExceptionForHR(result)?.Message : null));
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
            {
                completion.TrySetResult(new ShellOperationResult(false, false, ex.HResult, ex.Message));
            }
            finally
            {
                if (operation is not null) Marshal.FinalReleaseComObject(operation);
            }
        })
        {
            IsBackground = true,
            Name = "ZDesk Shell file operation"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IShellItem CreateShellItem(string path)
    {
        var id = typeof(IShellItem).GUID;
        var result = SHCreateItemFromParsingName(path, nint.Zero, ref id, out var item);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        return item;
    }

    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    private sealed class FileOperationComObject { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(nint progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(nint progressDialog);
        [PreserveSig] int SetProperties(nint propertyChangeArray);
        [PreserveSig] int SetOwnerWindow(nint owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, nint progressSink);
        [PreserveSig] int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, nint progressSink);
        [PreserveSig] int MoveItems(nint items, IShellItem destinationFolder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, nint progressSink);
        [PreserveSig] int CopyItems(nint items, IShellItem destinationFolder);
        [PreserveSig] int DeleteItem(IShellItem item, nint progressSink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, nint progressSink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, ref Guid handlerId, ref Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint displayName, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);
}
