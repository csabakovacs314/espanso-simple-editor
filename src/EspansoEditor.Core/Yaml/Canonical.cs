using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EspansoEditor.Core.Yaml;

/// <summary>
/// A layout-independent text form of a YAML node. Two nodes with the same canonical text mean the same thing
/// to Espanso, whatever their indentation or quoting.
/// </summary>
internal static class Canonical
{
    /// <param name="ignoreStyle">Treat quoted and unquoted scalars alike (compares values only).</param>
    public static string Of(YamlNode node, string? skipKey = null, bool ignoreStyle = false)
    {
        var sb = new StringBuilder();
        Write(sb, node, skipKey, ignoreStyle);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, YamlNode node, string? skipKey, bool ignoreStyle)
    {
        switch (node)
        {
            case YamlScalarNode s:
                // Plain scalars can be numbers or booleans; quoted ones are always text.
                sb.Append(ignoreStyle ? "S:" : s.Style is ScalarStyle.Plain or ScalarStyle.Any ? "P:" : "Q:");
                if (!s.Tag.IsEmpty && !s.Tag.IsNonSpecific) sb.Append('!').Append(s.Tag.Value).Append(':');
                sb.Append(Escape(s.Value ?? "~"));
                break;
            case YamlSequenceNode seq:
                sb.Append('[');
                foreach (var c in seq.Children)
                {
                    Write(sb, c, null, ignoreStyle);
                    sb.Append(',');
                }
                sb.Append(']');
                break;
            case YamlMappingNode map:
                sb.Append('{');
                foreach (var (k, v) in map.Children)
                {
                    if (skipKey != null && k is YamlScalarNode { Value: var kv } && kv == skipKey) continue;
                    Write(sb, k, null, ignoreStyle);
                    sb.Append('=');
                    Write(sb, v, null, ignoreStyle);
                    sb.Append(';');
                }
                sb.Append('}');
                break;
            default:
                sb.Append('?');
                break;
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("=", "\\=")
        .Replace("]", "\\]").Replace("}", "\\}");
}
