using System.Windows;
using DualNetwork.Core.Planning;

namespace DualNetwork.App;

public partial class ConfirmWindow : Window
{
    public sealed record ChangeLine(string Command, string Reason);

    public ConfirmWindow(RoutingPlan plan, string title, bool advanced, bool readOnly = false)
    {
        InitializeComponent();
        TitleText.Text = title;
        if (readOnly)
        {
            ApplyButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            FooterText.Text = "All changes are temporary: they are removed when Dual Network is turned off or exits, or when Windows restarts.";
        }
        SummaryList.ItemsSource = plan.Summary;
        WarningList.ItemsSource = plan.Warnings;

        var changes = plan.Routes.Select(r => new ChangeLine(r.ToPowerShell(), r.Reason))
            .Concat(plan.NrptRules.Select(n => new ChangeLine(n.ToPowerShell(), n.Reason)))
            .ToList();
        ChangeList.ItemsSource = changes;
        NoChangesText.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesExpander.Header = $"Exact Windows changes ({changes.Count})";
        ChangesExpander.IsExpanded = advanced;

        PreservedList.ItemsSource = plan.Preserved;
        PreservedExpander.Header = $"Preserved — not modified ({plan.Preserved.Count})";
        PreservedExpander.Visibility = plan.Preserved.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PreservedExpander.IsExpanded = advanced;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
