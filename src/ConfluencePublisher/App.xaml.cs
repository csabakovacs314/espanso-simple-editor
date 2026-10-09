using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ConfluencePublisher;

public partial class App : Application
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConfluencePublisher");

    private const string DarkControls = "pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml";

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            Resources.MergedDictionaries.Insert(0, new ResourceDictionary { Source = new Uri(DarkControls, UriKind.Absolute) });
        }
        catch (Exception ex)
        {
            Log("Dark control theme could not be loaded:\n" + ex);
        }
        base.OnStartup(e);
    }

    /// <summary>Error log. Only exception text goes here; the API token never does.</summary>
    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:O}\n{text}\n\n");
        }
        catch
        {
            // Logging is best effort.
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception.ToString());
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in:\n{Path.Combine(DataDir, "error.log")}",
            "Confluence Publisher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
