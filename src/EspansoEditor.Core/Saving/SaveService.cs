using System.Text;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Validation;
using EspansoEditor.Core.Yaml;

namespace EspansoEditor.Core.Saving;

public sealed class SaveOptions
{
    /// <summary>Folder that receives a timestamped copy of every file before it is overwritten.</summary>
    public required string BackupRoot { get; init; }

    /// <summary>How many backup folders to keep.</summary>
    public int KeepBackups { get; init; } = 30;

    /// <summary>Write even if a file changed on disk since it was opened (it is still backed up first).</summary>
    public bool OverwriteChangedFiles { get; init; }
}

public sealed class SaveResult
{
    public bool Success => Errors.Count == 0 && Conflicts.Count == 0;
    public List<Issue> BlockingIssues { get; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>Files that changed on disk since they were opened. Nothing was written.</summary>
    public List<MatchFile> Conflicts { get; } = new();

    public List<MatchFile> SavedFiles { get; } = new();
    public int SavedSnippets { get; set; }
    public string? BackupFolder { get; set; }
}

/// <summary>
/// The safe save: check, build each changed file, read it back and compare, refuse if a file changed on disk,
/// back up, then replace each file in one step.
/// </summary>
public static class SaveService
{
    public static string DefaultBackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EspansoSimpleEditor", "backups");

    public static SaveResult Save(EspansoConfig cfg, SaveOptions options)
    {
        var result = new SaveResult();
        var affected = cfg.Files.Where(f => f.HasChanges).ToList();
        if (affected.Count == 0) return result;

        // 1. Check snippets that will be written.
        var touched = affected.SelectMany(f => f.Snippets).Where(s => s.IsDirty || s.IsNew || s.IsMoved).ToHashSet();
        result.BlockingIssues.AddRange(Validator.Check(cfg).Where(i => i.Severity == Severity.Error && touched.Contains(i.Snippet)));
        if (result.BlockingIssues.Count > 0)
        {
            result.Errors.Add("Some snippets need fixing before they can be saved.");
            return result;
        }

        // 2. Build and verify every file before writing any. First with the smallest possible edits (a whole-word
        //    switch is one line), then rewriting changed snippets, then, for rare layouts where a "|" block would not
        //    read back right, writing changed text as quoted strings.
        var plan = new List<(MatchFile File, string NewText, IReadOnlySet<Snippet> Reemitted)>();
        var globals = cfg.GlobalVarNames;
        foreach (var f in affected)
        {
            string? problem = null;
            foreach (var (minimal, quoted) in new[] { (true, false), (false, false), (false, true) })
            {
                try
                {
                    var r = FileRewriter.RewriteWithInfo(f, globals, cfg.GroupDelimiter, forceQuoted: quoted, minimal: minimal);
                    problem = Verifier.Verify(f, r.Text, r.Reemitted);
                    if (problem == null)
                    {
                        plan.Add((f, r.Text, r.Reemitted));
                        break;
                    }
                }
                catch (Exception ex)
                {
                    problem = $"{f.RelativePath}: {ex.Message}";
                }
            }
            if (problem != null) result.Errors.Add(problem);
        }
        if (result.Errors.Count > 0) return result;

        // 3. Did anything change on disk meanwhile?
        foreach (var (f, _, _) in plan)
        {
            var current = System.IO.File.Exists(f.FullPath) ? MatchFileParser.Hash(System.IO.File.ReadAllBytes(f.FullPath)) : "";
            if (current != f.DiskHash) result.Conflicts.Add(f);
        }
        if (result.Conflicts.Count > 0 && !options.OverwriteChangedFiles) return result;
        result.Conflicts.Clear();

        // 4. Back up what is on disk now.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupDir = Path.Combine(options.BackupRoot, stamp);
        try
        {
            foreach (var (f, _, _) in plan)
            {
                if (!System.IO.File.Exists(f.FullPath)) continue;
                var target = Path.Combine(backupDir, f.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Copy(f.FullPath, target, overwrite: true);
            }
            result.BackupFolder = backupDir;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Could not make the backup, so nothing was saved: {ex.Message}");
            return result;
        }

        // 5. Write every new file next to its target first, then swap them in. Files that gain snippets are swapped
        //    first, so an interrupted save can at worst leave a moved snippet in both files, never in neither.
        var ordered = plan.OrderBy(p => p.File.Snippets.Any(s => s.IsNew || s.IsMoved) ? 0 : 1).ToList();
        var staged = new List<(MatchFile File, string NewText, string Target, string Tmp, byte[] Bytes)>();
        try
        {
            foreach (var (f, newText, _) in ordered)
            {
                var target = ResolveTarget(f.FullPath);
                var bytes = Encode(newText, f);
                var tmp = target + ".ese-tmp";
                System.IO.File.WriteAllBytes(tmp, bytes);
                staged.Add((f, newText, target, tmp, bytes));
            }
        }
        catch (Exception ex)
        {
            foreach (var st in staged) TryDelete(st.Tmp);
            result.Errors.Add($"Could not write the new files, so nothing was changed: {ex.Message}");
            return result;
        }

        var swapped = 0;
        try
        {
            foreach (var st in staged)
            {
                System.IO.File.Move(st.Tmp, st.Target, overwrite: true);
                swapped++;
            }
        }
        catch (Exception ex)
        {
            foreach (var st in staged.Skip(swapped)) TryDelete(st.Tmp);
            result.Errors.Add(swapped == 0
                ? $"Could not replace {staged[0].File.RelativePath}, so nothing was changed: {ex.Message}"
                : $"Only part of the save went through ({ex.Message}). Press Ctrl+S again and choose to save anyway; the backup is in {backupDir}.");
            return result;
        }

        // 6. Everything is on disk: take over the new positions.
        foreach (var st in staged)
        {
            var f = st.File;
            result.SavedSnippets += f.Snippets.Count(s => s.IsDirty || s.IsNew || s.IsMoved) + f.Removed.Count(r => !cfg.AllSnippets.Contains(r));
            result.SavedFiles.Add(f);
        }
        foreach (var st in staged) Adopt(st.File, st.NewText, st.Bytes);

        Prune(options.BackupRoot, options.KeepBackups);
        return result;
    }

    /// <summary>Writes through a symbolic link to the real file, so the link itself survives.</summary>
    private static string ResolveTarget(string path)
    {
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch
        {
            return path;
        }
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch { /* best effort */ }
    }

    private static byte[] Encode(string text, MatchFile f)
    {
        var withEndings = f.Newline == "\n" ? text : text.Replace("\n", f.Newline);
        var body = new UTF8Encoding(false).GetBytes(withEndings);
        if (!f.HasBom) return body;
        var bytes = new byte[body.Length + 3];
        bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
        body.CopyTo(bytes, 3);
        return bytes;
    }

    /// <summary>After a successful write, take over positions and names from the file as written.</summary>
    private static void Adopt(MatchFile f, string newText, byte[] bytes)
    {
        var back = MatchFileParser.ParseText(f.FullPath, f.RelativePath, newText, f.IsPackage);
        var pool = back.Snippets.ToList();
        foreach (var s in f.Snippets)
        {
            var sig = s.SemanticSignature();
            var i = pool.FindIndex(a => a.SemanticSignature() == sig);
            if (i < 0) continue;
            var a = pool[i];
            pool.RemoveAt(i);
            s.Span = a.Span;
            s.OriginFile = f;
            s.OriginGroup = s.Group;
            s.OriginalCanonical = a.OriginalCanonical;
            s.OriginalValueCanonical = a.OriginalValueCanonical;
            s.EndsWithBlockScalar = a.EndsWithBlockScalar;
            s.OriginalSignature = a.OriginalSignature;
            s.OriginalWord = a.OriginalWord;
            s.WordLayout = a.WordLayout;
            s.PreservedComments.Clear();
            s.PreservedComments.AddRange(a.PreservedComments);
            if (!s.IsAdvanced) s.Body = a.Body; // picks up the variable names that were written
            s.IsDirty = false;
        }
        foreach (var r in f.Removed.ToList())
        {
            // Moved-away snippets now belong to their new file; deleted ones are gone.
            f.Removed.Remove(r);
        }
        f.Text = back.Text;
        f.DiskHash = MatchFileParser.Hash(bytes);
        f.HasMatchesKey = back.HasMatchesKey;
        f.MatchesKeyLineEnd = back.MatchesKeyLineEnd;
        f.ItemIndent = back.ItemIndent;
        f.BlankLineBetweenItems = back.BlankLineBetweenItems;
        f.RootCanonicalWithoutMatches = back.RootCanonicalWithoutMatches;
        f.MarkerDelimiter = back.MarkerDelimiter;
        f.Groups.Clear();
        f.Groups.AddRange(back.Groups);
        f.NewGroups.RemoveAll(g => f.Groups.Any(m => m.Name == g));
    }

    private static void Prune(string root, int keep)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var dirs = Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
            foreach (var d in dirs.Skip(Math.Max(keep, 1))) Directory.Delete(d, recursive: true);
        }
        catch
        {
            // Pruning is housekeeping; never fail a save over it.
        }
    }
}
