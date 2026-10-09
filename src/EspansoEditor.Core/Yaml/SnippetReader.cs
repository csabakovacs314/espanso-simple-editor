using System.Text.RegularExpressions;
using EspansoEditor.Core.Model;
using YamlDotNet.RepresentationModel;

namespace EspansoEditor.Core.Yaml;

/// <summary>Turns one Espanso match (a YAML mapping) into a snippet the form can show.</summary>
internal static partial class SnippetReader
{
    private static readonly Dictionary<string, string> Friendly = new()
    {
        ["regex"] = "a regex trigger",
        ["image_path"] = "an image",
        ["markdown"] = "formatted text (Markdown)",
        ["html"] = "formatted text (HTML)",
        ["form_fields"] = "form field settings",
        ["search_terms"] = "search terms",
        ["left_word"] = "word-start matching",
        ["right_word"] = "word-end matching",
        ["uppercase_style"] = "an uppercase style",
        ["paragraph"] = "Markdown paragraph mode",
        ["passive_only"] = "passive mode",
    };

    // \{\{  (escaped braces)  |  {{ name }} or {{name.sub}}  |  $|$
    [GeneratedRegex(@"\\\{\\\{|\\\}\\\}|\{\{\s*(?<name>\w+)(?<dot>\.[\w.]+)?\s*\}\}|\$\|\$")]
    private static partial Regex ReplaceTokens();

    // [[name]]  |  $|$
    [GeneratedRegex(@"\[\[\s*(?<name>\w+)(?<dot>\.[\w.]+)?\s*\]\]|\$\|\$")]
    private static partial Regex FormTokens();

    [GeneratedRegex(@"^\w+$")]
    private static partial Regex VarNameRegex();

    public static Snippet Read(YamlMappingNode node, MatchFile file)
    {
        var s = new Snippet { File = file };
        string? reason = null;
        void Advanced(string why) => reason ??= why;

        string? replace = null, form = null;
        YamlSequenceNode? vars = null;

        foreach (var (k, v) in node.Children)
        {
            if (k is not YamlScalarNode { Value: { } key })
            {
                Advanced("uses an unusual setting");
                continue;
            }
            switch (key)
            {
                case "left_word" or "right_word":
                    if (TryBool(v, out var side) && side)
                    {
                        if (key == "left_word") s.LeftWord = true;
                        else s.RightWord = true;
                    }
                    Advanced("uses " + Friendly[key]);
                    break;
                case "trigger":
                    if (v is YamlScalarNode { Value: { } t }) s.Triggers.Add(t);
                    else Advanced("has an unusual trigger");
                    break;
                case "triggers":
                    if (v is YamlSequenceNode ts && ts.Children.All(c => c is YamlScalarNode { Value: not null }))
                        s.Triggers.AddRange(ts.Children.Cast<YamlScalarNode>().Select(c => c.Value!));
                    else Advanced("has an unusual trigger list");
                    break;
                case "replace":
                    if (v is YamlScalarNode { Value: { } r }) replace = r;
                    else Advanced("has a replacement that is not plain text");
                    break;
                case "form":
                    if (v is YamlScalarNode { Value: { } f }) form = f;
                    else Advanced("has an unusual form");
                    break;
                case "label":
                    if (v is YamlScalarNode { Value: { } l }) s.Label = l;
                    else Advanced("has an unusual description");
                    break;
                case "word":
                    if (TryBool(v, out var w)) s.Word = w;
                    else Advanced("has an unusual 'word' value");
                    break;
                case "propagate_case":
                    if (TryBool(v, out var pc)) s.PropagateCase = pc;
                    else Advanced("has an unusual 'propagate_case' value");
                    break;
                case "force_mode":
                    if (v is YamlScalarNode { Value: "clipboard" or "keys" } fm) s.ForceMode = fm.Value;
                    else Advanced("has an unusual typing method");
                    break;
                case "vars":
                    if (v is YamlSequenceNode vs) vars = vs;
                    else Advanced("has an unusual variable list");
                    break;
                default:
                    Advanced(Friendly.TryGetValue(key, out var f2) ? "uses " + f2 : $"uses the '{key}' setting");
                    break;
            }
        }

        if (replace != null && form != null) Advanced("has both a form and a replacement");
        if (node.Children.ContainsKey(new YamlScalarNode("trigger")) && node.Children.ContainsKey(new YamlScalarNode("triggers")))
            Advanced("has both 'trigger' and 'triggers'");
        if (replace == null && form == null) Advanced("has no plain text to edit");
        if (s.Triggers.Count == 0) Advanced("has no trigger");

        if (reason == null)
        {
            try
            {
                s.Body = form != null
                    ? ReadForm(form, vars)
                    : ReadReplace(replace!, vars);
            }
            catch (AdvancedException ex)
            {
                Advanced(ex.Message);
            }
        }

        if (reason != null)
        {
            s.AdvancedReason = reason;
            // Show whatever text there is, read-only.
            s.Body = new List<Run> { Run.Literal(replace ?? form ?? "") };
        }
        return s;
    }

    private static bool TryBool(YamlNode v, out bool value)
    {
        value = false;
        if (v is not YamlScalarNode { Value: { } t }) return false;
        if (t is "true" or "True" or "TRUE") { value = true; return true; }
        if (t is "false" or "False" or "FALSE") { value = false; return true; }
        return false;
    }

    private static List<Run> ReadForm(string form, YamlSequenceNode? vars)
    {
        if (vars is { Children.Count: > 0 }) throw new AdvancedException("mixes a form with variables");
        if (form.Contains("{{")) throw new AdvancedException("mixes a form with variables");
        var runs = new List<Run>();
        var pos = 0;
        foreach (Match m in FormTokens().Matches(form))
        {
            if (m.Index > pos) runs.Add(Run.Literal(form[pos..m.Index]));
            if (m.Value == "$|$") runs.Add(Run.Cursor());
            else if (m.Groups["dot"].Success) throw new AdvancedException("uses form fields with sub-values");
            else runs.Add(Run.Field(m.Groups["name"].Value));
            pos = m.Index + m.Length;
        }
        if (pos < form.Length) runs.Add(Run.Literal(form[pos..]));
        if (!runs.Any(r => r.Kind == RunKind.Field)) throw new AdvancedException("is a form without fields");
        return BodyText.Normalize(runs);
    }

    private static List<Run> ReadReplace(string replace, YamlSequenceNode? varsNode)
    {
        var locals = new Dictionary<string, Run>();
        if (varsNode != null)
        {
            foreach (var item in varsNode.Children)
            {
                if (item is not YamlMappingNode vm) throw new AdvancedException("has an unusual variable");
                var spec = ReadVar(vm);
                if (!locals.TryAdd(spec.VarName!, spec)) throw new AdvancedException("defines the same variable twice");
            }
        }

        var used = new HashSet<string>();
        var runs = new List<Run>();
        var pos = 0;
        foreach (Match m in ReplaceTokens().Matches(replace))
        {
            if (m.Index > pos) runs.Add(Run.Literal(replace[pos..m.Index]));
            if (m.Value == "$|$")
            {
                runs.Add(Run.Cursor());
            }
            else if (m.Value == "\\{\\{")
            {
                runs.Add(Run.Literal("{{"));
            }
            else if (m.Value == "\\}\\}")
            {
                runs.Add(Run.Literal("}}"));
            }
            else
            {
                if (m.Groups["dot"].Success) throw new AdvancedException("uses variables with sub-values");
                var name = m.Groups["name"].Value;
                if (locals.TryGetValue(name, out var spec))
                {
                    runs.Add(spec);
                    used.Add(name);
                }
                else
                {
                    runs.Add(Run.Global(name));
                }
            }
            pos = m.Index + m.Length;
        }
        if (pos < replace.Length) runs.Add(Run.Literal(replace[pos..]));

        if (locals.Keys.Any(k => !used.Contains(k))) throw new AdvancedException("has a variable that the text does not use");
        return BodyText.Normalize(runs);
    }

    private static Run ReadVar(YamlMappingNode vm)
    {
        var keys = vm.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").ToList();
        if (keys.Count != vm.Children.Count || keys.Any(k => k is not ("name" or "type" or "params")))
            throw new AdvancedException("uses variable options this editor does not cover");

        var name = MatchFileParser.Scalar(vm, "name");
        var type = MatchFileParser.Scalar(vm, "type");
        if (name == null || type == null || !VarNameRegex().IsMatch(name)) throw new AdvancedException("has an unusual variable");

        YamlMappingNode? p = null;
        if (vm.Children.TryGetValue(new YamlScalarNode("params"), out var pn))
        {
            if (pn is YamlMappingNode pmap) p = pmap;
            else if (!(pn is YamlScalarNode { Value: null or "" })) throw new AdvancedException("has unusual variable settings");
        }
        var pkeys = p?.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").ToHashSet() ?? new HashSet<string>();
        if (p != null && pkeys.Count != p.Children.Count) throw new AdvancedException("has unusual variable settings");

        string? Param(string key)
        {
            if (p == null || !p.Children.TryGetValue(new YamlScalarNode(key), out var v)) return null;
            if (v is not YamlScalarNode { Value: { } sv }) throw new AdvancedException("has unusual variable settings");
            if (sv.Contains("{{")) throw new AdvancedException("puts variables inside other variables");
            return sv;
        }

        switch (type)
        {
            case "date":
                if (!pkeys.SetEquals(new[] { "format" })) throw new AdvancedException("uses date options this editor does not cover (offset, locale or time zone)");
                return Run.Date(Param("format")!, name);
            case "clipboard":
                if (pkeys.Count > 0) throw new AdvancedException("uses clipboard options this editor does not cover");
                return Run.Clipboard(name);
            case "shell":
                if (!pkeys.Contains("cmd") || pkeys.Any(k => k is not ("cmd" or "shell")))
                    throw new AdvancedException("uses command options this editor does not cover");
                return Run.Shell(Param("cmd")!, Param("shell"), name);
            case "random":
                if (!pkeys.SetEquals(new[] { "choices" })) throw new AdvancedException("uses random options this editor does not cover");
                var choices = p!.Children[new YamlScalarNode("choices")];
                if (choices is not YamlSequenceNode cs || !cs.Children.All(c => c is YamlScalarNode { Value: not null }))
                    throw new AdvancedException("has unusual random choices");
                var list = cs.Children.Cast<YamlScalarNode>().Select(c => c.Value!).ToList();
                if (list.Any(c => c.Contains("{{"))) throw new AdvancedException("puts variables inside other variables");
                return Run.Random(list, name);
            default:
                throw new AdvancedException($"uses a '{type}' variable");
        }
    }

    private sealed class AdvancedException(string message) : Exception(message);
}
