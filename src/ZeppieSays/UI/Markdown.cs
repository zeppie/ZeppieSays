using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ZeppieSays.UI;

/// <summary>
/// Shows the Markdown that Notetaker writes (headings, bullets, checkboxes, **bold**, *italic*) in a
/// FlowDocumentScrollViewer: <c>ui:Markdown.Text="{Binding Preview}"</c>. Not a general Markdown renderer.
/// </summary>
public static partial class Markdown
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Markdown), new PropertyMetadata("", (d, e) =>
        {
            if (d is FlowDocumentScrollViewer viewer) viewer.Document = Build((string?)e.NewValue ?? "", viewer);
        }));

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);

    static FlowDocument Build(string markdown, FrameworkElement owner)
    {
        var doc = new FlowDocument
        {
            FontFamily = owner.TryFindResource("ContentControlThemeFontFamily") as FontFamily ?? new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        List? list = null;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("- "))
            {
                list ??= new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(20, 0, 0, 0) };
                var item = line[2..];
                var check = item.StartsWith("[ ] ") || item.StartsWith("[x] ");
                var paragraph = Inline(new Paragraph { Margin = new Thickness(0, 0, 0, 3) }, check ? (item[1] == 'x' ? "☑ " : "☐ ") + item[4..] : item);
                list.ListItems.Add(new ListItem(paragraph));
                continue;
            }
            if (list is not null) { doc.Blocks.Add(list); list = null; }
            if (line.Length == 0) continue;

            if (line.StartsWith("# "))
                doc.Blocks.Add(Heading(line[2..], 24, new Thickness(0, 0, 0, 4)));
            else if (line.StartsWith("## "))
                doc.Blocks.Add(Heading(line[3..], 17, new Thickness(0, 14, 0, 6)));
            else if (line.StartsWith("### "))
                doc.Blocks.Add(Heading(line[4..], 15, new Thickness(0, 10, 0, 4)));
            else
                doc.Blocks.Add(Inline(new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 22 }, line));
        }
        if (list is not null) doc.Blocks.Add(list);
        return doc;
    }

    static Paragraph Heading(string text, double size, Thickness margin) =>
        new(new Run(text)) { FontSize = size, FontWeight = FontWeights.SemiBold, Margin = margin };

    /// <summary>Adds the line's text with **bold** and *italic* spans.</summary>
    static Paragraph Inline(Paragraph paragraph, string text)
    {
        foreach (var inline in Spans(text))
        {
            if (inline is Italic) inline.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            paragraph.Inlines.Add(inline);
        }
        return paragraph;
    }

    /// <summary>A line's text as runs, with **bold** and *italic* spans; also for a TextBlock.</summary>
    internal static IEnumerable<Inline> Spans(string text)
    {
        var position = 0;
        foreach (Match m in Emphasis().Matches(text))
        {
            if (m.Index > position) yield return new Run(text[position..m.Index]);
            yield return m.Groups[1].Success ? new Bold(new Run(m.Groups[1].Value)) : new Italic(new Run(m.Groups[2].Value));
            position = m.Index + m.Length;
        }
        if (position < text.Length) yield return new Run(text[position..]);
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*|\*(.+?)\*")]
    private static partial Regex Emphasis();
}
