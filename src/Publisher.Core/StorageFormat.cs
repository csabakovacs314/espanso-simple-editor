using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Publisher.Core;

/// <summary>
/// Markdown to Confluence storage format (the XHTML Confluence Cloud keeps pages in), for the same subset
/// tools/md2jira.py handles: headings, paragraphs, bullet and numbered lists (nested by indent), bold, italic,
/// inline code, links, pictures, code blocks, tables, rules, and quote boxes ("> **In short**" becomes an info
/// panel, "> **Tip:**" a tip, "> **Warning:**" a warning). A table of contents follows the first panel when the
/// page has four or more sections.
/// </summary>
public static partial class StorageFormat
{
    [GeneratedRegex(@"^\s*```\s*([^\s`]*)\s*$")] private static partial Regex Fence();
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*---+\s*$")] private static partial Regex Rule();
    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")] private static partial Regex Bullet();
    [GeneratedRegex(@"^(\s*)\d+\.\s+(.*)$")] private static partial Regex Number();
    [GeneratedRegex(@"^\s*>")] private static partial Regex Quote();
    [GeneratedRegex(@"^\s*\|[\s:|-]+\|\s*$")] private static partial Regex TableRule();
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)\)")] private static partial Regex Image();
    [GeneratedRegex(@"(?<!!)\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex Link();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"(?<![\w*])\*(\S(?:[^*]*?\S)?)\*(?![\w*])")] private static partial Regex StarItalic();
    [GeneratedRegex(@"(?<![\w])_(\S(?:[^_]*?\S)?)_(?![\w])")] private static partial Regex UnderscoreItalic();
    [GeneratedRegex(@"\bhttps?://[^\s<>""']*[^\s<>""'.,;:!?)]")] private static partial Regex BareUrl();
    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\uFFFE\uFFFF]")] private static partial Regex NotXml();

    /// <summary>File names of the pictures a Markdown text refers to, in order, without duplicates.</summary>
    public static List<string> ImagePaths(string markdown) =>
        Image().Matches(markdown).Select(m => m.Groups[2].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string AttachmentName(string imagePath) => imagePath.Replace('\\', '/').Split('/')[^1];

    public static string Convert(string markdown)
    {
        var lines = Reflow(markdown.Replace("\r\n", "\n"));
        var sections = lines.Count(l => l.StartsWith("## "));
        var outp = new StringBuilder();
        var tocDone = false;
        var i = 0;
        while (i < lines.Count)
        {
            var ln = lines[i];

            var fence = Fence().Match(ln);
            if (fence.Success)
            {
                var code = new List<string>();
                i++;
                while (i < lines.Count && !Fence().IsMatch(lines[i])) code.Add(lines[i++]);
                i++; // closing fence
                outp.Append("<ac:structured-macro ac:name=\"code\">");
                if (fence.Groups[1].Value.Length > 0)
                    outp.Append("<ac:parameter ac:name=\"language\">").Append(Esc(XmlSafe(fence.Groups[1].Value))).Append("</ac:parameter>");
                outp.Append("<ac:plain-text-body><![CDATA[").Append(XmlSafe(string.Join("\n", code)).Replace("]]>", "]]]]><![CDATA[>"))
                    .Append("]]></ac:plain-text-body></ac:structured-macro>\n");
                continue;
            }

            if (ln.Trim().Length == 0)
            {
                i++;
                continue;
            }

            if (Rule().IsMatch(ln))
            {
                outp.Append("<hr />\n");
                i++;
                continue;
            }

            var h = Heading().Match(ln);
            if (h.Success)
            {
                var level = h.Groups[1].Length;
                outp.Append($"<h{level}>").Append(Inline(h.Groups[2].Value)).Append($"</h{level}>\n");
                i++;
                continue;
            }

            if (Quote().IsMatch(ln))
            {
                var body = new List<string>();
                while (i < lines.Count && Quote().IsMatch(lines[i])) body.Add(Regex.Replace(lines[i++], @"^\s*>\s?", ""));
                var first = body.Count > 0 ? body[0].Trim() : "";
                var box = Regex.Match(first, @"^\*\*(In short|Tip:|Warning:)\*\*\s*(.*)$");
                if (box.Success)
                {
                    body[0] = box.Groups[2].Value;
                    var (macro, title) = box.Groups[1].Value switch
                    {
                        "In short" => ("info", "In short"),
                        "Tip:" => ("tip", "Tip"),
                        _ => ("warning", "Warning"),
                    };
                    outp.Append($"<ac:structured-macro ac:name=\"{macro}\"><ac:parameter ac:name=\"title\">{title}</ac:parameter><ac:rich-text-body>");
                    foreach (var b in body.Where(b => b.Trim().Length > 0)) outp.Append("<p>").Append(Inline(b)).Append("</p>");
                    outp.Append("</ac:rich-text-body></ac:structured-macro>\n");
                    if (macro == "info" && !tocDone && sections >= 4)
                    {
                        outp.Append("<ac:structured-macro ac:name=\"toc\"><ac:parameter ac:name=\"maxLevel\">2</ac:parameter></ac:structured-macro>\n");
                        tocDone = true;
                    }
                }
                else
                {
                    outp.Append("<blockquote>");
                    foreach (var b in body.Where(b => b.Trim().Length > 0)) outp.Append("<p>").Append(Inline(b)).Append("</p>");
                    outp.Append("</blockquote>\n");
                }
                continue;
            }

            if (ln.TrimStart().StartsWith('|') && i + 1 < lines.Count && TableRule().IsMatch(lines[i + 1]))
            {
                outp.Append("<table><tbody>");
                var head = Cells(ln);
                if (head.Any(c => c.Length > 0))
                    outp.Append("<tr>").Append(string.Concat(head.Select(c => "<th>" + Inline(c) + "</th>"))).Append("</tr>");
                i += 2;
                while (i < lines.Count && lines[i].TrimStart().StartsWith('|'))
                    outp.Append("<tr>").Append(string.Concat(Cells(lines[i++]).Select(c => "<td>" + Inline(c) + "</td>"))).Append("</tr>");
                outp.Append("</tbody></table>\n");
                continue;
            }

            if (Bullet().IsMatch(ln) || Number().IsMatch(ln))
            {
                i = ListBlock(lines, i, outp);
                continue;
            }

            outp.Append("<p>").Append(Inline(ln)).Append("</p>\n");
            i++;
        }
        return outp.ToString();
    }

    /// <summary>Consecutive list lines become nested lists: a deeper indent opens a list inside the last item.</summary>
    private static int ListBlock(List<string> lines, int i, StringBuilder outp)
    {
        var open = new List<(int Indent, string Tag)>();
        var itemOpen = new List<bool>();
        while (i < lines.Count)
        {
            var b = Bullet().Match(lines[i]);
            var n = Number().Match(lines[i]);
            if (!b.Success && !n.Success) break;
            var m = b.Success ? b : n;
            var indent = m.Groups[1].Length;
            var tag = b.Success ? "ul" : "ol";

            while (open.Count > 0 && open[^1].Indent > indent)
            {
                if (itemOpen[^1]) outp.Append("</li>");
                outp.Append($"</{open[^1].Tag}>");
                open.RemoveAt(open.Count - 1);
                itemOpen.RemoveAt(itemOpen.Count - 1);
            }
            if (open.Count > 0 && open[^1].Indent == indent && open[^1].Tag != tag)
            {
                if (itemOpen[^1]) outp.Append("</li>");
                outp.Append($"</{open[^1].Tag}>");
                open.RemoveAt(open.Count - 1);
                itemOpen.RemoveAt(itemOpen.Count - 1);
            }
            if (open.Count == 0 || open[^1].Indent < indent)
            {
                outp.Append($"<{tag}>");
                open.Add((indent, tag));
                itemOpen.Add(false);
            }
            else if (itemOpen[^1])
            {
                outp.Append("</li>");
            }
            outp.Append("<li>").Append(Inline(m.Groups[2].Value));
            itemOpen[^1] = true;
            i++;
        }
        for (var k = open.Count - 1; k >= 0; k--)
        {
            if (itemOpen[k]) outp.Append("</li>");
            outp.Append($"</{open[k].Tag}>");
        }
        outp.Append('\n');
        return i;
    }

    private static List<string> Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

    /// <summary>Joins wrapped lines into whole paragraphs and list items, like md2jira.</summary>
    private static List<string> Reflow(string md)
    {
        var outp = new List<string>();
        var inCode = false;
        bool BlockStart(string l) => l.Trim().Length == 0 || Fence().IsMatch(l) || Heading().IsMatch(l) || Rule().IsMatch(l)
                                     || l.TrimStart().StartsWith('|') || Bullet().IsMatch(l) || Number().IsMatch(l)
                                     || Quote().IsMatch(l) || Image().IsMatch(l.Trim()) && Image().Match(l.Trim()).Length == l.Trim().Length;
        foreach (var ln in md.Split('\n'))
        {
            if (Fence().IsMatch(ln))
            {
                inCode = !inCode;
                outp.Add(ln);
                continue;
            }
            if (inCode)
            {
                outp.Add(ln);
                continue;
            }
            var prev = outp.Count > 0 ? outp[^1] : null;
            var prevOpen = prev != null && prev.Trim().Length > 0 && !(Fence().IsMatch(prev) || Heading().IsMatch(prev) || Rule().IsMatch(prev)
                                                                     || prev.TrimStart().StartsWith('|') || Quote().IsMatch(prev));
            if (prevOpen && !BlockStart(ln)) outp[^1] = prev!.TrimEnd() + " " + ln.Trim();
            else outp.Add(ln);
        }
        return outp;
    }

    /// <summary>Text with bold, italic, code, links and pictures, escaped for XHTML.</summary>
    public static string Inline(string text)
    {
        var codes = new List<string>();
        var t = Regex.Replace(XmlSafe(text), "`([^`]+)`", m =>
        {
            codes.Add(m.Groups[1].Value);
            return $"\u0000{codes.Count - 1}\u0000";
        });
        var pictures = new List<string>();
        t = Image().Replace(t, m =>
        {
            pictures.Add(AttachmentName(m.Groups[2].Value));
            return $"\u0001{pictures.Count - 1}\u0001";
        });
        var links = new List<(string Text, string Url)>();
        t = Link().Replace(t, m =>
        {
            links.Add((m.Groups[1].Value, m.Groups[2].Value));
            return $"\u0002{links.Count - 1}\u0002";
        });

        var urls = new List<string>();
        t = BareUrl().Replace(t, m =>
        {
            urls.Add(m.Value);
            return $"\u0003{urls.Count - 1}\u0003";
        });

        t = Esc(t);
        t = Bold().Replace(t, "<strong>$1</strong>");
        t = StarItalic().Replace(t, "<em>$1</em>");
        t = UnderscoreItalic().Replace(t, "<em>$1</em>");

        t = Regex.Replace(t, "\u0003(\\d+)\u0003", m =>
        {
            var url = Esc(urls[int.Parse(m.Groups[1].Value)]);
            return $"<a href=\"{url}\">{url}</a>";
        });

        t = Regex.Replace(t, "\u0002(\\d+)\u0002", m =>
        {
            var (label, url) = links[int.Parse(m.Groups[1].Value)];
            return $"<a href=\"{Esc(url)}\">{Esc(label)}</a>";
        });
        t = Regex.Replace(t, "\u0001(\\d+)\u0001", m =>
            $"<ac:image ac:width=\"900\"><ri:attachment ri:filename=\"{Esc(pictures[int.Parse(m.Groups[1].Value)])}\" /></ac:image>");
        t = Regex.Replace(t, "\u0000(\\d+)\u0000", m => "<code>" + Esc(codes[int.Parse(m.Groups[1].Value)]) + "</code>");
        return t;
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s).Replace("&#39;", "'");

    /// <summary>Drops characters XML cannot hold (control characters other than tab and line breaks), which Confluence would refuse.</summary>
    private static string XmlSafe(string s) => NotXml().Replace(s, "");
}
