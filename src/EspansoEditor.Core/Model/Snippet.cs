using System.Text;
using EspansoEditor.Core.Text;

namespace EspansoEditor.Core.Model;

/// <summary>Where an existing snippet sits in its file (character offsets into the \n-normalised text).</summary>
public readonly record struct TextSpan(int Start, int End, int DashColumn);

/// <summary>
/// Where the "word" setting can be changed with a one-line edit (offsets into the file text), so switching
/// "Only as a whole word" does not rewrite the rest of the snippet.
/// </summary>
/// <param name="KeyColumn">Column of the snippet's keys.</param>
/// <param name="AfterFirstLine">Offset just after the first line, when the first key and its value fit on it.</param>
/// <param name="WordLine">The whole line holding "word: ...", when it is a line of its own.</param>
/// <param name="WordValue">The value after "word:", when it is a single plain word.</param>
internal sealed record WordLayout(int KeyColumn, int? AfterFirstLine, (int Start, int End)? WordLine, (int Start, int End)? WordValue);

/// <summary>One Espanso match, as the editor sees it.</summary>
public sealed class Snippet
{
    public Guid Id { get; } = Guid.NewGuid();

    public MatchFile File { get; internal set; } = null!;

    /// <summary>Group name ("" = no group). Groups are comment lines like "# --- Name ---" in the file.</summary>
    public string Group { get; internal set; } = "";

    public List<string> Triggers { get; set; } = new();
    public string Label { get; set; } = "";
    public bool Word { get; set; }
    public bool PropagateCase { get; set; }

    /// <summary>Espanso left_word / right_word (read-only snippets): only fires at the start / end of a word.</summary>
    public bool LeftWord { get; internal set; }
    public bool RightWord { get; internal set; }

    /// <summary>
    /// Plain text, Jira Cloud (Markdown text) or HTML (pasted as rich text). For Jira and HTML the body holds
    /// Markdown; HTML is made from it when the snippet is written.
    /// </summary>
    public SnippetFormat Format { get; set; }

    /// <summary>null (automatic), "clipboard" or "keys".</summary>
    public string? ForceMode { get; set; }

    /// <summary>The text with chips. Line breaks are '\n' inside text runs.</summary>
    public List<Run> Body { get; set; } = new();

    /// <summary>Why the editor cannot change this snippet (it uses features the form does not cover). Null for normal snippets.</summary>
    public string? AdvancedReason { get; internal set; }

    /// <summary>Set by the editor whenever the user changes something.</summary>
    public bool IsDirty { get; set; }

    // Where it came from (null/empty for snippets created in this session).
    public TextSpan? Span { get; internal set; }
    public MatchFile? OriginFile { get; internal set; }
    public string OriginGroup { get; internal set; } = "";
    internal string? OriginalCanonical { get; set; }
    internal string? OriginalValueCanonical { get; set; }
    internal bool EndsWithBlockScalar { get; set; }

    /// <summary>The meaning of the snippet as it is on disk, and its "word" setting there.</summary>
    internal string? OriginalSignature { get; set; }
    internal bool OriginalWord { get; set; }
    internal WordLayout? WordLayout { get; set; }

    /// <summary>Comments that sat inside this snippet in the file; kept (as comment lines) if it is rewritten.</summary>
    public List<string> PreservedComments { get; } = new();

    public bool IsAdvanced => AdvancedReason != null;
    public bool IsReadOnly => IsAdvanced || File.IsReadOnly;
    public bool IsNew => Span is null;
    public bool UsesForm => Body.Any(r => r.Kind == RunKind.Field);
    public bool IsMoved => OriginFile != null && (!ReferenceEquals(OriginFile, File) || OriginGroup != Group);

    /// <summary>What the library shows: the description, else the first trigger.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label.Trim()
        : Triggers.Count > 0 ? Triggers[0]
        : "New snippet";

    /// <summary>A one-line taste of the text for the library, whitespace collapsed.</summary>
    public string PreviewLine
    {
        get
        {
            var text = string.Join(" ", BodyText.LiteralText(Body).Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
            return text.Length > 80 ? text[..77] + "..." : text;
        }
    }

    /// <summary>Stable text form of everything the editor can change. Used to check a save.</summary>
    public string SemanticSignature() => SemanticSignature(Word);

    /// <summary>The signature with "word" taken as given, to tell "only the word setting changed" apart.</summary>
    internal string SemanticSignature(bool word)
    {
        var sb = new StringBuilder();
        if (IsAdvanced) return "ADV|" + OriginalCanonical + "|" + Group;
        sb.Append(string.Join("\u001f", Triggers)).Append('|');
        sb.Append(Label).Append('|').Append(word).Append('|').Append(PropagateCase).Append('|').Append(ForceMode).Append('|');
        // Plain and Jira are both written as replace: text, so only HTML is told apart; its body is compared in the
        // shape HTML can hold.
        var html = Format == SnippetFormat.Html && !UsesForm;
        sb.Append(Group).Append('|').Append(UsesForm ? "form" : html ? "html" : "replace").Append('|');
        sb.Append(html ? BodyText.Signature(RichText.CanonicalForHtml(Body)) : Body.Signature());
        return sb.ToString();
    }

    public override string ToString() => $"{string.Join(",", Triggers)} ({DisplayName})";
}

internal static class BodyExtensions
{
    public static string Signature(this List<Run> body) => BodyText.Signature(body);
}
