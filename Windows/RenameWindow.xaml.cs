using System.IO;
using System.Windows;

namespace ZDesk.Windows;

public partial class RenameWindow : Window
{
    public string NewName => NameTextBox.Text.Trim();
    public RenameWindow(string currentName)
    {
        InitializeComponent();
        NameTextBox.Text = currentName;
        NameTextBox.SelectAll();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewName) || NewName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ErrorText.Text = "名称为空或包含无效字符。";
            return;
        }
        DialogResult = true;
    }
}
