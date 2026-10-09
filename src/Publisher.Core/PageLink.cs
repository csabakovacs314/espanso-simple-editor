using System.Text.RegularExpressions;

namespace Publisher.Core;

public static partial class PageLink
{
    [GeneratedRegex(@"/pages/(?:edit-v2/|viewpage\.action/)?(\d+)")] private static partial Regex PagesPath();
    [GeneratedRegex(@"[?&]pageId=(\d+)")] private static partial Regex PageIdQuery();
    [GeneratedRegex(@"^\d+$")] private static partial Regex Digits();

    /// <summary>
    /// The page ID in a Confluence page link (…/wiki/spaces/KEY/pages/123456/Title, …?pageId=123456) or a bare ID.
    /// Null when there is none.
    /// </summary>
    public static string? IdFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (Digits().IsMatch(text)) return text;
        var m = PagesPath().Match(text);
        if (m.Success) return m.Groups[1].Value;
        m = PageIdQuery().Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>"https://example.atlassian.net" from what the user typed (with or without https://, /wiki, a page path).</summary>
    public static Uri? Site(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host)) return null;
        return new Uri($"https://{u.Authority}");
    }
}
