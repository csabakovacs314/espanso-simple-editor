using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EspansoEditor.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using CommentToken = YamlDotNet.Core.Tokens.Comment;

namespace EspansoEditor.Core.Yaml;

/// <summary>
/// Reads a match file into snippets and remembers exactly where each snippet sits in the text,
/// so a save can rewrite only the snippets that changed and leave every other byte alone.
/// </summary>
public static partial class MatchFileParser
{
    // "# --- Customer Communication ---", "# ── Tools ──", "# === Work ===" (closing delimiter optional)
    [GeneratedRegex(@"^\s*#\s*(?<d>-{2,}|─{2,}|={2,})\s*(?<name>.+?)\s*(?:-{2,}|─{2,}|={2,})?\s*$")]
    private static partial Regex MarkerRegex();

    /// <summary>Canonical form of a file with nothing but (possibly) a matches list.</summary>
    internal const string EmptyRoot = "{}";

    private sealed record CommentLine(bool Inline, string Value, int Column);

    public static MatchFile Parse(string fullPath, string relativePath, byte[] bytes, bool isPackage)
    {
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string raw;
        try
        {
            raw = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            var bad = new MatchFile(fullPath, relativePath, isPackage)
            {
                ReadOnlyReason = "Is not valid UTF-8 text (perhaps saved in another encoding), so it is shown read-only to avoid damaging it.",
                LoadError = "not valid UTF-8",
                HasBom = hasBom,
                DiskHash = Hash(bytes),
            };
            return bad;
        }
        var file = ParseText(fullPath, relativePath, raw, isPackage);
        file.HasBom = hasBom;
        file.DiskHash = Hash(bytes);
        return file;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Parses file text. Line endings are normalised to \n; the file's usual style is remembered.</summary>
    public static MatchFile ParseText(string fullPath, string relativePath, string raw, bool isPackage)
    {
        var crlf = Regex.Count(raw, "\r\n");
        var lf = Regex.Count(raw, "(?<!\r)\n");
        var file = new MatchFile(fullPath, relativePath, isPackage)
        {
            Newline = crlf > lf ? "\r\n" : "\n",
        };
        var text = raw.Replace("\r\n", "\n");
        file.Text = text;
        if (isPackage) file.ReadOnlyReason = "Installed package. Package updates would overwrite edits.";
        if (text.Contains('\r')) file.ReadOnlyReason ??= "Uses unusual line endings, so it is shown read-only.";
        if (text.StartsWith('﻿')) file.ReadOnlyReason ??= "Starts with an extra byte-order mark, so it is shown read-only.";

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            file.LoadError = $"Line {ex.Start.Line}: {ex.Message}";
            file.ReadOnlyReason = "Espanso's YAML in this file could not be read. " + file.LoadError;
            return file;
        }

        if (stream.Documents.Count == 0)
        {
            file.HasMatchesKey = false;
            file.RootCanonicalWithoutMatches = EmptyRoot;
            return file;
        }
        if (stream.Documents.Count > 1) file.ReadOnlyReason ??= "Contains several YAML documents, so it is shown read-only.";

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            if (stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
            {
                file.HasMatchesKey = false;
                file.RootCanonicalWithoutMatches = EmptyRoot;
                return file;
            }
            file.ReadOnlyReason ??= "Does not have the usual Espanso layout, so it is shown read-only.";
            return file;
        }

        var lines = new LineIndex(text);
        var comments = ReadComments(text, lines, out var usesAnchors);
        if (usesAnchors) file.ReadOnlyReason ??= "Uses YAML anchors (&name / *name), so it is shown read-only.";
        file.RootCanonicalWithoutMatches = Canonical.Of(root, skipKey: "matches");

        YamlNode? matchesNode = null, matchesKey = null, keyAfterMatches = null;
        var seenMatches = false;
        foreach (var (k, v) in root.Children)
        {
            if (seenMatches && keyAfterMatches == null) keyAfterMatches = k;
            if (k is YamlScalarNode { Value: "matches" })
            {
                if (matchesKey != null) file.ReadOnlyReason ??= "Has two 'matches' entries, so it is shown read-only.";
                matchesKey = k;
                matchesNode = v;
                seenMatches = true;
            }
            if (k is YamlScalarNode { Value: "global_vars" } && v is YamlSequenceNode globals)
            {
                foreach (var g in globals.Children.OfType<YamlMappingNode>())
                {
                    var name = Scalar(g, "name");
                    if (name == null) continue;
                    file.GlobalVarNames.Add(name);
                    if (Scalar(g, "type") == "echo" && g.Children.TryGetValue(new YamlScalarNode("params"), out var p) && p is YamlMappingNode pm)
                    {
                        var echo = Scalar(pm, "echo");
                        if (echo != null) file.GlobalEcho[name] = echo;
                    }
                }
            }
        }

        if (matchesKey == null)
        {
            file.HasMatchesKey = false;
            return file;
        }

        file.HasMatchesKey = true;
        var keyLine = lines.LineOf((int)matchesKey.Start.Index);
        file.MatchesKeyLineEnd = lines.LineEndWithBreak(keyLine);
        var blockEndLine = keyAfterMatches != null ? lines.LineOf((int)keyAfterMatches.Start.Index) : lines.Count;

        if (matchesNode is YamlScalarNode { Value: null or "" }) return file; // "matches:" with nothing under it
        if (matchesNode is not YamlSequenceNode seq)
        {
            file.ReadOnlyReason ??= "Its 'matches' entry is not a list, so it is shown read-only.";
            return file;
        }

        var flow = seq.Style == SequenceStyle.Flow;
        if (flow) file.ReadOnlyReason ??= "Writes its snippets in the compact [ ... ] style, so it is shown read-only.";

        var items = new List<(YamlMappingNode Node, int DashLine, int DashCol)>();
        foreach (var child in seq.Children)
        {
            if (child is not YamlMappingNode map)
            {
                file.ReadOnlyReason ??= "Contains an entry that is not a snippet, so it is shown read-only.";
                continue;
            }
            var (dashLine, dashCol) = flow ? (lines.LineOf((int)map.Start.Index), 0) : FindDash(lines, map);
            if (dashLine < 0)
            {
                file.ReadOnlyReason ??= "Has a snippet layout this editor does not recognise, so it is shown read-only.";
                (dashLine, dashCol) = (lines.LineOf((int)map.Start.Index), 0);
            }
            items.Add((map, dashLine, dashCol));
        }
        if (items.Count > 0) file.ItemIndent = items[0].DashCol;

        bool IsWholeLineComment(int ln) => comments.TryGetValue(ln, out var c) && !c.Inline;

        var spans = new List<(int StartLine, int EndLineExclusive)>();
        for (var i = 0; i < items.Count; i++)
        {
            var (node, dashLine, dashCol) = items[i];
            var regionEnd = i + 1 < items.Count ? items[i + 1].DashLine : blockEndLine;
            var last = regionEnd - 1;
            // Trailing blank lines and real comment lines belong to whatever follows, not to this snippet.
            while (last > dashLine && (lines.Text(last).Trim().Length == 0 || IsWholeLineComment(last))) last--;
            spans.Add((dashLine, last + 1));

            var snippet = SnippetReader.Read(node, file);
            snippet.Span = new TextSpan(lines.Start(dashLine), lines.LineEndWithBreak(last), dashCol);
            snippet.OriginFile = file;
            snippet.OriginalCanonical = Canonical.Of(node);
            snippet.OriginalValueCanonical = Canonical.Of(node, ignoreStyle: true);
            snippet.EndsWithBlockScalar = LastScalar(node) is { Style: ScalarStyle.Literal or ScalarStyle.Folded };
            if (!flow) snippet.WordLayout = FindWordLayout(node, lines, dashLine, text);

            // Comments inside the snippet (between its keys, or at the end of a line) are carried over if it is rewritten.
            for (var ln = dashLine; ln <= last; ln++)
            {
                if (!comments.TryGetValue(ln, out var c)) continue;
                snippet.PreservedComments.Add(c.Inline ? "# " + c.Value.Trim() : lines.Text(ln).Trim());
            }
            file.Snippets.Add(snippet);
        }

        // Blank line between items? Look at the line right after each snippet.
        if (spans.Count > 1)
        {
            var blank = 0;
            for (var i = 0; i < spans.Count - 1; i++)
                if (spans[i].EndLineExclusive < lines.Count && lines.Text(spans[i].EndLineExclusive).Trim().Length == 0) blank++;
            file.BlankLineBetweenItems = blank * 2 >= spans.Count - 1;
        }

        // Group markers: whole-line comments between snippets.
        for (var ln = keyLine + 1; ln < blockEndLine; ln++)
        {
            if (!IsWholeLineComment(ln) || spans.Any(s => ln >= s.StartLine && ln < s.EndLineExclusive)) continue;
            var m = MarkerRegex().Match(lines.Text(ln));
            if (!m.Success) continue;
            var name = m.Groups["name"].Value.Trim();
            if (!name.Any(char.IsLetterOrDigit)) continue;
            file.Groups.Add(new GroupMarker(name, lines.Start(ln), lines.LineEndWithBreak(ln)));
            file.MarkerDelimiter ??= m.Groups["d"].Value;
        }

        foreach (var s in file.Snippets)
        {
            var marker = file.Groups.LastOrDefault(g => g.LineStart < s.Span!.Value.Start);
            s.Group = marker?.Name ?? "";
            s.OriginGroup = s.Group;
            s.OriginalSignature = s.SemanticSignature();
            s.OriginalWord = s.Word;
        }
        return file;
    }

    /// <summary>
    /// Finds where "word" can be switched with a one-line edit: the line after the first key (to add it), the
    /// "word:" line (to remove it) and its value (to flip it). Anything unusual returns null parts, and the
    /// snippet is then rewritten as a whole instead.
    /// </summary>
    private static WordLayout? FindWordLayout(YamlMappingNode node, LineIndex lines, int dashLine, string text)
    {
        if (node.Style == MappingStyle.Flow || node.Children.Count == 0) return null;
        var firstKeyLine = lines.LineOf((int)node.Start.Index);
        if (firstKeyLine != dashLine) return null; // "-" alone on its line
        var keyColumn = (int)node.Start.Index - lines.Start(dashLine);

        var pairs = node.Children.ToList();
        int? afterFirst = null;
        var (k0, v0) = pairs[0];
        var firstFits = lines.LineOf((int)k0.End.Index) == dashLine
                        && lines.LineOf((int)v0.End.Index) == dashLine
                        && v0 is not YamlScalarNode { Style: ScalarStyle.Literal or ScalarStyle.Folded }
                        && (pairs.Count == 1 || lines.LineOf((int)pairs[1].Key.Start.Index) > dashLine);
        var end = lines.LineEndWithBreak(dashLine);
        if (firstFits && end > 0 && text[end - 1] == '\n') afterFirst = end;

        (int, int)? wordLine = null, wordValue = null;
        foreach (var (k, v) in pairs)
        {
            if (k is not YamlScalarNode { Value: "word" }) continue;
            var line = lines.LineOf((int)k.Start.Index);
            if (v is YamlScalarNode { Style: ScalarStyle.Plain } sv && lines.LineOf((int)sv.End.Index) == line && sv.End.Index > sv.Start.Index)
            {
                wordValue = ((int)sv.Start.Index, (int)sv.End.Index);
                var before = text[lines.Start(line)..(int)k.Start.Index];
                if (line != dashLine && before.Trim().Length == 0) wordLine = (lines.Start(line), lines.LineEndWithBreak(line));
            }
            break;
        }
        return new WordLayout(keyColumn, afterFirst, wordLine, wordValue);
    }

    /// <summary>All real comments (not text inside block scalars), by line.</summary>
    private static Dictionary<int, CommentLine> ReadComments(string text, LineIndex lines, out bool usesAnchors)
    {
        var result = new Dictionary<int, CommentLine>();
        usesAnchors = false;
        try
        {
            var scanner = new Scanner(new StringReader(text), skipComments: false);
            while (scanner.MoveNext())
            {
                if (scanner.Current is YamlDotNet.Core.Tokens.Anchor or YamlDotNet.Core.Tokens.AnchorAlias) usesAnchors = true;
                if (scanner.Current is not CommentToken c) continue;
                var line = lines.LineOf((int)c.Start.Index);
                var col = (int)c.Start.Index - lines.Start(line);
                // A comment alone on its line is "whole line" even if the scanner calls it inline.
                var wholeLine = lines.Text(line)[..Math.Min(col, lines.Text(line).Length)].Trim().Length == 0;
                result[line] = new CommentLine(!wholeLine, c.Value, col);
            }
        }
        catch (YamlException)
        {
            // Parsing already succeeded; without comment positions we simply know less.
        }
        return result;
    }

    private static YamlScalarNode? LastScalar(YamlNode node) => node switch
    {
        YamlScalarNode s => s,
        YamlMappingNode m when m.Children.Count > 0 => LastScalar(m.Children.Last().Value),
        YamlSequenceNode q when q.Children.Count > 0 => LastScalar(q.Children[^1]),
        _ => null,
    };

    /// <summary>Finds the "- " that starts a sequence item, on the item's first line or the line above.</summary>
    private static (int Line, int Col) FindDash(LineIndex lines, YamlMappingNode map)
    {
        var startIndex = (int)map.Start.Index;
        var line = lines.LineOf(startIndex);
        var lineText = lines.Text(line);
        var col = startIndex - lines.Start(line);
        var before = lineText[..Math.Min(col, lineText.Length)];
        var dash = before.LastIndexOf('-');
        if (dash >= 0 && before[..dash].Trim().Length == 0 && before[(dash + 1)..].Trim().Length == 0)
            return (line, dash);

        for (var l = line - 1; l >= 0; l--)
        {
            var t = lines.Text(l);
            var trimmed = t.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (trimmed == "-") return (l, t.IndexOf('-'));
            break;
        }
        return (-1, -1);
    }

    internal static string? Scalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

    /// <summary>Line start offsets for quick line/offset lookups.</summary>
    internal sealed class LineIndex
    {
        private readonly string _text;
        private readonly List<int> _starts = new() { 0 };

        public LineIndex(string text)
        {
            _text = text;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n') _starts.Add(i + 1);
            if (_starts.Count > 1 && _starts[^1] == text.Length) _starts.RemoveAt(_starts.Count - 1);
        }

        public int Count => _starts.Count;

        public int Start(int line) => line >= _starts.Count ? _text.Length : _starts[line];

        /// <summary>Offset just after the line's '\n' (or end of text).</summary>
        public int LineEndWithBreak(int line) => line + 1 < _starts.Count ? _starts[line + 1] : _text.Length;

        public string Text(int line)
        {
            var s = Start(line);
            var e = LineEndWithBreak(line);
            var t = _text[s..e];
            return t.EndsWith('\n') ? t[..^1] : t;
        }

        public int LineOf(int offset)
        {
            var i = _starts.BinarySearch(offset);
            return i >= 0 ? i : ~i - 1;
        }
    }
}
