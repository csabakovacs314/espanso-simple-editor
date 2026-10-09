using System.Diagnostics;

namespace EspansoEditor.Core.Espanso;

/// <summary>Talks to the espanso command line: where is the config, is it running, restart.</summary>
public static class EspansoCli
{
    public sealed record CliResult(bool Started, int ExitCode, string Output);

    /// <param name="capture">Read the command's output. Off for commands that start long-running processes
    /// (espanso restart), which would otherwise keep the output pipe open for as long as Espanso runs.</param>
    public static CliResult Run(string arguments, int timeoutMs = 5000, bool capture = true)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/d /c espanso " + arguments)
                : new ProcessStartInfo("espanso", arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = capture;
            psi.RedirectStandardError = capture;
            using var p = Process.Start(psi);
            if (p == null) return new CliResult(false, -1, "");
            var stdout = capture ? p.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            var stderr = capture ? p.StandardError.ReadToEndAsync() : Task.FromResult("");
            if (!p.WaitForExit(timeoutMs))
            {
                // Only stop the command wrapper; never the Espanso processes it may have started.
                try { p.Kill(entireProcessTree: false); } catch { /* already gone */ }
                return new CliResult(true, -1, "timed out");
            }
            // A child that inherited the pipe can keep it open after the command itself exits; do not wait for it forever.
            var output = Task.WhenAll(stdout, stderr).Wait(2000) ? (stdout.Result + "\n" + stderr.Result).Trim() : "";
            return new CliResult(true, p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return new CliResult(false, -1, ex.Message);
        }
    }

    /// <summary>The config folder Espanso reports with "espanso path", or null.</summary>
    public static string? ConfigDirFromEspanso()
    {
        var r = Run("path");
        if (!r.Started || r.ExitCode != 0) return null;
        foreach (var line in r.Output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Config:", StringComparison.OrdinalIgnoreCase))
            {
                var dir = t["Config:".Length..].Trim();
                if (Directory.Exists(dir)) return dir;
            }
        }
        return null;
    }

    /// <summary>Usual places for the Espanso config folder, in the order Espanso itself prefers.</summary>
    public static IEnumerable<string> CandidateDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".config", "espanso");
        if (!string.IsNullOrEmpty(appData)) yield return Path.Combine(appData, "espanso");
        yield return Path.Combine(home, "Library", "Application Support", "espanso");
    }

    public sealed record Located(string? Folder, string How);

    /// <summary>Finds the config folder: ask Espanso first, then look in the usual places.</summary>
    public static Located Locate()
    {
        var fromCli = ConfigDirFromEspanso();
        if (fromCli != null) return new Located(fromCli, "reported by espanso path");
        foreach (var dir in CandidateDirs())
            if (Directory.Exists(Path.Combine(dir, "match"))) return new Located(dir, "found in the usual place");
        return new Located(null, "not found");
    }

    /// <summary>True/false when espanso answered, null when the command is not available.</summary>
    public static bool? IsRunning()
    {
        var r = Run("status");
        if (!r.Started || (r.ExitCode != 0 && r.Output.Length == 0)) return null;
        if (r.Output.Contains("not running", StringComparison.OrdinalIgnoreCase)) return false;
        if (r.Output.Contains("running", StringComparison.OrdinalIgnoreCase)) return true;
        return null;
    }

    public static CliResult Restart() => Run("restart", 20000, capture: false);
}
