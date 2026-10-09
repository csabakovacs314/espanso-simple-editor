using System.Text.RegularExpressions;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Text;

namespace EspansoEditor.Core.Validation;

public enum Severity { Warning, Error }

public sealed record Issue(Snippet Snippet, Severity Severity, string Message);

/// <summary>Checks snippets before saving. Errors block the save; warnings never do.</summary>
public static partial class Validator
{
    [GeneratedRegex(@"^\w+$")]
    private static partial Regex NameRegex();

    public static List<Issue> Check(EspansoConfig cfg) => cfg.AllSnippets.SelectMany(s => Check(s, cfg)).ToList();

    /// <param name="clashWarnings">Include the "blocks a longer trigger" warnings. They never block a save, and with
    /// thousands of snippets they are the slow part, so the save leaves them out.</param>
    public static IEnumerable<Issue> Check(Snippet s, EspansoConfig cfg, bool clashWarnings = true)
    {
        if (s.IsReadOnly) yield break;

        if (s.Triggers.Count == 0) yield return new Issue(s, Severity.Error, "Add at least one trigger.");
        foreach (var t in s.Triggers)
        {
            if (string.IsNullOrWhiteSpace(t)) yield return new Issue(s, Severity.Error, "A trigger is empty.");
            else if (t.Contains('\n')) yield return new Issue(s, Severity.Error, $"Trigger \"{t.Trim()}\" contains a line break.");
        }
        if (s.Triggers.Count != s.Triggers.Distinct().Count()) yield return new Issue(s, Severity.Warning, "The same trigger is listed twice.");

        foreach (var t in s.Triggers.Distinct())
        {
            var other = cfg.AllSnippets.FirstOrDefault(o => !ReferenceEquals(o, s) && o.Triggers.Contains(t));
            if (other != null)
                yield return new Issue(s, Severity.Warning, $"\"{t}\" is also the trigger of \"{other.DisplayName}\" in {other.File.RelativePath}.");
        }

        if (clashWarnings)
            foreach (var issue in ClashWarnings(s, cfg)) yield return issue;

        var body = BodyText.Normalize(s.Body);
        if (body.Count == 0) yield return new Issue(s, Severity.Warning, "The text is empty.");
        if (body.Count(r => r.Kind == RunKind.Cursor) > 1) yield return new Issue(s, Severity.Error, "Only one cursor position is allowed.");

        var literal = BodyText.LiteralText(body);
        if (literal.Contains("$|$")) yield return new Issue(s, Severity.Error, "The text contains \"$|$\". Use Insert > Cursor position instead.");

        var form = s.UsesForm;
        if (form && s.Format == SnippetFormat.Html)
            yield return new Issue(s, Severity.Error, "HTML snippets cannot ask when typing. Use Plain text or Jira Cloud format, or remove the ask-when-typing fields.");
        if (s.Format == SnippetFormat.Html && s.Body.Any(r => r.Kind == RunKind.Cursor))
            yield return new Issue(s, Severity.Warning, "The cursor position may land in the wrong place in an HTML snippet. Test it once, or use Jira Cloud format.");
        if (s.Format == SnippetFormat.Html && s.ForceMode == "keys")
            yield return new Issue(s, Severity.Warning, "HTML is pasted as formatted text; \"Type key by key\" does not apply to it.");
        if (form)
        {
            if (literal.Contains("[[")) yield return new Issue(s, Severity.Error, "The text contains \"[[\", which clashes with the ask-when-typing fields.");
            if (body.Any(r => r.Kind is RunKind.Date or RunKind.Clipboard or RunKind.Shell or RunKind.Random or RunKind.GlobalVar))
                yield return new Issue(s, Severity.Error, "Ask-when-typing fields cannot be mixed with date, clipboard, command, random or variable chips.");
        }

        foreach (var r in body)
        {
            switch (r.Kind)
            {
                case RunKind.Field when !NameRegex().IsMatch(r.Text):
                    yield return new Issue(s, Severity.Error, $"Field name \"{r.Text}\" may only use letters, digits and _.");
                    break;
                case RunKind.GlobalVar when !NameRegex().IsMatch(r.Text):
                    yield return new Issue(s, Severity.Error, $"Variable name \"{r.Text}\" may only use letters, digits and _.");
                    break;
                case RunKind.Date when string.IsNullOrWhiteSpace(r.Format):
                    yield return new Issue(s, Severity.Error, "A date chip has no format.");
                    break;
                case RunKind.Shell when string.IsNullOrWhiteSpace(r.Command):
                    yield return new Issue(s, Severity.Error, "A PowerShell chip has no command.");
                    break;
                case RunKind.Random when r.Choices.Count < 2:
                    yield return new Issue(s, Severity.Error, "A random chip needs at least two choices.");
                    break;
                case RunKind.Date or RunKind.Shell or RunKind.Random when HasVarSyntax(r):
                    yield return new Issue(s, Severity.Error, "Chip settings cannot contain {{ }}.");
                    break;
            }
        }
    }

    /// <summary>Warnings for triggers that block, or are blocked by, another trigger.</summary>
    private static IEnumerable<Issue> ClashWarnings(Snippet s, EspansoConfig cfg)
    {
        var clashes = TriggerClashes.For(s, cfg.AllSnippets);
        foreach (var g in clashes.Where(c => ReferenceEquals(c.Blocker, s)).GroupBy(c => c.Short))
        {
            var longer = string.Join(", ", g.Select(c => $"\"{c.Long}\"").Distinct());
            var fix = s.Word
                ? "Rename one of them."
                : "Tick \"Only as a whole word\" here to fix it.";
            yield return new Issue(s, Severity.Warning, $"\"{g.Key}\" fires before {longer} can be typed. {fix}");
        }
        foreach (var c in clashes.Where(c => ReferenceEquals(c.Blocked, s)))
        {
            var fix = c.Blocker.IsReadOnly || c.Blocker.Word
                ? "Rename this trigger."
                : $"Tick \"Only as a whole word\" on \"{c.Blocker.DisplayName}\", or rename one of them.";
            yield return new Issue(s, Severity.Warning, $"\"{c.Long}\" can never be typed: \"{c.Short}\" ({c.Blocker.DisplayName}) fires first. {fix}");
        }
    }

    private static bool HasVarSyntax(Run r) =>
        (r.Format?.Contains("{{") ?? false) || (r.Command?.Contains("{{") ?? false) || r.Choices.Any(c => c.Contains("{{"));
}
