using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EspansoEditor.Core.Model;

namespace EspansoEditor.Core.Text;

/// <summary>How a snippet's text is saved and what Espanso does with it.</summary>
public enum SnippetFormat
{
    /// <summary>Plain text, typed as it is (Espanso replace:).</summary>
    Plain,

    /// <summary>Markdown text (**bold**, * bullets, ### headings, `code`), typed as text; Jira Cloud turns it into formatting.</summary>
    Jira,

    /// <summary>Formatted text pasted as rich text (Espanso html:).</summary>
    Html,
}

public enum BlockKind { Line, Heading1, Heading2, Heading3, Bullet, Numbered }

/// <summary>A piece of a line: text with its style, or a chip (date, clipboard, field...).</summary>
public sealed record RichInline(string Text, bool Bold = false, bool Italic = false, bool Code = false, Run? Chip = null)
{
    public bool IsChip => Chip != null;
    public bool SameStyle(RichInline o) => Bold == o.Bold && Italic == o.Italic && Code == o.Code;
}

/// <summary>One line of formatted text: a normal line, a heading or a list item.</summary>
public sealed class RichBlock
{
    public BlockKind Kind { get; set; }

    /// <summary>Leading spaces (list nesting). Kept so Markdown reads back exactly.</summary>
    public string Indent { get; set; } = "";

    /// <summary>"*", "-" or "+" for bullets, "1." style for numbered items.</summary>
    public string Marker { get; set; } = "";

    public List<RichInline> Inlines { get; } = new();

    public bool IsList => Kind is BlockKind.Bullet or BlockKind.Numbered;

    public int HeadingLevel => Kind switch { BlockKind.Heading1 => 1, BlockKind.Heading2 => 2, BlockKind.Heading3 => 3, _ => 0 };
}

/// <summary>
/// Formatted text for Jira Cloud (Markdown) and HTML snippets. Inside the editor a formatted snippet's body is kept
/// as Markdown text with chips; this class turns it into lines with styles and back, and into HTML and back.
/// Only a small, predictable subset is used: bold, italic, inline code, three heading levels, bullet and numbered lists.
/// </summary>
public static partial class RichText
{
    private const char ChipMark = '￼';

    [GeneratedRegex(@"^(?<n>\d{1,9})\. ")]
    private static partial Regex NumberedRegex();

    // ---------- Markdown ----------

    // Stand-ins while parsing: escaped characters (HTML bodies only) and inline code spans.
    private const char EscStar = '', EscTick = '', EscSlash = '', CodeMark = '';

    /// <summary>Lines longer than this are not searched for bold and italic (the search would get slow).</summary>
    private const int MaxStyledLine = 4000;

    /// <param name="escaped">
    /// The body uses backslash escapes (\*, \`, \\, and \ before a line that would otherwise be a heading or list).
    /// HTML snippets are kept this way so literal characters never turn into formatting; Jira text is not, because
    /// it is typed into Jira exactly as written.
    /// </param>
    public static List<RichBlock> ParseMarkdown(IReadOnlyList<Run> body, bool escaped = false)
    {
        var (text, chips) = Flatten(BodyText.Normalize(body));
        var queue = new Queue<Run>(chips);
        var blocks = new List<RichBlock>();
        foreach (var line in text.Split('\n')) blocks.Add(ParseLine(line, queue, escaped));
        return blocks;
    }

    private static RichBlock ParseLine(string line, Queue<Run> chips, bool escaped)
    {
        var block = new RichBlock();
        var indentLength = line.Length - line.TrimStart(' ').Length;
        block.Indent = line[..indentLength];
        var rest = line[indentLength..];

        if (escaped && rest.Length >= 2 && rest[0] == '\\' && (rest[1] is '#' or '-' or '+' || char.IsAsciiDigit(rest[1])))
        {
            rest = rest[1..]; // an escaped line start: plain text, not a heading or list
        }
        else if (indentLength == 0 && rest.StartsWith("### "))
        {
            block.Kind = BlockKind.Heading3; block.Marker = "###"; rest = rest[4..];
        }
        else if (indentLength == 0 && rest.StartsWith("## "))
        {
            block.Kind = BlockKind.Heading2; block.Marker = "##"; rest = rest[3..];
        }
        else if (indentLength == 0 && rest.StartsWith("# "))
        {
            block.Kind = BlockKind.Heading1; block.Marker = "#"; rest = rest[2..];
        }
        else if (rest.Length >= 2 && rest[0] is '*' or '-' or '+' && rest[1] == ' ')
        {
            block.Kind = BlockKind.Bullet; block.Marker = rest[0].ToString(); rest = rest[2..];
        }
        else if (NumberedRegex().Match(rest) is { Success: true } m)
        {
            block.Kind = BlockKind.Numbered; block.Marker = m.Groups["n"].Value + "."; rest = rest[m.Length..];
        }
        block.Inlines.AddRange(ParseInlines(rest, chips, escaped));
        return block;
    }

    private static List<RichInline> ParseInlines(string s, Queue<Run> chips, bool escaped)
    {
        if (escaped) s = Unescape(s);

        // Inline code spans become one stand-in character each, so bold and italic can wrap them.
        var codes = new Queue<string>();
        var sb = new StringBuilder();
        var pos = 0;
        while (pos < s.Length)
        {
            var open = s.IndexOf('`', pos);
            var close = open < 0 ? -1 : s.IndexOf('`', open + 1);
            if (open < 0 || close < 0)
            {
                sb.Append(s, pos, s.Length - pos);
                break;
            }
            if (close == open + 1)
            {
                sb.Append("``");
                pos = close + 1;
                continue;
            }
            sb.Append(s, pos, open - pos).Append(CodeMark);
            codes.Enqueue(s[(open + 1)..close]);
            pos = close + 1;
        }
        s = sb.ToString();

        var styled = new List<RichInline>();
        if (s.Length > MaxStyledLine) styled.Add(new RichInline(s));
        else ParseEmphasis(s, false, false, styled);

        // Put code spans and chips back in their places.
        var result = new List<RichInline>();
        foreach (var piece in styled)
        {
            var parts = piece.Text.Split(CodeMark);
            for (var p = 0; p < parts.Length; p++)
            {
                if (p > 0 && codes.Count > 0) AddWithChips(result, piece with { Text = codes.Dequeue(), Code = true }, chips);
                if (parts[p].Length > 0) AddWithChips(result, piece with { Text = parts[p] }, chips);
            }
        }
        return result.Select(i => i with { Text = Restore(i.Text) }).ToList();
    }

    private static void AddWithChips(List<RichInline> result, RichInline piece, Queue<Run> chips)
    {
        var parts = piece.Text.Split(ChipMark);
        for (var p = 0; p < parts.Length; p++)
        {
            if (p > 0 && chips.Count > 0) result.Add(piece with { Text = "", Chip = chips.Dequeue() });
            if (parts[p].Length > 0) result.Add(piece with { Text = parts[p] });
        }
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '*' or '`' or '\\')
            {
                sb.Append(s[i + 1] switch { '*' => EscStar, '`' => EscTick, _ => EscSlash });
                i++;
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    private static string Restore(string s) =>
        s.IndexOfAny([EscStar, EscTick, EscSlash]) < 0 ? s : s.Replace(EscStar, '*').Replace(EscTick, '`').Replace(EscSlash, '\\');

    private static void ParseEmphasis(string s, bool bold, bool italic, List<RichInline> output)
    {
        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length > 0) output.Add(new RichInline(buf.ToString(), bold, italic));
            buf.Clear();
        }

        var i = 0;
        while (i < s.Length)
        {
            if (!bold && At(s, i, "**") && Opens(s, i, 2))
            {
                var close = FindClose(s, i + 2, "**");
                if (close > 0)
                {
                    Flush();
                    ParseEmphasis(s[(i + 2)..close], true, italic, output);
                    i = close + 2;
                    continue;
                }
            }
            if (!italic && s[i] == '*' && !At(s, i, "**") && Opens(s, i, 1))
            {
                var close = FindClose(s, i + 1, "*");
                if (close > 0)
                {
                    Flush();
                    ParseEmphasis(s[(i + 1)..close], bold, true, output);
                    i = close + 1;
                    continue;
                }
            }
            buf.Append(s[i]);
            i++;
        }
        Flush();
    }

    private static bool At(string s, int i, string marker) => string.CompareOrdinal(s, i, marker, 0, marker.Length) == 0;

    /// <summary>
    /// An opening marker is not inside a word (so 2*3*4 stays text) and is followed by text, not by a space or by
    /// more stars and a space (so "*** WARNING ***" stays text).
    /// </summary>
    private static bool Opens(string s, int i, int length)
    {
        if (i > 0 && char.IsLetterOrDigit(s[i - 1])) return false;
        var j = i + length;
        if (j >= s.Length || char.IsWhiteSpace(s[j])) return false;
        if (s[j] == '*' && (j + 1 >= s.Length || char.IsWhiteSpace(s[j + 1]) || s[j + 1] == '*')) return false;
        return true;
    }

    /// <summary>
    /// The closing marker: after text (not a space), not followed by a letter or digit, and for "*" not part of
    /// "**". For "**" at "***" the inner "*" closes first ("**a *b***").
    /// </summary>
    private static int FindClose(string s, int from, string marker)
    {
        for (var k = from + 1; k <= s.Length - marker.Length; k++)
        {
            if (!At(s, k, marker) || char.IsWhiteSpace(s[k - 1])) continue;
            if (marker == "*" && ((k + 1 < s.Length && s[k + 1] == '*') || s[k - 1] == '*')) continue;
            if (marker == "**" && k + 2 < s.Length && s[k + 2] == '*') k++;
            var after = k + marker.Length;
            if (after < s.Length && char.IsLetterOrDigit(s[after])) continue;
            return k;
        }
        return -1;
    }

    /// <param name="escape">Write backslash escapes (HTML bodies); see <see cref="ParseMarkdown"/>.</param>
    public static List<Run> ToMarkdown(IReadOnlyList<RichBlock> blocks, bool escape = false)
    {
        var runs = new List<Run>();
        for (var b = 0; b < blocks.Count; b++)
        {
            if (b > 0) runs.Add(Run.Literal("\n"));
            var block = blocks[b];
            var inlines = MoveEdgeSpacesOut(block.Inlines);
            var prefix = block.Kind switch
            {
                BlockKind.Heading1 => "# ",
                BlockKind.Heading2 => "## ",
                BlockKind.Heading3 => "### ",
                BlockKind.Bullet => (block.Marker is "*" or "-" or "+" ? block.Marker : "*") + " ",
                BlockKind.Numbered => (block.Marker.Length > 0 ? block.Marker : "1.") + " ",
                _ => "",
            };
            if (escape && block.Kind == BlockKind.Line && NeedsLineEscape(inlines)) prefix = "\\";
            runs.Add(Run.Literal(block.Indent + prefix));

            // Markers open and close only where the style changes, so "**a *b* c**" stays nested.
            var open = new List<string>();
            foreach (var group in Groups(inlines))
            {
                var first = group[0];
                var want = new List<string>();
                if (first.Bold) want.Add("**");
                if (first.Italic) want.Add("*");
                if (first.Code) want.Add("`");
                var keep = 0;
                while (keep < open.Count && keep < want.Count && open[keep] == want[keep]) keep++;
                for (var i = open.Count - 1; i >= keep; i--) runs.Add(Run.Literal(open[i]));
                open.RemoveRange(keep, open.Count - keep);
                for (var i = keep; i < want.Count; i++)
                {
                    runs.Add(Run.Literal(want[i]));
                    open.Add(want[i]);
                }
                foreach (var piece in group) runs.Add(piece.Chip ?? Run.Literal(escape ? Escape(piece.Text) : piece.Text));
            }
            for (var i = open.Count - 1; i >= 0; i--) runs.Add(Run.Literal(open[i]));
        }
        return BodyText.Normalize(runs);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("*", "\\*").Replace("`", "\\`");

    /// <summary>Would this plain line read back as a heading or list item?</summary>
    private static bool NeedsLineEscape(List<RichInline> inlines)
    {
        if (inlines.Count == 0 || inlines[0].IsChip) return false;
        var t = inlines[0].Text;
        if (t.Length == 0) return false;
        if (t[0] is '#' or '-' or '+') return true;
        return char.IsAsciiDigit(t[0]) && NumberedRegex().IsMatch(t);
    }

    /// <summary>
    /// Spaces at the start or end of a bold or italic stretch go outside its markers ("**word** "), which Markdown
    /// needs. Only the edges of the whole stretch move, so nested styles stay nested.
    /// </summary>
    private static List<RichInline> MoveEdgeSpacesOut(IEnumerable<RichInline> inlines)
    {
        var list = inlines.Where(i => i.IsChip || i.Text.Length > 0).ToList();
        list = MoveEdges(list, i => i.Bold, i => i with { Bold = false });
        list = MoveEdges(list, i => i.Italic, i => i with { Italic = false });
        return list;
    }

    private static List<RichInline> MoveEdges(List<RichInline> pieces, Func<RichInline, bool> has, Func<RichInline, RichInline> without)
    {
        var result = new List<RichInline>();
        var i = 0;
        while (i < pieces.Count)
        {
            if (!has(pieces[i]))
            {
                result.Add(pieces[i++]);
                continue;
            }
            var j = i;
            while (j < pieces.Count && has(pieces[j])) j++;
            var stretch = pieces.GetRange(i, j - i);
            i = j;

            var first = stretch[0];
            if (!first.IsChip && !first.Code && first.Text.StartsWith(' '))
            {
                var core = first.Text.TrimStart(' ');
                result.Add(without(first) with { Text = first.Text[..(first.Text.Length - core.Length)] });
                if (core.Length > 0) stretch[0] = first with { Text = core };
                else stretch.RemoveAt(0);
            }
            RichInline? tail = null;
            if (stretch.Count > 0 && stretch[^1] is { IsChip: false, Code: false } last && last.Text.EndsWith(' '))
            {
                var core = last.Text.TrimEnd(' ');
                tail = without(last) with { Text = last.Text[core.Length..] };
                if (core.Length > 0) stretch[^1] = last with { Text = core };
                else stretch.RemoveAt(stretch.Count - 1);
            }
            result.AddRange(stretch);
            if (tail != null) result.Add(tail);
        }
        return result;
    }

    /// <summary>Neighbouring pieces with the same style, so "**a**" is written once, not as "**a****b**".</summary>
    private static List<List<RichInline>> Groups(IEnumerable<RichInline> inlines)
    {
        var groups = new List<List<RichInline>>();
        foreach (var piece in inlines)
        {
            if (!piece.IsChip && piece.Text.Length == 0) continue;
            if (groups.Count > 0 && groups[^1][0].SameStyle(piece)) groups[^1].Add(piece);
            else groups.Add(new List<RichInline> { piece });
        }
        return groups;
    }

    /// <summary>True when the text uses any formatting this editor shows (bold, italic, code, headings, lists).</summary>
    public static bool HasFormatting(IReadOnlyList<RichBlock> blocks) =>
        blocks.Any(b => b.Kind != BlockKind.Line || b.Inlines.Any(i => i.Bold || i.Italic || i.Code));

    /// <summary>True when reading the text as Markdown and writing it back gives exactly the same text.</summary>
    public static bool RoundTrips(IReadOnlyList<Run> body)
    {
        if (body.Any(r => r.Kind == RunKind.Text && r.Text.IndexOfAny([ChipMark, EscStar, EscTick, EscSlash, CodeMark]) >= 0)) return false;
        return BodyText.Signature(ToMarkdown(ParseMarkdown(body))) == BodyText.Signature(body);
    }

    /// <summary>
    /// Should a plain-text snippet open as Jira Cloud (Markdown) formatted text? Only when it reads back exactly and
    /// clearly uses Markdown: bold, italic or code, or at least two headings or list items. A lone "- John" or
    /// "# comment" stays plain text.
    /// </summary>
    public static bool LooksLikeMarkdown(IReadOnlyList<Run> body)
    {
        if (!RoundTrips(body)) return false;
        var blocks = ParseMarkdown(body);
        if (blocks.Any(b => b.Inlines.Any(i => i.Bold || i.Italic || i.Code))) return true;
        return blocks.Count(b => b.Kind != BlockKind.Line) >= 2;
    }

    /// <summary>The text with formatting dropped: bullets become "• ", headings plain lines.</summary>
    public static List<Run> ToPlain(IReadOnlyList<RichBlock> blocks)
    {
        var runs = new List<Run>();
        for (var b = 0; b < blocks.Count; b++)
        {
            if (b > 0) runs.Add(Run.Literal("\n"));
            var block = blocks[b];
            runs.Add(Run.Literal(block.Indent + block.Kind switch
            {
                BlockKind.Bullet => "• ",
                BlockKind.Numbered => (block.Marker.Length > 0 ? block.Marker : "1.") + " ",
                _ => "",
            }));
            foreach (var piece in block.Inlines) runs.Add(piece.Chip ?? Run.Literal(piece.Text));
        }
        return BodyText.Normalize(runs);
    }

    /// <summary>The form HTML snippets are kept in: list nesting dropped, numbers counted from 1.</summary>
    public static List<Run> CanonicalForHtml(IReadOnlyList<Run> body) => ToMarkdown(ToHtmlShape(ParseMarkdown(body, escaped: true)), escape: true);

    private static List<RichBlock> ToHtmlShape(List<RichBlock> blocks)
    {
        var n = 0;
        RichBlock? previous = null;
        foreach (var b in blocks)
        {
            b.Indent = "";
            if (b.Kind == BlockKind.Bullet) b.Marker = "*";
            if (b.Kind == BlockKind.Numbered)
            {
                n = previous?.Kind == BlockKind.Numbered ? n + 1 : 1;
                b.Marker = n + ".";
            }
            previous = b;
        }
        return blocks;
    }

    // ---------- HTML ----------

    /// <summary>HTML for a formatted body. Chips stay chips, so variables still work.</summary>
    public static List<Run> ToHtml(IReadOnlyList<Run> markdownBody)
    {
        var blocks = ToHtmlShape(ParseMarkdown(markdownBody, escaped: true));
        foreach (var b in blocks)
        {
            var moved = MoveEdgeSpacesOut(b.Inlines);
            b.Inlines.Clear();
            b.Inlines.AddRange(moved);
        }
        var runs = new List<Run>();
        string? openList = null;
        void Text(string t) => runs.Add(Run.Literal(t));

        foreach (var b in blocks)
        {
            var listTag = b.Kind switch { BlockKind.Bullet => "ul", BlockKind.Numbered => "ol", _ => null };
            if (openList != null && openList != listTag)
            {
                Text($"</{openList}>\n");
                openList = null;
            }
            if (listTag != null && openList == null)
            {
                Text($"<{listTag}>\n");
                openList = listTag;
            }

            var (open, close) = b.Kind switch
            {
                BlockKind.Heading1 => ("<h1>", "</h1>"),
                BlockKind.Heading2 => ("<h2>", "</h2>"),
                BlockKind.Heading3 => ("<h3>", "</h3>"),
                BlockKind.Bullet or BlockKind.Numbered => ("<li>", "</li>"),
                _ => ("<div>", "</div>"),
            };
            Text(open);
            if (b.Kind == BlockKind.Line && b.Inlines.All(i => !i.IsChip && i.Text.Length == 0)) Text("<br>");
            foreach (var group in Groups(b.Inlines))
            {
                var first = group[0];
                Text((first.Bold ? "<strong>" : "") + (first.Italic ? "<em>" : "") + (first.Code ? "<code>" : ""));
                foreach (var piece in group)
                {
                    if (piece.Chip != null) runs.Add(piece.Chip);
                    else Text(HtmlEscape(piece.Text));
                }
                Text((first.Code ? "</code>" : "") + (first.Italic ? "</em>" : "") + (first.Bold ? "</strong>" : ""));
            }
            Text(close + "\n");
        }
        if (openList != null) Text($"</{openList}>\n");
        return BodyText.Normalize(runs);
    }

    private static string HtmlEscape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"<(?<close>/?)(?<tag>[a-zA-Z][a-zA-Z0-9]*)\s*/?>")]
    private static partial Regex TagRegex();

    /// <summary>
    /// Reads HTML made of the simple tags this editor writes (div, p, br, h1-h3, ul, ol, li, strong/b, em/i, code)
    /// into Markdown. Returns null for anything else (attributes, tables, links...), so such snippets stay read-only.
    /// </summary>
    public static List<Run>? FromHtml(IReadOnlyList<Run> htmlBody)
    {
        var (html, chips) = Flatten(BodyText.Normalize(htmlBody));
        if (htmlBody.Any(r => r.Kind == RunKind.Text && r.Text.IndexOfAny([ChipMark, EscStar, EscTick, EscSlash, CodeMark]) >= 0)) return null;
        var queue = new Queue<Run>(chips);
        var blocks = new List<RichBlock>();
        RichBlock? current = null;
        string? list = null;
        var number = 0;
        bool bold = false, italic = false, code = false;
        var lastWasBr = false;

        void Finish()
        {
            if (current != null) blocks.Add(current);
            current = null;
        }
        RichBlock Start(BlockKind kind, string marker = "")
        {
            Finish();
            current = new RichBlock { Kind = kind, Marker = marker };
            return current;
        }
        void AddText(string raw)
        {
            if (raw.Length == 0) return;
            var text = WebUtility.HtmlDecode(raw);
            if (current == null)
            {
                if (text.Trim().Length == 0) return;      // whitespace between tags
                Start(BlockKind.Line);
            }
            if (text.Contains('\n')) text = Regex.Replace(text, @"\s*\n\s*", " ");
            var pieces = text.Split(ChipMark);
            for (var p = 0; p < pieces.Length; p++)
            {
                if (p > 0 && queue.Count > 0) current!.Inlines.Add(new RichInline("", bold, italic, code, queue.Dequeue()));
                if (pieces[p].Length > 0) current!.Inlines.Add(new RichInline(pieces[p], bold, italic, code));
            }
            lastWasBr = false;
        }

        var pos = 0;
        foreach (Match m in TagRegex().Matches(html))
        {
            var between = html[pos..m.Index];
            if (between.Contains('<') || between.Contains('>')) return null;
            AddText(between);
            pos = m.Index + m.Length;

            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var closing = m.Groups["close"].Value == "/";
            switch (tag)
            {
                case "div" or "p":
                    if (closing) Finish();
                    else Start(BlockKind.Line);
                    break;
                case "h1" or "h2" or "h3":
                    if (closing) Finish();
                    else Start(tag == "h1" ? BlockKind.Heading1 : tag == "h2" ? BlockKind.Heading2 : BlockKind.Heading3);
                    break;
                case "ul" or "ol":
                    if (!closing && list != null) return null; // nested lists: kept read-only
                    Finish();
                    list = closing ? null : tag;
                    number = 0;
                    break;
                case "li":
                    if (closing) Finish();
                    else if (list == "ol") Start(BlockKind.Numbered, ++number + ".");
                    else Start(BlockKind.Bullet, "*");
                    break;
                case "br":
                    if (current?.IsList == true) return null; // a line break inside a list item: kept read-only
                    // <div><br></div> is an empty line; a <br> inside text starts a new line.
                    if (current != null && current.Inlines.Count > 0)
                    {
                        Finish();
                        Start(BlockKind.Line);
                        lastWasBr = true;
                    }
                    else if (current == null)
                    {
                        blocks.Add(new RichBlock());
                    }
                    break;
                case "strong" or "b":
                    bold = !closing;
                    break;
                case "em" or "i":
                    italic = !closing;
                    break;
                case "code":
                    code = !closing;
                    break;
                default:
                    return null;
            }
        }
        var tail = html[pos..];
        if (tail.Contains('<') || tail.Contains('>')) return null;
        AddText(tail);
        if (lastWasBr && current is { Inlines.Count: 0 }) current = null;
        Finish();
        if (blocks.Count == 0) blocks.Add(new RichBlock());
        return ToMarkdown(blocks, escape: true);
    }

    // ---------- helpers ----------

    private static (string Text, List<Run> Chips) Flatten(IEnumerable<Run> runs)
    {
        var sb = new StringBuilder();
        var chips = new List<Run>();
        foreach (var r in runs)
        {
            if (r.Kind == RunKind.Text)
            {
                sb.Append(r.Text);
            }
            else
            {
                sb.Append(ChipMark);
                chips.Add(r);
            }
        }
        return (sb.ToString(), chips);
    }
}
