using EspansoEditor.Core.Yaml;

namespace EspansoEditor.Core.Model;

/// <summary>An Espanso configuration folder and all its match files.</summary>
public sealed class EspansoConfig
{
    private EspansoConfig(string configDir, string matchDir)
    {
        ConfigDir = configDir;
        MatchDir = matchDir;
    }

    public string ConfigDir { get; }
    public string MatchDir { get; }
    public List<MatchFile> Files { get; } = new();

    /// <summary>The delimiter used for group comments, taken from the user's own files (default "---").</summary>
    public string GroupDelimiter { get; private set; } = "---";

    public IEnumerable<Snippet> AllSnippets => Files.SelectMany(f => f.Snippets);

    public bool HasUnsavedChanges => Files.Any(f => f.HasChanges) || Files.Any(f => f.NewGroups.Count > 0 && f.Snippets.Any(s => f.NewGroups.Contains(s.Group)));

    public IReadOnlyDictionary<string, string> GlobalEcho =>
        Files.SelectMany(f => f.GlobalEcho).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);

    public IReadOnlySet<string> GlobalVarNames => Files.SelectMany(f => f.GlobalVarNames).ToHashSet();

    /// <summary>
    /// Opens a configuration. Accepts the Espanso config folder (the one holding match\) or the match folder itself.
    /// </summary>
    public static EspansoConfig Load(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder not found: {folder}");

        string configDir, matchDir;
        if (Directory.Exists(Path.Combine(folder, "match")))
        {
            configDir = folder;
            matchDir = Path.Combine(folder, "match");
        }
        else if (string.Equals(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), "match", StringComparison.OrdinalIgnoreCase))
        {
            matchDir = folder;
            configDir = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar)) ?? folder;
        }
        else if (Directory.EnumerateFiles(folder).Any(IsMatchFileName))
        {
            matchDir = folder;
            configDir = folder;
        }
        else
        {
            throw new InvalidOperationException($"No Espanso match folder found in {folder}. Pick the folder that contains \"match\".");
        }

        var cfg = new EspansoConfig(configDir, matchDir);
        var packagesDir = Path.Combine(matchDir, "packages") + Path.DirectorySeparatorChar;
        var paths = Directory.EnumerateFiles(matchDir, "*", SearchOption.AllDirectories)
            .Where(IsMatchFileName)
            .Select(p => (Path: p, Rel: Path.GetRelativePath(matchDir, p), IsPackage: p.StartsWith(packagesDir, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.IsPackage)
            .ThenBy(x => x.Rel.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(x => Path.GetFileNameWithoutExtension(x.Rel).Equals("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Rel, StringComparer.OrdinalIgnoreCase);

        foreach (var p in paths)
        {
            var bytes = System.IO.File.ReadAllBytes(p.Path);
            cfg.Files.Add(MatchFileParser.Parse(p.Path, p.Rel, bytes, p.IsPackage));
        }

        cfg.GroupDelimiter = cfg.Files.Select(f => f.MarkerDelimiter).FirstOrDefault(d => d != null) ?? "---";
        return cfg;
    }

    private static bool IsMatchFileName(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) || ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates an empty snippet in a file and group. It is dirty until saved.</summary>
    public Snippet NewSnippet(MatchFile file, string group)
    {
        if (file.IsReadOnly) throw new InvalidOperationException($"{file.RelativePath} is read-only.");
        var s = new Snippet { File = file, Group = group ?? "", IsDirty = true };
        file.Snippets.Add(s);
        return s;
    }

    public void Delete(Snippet s)
    {
        if (s.IsReadOnly) throw new InvalidOperationException("This snippet is read-only.");
        s.File.Snippets.Remove(s);
        if (s.OriginFile != null && !s.OriginFile.Removed.Contains(s)) s.OriginFile.Removed.Add(s);
    }

    /// <summary>Moves a snippet to another file and/or group.</summary>
    public void Move(Snippet s, MatchFile target, string group)
    {
        group ??= "";
        if (ReferenceEquals(s.File, target) && s.Group == group) return;
        if (target.IsReadOnly) throw new InvalidOperationException($"{target.RelativePath} is read-only.");
        if (s.IsReadOnly) throw new InvalidOperationException("This snippet is read-only.");

        if (!ReferenceEquals(s.File, target))
        {
            s.File.Snippets.Remove(s);
            target.Snippets.Add(s);
        }
        s.File = target;
        s.Group = group;

        // Keep the "removed from its original place" bookkeeping right.
        if (s.OriginFile != null)
        {
            var away = !ReferenceEquals(s.OriginFile, target) || s.OriginGroup != group;
            if (away && !s.OriginFile.Removed.Contains(s)) s.OriginFile.Removed.Add(s);
            if (!away) s.OriginFile.Removed.Remove(s);
        }
        s.IsDirty = true;
    }

    public void AddGroup(MatchFile file, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Group name is empty.");
        if (!file.GroupNames.Contains(name)) file.NewGroups.Add(name);
    }

    /// <summary>Re-reads one file from disk, dropping unsaved changes in it.</summary>
    public MatchFile Reload(MatchFile file)
    {
        var fresh = MatchFileParser.Parse(file.FullPath, file.RelativePath, System.IO.File.ReadAllBytes(file.FullPath), file.IsPackage);
        var i = Files.IndexOf(file);
        if (i >= 0) Files[i] = fresh;
        return fresh;
    }
}
