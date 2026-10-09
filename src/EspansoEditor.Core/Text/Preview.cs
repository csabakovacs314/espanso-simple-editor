using EspansoEditor.Core.Model;

namespace EspansoEditor.Core.Text;

public enum PreviewPart { Text, Value, Placeholder, Cursor }

public sealed record PreviewSegment(string Text, PreviewPart Part);

/// <summary>What Espanso will type, with example values, and the labels shown on chips.</summary>
public static class Preview
{
    public static List<PreviewSegment> Render(IEnumerable<Run> body, IReadOnlyDictionary<string, string> globalEcho, DateTime now)
    {
        var list = new List<PreviewSegment>();
        foreach (var r in BodyText.Normalize(body))
        {
            list.Add(r.Kind switch
            {
                RunKind.Text => new PreviewSegment(r.Text, PreviewPart.Text),
                RunKind.Date => new PreviewSegment(StrfTime.Format(r.Format ?? "", now), PreviewPart.Value),
                RunKind.Clipboard => new PreviewSegment("(what you copied)", PreviewPart.Placeholder),
                RunKind.Shell => new PreviewSegment("(command output)", PreviewPart.Placeholder),
                RunKind.Random => new PreviewSegment("(" + string.Join(" / ", r.Choices) + ")", PreviewPart.Placeholder),
                RunKind.GlobalVar => globalEcho.TryGetValue(r.Text, out var v)
                    ? new PreviewSegment(v, PreviewPart.Value)
                    : new PreviewSegment("(" + r.Text + ")", PreviewPart.Placeholder),
                RunKind.Field => new PreviewSegment("[" + r.Text + "]", PreviewPart.Placeholder),
                RunKind.Cursor => new PreviewSegment("|", PreviewPart.Cursor),
                _ => new PreviewSegment("", PreviewPart.Text),
            });
        }
        return list;
    }

    /// <summary>Short kind label shown on a chip, e.g. "DATE".</summary>
    public static string ChipKind(Run r) => r.Kind switch
    {
        RunKind.Date => r.Format is "%H:%M" or "%H:%M:%S" or "%I:%M %p" ? "TIME" : "DATE",
        RunKind.Clipboard => "CLIPBOARD",
        RunKind.Shell => "POWERSHELL",
        RunKind.Random => "RANDOM",
        RunKind.GlobalVar => "VARIABLE",
        RunKind.Field => "ASK",
        RunKind.Cursor => "CURSOR",
        _ => "",
    };

    /// <summary>Value part of a chip, e.g. "09/10/2026" or the command.</summary>
    public static string ChipValue(Run r, IReadOnlyDictionary<string, string> globalEcho, DateTime now) => r.Kind switch
    {
        RunKind.Date => StrfTime.Format(r.Format ?? "", now),
        RunKind.Clipboard => "",
        RunKind.Shell => Shorten(r.Command ?? "", 40),
        RunKind.Random => Shorten(string.Join(" / ", r.Choices), 40),
        RunKind.GlobalVar => globalEcho.TryGetValue(r.Text, out var v) ? $"{r.Text} · {Shorten(v, 30)}" : r.Text,
        RunKind.Field => r.Text,
        _ => "",
    };

    private static string Shorten(string s, int max)
    {
        s = s.Replace('\n', ' ');
        return s.Length > max ? s[..(max - 3)] + "..." : s;
    }
}
