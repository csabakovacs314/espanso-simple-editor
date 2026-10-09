using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.IO.Path;
using System.Windows.Threading;
using EspansoEditor.Core.Espanso;
using EspansoEditor.Core.Model;
using EspansoEditor.Core.Saving;

namespace EspansoEditor.App;

public partial class MainWindow : Window, IEditorHost
{
    private enum Kind { Ok, Info, Warn, Error }

    private readonly Dictionary<Snippet, TabItem> _tabs = new();
    private readonly Dictionary<Snippet, SnippetWindow> _windows = new();
    private readonly Dictionary<Snippet, SnippetNode> _nodes = new();
    private readonly HashSet<string> _collapsed = new();
    private readonly ObservableCollection<TreeNode> _roots = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(7) };

    private AppSettings _settings = new();
    private EspansoConfig? _cfg;
    private string? _detected;
    private bool _suppressTreeSelection;
    private bool _building;
    private TreeNode? _selectedNode;

    public MainWindow()
    {
        InitializeComponent();
        Library.ItemsSource = _roots;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        TreeNode.ExpansionChanged += node =>
        {
            if (_building) return;
            if (node.IsExpanded) _collapsed.Remove(node.Key);
            else _collapsed.Add(node.Key);
        };
    }

    public EspansoConfig Config => _cfg ?? throw new InvalidOperationException("No Espanso configuration is loaded.");

    private Snippet? ActiveSnippet => (Tabs.SelectedItem as TabItem)?.Tag as Snippet;

    // ---------- start-up and loading ----------

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = AppSettings.Load();
        AutoOpenBox.IsChecked = _settings.AutoOpen;
        if (!string.IsNullOrEmpty(_settings.LastFolder)) OtherPathBox.Text = _settings.LastFolder;

        if (_settings.AutoOpen && !string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
        {
            if (TryLoad(_settings.LastFolder)) return;
        }
        ShowLoadView();
        await DetectAsync();
    }

    private async Task DetectAsync()
    {
        AutoPathText.Text = "Looking for Espanso…";
        AutoHowText.Text = "";
        var located = await Task.Run(EspansoCli.Locate);
        _detected = located.Folder;
        if (_detected != null)
        {
            AutoRadio.IsEnabled = true;
            AutoPathText.Text = _detected;
            var files = 0;
            try
            {
                var match = Path.Combine(_detected, "match");
                if (Directory.Exists(match))
                    files = Directory.EnumerateFiles(match, "*.y*ml", SearchOption.AllDirectories).Count();
            }
            catch
            {
                // Only used for the label.
            }
            AutoHowText.Text = $"{Capitalise(located.How)} · {files} snippet file(s)";
            if (!string.IsNullOrEmpty(_settings.LastFolder) && !SamePath(_settings.LastFolder, _detected))
                OtherRadio.IsChecked = true;
            else
                AutoRadio.IsChecked = true;
        }
        else
        {
            AutoRadio.IsEnabled = false;
            OtherRadio.IsChecked = true;
            AutoPathText.Text = "Not found on this computer";
            AutoHowText.Text = "The espanso command did not answer and there is no config folder in the usual places. Choose the folder below.";
        }
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private void ShowLoadView()
    {
        EditorView.Visibility = Visibility.Collapsed;
        LoadView.Visibility = Visibility.Visible;
        Toast.Visibility = Visibility.Collapsed;
        Title = "Espanso Simple Editor";
    }

    private void ShowLoadError(string message)
    {
        LoadErrorText.Text = message;
        LoadErrorText.Visibility = Visibility.Visible;
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var folder = AutoRadio.IsChecked == true ? _detected : OtherPathBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowLoadError("Choose a folder first.");
            return;
        }
        TryLoad(folder);
    }

    private bool TryLoad(string folder)
    {
        EspansoConfig cfg;
        try
        {
            cfg = EspansoConfig.Load(folder);
        }
        catch (Exception ex)
        {
            ShowLoadView();
            ShowLoadError(ex.Message);
            return false;
        }

        CloseAllEditors();
        _cfg = cfg;
        _settings.LastFolder = folder;
        _settings.AutoOpen = AutoOpenBox.IsChecked == true;
        _settings.Save();

        LoadErrorText.Visibility = Visibility.Collapsed;
        LoadView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;

        _collapsed.Clear();
        foreach (var f in cfg.Files.Where(f => f.IsPackage)) _collapsed.Add("F|" + f.FullPath);
        SearchBox.Text = "";
        RebuildTree();
        UpdateStatus();
        _ = RefreshEspansoStatusAsync();

        var count = cfg.AllSnippets.Count();
        var unreadable = cfg.Files.Count(f => f.LoadError != null);
        var advanced = cfg.AllSnippets.Count(s => s.IsAdvanced);
        var note = unreadable > 0
            ? $" {unreadable} file(s) could not be read and are shown read-only."
            : advanced > 0 ? $" {advanced} snippet(s) use features this editor keeps read-only." : " Pick one on the left to edit it.";
        ShowToast(unreadable > 0 ? Kind.Warn : Kind.Ok, $"Loaded {count} snippets", $"From {cfg.Files.Count} file(s) in {cfg.MatchDir}.{note}");
        return true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the Espanso folder (the one that contains 'match')",
        };
        var start = OtherPathBox.Text.Trim().Trim('"');
        if (Directory.Exists(start)) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) == true)
        {
            OtherPathBox.Text = dlg.FolderName;
            OtherRadio.IsChecked = true;
        }
    }

    private void OtherPathBox_GotFocus(object sender, RoutedEventArgs e) => OtherRadio.IsChecked = true;

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmUnsaved()) return;
        CloseAllEditors();
        _cfg = null;
        _roots.Clear();
        ShowLoadView();
        await DetectAsync();
    }

    /// <summary>True when it is fine to drop the current state (saved, or the user said so).</summary>
    private bool ConfirmUnsaved()
    {
        if (_cfg?.HasUnsavedChanges != true) return true;
        var r = MessageBox.Show(this, "You have unsaved changes. Save them first?", "Unsaved changes",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes)
        {
            SaveAll();
            return _cfg?.HasUnsavedChanges != true;
        }
        return true;
    }

    // ---------- library tree ----------

    private void RebuildTree()
    {
        if (_cfg == null) return;
        var q = SearchBox.Text.Trim();
        var keep = ActiveSnippet; // only ever re-select an open tab, so a closed one cannot come back
        var duplicates = DuplicateTriggers();

        _building = true;
        _suppressTreeSelection = true;
        try
        {
            _roots.Clear();
            _nodes.Clear();
            foreach (var file in _cfg.Files)
            {
                var fn = new FileNode(file);
                var any = false;
                foreach (var g in new[] { "" }.Concat(file.GroupNames).Distinct())
                {
                    var items = file.Snippets.Where(s => s.Group == g && Matches(s, q)).ToList();
                    IList<TreeNode> target = fn.Children;
                    if (g.Length > 0)
                    {
                        if (items.Count == 0 && q.Length > 0) continue;
                        var gn = new GroupNode(file, g);
                        gn.IsExpanded = q.Length > 0 || !_collapsed.Contains(gn.Key);
                        fn.Children.Add(gn);
                        target = gn.Children;
                    }
                    foreach (var s in items)
                    {
                        var sn = new SnippetNode(s) { HasWarning = s.Triggers.Any(duplicates.Contains) };
                        _nodes[s] = sn;
                        target.Add(sn);
                        any = true;
                    }
                }
                if (q.Length > 0 && !any) continue;
                fn.IsExpanded = q.Length > 0 || !_collapsed.Contains(fn.Key);
                _roots.Add(fn);
            }

            if (keep != null && _nodes.TryGetValue(keep, out var node))
            {
                node.IsSelected = true;
                _selectedNode = node;
            }
        }
        finally
        {
            _building = false;
            _suppressTreeSelection = false;
        }
    }

    private HashSet<string> DuplicateTriggers() =>
        _cfg == null
            ? new HashSet<string>()
            : _cfg.AllSnippets.SelectMany(s => s.Triggers.Distinct()).GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

    private static bool Matches(Snippet s, string q)
    {
        if (q.Length == 0) return true;
        bool Has(string? text) => text != null && text.Contains(q, StringComparison.OrdinalIgnoreCase);
        return Has(s.Label) || s.Triggers.Any(Has) || Has(BodyText.LiteralText(s.Body));
    }

    private void Library_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNode n) _selectedNode = n;
        if (_suppressTreeSelection) return;
        if (e.NewValue is SnippetNode sn) OpenSnippet(sn.Snippet);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildTree();
    }

    // ---------- tabs and windows ----------

    private void OpenSnippet(Snippet s, bool focusTrigger = false)
    {
        if (_windows.TryGetValue(s, out var w))
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            return;
        }
        if (!_tabs.TryGetValue(s, out var tab))
        {
            tab = new TabItem { Content = new SnippetEditor(s, this), Tag = s, Header = MakeHeader(s) };
            _tabs[s] = tab;
            Tabs.Items.Add(tab);
        }
        Tabs.SelectedItem = tab;
        EmptyHint.Visibility = Visibility.Collapsed;
        if (focusTrigger && tab.Content is SnippetEditor editor)
            Dispatcher.BeginInvoke(editor.FocusTrigger, DispatcherPriority.Input);
    }

    private object MakeHeader(Snippet s)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = s.DisplayName,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (s.IsDirty || s.IsNew || s.IsMoved)
            panel.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = (Brush)FindResource("InkBrush"), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var close = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9 },
            Style = (Style)FindResource("QuietButton"),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Close (Ctrl+W)",
        };
        close.Click += (_, _) => CloseTab(s);
        panel.Children.Add(close);
        return panel;
    }

    private void CloseTab(Snippet s)
    {
        if (!_tabs.Remove(s, out var tab)) return;
        Tabs.Items.Remove(tab);
        EmptyHint.Visibility = Tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseAllEditors()
    {
        foreach (var s in _tabs.Keys.ToList()) CloseTab(s);
        foreach (var w in _windows.Values.ToList()) w.Close();
        _windows.Clear();
    }

    private IEnumerable<SnippetEditor> AllEditors() =>
        _tabs.Values.Select(t => t.Content).OfType<SnippetEditor>().Concat(_windows.Values.Select(w => w.Editor));

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged also bubbles up from combo boxes inside the editors.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (ActiveSnippet is { } s && _nodes.TryGetValue(s, out var node))
        {
            _suppressTreeSelection = true;
            try
            {
                node.IsSelected = true;
                _selectedNode = node;
            }
            finally
            {
                _suppressTreeSelection = false;
            }
        }
    }

    // ---------- IEditorHost ----------

    public void SnippetChanged(Snippet s)
    {
        if (_tabs.TryGetValue(s, out var tab)) tab.Header = MakeHeader(s);
        if (_windows.TryGetValue(s, out var w)) w.UpdateTitle();
        var duplicates = DuplicateTriggers();
        foreach (var node in _nodes.Values)
        {
            var warn = node.Snippet.Triggers.Any(duplicates.Contains);
            if (warn != node.HasWarning || ReferenceEquals(node.Snippet, s))
            {
                node.HasWarning = warn;
                node.Refresh();
            }
        }
        UpdateStatus();
    }

    public void MoveSnippet(Snippet s, MatchFile file, string group)
    {
        try
        {
            Config.Move(s, file, group);
        }
        catch (Exception ex)
        {
            ShowToast(Kind.Error, "Could not move the snippet", ex.Message);
            return;
        }
        RebuildTree();
        SnippetChanged(s);
    }

    public void PopOut(Snippet s)
    {
        CloseTab(s);
        var w = new SnippetWindow(s, this);
        _windows[s] = w;
        w.Closed += (_, _) => _windows.Remove(s);
        w.Show();
    }

    public void DeleteSnippet(Snippet s)
    {
        Window owner = _windows.TryGetValue(s, out var win) ? win : this;
        var answer = MessageBox.Show(owner,
            $"Delete \"{s.DisplayName}\"?\n\nIt is removed from {s.File.RelativePath} when you save. A backup of the file is kept.",
            "Delete snippet", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            Config.Delete(s);
        }
        catch (Exception ex)
        {
            ShowToast(Kind.Error, "Could not delete", ex.Message);
            return;
        }
        CloseTab(s);
        win?.Close();
        RebuildTree();
        UpdateStatus();
        ShowToast(Kind.Info, "Snippet deleted", "Press Ctrl+S to save the change.");
    }

    public void SaveAll()
    {
        if (_cfg == null) return;
        var options = new SaveOptions { BackupRoot = SaveService.DefaultBackupRoot };
        var result = SaveService.Save(_cfg, options);

        if (result.Conflicts.Count > 0)
        {
            var names = string.Join(", ", result.Conflicts.Select(f => f.RelativePath));
            var answer = MessageBox.Show(this,
                $"{names} changed on disk after you opened it, for example in another editor.\n\n"
                + "Yes: save your version anyway. The version on disk is backed up first.\n"
                + "No: reload everything from disk and drop your unsaved changes.\n"
                + "Cancel: do nothing for now.",
                "File changed on disk", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                result = SaveService.Save(_cfg, new SaveOptions { BackupRoot = options.BackupRoot, OverwriteChangedFiles = true });
            }
            else
            {
                if (answer == MessageBoxResult.No && TryLoad(_settings.LastFolder ?? _cfg.ConfigDir))
                    ShowToast(Kind.Info, "Reloaded from disk", "Your unsaved changes were dropped.");
                return;
            }
        }

        if (result.BlockingIssues.Count > 0)
        {
            var issue = result.BlockingIssues[0];
            OpenSnippet(issue.Snippet);
            ShowToast(Kind.Error, "Not saved", $"\"{issue.Snippet.DisplayName}\": {issue.Message} Nothing on disk changed.");
            return;
        }
        if (result.Errors.Count > 0)
        {
            ShowToast(Kind.Error, "Not saved", string.Join(" ", result.Errors));
            return;
        }
        if (result.SavedFiles.Count == 0)
        {
            ShowToast(Kind.Info, "Nothing to save", "Every snippet already matches what is on disk.");
            return;
        }

        RebuildTree();
        foreach (var editor in AllEditors()) editor.RefreshFromOutside();
        foreach (var (s, tab) in _tabs) tab.Header = MakeHeader(s);
        foreach (var w in _windows.Values) w.UpdateTitle();
        UpdateStatus();
        ShowToast(Kind.Ok, "Saved " + string.Join(" and ", result.SavedFiles.Select(f => f.RelativePath)),
            $"{result.SavedSnippets} snippet change(s) written, backup kept. Espanso reloads by itself.");
    }

    // ---------- toolbar ----------

    private (MatchFile? File, string Group) TargetLocation()
    {
        if (_cfg == null) return (null, "");
        (MatchFile? File, string Group) pick = _selectedNode switch
        {
            FileNode f => (f.File, ""),
            GroupNode g => (g.File, g.Group),
            SnippetNode s => (s.Snippet.File, s.Snippet.Group),
            _ => ActiveSnippet is { } a ? (a.File, a.Group) : (null, ""),
        };
        if (pick.File == null || pick.File.IsReadOnly)
        {
            var first = _cfg.Files.FirstOrDefault(f => !f.IsReadOnly);
            return (first, "");
        }
        return pick;
    }

    private void NewSnippet_Click(object sender, RoutedEventArgs e) => NewSnippet();

    private void NewSnippet()
    {
        if (_cfg == null) return;
        var (file, group) = TargetLocation();
        if (file == null)
        {
            ShowToast(Kind.Warn, "No editable file", "Every file in this configuration is read-only.");
            return;
        }
        var s = _cfg.NewSnippet(file, group);
        SearchBox.Text = "";
        RebuildTree();
        OpenSnippet(s, focusTrigger: true);
        UpdateStatus();
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg == null) return;
        var (file, _) = TargetLocation();
        if (file == null)
        {
            ShowToast(Kind.Warn, "No editable file", "Every file in this configuration is read-only.");
            return;
        }
        var d = _cfg.GroupDelimiter;
        var name = InputDialog.Ask(this, "New group", $"Name of the new group in {file.DisplayName}", "",
            hint: $"Groups are saved as comment lines like  # {d} Name {d}  above their snippets, the same style your files use.",
            validate: n => n.Trim().Length == 0 ? "Enter a name."
                : file.GroupNames.Contains(n.Trim()) ? "That group already exists."
                : n.Contains('#') ? "A group name cannot contain #." : null);
        if (name == null) return;
        _cfg.AddGroup(file, name.Trim());
        var s = _cfg.NewSnippet(file, name.Trim());
        SearchBox.Text = "";
        RebuildTree();
        OpenSnippet(s, focusTrigger: true);
        UpdateStatus();
        ShowToast(Kind.Info, "Group added", "It is written to the file together with its first snippet when you save.");
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAll();

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        ShowToast(Kind.Info, "Restarting Espanso…", "Running espanso restart.");
        var r = await Task.Run(EspansoCli.Restart);
        if (r.Started && r.ExitCode == 0) ShowToast(Kind.Ok, "Espanso restarted", "It now uses the files as saved.");
        else if (r.ExitCode == EspansoCli.CmdNotRecognized) ShowToast(Kind.Warn, "Could not restart Espanso", "Windows does not know the espanso command. Restart Espanso from its tray icon instead.");
        else ShowToast(Kind.Warn, "Could not restart Espanso", r.Output.Length > 0 ? r.Output : $"espanso restart ended with code {r.ExitCode}.");
        await Task.Delay(2000); // give the new Espanso a moment to start before checking
        await RefreshEspansoStatusAsync();
    }

    private async Task RefreshEspansoStatusAsync()
    {
        EspansoStatusText.Text = "Checking Espanso…";
        EspansoPill.ToolTip = null;
        var status = await Task.Run(EspansoCli.GetStatus);
        EspansoPill.ToolTip = status.Detail + "\nDetails of every espanso call: " + System.IO.Path.Combine(App.DataDir, "espanso-cli.log");
        switch (status.State)
        {
            case EspansoState.Running:
                EspansoStatusText.Text = "Espanso running · reloads after each save";
                EspansoPill.Background = (Brush)FindResource("AccentSoftBrush");
                EspansoStatusText.Foreground = (Brush)FindResource("AccentDarkBrush");
                break;
            case EspansoState.NotRunning:
                EspansoStatusText.Text = "Espanso is not running";
                EspansoPill.Background = (Brush)FindResource("WarnSoftBrush");
                EspansoStatusText.Foreground = (Brush)FindResource("WarnBrush");
                break;
            case EspansoState.NotInstalled:
                EspansoStatusText.Text = "espanso command not found";
                EspansoPill.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xEA));
                EspansoStatusText.Foreground = (Brush)FindResource("MutedBrush");
                break;
            default:
                EspansoStatusText.Text = "Espanso status unknown";
                EspansoPill.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xEA));
                EspansoStatusText.Foreground = (Brush)FindResource("MutedBrush");
                break;
        }
    }

    private void UpdateStatus()
    {
        if (_cfg == null) return;
        var all = _cfg.AllSnippets.ToList();
        var deleted = _cfg.Files.SelectMany(f => f.Removed).Count(r => !all.Contains(r));
        var pending = all.Count(s => s.IsDirty || s.IsNew || s.IsMoved) + deleted;
        StatusLeft.Text = $"{all.Count} snippets in {_cfg.Files.Count} file(s)  ·  {_cfg.MatchDir}";
        StatusRight.Text = pending > 0 ? $"Unsaved changes: {pending}   ·   Ctrl+S to save" : "All changes saved";
        Title = (pending > 0 ? "• " : "") + "Espanso Simple Editor";
    }

    // ---------- keyboard, closing, toast ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditorView.Visibility != Visibility.Visible || Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.S:
                SaveAll();
                e.Handled = true;
                break;
            case Key.N:
                NewSnippet();
                e.Handled = true;
                break;
            case Key.F:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.W:
                if (ActiveSnippet is { } s) CloseTab(s);
                e.Handled = true;
                break;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmUnsaved())
        {
            e.Cancel = true;
            return;
        }
        foreach (var w in _windows.Values.ToList()) w.Close();
    }

    private void ShowToast(Kind kind, string title, string body)
    {
        ToastTitle.Text = title;
        ToastBody.Text = body;
        (ToastIcon.Text, ToastIcon.Foreground, ToastTitle.Foreground) = kind switch
        {
            Kind.Ok => ("", (Brush)FindResource("AccentBrush"), (Brush)FindResource("AccentDarkBrush")),
            Kind.Warn => ("", (Brush)FindResource("WarnBrush"), (Brush)FindResource("WarnBrush")),
            Kind.Error => ("", (Brush)FindResource("ErrorBrush"), (Brush)FindResource("ErrorBrush")),
            _ => ("", (Brush)FindResource("InfoBrush"), (Brush)FindResource("InkBrush")),
        };
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(kind is Kind.Error or Kind.Warn ? 14 : 7);
        _toastTimer.Start();
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }
}
