using System.Text;
using EspansoEditor.Core.Model;

namespace EspansoEditor.Core.Yaml;

/// <summary>The new file text, plus any untouched snippet that had to be rewritten as a side effect.</summary>
public sealed record RewriteResult(string Text, IReadOnlySet<Snippet> Reemitted);

/// <summary>
/// Produces the new text of a match file by editing only what changed: changed snippets are rewritten in place,
/// deleted ones are cut out, new ones are inserted into their group. Comments, blank lines and every
/// untouched snippet stay exactly as they were.
/// </summary>
public static class FileRewriter
{
    private sealed record Edit(int Start, int End, string Text);

    public static string Rewrite(MatchFile f, IReadOnlySet<string> globalNames, string groupDelimiter = "---", bool forceQuoted = false,
        bool minimal = false) =>
        RewriteWithInfo(f, globalNames, groupDelimiter, forceQuoted, minimal).Text;

    /// <param name="minimal">
    /// Change as few bytes as possible: a snippet that is back to what is on disk is left alone, and a snippet whose only
    /// change is "Only as a whole word" gets a one-line edit. Every other change rewrites the snippet as usual.
    /// </param>
    public static RewriteResult RewriteWithInfo(MatchFile f, IReadOnlySet<string> globalNames, string groupDelimiter = "---", bool forceQuoted = false,
        bool minimal = false)
    {
        if (f.IsReadOnly) throw new InvalidOperationException($"{f.RelativePath} is read-only: {f.ReadOnlyReason}");

        var text = f.Text;
        var edits = new List<Edit>();
        var removed = new HashSet<Snippet>(f.Removed.Where(s => ReferenceEquals(s.OriginFile, f) && s.Span != null));
        var reemitted = new HashSet<Snippet>();
        string Emit(Snippet s, int col) => SnippetWriter.EmitItem(s, col, globalNames, forceQuoted);

        // 1. Snippets that leave their original place in this file.
        foreach (var s in removed)
        {
            var sp = s.Span!.Value;
            var end = sp.End;
            // Avoid leaving two blank lines where one was.
            if (IsBlankLineBefore(text, sp.Start) && end < text.Length)
            {
                var nl = text.IndexOf('\n', end);
                var lineEnd = nl < 0 ? text.Length : nl + 1;
                if (text[end..lineEnd].Trim().Length == 0) end = lineEnd;
            }
            edits.Add(new Edit(sp.Start, end, ""));
        }

        // 2. Changed snippets that stay where they are.
        var staying = f.Snippets.Where(s => s.Span != null && ReferenceEquals(s.OriginFile, f) && !removed.Contains(s)).ToList();
        var rewritten = new HashSet<Snippet>();
        foreach (var s in staying.Where(s => s.IsDirty))
        {
            var small = minimal ? SmallEdits(s, text) : null;
            if (small != null)
            {
                edits.AddRange(small);
                continue;
            }
            edits.Add(new Edit(s.Span!.Value.Start, s.Span!.Value.End, Emit(s, s.Span!.Value.DashColumn)));
            rewritten.Add(s);
        }

        // 3. New or moved-in snippets, inserted at the end of their group.
        var inserts = f.Snippets.Where(s => s.Span == null || !ReferenceEquals(s.OriginFile, f) || removed.Contains(s)).ToList();
        if (inserts.Count > 0)
        {
            var indent = f.ItemIndent;
            var pad = new string(' ', indent);
            var sep = f.BlankLineBetweenItems ? "\n" : "";
            var byAnchor = new SortedDictionary<int, StringBuilder>();
            var newGroupText = new StringBuilder();
            string Marker(string group) => pad + "# " + groupDelimiter + " " + group + " " + groupDelimiter + "\n";

            foreach (var group in inserts.Select(s => s.Group).Distinct())
            {
                var items = inserts.Where(s => s.Group == group).Select(s => Emit(s, indent)).ToList();
                var inGroup = staying.Where(s => s.OriginGroup == group).ToList();
                var marker = f.Groups.FirstOrDefault(g => g.Name == group);

                if (!f.HasMatchesKey)
                {
                    if (group.Length > 0) newGroupText.Append(sep).Append(Marker(group));
                    foreach (var item in items) newGroupText.Append(sep).Append(item);
                }
                else if (inGroup.Count > 0)
                {
                    var sb = Get(byAnchor, inGroup.Max(s => s.Span!.Value.End));
                    foreach (var item in items) sb.Append(sep).Append(item);
                }
                else if (marker != null)
                {
                    var sb = Get(byAnchor, marker.LineEnd);
                    foreach (var item in items) sb.Append(item).Append(sep);
                }
                else if (group.Length == 0)
                {
                    var sb = Get(byAnchor, f.MatchesKeyLineEnd);
                    foreach (var item in items) sb.Append(item).Append(sep);
                }
                else
                {
                    newGroupText.Append(sep).Append(Marker(group));
                    foreach (var item in items) newGroupText.Append(item).Append(sep);
                }
            }

            if (newGroupText.Length > 0)
            {
                if (!f.HasMatchesKey)
                {
                    var prefix = new StringBuilder();
                    if (text.Length > 0 && !text.EndsWith('\n')) prefix.Append('\n');
                    if (text.Trim().Length > 0) prefix.Append('\n');
                    prefix.Append("matches:\n");
                    var body = newGroupText.ToString();
                    if (body.StartsWith('\n')) body = body[1..];
                    edits.Add(new Edit(text.Length, text.Length, prefix + body));
                }
                else
                {
                    var anchor = staying.Count > 0 ? staying.Max(s => s.Span!.Value.End) : f.MatchesKeyLineEnd;
                    var body = newGroupText.ToString();
                    if (staying.Count == 0 && body.StartsWith('\n')) body = body[1..];
                    if (body.EndsWith("\n\n")) body = body[..^1];
                    Get(byAnchor, anchor).Append(body);
                }
            }

            foreach (var (anchor, sb) in byAnchor)
            {
                var insert = sb.ToString();
                if (anchor == text.Length && text.Length > 0 && !text.EndsWith('\n'))
                    insert = EndOfFileInsert(f, text, staying, rewritten, reemitted, edits, globalNames, forceQuoted) + insert;
                edits.Add(new Edit(anchor, anchor, insert));
            }
        }

        // Apply from the end so earlier offsets stay valid. On equal starts, the longer edit goes first,
        // so an insertion lands in front of a rewritten snippet that starts at the same place.
        var ordered = edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var later = ordered[i - 1];
            var earlier = ordered[i];
            if (earlier.End > later.Start && !(earlier.Start == later.Start && later.End == later.Start))
                throw new InvalidOperationException("Internal error: overlapping edits in " + f.RelativePath);
        }

        var result = new StringBuilder(text);
        foreach (var e in ordered)
        {
            result.Remove(e.Start, e.End - e.Start);
            result.Insert(e.Start, e.Text);
        }
        return new RewriteResult(result.ToString(), reemitted);
    }

    /// <summary>
    /// The file ends without a line break and something is appended after the last snippet. Returns the text to put
    /// in front of the insertion. A "|" block at the very end would gain a final line break from that, so such a
    /// snippet is rewritten, but only if its rewrite means exactly the same to Espanso.
    /// </summary>
    private static string EndOfFileInsert(MatchFile f, string text, List<Snippet> staying, HashSet<Snippet> rewritten,
        HashSet<Snippet> reemitted, List<Edit> edits, IReadOnlySet<string> globalNames, bool forceQuoted)
    {
        var last = staying.FirstOrDefault(s => s.Span!.Value.End == text.Length);
        if (last == null || !last.EndsWithBlockScalar) return "\n";   // a line break changes nothing here
        if (rewritten.Contains(last)) return "";                      // already rewritten; its new text ends with a line break
        if (last.IsDirty)
            throw new InvalidOperationException("Internal: the last snippet needs a full rewrite here."); // the save retries without small edits
        if (last.IsAdvanced)
            throw new InvalidOperationException($"{f.RelativePath} ends without a line break right after a snippet this editor cannot rewrite. Add an empty line at the end of that file, then save again.");

        var emitted = SnippetWriter.EmitItem(last, last.Span!.Value.DashColumn, globalNames, forceQuoted);
        var check = MatchFileParser.ParseText(f.FullPath, f.RelativePath, "matches:\n" + emitted, false);
        if (check.Snippets.Count != 1 || check.Snippets[0].OriginalValueCanonical != last.OriginalValueCanonical)
            throw new InvalidOperationException($"{f.RelativePath} ends without a line break right after \"{last.DisplayName}\", which cannot be rewritten without changing it. Add an empty line at the end of that file, then save again.");

        edits.Add(new Edit(last.Span!.Value.Start, last.Span!.Value.End, emitted));
        reemitted.Add(last);
        return "";
    }

    /// <summary>
    /// The smallest edit for a changed snippet that stays in place, or null when it has to be rewritten as a whole.
    /// An empty list means it is back to what is on disk.
    /// </summary>
    private static List<Edit>? SmallEdits(Snippet s, string text)
    {
        if (s.IsAdvanced || s.OriginalSignature == null || s.Span is not { } sp) return null;
        if (s.SemanticSignature() == s.OriginalSignature) return new List<Edit>();
        if (s.Word == s.OriginalWord || s.SemanticSignature(s.OriginalWord) != s.OriginalSignature) return null;
        if (s.WordLayout is not { } layout) return null;

        // A "|" block at the very end of a file without a final line break would gain one; leave that to a full rewrite.
        if (sp.End == text.Length && !text.EndsWith('\n') && s.EndsWithBlockScalar) return null;

        if (s.Word)
        {
            if (layout.WordValue is { } v) return new List<Edit> { new(v.Start, v.End, "true") };
            if (layout.AfterFirstLine is { } at) return new List<Edit> { new(at, at, new string(' ', layout.KeyColumn) + "word: true\n") };
            return null;
        }
        if (layout.WordLine is { } line) return new List<Edit> { new(line.Start, line.End, "") };
        return null;
    }

    private static StringBuilder Get(SortedDictionary<int, StringBuilder> d, int key)
    {
        if (!d.TryGetValue(key, out var sb)) d[key] = sb = new StringBuilder();
        return sb;
    }

    private static bool IsBlankLineBefore(string text, int lineStart)
    {
        if (lineStart == 0) return true;
        var prevEnd = lineStart - 1; // the '\n' ending the previous line
        var prevStart = prevEnd == 0 ? 0 : text.LastIndexOf('\n', prevEnd - 1) + 1;
        return text[prevStart..prevEnd].Trim().Length == 0;
    }
}
