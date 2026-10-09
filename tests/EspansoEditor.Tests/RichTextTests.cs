using System.Text;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Saving;
using EspansoEditor.Core.Text;
using EspansoEditor.Core.Validation;
using EspansoEditor.Core.Yaml;
using Xunit;

namespace EspansoEditor.Tests;

/// <summary>0.1.4: formatted snippets saved as Plain text, Jira Cloud (Markdown) or HTML.</summary>
public sealed class RichTextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ese-rich-" + Guid.NewGuid().ToString("N"));

    public RichTextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    private const string JiraTemplate =
        "**Description:**\n" +
        "\n" +
        "* **Source:** Customer / Internal\n" +
        "* **Customer Information:** [Include Instance/Region]\n" +
        "* **Customer Expected Outcome:** \n" +
        "* **Has it worked before?** Yes / No\n";

    private static List<Run> Text(string s) => new() { Run.Literal(s) };

    private static MatchFile Parse(string text) => MatchFileParser.ParseText("/x/base.yml", "base.yml", text, false);

    private EspansoConfig MakeConfig(string text)
    {
        var match = Path.Combine(_root, "espanso", "match");
        Directory.CreateDirectory(match);
        File.WriteAllText(Path.Combine(match, "base.yml"), text, new UTF8Encoding(false));
        return EspansoConfig.Load(Path.Combine(_root, "espanso"));
    }

    private string DiskText() => File.ReadAllText(Path.Combine(_root, "espanso", "match", "base.yml"));

    // ---------- Markdown ----------

    [Theory]
    [InlineData(JiraTemplate)]
    [InlineData("# Title\n## Sub\n### Small\nplain *italic* **bold** `code` end")]
    [InlineData("1. first\n2. second\n  - nested\n")]
    [InlineData("**bold *and italic* inside**")]
    public void Markdown_reads_back_exactly(string text)
    {
        Assert.True(RichText.RoundTrips(Text(text)));
        Assert.True(RichText.LooksLikeMarkdown(Text(text)));
    }

    [Theory]
    [InlineData("Plain text, nothing special.")]
    [InlineData("2 * 3 * 4 = 24")]
    [InlineData("**unclosed bold")]
    [InlineData("a*b")]
    public void Ordinary_text_stays_plain(string text) => Assert.False(RichText.LooksLikeMarkdown(Text(text)));

    [Fact]
    public void Lines_and_styles_are_understood()
    {
        var blocks = RichText.ParseMarkdown(Text(JiraTemplate));
        Assert.Equal(BlockKind.Line, blocks[0].Kind);
        Assert.True(Assert.Single(blocks[0].Inlines).Bold);
        Assert.Equal(BlockKind.Bullet, blocks[2].Kind);
        Assert.Equal(new[] { "Source:", " Customer / Internal" }, blocks[2].Inlines.Select(i => i.Text));
        Assert.Equal(" ", blocks[4].Inlines[^1].Text); // trailing space kept
    }

    [Fact]
    public void Chips_keep_their_place_and_style()
    {
        var body = new List<Run> { Run.Literal("* **Date:** "), Run.Date("%d/%m/%Y"), Run.Literal(" by **"), Run.Clipboard(), Run.Literal("**") };
        Assert.True(RichText.RoundTrips(body));
        var b = Assert.Single(RichText.ParseMarkdown(body));
        Assert.Equal(RunKind.Date, b.Inlines[2].Chip!.Kind);
        Assert.True(b.Inlines[^1].IsChip && b.Inlines[^1].Bold);
    }

    [Fact]
    public void Plain_version_drops_the_markup()
    {
        var plain = RichText.ToPlain(RichText.ParseMarkdown(Text("# Hi\n* **a** b\n1. c")));
        Assert.Equal("Hi\n• a b\n1. c", Assert.Single(plain).Text);
    }

    // ---------- reading and writing ----------

    [Fact]
    public void Markdown_snippets_open_as_Jira_and_save_unchanged()
    {
        var yaml = "matches:\n  - trigger: \"/jira\"\n    replace: |\n" + string.Join("", JiraTemplate.Split('\n').Select(l => l.Length == 0 ? "\n" : "      " + l + "\n")).TrimEnd('\n') + "\n" +
                   "  - trigger: \"/plain\"\n    replace: \"just text\"\n";
        var f = Parse(yaml);
        Assert.Equal(SnippetFormat.Jira, f.Snippets[0].Format);
        Assert.Equal(SnippetFormat.Plain, f.Snippets[1].Format);
        foreach (var s in f.Snippets) s.IsDirty = true;
        var text = FileRewriter.Rewrite(f, new HashSet<string>());
        Assert.Null(Verifier.Verify(f, text));
        Assert.Equal(yaml, text);
    }

    [Fact]
    public void Html_snippets_are_written_and_read_back()
    {
        var cfg = MakeConfig("matches:\n  - trigger: \"/x\"\n    replace: \"x\"\n");
        var s = cfg.NewSnippet(cfg.Files[0], "");
        s.Triggers.Add("/report");
        s.Format = SnippetFormat.Html;
        s.Body = new List<Run> { Run.Literal("## Report\n**Date:** "), Run.Date("%d/%m/%Y"), Run.Literal("\n\n- one <a> & b\n- *two*\n1. `code`") };
        var r = SaveService.Save(cfg, new SaveOptions { BackupRoot = Path.Combine(_root, "bk") });
        Assert.True(r.Success, string.Join(" ", r.Errors));

        var disk = DiskText();
        Assert.Contains("    html: |\n      <h2>Report</h2>\n      <div><strong>Date:</strong> {{date}}</div>\n      <div><br></div>\n      <ul>\n      <li>one &lt;a&gt; &amp; b</li>\n      <li><em>two</em></li>\n      </ul>\n      <ol>\n      <li><code>code</code></li>\n      </ol>\n", disk);

        var back = Parse(disk).Snippets.Single(x => x.Triggers[0] == "/report");
        Assert.Equal(SnippetFormat.Html, back.Format);
        Assert.False(back.IsAdvanced);
        Assert.Equal(s.SemanticSignature(), back.SemanticSignature());
    }

    [Fact]
    public void Switching_Jira_to_Html_and_back_round_trips()
    {
        var yaml = "matches:\n  - trigger: \"/t\"\n    replace: \"**Hi** there\\n* one\\n* two\"\n";
        var cfg = MakeConfig(yaml);
        var s = cfg.AllSnippets.Single();
        Assert.Equal(SnippetFormat.Jira, s.Format);
        s.Format = SnippetFormat.Html;
        s.IsDirty = true;
        Assert.True(SaveService.Save(cfg, new SaveOptions { BackupRoot = Path.Combine(_root, "bk") }).Success);
        Assert.Contains("html: |", DiskText());
        s.Format = SnippetFormat.Jira;
        s.IsDirty = true;
        Assert.True(SaveService.Save(cfg, new SaveOptions { BackupRoot = Path.Combine(_root, "bk") }).Success);
        Assert.Contains("replace: |-\n      **Hi** there\n      * one\n      * two\n", DiskText());
    }

    [Theory]
    [InlineData("<div class=\"x\">hi</div>")]
    [InlineData("<table><tr><td>a</td></tr></table>")]
    [InlineData("<a href=\"https://x\">link</a>")]
    public void Html_the_editor_cannot_show_stays_read_only(string html)
    {
        var f = Parse("matches:\n  - trigger: \":h\"\n    html: '" + html + "'\n");
        Assert.True(f.Snippets[0].IsAdvanced);
    }

    [Fact]
    public void Hand_written_simple_html_is_editable()
    {
        var f = Parse("matches:\n  - trigger: \":h\"\n    html: \"<p>Hello <b>you</b></p><p>Bye</p>\"\n");
        var s = f.Snippets[0];
        Assert.False(s.IsAdvanced);
        Assert.Equal("Hello **you**\nBye", Assert.Single(s.Body).Text);
    }

    // ---------- from the code review ----------

    [Theory]
    [InlineData("a*b*c and use `npm`")]
    [InlineData("- dash line")]
    [InlineData("1. not a list")]
    [InlineData("# not a heading")]
    [InlineData("back\\slash")]
    public void Html_keeps_literal_characters_literal(string literal)
    {
        // What the editor shows as plain text in an HTML snippet must paste as that text.
        var block = new RichBlock();
        block.Inlines.Add(new RichInline(literal));
        var body = RichText.ToMarkdown(new[] { block }, escape: true);
        var html = string.Concat(RichText.ToHtml(body).Select(r => r.Text));
        Assert.Equal("<div>" + literal.Replace("&", "&amp;") + "</div>\n", html);
        var back = RichText.ParseMarkdown(body, escaped: true);
        Assert.Equal(literal, Assert.Single(Assert.Single(back).Inlines).Text);
    }

    [Theory]
    [InlineData("<div>1. Step one</div>", "1. Step one")]
    [InlineData("<div>- dash</div>", "- dash")]
    [InlineData("<p>a*b*c</p>", "a*b*c")]
    public void Hand_written_html_text_is_not_turned_into_formatting(string html, string text)
    {
        var f = Parse("matches:\n  - trigger: \":h\"\n    html: \"" + html + "\"\n");
        var s = f.Snippets[0];
        Assert.False(s.IsAdvanced);
        var b = Assert.Single(RichText.ParseMarkdown(s.Body, escaped: true));
        Assert.Equal(BlockKind.Line, b.Kind);
        Assert.Equal(text, Assert.Single(b.Inlines).Text);
        s.IsDirty = true;
        var written = FileRewriter.Rewrite(f, new HashSet<string>());
        Assert.Null(Verifier.Verify(f, written));
        Assert.Contains("<div>" + text + "</div>", written);
    }

    [Fact]
    public void Styles_with_spaces_at_the_edge_or_code_inside_are_written_correctly()
    {
        var block = new RichBlock();
        block.Inlines.Add(new RichInline("word ", Bold: true));
        block.Inlines.Add(new RichInline("next "));
        block.Inlines.Add(new RichInline("x", Bold: true, Code: true));
        block.Inlines.Add(new RichInline(" a ", Bold: true));
        block.Inlines.Add(new RichInline("b", Bold: true, Italic: true));
        var md = Assert.Single(RichText.ToMarkdown(new[] { block })).Text;
        Assert.Equal("**word** next **`x` a *b***", md);
        var back = Assert.Single(RichText.ParseMarkdown(Text(md)));
        Assert.Equal(new[] { (true, false, false), (false, false, false), (true, false, true), (true, false, false), (true, true, false) },
            back.Inlines.Select(i => (i.Bold, i.Italic, i.Code)));
    }

    [Theory]
    [InlineData("2*3*4")]
    [InlineData("*** WARNING ***")]
    [InlineData("# comment line")]
    [InlineData("- John")]
    [InlineData("snake_case and file*.txt")]
    public void Ordinary_text_is_not_taken_for_Markdown(string text) => Assert.False(RichText.LooksLikeMarkdown(Text(text)));

    [Theory]
    [InlineData("<ol><li>a<ul><li>x</li></ul></li><li>b</li></ol>")]
    [InlineData("<ul><li>a<br>b</li></ul>")]
    public void Html_the_editor_would_flatten_stays_read_only(string html)
    {
        var f = Parse("matches:\n  - trigger: \":h\"\n    html: \"" + html + "\"\n");
        Assert.True(f.Snippets[0].IsAdvanced);
    }

    [Fact]
    public void Long_lines_parse_quickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        RichText.LooksLikeMarkdown(Text(string.Concat(Enumerable.Repeat("**a ", 20000))));
        RichText.LooksLikeMarkdown(Text(string.Concat(Enumerable.Repeat("*a ", 1300))));
        RichText.ToHtml(Text(string.Join("\n", Enumerable.Repeat("* item", 20000))));
        Assert.True(watch.ElapsedMilliseconds < 3000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Html_cannot_ask_when_typing()
    {
        var cfg = MakeConfig("matches:\n  - trigger: \"/x\"\n    replace: \"x\"\n");
        var s = cfg.AllSnippets.Single();
        s.Format = SnippetFormat.Html;
        s.Body = new List<Run> { Run.Literal("Hi "), Run.Field("name") };
        Assert.Contains(Validator.Check(s, cfg), i => i.Severity == Severity.Error && i.Message.Contains("HTML snippets cannot ask"));
    }
}
