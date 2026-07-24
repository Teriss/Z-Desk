using System.Windows;
using ZDesk.Models;

namespace ZDesk.Windows;

public partial class ConflictWindow : Window
{
    public FileConflictStrategy Strategy { get; private set; } = FileConflictStrategy.Rename;
    public ConflictWindow() => InitializeComponent();
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Strategy = OverwriteRadio.IsChecked == true
            ? FileConflictStrategy.Overwrite
            : SkipRadio.IsChecked == true ? FileConflictStrategy.Skip : FileConflictStrategy.Rename;
        if (Strategy == FileConflictStrategy.Overwrite && MessageBox.Show(
            this, "覆盖会永久替换目标中的同名项目，且无法撤销。确定继续？", "确认覆盖",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        DialogResult = true;
    }
}
