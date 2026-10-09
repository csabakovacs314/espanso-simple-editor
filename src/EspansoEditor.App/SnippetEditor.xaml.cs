using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Text;
using EspansoEditor.Core.Validation;
using CoreRun = EspansoEditor.Core.Model.Run;
using WpfRun = System.Windows.Documents.Run;

namespace EspansoEditor.App;

/// <summary>What a snippet editor needs from the main window.</summary>
public interface IEditorHost
{
    EspansoConfig Config { get; }
    void SnippetChanged(Snippet s);

    /// <summary>A trigger was added or removed; whole word may be switched on where a trigger would block another.</summary>
    void TriggersChanged(Snippet s);
    void MoveSnippet(Snippet s, MatchFile file, string group);
    void PopOut(Snippet s);
    void DeleteSnippet(Snippet s);
    void SaveAll();

    /// <summary>Opens the trigger box (whole word for new snippets and for all snippets at once).</summary>
    void OpenTriggerSettings(Window? owner);
}

public sealed record LocationItem(MatchFile File, string Group)
{
    public string Text => Group.Length == 0 ? $"{File.DisplayName}  (no group)" : $"{File.DisplayName} › {Group}";
}

/// <summary>The form for one snippet: description, location, text with chips, trigger and options, preview.</summary>
public partial class SnippetEditor : UserControl
{
    private static readonly Regex NameRegex = new(@"^\w+$");

    private readonly IEditorHost _host;
    private bool _loading;
    private TextBox? _newTriggerBox;

    public SnippetEditor(Snippet snippet, IEditorHost host)
    {
        InitializeComponent();
        Snippet = snippet;
        _host = host;
        DataObject.AddPastingHandler(Editor, OnPaste);
        CommandManager.AddPreviewExecutedHandler(Editor, OnEditorCommand);
        LoadFromModel();
    }

    public Snippet Snippet { get; }

    private ChipContext Ctx => new(_host.Config.GlobalEcho, DateTime.Now, Snippet.IsReadOnly ? null : EditChip);

    private Window? OwnerWindow => Window.GetWindow(this);

    /// <summary>Hides "Own window" when the editor already is one.</summary>
    public void SetPoppedOut(bool poppedOut) => PopOutButton.Visibility = poppedOut ? Visibility.Collapsed : Visibility.Visible;

    public void LoadFromModel()
    {
        _loading = true;
        try
        {
            DescriptionBox.Text = Snippet.Label;
            FillLocations();
            var endsWithNewline = EndsWithNewline(Snippet.Body);
            NewlineBox.IsChecked = endsWithNewline;
            FormatBox.SelectedIndex = (int)Snippet.Format;
            Editor.Document = IsRich
                ? DocumentConverter.ToRichDocument(Snippet.Body, endsWithNewline, Ctx, IsHtml)
                : DocumentConverter.ToDocument(Snippet.Body, endsWithNewline, Ctx);
            UpdateFormatUi();
            WordBox.IsChecked = Snippet.Word;
            CaseBox.IsChecked = Snippet.PropagateCase;
            ModeBox.SelectedIndex = Snippet.ForceMode switch { "clipboard" => 1, "keys" => 2, _ => 0 };
            BuildTriggers();
            ApplyReadOnly();
        }
        finally
        {
            _loading = false;
        }
        RefreshDerived();
    }

    /// <summary>Called by the main window when something outside this editor changed (a save, a move, another snippet's trigger).</summary>
    public void RefreshFromOutside()
    {
        _loading = true;
        try
        {
            FillLocations();
        }
        finally
        {
            _loading = false;
        }
        AdoptVariableNames();
        RefreshDerived();
    }

    /// <summary>Called after the trigger box changed "whole word" for many snippets at once.</summary>
    public void RefreshOptions()
    {
        _loading = true;
        try
        {
            WordBox.IsChecked = Snippet.Word;
        }
        finally
        {
            _loading = false;
        }
        RefreshDerived();
    }

    /// <summary>Re-runs the checks, for when another snippet changed (a trigger, or whole word).</summary>
    public void RefreshChecks() => RefreshDerived();

    public void FocusTrigger()
    {
        if (Snippet.Triggers.Count == 0) _newTriggerBox?.Focus();
        else Editor.Focus();
    }

    private static bool EndsWithNewline(IEnumerable<CoreRun> body)
    {
        var runs = BodyText.Normalize(body);
        return runs.Count > 0 && runs[^1].Kind == RunKind.Text && runs[^1].Text.EndsWith('\n');
    }

    private void FillLocations()
    {
        LocationBox.Items.Clear();
        LocationItem? current = null;
        foreach (var file in _host.Config.Files)
        {
            if (file.IsReadOnly && !ReferenceEquals(file, Snippet.File)) continue;
            foreach (var group in new[] { "" }.Concat(file.GroupNames))
            {
                var item = new LocationItem(file, group);
                LocationBox.Items.Add(item);
                if (ReferenceEquals(file, Snippet.File) && group == Snippet.Group) current = item;
            }
        }
        if (current == null)
        {
            current = new LocationItem(Snippet.File, Snippet.Group);
            LocationBox.Items.Add(current);
        }
        LocationBox.SelectedItem = current;
    }

    private void ApplyReadOnly()
    {
        var ro = Snippet.IsReadOnly;
        DescriptionBox.IsReadOnly = ro;
        Editor.IsReadOnly = ro;
        Editor.IsReadOnlyCaretVisible = false;
        LocationBox.IsEnabled = !ro;
        InsertButton.IsEnabled = !ro;
        FormatBox.IsEnabled = !ro;
        FormatBar.IsEnabled = !ro;
        DeleteButton.IsEnabled = !ro;
        WordBox.IsEnabled = !ro;
        CaseBox.IsEnabled = !ro;
        NewlineBox.IsEnabled = !ro;
        ModeBox.IsEnabled = !ro;
    }

    private void RefreshDerived()
    {
        var issues = Snippet.IsReadOnly ? new List<Issue>() : Validator.Check(Snippet, _host.Config).ToList();

        // Banner: why it is read-only, else warnings.
        string? title = null, text = null;
        var kind = "info";
        if (Snippet.File.IsReadOnly)
        {
            title = "Read-only.";
            text = Snippet.File.ReadOnlyReason;
        }
        else if (Snippet.IsAdvanced)
        {
            title = "Read-only.";
            text = $"This snippet {Snippet.AdvancedReason}, which this editor does not cover yet. It is kept exactly as it is; change it in a text editor if needed.";
        }
        else
        {
            var warnings = issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Message).ToList();
            if (warnings.Count > 0)
            {
                title = "Note:";
                text = string.Join(" ", warnings) + " Saving still works.";
                kind = "warn";
            }
        }
        if (title != null)
        {
            Banner.Visibility = Visibility.Visible;
            Banner.Background = Res(kind == "warn" ? "WarnSoftBrush" : "InfoSoftBrush");
            Banner.BorderBrush = Res(kind == "warn" ? "WarnLineBrush" : "InfoLineBrush");
            BannerText.Inlines.Clear();
            BannerText.Inlines.Add(new Bold(new WpfRun(title + " ")));
            BannerText.Inlines.Add(new WpfRun(text));
        }
        else
        {
            Banner.Visibility = Visibility.Collapsed;
        }

        TriggerError.Visibility = !Snippet.IsReadOnly && Snippet.Triggers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        IssuesPanel.Children.Clear();
        foreach (var e in issues.Where(i => i.Severity == Severity.Error && !i.Message.Contains("at least one trigger")))
        {
            IssuesPanel.Children.Add(new TextBlock
            {
                Text = "Needs fixing before saving: " + e.Message,
                Foreground = Res("ErrorBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });
        }

        // Preview of what Espanso will type.
        PreviewText.Inlines.Clear();
        var shown = Snippet.Format == SnippetFormat.Html && !Snippet.IsReadOnly
            ? RichText.ToPlain(RichText.ParseMarkdown(Snippet.Body, escaped: true))
            : Snippet.Body;
        foreach (var seg in Preview.Render(shown, _host.Config.GlobalEcho, DateTime.Now))
        {
            var run = new WpfRun(seg.Text);
            switch (seg.Part)
            {
                case PreviewPart.Placeholder:
                    run.Foreground = Res("MutedBrush");
                    run.FontStyle = FontStyles.Italic;
                    break;
                case PreviewPart.Cursor:
                    run.Foreground = Res("CursorInkBrush");
                    run.FontWeight = FontWeights.Bold;
                    break;
            }
            PreviewText.Inlines.Add(run);
        }
        if (PreviewText.Inlines.Count == 0)
            PreviewText.Inlines.Add(new WpfRun("(nothing yet)") { Foreground = Res("MutedBrush"), FontStyle = FontStyles.Italic });
    }

    private void Changed()
    {
        if (_loading) return;
        Snippet.IsDirty = true;
        RefreshDerived();
        _host.SnippetChanged(Snippet);
    }

    private bool IsRich => Snippet.Format != SnippetFormat.Plain;

    private bool IsHtml => Snippet.Format == SnippetFormat.Html;

    private List<CoreRun> ReadBody()
    {
        var runs = IsRich
            ? RichText.ToMarkdown(DocumentConverter.FromRichDocument(Editor.Document), escape: IsHtml)
            : DocumentConverter.FromDocument(Editor.Document);
        if (NewlineBox.IsChecked == true) runs.Add(CoreRun.Literal("\n"));
        return BodyText.Normalize(runs);
    }

    // ---------- events ----------

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || Snippet.IsReadOnly) return;
        Snippet.Body = ReadBody();
        Changed();
    }

    private void DescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || Snippet.IsReadOnly) return;
        Snippet.Label = DescriptionBox.Text;
        Changed();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Snippet.IsReadOnly) return;
        Snippet.Word = WordBox.IsChecked == true;
        Snippet.PropagateCase = CaseBox.IsChecked == true;
        Changed();
    }

    private void Newline_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Snippet.IsReadOnly) return;
        Snippet.Body = ReadBody();
        Changed();
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Snippet.IsReadOnly) return;
        Snippet.ForceMode = ModeBox.SelectedIndex switch { 1 => "clipboard", 2 => "keys", _ => null };
        Changed();
    }

    private void LocationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Snippet.IsReadOnly) return;
        if (LocationBox.SelectedItem is LocationItem li && (!ReferenceEquals(li.File, Snippet.File) || li.Group != Snippet.Group))
            _host.MoveSnippet(Snippet, li.File, li.Group);
    }

    private void PopOut_Click(object sender, RoutedEventArgs e) => _host.PopOut(Snippet);

    // ---------- format: plain text, Jira Cloud (Markdown), HTML ----------

    private void UpdateFormatUi()
    {
        FormatBar.Visibility = IsRich ? Visibility.Visible : Visibility.Collapsed;
        FormatHint.Text = Snippet.Format switch
        {
            SnippetFormat.Jira => "Saved as Markdown text: **bold**, * bullets, ### headings, `code`. Jira Cloud turns it into formatting. Chips are filled in when it runs.",
            SnippetFormat.Html => "Pasted as formatted text (Espanso html:). Keeps its formatting in Jira, Outlook, Word and most web editors; plain-text boxes get the text only.",
            _ => "Text Espanso types. Coloured chips are filled in when it runs; double-click a chip to change it.",
        };
        PreviewLabel.Text = Snippet.Format == SnippetFormat.Html ? "Espanso will paste (formatting not shown here)" : "Espanso will type";
    }

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Snippet.IsReadOnly || FormatBox.SelectedIndex < 0) return;
        var target = (SnippetFormat)FormatBox.SelectedIndex;
        if (target == Snippet.Format) return;
        var newline = NewlineBox.IsChecked == true;

        _loading = true;
        try
        {
            if (!IsRich)
            {
                // Plain to formatted: Markdown already in the text (and "• " bullets) become formatting.
                var markdown = BulletsToMarkdown(ReadBody());
                Snippet.Format = target;
                Snippet.Body = IsHtml ? RichText.ToMarkdown(RichText.ParseMarkdown(markdown), escape: true) : markdown;
                Editor.Document = DocumentConverter.ToRichDocument(Snippet.Body, newline, Ctx, IsHtml);
            }
            else if (target == SnippetFormat.Plain)
            {
                var plain = RichText.ToPlain(DocumentConverter.FromRichDocument(Editor.Document));
                if (newline) plain.Add(CoreRun.Literal("\n"));
                Snippet.Body = BodyText.Normalize(plain);
                Snippet.Format = target;
                Editor.Document = DocumentConverter.ToDocument(Snippet.Body, newline, Ctx);
            }
            else
            {
                // Jira <-> HTML: the document stays; the text is written again in the new format's style.
                Snippet.Format = target;
                Snippet.Body = ReadBody();
            }
        }
        finally
        {
            _loading = false;
        }
        UpdateFormatUi();
        Changed();
    }

    /// <summary>Lines that start with "• " (how plain text shows bullets) become Markdown bullets again.</summary>
    private static List<CoreRun> BulletsToMarkdown(List<CoreRun> runs)
    {
        var result = new List<CoreRun>();
        var lineStart = true;
        foreach (var r in runs)
        {
            if (r.Kind != RunKind.Text)
            {
                result.Add(r);
                lineStart = false;
                continue;
            }
            var lines = r.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if ((i > 0 || lineStart) && lines[i].TrimStart(' ').StartsWith("• "))
                {
                    var indent = lines[i].Length - lines[i].TrimStart(' ').Length;
                    lines[i] = lines[i][..indent] + "* " + lines[i][(indent + 2)..];
                }
            }
            result.Add(CoreRun.Literal(string.Join("\n", lines)));
            lineStart = r.Text.EndsWith('\n');
        }
        return BodyText.Normalize(result);
    }

    /// <summary>Formatting keys (Ctrl+B and so on) only work where the format can keep them.</summary>
    private void OnEditorCommand(object sender, ExecutedRoutedEventArgs e)
    {
        var c = e.Command;
        var never = c == EditingCommands.ToggleUnderline || c == EditingCommands.IncreaseFontSize || c == EditingCommands.DecreaseFontSize
                    || c == EditingCommands.AlignCenter || c == EditingCommands.AlignRight || c == EditingCommands.AlignJustify
                    || c == EditingCommands.ToggleSubscript || c == EditingCommands.ToggleSuperscript;
        var formatting = c == EditingCommands.ToggleBold || c == EditingCommands.ToggleItalic || c == EditingCommands.ToggleBullets
                         || c == EditingCommands.ToggleNumbering || c == EditingCommands.IncreaseIndentation || c == EditingCommands.DecreaseIndentation;
        // HTML cannot hold nested lists or tabs; nesting is for Jira only.
        var nesting = c == EditingCommands.IncreaseIndentation || c == EditingCommands.DecreaseIndentation
                      || c == EditingCommands.TabForward || c == EditingCommands.TabBackward;
        if (never || (formatting && !IsRich) || (formatting && Snippet.IsReadOnly) || (nesting && IsHtml))
        {
            e.Handled = true;
            return;
        }
        // Shift+Enter would make a line that belongs to no list or heading; make it a normal new line.
        if (c == EditingCommands.EnterLineBreak && IsRich)
        {
            e.Handled = true;
            EditingCommands.EnterParagraphBreak.Execute(null, Editor);
        }
    }

    private void SyncBody()
    {
        if (_loading || Snippet.IsReadOnly) return;
        Snippet.Body = ReadBody();
        Changed();
        Editor.Focus();
    }

    private void Bold_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleBold.Execute(null, Editor);
        SyncBody();
    }

    private void Italic_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleItalic.Execute(null, Editor);
        SyncBody();
    }

    private void Code_Click(object sender, RoutedEventArgs e)
    {
        var sel = Editor.Selection;
        if (sel.IsEmpty) return;
        var on = !(sel.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f && DocumentConverter.IsMono(f));
        if (on)
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, FindResource("MonoFont"));
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, FindResource("CodeBrush"));
        }
        else
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, FindResource("UiFont"));
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        }
        SyncBody();
    }

    private void Heading_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var level)) return;
        foreach (var p in SelectedParagraphs().ToList())
        {
            if (p.Parent is ListItem) continue; // a list item cannot be a heading
            if (level == 0) DocumentConverter.ClearHeading(p);
            else DocumentConverter.StyleHeading(p, level);
        }
        SyncBody();
    }

    private void Bullets_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleBullets.Execute(null, Editor);
        SyncBody();
    }

    private void Numbers_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleNumbering.Execute(null, Editor);
        SyncBody();
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var p = Editor.Selection.Start.Paragraph;
        var end = Editor.Selection.End.Paragraph;
        for (var guard = 0; p != null && guard < 10000; guard++)
        {
            yield return p;
            if (ReferenceEquals(p, end)) yield break;
            p = p.ContentEnd.GetNextInsertionPosition(LogicalDirection.Forward)?.Paragraph;
        }
    }

    private void AllTriggers_Click(object sender, RoutedEventArgs e) => _host.OpenTriggerSettings(OwnerWindow);

    private void Delete_Click(object sender, RoutedEventArgs e) => _host.DeleteSnippet(Snippet);

    // ---------- triggers ----------

    private void BuildTriggers()
    {
        TriggerPanel.Children.Clear();
        var mono = (FontFamily)FindResource("MonoFont");
        foreach (var t in Snippet.Triggers.ToList())
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = t,
                FontFamily = mono,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("AccentDarkBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!Snippet.IsReadOnly)
            {
                var remove = new Button
                {
                    Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9 },
                    Style = (Style)FindResource("QuietButton"),
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = "Remove " + t,
                };
                AutomationProperties.SetName(remove, "Remove trigger " + t);
                remove.Click += (_, _) =>
                {
                    Snippet.Triggers.Remove(t);
                    BuildTriggers();
                    Changed();
                    _host.TriggersChanged(Snippet);
                };
                panel.Children.Add(remove);
            }
            TriggerPanel.Children.Add(new Border
            {
                Child = panel,
                Background = Res("TriggerChipBrush"),
                BorderBrush = Res("TriggerChipLineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 3, Snippet.IsReadOnly ? 10 : 2, 3),
                Margin = new Thickness(0, 0, 6, 6),
            });
        }

        if (Snippet.IsReadOnly) return;
        _newTriggerBox = new TextBox
        {
            Width = 150,
            Margin = new Thickness(0, 0, 6, 6),
            FontFamily = mono,
            ToolTip = "Type a trigger, for example :sig, and press Enter",
        };
        AutomationProperties.SetName(_newTriggerBox, "New trigger");
        _newTriggerBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddTrigger();
                e.Handled = true;
            }
        };
        var add = new Button { Content = "Add", Margin = new Thickness(0, 0, 6, 6) };
        add.Click += (_, _) => AddTrigger();
        TriggerPanel.Children.Add(_newTriggerBox);
        TriggerPanel.Children.Add(add);
    }

    private void AddTrigger()
    {
        var t = (_newTriggerBox?.Text ?? "").Trim();
        if (t.Length == 0) return;
        if (!Snippet.Triggers.Contains(t)) Snippet.Triggers.Add(t);
        BuildTriggers();
        Changed();
        _host.TriggersChanged(Snippet);
        _newTriggerBox?.Focus();
    }

    // ---------- chips ----------

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = InsertButton, Placement = PlacementMode.Bottom };

        void Item(string header, string hint, Func<CoreRun?> make)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = hint, Foreground = Res("MutedBrush"), FontSize = 12 });
            var mi = new MenuItem { Header = content };
            mi.Click += (_, _) =>
            {
                var r = make();
                if (r != null) InsertChip(r);
            };
            menu.Items.Add(mi);
        }

        var now = DateTime.Now;
        Item("Date", "Today, e.g. " + StrfTime.Format("%d/%m/%Y", now), () => AskDate(null));
        Item("Time", "The time now, e.g. " + StrfTime.Format("%H:%M", now), () => CoreRun.Date("%H:%M"));
        Item("Clipboard", "Whatever you copied last", () => CoreRun.Clipboard());
        Item("Cursor position", "Where the cursor ends up after typing", () =>
        {
            if (Snippet.Body.Any(r => r.Kind == RunKind.Cursor))
            {
                MessageBox.Show(OwnerWindow!, "This snippet already has a cursor position. Delete the old one first.", "Cursor position",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return CoreRun.Cursor();
        });
        menu.Items.Add(new Separator());
        Item("Ask when typing…", "Espanso shows a small form for this first", () => AskField(null));
        Item("PowerShell output…", "The result of a command", () => AskShell(null));
        Item("Random choice…", "One of several texts, picked at random", () => AskRandom(null));
        if (_host.Config.GlobalVarNames.Count > 0)
            Item("Variable…", "A global variable from your Espanso files", () => AskGlobal(null));
        menu.IsOpen = true;
    }

    private void InsertChip(CoreRun run)
    {
        Editor.Focus();
        Editor.BeginChange();
        try
        {
            if (!Editor.Selection.IsEmpty) Editor.Selection.Text = string.Empty;
            var pos = Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            var container = new InlineUIContainer(DocumentConverter.MakeChip(run, Ctx), pos)
            {
                BaselineAlignment = BaselineAlignment.Center,
            };
            Editor.CaretPosition = container.ElementEnd;
        }
        finally
        {
            Editor.EndChange();
        }
        // TextChanged normally fires; make sure the model is in step either way.
        if (!_loading)
        {
            Snippet.Body = ReadBody();
            Changed();
        }
    }

    private void EditChip(FrameworkElement chip)
    {
        if (Snippet.IsReadOnly || chip.Tag is not CoreRun r) return;
        CoreRun? updated = r.Kind switch
        {
            RunKind.Date => AskDate(r),
            RunKind.Field => AskField(r),
            RunKind.Shell => AskShell(r),
            RunKind.Random => AskRandom(r),
            RunKind.GlobalVar => AskGlobal(r),
            _ => null,
        };
        if (updated == null) return;
        if (chip.Parent is InlineUIContainer container)
        {
            container.Child = DocumentConverter.MakeChip(updated, Ctx);
            Snippet.Body = ReadBody();
            Changed();
        }
    }

    /// <summary>After a save, chips pick up the variable names that were written, so later saves keep them.</summary>
    private void AdoptVariableNames()
    {
        var chips = DocumentConverter.Chips(Editor.Document).ToList();
        var model = Snippet.Body.Where(r => r.IsChip).ToList();
        if (chips.Count != model.Count) return;
        for (var i = 0; i < chips.Count; i++)
            if (chips[i].Tag is CoreRun cr && cr.SameContent(model[i])) chips[i].Tag = model[i];
    }

    private CoreRun? AskDate(CoreRun? old)
    {
        var now = DateTime.Now;
        var f = InputDialog.Ask(OwnerWindow, old == null ? "Insert date" : "Change date", "Format, using Espanso's date codes",
            old?.Format ?? "%d/%m/%Y",
            hint: "%d day · %m month · %Y year · %A weekday · %B month name · %H:%M time\n"
                  + $"Examples: %d/%m/%Y gives {StrfTime.Format("%d/%m/%Y", now)} · %A, %d %B gives {StrfTime.Format("%A, %d %B", now)}",
            preview: v => "Today this gives: " + StrfTime.Format(v, now),
            validate: v => v.Trim().Length == 0 ? "Enter a format." : v.Contains("{{") ? "The format cannot contain {{." : null);
        return f == null ? null : CoreRun.Date(f, old?.VarName);
    }

    private CoreRun? AskField(CoreRun? old)
    {
        var name = InputDialog.Ask(OwnerWindow, "Ask when typing", "What should Espanso ask for? One word, for example name or day.",
            old?.Text ?? "",
            hint: "Espanso shows a small form with a box for each of these before it types the snippet.",
            validate: v => NameRegex.IsMatch(v.Trim()) ? null : "Use letters, digits and _ only, with no spaces.");
        return name == null ? null : CoreRun.Field(name.Trim());
    }

    private CoreRun? AskShell(CoreRun? old)
    {
        var cmd = InputDialog.Ask(OwnerWindow, "PowerShell output", "Command whose output Espanso types",
            old?.Command ?? "",
            hint: "Runs every time the snippet expands. Example: Get-Date -Format yyyy",
            validate: v => v.Trim().Length == 0 ? "Enter a command." : v.Contains("{{") ? "The command cannot contain {{." : null);
        return cmd == null ? null : CoreRun.Shell(cmd, old?.ShellName, old?.VarName);
    }

    private CoreRun? AskRandom(CoreRun? old)
    {
        var text = InputDialog.Ask(OwnerWindow, "Random choice", "One choice per line",
            old == null ? "" : string.Join("\n", old.Choices),
            multiline: true,
            validate: v => v.Split('\n').Count(l => l.Trim().Length > 0) < 2 ? "Enter at least two choices." : v.Contains("{{") ? "Choices cannot contain {{." : null);
        if (text == null) return null;
        var choices = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        return CoreRun.Random(choices, old?.VarName);
    }

    private CoreRun? AskGlobal(CoreRun? old)
    {
        var names = _host.Config.GlobalVarNames.OrderBy(n => n).ToList();
        var name = InputDialog.Ask(OwnerWindow, "Variable", "Global variable", old?.Text ?? names.FirstOrDefault() ?? "",
            options: names,
            validate: v => NameRegex.IsMatch(v.Trim()) ? null : "Use letters, digits and _ only.");
        return name == null ? null : CoreRun.Global(name.Trim());
    }

    // ---------- paste as plain text ----------

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? e.DataObject.GetData(DataFormats.Text) as string;
        if (text == null)
        {
            e.CancelCommand();
            return;
        }
        var plain = new DataObject();
        plain.SetData(DataFormats.UnicodeText, text);
        e.DataObject = plain;
    }

    private Brush Res(string key) => (Brush)FindResource(key);
}
