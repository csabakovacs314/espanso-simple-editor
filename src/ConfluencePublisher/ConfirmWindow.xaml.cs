using System.Windows;
using Publisher.Core;

namespace ConfluencePublisher;

/// <summary>Shows exactly where the guide goes and whether a page is created or replaced. Nothing is written without "yes".</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    public static bool Ask(Window owner, PublishPlan plan)
    {
        var d = new ConfirmWindow { Owner = owner };
        d.HeadingText.Text = plan.IsUpdate
            ? $"Update \"{plan.Title}\" on {plan.Site.Host}?"
            : $"Create \"{plan.Title}\" on {plan.Site.Host}?";
        d.ParentText.Text = plan.Parent.Path;
        if (plan.Existing is { } page)
        {
            d.UpdateNote.Visibility = Visibility.Visible;
            d.UpdateText.Text = $"This parent already has a page called \"{page.Title}\". Its text will be replaced (version {page.Version} becomes {page.Version + 1}); "
                                + "Confluence keeps the old version in the page history.";
            d.ConfirmButton.Content = $"Update page (v{page.Version} to v{page.Version + 1})";
        }
        else
        {
            d.ConfirmButton.Content = "Create page";
        }
        d.DetailsBox.Text = plan.Describe();
        return d.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
