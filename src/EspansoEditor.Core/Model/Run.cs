using System.Text;

namespace EspansoEditor.Core.Model;

/// <summary>What a piece of snippet text is: plain words or one of the "chips" (variables, form fields, cursor).</summary>
public enum RunKind
{
    Text,
    Date,
    Clipboard,
    Shell,
    Random,
    GlobalVar,
    Field,
    Cursor
}

/// <summary>
/// One piece of a snippet body. A body is a flat list of runs; line breaks live inside Text runs as '\n'.
/// VarName remembers the original Espanso variable name so a re-save keeps it.
/// </summary>
public sealed class Run
{
    public RunKind Kind { get; init; }

    /// <summary>Text: the literal text. GlobalVar: the variable name. Field: the field name.</summary>
    public string Text { get; init; } = "";

    /// <summary>Date: strftime-style format, e.g. %d/%m/%Y.</summary>
    public string? Format { get; init; }

    /// <summary>Shell: the command.</summary>
    public string? Command { get; init; }

    /// <summary>Shell: optional shell name (cmd, powershell, pwsh, wsl...). Null means Espanso's default.</summary>
    public string? ShellName { get; init; }

    /// <summary>Random: the choices.</summary>
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>The Espanso variable name this chip came from, reused on save. Null for new chips.</summary>
    public string? VarName { get; init; }

    public bool IsChip => Kind != RunKind.Text;

    public static Run Literal(string text) => new() { Kind = RunKind.Text, Text = text };
    public static Run Date(string format, string? varName = null) => new() { Kind = RunKind.Date, Format = format, VarName = varName };
    public static Run Clipboard(string? varName = null) => new() { Kind = RunKind.Clipboard, VarName = varName };
    public static Run Shell(string command, string? shellName = null, string? varName = null) => new() { Kind = RunKind.Shell, Command = command, ShellName = shellName, VarName = varName };
    public static Run Random(IEnumerable<string> choices, string? varName = null) => new() { Kind = RunKind.Random, Choices = choices.ToArray(), VarName = varName };
    public static Run Global(string name) => new() { Kind = RunKind.GlobalVar, Text = name };
    public static Run Field(string name) => new() { Kind = RunKind.Field, Text = name };
    public static Run Cursor() => new() { Kind = RunKind.Cursor };

    public Run WithVarName(string? name) => new()
    {
        Kind = Kind, Text = Text, Format = Format, Command = Command, ShellName = ShellName, Choices = Choices, VarName = name
    };

    /// <summary>True when both runs mean the same thing (the variable name is ignored).</summary>
    public bool SameContent(Run other) =>
        Kind == other.Kind
        && Text == other.Text
        && Format == other.Format
        && Command == other.Command
        && ShellName == other.ShellName
        && Choices.SequenceEqual(other.Choices);

    /// <summary>Stable text form used to compare snippets.</summary>
    public string Signature()
    {
        var sb = new StringBuilder();
        sb.Append(Kind).Append('|').Append(Text).Append('|').Append(Format).Append('|').Append(Command).Append('|').Append(ShellName).Append('|');
        sb.Append(string.Join("\u001f", Choices));
        return sb.ToString();
    }

    public override string ToString() => Kind == RunKind.Text ? Text : $"<{Kind}:{Text}{Format}{Command}{string.Join("|", Choices)}>";
}

public static class BodyText
{
    /// <summary>Merges neighbouring text runs and drops empty ones, so equal bodies compare equal.</summary>
    public static List<Run> Normalize(IEnumerable<Run> runs)
    {
        var result = new List<Run>();
        var text = new System.Text.StringBuilder();
        void FlushText()
        {
            if (text.Length > 0) result.Add(Run.Literal(text.ToString()));
            text.Clear();
        }
        foreach (var r in runs)
        {
            if (r.Kind == RunKind.Text)
            {
                text.Append(r.Text);
                continue;
            }
            FlushText();
            result.Add(r);
        }
        FlushText();
        return result;
    }

    public static bool SameContent(IEnumerable<Run> a, IEnumerable<Run> b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
            if (!x[i].SameContent(y[i])) return false;
        return true;
    }

    public static string Signature(IEnumerable<Run> runs) => string.Join("\u001e", Normalize(runs).Select(r => r.Signature()));

    /// <summary>The literal text only, chips left out. Used for display names.</summary>
    public static string LiteralText(IEnumerable<Run> runs) => string.Concat(runs.Where(r => r.Kind == RunKind.Text).Select(r => r.Text));
}
