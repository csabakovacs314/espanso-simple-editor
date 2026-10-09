using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace EspansoEditor.App;

public partial class App : Application
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EspansoSimpleEditor");

    private static readonly object LogLock = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        Core.Espanso.EspansoCli.Log = WriteCliLog;
        base.OnStartup(e);
    }

    /// <summary>Keeps a small log of every espanso command call, for troubleshooting detection.</summary>
    private static void WriteCliLog(string line)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataDir);
                var path = Path.Combine(DataDir, "espanso-cli.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}\n");
            }
        }
        catch
        {
            // Logging is best effort.
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = Path.Combine(DataDir, "error.log");
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(log, $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch
        {
            // Logging must never cause a second failure.
        }
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nYour files on disk were not changed by this error. Details are in:\n{log}",
            "Espanso Simple Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
