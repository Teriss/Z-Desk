using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using ZDesk.Models;
using ZDesk.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;

namespace ZDesk.Windows;

public sealed class MemoNoteCard
{
    public Guid Id { get; }
    public string Title { get; }
    public string PreviewText { get; }
    public string SearchText { get; }
    public string UpdatedText { get; }
    public BitmapSource? PreviewImage { get; }
    public Visibility PreviewVisibility => PreviewImage is null ? Visibility.Collapsed : Visibility.Visible;

    public MemoNoteCard(MemoNoteMetadata metadata, BitmapSource? previewImage)
    {
        Id = metadata.Id;
        Title = string.IsNullOrWhiteSpace(metadata.Title) ? "无标题便笺" : metadata.Title;
        PreviewText = metadata.PreviewText;
        SearchText = metadata.SearchText;
        UpdatedText = FormatUpdatedTime(metadata.UpdatedAtUtc);
        PreviewImage = previewImage;
    }

    private static string FormatUpdatedTime(DateTime utc)
    {
        var local = (utc == default ? DateTime.UtcNow : utc).ToLocalTime();
        return local.Date == DateTime.Now.Date ? $"今天 {local:HH:mm}" : local.ToString("yyyy-MM-dd HH:mm");
    }
}

public partial class MemoWindow : Window
{
    private bool _suppressChanges;
    private bool _suppressBounds;
    private bool _suppressListEvents;
    private bool _linkOpening;
    private bool _codeMode;
    private TextPointer? _pendingLinkStart;
    private TextPointer? _pendingLinkEnd;
    private IReadOnlyList<MemoNoteCard> _cards = [];

    public nint Handle { get; private set; }
    public bool AllowClose { get; set; }
    public Guid? CurrentNoteId { get; private set; }
    public RichTextBox EditorControl => Editor;

    public event Action? DocumentChanged;
    public event Action<MemoWindowBounds>? BoundsChanged;
    public event Action<int>? CaretChanged;
    public event Action? HideRequested;
    public event Action? NewNoteRequested;
    public event Action<Guid>? NoteSelected;
    public event Action<Guid>? NoteDeleteRequested;
    public event Action? BackRequested;

    public MemoWindow()
    {
        (Application.Current as ZDesk.App)?.EnsureBaseResources();
        InitializeComponent();
        Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, Paste_Executed, Paste_CanExecute));
        ResetDocument();
    }

    public void SetNotes(IReadOnlyList<MemoNoteCard> cards)
    {
        _cards = cards;
        ApplyCardFilter();
    }

    public void ShowList(bool clearSearch = true)
    {
        CurrentNoteId = null;
        ListPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailToolbarPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        DeleteCurrentButton.Visibility = Visibility.Collapsed;
        SearchTextBox.Visibility = Visibility.Visible;
        NewNoteButton.Visibility = Visibility.Visible;
        TitleText.Text = "备忘录";
        SubtitleText.Text = "  ·  便笺列表";
        if (clearSearch)
        {
            _suppressListEvents = true;
            SearchTextBox.Text = string.Empty;
            NoteListBox.SelectedItem = null;
            _suppressListEvents = false;
        }
        ApplyCardFilter();
    }

    public void ShowDetail(MemoNoteMetadata note)
    {
        CurrentNoteId = note.Id;
        ListPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailToolbarPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        DeleteCurrentButton.Visibility = Visibility.Visible;
        SearchTextBox.Visibility = Visibility.Collapsed;
        NewNoteButton.Visibility = Visibility.Collapsed;
        TitleText.Text = string.IsNullOrWhiteSpace(note.Title) ? "无标题便笺" : note.Title;
        SubtitleText.Text = "  ·  正在编辑";
    }

    public void UpdateDetailTitle(string title)
    {
        if (CurrentNoteId is not null)
            TitleText.Text = string.IsNullOrWhiteSpace(title) ? "无标题便笺" : title;
    }

    public void ResetDocument()
    {
        _suppressChanges = true;
        try
        {
            Editor.Document = CreateEmptyDocument();
            _codeMode = false;
        }
        finally { _suppressChanges = false; }
    }

    public bool LoadPackage(byte[] package)
    {
        _suppressChanges = true;
        try
        {
            var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
            using var stream = new MemoryStream(package, writable: false);
            range.Load(stream, DataFormats.XamlPackage);
            _codeMode = false;
            StatusText.Text = "内容已载入";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException or System.Xml.XmlException or System.Windows.Markup.XamlParseException)
        {
            StatusText.Text = "备忘录内容无法读取，将尝试备份";
            LogService.Warning("Memo note package load failed", ex);
            return false;
        }
        finally { _suppressChanges = false; }
    }

    public byte[] SavePackage()
    {
        using var stream = new MemoryStream();
        new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Save(stream, DataFormats.XamlPackage);
        return stream.ToArray();
    }

    public MemoContentSnapshot CaptureContentSnapshot()
    {
        var raw = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var titleIndex = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var title = titleIndex >= 0 ? CollapseWhitespace(lines[titleIndex]) : "无标题便笺";
        var summary = titleIndex >= 0 ? CollapseWhitespace(string.Join(' ', lines.Skip(titleIndex + 1))) : string.Empty;
        var search = CollapseWhitespace(normalized);
        var previewImage = CapturePreviewImagePng();
        return new MemoContentSnapshot(
            Truncate(title, 60),
            Truncate(summary, 160),
            search,
            previewImage,
            GetCaretOffset());
    }

    public void FocusEditor(int offset = 0)
    {
        Editor.Focus();
        var length = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.Length;
        var position = Editor.Document.ContentStart.GetPositionAtOffset(Math.Clamp(offset, 0, length), LogicalDirection.Forward);
        Editor.CaretPosition = position ?? Editor.Document.ContentEnd;
    }

    public void FocusList()
    {
        if (SearchTextBox.Visibility == Visibility.Visible)
            SearchTextBox.Focus();
        else
            NoteListBox.Focus();
    }

    public void SetPhysicalBounds(DrawingRectangle bounds)
    {
        if (Handle == nint.Zero) return;
        _suppressBounds = true;
        try
        {
            SetWindowPos(Handle, nint.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        }
        finally { _suppressBounds = false; }
    }

    public MemoWindowBounds GetPhysicalBounds()
    {
        if (Handle != nint.Zero && GetWindowRect(Handle, out var rect))
            return new MemoWindowBounds(rect.Left, rect.Top, Math.Max(480, rect.Right - rect.Left), Math.Max(320, rect.Bottom - rect.Top));
        return new MemoWindowBounds((int)Left, (int)Top, Math.Max(480, (int)Width), Math.Max(320, (int)Height));
    }

    public void SetStatus(string text) => StatusText.Text = text;

    private static FlowDocument CreateEmptyDocument() => new()
    {
        PagePadding = new Thickness(0),
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontSize = 14,
        Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 248)),
        Blocks = { new Paragraph() }
    };

    private static string CollapseWhitespace(string value)
    {
        // TextRange exposes an InlineUIContainer as U+FFFC. It is an editor
        // placeholder, not user text, so keep images out of titles, summaries
        // and the full-text search cache.
        value = value.Replace("\uFFFC", string.Empty, StringComparison.Ordinal);
        return string.Join(' ', value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..Math.Max(0, length - 1)] + "…";

    private byte[]? CapturePreviewImagePng()
    {
        var image = FindFirstImage(Editor.Document.Blocks);
        if (image?.Source is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0) return null;
        try
        {
            var scale = Math.Min(1.0, Math.Min(336.0 / source.PixelWidth, 216.0 / source.PixelHeight));
            BitmapSource output = source;
            if (scale < 0.999)
            {
                output = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                output.Freeze();
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            LogService.Warning("Memo preview thumbnail generation failed", ex);
            return null;
        }
    }

    private static Image? FindFirstImage(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            var result = block switch
            {
                Paragraph paragraph => FindFirstImage(paragraph.Inlines),
                List list => FindFirstImage(list.ListItems.SelectMany(item => item.Blocks)),
                Section section => FindFirstImage(section.Blocks),
                _ => null
            };
            if (result is not null) return result;
        }
        return null;
    }

    private static Image? FindFirstImage(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is InlineUIContainer { Child: Image image }) return image;
            if (inline is Span span)
            {
                var result = FindFirstImage(span.Inlines);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private void ApplyCardFilter()
    {
        var query = SearchTextBox?.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(query)
            ? _cards
            : _cards.Where(card => card.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || card.PreviewText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || card.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (NoteListBox is not null)
        {
            _suppressListEvents = true;
            NoteListBox.ItemsSource = filtered;
            NoteListBox.SelectedItem = null;
            _suppressListEvents = false;
        }
        if (EmptyStatePanel is not null)
        {
            EmptyStatePanel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (filtered.Count == 0 && !string.IsNullOrEmpty(query))
                ((TextBlock)((StackPanel)EmptyStatePanel).Children[0]).Text = "没有匹配的便笺";
            else if (filtered.Count == 0)
                ((TextBlock)((StackPanel)EmptyStatePanel).Children[0]).Text = "还没有便笺";
        }
    }

    private void Paste_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsData(DataFormats.Rtf) || Clipboard.ContainsData(DataFormats.Html);
        e.Handled = true;
    }

    private void Paste_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var data = Clipboard.GetDataObject();
        if (data is null) return;
        if (MemoPasteService.TryPaste(Editor, data, out var error))
        {
            SetStatus("已粘贴并自动识别格式");
            e.Handled = true;
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            SetStatus($"粘贴失败：{error}");
            e.Handled = true;
        }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressChanges) DocumentChanged?.Invoke();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (CurrentNoteId is not null) CaretChanged?.Invoke(GetCaretOffset());
        if (Editor.Selection.Text.Length == 0) return;
        var family = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty);
        if (family is FontFamily font && font.Source.Contains("Mono", StringComparison.OrdinalIgnoreCase))
            SetStatus("已选中代码样式文本");
    }

    public int GetCaretOffset()
    {
        var caret = Editor.CaretPosition ?? Editor.Document.ContentEnd;
        return new TextRange(Editor.Document.ContentStart, caret).Text.Length;
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.B: Bold_Click(sender, new RoutedEventArgs()); e.Handled = true; return;
                case Key.I: Italic_Click(sender, new RoutedEventArgs()); e.Handled = true; return;
                case Key.U: Underline_Click(sender, new RoutedEventArgs()); e.Handled = true; return;
                case Key.K: Link_Click(sender, new RoutedEventArgs()); e.Handled = true; return;
            }
        }
        if (e.Key is Key.Space or Key.Enter)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Key == Key.Enter) TryAutoFormatCodeFence();
                MemoPasteService.TryAutoFormatParagraph(Editor);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void TryAutoFormatCodeFence()
    {
        var current = Editor.CaretPosition?.Paragraph;
        var previous = current?.PreviousBlock as Paragraph;
        if (current is null || previous is null) return;
        var previousText = new TextRange(previous.ContentStart, previous.ContentEnd).Text.TrimEnd('\r', '\n').Trim();
        if (previousText.StartsWith("```", StringComparison.Ordinal))
        {
            new TextRange(previous.ContentStart, previous.ContentEnd).Text = string.Empty;
            _codeMode = !_codeMode;
            if (_codeMode) ApplyCodeParagraphStyle(current);
            else ClearCodeParagraphStyle(current);
            DocumentChanged?.Invoke();
        }
        else if (_codeMode)
        {
            ApplyCodeParagraphStyle(previous);
            DocumentChanged?.Invoke();
        }
    }

    private static void ApplyCodeParagraphStyle(Paragraph paragraph)
    {
        paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        paragraph.Background = new SolidColorBrush(Color.FromRgb(39, 42, 50));
        paragraph.Foreground = new SolidColorBrush(Color.FromRgb(230, 232, 239));
        paragraph.Padding = new Thickness(10, 7, 10, 7);
    }

    private static void ClearCodeParagraphStyle(Paragraph paragraph)
    {
        paragraph.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        paragraph.Background = Brushes.Transparent;
        paragraph.Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 248));
        paragraph.Padding = new Thickness(0);
    }

    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _linkOpening) return;
        var point = e.GetPosition(Editor);
        var pointer = Editor.GetPositionFromPoint(point, snapToText: true);
        DependencyObject? current = pointer?.Parent;
        while (current is not null)
        {
            if (current is Hyperlink link && link.NavigateUri is { } uri && uri.Scheme is "http" or "https")
            {
                _linkOpening = true;
                try
                {
                    Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                    e.Handled = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    SetStatus($"无法打开链接：{ex.Message}");
                }
                finally { _linkOpening = false; }
                return;
            }
            current = current switch
            {
                FrameworkContentElement content => content.Parent,
                FrameworkElement element => element.Parent,
                _ => null
            };
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => ExecuteCommand(ApplicationCommands.Undo);
    private void Redo_Click(object sender, RoutedEventArgs e) => ExecuteCommand(ApplicationCommands.Redo);
    private void Bold_Click(object sender, RoutedEventArgs e) => ToggleProperty(TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal);
    private void Italic_Click(object sender, RoutedEventArgs e) => ToggleProperty(TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal);
    private void Underline_Click(object sender, RoutedEventArgs e) => ToggleProperty(Inline.TextDecorationsProperty, TextDecorations.Underline, null);
    private void Bullets_Click(object sender, RoutedEventArgs e) => ExecuteCommand(EditingCommands.ToggleBullets);
    private void Numbering_Click(object sender, RoutedEventArgs e) => ExecuteCommand(EditingCommands.ToggleNumbering);

    private void BlockStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Editor is null || BlockStyleCombo.SelectedItem is not ComboBoxItem item) return;
        switch (item.Tag as string)
        {
            case "h1": ApplyProperty(TextElement.FontSizeProperty, 21d); ApplyProperty(TextElement.FontWeightProperty, FontWeights.SemiBold); break;
            case "h2": ApplyProperty(TextElement.FontSizeProperty, 17d); ApplyProperty(TextElement.FontWeightProperty, FontWeights.SemiBold); break;
            default: ApplyProperty(TextElement.FontSizeProperty, 14d); ApplyProperty(TextElement.FontWeightProperty, FontWeights.Normal); break;
        }
    }

    private void InlineCode_Click(object sender, RoutedEventArgs e)
    {
        ApplyProperty(TextElement.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas"));
        ApplyProperty(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromRgb(46, 49, 58)));
    }

    private void CodeBlock_Click(object sender, RoutedEventArgs e)
    {
        ApplyProperty(TextElement.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas"));
        ApplyProperty(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromRgb(39, 42, 50)));
        ApplyProperty(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(230, 232, 239)));
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        _pendingLinkStart = Editor.Selection.Start;
        _pendingLinkEnd = Editor.Selection.End;
        LinkLabelTextBox.Text = Editor.Selection.Text.Trim();
        LinkUrlTextBox.Text = Editor.Selection.Text.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase) ? Editor.Selection.Text.Trim() : "https://";
        LinkPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => LinkUrlTextBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ApplyLink_Click(object sender, RoutedEventArgs e)
    {
        var label = LinkLabelTextBox.Text.Trim();
        var url = LinkUrlTextBox.Text.Trim();
        if (string.IsNullOrEmpty(label)) label = url;
        if (_pendingLinkStart is null || _pendingLinkEnd is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            SetStatus("链接必须使用 http 或 https 地址");
            return;
        }
        MemoPasteService.InsertHyperlink(Editor, _pendingLinkStart, _pendingLinkEnd, label, uri.ToString());
        LinkPopup.IsOpen = false;
        _pendingLinkStart = _pendingLinkEnd = null;
    }

    private void CancelLink_Click(object sender, RoutedEventArgs e)
    {
        LinkPopup.IsOpen = false;
        _pendingLinkStart = _pendingLinkEnd = null;
    }

    private void ToggleProperty(DependencyProperty property, object onValue, object? offValue)
    {
        var range = new TextRange(Editor.Selection.Start, Editor.Selection.End);
        if (range.IsEmpty) return;
        var current = range.GetPropertyValue(property);
        if (offValue is not null && Equals(current, onValue)) range.ApplyPropertyValue(property, offValue);
        else range.ApplyPropertyValue(property, onValue);
        DocumentChanged?.Invoke();
    }

    private void ApplyProperty(DependencyProperty property, object value)
    {
        var range = new TextRange(Editor.Selection.Start, Editor.Selection.End);
        if (range.IsEmpty)
        {
            if (Editor.CaretPosition?.Paragraph is Paragraph paragraph)
            {
                paragraph.SetValue(property, value);
                DocumentChanged?.Invoke();
            }
            return;
        }
        range.ApplyPropertyValue(property, value);
        DocumentChanged?.Invoke();
    }

    private void ExecuteCommand(RoutedCommand command)
    {
        if (command.CanExecute(null, Editor)) command.Execute(null, Editor);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractiveHeaderSource(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private static bool IsInteractiveHeaderSource(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Control) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    private void NewNoteButton_Click(object sender, RoutedEventArgs e) => NewNoteRequested?.Invoke();
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void DeleteCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentNoteId is Guid id) NoteDeleteRequested?.Invoke(id);
    }

    private void DeleteNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is MemoNoteCard card)
        {
            NoteDeleteRequested?.Invoke(card.Id);
            e.Handled = true;
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyCardFilter();

    private void NoteListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvents || NoteListBox.SelectedItem is not MemoNoteCard card) return;
        NoteSelected?.Invoke(card.Id);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (LinkPopup.IsOpen)
        {
            LinkPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        HideRequested?.Invoke();
        e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        HideRequested?.Invoke();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        Handle = new WindowInteropHelper(this).Handle;
        SourceInitialized -= Window_SourceInitialized;
    }

    private void Window_BoundsChanged(object? sender, EventArgs e)
    {
        if (!_suppressBounds && Handle != nint.Zero) BoundsChanged?.Invoke(GetPhysicalBounds());
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        Window_BoundsChanged(this, e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Window_BoundsChanged(this, EventArgs.Empty);
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
