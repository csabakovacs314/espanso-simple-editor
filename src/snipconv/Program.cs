using EspansoEditor.Core.Model;
using EspansoEditor.Core.Yaml;

// snipconv: read-only checker for the editor's conversion.
//   snipconv check <espanso folder | match file>        report what the editor sees and test a full re-save in memory
//   snipconv resave <match file> <output file>           write the "every snippet re-saved" version elsewhere, for comparing

if (args.Length < 2 || args[0] is not ("check" or "resave"))
{
    Console.WriteLine("""
        snipconv: checks how Espanso Simple Editor reads and re-saves your snippets. It never changes your files.

          snipconv check  <espanso folder or .yml file>
          snipconv resave <.yml file> <output .yml file>
        """);
    return 2;
}

var target = args[1];
var files = new List<MatchFile>();
IReadOnlySet<string> globals;
if (Directory.Exists(target))
{
    var cfg = EspansoConfig.Load(target);
    files.AddRange(cfg.Files);
    globals = cfg.GlobalVarNames;
    Console.WriteLine($"Config: {cfg.ConfigDir}");
    Console.WriteLine($"Match folder: {cfg.MatchDir}");
}
else if (File.Exists(target))
{
    var f = MatchFileParser.Parse(Path.GetFullPath(target), Path.GetFileName(target), File.ReadAllBytes(target), false);
    files.Add(f);
    globals = f.GlobalVarNames;
}
else
{
    Console.Error.WriteLine($"Not found: {target}");
    return 2;
}

var failures = 0;
foreach (var f in files)
{
    Console.WriteLine();
    Console.WriteLine($"== {f.RelativePath}  ({f.Newline switch { "\r\n" => "CRLF", _ => "LF" }}{(f.HasBom ? ", BOM" : "")})");
    if (f.ReadOnlyReason != null) Console.WriteLine($"   read-only: {f.ReadOnlyReason}");
    var editable = f.Snippets.Count(s => !s.IsAdvanced);
    Console.WriteLine($"   {f.Snippets.Count} snippets: {editable} editable, {f.Snippets.Count - editable} advanced (kept as they are)");
    foreach (var g in f.Snippets.GroupBy(s => s.Group))
    {
        Console.WriteLine($"   [{(g.Key.Length == 0 ? "no group" : g.Key)}]");
        foreach (var s in g)
        {
            var chips = s.Body.Where(r => r.IsChip).Select(r => r.Kind.ToString().ToLowerInvariant()).ToList();
            var extra = s.IsAdvanced ? $"  ADVANCED: {s.AdvancedReason}" : chips.Count > 0 ? "  chips: " + string.Join(", ", chips) : "";
            Console.WriteLine($"     {string.Join(" ", s.Triggers),-14} {Trim(s.PreviewLine, 50)}{extra}");
        }
    }
    if (f.IsReadOnly) continue;

    // Re-save every editable snippet in memory and check it reads back identically.
    foreach (var s in f.Snippets.Where(s => !s.IsAdvanced)) s.IsDirty = true;
    string newText;
    try
    {
        newText = FileRewriter.Rewrite(f, globals);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   RE-SAVE FAILED: {ex.Message}");
        failures++;
        continue;
    }
    var problem = Verifier.Verify(f, newText);
    var oldLines = f.Text.Split('\n');
    var newLines = newText.Split('\n');
    var changed = CountChangedLines(oldLines, newLines);
    Console.WriteLine(problem == null
        ? $"   Re-save test: PASS. Every snippet reads back identically; {changed} of {oldLines.Length} lines differ in bytes."
        : $"   Re-save test: FAIL. {problem}");
    if (problem != null) failures++;
    if (changed > 0) ShowDiff(oldLines, newLines, 12);

    if (args[0] == "resave" && args.Length >= 3)
    {
        File.WriteAllText(args[2], f.Newline == "\n" ? newText : newText.Replace("\n", f.Newline));
        Console.WriteLine($"   Written to {args[2]}");
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All files passed." : $"{failures} file(s) failed.");
return failures == 0 ? 0 : 1;

static string Trim(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s.PadRight(max);

static int CountChangedLines(string[] a, string[] b)
{
    var lcs = Lcs(a, b);
    return Math.Max(a.Length, b.Length) - lcs;
}

static int Lcs(string[] a, string[] b)
{
    var dp = new int[a.Length + 1, b.Length + 1];
    for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
            dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
    return dp[0, 0];
}

static void ShowDiff(string[] a, string[] b, int max)
{
    int i = 0, j = 0, shown = 0;
    while ((i < a.Length || j < b.Length) && shown < max)
    {
        if (i < a.Length && j < b.Length && a[i] == b[j]) { i++; j++; continue; }
        if (i < a.Length) { Console.WriteLine($"     - {i + 1,4}: {Visible(a[i])}"); i++; shown++; }
        if (j < b.Length) { Console.WriteLine($"     + {j + 1,4}: {Visible(b[j])}"); j++; shown++; }
    }
}

static string Visible(string s) => s.Replace("\t", "→") + (s.EndsWith(' ') ? "·(trailing space)" : "");
