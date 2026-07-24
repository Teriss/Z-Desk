using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZDesk.Services;

public static class ShellIconService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiTypeName = 0x000000400;
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TypeNameCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string path, bool isDirectory)
    {
        var key = isDirectory ? $"directory:{path}" : path;
        return Cache.GetOrAdd(key, _ => LoadIcon(path));
    }

    public static ImageSource? GetDisplayImage(string path, bool isDirectory, int requestedSize = 96)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            // Explorer shortcuts may point to an executable with no usable
            // thumbnail while carrying their real icon in IconLocation.
            var shortcutIcon = LoadShellIcon(path);
            if (shortcutIcon is not null) return shortcutIcon;
            if (TryResolveShortcutTarget(path, out var target))
                return GetDisplayImage(target, Directory.Exists(target), requestedSize);
            return GetIcon(path, isDirectory);
        }
        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var urlStamp = GetLastWriteStamp(path);
            var urlKey = $"url-icon:{path}:{urlStamp}";
            return Cache.GetOrAdd(urlKey, _ => LoadInternetShortcutIcon(path) ?? LoadIcon(path));
        }
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { stamp = 0; }
        var size = Math.Clamp(requestedSize, 16, 256);
        var key = $"thumbnail:{path}:{stamp}:{size}";
        return Cache.GetOrAdd(key, _ => LoadShellThumbnail(path, size) ?? LoadIcon(path));
    }

    public static string GetTypeName(string path, bool isDirectory)
    {
        var extension = isDirectory ? "<directory>" : Path.GetExtension(path);
        return TypeNameCache.GetOrAdd(extension, _ =>
        {
            var result = SHGetFileInfo(path, 0, out var info,
                (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiTypeName);
            if (result != nint.Zero && !string.IsNullOrWhiteSpace(info.TypeName)) return info.TypeName;
            if (isDirectory) return "文件夹";
            return string.IsNullOrWhiteSpace(extension) ? "文件" : $"{extension.TrimStart('.').ToUpperInvariant()} 文件";
        });
    }

    private static ImageSource? LoadIcon(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && TryResolveShortcutTarget(path, out var target))
        {
            var targetIcon = LoadShellIcon(target);
            if (targetIcon is not null) return targetIcon;
        }
        return LoadShellIcon(path);
    }

    private static ImageSource? LoadInternetShortcutIcon(string shortcutPath)
    {
        string? iconFile = null;
        var iconIndex = 0;
        try
        {
            foreach (var line in File.ReadLines(shortcutPath))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim().Trim('"');
                if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) iconFile = value;
                else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out iconIndex);
            }

            if (string.IsNullOrWhiteSpace(iconFile)) return null;
            iconFile = Environment.ExpandEnvironmentVariables(iconFile);
            if (!Path.IsPathRooted(iconFile)) iconFile = Path.Combine(Path.GetDirectoryName(shortcutPath) ?? string.Empty, iconFile);
            if (!File.Exists(iconFile)) return null;
            return LoadIndexedIcon(iconFile, iconIndex);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static ImageSource? LoadIndexedIcon(string path, int index)
    {
        nint large = nint.Zero;
        nint small = nint.Zero;
        try
        {
            if (ExtractIconEx(path, index, out large, out small, 1) == 0) return null;
            var handle = large != nint.Zero ? large : small;
            if (handle == nint.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        finally
        {
            if (large != nint.Zero) DestroyIcon(large);
            if (small != nint.Zero) DestroyIcon(small);
        }
    }

    private static long GetLastWriteStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return 0; }
    }

    private static ImageSource? LoadShellIcon(string path)
    {
        // Intentionally omit SHGFI_LINKOVERLAY/SHGFI_ADDOVERLAYS. The container
        // already communicates that an item is a shortcut; the desktop arrow is visual noise here.
        var flags = ShgfiIcon | ShgfiLargeIcon;

        var result = SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags);
        if (result == nint.Zero || info.Icon == nint.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    private static ImageSource? LoadShellThumbnail(string path, int size)
    {
        var interfaceId = typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory? factory = null;
        nint bitmap = nint.Zero;
        try
        {
            var result = SHCreateItemFromParsingName(path, nint.Zero, ref interfaceId, out factory);
            if (result < 0 || factory is null) return null;
            result = factory.GetImage(new NativeSize(size, size), ShellItemImageFlags.ResizeToFit, out bitmap);
            if (result < 0 || bitmap == nint.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (bitmap != nint.Zero) DeleteObject(bitmap);
            if (factory is not null && Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
        }
    }

    private static bool TryResolveShortcutTarget(string shortcutPath, out string target)
    {
        target = string.Empty;
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [shortcutPath]);
            target = shortcut?.GetType().GetProperty("TargetPath")?.GetValue(shortcut) as string ?? string.Empty;
            return File.Exists(target) || Directory.Exists(target);
        }
        catch { return false; }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [Flags]
    private enum ShellItemImageFlags
    {
        ResizeToFit = 0x0000
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFlags flags, out nint bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out nint largeIcon, out nint smallIcon, uint iconCount);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
