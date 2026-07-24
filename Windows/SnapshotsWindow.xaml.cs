using System.Windows;
using ZDesk.Models;
using ZDesk.Services;

namespace ZDesk.Windows;

public partial class SnapshotsWindow : Window
{
    private readonly SnapshotService _service;
    private readonly IReadOnlyList<GroupDefinition> _current;
    public LayoutSnapshot? SelectedSnapshot { get; private set; }
    public SnapshotsWindow(SnapshotService service, IReadOnlyList<GroupDefinition> current)
    {
        InitializeComponent(); _service = service; _current = current; Loaded += async (_, _) => await RefreshAsync();
    }
    private async void Create_Click(object sender, RoutedEventArgs e) { await _service.SaveAsync(NameBox.Text, _current); await RefreshAsync(); }
    private void Restore_Click(object sender, RoutedEventArgs e) { if (SnapshotList.SelectedItem is LayoutSnapshot snapshot) { SelectedSnapshot = snapshot; DialogResult = true; } }
    private async Task RefreshAsync() => SnapshotList.ItemsSource = await _service.LoadAllAsync();
}
