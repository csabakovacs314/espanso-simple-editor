using System.Text;

namespace Publisher.Core;

/// <summary>
/// What publishing will do, worked out before anything is written, so the user can confirm it:
/// under which parent, create or update, and which pictures go up.
/// </summary>
public sealed class PublishPlan
{
    public required Uri Site { get; init; }
    public required Guide Guide { get; init; }
    public required string Title { get; init; }
    public required PageInfo Parent { get; init; }

    /// <summary>The child of <see cref="Parent"/> with the same title, which will be updated. Null: a new page is created.</summary>
    public PageInfo? Existing { get; init; }

    /// <summary>Pages with the same title elsewhere in the space. They are never touched, but the user should know.</summary>
    public IReadOnlyList<PageInfo> SameTitleElsewhere { get; init; } = [];

    public bool IsUpdate => Existing != null;

    /// <summary>Where the page will be: "Space › … › Parent › Title".</summary>
    public string TargetPath => Parent.Path + " › " + Title;

    /// <summary>The confirmation text: everything the user agrees to, in plain words.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(IsUpdate ? "UPDATE an existing page" : "CREATE a new page");
        sb.AppendLine();
        sb.AppendLine($"Site:    {Site.Host}");
        sb.AppendLine($"Space:   {Parent.SpaceName} ({Parent.SpaceKey})");
        sb.AppendLine($"Parent:  {Parent.Path}");
        sb.AppendLine($"Title:   {Title}");
        if (Existing != null)
            sb.AppendLine($"Page:    version {Existing.Version} becomes version {Existing.Version + 1}. Its current text is replaced; Confluence keeps the old version in the page history.");
        sb.AppendLine($"Pictures: {Guide.Images.Count} attached" + (Guide.Images.Count > 0 ? " (" + string.Join(", ", Guide.Images.Select(i => i.Name)) + ")" : ""));
        if (Guide.MissingImages.Count > 0)
            sb.AppendLine($"Missing pictures (will show as broken): {string.Join(", ", Guide.MissingImages)}");
        if (SameTitleElsewhere.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Note: pages with this title already exist elsewhere in the space and are left alone:");
            foreach (var p in SameTitleElsewhere.Take(5)) sb.AppendLine("  " + p.Path);
            if (!IsUpdate) sb.AppendLine("If this site does not allow two pages with the same title, Confluence will refuse; change the title then.");
        }
        sb.AppendLine();
        sb.AppendLine($"Source:  {Guide.SourcePath}");
        return sb.ToString().TrimEnd();
    }
}

public static class PagePublisher
{
    /// <summary>
    /// Looks up the parent and checks whether it already has a child page with this title. Reads only; writes nothing.
    /// </summary>
    public static async Task<PublishPlan> PlanAsync(ConfluenceClient client, Guide guide, string parentLinkOrId, string title, CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length == 0) throw new ConfluenceException("The page needs a title.");
        if (title.Length > 255) throw new ConfluenceException("Confluence titles can be at most 255 characters.");
        var parentId = PageLink.IdFrom(parentLinkOrId)
                       ?? throw new ConfluenceException("Pick the parent page: paste its link or ID, or search for it.");
        if (parentLinkOrId.Contains("://") && PageLink.Site(parentLinkOrId) is { } linkSite
            && !string.Equals(linkSite.Host, client.Site.Host, StringComparison.OrdinalIgnoreCase))
            throw new ConfluenceException($"The parent link is on {linkSite.Host}, but you are signed in to {client.Site.Host}. Use a page from {client.Site.Host}, or change the site.");
        var parent = await client.GetPageAsync(parentId, ct);

        var existing = await SameTitleChildAsync(client, parent.Id, title, ct);
        // A full page read of the match, so the version number is the one the update must follow.
        if (existing != null) existing = await client.GetPageAsync(existing.Id, ct);

        var elsewhere = (await client.FindByTitleAsync(parent.SpaceKey, title, ct))
            .Where(p => p.Id != existing?.Id && p.Id != parent.Id).ToList();

        return new PublishPlan
        {
            Site = client.Site,
            Guide = guide,
            Title = title,
            Parent = parent,
            Existing = existing,
            SameTitleElsewhere = elsewhere,
        };
    }

    private static async Task<PageInfo?> SameTitleChildAsync(ConfluenceClient client, string parentId, string title, CancellationToken ct) =>
        (await client.ChildrenAsync(parentId, ct)).FirstOrDefault(c => string.Equals(c.Title.Trim(), title, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Does exactly what the confirmed plan says. Before an update it checks the page has not changed since the plan
    /// was made; if it has, it stops without writing so the user can look again.
    /// Returns the page link.
    /// </summary>
    public static async Task<string> ExecuteAsync(ConfluenceClient client, PublishPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        PageInfo page;
        if (plan.Existing is { } existing)
        {
            progress?.Report($"Checking \"{existing.Title}\" is still version {existing.Version}…");
            var now = await client.GetPageAsync(existing.Id, ct);
            if (now.Version != existing.Version)
                throw new ConfluenceException($"The page changed after you confirmed (now version {now.Version}). Nothing was written. Check again and publish once more.");
            if (now.ParentId != plan.Parent.Id)
                throw new ConfluenceException("The page was moved after you confirmed. Nothing was written. Check again and publish once more.");
            progress?.Report($"Updating \"{plan.Title}\" to version {existing.Version + 1}…");
            page = await client.UpdateAsync(now, plan.Title, plan.Guide.Storage, "Published from " + Path.GetFileName(plan.Guide.SourcePath), ct);
        }
        else
        {
            if (await SameTitleChildAsync(client, plan.Parent.Id, plan.Title, ct) is { } appeared)
                throw new ConfluenceException($"\"{appeared.Title}\" appeared under {plan.Parent.Title} after you confirmed. Nothing was written. Check again and publish once more.");
            progress?.Report($"Creating \"{plan.Title}\" under {plan.Parent.Title}…");
            page = await client.CreateAsync(plan.Parent.SpaceKey, plan.Parent.Id, plan.Title, plan.Guide.Storage, ct);
        }

        var n = 0;
        foreach (var (name, path) in plan.Guide.Images)
        {
            progress?.Report($"Picture {++n} of {plan.Guide.Images.Count}: {name}");
            try
            {
                await client.UploadAttachmentAsync(page.Id, name, path, ct);
            }
            catch (Exception e) when (e is ConfluenceException or IOException)
            {
                throw new ConfluenceException($"The page text is saved ({page.WebUrl}), but picture {name} could not be uploaded, so pictures from it on are missing. "
                                              + $"Publish again to finish (it will update the same page).\n\n{e.Message}");
            }
        }
        progress?.Report("Done: " + page.WebUrl);
        return page.WebUrl;
    }
}
