using System.Windows;
using System.Windows.Input;
using EspansoEditor.Core.Model;

namespace EspansoEditor.App;

/// <summary>One snippet in its own window. Edits go straight into the same snippet the main window shows.</summary>
public partial class SnippetWindow : Window
{
    private readonly IEditorHost _host;

    public SnippetWindow(Snippet snippet, IEditorHost host)
    {
        InitializeComponent();
        _host = host;
        Editor = new SnippetEditor(snippet, host);
        Editor.SetPoppedOut(true);
        Host.Content = Editor;
        UpdateTitle();
    }

    public SnippetEditor Editor { get; }

    public void UpdateTitle()
    {
        var s = Editor.Snippet;
        var dirty = s.IsDirty || s.IsNew || s.IsMoved ? " •" : "";
        var where = s.Group.Length > 0 ? $"{s.File.DisplayName} › {s.Group}" : s.File.DisplayName;
        Title = $"{s.DisplayName}{dirty} · Espanso Simple Editor";
        HeaderText.Text = $"{s.DisplayName}{dirty}   ·   {where}";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => _host.SaveAll();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _host.SaveAll();
            e.Handled = true;
        }
    }
}
