using System.Text;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Saving;
using EspansoEditor.Core.Text;
using EspansoEditor.Core.Validation;
using EspansoEditor.Core.Yaml;
using Xunit;

namespace EspansoEditor.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ese-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlySet<string> NoGlobals = new HashSet<string>();

    public CoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    private static MatchFile Parse(string text, string name = "base.yml") => MatchFileParser.ParseText("/x/" + name, name, text, false);

    private EspansoConfig MakeConfig(params (string Name, string Text)[] files)
    {
        var match = Path.Combine(_root, "espanso", "match");
        Directory.CreateDirectory(match);
        foreach (var (name, text) in files)
        {
            var p = Path.Combine(match, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text, new UTF8Encoding(false));
        }
        return EspansoConfig.Load(Path.Combine(_root, "espanso"));
    }

    private SaveOptions Options(bool overwrite = false) => new() { BackupRoot = Path.Combine(_root, "backups"), OverwriteChangedFiles = overwrite };

    // ---------- reading ----------

    [Fact]
    public void Reads_groups_and_snippets_from_hand_written_file()
    {
        var f = Parse(Fixtures.HandWritten);
        Assert.Null(f.ReadOnlyReason);
        Assert.Equal(6, f.Snippets.Count);
        Assert.All(f.Snippets, s => Assert.False(s.IsAdvanced));
        Assert.Equal(new[] { "Tickets", "Templates", "Signatures" }, f.Groups.Select(g => g.Name));
        Assert.Equal("", f.Snippets[0].Group);
        Assert.Equal("Tickets", f.Snippets.Single(s => s.Triggers[0] == "/co").Group);
        Assert.Equal("---", f.MarkerDelimiter);

        var date = f.Snippets.Single(s => s.Triggers[0] == ":date");
        var chip = Assert.Single(date.Body);
        Assert.Equal(RunKind.Date, chip.Kind);
        Assert.Equal("%m/%d/%Y", chip.Format);
        Assert.Equal("mydate", chip.VarName);

        var ack = f.Snippets.Single(s => s.Triggers[0] == "/ack");
        Assert.Equal("Hello,\n\nThanks for the ticket. \n\nBest,\nSupport Team  \n", Assert.Single(ack.Body).Text);
    }

    [Fact]
    public void Advanced_snippets_are_flagged_and_kept()
    {
        var f = Parse(Fixtures.Advanced);
        Assert.False(f.Snippets.Single(s => s.Triggers.Contains(":sig")).IsAdvanced);
        Assert.Equal(RunKind.GlobalVar, f.Snippets.Single(s => s.Triggers.Contains(":sig")).Body[1].Kind);
        Assert.True(f.Snippets.Single(s => s.Triggers.Contains(":cat")).IsAdvanced);
        Assert.True(f.Snippets.Single(s => s.Triggers.Count == 0).IsAdvanced); // regex
        Assert.True(f.Snippets.Single(s => s.Triggers.Contains(":pick")).IsAdvanced);
        Assert.Contains("offset", f.Snippets.Single(s => s.Triggers.Contains(":yday")).AdvancedReason);
        Assert.True(f.Snippets.Single(s => s.Triggers.Contains(":md")).IsAdvanced);
        Assert.Equal("Jane Doe", f.GlobalEcho["myname"]);

        // Editing the simple one leaves every advanced one byte-identical.
        var sig = f.Snippets.Single(s => s.Triggers.Contains(":sig"));
        sig.Label = "Signature";
        sig.IsDirty = true;
        var text = FileRewriter.Rewrite(f, f.GlobalVarNames);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Contains("  - trigger: \":cat\"\n    image_path: \"$CONFIG/images/cat.png\"\n", text);
        Assert.Contains("imports:\n  - \"../shared/other.yml\"\n", text);
    }

    [Theory]
    [InlineData(Fixtures.HandWritten)]
    [InlineData(Fixtures.EspansoDefault)]
    [InlineData(Fixtures.Advanced)]
    public void Resaving_every_snippet_reads_back_identically(string fixture)
    {
        var f = Parse(fixture);
        foreach (var s in f.Snippets.Where(s => !s.IsAdvanced)) s.IsDirty = true;
        var text = FileRewriter.Rewrite(f, f.GlobalVarNames);
        Assert.Null(Verifier.Verify(f, text));
    }

    [Fact]
    public void Resaving_Espansos_default_file_changes_no_bytes()
    {
        var f = Parse(Fixtures.EspansoDefault);
        foreach (var s in f.Snippets) s.IsDirty = true;
        Assert.Equal(Fixtures.EspansoDefault, FileRewriter.Rewrite(f, NoGlobals));
    }

    [Fact]
    public void Editing_one_trigger_changes_one_line()
    {
        var f = Parse(Fixtures.HandWritten);
        var co = f.Snippets.Single(s => s.Triggers[0] == "/co");
        co.Triggers[0] = "/done";
        co.IsDirty = true;
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Equal(Fixtures.HandWritten.Replace("\"/co\"", "\"/done\""), text);
    }

    [Fact]
    public void Editing_text_keeps_comments_and_neighbours()
    {
        var f = Parse(Fixtures.HandWritten);
        var ack = f.Snippets.Single(s => s.Triggers[0] == "/ack");
        ack.Body = new List<Run> { Run.Literal("Hello,\n\nThanks, on it now.\n") };
        ack.IsDirty = true;
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Contains("  # --- Tickets ---\n  - trigger: \"/ack\"\n    replace: |\n      Hello,\n\n      Thanks, on it now.\n\n  - trigger: \"/co\"", text);
        Assert.StartsWith("# espanso match file\n# yaml-language-server", text);
    }

    // ---------- adding, deleting, moving ----------

    [Fact]
    public void New_snippet_goes_to_the_end_of_its_group()
    {
        var cfg = MakeConfig(("base.yml", Fixtures.HandWritten));
        var f = cfg.Files[0];
        var s = cfg.NewSnippet(f, "Tickets");
        s.Triggers.Add("/new");
        s.Body = new List<Run> { Run.Literal("Brand new.") };
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Contains("    replace: \"The requested change is done.\"\n\n  - trigger: \"/new\"\n    replace: \"Brand new.\"\n\n  # --- Templates ---", text);
    }

    [Fact]
    public void New_group_gets_a_marker_in_the_users_style()
    {
        var cfg = MakeConfig(("base.yml", Fixtures.EspansoDefault + "  # --- Mine ---\n"));
        var f = cfg.Files[0];
        var s = cfg.NewSnippet(f, "Brand New Group");
        s.Triggers.Add(":bn");
        s.Body = new List<Run> { Run.Literal("x") };
        var text = FileRewriter.Rewrite(f, NoGlobals, cfg.GroupDelimiter);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Contains("  # --- Brand New Group ---\n  - trigger: \":bn\"\n    replace: \"x\"\n", text);
    }

    [Fact]
    public void Deleting_removes_only_that_snippet()
    {
        var cfg = MakeConfig(("base.yml", Fixtures.HandWritten));
        var f = cfg.Files[0];
        cfg.Delete(f.Snippets.Single(s => s.Triggers[0] == "/co"));
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        Assert.DoesNotContain("/co\"", text);
        Assert.Contains("      Support Team  \n\n  # --- Templates ---", text);
    }

    [Fact]
    public void Moving_between_groups_and_files_saves_both_files()
    {
        var cfg = MakeConfig(("base.yml", Fixtures.HandWritten), ("work.yml", "matches:\n  - trigger: \":w\"\n    replace: \"w\"\n"));
        var basef = cfg.Files.Single(f => f.RelativePath == "base.yml");
        var work = cfg.Files.Single(f => f.RelativePath == "work.yml");

        cfg.Move(basef.Snippets.Single(s => s.Triggers[0] == "/co"), basef, "Signatures");
        cfg.Move(basef.Snippets.Single(s => s.Triggers[0] == "/tpl"), work, "");

        var result = SaveService.Save(cfg, Options());
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(2, result.SavedFiles.Count);

        var reloaded = EspansoConfig.Load(Path.Combine(_root, "espanso"));
        var rb = reloaded.Files.Single(f => f.RelativePath == "base.yml");
        var rw = reloaded.Files.Single(f => f.RelativePath == "work.yml");
        Assert.Equal("Signatures", rb.Snippets.Single(s => s.Triggers[0] == "/co").Group);
        Assert.DoesNotContain(rb.Snippets, s => s.Triggers[0] == "/tpl");
        Assert.Contains(rw.Snippets, s => s.Triggers[0] == "/tpl");
        Assert.True(Directory.Exists(result.BackupFolder));
        Assert.True(File.Exists(Path.Combine(result.BackupFolder!, "base.yml")));

        // The editor's own objects are now clean and point at the new positions: a second save is a no-op.
        Assert.False(cfg.HasUnsavedChanges);
        var again = SaveService.Save(cfg, Options());
        Assert.True(again.Success);
        Assert.Empty(again.SavedFiles);
    }

    [Fact]
    public void Snippets_can_be_added_to_a_file_without_matches()
    {
        var cfg = MakeConfig(("empty.yml", "# nothing here yet\n"));
        var f = cfg.Files[0];
        var s = cfg.NewSnippet(f, "");
        s.Triggers.Add(":first");
        s.Body = new List<Run> { Run.Literal("First!") };
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Equal("# nothing here yet\n\nmatches:\n  - trigger: \":first\"\n    replace: \"First!\"\n", text);
    }

    // ---------- chips and text ----------

    [Fact]
    public void Chips_become_variables_and_read_back()
    {
        var f = Parse("matches:\n  - trigger: \":a\"\n    replace: \"a\"\n");
        var s = f.Snippets[0];
        s.Body = new List<Run>
        {
            Run.Literal("Today "), Run.Date("%d/%m/%Y"), Run.Literal(" at "), Run.Date("%H:%M"),
            Run.Literal(", copied: "), Run.Clipboard(), Run.Literal(", year "), Run.Shell("Get-Date -Format yyyy", "powershell"),
            Run.Literal(", "), Run.Random(new[] { "Sure!", "Will do." }), Run.Cursor(), Run.Literal("\n"),
        };
        s.IsDirty = true;
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        var back = Parse(text).Snippets[0];
        Assert.False(back.IsAdvanced);
        Assert.Equal(new[] { "date", "time", "clipboard", "output", "choice" }, back.Body.Where(r => r.VarName != null).Select(r => r.VarName));
        Assert.Contains("replace: |\n      Today {{date}} at {{time}}, copied: {{clipboard}}, year {{output}}, {{choice}}$|$\n", text);
    }

    [Fact]
    public void Ask_fields_make_a_form()
    {
        var f = Parse("matches:\n  - trigger: \":a\"\n    replace: \"a\"\n");
        var s = f.Snippets[0];
        s.Body = new List<Run> { Run.Literal("Hi "), Run.Field("name"), Run.Literal(",\nsee you on "), Run.Field("day") };
        s.IsDirty = true;
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Contains("    form: |-\n      Hi [[name]],\n      see you on [[day]]\n", text);
        Assert.True(Parse(text).Snippets[0].UsesForm);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("quote \" and backslash \\ and {{braces}} and [[not a field]]")]
    [InlineData("  leading spaces\nsecond")]
    [InlineData("line\n")]
    [InlineData("two trailing\n\n")]
    [InlineData("\nstarts with a blank line\n")]
    [InlineData("tab\there\n\tand at line start")]
    [InlineData("unicode: árvíztűrő tükörfúrógép, 日本語, emoji 🙂\n")]
    [InlineData("# looks like a comment\n--- looks like a document marker\n...\n")]
    [InlineData("trailing spaces   \n   \nindented after blank")]
    [InlineData("")]
    public void Any_text_survives_a_round_trip(string value)
    {
        var f = Parse("matches:\n  - trigger: \":a\"\n    replace: \"a\"\n");
        var s = f.Snippets[0];
        s.Body = value.Length == 0 ? new List<Run>() : new List<Run> { Run.Literal(value) };
        s.IsDirty = true;
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Equal(value, BodyText.LiteralText(Parse(text).Snippets[0].Body));
    }

    // ---------- checks and saving ----------

    [Fact]
    public void Validation_blocks_missing_trigger_and_warns_on_duplicates()
    {
        var cfg = MakeConfig(("base.yml", Fixtures.HandWritten), ("work.yml", "matches:\n  - trigger: \"/co\"\n    replace: \"dup\"\n"));
        var s = cfg.NewSnippet(cfg.Files[0], "");
        s.Body = new List<Run> { Run.Literal("x"), Run.Cursor(), Run.Cursor() };
        var issues = Validator.Check(cfg);
        Assert.Contains(issues, i => i.Snippet == s && i.Severity == Severity.Error && i.Message.Contains("trigger"));
        Assert.Contains(issues, i => i.Snippet == s && i.Severity == Severity.Error && i.Message.Contains("cursor"));
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("/co"));

        var result = SaveService.Save(cfg, Options());
        Assert.False(result.Success);
        Assert.NotEmpty(result.BlockingIssues);
    }

    [Fact]
    public void A_file_changed_on_disk_is_not_overwritten_without_asking()
    {
        var cfg = MakeConfig(("base.yml", Fixtures.HandWritten));
        var f = cfg.Files[0];
        f.Snippets[0].Label = "Greeting";
        f.Snippets[0].IsDirty = true;
        File.AppendAllText(f.FullPath, "\n# edited elsewhere\n");

        var first = SaveService.Save(cfg, Options());
        Assert.False(first.Success);
        Assert.Single(first.Conflicts);
        Assert.Contains("# edited elsewhere", File.ReadAllText(f.FullPath));

        var forced = SaveService.Save(cfg, Options(overwrite: true));
        Assert.True(forced.Success, string.Join("; ", forced.Errors));
        Assert.Contains("# edited elsewhere", File.ReadAllText(Path.Combine(forced.BackupFolder!, "base.yml")));
        Assert.Contains("label: \"Greeting\"", File.ReadAllText(f.FullPath));
    }

    [Fact]
    public void Line_endings_and_bom_are_kept()
    {
        var match = Path.Combine(_root, "espanso", "match");
        Directory.CreateDirectory(match);
        var crlf = Fixtures.HandWritten.Replace("\n", "\r\n");
        File.WriteAllBytes(Path.Combine(match, "base.yml"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(crlf)).ToArray());
        var cfg = EspansoConfig.Load(Path.Combine(_root, "espanso"));
        var f = cfg.Files[0];
        Assert.Equal("\r\n", f.Newline);
        Assert.True(f.HasBom);
        f.Snippets[1].Label = "Date";
        f.Snippets[1].IsDirty = true;
        Assert.True(SaveService.Save(cfg, Options()).Success);
        var bytes = File.ReadAllBytes(f.FullPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var written = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.DoesNotContain("\n", written.Replace("\r\n", ""));
        Assert.Contains("label: \"Date\"\r\n", written);
    }

    [Fact]
    public void Packages_and_flow_style_files_are_read_only()
    {
        var cfg = MakeConfig(
            ("base.yml", "matches: [{trigger: \":a\", replace: \"a\"}]\n"),
            (Path.Combine("packages", "greet", "package.yml"), "matches:\n  - trigger: \":hi\"\n    replace: \"hi\"\n"));
        Assert.All(cfg.Files, f => Assert.True(f.IsReadOnly));
        Assert.Contains(cfg.Files, f => f.IsPackage && f.DisplayName == "greet");
    }

    [Fact]
    public void Strftime_preview_formats()
    {
        var t = new DateTime(2026, 10, 9, 9, 48, 5);
        Assert.Equal("10/09/2026", StrfTime.Format("%m/%d/%Y", t, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("09:48:05 Friday 9", StrfTime.Format("%H:%M:%S %A %-d", t, System.Globalization.CultureInfo.InvariantCulture));
    }
}
