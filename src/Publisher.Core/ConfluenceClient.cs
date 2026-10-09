using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Publisher.Core;

/// <summary>A Confluence page as the publisher needs it.</summary>
/// <param name="Ancestors">Titles from the top of the space down to the direct parent.</param>
/// <param name="ParentId">The direct parent's ID; null for a page at the top of its space.</param>
public sealed record PageInfo(string Id, string Title, string SpaceKey, string SpaceName,
    IReadOnlyList<string> Ancestors, string? ParentId, int Version, string WebUrl)
{
    /// <summary>"Space › Parent › … › This page", the way the confirmation shows it.</summary>
    public string Path => string.Join(" › ", new[] { SpaceName }.Concat(Ancestors).Append(Title));
}

public sealed class ConfluenceException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// The few Confluence Cloud REST calls the publisher uses (API v1 under /wiki/rest/api). Signs in with the
/// Atlassian account e-mail and an API token (Basic auth). The token only ever goes into the Authorization header.
/// </summary>
public sealed class ConfluenceClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _site;

    public Uri Site => _site;

    public ConfluenceClient(Uri site, string email, string apiToken, HttpMessageHandler? handler = null)
    {
        _site = site;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(site, "/wiki/rest/api/");
        _http.Timeout = TimeSpan.FromSeconds(100);
        var basic = System.Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EspansoConfluencePublisher/0.1");
    }

    public void Dispose() => _http.Dispose();

    private const string PageExpand = "space,ancestors,version";

    /// <summary>The signed-in user's display name. Fails with a clear message when the e-mail or token is wrong.</summary>
    public async Task<string> WhoAmIAsync(CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, "user/current", null, ct);
        var name = json?["displayName"]?.GetValue<string>();
        if (json?["type"]?.GetValue<string>() == "anonymous" || string.IsNullOrEmpty(name))
            throw new ConfluenceException("Confluence did not accept the sign-in. Check the e-mail address and API token.", HttpStatusCode.Unauthorized);
        return name;
    }

    public async Task<PageInfo> GetPageAsync(string id, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, $"content/{Uri.EscapeDataString(id)}?expand={PageExpand}", null, ct, notFound: "page " + id);
        if (json?["type"]?.GetValue<string>() is { } type && type != "page")
            throw new ConfluenceException($"{id} is a {type}, not a page. Pick a page as the parent.");
        return Page(json!);
    }

    /// <summary>Pages whose title matches the words typed, newest first. Optionally only in one space.</summary>
    public async Task<IReadOnlyList<PageInfo>> SearchAsync(string text, string? spaceKey = null, int limit = 20, CancellationToken ct = default)
    {
        var cql = $"type=page AND title ~ {CqlString(text)}";
        if (!string.IsNullOrWhiteSpace(spaceKey)) cql += $" AND space = {CqlString(spaceKey.Trim())}";
        cql += " ORDER BY lastmodified DESC";
        var json = await SendAsync(HttpMethod.Get, $"content/search?cql={Uri.EscapeDataString(cql)}&limit={limit}&expand={PageExpand}", null, ct);
        return Results(json);
    }

    /// <summary>The direct child pages of a page (all of them, following the paging).</summary>
    public async Task<IReadOnlyList<PageInfo>> ChildrenAsync(string parentId, CancellationToken ct = default)
    {
        var all = new List<PageInfo>();
        // Confluence may send fewer than asked for, so move on by what came back and stop only when there is no next batch.
        for (var start = 0; start < 5000;)
        {
            var json = await SendAsync(HttpMethod.Get,
                $"content/{Uri.EscapeDataString(parentId)}/child/page?limit=100&start={start}&expand={PageExpand}", null, ct, notFound: "page " + parentId);
            var count = json?["results"]?.AsArray().Count ?? 0;
            all.AddRange(Results(json));
            if (count == 0 || json?["_links"]?["next"] == null) break;
            start += count;
        }
        return all;
    }

    /// <summary>Pages in a space with exactly this title.</summary>
    public async Task<IReadOnlyList<PageInfo>> FindByTitleAsync(string spaceKey, string title, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get,
            $"content?type=page&spaceKey={Uri.EscapeDataString(spaceKey)}&title={Uri.EscapeDataString(title)}&expand={PageExpand}", null, ct);
        return Results(json);
    }

    public async Task<PageInfo> CreateAsync(string spaceKey, string parentId, string title, string storage, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["type"] = "page",
            ["title"] = title,
            ["space"] = new JsonObject { ["key"] = spaceKey },
            ["ancestors"] = new JsonArray(new JsonObject { ["id"] = parentId }),
            ["body"] = Storage(storage),
            // Open in the current Confluence editor rather than the legacy one.
            ["metadata"] = new JsonObject
            {
                ["properties"] = new JsonObject { ["editor"] = new JsonObject { ["key"] = "editor", ["value"] = "v2" } },
            },
        };
        var json = await SendAsync(HttpMethod.Post, $"content?expand={PageExpand}", body, ct);
        return Page(json!);
    }

    /// <summary>Replaces the page's text. Fails if someone saved the page after <paramref name="current"/> was read.</summary>
    public async Task<PageInfo> UpdateAsync(PageInfo current, string title, string storage, string message, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["id"] = current.Id,
            ["type"] = "page",
            ["title"] = title,
            ["space"] = new JsonObject { ["key"] = current.SpaceKey },
            ["body"] = Storage(storage),
            ["version"] = new JsonObject { ["number"] = current.Version + 1, ["message"] = message },
        };
        var json = await SendAsync(HttpMethod.Put, $"content/{Uri.EscapeDataString(current.Id)}?expand={PageExpand}", body, ct, notFound: "page " + current.Id);
        return Page(json!);
    }

    /// <summary>Adds a file to a page, or a new version of it when the page already has a file with that name.</summary>
    public async Task UploadAttachmentAsync(string pageId, string name, string path, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
        file.Headers.ContentType = new MediaTypeHeaderValue(MediaType(name));
        form.Add(file, "file", name);
        form.Add(new StringContent("true"), "minorEdit");
        using var req = new HttpRequestMessage(HttpMethod.Put, $"content/{Uri.EscapeDataString(pageId)}/child/attachment") { Content = form };
        req.Headers.Add("X-Atlassian-Token", "nocheck");
        await SendAsync(req, ct, "page " + pageId);
    }

    private static string MediaType(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    private static JsonObject Storage(string storage) => new()
    {
        ["storage"] = new JsonObject { ["value"] = storage, ["representation"] = "storage" },
    };

    /// <summary>A CQL string literal: double quotes, with backslashes and quotes escaped.</summary>
    public static string CqlString(string text) => "\"" + text.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private IReadOnlyList<PageInfo> Results(JsonNode? json) =>
        json?["results"]?.AsArray().Where(r => r != null && r["type"]?.GetValue<string>() == "page").Select(r => Page(r!)).ToList()
        ?? new List<PageInfo>();

    private PageInfo Page(JsonNode n)
    {
        var chain = n["ancestors"]?.AsArray().Where(a => a != null).ToList() ?? new List<JsonNode?>();
        var ancestors = chain.Select(a => a?["title"]?.GetValue<string>() ?? "?").ToList();
        var parentId = chain.Count > 0 ? chain[^1]?["id"]?.GetValue<string>() : null;
        var webui = n["_links"]?["webui"]?.GetValue<string>() ?? "";
        return new PageInfo(
            n["id"]?.GetValue<string>() ?? throw new ConfluenceException("Confluence sent a page without an ID."),
            n["title"]?.GetValue<string>() ?? "",
            n["space"]?["key"]?.GetValue<string>() ?? "",
            n["space"]?["name"]?.GetValue<string>() ?? n["space"]?["key"]?.GetValue<string>() ?? "",
            ancestors,
            parentId,
            n["version"]?["number"]?.GetValue<int>() ?? 0,
            webui.Length == 0 ? "" : _site.ToString().TrimEnd('/') + "/wiki" + webui);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, string? notFound = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(req, ct, notFound);
    }

    private async Task<JsonNode?> SendAsync(HttpRequestMessage req, CancellationToken ct, string? notFound)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ConfluenceException($"Could not reach {_site.Host}: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConfluenceException($"{_site.Host} did not answer in time.");
        }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                try
                {
                    return JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    throw new ConfluenceException($"{_site.Host} did not answer like Confluence Cloud. Check the site address.");
                }
            }
            throw new ConfluenceException(Explain(resp.StatusCode, ServerMessage(text), notFound), resp.StatusCode);
        }
    }

    private static string? ServerMessage(string text)
    {
        try
        {
            var msg = JsonNode.Parse(text)?["message"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(msg) ? null : msg.Length > 300 ? msg[..300] + "…" : msg;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Explain(HttpStatusCode status, string? server, string? notFound)
    {
        var why = status switch
        {
            HttpStatusCode.Unauthorized => "Confluence did not accept the sign-in. Check the e-mail address and API token.",
            HttpStatusCode.Forbidden => "Your account is not allowed to do this here (no permission for that space or page).",
            HttpStatusCode.NotFound => notFound != null
                ? $"Confluence cannot find {notFound}, or your account cannot see it."
                : "Confluence cannot find that. Check the site address.",
            HttpStatusCode.Conflict => "Someone changed the page in the meantime. Check again and publish once more.",
            HttpStatusCode.TooManyRequests => "Confluence asks to slow down. Wait a minute and try again.",
            _ => $"Confluence answered {(int)status} {status}.",
        };
        return server == null ? why : $"{why}\n\nConfluence says: {server}";
    }
}
