using System.Windows;

namespace EverLinkHost;

/// <summary>
/// Small read-only popup listing a single Relay's known feature-compatibility issues
/// (see DeviceInfo.CompatibilityIssues) - e.g. a v1 firmware lacking rumble support, or a
/// chip model without USB OTG. Opened from the warning button next to a Relay row in the
/// main list (see MainWindow.OpenIssuesButton_Click), which only appears at all when
/// there's at least one issue to show.
/// </summary>
public partial class RelayIssuesWindow : Window
{
    public RelayIssuesWindow(IReadOnlyList<string> issues)
    {
        InitializeComponent();
        WindowChromeHelper.EnableDarkTitleBar(this);
        IssuesList.ItemsSource = issues;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
