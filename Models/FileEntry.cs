using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ZDesk.Services;

namespace ZDesk.Models;

public sealed class FileEntry : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim IconLoadGate = new(4, 4);
    private static readonly SemaphoreSlim MetadataLoadGate = new(8, 8);
    private ImageSource? _iconSource;
    private string _typeName = string.Empty;
    private string _sizeText = string.Empty;
    private string _modifiedText = string.Empty;
    private DateTime _modifiedTime;
    private long? _sizeBytes;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _metadataLoadTask;

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string TypeName { get => _typeName; private set { if (_typeName == value) return; _typeName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailSummary)); } }
    public string SizeText { get => _sizeText; private set { if (_sizeText == value) return; _sizeText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailSummary)); } }
    public string ModifiedText { get => _modifiedText; private set { if (_modifiedText == value) return; _modifiedText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailSummary)); } }
    public DateTime ModifiedTime { get => _modifiedTime; private set { if (_modifiedTime == value) return; _modifiedTime = value; OnPropertyChanged(); } }
    public long? SizeBytes { get => _sizeBytes; private set { if (_sizeBytes == value) return; _sizeBytes = value; OnPropertyChanged(); } }
    public Task MetadataLoaded => _metadataLoadTask;
    public string DetailSummary => string.Join("  |  ", new[] { TypeName, SizeText, ModifiedText }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public ImageSource? IconSource
    {
        get => _iconSource;
        private set
        {
            if (ReferenceEquals(_iconSource, value)) return;
            _iconSource = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileEntry(string name, string fullPath, bool isDirectory)
    {
        Name = GetDisplayName(fullPath, name);
        FullPath = fullPath;
        IsDirectory = isDirectory;
        // Queue icon extraction behind the first layout pass. A folder with
        // hundreds of entries should render names immediately while shell icon
        // work is throttled in the background.
        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => _ = LoadIconAsync()));
        else
            _ = LoadIconAsync();
        _metadataLoadTask = LoadMetadataAsync();
    }

    /// <summary>Returns the Explorer-style label while keeping the real path unchanged.</summary>
    public static string GetDisplayName(string fullPath, string? fallbackName = null)
    {
        var name = string.IsNullOrWhiteSpace(fallbackName)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath))
            : fallbackName;
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(name);
        return name;
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            await MetadataLoadGate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var metadata = await Task.Run(() =>
            {
                var typeName = ShellIconService.GetTypeName(FullPath, IsDirectory);
                if (IsDirectory)
                {
                    var directory = new DirectoryInfo(FullPath);
                    return (typeName, string.Empty, directory.LastWriteTime, (long?)null);
                }

                var file = new FileInfo(FullPath);
                return (typeName, FormatSize(file.Length), file.LastWriteTime, (long?)file.Length);
            });
            if (_lifetime.IsCancellationRequested) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                TypeName = metadata.typeName;
                SizeText = metadata.Item2;
                ModifiedTime = metadata.Item3;
                ModifiedText = metadata.Item3.ToString("yyyy/M/d HH:mm");
                SizeBytes = metadata.Item4;
            }
            else
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (_lifetime.IsCancellationRequested) return;
                    TypeName = metadata.typeName;
                    SizeText = metadata.Item2;
                    ModifiedTime = metadata.Item3;
                    ModifiedText = metadata.Item3.ToString("yyyy/M/d HH:mm");
                    SizeBytes = metadata.Item4;
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"Could not load file metadata: {FullPath}", ex);
        }
        finally
        {
            MetadataLoadGate.Release();
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private async Task LoadIconAsync()
    {
        try
        {
            await IconLoadGate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var icon = await Task.Run(() => ShellIconService.GetDisplayImage(FullPath, IsDirectory));
            if (_lifetime.IsCancellationRequested) return;
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (!_lifetime.IsCancellationRequested) IconSource = icon;
                });
            }
            else
            {
                IconSource = icon;
            }
        }
        catch (Exception ex)
        {
            LogService.Warning($"Could not load shell icon: {FullPath}", ex);
        }
        finally
        {
            IconLoadGate.Release();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
