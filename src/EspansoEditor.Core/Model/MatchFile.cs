namespace EspansoEditor.Core.Model;

/// <summary>A group marker comment line in a match file, e.g. "  # --- Customer Communication ---".</summary>
public sealed record GroupMarker(string Name, int LineStart, int LineEnd);

/// <summary>One Espanso match file (a .yml under match\).</summary>
public sealed class MatchFile
{
    public MatchFile(string fullPath, string relativePath, bool isPackage)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        IsPackage = isPackage;
    }

    public string FullPath { get; }

    /// <summary>Path relative to the match folder, e.g. "base.yml" or "packages\greetings\package.yml".</summary>
    public string RelativePath { get; }

    public bool IsPackage { get; }

    /// <summary>Files whose name starts with "_" are only loaded by Espanso when another file imports them.</summary>
    public bool IsUnderscore => Path.GetFileName(FullPath).StartsWith('_');

    /// <summary>"base", "_private", "work\tickets", or the package folder name for packages.</summary>
    public string DisplayName
    {
        get
        {
            if (IsPackage)
            {
                var dir = Path.GetFileName(Path.GetDirectoryName(FullPath) ?? "");
                return string.IsNullOrEmpty(dir) ? Path.GetFileNameWithoutExtension(FullPath) : dir;
            }
            var rel = RelativePath;
            var ext = Path.GetExtension(rel);
            return ext.Length > 0 ? rel[..^ext.Length] : rel;
        }
    }

    /// <summary>Why this file cannot be edited (package, unreadable, unusual layout). Null when editable.</summary>
    public string? ReadOnlyReason { get; internal set; }

    /// <summary>Set when the YAML could not be read at all.</summary>
    public string? LoadError { get; internal set; }

    public bool IsReadOnly => ReadOnlyReason != null;

    /// <summary>File text with line endings normalised to \n.</summary>
    public string Text { get; internal set; } = "";

    /// <summary>The line ending the file uses on disk ("\r\n" or "\n"); kept on save.</summary>
    public string Newline { get; internal set; } = "\n";

    public bool HasBom { get; internal set; }

    /// <summary>SHA-256 of the bytes on disk when loaded; used to spot changes made by someone else.</summary>
    public string DiskHash { get; internal set; } = "";

    /// <summary>Snippets in this file, in file order (new ones at the end).</summary>
    public List<Snippet> Snippets { get; } = new();

    /// <summary>Snippets that were in this file on disk but have been deleted or moved elsewhere.</summary>
    public List<Snippet> Removed { get; } = new();

    public List<GroupMarker> Groups { get; } = new();

    /// <summary>Groups created in the editor that do not exist in the file yet.</summary>
    public List<string> NewGroups { get; } = new();

    /// <summary>Global echo variables (name to value), for showing chip values.</summary>
    public Dictionary<string, string> GlobalEcho { get; } = new();

    public HashSet<string> GlobalVarNames { get; } = new();

    // Layout facts used when writing (all offsets into Text).
    internal bool HasMatchesKey { get; set; }
    internal int MatchesKeyLineEnd { get; set; }
    internal int ItemIndent { get; set; } = 2;
    internal bool BlankLineBetweenItems { get; set; } = true;
    internal string RootCanonicalWithoutMatches { get; set; } = "";
    internal string? MarkerDelimiter { get; set; }

    public IEnumerable<string> GroupNames =>
        Groups.Select(g => g.Name)
            .Concat(NewGroups)
            .Concat(Snippets.Select(s => s.Group).Where(g => g.Length > 0))
            .Distinct();

    public bool HasChanges =>
        Removed.Count > 0
        || Snippets.Any(s => s.IsDirty || s.IsNew || !ReferenceEquals(s.OriginFile, this) || s.OriginGroup != s.Group);

    public override string ToString() => RelativePath;
}
