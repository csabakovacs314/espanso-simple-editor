using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Publisher.Core;

namespace EspansoEditor.Tests;

public class PageLinkTests
{
    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123456 ", "123456")]
    [InlineData("https://acme.atlassian.net/wiki/spaces/TEAM/pages/123456/Guides", "123456")]
    [InlineData("https://acme.atlassian.net/wiki/spaces/TEAM/pages/edit-v2/123456", "123456")]
    [InlineData("https://acme.atlassian.net/wiki/pages/viewpage.action?pageId=987", "987")]
    [InlineData("https://acme.atlassian.net/wiki/spaces/TEAM/overview", null)]
    [InlineData("", null)]
    [InlineData("Guides", null)]
    public void IdFrom(string text, string? expected) => Assert.Equal(expected, PageLink.IdFrom(text));

    [Theory]
    [InlineData("acme.atlassian.net", "https://acme.atlassian.net/")]
    [InlineData("https://acme.atlassian.net/wiki/spaces/TEAM", "https://acme.atlassian.net/")]
    [InlineData("http://acme.atlassian.net", null)]
    [InlineData("", null)]
    public void Site(string text, string? expected) => Assert.Equal(expected, PageLink.Site(text)?.ToString());
}

public class StorageFormatTests
{
    private static XElement Parse(string storage) =>
        XElement.Parse("<root xmlns:ac=\"http://atlassian.com/content\" xmlns:ri=\"http://atlassian.com/resource/identifier\">" + storage + "</root>");

    [Fact]
    public void Inline_styles_and_escaping()
    {
        Assert.Equal("<strong>Free</strong> &amp; <em>light</em> &lt;b&gt;", StorageFormat.Inline("**Free** & *light* <b>"));
        Assert.Equal("press <code>Ctrl+S</code> or <code>**x**</code>", StorageFormat.Inline("press `Ctrl+S` or `**x**`"));
        Assert.Equal("<em>Mockup: a template.</em>", StorageFormat.Inline("_Mockup: a template._"));
        Assert.Equal("snake_case_name stays", StorageFormat.Inline("snake_case_name stays"));
        Assert.Equal("Install from <a href=\"https://espanso.org/install/\">https://espanso.org/install/</a>.",
            StorageFormat.Inline("Install from https://espanso.org/install/."));
        Assert.Equal("<a href=\"https://x.org/?a=1&amp;b=2\">the &lt;site&gt;</a>", StorageFormat.Inline("[the <site>](https://x.org/?a=1&b=2)"));
    }

    [Fact]
    public void Pictures_become_attachments()
    {
        var s = StorageFormat.Convert("![Main window](images/espanso-editor-main.png)");
        Assert.Contains("<ri:attachment ri:filename=\"espanso-editor-main.png\" />", s);
        Assert.Equal(["images/a.png", "b.png"], StorageFormat.ImagePaths("![x](images/a.png) ![y](b.png) ![z](IMAGES/A.png)"));
    }

    [Fact]
    public void Lists_nest_by_indent_and_wrapped_lines_join()
    {
        var s = StorageFormat.Convert("* one\n  continues\n  * inner\n* two\n\n1. first\n2. second");
        Assert.Equal("<ul><li>one continues<ul><li>inner</li></ul></li><li>two</li></ul>\n<ol><li>first</li><li>second</li></ol>\n", s);
        Parse(s);
    }

    [Fact]
    public void Boxes_code_and_table()
    {
        var md = "> **In short**\n> Short text.\n\n## A\n## B\n## C\n## D\n\n> **Tip:** Use `Triggers…`.\n\n> **Warning:** Careful.\n\n"
                 + "```yaml\nmatches: ]]> <x>\n```\n\n| Key | Does |\n|---|---|\n| `Ctrl+S` | save |\n";
        var s = StorageFormat.Convert(md);
        Assert.Contains("<ac:structured-macro ac:name=\"info\"><ac:parameter ac:name=\"title\">In short</ac:parameter><ac:rich-text-body><p>Short text.</p>", s);
        Assert.Contains("ac:name=\"toc\"", s);
        Assert.True(s.IndexOf("ac:name=\"toc\"", StringComparison.Ordinal) > s.IndexOf("ac:name=\"info\"", StringComparison.Ordinal));
        Assert.Contains("ac:name=\"tip\"><ac:parameter ac:name=\"title\">Tip</ac:parameter><ac:rich-text-body><p>Use <code>Triggers…</code>.</p>", s);
        Assert.Contains("ac:name=\"warning\"", s);
        Assert.Contains("<ac:parameter ac:name=\"language\">yaml</ac:parameter><ac:plain-text-body><![CDATA[matches: ]]]]><![CDATA[> <x>]]>", s);
        Assert.Contains("<tr><th>Key</th><th>Does</th></tr><tr><td><code>Ctrl+S</code></td><td>save</td></tr>", s);
        var xml = Parse(s);
        Assert.Contains("matches: ]]> <x>", xml.Value);
    }

    [Fact]
    public void Bare_urls_next_to_quotes_and_brackets_stay_well_formed()
    {
        var s = StorageFormat.Convert("See \"https://example.com\" or <https://x.org/a?b=1&c=2>.");
        Assert.Contains("<a href=\"https://example.com\">https://example.com</a>&quot;", s);
        Assert.Contains("<a href=\"https://x.org/a?b=1&amp;c=2\">", s);
        Parse(s);
    }

    [Fact]
    public void Fences_with_any_language_or_indent_are_code()
    {
        var s = StorageFormat.Convert("```c#\n# not a heading\n```\n\n* item\n  ```\n  code\n  ```\n\nAfter.");
        Assert.DoesNotContain("<h1>", s);
        Assert.Contains("<ac:parameter ac:name=\"language\">c#</ac:parameter><ac:plain-text-body><![CDATA[# not a heading]]>", s);
        Assert.Contains("<ul><li>item</li></ul>", s);
        Assert.Contains("<![CDATA[  code]]>", s);
        Assert.EndsWith("<p>After.</p>\n", s);
        Parse(s);
    }

    [Fact]
    public void Control_characters_are_dropped()
    {
        var s = StorageFormat.Convert("A\u0001B `c\u0002d`\n\n```\nx\u000By\n```");
        Assert.Contains("<p>AB <code>cd</code></p>", s);
        Assert.Contains("[CDATA[xy]]", s);
        Parse(s);
    }

    [Fact]
    public void No_toc_for_short_pages()
    {
        Assert.DoesNotContain("toc", StorageFormat.Convert("> **In short**\n> x\n\n## A\n## B"));
    }

    [Fact]
    public void The_real_guides_convert_to_well_formed_pages()
    {
        var docs = FindDocs();
        if (docs == null) return; // Not run from the source tree.
        foreach (var name in new[] { "Espanso-Guide.md", "Espanso-Guide-Short.md" })
        {
            var g = Guide.Load(Path.Combine(docs, name));
            Assert.Equal("Espanso and Espanso Simple Editor", g.Title);
            Assert.DoesNotContain("<h1>", g.Storage);
            Assert.Empty(g.MissingImages);
            Assert.NotEmpty(g.Images);
            Assert.All(g.Images, i => Assert.True(File.Exists(i.Path)));
            Parse(g.Storage);
        }
    }

    internal static string? FindDocs()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var docs = Path.Combine(d.FullName, "docs");
            if (File.Exists(Path.Combine(docs, "Espanso-Guide.md"))) return docs;
        }
        return null;
    }
}

public class GuideTests
{
    [Fact]
    public void Title_comes_out_of_the_body_and_missing_pictures_are_listed()
    {
        var dir = Directory.CreateTempSubdirectory("pubtest");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "images"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "images", "a.png"), [1, 2, 3]);
            var md = Path.Combine(dir.FullName, "g.md");
            File.WriteAllText(md, "# My **Guide**\r\n\r\nText.\r\n\r\n![A](images/a.png)\r\n![B](images/b.png)\r\n");
            var g = Guide.Load(md);
            Assert.Equal("My Guide", g.Title);
            Assert.StartsWith("<p>Text.</p>", g.Storage);
            Assert.Equal("a.png", Assert.Single(g.Images).Name);
            Assert.Equal("images/b.png", Assert.Single(g.MissingImages));

            File.WriteAllText(md, "Intro.\n\n```powershell\n# a comment\n```\n");
            g = Guide.Load(md);
            Assert.Equal("g", g.Title);
            Assert.Contains("# a comment", g.Storage);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

/// <summary>A pretend Confluence: answers from a small table of pages and records every request.</summary>
internal sealed class FakeConfluence : HttpMessageHandler
{
    public readonly List<(string Method, string Url, string Body, HttpRequestMessage Request)> Log = new();
    public readonly Dictionary<string, JsonObject> Pages = new();
    public readonly Dictionary<string, string> ParentOf = new();
    public HttpStatusCode? FailWith;
    public bool FailUploads;
    public int ChildPageSize = 100;
    public int NextId = 900;

    public void Add(string id, string title, string? parent, int version = 1)
    {
        Pages[id] = new JsonObject { ["id"] = id, ["type"] = "page", ["title"] = title, ["version"] = version };
        if (parent != null) ParentOf[id] = parent;
    }

    private JsonObject Json(string id)
    {
        var p = Pages[id];
        var chain = new List<string>();
        for (var a = ParentOf.GetValueOrDefault(id); a != null; a = ParentOf.GetValueOrDefault(a)) chain.Insert(0, a);
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "page",
            ["title"] = p["title"]!.GetValue<string>(),
            ["space"] = new JsonObject { ["key"] = "TEAM", ["name"] = "Team Space" },
            ["ancestors"] = new JsonArray(chain.Select(a => (JsonNode)new JsonObject { ["id"] = a, ["title"] = Pages[a]["title"]!.GetValue<string>() }).ToArray()),
            ["version"] = new JsonObject { ["number"] = p["version"]!.GetValue<int>() },
            ["_links"] = new JsonObject { ["webui"] = $"/spaces/TEAM/pages/{id}" },
        };
    }

    private static HttpResponseMessage Ok(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private HttpResponseMessage List(IEnumerable<string> ids) =>
        Ok(new JsonObject { ["results"] = new JsonArray(ids.Select(i => (JsonNode)Json(i)).ToArray()) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
        var url = req.RequestUri!.PathAndQuery;
        Log.Add((req.Method.Method, url, body, req));
        if (FailWith is { } fail)
            return new HttpResponseMessage(fail) { Content = new StringContent("{\"statusCode\":401,\"message\":\"Unauthorized\"}") };

        var path = req.RequestUri.AbsolutePath["/wiki/rest/api/".Length..];
        var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
        var parts = path.Split('/');

        if (path == "user/current") return Ok(new JsonObject { ["type"] = "known", ["displayName"] = "Csaba" });
        if (req.Method == HttpMethod.Get && path == "content")
            return List(Pages.Keys.Where(k => Pages[k]["title"]!.GetValue<string>() == query["title"]));
        if (req.Method == HttpMethod.Get && parts.Length == 4 && parts[2] == "child" && parts[3] == "page")
        {
            // Pages like Confluence: at most ChildPageSize per answer, with a "next" link while more are left.
            var all = ParentOf.Where(kv => kv.Value == parts[1]).Select(kv => kv.Key).OrderBy(k => k).ToList();
            var start = int.Parse(query["start"] ?? "0");
            var batch = all.Skip(start).Take(ChildPageSize).ToList();
            var json = new JsonObject { ["results"] = new JsonArray(batch.Select(i => (JsonNode)Json(i)).ToArray()), ["_links"] = new JsonObject() };
            if (start + batch.Count < all.Count) json["_links"]!["next"] = "/rest/api/content/" + parts[1] + "/child/page?start=" + (start + batch.Count);
            return Ok(json);
        }
        if (req.Method == HttpMethod.Put && parts.Length == 4 && parts[3] == "attachment")
            return FailUploads
                ? new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) { Content = new StringContent("{\"message\":\"too big\"}") }
                : Ok(new JsonObject { ["results"] = new JsonArray() });
        if (req.Method == HttpMethod.Get && parts.Length == 2)
            return Pages.ContainsKey(parts[1]) ? Ok(Json(parts[1])) : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        if (req.Method == HttpMethod.Post && path == "content")
        {
            var j = JsonNode.Parse(body)!;
            var id = (NextId++).ToString();
            Add(id, j["title"]!.GetValue<string>(), j["ancestors"]![0]!["id"]!.GetValue<string>());
            return Ok(Json(id));
        }
        if (req.Method == HttpMethod.Put && parts.Length == 2)
        {
            var j = JsonNode.Parse(body)!;
            var v = j["version"]!["number"]!.GetValue<int>();
            if (v != Pages[parts[1]]["version"]!.GetValue<int>() + 1) return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{}") };
            Pages[parts[1]]["version"] = v;
            Pages[parts[1]]["title"] = j["title"]!.GetValue<string>();
            return Ok(Json(parts[1]));
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"unexpected " + url + "\"}") };
    }
}

public class PagePublisherTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("pubflow");
    private readonly FakeConfluence _fake = new();
    private readonly ConfluenceClient _client;
    private readonly Guide _guide;

    public PagePublisherTests()
    {
        _fake.Add("1", "Home", null);
        _fake.Add("10", "Tools", "1");
        _fake.Add("11", "Espanso and Espanso Simple Editor", "10", version: 4);
        _fake.Add("20", "Archive", "1");
        _fake.Add("21", "Espanso and Espanso Simple Editor", "20");
        _client = new ConfluenceClient(new Uri("https://acme.atlassian.net"), "me@acme.com", "secret-token", _fake);

        Directory.CreateDirectory(Path.Combine(_dir.FullName, "images"));
        File.WriteAllBytes(Path.Combine(_dir.FullName, "images", "main.png"), [137, 80, 78, 71]);
        var md = Path.Combine(_dir.FullName, "guide.md");
        File.WriteAllText(md, "# Espanso and Espanso Simple Editor\n\nHello.\n\n![Main](images/main.png)\n");
        _guide = Guide.Load(md);
    }

    public void Dispose()
    {
        _client.Dispose();
        _dir.Delete(true);
    }

    [Fact]
    public async Task Sign_in_uses_basic_auth_and_never_puts_the_token_in_a_url()
    {
        Assert.Equal("Csaba", await _client.WhoAmIAsync());
        var auth = _fake.Log[0].Request.Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        Assert.Equal("me@acme.com:secret-token", Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)));
        Assert.All(_fake.Log, l => Assert.DoesNotContain("secret", l.Url));
    }

    [Fact]
    public async Task Planning_reads_only_and_finds_the_child_to_update()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "https://acme.atlassian.net/wiki/spaces/TEAM/pages/10/Tools", _guide.Title);
        Assert.All(_fake.Log, l => Assert.Equal("GET", l.Method));
        Assert.True(plan.IsUpdate);
        Assert.Equal("11", plan.Existing!.Id);
        Assert.Equal("Team Space › Home › Tools", plan.Parent.Path);
        Assert.Equal("Team Space › Home › Archive › Espanso and Espanso Simple Editor", Assert.Single(plan.SameTitleElsewhere).Path);
        var text = plan.Describe();
        Assert.Contains("UPDATE", text);
        Assert.Contains("Parent:  Team Space › Home › Tools", text);
        Assert.Contains("version 4 becomes version 5", text);
        Assert.Contains("main.png", text);
    }

    [Fact]
    public async Task Update_replaces_the_text_and_uploads_pictures()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "10", _guide.Title);
        _fake.Log.Clear();
        var url = await PagePublisher.ExecuteAsync(_client, plan);
        Assert.Equal("https://acme.atlassian.net/wiki/spaces/TEAM/pages/11", url);
        Assert.Equal(5, _fake.Pages["11"]["version"]!.GetValue<int>());
        var put = _fake.Log.Single(l => l.Method == "PUT" && l.Url.StartsWith("/wiki/rest/api/content/11?"));
        var body = JsonNode.Parse(put.Body)!;
        Assert.Equal("<p>Hello.</p>", body["body"]!["storage"]!["value"]!.GetValue<string>().Split('\n')[0]);
        Assert.Null(body["ancestors"]); // An update never moves the page.
        var upload = _fake.Log.Single(l => l.Url.EndsWith("/content/11/child/attachment"));
        Assert.Equal("PUT", upload.Method);
        Assert.Equal("nocheck", upload.Request.Headers.GetValues("X-Atlassian-Token").Single());
        Assert.Contains("filename=main.png", upload.Body);
        Assert.Equal(1, _fake.Pages["21"]["version"]!.GetValue<int>()); // The same-titled page elsewhere is untouched.
    }

    [Fact]
    public async Task Creates_a_new_page_when_the_parent_has_no_such_child()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "1", "  New guide ");
        Assert.False(plan.IsUpdate);
        Assert.Equal("New guide", plan.Title);
        Assert.Contains("CREATE", plan.Describe());
        var url = await PagePublisher.ExecuteAsync(_client, plan);
        Assert.Equal("https://acme.atlassian.net/wiki/spaces/TEAM/pages/900", url);
        Assert.Equal("1", _fake.ParentOf["900"]);
        var post = JsonNode.Parse(_fake.Log.Single(l => l.Method == "POST").Body)!;
        Assert.Equal("TEAM", post["space"]!["key"]!.GetValue<string>());
        Assert.Equal("1", post["ancestors"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stops_without_writing_when_the_page_changed_after_confirming()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "10", _guide.Title);
        _fake.Pages["11"]["version"] = 6;
        _fake.Log.Clear();
        var e = await Assert.ThrowsAsync<ConfluenceException>(() => PagePublisher.ExecuteAsync(_client, plan));
        Assert.Contains("Nothing was written", e.Message);
        Assert.All(_fake.Log, l => Assert.Equal("GET", l.Method));
    }

    [Fact]
    public async Task Stops_without_writing_when_the_page_was_moved_after_confirming()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "10", _guide.Title);
        _fake.ParentOf["11"] = "20";
        _fake.Log.Clear();
        var e = await Assert.ThrowsAsync<ConfluenceException>(() => PagePublisher.ExecuteAsync(_client, plan));
        Assert.Contains("moved", e.Message);
        Assert.All(_fake.Log, l => Assert.Equal("GET", l.Method));
    }

    [Fact]
    public async Task Stops_when_the_page_was_moved_deeper_under_the_same_parent()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "10", _guide.Title);
        _fake.Add("12", "Sub", "10");
        _fake.ParentOf["11"] = "12";
        _fake.Log.Clear();
        await Assert.ThrowsAsync<ConfluenceException>(() => PagePublisher.ExecuteAsync(_client, plan));
        Assert.All(_fake.Log, l => Assert.Equal("GET", l.Method));
    }

    [Fact]
    public async Task Finds_the_page_to_update_on_a_later_batch_of_children()
    {
        for (var k = 0; k < 7; k++) _fake.Add("1" + k + "0", "Other " + k, "10");
        _fake.Add("19", "Zebra", "10");
        _fake.ChildPageSize = 3;
        var plan = await PagePublisher.PlanAsync(_client, _guide, "10", "zebra");
        Assert.Equal("19", plan.Existing?.Id);
    }

    [Fact]
    public async Task Stops_a_create_when_the_page_appeared_after_confirming()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "1", "New guide");
        _fake.Add("50", "New guide", "1");
        _fake.Log.Clear();
        var e = await Assert.ThrowsAsync<ConfluenceException>(() => PagePublisher.ExecuteAsync(_client, plan));
        Assert.Contains("Nothing was written", e.Message);
        Assert.All(_fake.Log, l => Assert.Equal("GET", l.Method));
    }

    [Fact]
    public async Task New_pages_use_the_current_editor()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "1", "New guide");
        await PagePublisher.ExecuteAsync(_client, plan);
        var post = JsonNode.Parse(_fake.Log.Single(l => l.Method == "POST").Body)!;
        Assert.Equal("v2", post["metadata"]!["properties"]!["editor"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failed_picture_says_the_text_is_saved_and_to_publish_again()
    {
        var plan = await PagePublisher.PlanAsync(_client, _guide, "10", _guide.Title);
        _fake.FailUploads = true;
        var e = await Assert.ThrowsAsync<ConfluenceException>(() => PagePublisher.ExecuteAsync(_client, plan));
        Assert.Contains("page text is saved", e.Message);
        Assert.Contains("main.png", e.Message);
        Assert.Contains("Publish again", e.Message);
    }

    [Fact]
    public async Task A_parent_link_from_another_site_is_refused()
    {
        var e = await Assert.ThrowsAsync<ConfluenceException>(() =>
            PagePublisher.PlanAsync(_client, _guide, "https://other.atlassian.net/wiki/spaces/X/pages/10/Tools", "x"));
        Assert.Contains("other.atlassian.net", e.Message);
        Assert.Empty(_fake.Log);
    }

    [Fact]
    public async Task Clear_messages_for_bad_sign_in_and_unknown_parent()
    {
        var missing = await Assert.ThrowsAsync<ConfluenceException>(() => PagePublisher.PlanAsync(_client, _guide, "424242", "x"));
        Assert.Contains("cannot find page 424242", missing.Message);
        await Assert.ThrowsAsync<ConfluenceException>(() => PagePublisher.PlanAsync(_client, _guide, "not a link", "x"));

        _fake.FailWith = HttpStatusCode.Unauthorized;
        var e = await Assert.ThrowsAsync<ConfluenceException>(() => _client.WhoAmIAsync());
        Assert.Contains("e-mail address and API token", e.Message);
        Assert.DoesNotContain("secret", e.Message);
    }

    [Fact]
    public void Cql_strings_are_escaped()
    {
        Assert.Equal("\"say \\\"hi\\\" \\\\ there\"", ConfluenceClient.CqlString(" say \"hi\" \\ there "));
    }
}
