using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace ZDesk.Services;

public static class ShellFileService
{
    public static void Open(string path)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            File = path,
            Show = 1
        };
        if (!ShellExecuteEx(ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void MoveToRecycleBin(string path)
    {
        if (Directory.Exists(path))
        {
            FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
        }
        else if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    public static void ShowProperties(string path)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = 0x0000000C,
            Verb = "properties",
            File = path,
            Show = 1
        };
        if (!ShellExecuteEx(ref info))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public nint Instance;
        public nint IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint ClassKey;
        public uint HotKey;
        public nint Icon;
        public nint Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);
}
