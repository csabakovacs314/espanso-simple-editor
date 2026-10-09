using EspansoEditor.Core.Model;

namespace EspansoEditor.Core.Yaml;

/// <summary>
/// Reads the new file text back and checks it says exactly what the editor intended:
/// every untouched snippet unchanged, every edited snippet as shown on screen, nothing else changed.
/// </summary>
public static class Verifier
{
    /// <param name="reemitted">Untouched snippets the rewrite had to rewrite anyway; checked by meaning instead of byte form.</param>
    public static string? Verify(MatchFile original, string newText, IReadOnlySet<Snippet>? reemitted = null)
    {
        var back = MatchFileParser.ParseText(original.FullPath, original.RelativePath, newText, original.IsPackage);
        if (back.LoadError != null) return $"The new version of {original.RelativePath} could not be read back ({back.LoadError}).";
        if (back.ReadOnlyReason != null && !original.IsPackage) return $"The new version of {original.RelativePath} has an unexpected layout ({back.ReadOnlyReason}).";
        if (back.RootCanonicalWithoutMatches != original.RootCanonicalWithoutMatches)
            return $"Settings outside the snippets in {original.RelativePath} would change. Nothing was saved.";

        var actual = back.Snippets.ToList();
        var removed = new HashSet<Snippet>(original.Removed);
        foreach (var s in original.Snippets)
        {
            var untouched = s.Span != null && ReferenceEquals(s.OriginFile, original) && !removed.Contains(s) && !s.IsDirty
                            && !(reemitted?.Contains(s) ?? false);
            int index;
            if (untouched)
            {
                var canon = s.OriginalCanonical;
                index = actual.FindIndex(a => a.OriginalCanonical == canon && a.Group == s.Group);
                if (index < 0) return $"\"{s.DisplayName}\" would change although it was not edited. Nothing was saved.";
            }
            else
            {
                var expected = s.SemanticSignature();
                index = actual.FindIndex(a => a.SemanticSignature() == expected);
                if (index < 0) return $"\"{s.DisplayName}\" would not be saved exactly as shown. Nothing was saved.";
            }
            actual.RemoveAt(index);
        }
        if (actual.Count > 0) return $"{original.RelativePath} would gain {actual.Count} unexpected snippet(s). Nothing was saved.";
        return null;
    }
}
