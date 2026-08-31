using System.Windows;
using NodePilot.EngineSwitcher.Localization;
using NodePilot.EngineSwitcher.Services;

namespace NodePilot.EngineSwitcher;

internal partial class NodePilotLoginWindow : Window
{
    private readonly StringCatalog _strings;

    internal NodePilotLoginWindow(StringCatalog strings, string profile, string? previousError)
    {
        _strings = strings;
        InitializeComponent();
        Title = strings.NodePilotSignInTitle;
        HeadingText.Text = strings.NodePilotSignInTitle;
        ExplanationText.Text = strings.NodePilotSignInExplanation(profile);
        UsernameLabel.Text = strings.Username;
        PasswordLabel.Text = strings.Password;
        CancelButton.Content = strings.Cancel;
        SignInButton.Content = strings.SignIn;
        ShowError(previousError);

        Loaded += (_, _) => UsernameInput.Focus();
        SourceInitialized += (_, _) => new ThemeService().ApplyCurrentTheme(this);
    }

    internal NodePilotCredentials? Credentials { get; private set; }

    private void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UsernameInput.Text) || PasswordInput.Password.Length == 0)
        {
            ShowError(_strings.CredentialsRequired);
            return;
        }

        Credentials = new NodePilotCredentials(UsernameInput.Text.Trim(), PasswordInput.Password);
        PasswordInput.Clear();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        PasswordInput.Clear();
        DialogResult = false;
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(error)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
