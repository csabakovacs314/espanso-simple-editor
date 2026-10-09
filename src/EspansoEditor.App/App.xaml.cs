using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace EspansoEditor.App;

public partial class App : Application
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EspansoSimpleEditor");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
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
