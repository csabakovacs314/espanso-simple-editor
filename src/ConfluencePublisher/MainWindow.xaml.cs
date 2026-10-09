using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Publisher.Core;

namespace ConfluencePublisher;

public partial class MainWindow : Window
{
    private readonly PublisherSettings _settings = PublisherSettings.Load();
    private PageInfo? _parent;
    private string? _pageUrl;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        SiteBox.Text = _settings.Site;
        EmailBox.Text = _settings.Email;
        var token = _settings.GetToken();
        if (token != null)
        {
            TokenBox.Password = token;
            RememberBox.IsChecked = true;
        }

        var file = File.Exists(_settings.LastFile) ? _settings.LastFile : DefaultGuide();
        if (file != null) LoadGuide(file);
        else PicturesText.Text = "Pick the Markdown file to publish.";

        ParentBox.Text = _settings.LastParent;
        if (_settings.LastParent.Length > 0 && _settings.LastParentPath.Length > 0)
        {
            ParentPathText.Text = _settings.LastParentPath;
            ParentCard.Visibility = Visibility.Visible;
            ParentLabel.Text = "Last used parent (checked again when you publish)";
        }
        ParentBox.TextChanged += (_, _) =>
        {
            if (_parent == null || PageLink.IdFrom(ParentBox.Text) != _parent.Id)
            {
                _parent = null;
                ParentCard.Visibility = Visibility.Collapsed;
            }
        };
        Log("Ready. Fill in the three steps, then press Publish…");
    }

    /// <summary>docs\Espanso-Guide-Short.md (or the full guide) in the folder of the exe or any folder above it.</summary>
    private static string? DefaultGuide()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            foreach (var name in new[] { "Espanso-Guide-Short.md", "Espanso-Guide.md" })
            {
                var p = Path.Combine(d.FullName, "docs", name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private Guide? LoadGuide(string path)
    {
        try
        {
            var g = Guide.Load(path);
            FileBox.Text = path;
            // A different file brings its own title; reading the same file again keeps a title the user typed.
            if (path != _loadedPath || TitleBox.Text.Trim().Length == 0) TitleBox.Text = g.Title;
            _loadedPath = path;
            var text = g.Images.Count == 0
                ? "No pictures."
                : $"{g.Images.Count} picture(s) will be attached: {string.Join(", ", g.Images.Select(i => i.Name))}.";
            if (g.MissingImages.Count > 0) text += $"\nNot found next to the file: {string.Join(", ", g.MissingImages)}.";
            PicturesText.Text = text;
            PicturesText.Foreground = (System.Windows.Media.Brush)FindResource(g.MissingImages.Count > 0 ? "WarnBrush" : "MutedBrush");
            _settings.LastFile = path;
            return g;
        }
        catch (Exception ex)
        {
            PicturesText.Text = "Could not read the file: " + ex.Message;
            PicturesText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            return null;
        }
    }

    private string? _loadedPath;

    private void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\n");
        LogBox.ScrollToEnd();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        TestButton.IsEnabled = FindButton.IsEnabled = PublishButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : null;
    }

    /// <summary>A client for what is typed in step 1, or a plain message saying what is missing.</summary>
    private ConfluenceClient NewClient()
    {
        var site = PageLink.Site(SiteBox.Text)
                   ?? throw new ConfluenceException("Type the Confluence site, for example yourcompany.atlassian.net.");
        if (!EmailBox.Text.Contains('@')) throw new ConfluenceException("Type the e-mail address of your Atlassian account.");
        if (TokenBox.Password.Trim().Length == 0)
            throw new ConfluenceException("Paste your Atlassian API token. \"Create a token…\" opens the page where you make one.");
        SiteBox.Text = site.Host;
        return new ConfluenceClient(site, EmailBox.Text.Trim(), TokenBox.Password.Trim());
    }

    private void RememberSignIn()
    {
        _settings.Site = SiteBox.Text.Trim();
        _settings.Email = EmailBox.Text.Trim();
        _settings.SetToken(RememberBox.IsChecked == true ? TokenBox.Password.Trim() : null);
        _settings.Save();
    }

    private void Fail(Exception ex)
    {
        var msg = ex is ConfluenceException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        if (ex is not ConfluenceException) App.Log(ex.ToString());
        Log("Stopped: " + msg.Replace("\n\n", " "));
        MessageBox.Show(this, msg, "Confluence Publisher", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            using var client = NewClient();
            var name = await client.WhoAmIAsync();
            SignInText.Text = $"Signed in as {name}.";
            SignInText.Foreground = (System.Windows.Media.Brush)FindResource("AccentDarkBrush");
            Log($"Signed in to {client.Site.Host} as {name}.");
            RememberSignIn();
        }
        catch (Exception ex)
        {
            SignInText.Text = "Not signed in.";
            SignInText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            Fail(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CreateToken_Click(object sender, RoutedEventArgs e) =>
        Open("https://id.atlassian.com/manage-profile/security/api-tokens");

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pageUrl != null) Open(_pageUrl);
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log("Could not open " + url + ":\n" + ex.Message);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Markdown file to publish",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
        };
        if (File.Exists(FileBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(FileBox.Text);
        if (dlg.ShowDialog(this) == true) LoadGuide(dlg.FileName);
    }

    private void ParentBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Find_Click(sender, e);
    }

    private void ShowParent(PageInfo page)
    {
        _parent = page;
        ParentPathText.Text = page.Path;
        ParentLabel.Text = "Selected parent";
        ParentCard.Visibility = Visibility.Visible;
        ParentProblemText.Visibility = Visibility.Collapsed;
    }

    /// <summary>A link or ID: looks that page up. Anything else: searches page titles.</summary>
    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var text = ParentBox.Text.Trim();
        if (text.Length == 0)
        {
            ParentBox.Focus();
            return;
        }
        SetBusy(true);
        ParentProblemText.Visibility = Visibility.Collapsed;
        try
        {
            using var client = NewClient();
            var id = PageLink.IdFrom(text);
            if (id != null)
            {
                ResultsList.Visibility = Visibility.Collapsed;
                ShowParent(await client.GetPageAsync(id));
                Log("Parent: " + _parent!.Path);
                return;
            }
            var found = await client.SearchAsync(text);
            ResultsList.ItemsSource = found;
            ResultsList.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Log(found.Count == 0 ? $"No page title matches \"{text}\"." : $"{found.Count} page(s) match \"{text}\". Click the parent.");
            if (found.Count == 0)
            {
                ParentProblemText.Text = $"No page title matches \"{text}\". Try other words, or paste the page's link.";
                ParentProblemText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ParentProblemText.Text = ex.Message;
            ParentProblemText.Visibility = Visibility.Visible;
            Log("Stopped: " + ex.Message.Replace("\n\n", " "));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not PageInfo page) return;
        ParentBox.Text = page.WebUrl.Length > 0 ? page.WebUrl : page.Id;
        ShowParent(page);
        ResultsList.Visibility = Visibility.Collapsed;
        Log("Parent: " + page.Path);
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!File.Exists(FileBox.Text))
        {
            Fail(new ConfluenceException("Pick the Markdown file to publish (step 2)."));
            return;
        }
        if (PageLink.IdFrom(ParentBox.Text) == null)
        {
            Fail(new ConfluenceException("Choose the parent page first (step 3): paste its link, or search and click it."));
            return;
        }
        var guide = LoadGuide(FileBox.Text);
        if (guide == null) return;

        SetBusy(true);
        try
        {
            using var client = NewClient();
            Log("Checking the parent page and whether the guide is already there…");
            var plan = await PagePublisher.PlanAsync(client, guide, ParentBox.Text, TitleBox.Text);
            ShowParent(plan.Parent);
            Log((plan.IsUpdate ? "Would update: " : "Would create: ") + plan.TargetPath);

            if (!ConfirmWindow.Ask(this, plan))
            {
                Log("Cancelled. Nothing was written.");
                return;
            }

            RememberSignIn();
            var url = await PagePublisher.ExecuteAsync(client, plan, new Progress<string>(Log));
            _pageUrl = url;
            PageLinkText.Text = (plan.IsUpdate ? "Updated: " : "Created: ") + plan.TargetPath + "  (open)";
            OpenLinkBlock.Visibility = Visibility.Visible;
            _settings.LastParent = ParentBox.Text.Trim();
            _settings.LastParentPath = plan.Parent.Path;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Site = SiteBox.Text.Trim();
        _settings.Email = EmailBox.Text.Trim();
        if (RememberBox.IsChecked != true) _settings.SetToken(null);
        _settings.Save();
    }
}
