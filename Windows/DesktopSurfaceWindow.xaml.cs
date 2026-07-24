using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ZDesk.Models;
using ZDesk.Services;

namespace ZDesk.Windows;

public partial class DesktopSurfaceWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const string InternalFormat = "ZDesk.DesktopSurfaceItems";
    private readonly DesktopFileService _desktopFiles;
    private readonly AppState _state;
    private readonly Func<HashSet<string>> _assignedPaths;
    private readonly FileTransferService _transferService = new();
    private readonly ObservableCollection<DesktopIconItem> _items = [];
    private Point _mouseDown;
    private bool _selecting;
    private bool _rightSelecting;

    public event EventHandler? LayoutChanged;
    public event Action<IReadOnlyList<string>>? UnassignRequested;
    public event Action<GroupKind, Rect, IReadOnlyList<string>>? CreateLayoutRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public DesktopSurfaceWindow(DesktopFileService desktopFiles, AppState state, Func<HashSet<string>> assignedPaths)
    {
        InitializeComponent();
        _desktopFiles = desktopFiles;
        _state = state;
        _assignedPaths = assignedPaths;
        IconList.ItemsSource = _items;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        _desktopFiles.Changed += DesktopFiles_Changed;
        RefreshItems();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(HitTestHook);
    }

    private IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;
        var raw = lParam.ToInt64();
        var point = PointFromScreen(new Point((short)(raw & 0xffff), (short)((raw >> 16) & 0xffff)));
        var hit = InputHitTest(point) as DependencyObject;
        if (hit is null || ReferenceEquals(hit, Surface))
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }

    public void RefreshItems()
    {
        var assigned = _assignedPaths();
        var paths = _desktopFiles.EnumerateItems()
            .Where(path => !assigned.Contains(path))
            .ToArray();

        var selected = IconList.SelectedItems.OfType<DesktopIconItem>()
            .Select(item => item.Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var placements = _state.DesktopIconPlacements
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        _items.Clear();
        var rows = Math.Max(1, (int)Math.Floor((Height - 32) / 90));
        var newIndex = 0;
        foreach (var path in paths)
        {
            if (!placements.TryGetValue(path, out var placement))
            {
                placement = new DesktopIconPlacement
                {
                    Path = path,
                    X = 16 + (newIndex / rows) * 96,
                    Y = 16 + (newIndex % rows) * 90
                };
                _state.DesktopIconPlacements.Add(placement);
                newIndex++;
            }

            var item = new DesktopIconItem(
                new FileEntry(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, Directory.Exists(path)),
                placement);
            _items.Add(item);
            if (selected.Contains(path)) IconList.SelectedItems.Add(item);
        }

        _state.DesktopIconPlacements.RemoveAll(item =>
            !File.Exists(item.Path) && !Directory.Exists(item.Path));
    }

    public void EnsureVisibleBounds()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        foreach (var item in _items)
        {
            item.X = Math.Clamp(item.X, 0, Math.Max(0, Width - 88));
            item.Y = Math.Clamp(item.Y, 0, Math.Max(0, Height - 82));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _desktopFiles.Changed -= DesktopFiles_Changed;
        base.OnClosed(e);
    }

    private void DesktopFiles_Changed(object? sender, EventArgs e) => RefreshItems();

    private void IconList_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDown = e.GetPosition(Surface);
    }

    private void IconList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(Surface);
        if (Math.Abs(current.X - _mouseDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _mouseDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var selected = IconList.SelectedItems.OfType<DesktopIconItem>().ToArray();
        if (selected.Length == 0) return;
        var paths = selected.Select(item => item.Entry.FullPath).ToArray();
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        data.SetData(InternalFormat, paths);
        var effect = DragDrop.DoDragDrop(IconList, data, DragDropEffects.Move);
        if (effect == DragDropEffects.Move) RefreshItems();
    }

    private void Surface_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Surface_Drop(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop, true) as string[] ?? [];
        if (paths.Length == 0) return;

        if (e.Data.GetData(InternalFormat) is string[] internalPaths)
        {
            PositionItems(internalPaths, e.GetPosition(Surface));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        var alreadyDesktop = paths.Where(_desktopFiles.IsDesktopPath).ToArray();
        if (alreadyDesktop.Length > 0) UnassignRequested?.Invoke(alreadyDesktop);
        var toMove = paths.Where(path => !_desktopFiles.IsDesktopPath(path)).ToArray();
        if (toMove.Length > 0)
        {
            await _transferService.ExecuteAsync(toMove, _desktopFiles.UserDesktop, FileTransferMode.Move);
        }
        RefreshItems();
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void PositionItems(IEnumerable<string> paths, Point drop)
    {
        var index = 0;
        foreach (var path in paths)
        {
            var item = _items.FirstOrDefault(candidate =>
                string.Equals(candidate.Entry.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (item is null) continue;
            item.X = Math.Clamp(drop.X + (index % 4) * 92, 0, Math.Max(0, Width - 88));
            item.Y = Math.Clamp(drop.Y + (index / 4) * 86, 0, Math.Max(0, Height - 82));
            index++;
        }
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void IconList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IconList.SelectedItem is not DesktopIconItem item) return;
        try { Process.Start(new ProcessStartInfo(item.Entry.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { LogService.Warning($"Could not open desktop item: {item.Entry.FullPath}", ex); }
    }

    private void IconList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = ItemsControl.ContainerFromElement(IconList, source) as ListBoxItem;
        if (container is null) return;
        if (!container.IsSelected)
        {
            IconList.SelectedItems.Clear();
            container.IsSelected = true;
        }
        var paths = IconList.SelectedItems.OfType<DesktopIconItem>().Select(item => item.Entry.FullPath).ToArray();
        var screen = PointToScreen(e.GetPosition(this));
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (ShellContextMenuService.Show(handle, paths, (int)screen.X, (int)screen.Y)) e.Handled = true;
    }

    private void IconList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        var selected = IconList.SelectedItems.OfType<DesktopIconItem>().ToArray();
        if (selected.Length == 0) return;
        if (MessageBox.Show($"将选中的 {selected.Length} 项移到回收站？", "删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var item in selected) ShellFileService.MoveToRecycleBin(item.Entry.FullPath);
        RefreshItems();
        e.Handled = true;
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginSelection(e, rightButton: false);
    private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => BeginSelection(e, rightButton: true);

    private void BeginSelection(MouseButtonEventArgs e, bool rightButton)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) return;
        _mouseDown = e.GetPosition(Surface);
        _selecting = true;
        _rightSelecting = rightButton;
        IconList.SelectedItems.Clear();
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting) return;
        var rectangle = Normalize(_mouseDown, e.GetPosition(Surface));
        SelectionRectangle.Visibility = Visibility.Visible;
        SelectionRectangle.Margin = new Thickness(rectangle.X, rectangle.Y, 0, 0);
        SelectionRectangle.Width = rectangle.Width;
        SelectionRectangle.Height = rectangle.Height;
        foreach (var item in _items)
        {
            var container = (ListBoxItem?)IconList.ItemContainerGenerator.ContainerFromItem(item);
            if (container is not null) container.IsSelected = rectangle.IntersectsWith(new Rect(item.X, item.Y, 88, 82));
        }
    }

    private void Surface_MouseButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        var bounds = Normalize(_mouseDown, e.GetPosition(Surface));
        _selecting = false;
        Surface.ReleaseMouseCapture();
        SelectionRectangle.Visibility = Visibility.Collapsed;
        if (_rightSelecting)
        {
            ShowDesktopMenu(e.GetPosition(this), bounds);
            e.Handled = true;
        }
        _rightSelecting = false;
    }

    private void ShowDesktopMenu(Point position, Rect selectionBounds)
    {
        var selectedPaths = IconList.SelectedItems.OfType<DesktopIconItem>()
            .Select(item => item.Entry.FullPath).ToArray();
        var menu = new ContextMenu();
        AddMenuItem(menu, "新建引用布局", () => CreateLayoutRequested?.Invoke(GroupKind.Empty, selectionBounds, selectedPaths));
        AddMenuItem(menu, "新建文件夹布局", () => CreateLayoutRequested?.Invoke(GroupKind.Folder, selectionBounds, selectedPaths));
        if (selectedPaths.Length > 0) AddMenuItem(menu, "将所选图标创建为布局", () => CreateLayoutRequested?.Invoke(GroupKind.Empty, selectionBounds, selectedPaths));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "刷新", RefreshItems);
        AddMenuItem(menu, "设置", () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        AddMenuItem(menu, "退出 Z-Desk", () => ExitRequested?.Invoke(this, EventArgs.Empty));
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static void AddMenuItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static Rect Normalize(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X),
        Math.Abs(first.Y - second.Y));

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    public sealed class DesktopIconItem : INotifyPropertyChanged
    {
        private readonly DesktopIconPlacement _placement;
        public FileEntry Entry { get; }
        public double X { get => _placement.X; set { if (Math.Abs(_placement.X - value) < 0.1) return; _placement.X = value; OnChanged(); } }
        public double Y { get => _placement.Y; set { if (Math.Abs(_placement.Y - value) < 0.1) return; _placement.Y = value; OnChanged(); } }
        public DesktopIconItem(FileEntry entry, DesktopIconPlacement placement) { Entry = entry; _placement = placement; }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
