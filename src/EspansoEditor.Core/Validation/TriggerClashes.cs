using EspansoEditor.Core.Model;

namespace EspansoEditor.Core.Validation;

/// <summary>
/// One trigger that fires while a longer one is still being typed, so the longer one can never be used.
/// Example: "/jira" fires as soon as you type it, before "/jirauao" is complete.
/// </summary>
public sealed record TriggerClash(Snippet Blocker, string Short, Snippet Blocked, string Long);

/// <summary>
/// Finds triggers that block other triggers. Espanso expands the moment the last typed characters match a trigger,
/// so a trigger X blocks Y when X appears inside Y and ends before Y does. A whole-word X (word: true) only fires
/// between word separators, so it blocks Y only where Y itself has separators around X.
/// </summary>
public static class TriggerClashes
{
    // Espanso's default word separators.
    private const string Separators = " ,.?!\r\n\t";

    private static bool IsSeparator(char c) => Separators.Contains(c);

    public static List<TriggerClash> Find(IEnumerable<Snippet> snippets)
    {
        var all = snippets.ToList();
        // Every text that makes a snippet fire: its triggers, plus the capitalised and upper-case forms
        // when it follows the case you type (propagate_case).
        var byTrigger = new Dictionary<string, List<(Snippet Snippet, string Trigger)>>(StringComparer.Ordinal);
        foreach (var s in all)
        {
            foreach (var t in s.Triggers.Distinct())
            {
                if (string.IsNullOrEmpty(t)) continue;
                foreach (var form in Forms(t, s.PropagateCase))
                {
                    if (!byTrigger.TryGetValue(form, out var list)) byTrigger[form] = list = new();
                    if (!list.Contains((s, t))) list.Add((s, t));
                }
            }
        }
        var lookup = byTrigger.GetAlternateLookup<ReadOnlySpan<char>>();

        var result = new List<TriggerClash>();
        var seen = new HashSet<(Snippet, string, Snippet, string)>();
        // Note: one snippet can block itself, e.g. triggers [":br", ":brx"]: ":brx" can never be typed.
        foreach (var y in all)
        {
            foreach (var longer in y.Triggers.Distinct())
            {
                if (string.IsNullOrEmpty(longer)) continue;
                var span = longer.AsSpan();
                // Every piece of the trigger that ends before its last character.
                for (var start = 0; start < span.Length - 1; start++)
                {
                    for (var end = start + 1; end < span.Length; end++)
                    {
                        if (!lookup.TryGetValue(span[start..end], out var blockers)) continue;
                        foreach (var (x, trigger) in blockers)
                        {
                            // A whole-word trigger needs separators on both sides; left_word / right_word on one side.
                            var needLeft = x.Word || x.LeftWord;
                            var needRight = x.Word || x.RightWord;
                            var fires = (!needLeft || start == 0 || IsSeparator(span[start - 1]))
                                        && (!needRight || IsSeparator(span[end]));
                            if (!fires) continue;
                            if (seen.Add((x, trigger, y, longer))) result.Add(new TriggerClash(x, trigger, y, longer));
                        }
                    }
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> Forms(string trigger, bool propagateCase)
    {
        yield return trigger;
        if (!propagateCase) yield break;
        var i = trigger.ToList().FindIndex(char.IsLetter);
        if (i >= 0) yield return trigger[..i] + char.ToUpperInvariant(trigger[i]) + trigger[(i + 1)..];
        yield return trigger.ToUpperInvariant();
    }

    /// <summary>Clashes this snippet is part of, as the blocker or the blocked one.</summary>
    public static List<TriggerClash> For(Snippet s, IEnumerable<Snippet> snippets) =>
        Find(snippets).Where(c => ReferenceEquals(c.Blocker, s) || ReferenceEquals(c.Blocked, s)).ToList();

    /// <summary>
    /// The editable snippets that should be whole word so every longer trigger can be typed: the blockers that are
    /// not whole word yet.
    /// </summary>
    public static List<Snippet> FixableBlockers(IEnumerable<Snippet> snippets) =>
        Find(snippets).Select(c => c.Blocker).Where(b => !b.Word && !b.IsReadOnly).Distinct().ToList();

    /// <summary>
    /// Like <see cref="FixableBlockers"/>, but only for clashes this snippet is part of: the snippet itself when its
    /// trigger blocks a longer one, or the other snippet when that one blocks this snippet's trigger.
    /// </summary>
    public static List<Snippet> FixableBlockersFor(Snippet s, IEnumerable<Snippet> snippets) =>
        For(s, snippets).Select(c => c.Blocker).Where(b => !b.Word && !b.IsReadOnly).Distinct().ToList();
}
