using System.Text;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Text;

namespace EspansoEditor.Core.Yaml;

/// <summary>Writes one snippet as an Espanso YAML list item, in the same layout Espanso's own files use.</summary>
public static class SnippetWriter
{
    private sealed record VarSpec(string Name, string Type, List<(string Key, object Value)> Params);

    /// <param name="dashColumn">Column of the "- " that starts the item.</param>
    /// <param name="globalNames">Global variable names, so new local variables never shadow them.</param>
    /// <param name="forceQuoted">Write all text as double-quoted strings, never as | blocks.</param>
    public static string EmitItem(Snippet s, int dashColumn, IReadOnlySet<string> globalNames, bool forceQuoted = false)
    {
        if (s.Triggers.Count == 0) throw new InvalidOperationException("A snippet needs at least one trigger.");

        var pad = new string(' ', dashColumn);
        var k = pad + "  ";
        var sb = new StringBuilder();

        if (s.Triggers.Count == 1) sb.Append(pad).Append("- trigger: ").Append(Quote(s.Triggers[0])).Append('\n');
        else sb.Append(pad).Append("- triggers: [").Append(string.Join(", ", s.Triggers.Select(Quote))).Append("]\n");

        if (!string.IsNullOrEmpty(s.Label)) sb.Append(k).Append("label: ").Append(Quote(s.Label)).Append('\n');

        var html = s.Format == SnippetFormat.Html && !s.UsesForm;
        var (text, vars) = BuildText(s, html ? RichText.ToHtml(s.Body) : s.Body, globalNames);
        AppendScalar(sb, k, s.UsesForm ? "form" : html ? "html" : "replace", text, forceQuoted);

        if (s.Word) sb.Append(k).Append("word: true\n");
        if (s.PropagateCase) sb.Append(k).Append("propagate_case: true\n");
        if (s.ForceMode is "clipboard" or "keys") sb.Append(k).Append("force_mode: ").Append(s.ForceMode).Append('\n');

        if (vars.Count > 0)
        {
            sb.Append(k).Append("vars:\n");
            foreach (var v in vars)
            {
                sb.Append(k).Append("  - name: ").Append(v.Name).Append('\n');
                sb.Append(k).Append("    type: ").Append(v.Type).Append('\n');
                if (v.Params.Count == 0) continue;
                sb.Append(k).Append("    params:\n");
                foreach (var (key, value) in v.Params)
                {
                    if (value is string str)
                    {
                        sb.Append(k).Append("      ").Append(key).Append(": ").Append(Quote(str)).Append('\n');
                    }
                    else if (value is IEnumerable<string> list)
                    {
                        sb.Append(k).Append("      ").Append(key).Append(":\n");
                        foreach (var item in list) sb.Append(k).Append("        - ").Append(Quote(item)).Append('\n');
                    }
                }
            }
        }

        // Comments that were inside the snippet are kept, at key indentation so they can never become text.
        foreach (var c in s.PreservedComments) sb.Append(k).Append(c).Append('\n');
        return sb.ToString();
    }

    /// <summary>Builds the replace/form text and the variable list from the body.</summary>
    private static (string Text, List<VarSpec> Vars) BuildText(Snippet s, IReadOnlyList<Run> source, IReadOnlySet<string> globalNames)
    {
        var body = BodyText.Normalize(source);
        var form = s.UsesForm;
        var sb = new StringBuilder();
        var vars = new List<VarSpec>();
        var byName = new Dictionary<string, Run>();

        // Names that must not be used for new local variables.
        var taken = new HashSet<string>(globalNames);
        foreach (var r in body.Where(r => r.Kind == RunKind.GlobalVar)) taken.Add(r.Text);
        foreach (var r in body.Where(r => r.VarName != null)) taken.Add(r.VarName!);

        foreach (var r in body)
        {
            switch (r.Kind)
            {
                case RunKind.Text:
                    sb.Append(EscapeLiteral(r.Text));
                    break;
                case RunKind.Cursor:
                    sb.Append("$|$");
                    break;
                case RunKind.Field:
                    sb.Append("[[").Append(r.Text).Append("]]");
                    break;
                case RunKind.GlobalVar:
                    sb.Append("{{").Append(r.Text).Append("}}");
                    break;
                default:
                    var name = r.VarName;
                    if (name != null && byName.TryGetValue(name, out var existing))
                    {
                        if (!existing.SameContent(r)) name = null; // same name but changed: give it a new one
                    }
                    if (name == null)
                    {
                        name = Unique(BaseName(r), taken);
                        taken.Add(name);
                    }
                    if (!byName.ContainsKey(name))
                    {
                        byName[name] = r;
                        vars.Add(Spec(name, r));
                    }
                    sb.Append("{{").Append(name).Append("}}");
                    break;
            }
        }
        if (form && vars.Count > 0) throw new InvalidOperationException("A snippet with 'ask when typing' fields cannot also use variables.");
        return (sb.ToString(), vars);
    }

    private static string EscapeLiteral(string text) => text.Replace("{{", "\\{\\{");

    private static string BaseName(Run r) => r.Kind switch
    {
        RunKind.Date => r.Format is "%H:%M" or "%H:%M:%S" or "%I:%M %p" ? "time" : "date",
        RunKind.Clipboard => "clipboard",
        RunKind.Shell => "output",
        RunKind.Random => "choice",
        _ => "value",
    };

    private static string Unique(string baseName, HashSet<string> taken)
    {
        if (!taken.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
            if (!taken.Contains(baseName + i)) return baseName + i;
    }

    private static VarSpec Spec(string name, Run r) => r.Kind switch
    {
        RunKind.Date => new VarSpec(name, "date", new() { ("format", r.Format ?? "%d/%m/%Y") }),
        RunKind.Clipboard => new VarSpec(name, "clipboard", new()),
        RunKind.Shell => new VarSpec(name, "shell", r.ShellName is { Length: > 0 }
            ? new() { ("cmd", r.Command ?? ""), ("shell", r.ShellName) }
            : new() { ("cmd", r.Command ?? "") }),
        RunKind.Random => new VarSpec(name, "random", new() { ("choices", r.Choices.ToList()) }),
        _ => throw new InvalidOperationException($"Unexpected chip {r.Kind}"),
    };

    /// <summary>Writes "key: value" choosing a literal block (|) for multi-line text when that is safe.</summary>
    internal static void AppendScalar(StringBuilder sb, string keyPad, string key, string value, bool forceQuoted = false)
    {
        if (!forceQuoted && TryLiteralBlock(value, out var header, out var lines))
        {
            sb.Append(keyPad).Append(key).Append(": ").Append(header).Append('\n');
            foreach (var line in lines)
            {
                if (line.Length == 0) sb.Append('\n');
                else sb.Append(keyPad).Append("  ").Append(line).Append('\n');
            }
        }
        else
        {
            sb.Append(keyPad).Append(key).Append(": ").Append(Quote(value)).Append('\n');
        }
    }

    internal static bool TryLiteralBlock(string value, out string header, out string[] lines)
    {
        header = "";
        lines = Array.Empty<string>();
        var trailing = value.EndsWith('\n');
        var body = trailing ? value[..^1] : value;
        if (!trailing && !body.Contains('\n')) return false;  // one line: quoted is clearer
        if (body.Length == 0 || body.EndsWith('\n')) return false;   // several trailing newlines need |+ ; use quotes instead
        foreach (var ch in body)
        {
            if (ch == '\n' || ch == '\t') continue;
            if (ch < 0x20 || ch == 0x7F || (ch >= 0x80 && ch <= 0x9F) || ch == (char)0xFEFF || ch == (char)0x2028 || ch == (char)0x2029) return false;
        }
        var parts = body.Split('\n');
        // YAML works out the indentation from the first non-empty line: it must not start with blanks,
        // and no whitespace-only line may come before it.
        foreach (var line in parts)
        {
            if (line.Length == 0) continue;
            if (line.Trim().Length == 0) return false;
            if (line[0] == ' ' || line[0] == '\t') return false;
            break;
        }
        if (parts.Any(l => l.Length > 0 && l[0] == '\t')) return false;
        header = trailing ? "|" : "|-";
        lines = parts;
        return true;
    }

    /// <summary>YAML double-quoted string; safe for any text.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (ch < 0x20 || ch == 0x7F || (ch >= 0x80 && ch <= 0x9F) || ch == (char)0xFEFF || ch == (char)0x2028 || ch == (char)0x2029)
                        sb.Append("\\u").Append(((int)ch).ToString("X4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
