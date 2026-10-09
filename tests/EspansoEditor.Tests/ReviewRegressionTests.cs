using System.Text;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Saving;
using EspansoEditor.Core.Yaml;
using Xunit;

namespace EspansoEditor.Tests;

/// <summary>Cases found in the independent review of 0.1.0. Each one used to lose or change data.</summary>
public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ese-review-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlySet<string> NoGlobals = new HashSet<string>();

    public ReviewRegressionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    private static MatchFile Parse(string text) => MatchFileParser.ParseText("/x/base.yml", "base.yml", text, false);

    private EspansoConfig MakeConfig(params (string Name, string Text)[] files)
    {
        var match = Path.Combine(_root, "espanso", "match");
        Directory.CreateDirectory(match);
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(match, name), text, new UTF8Encoding(false));
        return EspansoConfig.Load(Path.Combine(_root, "espanso"));
    }

    [Fact]
    public void Untouched_last_form_and_trailing_comments_survive_an_insert()
    {
        const string text =
            "matches:\n" +
            "  - trigger: \":a\"\n" +
            "    replace: \"a\"\n" +
            "  - trigger: \":hu\"\n" +
            "    form: \"Kedves [[név]], köszönöm\"\n" +
            "    # - trigger: \":old\"\n" +
            "    #   replace: \"old\"";
        var cfg = MakeConfig(("base.yml", text));
        var f = cfg.Files[0];
        var hu = f.Snippets.Single(s => s.Triggers[0] == ":hu");
        Assert.False(hu.IsAdvanced);
        Assert.Equal(RunKind.Field, hu.Body[1].Kind);
        Assert.Equal("név", hu.Body[1].Text);

        var s = cfg.NewSnippet(f, "");
        s.Triggers.Add(":new");
        s.Body = new List<Run> { Run.Literal("new") };
        var r = FileRewriter.RewriteWithInfo(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, r.Text, r.Reemitted));
        Assert.Contains("    form: \"Kedves [[név]], köszönöm\"\n", r.Text);
        Assert.EndsWith("    # - trigger: \":old\"\n    #   replace: \"old\"", r.Text);
        Assert.False(hu.IsDirty);
    }

    [Fact]
    public void Block_at_end_of_file_is_rewritten_only_when_needed_and_not_marked_dirty()
    {
        var cfg = MakeConfig(("base.yml", Fixtures.HandWritten));
        var f = cfg.Files[0];
        var me = f.Snippets.Single(s => s.Triggers[0] == "/me");
        var s = cfg.NewSnippet(f, "A New Group");
        s.Triggers.Add(":x");
        s.Body = new List<Run> { Run.Literal("x") };
        var r = FileRewriter.RewriteWithInfo(f, NoGlobals);
        Assert.Contains(me, r.Reemitted);
        Assert.False(me.IsDirty);
        Assert.Null(Verifier.Verify(f, r.Text, r.Reemitted));
        Assert.Contains("replace: |-\n      Best,\n\n      Support Team\n", r.Text);

        var result = SaveService.Save(cfg, new SaveOptions { BackupRoot = Path.Combine(_root, "bk") });
        Assert.True(result.Success, string.Join("; ", result.Errors));
        var back = EspansoConfig.Load(Path.Combine(_root, "espanso")).Files[0];
        Assert.Equal("Best,\n\nSupport Team", BodyText.LiteralText(back.Snippets.Single(x => x.Triggers[0] == "/me").Body));
    }

    [Fact]
    public void Accented_variable_and_field_names_are_understood()
    {
        var f = Parse("matches:\n  - trigger: \":sig\"\n    replace: \"Üdv, {{név}}\"\n");
        var sig = f.Snippets[0];
        Assert.False(sig.IsAdvanced);
        Assert.Equal(RunKind.GlobalVar, sig.Body[1].Kind);
        sig.Label = "Aláírás";
        sig.IsDirty = true;
        var text = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, text));
        Assert.Contains("replace: \"Üdv, {{név}}\"", text);
    }

    [Theory]
    [InlineData("  - trigger: \":f\"\n    form: \"no fields here\"\n", "form without fields")]
    [InlineData("  - trigger: \":t\"\n    triggers: [\":u\"]\n    replace: \"x\"\n", "both 'trigger' and 'triggers'")]
    public void Ambiguous_matches_are_read_only(string item, string reason)
    {
        var s = Parse("matches:\n" + item).Snippets[0];
        Assert.True(s.IsAdvanced);
        Assert.Contains(reason, s.AdvancedReason);
    }

    [Fact]
    public void Escaped_closing_braces_read_as_braces()
    {
        var s = Parse("matches:\n  - trigger: \":b\"\n    replace: \"\\\\{\\\\{x\\\\}\\\\}\"\n").Snippets[0];
        Assert.Equal("{{x}}", BodyText.LiteralText(s.Body));
    }

    [Fact]
    public void Invalid_utf8_file_is_read_only()
    {
        var bytes = Encoding.ASCII.GetBytes("matches:\n  - trigger: \":e\"\n    replace: \"caf").Concat(new byte[] { 0xE9 }).Concat(Encoding.ASCII.GetBytes("\"\n")).ToArray();
        var f = MatchFileParser.Parse("/x/base.yml", "base.yml", bytes, false);
        Assert.True(f.IsReadOnly);
        Assert.Contains("UTF-8", f.ReadOnlyReason);
    }

    [Fact]
    public void Files_with_yaml_anchors_are_read_only()
    {
        var f = Parse("matches:\n  - &one\n    trigger: \":a\"\n    replace: \"a\"\n  - *one\n");
        Assert.True(f.IsReadOnly);
        Assert.Contains("anchors", f.ReadOnlyReason);
    }

    [Fact]
    public void Double_bom_file_is_read_only()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("matches:\n  - trigger: \":a\"\n    replace: \"a\"\n")).ToArray();
        Assert.True(MatchFileParser.Parse("/x/base.yml", "base.yml", bytes, false).IsReadOnly);
    }

    [Fact]
    public void Comments_inside_an_edited_snippet_are_kept()
    {
        const string text =
            "matches:\n" +
            "  - trigger: \":a\"   # the greeting\n" +
            "    # word: true\n" +
            "    replace: |\n" +
            "      # not a comment, this is text\n" +
            "      hello\n" +
            "\n" +
            "  - trigger: \":b\"\n" +
            "    replace: \"b\"\n";
        var f = Parse(text);
        var a = f.Snippets[0];
        Assert.Equal("# not a comment, this is text\nhello\n", BodyText.LiteralText(a.Body));
        a.Label = "Greeting";
        a.IsDirty = true;
        var newText = FileRewriter.Rewrite(f, NoGlobals);
        Assert.Null(Verifier.Verify(f, newText));
        Assert.Contains("    # the greeting\n", newText);
        Assert.Contains("    # word: true\n", newText);
        Assert.Contains("      # not a comment, this is text\n", newText);
    }

    [Fact]
    public void A_failed_swap_loses_nothing_and_a_retry_completes_the_move()
    {
        var cfg = MakeConfig(
            ("a.yml", "matches:\n  - trigger: \":a1\"\n    replace: \"a1\"\n  - trigger: \":a2\"\n    replace: \"a2\"\n"),
            ("b.yml", "matches:\n  - trigger: \":b1\"\n    replace: \"b1\"\n"));
        var a = cfg.Files.Single(f => f.RelativePath == "a.yml");
        var b = cfg.Files.Single(f => f.RelativePath == "b.yml");
        var a1 = a.Snippets.Single(s => s.Triggers[0] == ":a1");
        cfg.Move(a1, b, "");

        // Make replacing a.yml impossible: a directory now sits where the file was.
        var aText = File.ReadAllText(a.FullPath);
        File.Delete(a.FullPath);
        Directory.CreateDirectory(a.FullPath);

        var options = new SaveOptions { BackupRoot = Path.Combine(_root, "bk"), OverwriteChangedFiles = true };
        var failed = SaveService.Save(cfg, options);
        Assert.False(failed.Success);
        Assert.True(a1.IsMoved);              // the editor still knows the move is pending
        Assert.Contains(":a1", File.ReadAllText(b.FullPath));

        Directory.Delete(a.FullPath);
        File.WriteAllText(a.FullPath, aText);
        var retry = SaveService.Save(cfg, options);
        Assert.True(retry.Success, string.Join("; ", retry.Errors));

        var back = EspansoConfig.Load(Path.Combine(_root, "espanso"));
        Assert.Equal(1, back.AllSnippets.Count(s => s.Triggers.Contains(":a1")));
        Assert.DoesNotContain(back.Files.Single(f => f.RelativePath == "a.yml").Snippets, s => s.Triggers.Contains(":a1"));
        Assert.Equal(3, back.AllSnippets.Count()); // a1, a2, b1
    }
}
