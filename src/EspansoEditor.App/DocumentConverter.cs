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
