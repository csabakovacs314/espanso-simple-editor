using System.Text;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Saving;
using EspansoEditor.Core.Validation;
using EspansoEditor.Core.Yaml;
using Xunit;

namespace EspansoEditor.Tests;

/// <summary>0.1.2: whole-word default, switching it for all snippets, and triggers that block longer ones.</summary>
public sealed class WholeWordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ese-ww-" + Guid.NewGuid().ToString("N"));

    public WholeWordTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    /// <summary>Triggers that start with other triggers, like a real support file.</summary>
    private const string Prefixes =
        "matches:\n" +
        "  # --- Jira ---\n" +
        "  - trigger: \"/jira\"\n" +
        "    replace: |\n" +
        "      Jira ticket text\n" +
        "\n" +
        "  - trigger: \"/jirauao\"\n" +
        "    replace: \"UAO text\"\n" +
        "\n" +
        "  - trigger: \"/jiraplugin\"\n" +
        "    label: \"Plugin\"\n" +
        "    replace: |\n" +
        "      Plugin text\n" +
        "\n" +
        "  # --- Closing ---\n" +
        "  - trigger: \"/close\"\n" +
        "    replace: \"Closing\"\n" +
        "\n" +
        "  - trigger: \"/close2\"\n" +
        "    replace: \"Closing 2\"\n" +
        "\n" +
        "  - trigger: \"/me\"\n" +
        "    replace: \"Csaba\"\n";

    private static MatchFile Parse(string text) => MatchFileParser.ParseText("/x/base.yml", "base.yml", text, false);

    private static Snippet By(MatchFile f, string trigger) => f.Snippets.Single(s => s.Triggers.Contains(trigger));

    private EspansoConfig MakeConfig(string text)
    {
        var match = Path.Combine(_root, "espanso", "match");
        Directory.CreateDirectory(match);
        File.WriteAllText(Path.Combine(match, "base.yml"), text, new UTF8Encoding(false));
        return EspansoConfig.Load(Path.Combine(_root, "espanso"));
    }

    private string DiskText() => File.ReadAllText(Path.Combine(_root, "espanso", "match", "base.yml"));

    private SaveOptions Options => new() { BackupRoot = Path.Combine(_root, "backups") };

    // ---------- finding blocked triggers ----------

    [Fact]
    public void Short_triggers_that_start_longer_ones_are_found()
    {
        var f = Parse(Prefixes);
        var clashes = TriggerClashes.Find(f.Snippets);
        Assert.Equal(3, clashes.Count);
        Assert.Contains(clashes, c => c.Short == "/jira" && c.Long == "/jirauao");
        Assert.Contains(clashes, c => c.Short == "/jira" && c.Long == "/jiraplugin");
        Assert.Contains(clashes, c => c.Short == "/close" && c.Long == "/close2");
        Assert.Equal(new[] { "/jira", "/close" }, TriggerClashes.FixableBlockers(f.Snippets).Select(s => s.Triggers[0]));
    }

    [Fact]
    public void Whole_word_trigger_no_longer_blocks()
    {
        var f = Parse(Prefixes);
        By(f, "/jira").Word = true;
        var clashes = TriggerClashes.Find(f.Snippets);
        Assert.DoesNotContain(clashes, c => c.Short == "/jira");
        Assert.Single(clashes); // /close still blocks /close2
    }

    [Fact]
    public void A_trigger_inside_another_one_blocks_it_too()
    {
        var f = Parse("matches:\n  - trigger: \"ir\"\n    replace: \"x\"\n  - trigger: \"/jira\"\n    replace: \"y\"\n");
        var c = Assert.Single(TriggerClashes.Find(f.Snippets));
        Assert.Equal("ir", c.Short);
        Assert.Equal("/jira", c.Long);
    }

    [Fact]
    public void Whole_word_still_blocks_when_the_longer_trigger_has_a_space_after_it()
    {
        var f = Parse("matches:\n  - trigger: \":a\"\n    replace: \"x\"\n    word: true\n  - trigger: \":a b\"\n    replace: \"y\"\n");
        Assert.Single(TriggerClashes.Find(f.Snippets));
    }

    [Fact]
    public void Same_trigger_twice_is_a_duplicate_not_a_block()
    {
        var f = Parse("matches:\n  - trigger: \":a\"\n    replace: \"x\"\n  - trigger: \":a\"\n    replace: \"y\"\n");
        Assert.Empty(TriggerClashes.Find(f.Snippets));
    }

    [Fact]
    public void Both_snippets_get_a_warning()
    {
        var cfg = MakeConfig(Prefixes);
        var all = cfg.AllSnippets.ToList();
        var jira = all.Single(s => s.Triggers[0] == "/jira");
        var uao = all.Single(s => s.Triggers[0] == "/jirauao");
        Assert.Contains(Validator.Check(jira, cfg), i => i.Severity == Severity.Warning && i.Message.Contains("fires before \"/jirauao\", \"/jiraplugin\""));
        Assert.Contains(Validator.Check(uao, cfg), i => i.Severity == Severity.Warning && i.Message.Contains("\"/jirauao\" can never be typed"));
        jira.Word = true;
        Assert.DoesNotContain(Validator.Check(uao, cfg), i => i.Message.Contains("never be typed"));
    }

    // ---------- one-line saves ----------

    [Fact]
    public void Switching_whole_word_on_adds_one_line_and_nothing_else()
    {
        var f = Parse(Prefixes);
        var jira = By(f, "/jira");
        jira.Word = true;
        jira.IsDirty = true;
        var text = FileRewriter.Rewrite(f, new HashSet<string>(), minimal: true);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Equal(Prefixes.Replace("  - trigger: \"/jira\"\n", "  - trigger: \"/jira\"\n    word: true\n"), text);
    }

    [Fact]
    public void Switching_whole_word_off_removes_that_line()
    {
        var withWord = Prefixes.Replace("  - trigger: \"/close\"\n    replace: \"Closing\"\n", "  - trigger: \"/close\"\n    replace: \"Closing\"\n    word: true # needed\n");
        var f = Parse(withWord);
        var close = By(f, "/close");
        Assert.True(close.Word);
        close.Word = false;
        close.IsDirty = true;
        var text = FileRewriter.Rewrite(f, new HashSet<string>(), minimal: true);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Equal(Prefixes, text);
    }

    [Fact]
    public void Word_false_is_flipped_in_place()
    {
        var withFalse = Prefixes.Replace("    replace: \"UAO text\"\n", "    replace: \"UAO text\"\n    word: false\n");
        var f = Parse(withFalse);
        var uao = By(f, "/jirauao");
        uao.Word = true;
        uao.IsDirty = true;
        var text = FileRewriter.Rewrite(f, new HashSet<string>(), minimal: true);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Equal(withFalse.Replace("word: false", "word: true"), text);
    }

    [Fact]
    public void An_edit_undone_by_hand_writes_nothing()
    {
        var f = Parse(Prefixes);
        var me = By(f, "/me");
        me.Label = "Name";
        me.Label = "";
        me.IsDirty = true;
        Assert.Equal(Prefixes, FileRewriter.Rewrite(f, new HashSet<string>(), minimal: true));
    }

    [Fact]
    public void Whole_word_with_other_changes_rewrites_the_snippet_as_before()
    {
        var f = Parse(Prefixes);
        var me = By(f, "/me");
        me.Word = true;
        me.Label = "Name";
        me.IsDirty = true;
        var text = FileRewriter.Rewrite(f, new HashSet<string>(), minimal: true);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Contains("  - trigger: \"/me\"\n    label: \"Name\"\n    replace: \"Csaba\"\n    word: true\n", text);
    }

    [Fact]
    public void Last_block_at_end_of_file_without_line_break_still_saves()
    {
        var noFinalBreak = Prefixes + "\n  - trigger: \"/sig\"\n    replace: |\n      Best,\n      Csaba";
        var cfg = MakeConfig(noFinalBreak);
        var sig = cfg.AllSnippets.Single(s => s.Triggers[0] == "/sig");
        var change = cfg.SetWholeWord(new[] { sig }, true);
        Assert.Single(change.Changed);
        var r = SaveService.Save(cfg, Options);
        Assert.True(r.Success, string.Join(" ", r.Errors));
        var back = Parse(DiskText());
        Assert.True(By(back, "/sig").Word);
        Assert.Equal("Best,\nCsaba", Assert.Single(By(back, "/sig").Body).Text);
        Assert.StartsWith(Prefixes, DiskText());
    }

    // ---------- all at once ----------

    [Fact]
    public void Set_all_to_whole_word_saves_one_line_per_snippet_and_back()
    {
        var cfg = MakeConfig(Prefixes);
        var change = cfg.SetWholeWord(cfg.AllSnippets, true);
        Assert.Equal(6, change.Changed.Count);
        var r = SaveService.Save(cfg, Options);
        Assert.True(r.Success, string.Join(" ", r.Errors));
        var expected = string.Join("\n", Prefixes.Split('\n').SelectMany(l => l.StartsWith("  - trigger:") ? new[] { l, "    word: true" } : new[] { l }));
        Assert.Equal(expected, DiskText());
        Assert.All(cfg.AllSnippets, s => Assert.False(s.IsDirty));

        // The positions were taken over after the save, so switching back is again one line each.
        cfg.SetWholeWord(cfg.AllSnippets, false);
        r = SaveService.Save(cfg, Options);
        Assert.True(r.Success, string.Join(" ", r.Errors));
        Assert.Equal(Prefixes, DiskText());
    }

    [Fact]
    public void Only_where_needed_fixes_the_blockers()
    {
        var cfg = MakeConfig(Prefixes);
        var change = cfg.SetWholeWord(TriggerClashes.FixableBlockers(cfg.AllSnippets), true);
        Assert.Equal(new[] { "/jira", "/close" }, change.Changed.Select(c => c.Snippet.Triggers[0]));
        Assert.Empty(TriggerClashes.Find(cfg.AllSnippets));
        Assert.True(SaveService.Save(cfg, Options).Success);
        Assert.Equal(2, DiskText().Split('\n').Count(l => l == "    word: true"));
    }

    [Fact]
    public void Undo_puts_everything_back()
    {
        var cfg = MakeConfig(Prefixes);
        var close = cfg.AllSnippets.Single(s => s.Triggers[0] == "/close");
        close.Label = "Close";
        close.IsDirty = true;
        var change = cfg.SetWholeWord(cfg.AllSnippets, true);
        change.Undo();
        Assert.All(cfg.AllSnippets, s => Assert.False(s.Word));
        Assert.True(close.IsDirty);
        Assert.Equal(1, cfg.AllSnippets.Count(s => s.IsDirty));
    }

    [Fact]
    public void Read_only_snippets_are_counted_not_changed()
    {
        var cfg = MakeConfig(Prefixes + "  - trigger: \":cat\"\n    image_path: \"cat.png\"\n");
        var change = cfg.SetWholeWord(cfg.AllSnippets, true);
        Assert.Equal(6, change.Changed.Count);
        Assert.Equal(1, change.Locked);
        Assert.False(cfg.AllSnippets.Single(s => s.Triggers[0] == ":cat").Word);
    }

    [Fact]
    public void New_snippets_can_start_as_whole_word()
    {
        var cfg = MakeConfig(Prefixes);
        var s = cfg.NewSnippet(cfg.Files[0], "Closing", wholeWord: true);
        s.Triggers.Add("/close3");
        s.Body = new List<Run> { Run.Literal("Closing 3") };
        Assert.True(SaveService.Save(cfg, Options).Success);
        Assert.Contains("  - trigger: \"/close3\"\n    replace: \"Closing 3\"\n    word: true\n", DiskText());
    }
}
