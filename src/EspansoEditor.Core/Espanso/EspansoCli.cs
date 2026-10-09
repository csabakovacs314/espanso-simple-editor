using System.Diagnostics;

namespace EspansoEditor.Core.Espanso;

public enum EspansoState { Running, NotRunning, NotInstalled, Unknown }

/// <summary>What the status check found, plus a plain-language detail for the tooltip and the log.</summary>
public sealed record EspansoStatus(EspansoState State, string Detail);

/// <summary>
/// Talks to the espanso command line: where is the config, is it running, restart.
/// On Windows espanso is a window-less program (espansod.exe). When it is started hidden, the text it prints
/// can get lost, so nothing here depends on that text alone: exit codes and the running process are used too.
/// </summary>
public static class EspansoCli
{
    public sealed record CliResult(bool Started, int ExitCode, string Output);

    /// <summary>Receives one line per espanso call (command, exit code, output), for the diagnostics log.</summary>
    public static Action<string>? Log { get; set; }

    // Exit codes from Espanso's own source (espanso/src/exit_code.rs) and from cmd.exe.
    public const int ServiceSuccess = 0;
    public const int ServiceNotRunning = 4;
    public const int CmdNotRecognized = 9009;

    private static readonly string[] ProcessNames = { "espansod", "espanso" };

    /// <param name="capture">Read the command's output. Off for commands that start long-running processes
    /// (espanso restart), which would otherwise keep the output pipe open for as long as Espanso runs.</param>
    public static CliResult Run(string arguments, int timeoutMs = 5000, bool capture = true)
    {
        CliResult result;
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
            if (p == null)
            {
                result = new CliResult(false, -1, "");
            }
            else
            {
                var stdout = capture ? p.StandardOutput.ReadToEndAsync() : Task.FromResult("");
                var stderr = capture ? p.StandardError.ReadToEndAsync() : Task.FromResult("");
                if (!p.WaitForExit(timeoutMs))
                {
                    // Only stop the command wrapper; never the Espanso processes it may have started.
                    try { p.Kill(entireProcessTree: false); } catch { /* already gone */ }
                    result = new CliResult(true, -1, "timed out");
                }
                else
                {
                    // A child that inherited the pipe can keep it open after the command itself exits; do not wait for it forever.
                    var output = Task.WhenAll(stdout, stderr).Wait(2000) ? (stdout.Result + "\n" + stderr.Result).Trim() : "";
                    result = new CliResult(true, p.ExitCode, output);
                }
            }
        }
        catch (Exception ex)
        {
            result = new CliResult(false, -1, ex.Message);
        }
        Log?.Invoke($"espanso {arguments} -> started={result.Started} exit={result.ExitCode} output={Shorten(result.Output)}");
        return result;
    }

    /// <summary>
    /// Runs espanso with its output sent to a temporary file by cmd.exe. Used when the piped output comes back empty,
    /// which happens with the window-less espansod.exe.
    /// </summary>
    private static string RunToFile(string arguments, int timeoutMs = 5000)
    {
        if (!OperatingSystem.IsWindows()) return "";
        var tmp = Path.Combine(Path.GetTempPath(), "ese-espanso-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/d /c espanso {arguments} > \"{tmp}\" 2>&1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: false); } catch { /* already gone */ }
            }
            var text = File.Exists(tmp) ? File.ReadAllText(tmp).Trim() : "";
            Log?.Invoke($"espanso {arguments} (to file) -> output={Shorten(text)}");
            return text;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"espanso {arguments} (to file) -> failed: {ex.Message}");
            return "";
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* temp file */ }
        }
    }

    /// <summary>Pulls the config folder out of "espanso path" or "espanso path config" output.</summary>
    public static string? ParseConfigDir(string output, Func<string, bool>? exists = null)
    {
        exists ??= Directory.Exists;
        var lines = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var t in lines)
        {
            if (!t.StartsWith("Config:", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = t["Config:".Length..].Trim().Trim('"');
            if (exists(dir)) return dir;
        }
        // "espanso path config" prints just the folder.
        if (lines.Count == 1 && exists(lines[0].Trim('"'))) return lines[0].Trim('"');
        return null;
    }

    /// <summary>The config folder Espanso reports, or null.</summary>
    public static string? ConfigDirFromEspanso()
    {
        var dir = ParseConfigDir(Run("path").Output);
        if (dir != null) return dir;
        dir = ParseConfigDir(Run("path config").Output);
        if (dir != null) return dir;
        return ParseConfigDir(RunToFile("path"));
    }

    /// <summary>Usual places for the Espanso config folder.</summary>
    public static IEnumerable<string> CandidateDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".config", "espanso");
        if (!string.IsNullOrEmpty(appData)) yield return Path.Combine(appData, "espanso");
        yield return Path.Combine(home, "Library", "Application Support", "espanso");
    }

    /// <summary>Config folders next to a running Espanso (the portable edition keeps it in ".espanso").</summary>
    public static IEnumerable<string> PortableDirsFromRunningEspanso()
    {
        foreach (var name in ProcessNames)
        {
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in found)
            {
                string? exe = null;
                try { exe = p.MainModule?.FileName; }
                catch { /* access denied for some processes */ }
                finally { p.Dispose(); }
                var dir = exe == null ? null : Path.GetDirectoryName(exe);
                if (dir != null) yield return Path.Combine(dir, ".espanso");
            }
        }
    }

    public sealed record Located(string? Folder, string How);

    /// <summary>Finds the config folder: ask Espanso, then look next to the running Espanso, then in the usual places.</summary>
    public static Located Locate()
    {
        var fromCli = ConfigDirFromEspanso();
        if (fromCli != null) return new Located(fromCli, "reported by espanso path");
        foreach (var dir in PortableDirsFromRunningEspanso().Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(Path.Combine(dir, "match"))) return new Located(dir, "next to the running Espanso (portable)");
        foreach (var dir in CandidateDirs())
            if (Directory.Exists(Path.Combine(dir, "match"))) return new Located(dir, "found in the usual place");
        return new Located(null, "not found");
    }

    /// <summary>True when an Espanso process is running on this computer.</summary>
    public static bool IsProcessRunning()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                var found = Process.GetProcessesByName(name);
                var any = found.Length > 0;
                foreach (var p in found) p.Dispose();
                if (any) return true;
            }
            catch
            {
                // Process listing can fail in locked-down sessions; the command line check still runs.
            }
        }
        return false;
    }

    /// <summary>Is Espanso running? Checks the process list first, then "espanso status" by its exit code.</summary>
    public static EspansoStatus GetStatus()
    {
        if (IsProcessRunning()) return new EspansoStatus(EspansoState.Running, "The Espanso process is running.");
        var r = Run("status");
        return Interpret(r.Started, r.ExitCode, r.Output);
    }

    /// <summary>Turns an "espanso status" result into a state. Pure, so it can be tested.</summary>
    public static EspansoStatus Interpret(bool started, int exitCode, string output)
    {
        if (!started) return new EspansoStatus(EspansoState.NotInstalled, "The espanso command could not be started: " + output);
        if (exitCode == CmdNotRecognized) return new EspansoStatus(EspansoState.NotInstalled, "Windows does not know the espanso command (it is not on PATH).");
        if (output.Contains("not running", StringComparison.OrdinalIgnoreCase) || exitCode == ServiceNotRunning)
            return new EspansoStatus(EspansoState.NotRunning, "espanso status says it is not running.");
        if (exitCode == ServiceSuccess) return new EspansoStatus(EspansoState.Running, "espanso status says it is running.");
        if (exitCode == -1 && output == "timed out") return new EspansoStatus(EspansoState.Unknown, "espanso status did not answer in time.");
        return new EspansoStatus(EspansoState.Unknown, $"espanso status ended with code {exitCode}.");
    }

    public static CliResult Restart() => Run("restart", 20000, capture: false);

    private static string Shorten(string s)
    {
        s = s.Replace("\r", "").Replace("\n", " | ");
        return s.Length > 300 ? s[..297] + "..." : s;
    }
}
