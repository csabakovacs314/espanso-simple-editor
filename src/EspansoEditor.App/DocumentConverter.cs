using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Text;
using CoreRun = EspansoEditor.Core.Model.Run;
using WpfRun = System.Windows.Documents.Run;

namespace EspansoEditor.App;

public sealed record ChipContext(IReadOnlyDictionary<string, string> GlobalEcho, DateTime Now, Action<FrameworkElement>? OnEdit);

/// <summary>Turns a snippet body into a RichTextBox document (text plus chip elements) and back.</summary>
public static class DocumentConverter
{
    public static FlowDocument ToDocument(IReadOnlyList<CoreRun> body, bool dropFinalNewline, ChipContext ctx)
    {
        var doc = NewDocument();
        var para = NewParagraph();
        doc.Blocks.Add(para);

        var runs = BodyText.Normalize(body);
        if (dropFinalNewline && runs.Count > 0 && runs[^1].Kind == RunKind.Text && runs[^1].Text.EndsWith('\n'))
            runs[^1] = CoreRun.Literal(runs[^1].Text[..^1]);

        foreach (var r in runs)
        {
            if (r.Kind == RunKind.Text)
            {
                var parts = r.Text.Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        para = NewParagraph();
                        doc.Blocks.Add(para);
                    }
                    if (parts[i].Length > 0) para.Inlines.Add(new WpfRun(parts[i]));
                }
            }
            else
            {
                para.Inlines.Add(new InlineUIContainer(MakeChip(r, ctx)) { BaselineAlignment = BaselineAlignment.Center });
            }
        }
        return doc;
    }

    public static FlowDocument NewDocument() => new()
    {
        PagePadding = new Thickness(2),
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        FontSize = 14,
        LineHeight = 23,
    };

    public static Paragraph NewParagraph() => new() { Margin = new Thickness(0) };

    public static List<CoreRun> FromDocument(FlowDocument doc)
    {
        var result = new List<CoreRun>();
        var first = true;

        void AddBlock(Block b)
        {
            switch (b)
            {
                case Paragraph p:
                    if (!first) result.Add(CoreRun.Literal("\n"));
                    first = false;
                    AddInlines(p.Inlines);
                    break;
                case List l:
                    foreach (var item in l.ListItems)
                        foreach (var inner in item.Blocks)
                            AddBlock(inner);
                    break;
                case Section s:
                    foreach (var inner in s.Blocks) AddBlock(inner);
                    break;
                case BlockUIContainer:
                    break;
                default:
                    if (!first) result.Add(CoreRun.Literal("\n"));
                    first = false;
                    result.Add(CoreRun.Literal(new TextRange(b.ContentStart, b.ContentEnd).Text.TrimEnd('\r', '\n')));
                    break;
            }
        }

        void AddInlines(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case WpfRun r:
                        result.Add(CoreRun.Literal(r.Text));
                        break;
                    case LineBreak:
                        result.Add(CoreRun.Literal("\n"));
                        break;
                    case InlineUIContainer { Child: FrameworkElement { Tag: CoreRun chip } }:
                        result.Add(chip);
                        break;
                    case Span span:
                        AddInlines(span.Inlines);
                        break;
                }
            }
        }

        foreach (var block in doc.Blocks) AddBlock(block);
        return BodyText.Normalize(result);
    }

    // ---------- formatted text (Jira Cloud and HTML snippets) ----------

    public const double Heading1Size = 22, Heading2Size = 19, Heading3Size = 16.5;

    /// <summary>How a list item was written, so an unchanged item is written back the same way.</summary>
    private sealed record ItemTag(BlockKind Kind, string Marker, string Indent, int Depth);

    /// <summary>A formatted document: bold, italic, code, headings and lists from the snippet's Markdown.</summary>
    /// <param name="escaped">The body uses the backslash escapes of HTML snippets.</param>
    public static FlowDocument ToRichDocument(IReadOnlyList<CoreRun> body, bool dropFinalNewline, ChipContext ctx, bool escaped)
    {
        var doc = NewDocument();
        var blocks = RichText.ParseMarkdown(body, escaped);
        if (dropFinalNewline && blocks.Count > 1 && blocks[^1] is { Kind: BlockKind.Line, Indent.Length: 0, Inlines.Count: 0 })
            blocks.RemoveAt(blocks.Count - 1);

        // Open lists, outermost first, with the indent their items use. A deeper indent nests, an equal one
        // continues the list, a smaller one goes back out (like Markdown).
        var open = new List<(int Indent, List List)>();
        foreach (var b in blocks)
        {
            var para = NewParagraph();
            if (!b.IsList && b.Indent.Length > 0) para.Inlines.Add(new WpfRun(b.Indent));
            foreach (var piece in b.Inlines) para.Inlines.Add(MakeInline(piece, ctx));

            if (!b.IsList)
            {
                open.Clear();
                if (b.HeadingLevel > 0) StyleHeading(para, b.HeadingLevel);
                doc.Blocks.Add(para);
                continue;
            }

            var numbered = b.Kind == BlockKind.Numbered;
            var indent = b.Indent.Length;
            while (open.Count > 0 && open[^1].Indent > indent) open.RemoveAt(open.Count - 1);
            if (open.Count > 0 && open[^1].Indent == indent && IsNumbered(open[^1].List) != numbered) open.RemoveAt(open.Count - 1);
            if (open.Count == 0 || open[^1].Indent < indent || IsNumbered(open[^1].List) != numbered)
            {
                var list = NewList(numbered, b.Marker);
                if (open.Count == 0 || open[^1].List.ListItems.Count == 0) doc.Blocks.Add(list);
                else open[^1].List.ListItems.LastListItem.Blocks.Add(list);
                if (open.Count == 0 || open[^1].Indent < indent) open.Add((indent, list));
                else open[^1] = (indent, list);
            }
            open[^1].List.ListItems.Add(new ListItem(para) { Tag = new ItemTag(b.Kind, b.Marker, b.Indent, open.Count - 1) });
        }
        if (doc.Blocks.Count == 0) doc.Blocks.Add(NewParagraph());
        return doc;
    }

    /// <summary>Reads a formatted document back into lines with styles.</summary>
    public static List<RichBlock> FromRichDocument(FlowDocument doc)
    {
        var blocks = new List<RichBlock>();

        void AddParagraph(Paragraph p, BlockKind kind, string marker, string indent)
        {
            if (kind == BlockKind.Line) kind = HeadingKind(p.FontSize);
            var heading = kind is BlockKind.Heading1 or BlockKind.Heading2 or BlockKind.Heading3;
            var current = new RichBlock { Kind = kind, Marker = marker, Indent = indent };
            blocks.Add(current);

            // A heading is bold by itself; inside it only bold set on the text (Ctrl+B) counts.
            bool Bold(TextElement e) => heading ? HasOwnBold(e, p) : IsBold(e);

            void Inlines(InlineCollection inlines)
            {
                foreach (var inline in inlines)
                {
                    switch (inline)
                    {
                        case WpfRun r when r.Text.Length > 0:
                            current.Inlines.Add(new RichInline(r.Text, Bold(r), r.FontStyle == FontStyles.Italic, IsMono(r.FontFamily)));
                            break;
                        case LineBreak:
                            // Shift+Enter is turned into a new line by the editor; one from elsewhere continues the same kind of line.
                            current = new RichBlock { Kind = current.IsList ? BlockKind.Line : current.Kind, Indent = current.IsList ? current.Indent + "  " : current.Indent };
                            blocks.Add(current);
                            break;
                        case InlineUIContainer { Child: FrameworkElement { Tag: CoreRun chip } } c:
                            current.Inlines.Add(new RichInline("", Bold(c), c.FontStyle == FontStyles.Italic, IsMono(c.FontFamily), chip));
                            break;
                        case Span span:
                            Inlines(span.Inlines);
                            break;
                    }
                }
            }
            Inlines(p.Inlines);
        }

        void AddBlock(Block b, int depth)
        {
            switch (b)
            {
                case Paragraph p:
                    AddParagraph(p, BlockKind.Line, "", "");
                    break;
                case List l:
                    var numbered = IsNumbered(l);
                    var kind = numbered ? BlockKind.Numbered : BlockKind.Bullet;
                    var n = Math.Max(1, l.StartIndex) - 1;
                    string? lastIndent = null, lastMarker = null;
                    foreach (var item in l.ListItems)
                    {
                        // An item loaded from the file keeps its own marker and indent ("1." twice, "-" or "*",
                        // 4-space nesting) unless it was moved to another list; a new item follows the one before it.
                        string marker, indent;
                        if (item.Tag is ItemTag t && t.Kind == kind && t.Depth == depth)
                        {
                            marker = t.Marker;
                            indent = t.Indent;
                            if (numbered && int.TryParse(t.Marker.TrimEnd('.'), out var num)) n = num;
                        }
                        else
                        {
                            n++;
                            marker = numbered ? n + "." : lastMarker ?? l.Tag as string ?? "*";
                            indent = lastIndent ?? new string(' ', depth * 2);
                        }
                        lastIndent = indent;
                        lastMarker = numbered ? null : marker;

                        var first = true;
                        foreach (var inner in item.Blocks)
                        {
                            if (inner is Paragraph ip && first)
                                AddParagraph(ip, kind, marker, indent);
                            else if (inner is Paragraph more)
                                AddParagraph(more, BlockKind.Line, "", indent + "  ");
                            else
                                AddBlock(inner, depth + 1);
                            first = false;
                        }
                    }
                    break;
                case Section s:
                    foreach (var inner in s.Blocks) AddBlock(inner, depth);
                    break;
            }
        }

        foreach (var block in doc.Blocks) AddBlock(block, 0);
        if (blocks.Count == 0) blocks.Add(new RichBlock());
        return blocks;
    }

    public static void StyleHeading(Paragraph p, int level)
    {
        p.FontSize = level switch { 1 => Heading1Size, 2 => Heading2Size, _ => Heading3Size };
        p.FontWeight = FontWeights.Bold;
    }

    public static void ClearHeading(Paragraph p)
    {
        p.ClearValue(TextElement.FontSizeProperty);
        p.ClearValue(TextElement.FontWeightProperty);
    }

    private static BlockKind HeadingKind(double size) =>
        size >= 21 ? BlockKind.Heading1 : size >= 18 ? BlockKind.Heading2 : size >= 16 ? BlockKind.Heading3 : BlockKind.Line;

    public static List NewList(bool numbered, string marker)
    {
        var list = new List
        {
            MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0),
            Padding = new Thickness(24, 0, 0, 0),
        };
        if (numbered && int.TryParse(marker.TrimEnd('.'), out var start) && start > 0) list.StartIndex = start;
        else if (!numbered) list.Tag = marker is "*" or "-" or "+" ? marker : "*";
        return list;
    }

    private static bool IsNumbered(List l) => l.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin
        or TextMarkerStyle.UpperLatin or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;

    private static bool IsBold(TextElement e) => e.FontWeight.ToOpenTypeWeight() >= 600;

    /// <summary>Bold set on the text itself or a span around it, not inherited from the paragraph.</summary>
    private static bool HasOwnBold(TextElement e, Paragraph stop)
    {
        for (DependencyObject? x = e; x != null && !ReferenceEquals(x, stop); x = (x as TextElement)?.Parent)
        {
            if (x.ReadLocalValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600) return true;
        }
        return false;
    }

    public static bool IsMono(FontFamily? f) => f?.Source is { } s && (s.Contains("Cascadia") || s.Contains("Consolas"));

    private static Inline MakeInline(RichInline piece, ChipContext ctx)
    {
        Inline inline = piece.Chip != null
            ? new InlineUIContainer(MakeChip(piece.Chip, ctx)) { BaselineAlignment = BaselineAlignment.Center }
            : new WpfRun(piece.Text);
        if (piece.Bold) inline.FontWeight = FontWeights.Bold;
        if (piece.Italic) inline.FontStyle = FontStyles.Italic;
        if (piece.Code) SetCode(inline, true);
        return inline;
    }

    /// <summary>Inline code look: monospace on a slightly lighter background.</summary>
    public static void SetCode(TextElement e, bool on)
    {
        if (on)
        {
            e.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
            e.Background = (Brush)Application.Current.FindResource("CodeBrush");
        }
        else
        {
            e.ClearValue(TextElement.FontFamilyProperty);
            e.ClearValue(TextElement.BackgroundProperty);
        }
    }

    /// <summary>All chip elements in document order.</summary>
    public static IEnumerable<FrameworkElement> Chips(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
            foreach (var chip in ChipsIn(block))
                yield return chip;
    }

    private static IEnumerable<FrameworkElement> ChipsIn(Block b)
    {
        switch (b)
        {
            case Paragraph p:
                foreach (var c in ChipsIn(p.Inlines)) yield return c;
                break;
            case List l:
                foreach (var item in l.ListItems)
                    foreach (var inner in item.Blocks)
                        foreach (var c in ChipsIn(inner)) yield return c;
                break;
            case Section s:
                foreach (var inner in s.Blocks)
                    foreach (var c in ChipsIn(inner)) yield return c;
                break;
        }
    }

    private static IEnumerable<FrameworkElement> ChipsIn(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is InlineUIContainer { Child: FrameworkElement { Tag: CoreRun } fe }) yield return fe;
            else if (inline is Span span)
                foreach (var c in ChipsIn(span.Inlines)) yield return c;
        }
    }

    /// <summary>The coloured pill shown in the text for a variable, field or cursor.</summary>
    public static FrameworkElement MakeChip(CoreRun run, ChipContext ctx)
    {
        Brush bg, border, fg;
        switch (run.Kind)
        {
            case RunKind.Field:
                bg = Res("ChipFieldBrush"); border = Res("ChipFieldLineBrush"); fg = Res("ChipFieldInkBrush");
                break;
            case RunKind.Cursor:
                bg = Res("ChipCursorBrush"); border = Res("ChipCursorLineBrush"); fg = Res("ChipCursorInkBrush");
                break;
            default:
                bg = Res("ChipVarBrush"); border = Res("ChipVarLineBrush"); fg = Res("ChipVarInkBrush");
                break;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = Preview.ChipKind(run),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        });
        var value = Preview.ChipValue(run, ctx.GlobalEcho, ctx.Now);
        if (value.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = value, FontSize = 13, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        }

        var chip = new Border
        {
            Child = panel,
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(1, 0, 1, 0),
            Tag = run,
            Cursor = Cursors.Hand,
            ToolTip = Describe(run) + (ctx.OnEdit != null && run.Kind != RunKind.Cursor ? "\nDouble-click to change." : ""),
            Focusable = false,
        };
        if (ctx.OnEdit != null)
        {
            chip.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    ctx.OnEdit(chip);
                    e.Handled = true;
                }
            };
        }
        return chip;
    }

    public static string Describe(CoreRun r) => r.Kind switch
    {
        RunKind.Date => $"Date and time, format {r.Format}",
        RunKind.Clipboard => "Whatever is on the clipboard",
        RunKind.Shell => $"Output of: {r.Command}" + (r.ShellName != null ? $" ({r.ShellName})" : ""),
        RunKind.Random => "One of: " + string.Join(" / ", r.Choices),
        RunKind.GlobalVar => $"Global variable {r.Text}",
        RunKind.Field => $"Espanso asks for \"{r.Text}\" before typing",
        RunKind.Cursor => "The cursor ends up here",
        _ => "",
    };

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
