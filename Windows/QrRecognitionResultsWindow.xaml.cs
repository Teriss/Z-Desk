using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZDesk.Models;

namespace ZDesk.Windows;

public partial class QrRecognitionResultsWindow : Window
{
    private readonly IReadOnlyList<QrCodeRecognitionResult> _results;

    public QrRecognitionResultsWindow(IReadOnlyList<QrCodeRecognitionResult> results)
    {
        InitializeComponent();
        _results = results;
        ResultsList.ItemsSource = new ObservableCollection<ResultRow>(
            results.Select((result, index) => new ResultRow(index + 1, result.Text)));
        SummaryText.Text = results.Count == 0 ? "没有找到二维码" : $"识别到 {results.Count} 个二维码";
        EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CopyAllButton.IsEnabled = results.Count > 0;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CopyItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text }) Copy(text);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) =>
        Copy(string.Join(Environment.NewLine + Environment.NewLine, _results.Select(result => result.Text)));

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            StatusText.Text = "已复制到剪贴板";
        }
        catch (ExternalException)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.LightPink;
            StatusText.Text = "剪贴板正被其他程序占用，请重试。";
        }
    }

    private sealed record ResultRow(int Index, string Text);
}
