using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HtmlAgilityPack;

namespace ZDesk.Services;

/// <summary>Converts clipboard data into safe FlowDocument fragments.</summary>
public static class MemoPasteService
{
    private static readonly Regex InlineToken = new(
        @"(?<code>`[^`\r\n]+`)|(?<markdown>\[[^\]]+\]\(https?://[^)\s]+\))|(?<url>https?://[^\s<>()[\]]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CssDeclaration = new(
        @"(?<key>[\w-]+)\s*:\s*(?<value>[^;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryPaste(RichTextBox editor, IDataObject data, out string? error)
    {
        error = null;
        try
        {
            if (data.GetDataPresent(DataFormats.Rtf))
            {
                PasteDataObject(editor, data);
                return true;
            }

            if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is { } html)
            {
                var text = GetClipboardText(html);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    PasteFragment(editor, CreateHtmlFragment(text));
                    return true;
                }
            }

            var plainText = data.GetDataPresent(DataFormats.UnicodeText)
                ? GetClipboardText(data.GetData(DataFormats.UnicodeText))
                : data.GetDataPresent(DataFormats.Text) ? GetClipboardText(data.GetData(DataFormats.Text)) : null;
            if (!string.IsNullOrEmpty(plainText))
            {
                PasteFragment(editor, CreateTextFragment(plainText));
                return true;
            }

            if (data.GetDataPresent(DataFormats.Bitmap) && TryGetBitmap(data.GetData(DataFormats.Bitmap), out var bitmap))
            {
                PasteBitmap(editor, bitmap);
                return true;
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or IOException or FormatException or InvalidDataException or NotSupportedException or System.Windows.Markup.XamlParseException)
        {
            error = ex.Message;
            return false;
        }

        return false;
    }

    public static FlowDocument CreateTextFragment(string text)
    {
        var document = CreateFragmentDocument();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var inCode = false;
        var openingFence = string.Empty;
        var codeLines = new List<string>();
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    AddCodeParagraph(document, string.Join("\n", codeLines));
                    codeLines.Clear();
                    openingFence = string.Empty;
                }
                else
                {
                    openingFence = line;
                }
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                codeLines.Add(line);
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                paragraph.FontSize = 17;
                paragraph.FontWeight = FontWeights.SemiBold;
                AppendInlineMarkdown(paragraph.Inlines, trimmed[3..]);
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                paragraph.FontSize = 21;
                paragraph.FontWeight = FontWeights.SemiBold;
                AppendInlineMarkdown(paragraph.Inlines, trimmed[2..]);
            }
            else
            {
                AppendInlineMarkdown(paragraph.Inlines, line);
            }
            document.Blocks.Add(paragraph);
        }

        if (inCode)
        {
            // An unmatched fence is ordinary text. Keep it verbatim and avoid
            // recognizing links or inline code inside the unfinished block.
            AddPlainParagraph(document, openingFence);
            foreach (var codeLine in codeLines) AddPlainParagraph(document, codeLine);
        }
        return document;
    }

    public static FlowDocument CreateHtmlFragment(string html)
    {
        var document = CreateFragmentDocument();
        var parsed = new HtmlDocument();
        parsed.LoadHtml(html);
        var root = parsed.DocumentNode.SelectSingleNode("//body") ?? parsed.DocumentNode;
        AppendHtmlBlocks(document, root);
        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph());
        return document;
    }

    public static void InsertHyperlink(RichTextBox editor, TextPointer start, TextPointer end, string label, string url)
    {
        var fragment = CreateFragmentDocument();
        var paragraph = new Paragraph();
        AddHyperlink(paragraph.Inlines, label, url);
        fragment.Blocks.Add(paragraph);
        PasteFragmentAt(editor, fragment, start, end);
    }

    public static bool TryAutoFormatParagraph(RichTextBox editor)
    {
        var paragraph = editor.CaretPosition?.Paragraph;
        if (paragraph is null) return false;
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        var text = range.Text.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text) || (!text.Contains("http", StringComparison.OrdinalIgnoreCase) && !text.Contains('`')))
            return false;
        var fragment = CreateTextFragment(text);
        var start = range.Start;
        range.Text = string.Empty;
        PasteFragmentAt(editor, fragment, start, start);
        return true;
    }

    private static FlowDocument CreateFragmentDocument() => new()
    {
        PagePadding = new Thickness(0),
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontSize = 14,
        Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 248))
    };

    private static string GetClipboardText(object? value) => value switch
    {
        string text => text,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        MemoryStream stream => System.Text.Encoding.UTF8.GetString(stream.ToArray()),
        _ => value?.ToString() ?? string.Empty
    };

    private static void PasteDataObject(RichTextBox editor, IDataObject data)
    {
        var range = new TextRange(editor.Selection.Start, editor.Selection.End);
        var raw = data.GetData(DataFormats.Rtf);
        if (raw is null) return;
        using var stream = raw switch
        {
            MemoryStream memory => new MemoryStream(memory.ToArray(), writable: false),
            byte[] bytes => new MemoryStream(bytes, writable: false),
            _ => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw.ToString() ?? string.Empty), writable: false)
        };
        range.Load(stream, DataFormats.Rtf);
    }

    private static void PasteFragment(RichTextBox editor, FlowDocument fragment)
    {
        using var package = new MemoryStream();
        new TextRange(fragment.ContentStart, fragment.ContentEnd).Save(package, DataFormats.XamlPackage);
        package.Position = 0;
        new TextRange(editor.Selection.Start, editor.Selection.End).Load(package, DataFormats.XamlPackage);
    }

    private static void PasteFragmentAt(RichTextBox editor, FlowDocument fragment, TextPointer start, TextPointer end)
    {
        using var package = new MemoryStream();
        new TextRange(fragment.ContentStart, fragment.ContentEnd).Save(package, DataFormats.XamlPackage);
        package.Position = 0;
        new TextRange(start, end).Load(package, DataFormats.XamlPackage);
    }

    private static void PasteBitmap(RichTextBox editor, BitmapSource bitmap)
    {
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = Math.Max(200, editor.ActualWidth - 48),
            HorizontalAlignment = HorizontalAlignment.Left,
            SnapsToDevicePixels = true
        };
        image.Width = Math.Min(bitmap.Width, image.MaxWidth);
        image.Height = image.Width * bitmap.Height / Math.Max(1, bitmap.Width);
        var range = new TextRange(editor.Selection.Start, editor.Selection.End);
        var insertion = range.Start;
        range.Text = string.Empty;
        new InlineUIContainer(image, insertion);
    }

    private static void AppendInlineMarkdown(InlineCollection target, string text)
    {
        var position = 0;
        foreach (Match match in InlineToken.Matches(text))
        {
            AppendPlain(target, text[position..match.Index]);
            if (match.Groups["code"].Success)
            {
                var run = new Run(match.Value[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Background = new SolidColorBrush(Color.FromRgb(46, 49, 58))
                };
                target.Add(run);
            }
            else if (match.Groups["markdown"].Success)
            {
                var value = match.Value;
                var close = value.LastIndexOf("](", StringComparison.Ordinal);
                var label = value[1..close];
                var url = value[(close + 2)..^1];
                AddHyperlink(target, label, url);
            }
            else
            {
                var url = TrimUrlPunctuation(match.Value);
                AddHyperlink(target, url, url);
                AppendPlain(target, match.Value[url.Length..]);
            }
            position = match.Index + match.Length;
        }
        AppendPlain(target, text[position..]);
    }

    private static string TrimUrlPunctuation(string value)
    {
        var length = value.Length;
        while (length > 0 && ".,;!?".Contains(value[length - 1], StringComparison.Ordinal)) length--;
        return value[..length];
    }

    private static void AddHyperlink(InlineCollection target, string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            AppendPlain(target, label);
            return;
        }
        var link = new Hyperlink { NavigateUri = uri, Foreground = new SolidColorBrush(Color.FromRgb(115, 174, 245)) };
        link.Inlines.Add(new Run(label));
        target.Add(link);
    }

    private static void AppendPlain(InlineCollection target, string text)
    {
        if (!string.IsNullOrEmpty(text)) target.Add(new Run(text));
    }

    private static void AddCodeParagraph(FlowDocument document, string code)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(10, 7, 10, 7),
            Background = new SolidColorBrush(Color.FromRgb(39, 42, 50)),
            Foreground = new SolidColorBrush(Color.FromRgb(230, 232, 239)),
            FontFamily = new FontFamily("Cascadia Mono, Consolas")
        };
        paragraph.Inlines.Add(new Run(code));
        document.Blocks.Add(paragraph);
    }

    private static void AddPlainParagraph(FlowDocument document, string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
        paragraph.Inlines.Add(new Run(text));
        document.Blocks.Add(paragraph);
    }

    private static void AppendHtmlBlocks(FlowDocument document, HtmlNode root)
    {
        foreach (var node in root.ChildNodes)
        {
            if (node.NodeType == HtmlNodeType.Comment) continue;
            if (node.NodeType == HtmlNodeType.Text)
            {
                if (!string.IsNullOrWhiteSpace(node.InnerText))
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
                    AppendPlain(paragraph.Inlines, HtmlEntity.DeEntitize(node.InnerText));
                    document.Blocks.Add(paragraph);
                }
                continue;
            }

            var name = node.Name.ToLowerInvariant();
            if (name is "script" or "style" or "noscript") continue;
            if (name is "ul" or "ol")
            {
                var list = new List { MarkerStyle = name == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(18, 0, 0, 8) };
                foreach (var item in node.ChildNodes.Where(child => child.Name.Equals("li", StringComparison.OrdinalIgnoreCase)))
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                    AppendHtmlInlines(paragraph.Inlines, item);
                    list.ListItems.Add(new ListItem(paragraph));
                }
                if (list.ListItems.Count > 0) document.Blocks.Add(list);
                continue;
            }
            if (name == "pre")
            {
                AddCodeParagraph(document, HtmlEntity.DeEntitize(node.InnerText));
                continue;
            }
            if (name is "p" or "div" or "blockquote" or "h1" or "h2" or "h3")
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
                if (name is "h1" or "h2" or "h3")
                {
                    paragraph.FontSize = name == "h1" ? 21 : name == "h2" ? 17 : 15;
                    paragraph.FontWeight = FontWeights.SemiBold;
                }
                if (name == "blockquote")
                {
                    paragraph.Margin = new Thickness(16, 0, 0, 8);
                    paragraph.BorderBrush = new SolidColorBrush(Color.FromRgb(74, 130, 216));
                    paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
                    paragraph.Padding = new Thickness(10, 0, 0, 0);
                }
                AppendHtmlInlines(paragraph.Inlines, node);
                document.Blocks.Add(paragraph);
                continue;
            }

            if (node.ChildNodes.Count > 0) AppendHtmlBlocks(document, node);
        }
    }

    private static void AppendHtmlInlines(InlineCollection target, HtmlNode node)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                AppendPlain(target, HtmlEntity.DeEntitize(child.InnerText));
                continue;
            }
            var name = child.Name.ToLowerInvariant();
            if (name is "script" or "style" or "noscript") continue;
            if (name == "br") { target.Add(new LineBreak()); continue; }
            if (name == "img")
            {
                if (TryDecodeDataUri(child.GetAttributeValue("src", string.Empty), out var image))
                    target.Add(new InlineUIContainer(new Image { Source = image, Stretch = Stretch.Uniform, MaxWidth = 640 }));
                continue;
            }
            if (name == "a")
            {
                var label = HtmlEntity.DeEntitize(child.InnerText);
                var href = child.GetAttributeValue("href", string.Empty);
                AddHyperlink(target, label, href);
                continue;
            }
            if (name == "code")
            {
                var span = new Span { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = new SolidColorBrush(Color.FromRgb(46, 49, 58)) };
                AppendHtmlInlines(span.Inlines, child);
                target.Add(span);
                continue;
            }
            if (name is "strong" or "b" or "em" or "i" or "u" or "s" or "del" or "span")
            {
                var span = new Span();
                if (name is "strong" or "b") span.FontWeight = FontWeights.Bold;
                if (name is "em" or "i") span.FontStyle = FontStyles.Italic;
                if (name is "u") span.TextDecorations = TextDecorations.Underline;
                if (name is "s" or "del") span.TextDecorations = TextDecorations.Strikethrough;
                ApplyCss(span, child.GetAttributeValue("style", string.Empty));
                AppendHtmlInlines(span.Inlines, child);
                target.Add(span);
                continue;
            }
            AppendHtmlInlines(target, child);
        }
    }

    private static void ApplyCss(Span span, string style)
    {
        foreach (Match declaration in CssDeclaration.Matches(style))
        {
            var value = declaration.Groups["value"].Value.Trim();
            switch (declaration.Groups["key"].Value.ToLowerInvariant())
            {
                case "font-weight" when value.Contains("bold", StringComparison.OrdinalIgnoreCase): span.FontWeight = FontWeights.Bold; break;
                case "font-style" when value.Contains("italic", StringComparison.OrdinalIgnoreCase): span.FontStyle = FontStyles.Italic; break;
                case "text-decoration" when value.Contains("underline", StringComparison.OrdinalIgnoreCase): span.TextDecorations = TextDecorations.Underline; break;
                case "font-family" when value.Contains("mono", StringComparison.OrdinalIgnoreCase): span.FontFamily = new FontFamily("Cascadia Mono, Consolas"); break;
            }
        }
    }

    private static bool TryDecodeDataUri(string uri, out BitmapImage image)
    {
        image = new BitmapImage();
        if (!uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return false;
        var comma = uri.IndexOf(',');
        if (comma < 0) return false;
        try
        {
            var bytes = uri[comma..].TrimStart(',');
            var data = uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(bytes)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(bytes));
            using var stream = new MemoryStream(data);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image.PixelWidth > 0 && image.PixelHeight > 0;
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetBitmap(object? value, out BitmapSource bitmap)
    {
        if (value is BitmapSource source)
        {
            bitmap = source;
            if (bitmap.CanFreeze) bitmap.Freeze();
            return true;
        }
        if (value is System.Drawing.Bitmap drawingBitmap)
        {
            var handle = drawingBitmap.GetHbitmap();
            try
            {
                bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    handle,
                    nint.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return true;
            }
            finally { DeleteObject(handle); }
        }
        bitmap = null!;
        return false;
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
