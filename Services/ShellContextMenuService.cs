using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Interop;

namespace ZDesk.Services;

public static class ShellContextMenuService
{
    private const uint CmfNormal = 0x00000000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint MfSeparator = 0x0800;
    private const uint MfString = 0x0000;
    private const uint RenameCommand = 0x7F00;
    private const int SwShowNormal = 1;
    private static int _warmUpStarted;

    public static Task WarmUpAsync(IEnumerable<string> paths)
    {
        var existing = paths.Where(candidate => File.Exists(candidate) || Directory.Exists(candidate)).ToArray();
        var path = existing.FirstOrDefault(candidate =>
                       candidate.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                       candidate.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                   ?? existing.FirstOrDefault();
        if (path is null || Interlocked.Exchange(ref _warmUpStarted, 1) != 0)
            return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                WarmUp(path);
            }
            catch (Exception ex)
            {
                LogService.Warning("Shell context menu warm-up failed", ex);
            }
            finally
            {
                completion.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "ZDesk Shell menu warm-up"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static bool Show(nint owner, IReadOnlyList<string> paths, int screenX, int screenY, Action? renameAction = null)
    {
        var existing = paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        if (existing.Length == 0) return false;

        if (existing.All(IsPhysicalDesktopItem))
            return ShowDesktopItems(owner, existing, screenX, screenY, renameAction);

        var parentPaths = existing.Select(path => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parentPaths.Length > 1)
            return ShowShellItemArray(owner, existing, screenX, screenY);

        var pidls = new List<nint>();
        var childPidls = new List<nint>();
        IShellFolder? parentFolder = null;
        object? contextMenuObject = null;
        nint menu = nint.Zero;
        try
        {
            foreach (var path in existing)
            {
                if (SHParseDisplayName(path, nint.Zero, out var pidl, 0, out _) != 0) continue;
                pidls.Add(pidl);
                var shellFolderId = typeof(IShellFolder).GUID;
                if (SHBindToParent(pidl, ref shellFolderId, out var folder, out var child) != 0) continue;
                parentFolder ??= folder;
                if (!ReferenceEquals(parentFolder, folder)) Marshal.ReleaseComObject(folder);
                childPidls.Add(child);
            }

            if (parentFolder is null || childPidls.Count == 0) return false;
            var contextMenuId = typeof(IContextMenu).GUID;
            var children = childPidls.ToArray();
            if (parentFolder.GetUIObjectOf(owner, (uint)children.Length, children, ref contextMenuId, nint.Zero, out var contextPointer) != 0)
                return false;

            contextMenuObject = Marshal.GetObjectForIUnknown(contextPointer);
            Marshal.Release(contextPointer);
            var contextMenu = (IContextMenu)contextMenuObject;
            menu = CreatePopupMenu();
            if (menu == nint.Zero) return false;
            contextMenu.QueryContextMenu(menu, 0, 1, 0x7DFF, CmfNormal);
            if (existing.Length == 1 && renameAction is not null)
            {
                AppendMenu(menu, MfSeparator, nint.Zero, null);
                AppendMenu(menu, MfString, new nint(RenameCommand), "重命名");
            }
            var command = TrackShellMenu(contextMenuObject, menu, screenX, screenY, owner);
            if (command == 0) return true;
            if (command == RenameCommand)
            {
                renameAction?.Invoke();
                return true;
            }

            var invoke = new CommandInfo
            {
                Size = Marshal.SizeOf<CommandInfo>(),
                Window = owner,
                Verb = new nint(command - 1),
                Show = SwShowNormal
            };
            contextMenu.InvokeCommand(ref invoke);
            return true;
        }
        finally
        {
            if (menu != nint.Zero) DestroyMenu(menu);
            if (contextMenuObject is not null) Marshal.FinalReleaseComObject(contextMenuObject);
            if (parentFolder is not null) Marshal.FinalReleaseComObject(parentFolder);
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static bool ShowShellItemArray(nint owner, IReadOnlyList<string> paths, int screenX, int screenY)
    {
        var pidls = new List<nint>();
        IShellItemArray? itemArray = null;
        object? contextMenuObject = null;
        nint menu = nint.Zero;
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, nint.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
            }
            if (pidls.Count == 0 || SHCreateShellItemArrayFromIDLists(
                    (uint)pidls.Count, pidls.ToArray(), out itemArray) != 0) return false;

            var handler = new Guid("3981E225-FE43-4D85-BB14-33A1FBD9A7BC");
            var contextMenuId = typeof(IContextMenu).GUID;
            if (itemArray.BindToHandler(nint.Zero, ref handler, ref contextMenuId, out var contextPointer) != 0)
                return false;
            contextMenuObject = Marshal.GetObjectForIUnknown(contextPointer);
            Marshal.Release(contextPointer);
            var contextMenu = (IContextMenu)contextMenuObject;
            menu = CreatePopupMenu();
            if (menu == nint.Zero) return false;
            contextMenu.QueryContextMenu(menu, 0, 1, 0x7DFF, CmfNormal);
            var command = TrackShellMenu(contextMenuObject, menu, screenX, screenY, owner);
            if (command == 0) return true;
            var invoke = new CommandInfo
            {
                Size = Marshal.SizeOf<CommandInfo>(),
                Window = owner,
                Verb = new nint(command - 1),
                Show = SwShowNormal
            };
            contextMenu.InvokeCommand(ref invoke);
            return true;
        }
        finally
        {
            if (menu != nint.Zero) DestroyMenu(menu);
            if (contextMenuObject is not null) Marshal.FinalReleaseComObject(contextMenuObject);
            if (itemArray is not null) Marshal.FinalReleaseComObject(itemArray);
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static bool ShowDesktopItems(nint owner, IReadOnlyList<string> paths, int screenX, int screenY, Action? renameAction)
    {
        var pidls = new List<nint>();
        object? contextMenuObject = null;
        IShellFolder? desktopFolder = null;
        nint menu = nint.Zero;
        try
        {
            if (SHGetDesktopFolder(out desktopFolder) != 0) return false;
            var children = new List<nint>();
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, nint.Zero, out var pidl, 0, out _) != 0) continue;
                pidls.Add(pidl);
                children.Add(ILFindLastID(pidl));
            }
            if (children.Count == 0) return false;
            var contextMenuId = typeof(IContextMenu).GUID;
            if (desktopFolder.GetUIObjectOf(owner, (uint)children.Count, children.ToArray(), ref contextMenuId,
                    nint.Zero, out var contextPointer) != 0) return false;
            contextMenuObject = Marshal.GetObjectForIUnknown(contextPointer);
            Marshal.Release(contextPointer);
            var contextMenu = (IContextMenu)contextMenuObject;
            menu = CreatePopupMenu();
            if (menu == nint.Zero) return false;
            contextMenu.QueryContextMenu(menu, 0, 1, 0x7DFF, CmfNormal);
            if (paths.Count == 1 && renameAction is not null)
            {
                AppendMenu(menu, MfSeparator, nint.Zero, null);
                AppendMenu(menu, MfString, new nint(RenameCommand), "重命名");
            }
            var command = TrackShellMenu(contextMenuObject, menu, screenX, screenY, owner);
            if (command == 0) return true;
            if (command == RenameCommand)
            {
                renameAction?.Invoke();
                return true;
            }
            var invoke = new CommandInfo { Size = Marshal.SizeOf<CommandInfo>(), Window = owner, Verb = new nint(command - 1), Show = SwShowNormal };
            contextMenu.InvokeCommand(ref invoke);
            return true;
        }
        finally
        {
            if (menu != nint.Zero) DestroyMenu(menu);
            if (contextMenuObject is not null) Marshal.FinalReleaseComObject(contextMenuObject);
            if (desktopFolder is not null) Marshal.FinalReleaseComObject(desktopFolder);
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static bool IsPhysicalDesktopItem(string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        var user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        return string.Equals(parent, user, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parent, common, StringComparison.OrdinalIgnoreCase);
    }

    private static void WarmUp(string path)
    {
        nint pidl = nint.Zero;
        nint contextPointer = nint.Zero;
        nint menu = nint.Zero;
        IShellFolder? parentFolder = null;
        object? contextMenuObject = null;
        try
        {
            if (SHParseDisplayName(path, nint.Zero, out pidl, 0, out _) != 0) return;
            var shellFolderId = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidl, ref shellFolderId, out parentFolder, out var child) != 0) return;
            var contextMenuId = typeof(IContextMenu).GUID;
            if (parentFolder.GetUIObjectOf(nint.Zero, 1, [child], ref contextMenuId, nint.Zero, out contextPointer) != 0)
                return;
            contextMenuObject = Marshal.GetObjectForIUnknown(contextPointer);
            Marshal.Release(contextPointer);
            contextPointer = nint.Zero;
            menu = CreatePopupMenu();
            if (menu == nint.Zero) return;
            ((IContextMenu)contextMenuObject).QueryContextMenu(menu, 0, 1, 0x7DFF, CmfNormal);
        }
        finally
        {
            if (menu != nint.Zero) DestroyMenu(menu);
            if (contextMenuObject is not null) Marshal.FinalReleaseComObject(contextMenuObject);
            if (contextPointer != nint.Zero) Marshal.Release(contextPointer);
            if (parentFolder is not null) Marshal.FinalReleaseComObject(parentFolder);
            if (pidl != nint.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static uint TrackShellMenu(object contextMenu, nint menu, int x, int y, nint owner)
    {
        var source = HwndSource.FromHwnd(owner);
        HwndSourceHook? hook = null;
        if (source is not null && (contextMenu is IContextMenu2 || contextMenu is IContextMenu3))
        {
            hook = (nint _, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message is not (0x0117 or 0x002B or 0x002C or 0x0120)) return nint.Zero;
                if (contextMenu is IContextMenu3 menu3)
                {
                    var result = menu3.HandleMenuMsg2((uint)message, wParam, lParam, out var menuResult);
                    handled = result >= 0;
                    return menuResult;
                }
                if (contextMenu is IContextMenu2 menu2)
                {
                    handled = menu2.HandleMenuMsg((uint)message, wParam, lParam) >= 0;
                }
                return nint.Zero;
            };
            source.AddHook(hook);
        }

        try { return TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCommand, x, y, owner, nint.Zero); }
        finally
        {
            if (hook is not null) source?.RemoveHook(hook);
        }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(nint hwnd, nint bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName, ref uint eaten, out nint itemIdList, ref uint attributes);
        [PreserveSig] int EnumObjects(nint hwnd, uint flags, out nint enumIdList);
        [PreserveSig] int BindToObject(nint itemIdList, nint bindContext, ref Guid interfaceId, out nint result);
        [PreserveSig] int BindToStorage(nint itemIdList, nint bindContext, ref Guid interfaceId, out nint result);
        [PreserveSig] int CompareIDs(nint lParam, nint first, nint second);
        [PreserveSig] int CreateViewObject(nint hwnd, ref Guid interfaceId, out nint result);
        [PreserveSig] int GetAttributesOf(uint count, nint itemIdLists, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] nint[] itemIdLists, ref Guid interfaceId, nint reserved, out nint result);
        [PreserveSig] int GetDisplayNameOf(nint itemIdList, uint flags, out nint name);
        [PreserveSig] int SetNameOf(nint hwnd, nint itemIdList, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out nint renamedItem);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint firstCommand, uint lastCommand, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo commandInfo);
        [PreserveSig] int GetCommandString(nuint command, uint flags, nint reserved, nint name, uint maximumCount);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F4-0000-0000-C000-000000000046")]
    private interface IContextMenu2 : IContextMenu
    {
        [PreserveSig] new int QueryContextMenu(nint menu, uint index, uint firstCommand, uint lastCommand, uint flags);
        [PreserveSig] new int InvokeCommand(ref CommandInfo commandInfo);
        [PreserveSig] new int GetCommandString(nuint command, uint flags, nint reserved, nint name, uint maximumCount);
        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    private interface IContextMenu3 : IContextMenu2
    {
        [PreserveSig] new int QueryContextMenu(nint menu, uint index, uint firstCommand, uint lastCommand, uint flags);
        [PreserveSig] new int InvokeCommand(ref CommandInfo commandInfo);
        [PreserveSig] new int GetCommandString(nuint command, uint flags, nint reserved, nint name, uint maximumCount);
        [PreserveSig] new int HandleMenuMsg(uint message, nint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, nint wParam, nint lParam, out nint result);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handlerId, ref Guid interfaceId, out nint result);
        [PreserveSig] int GetPropertyStore(uint flags, ref Guid interfaceId, out nint result);
        [PreserveSig] int GetPropertyDescriptionList(nint propertyKey, ref Guid interfaceId, out nint result);
        [PreserveSig] int GetAttributes(uint flags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out nint shellItem);
        [PreserveSig] int EnumItems(out nint enumShellItems);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CommandInfo
    {
        public int Size;
        public uint Mask;
        public nint Window;
        public nint Verb;
        [MarshalAs(UnmanagedType.LPStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? Directory;
        public int Show;
        public uint HotKey;
        public nint Icon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint itemIdList, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder desktopFolder);

    [DllImport("shell32.dll")]
    private static extern nint ILFindLastID(nint itemIdList);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(nint itemIdList, ref Guid interfaceId, out IShellFolder parent, out nint child);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateShellItemArrayFromIDLists(
        uint itemCount,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] itemIdLists,
        out IShellItemArray shellItemArray);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nint identifier, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
}
