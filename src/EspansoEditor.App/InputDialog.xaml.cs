using System.Windows;
using System.Windows.Controls;

namespace EspansoEditor.App;

/// <summary>A small "type a value" dialog with optional live preview, hint, choices and validation.</summary>
public partial class InputDialog : Window
{
    private Func<string, string>? _preview;
    private Func<string, string?>? _validate;
    private bool _useOptions;

    public InputDialog()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    public string Value => _useOptions ? OptionsBox.Text : ValueBox.Text;

    public static string? Ask(
        Window? owner,
        string title,
        string prompt,
        string initial = "",
        string? hint = null,
        Func<string, string>? preview = null,
        bool multiline = false,
        IEnumerable<string>? options = null,
        Func<string, string?>? validate = null)
    {
        var d = new InputDialog
        {
            Title = title,
            Owner = owner,
            _preview = preview,
            _validate = validate,
        };
        d.PromptText.Text = prompt;
        if (owner == null) d.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (options != null)
        {
            d._useOptions = true;
            d.ValueBox.Visibility = Visibility.Collapsed;
            d.OptionsBox.Visibility = Visibility.Visible;
            foreach (var o in options) d.OptionsBox.Items.Add(o);
            d.OptionsBox.Text = initial;
        }
        else
        {
            d.ValueBox.Text = initial;
            if (multiline)
            {
                d.ValueBox.AcceptsReturn = true;
                d.ValueBox.TextWrapping = TextWrapping.Wrap;
                d.ValueBox.MinHeight = 120;
                d.ValueBox.VerticalContentAlignment = VerticalAlignment.Top;
                d.ValueBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                d.OkButton.IsDefault = false; // Enter makes a new line
            }
        }

        if (hint != null)
        {
            d.HintText.Text = hint;
            d.HintText.Visibility = Visibility.Visible;
        }
        d.UpdatePreview();
        d.Loaded += (_, _) =>
        {
            if (d._useOptions) d.OptionsBox.Focus();
            else
            {
                d.ValueBox.Focus();
                d.ValueBox.SelectAll();
            }
        };
        return d.ShowDialog() == true ? d.Value : null;
    }

    private void ValueBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_preview == null || PreviewText == null) return;
        string text;
        try { text = _preview(Value); }
        catch (Exception ex) { text = ex.Message; }
        PreviewText.Text = text;
        PreviewText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var problem = _validate?.Invoke(Value);
        if (problem != null)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
