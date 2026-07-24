using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ZDesk.Models;
using ZDesk.Services;

namespace ZDesk.Windows;

public partial class RulesWindow : Window
{
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private readonly RuleEngine _engine = new();
    private readonly RuleHistoryService _history = new();
    private IReadOnlyList<RuleMatch> _preview = [];
    public ObservableCollection<ClassificationRule> Rules { get; }

    public RulesWindow(IEnumerable<ClassificationRule> rules)
    {
        InitializeComponent();
        Rules = new ObservableCollection<ClassificationRule>(rules.Select(Clone));
        RulesGrid.ItemsSource = Rules;
        Loaded += async (_, _) =>
        {
            var latest = (await _history.LoadAsync()).FirstOrDefault();
            HistoryText.Text = latest is null
                ? "暂无执行历史"
                : $"上次执行：{latest.ExecutedAt.LocalDateTime:g} · 移动 {latest.Moved}/{latest.Total} 项";
        };
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Rules.Add(new ClassificationRule());
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is ClassificationRule rule) Rules.Remove(rule);
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.CommitEdit();
        _preview = _engine.Preview(Rules);
        StatusText.Text = _preview.Count == 0 ? "没有匹配项目" : $"预览：将移动 {_preview.Count} 个文件（不会覆盖同名文件）";
    }
    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (_preview.Count == 0) { Preview_Click(sender, e); if (_preview.Count == 0) return; }
        var confirm = MessageBox.Show(this, $"执行预览中的 {_preview.Count} 项移动？", "执行规则", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        var result = await _engine.ExecuteAsync(_preview);
        await _history.AppendAsync(result);
        var reversible = _preview.Where(match => File.Exists(match.TargetPath) && !File.Exists(match.SourcePath)).ToArray();
        if (reversible.Length > 0)
        {
            OperationHistoryService.Shared.Record("最近一次规则整理", () => Task.Run(() =>
            {
                foreach (var match in reversible.Reverse())
                {
                    if (!File.Exists(match.TargetPath) || File.Exists(match.SourcePath)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(match.SourcePath)!);
                    File.Move(match.TargetPath, match.SourcePath);
                }
            }));
        }
        StatusText.Text = $"已移动 {result.Moved}/{result.Total} 项，失败 {result.Issues.Count} 项";
        _preview = [];
    }
    private void Save_Click(object sender, RoutedEventArgs e) { RulesGrid.CommitEdit(); DialogResult = true; }
    private static ClassificationRule Clone(ClassificationRule r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Enabled = r.Enabled,
        SourceFolder = r.SourceFolder,
        TargetFolder = r.TargetFolder,
        Extensions = r.Extensions,
        NameContains = r.NameContains,
        MinimumAgeDays = r.MinimumAgeDays,
        ExcludeNameContains = r.ExcludeNameContains
    };
}
