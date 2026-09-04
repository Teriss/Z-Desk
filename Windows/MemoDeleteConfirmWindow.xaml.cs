using System.Windows;
using System.Windows.Input;

namespace ZDesk.Windows;

public partial class MemoDeleteConfirmWindow : Window
{
    public MemoDeleteConfirmWindow(string noteTitle)
    {
        (Application.Current as ZDesk.App)?.EnsureBaseResources();
        InitializeComponent();
        MessageText.Text = $"“{noteTitle}”的正文、图片和备份都会永久删除。此操作无法撤销。";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
