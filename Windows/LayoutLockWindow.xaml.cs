using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ZDesk.Models;

namespace ZDesk.Windows;

public partial class LayoutLockWindow : Window
{
    public sealed class LayoutLockOption
    {
        internal GroupDefinition Host { get; }
        internal LayoutTab? Tab { get; }
        public Guid Id => Tab?.Id ?? Host.Id;
        public string Title { get; }
        public bool IsLocked { get; set; }

        internal LayoutLockOption(GroupDefinition host, LayoutTab? tab)
        {
            Host = host;
            Tab = tab;
            Title = tab is null ? host.Title : $"{host.Title} / {tab.Title}";
            IsLocked = tab?.IsRuleLocked ?? host.IsRuleLocked;
        }

        internal void Apply()
        {
            if (Tab is null) Host.IsRuleLocked = IsLocked;
            else Tab.IsRuleLocked = IsLocked;
        }
    }

    public ObservableCollection<LayoutLockOption> Options { get; } = [];

    public LayoutLockWindow(IEnumerable<GroupDefinition> groups)
    {
        InitializeComponent();
        foreach (var option in CreateOptions(groups)) Options.Add(option);
        LayoutOptionsList.ItemsSource = Options;
        LayoutOptionsList.ItemTemplate = CreateItemTemplate();
        UpdateSummary();
    }

    public static IEnumerable<LayoutLockOption> CreateOptions(IEnumerable<GroupDefinition> groups)
    {
        foreach (var group in groups)
        {
            if (group.Tabs.Count == 0)
            {
                if (group.Kind == GroupKind.Empty) yield return new LayoutLockOption(group, null);
                continue;
            }

            foreach (var tab in group.Tabs.Where(tab => tab.Kind == GroupKind.Empty))
                yield return new LayoutLockOption(group, tab);
        }
    }

    private DataTemplate CreateItemTemplate()
    {
        var template = new DataTemplate(typeof(LayoutLockOption));
        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(LayoutLockOption.Title)));
        factory.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(LayoutLockOption.IsLocked)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        factory.SetValue(Control.ForegroundProperty, FindResource("SettingsTextBrush"));
        factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        factory.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OptionChanged));
        factory.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OptionChanged));
        template.VisualTree = factory;
        return template;
    }

    private void OptionChanged(object sender, RoutedEventArgs e) => UpdateSummary();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var option in Options) option.IsLocked = true; LayoutOptionsList.Items.Refresh(); UpdateSummary(); }
    private void ClearAll_Click(object sender, RoutedEventArgs e) { foreach (var option in Options) option.IsLocked = false; LayoutOptionsList.Items.Refresh(); UpdateSummary(); }
    private void UpdateSummary() => SelectionSummaryText.Text = $"已锁定 {Options.Count(option => option.IsLocked)} 个";
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void CancelButton_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in Options) option.Apply();
        DialogResult = true;
    }
}
