namespace Publisher.Core;

/// <summary>A Markdown guide ready to publish: its title, the page body and the pictures to attach.</summary>
public sealed class Guide
{
    public required string SourcePath { get; init; }
    public required string Title { get; init; }
    public required string Storage { get; init; }

    /// <summary>Pictures as (attachment name, full path on disk).</summary>
    public required IReadOnlyList<(string Name, string Path)> Images { get; init; }

    /// <summary>Pictures the text refers to that are not on disk.</summary>
    public required IReadOnlyList<string> MissingImages { get; init; }

    /// <summary>
    /// Reads a Markdown file. The first "# " heading is the page title and is left out of the body, because
    /// Confluence shows the title above the page itself.
    /// </summary>
    public static Guide Load(string path)
    {
        var md = File.ReadAllText(path).Replace("\r\n", "\n");
        var lines = md.Split('\n').ToList();
        var titleLine = -1;
        var inCode = false;
        for (var k = 0; k < lines.Count && titleLine < 0; k++)
        {
            if (lines[k].TrimStart().StartsWith("```")) inCode = !inCode;
            else if (!inCode && lines[k].StartsWith("# ")) titleLine = k;
        }
        var title = titleLine >= 0 ? lines[titleLine][2..].Trim().Replace("**", "") : Path.GetFileNameWithoutExtension(path);
        if (titleLine >= 0) lines.RemoveAt(titleLine);
        var body = string.Join("\n", lines);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var images = new List<(string, string)>();
        var missing = new List<string>();
        foreach (var rel in StorageFormat.ImagePaths(body))
        {
            var full = Path.GetFullPath(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(full)) images.Add((StorageFormat.AttachmentName(rel), full));
            else missing.Add(rel);
        }
        return new Guide
        {
            SourcePath = path,
            Title = title,
            Storage = StorageFormat.Convert(body),
            Images = images,
            MissingImages = missing,
        };
    }
}
