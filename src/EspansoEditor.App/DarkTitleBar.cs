using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EspansoEditor.App;

/// <summary>Asks Windows for a dark title bar, to match the dark window (Windows 10 20H1 and later, Windows 11).</summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeOld = 19; // Windows 10 before 20H1

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var on = 1;
                if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeOld, ref on, sizeof(int));
            }
            catch
            {
                // Older Windows: the title bar simply stays light.
            }
        };
    }
}
