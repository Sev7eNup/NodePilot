using System.Windows;
using NodePilot.ServiceSwitcher.Localization;
using NodePilot.ServiceSwitcher.Models;
using NodePilot.ServiceSwitcher.Services;

namespace NodePilot.ServiceSwitcher;

internal partial class SwitchConfirmationWindow : Window
{
    internal SwitchConfirmationWindow(
        StringCatalog strings,
        SwitchTarget target,
        IEnumerable<string> servicesToStop,
        IEnumerable<string> servicesToStart)
    {
        InitializeComponent();
        Title = strings.ConfirmTitle;
        HeadingText.Text = strings.ConfirmSwitchHeading(target);
        ExplanationText.Text = strings.ConfirmExplanation;
        StopLabel.Text = strings.ServicesToStop;
        StartLabel.Text = strings.ServicesToStart;
        StopServicesText.Text = JoinServices(servicesToStop, strings);
        StartServicesText.Text = JoinServices(servicesToStart, strings);
        NoticeText.Text = strings.ConfirmSwitchNotice;
        CancelButton.Content = strings.Cancel;
        ConfirmButton.Content = strings.ActivateTarget(target);

        SourceInitialized += (_, _) => new ThemeService().ApplyCurrentTheme(this);
    }

    private static string JoinServices(IEnumerable<string> services, StringCatalog strings) =>
        string.Join(Environment.NewLine, services.DefaultIfEmpty(strings.NoServices));

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
