using System.Windows;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Validation;

namespace EspansoEditor.App;

/// <summary>
/// The trigger box: whole word as the default for new snippets, whole word on or off for every snippet at once,
/// and the triggers that block longer ones.
/// </summary>
public partial class TriggerSettingsWindow : Window
{
    private const int MaxClashLines = 12;

    private readonly EspansoConfig _cfg;
    private readonly AppSettings _settings;
    private readonly Action _changed;
    private WordChange? _last;
    private bool _loading;

    internal TriggerSettingsWindow(Window owner, EspansoConfig cfg, AppSettings settings, Action changed)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Owner = owner;
        _cfg = cfg;
        _settings = settings;
        _changed = changed;
        _loading = true;
        (settings.WholeWordMode switch
        {
            WholeWord.Always => AlwaysRadio,
            WholeWord.Never => NeverRadio,
            _ => NeededRadio,
        }).IsChecked = true;
        _loading = false;
        Refresh();
    }

    private void Refresh()
    {
        var all = _cfg.AllSnippets.Where(s => s.Triggers.Count > 0).ToList();
        var editable = all.Where(s => !s.IsReadOnly).ToList();
        var word = editable.Count(s => s.Word);
        CountText.Text = editable.Count == 0
            ? "There are no editable snippets."
            : $"{word} of {editable.Count} snippets are whole word now.";
        AllOnButton.IsEnabled = editable.Any(s => !s.Word);
        AllOffButton.IsEnabled = editable.Any(s => s.Word);
        var locked = all.Count - editable.Count;
        LockedText.Visibility = locked > 0 ? Visibility.Visible : Visibility.Collapsed;
        LockedText.Text = $"{locked} read-only snippet(s) (packages, or features this editor keeps as they are) are left alone.";

        var clashes = TriggerClashes.Find(_cfg.AllSnippets);
        if (clashes.Count == 0)
        {
            ClashText.Text = "None. Every trigger can be typed.";
            ClashText.Foreground = (System.Windows.Media.Brush)FindResource("AccentDarkBrush");
        }
        else
        {
            var lines = clashes
                .GroupBy(c => (c.Blocker, c.Short))
                .Select(g => $"{g.Key.Short}  blocks  {string.Join(", ", g.Select(c => c.Long).Distinct())}"
                             + (g.Key.Blocker.IsReadOnly ? "   (read-only: rename the longer one)" : g.Key.Blocker.Word ? "   (rename one of them)" : ""))
                .ToList();
            var shown = lines.Take(MaxClashLines).ToList();
            if (lines.Count > MaxClashLines) shown.Add($"and {lines.Count - MaxClashLines} more");
            ClashText.Text = string.Join("\n", shown);
            ClashText.Foreground = (System.Windows.Media.Brush)FindResource("WarnBrush");
        }
        var fixable = TriggerClashes.FixableBlockers(_cfg.AllSnippets);
        NeededButton.Content = fixable.Count == 0
            ? "Whole word only where needed"
            : $"Whole word only where needed ({string.Join(", ", fixable.Select(s => s.Triggers.FirstOrDefault(t => clashes.Any(c => ReferenceEquals(c.Blocker, s) && c.Short == t)) ?? s.DisplayName))})";
        NeededButton.IsEnabled = fixable.Count > 0;
        UndoButton.IsEnabled = _last != null;
    }

    private void Apply(IEnumerable<Snippet> snippets, bool on, string what)
    {
        var change = _cfg.SetWholeWord(snippets, on);
        _last = change.Changed.Count > 0 ? change : null;
        _changed();
        ResultBar.Visibility = Visibility.Visible;
        ResultText.Text = change.Changed.Count == 0
            ? "Nothing to change."
            : $"{what}: {change.Changed.Count} snippet(s) changed. Press Ctrl+S in the main window to save.";
        Refresh();
    }

    private void AllOn_Click(object sender, RoutedEventArgs e) => Apply(_cfg.AllSnippets, true, "Whole word on");

    private void AllOff_Click(object sender, RoutedEventArgs e) => Apply(_cfg.AllSnippets, false, "Whole word off");

    private void Needed_Click(object sender, RoutedEventArgs e) =>
        Apply(TriggerClashes.FixableBlockers(_cfg.AllSnippets), true, "Whole word where needed");

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        _last.Undo();
        _last = null;
        _changed();
        ResultText.Text = "Undone. The snippets are back to how they were.";
        Refresh();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.WholeWordMode = AlwaysRadio.IsChecked == true ? WholeWord.Always
            : NeverRadio.IsChecked == true ? WholeWord.Never
            : WholeWord.Needed;
        _settings.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
