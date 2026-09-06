using System.Windows;
using System.Windows.Threading;
using NodePilot.Switcher.Localization;
using NodePilot.Switcher.Models;
using NodePilot.Switcher.Services;

namespace NodePilot.Switcher.ViewModels;

internal interface IUserInteraction
{
    Task<bool> ConfirmSwitchAsync(SwitchTarget target, ManagedEnvironmentSnapshot snapshot);
    void ShowError(string error);
}

internal sealed class DialogUserInteraction : IUserInteraction, INodePilotCredentialPrompt
{
    private readonly StringCatalog _strings;

    public DialogUserInteraction(StringCatalog strings) => _strings = strings;

    public Task<bool> ConfirmSwitchAsync(SwitchTarget target, ManagedEnvironmentSnapshot snapshot)
    {
        var stop = target == SwitchTarget.NodePilot
            ? snapshot.SystemCenterServices.Select(service => service.Name)
            : snapshot.NodePilot is null ? [] : new[] { snapshot.NodePilot.Name };
        var start = target == SwitchTarget.NodePilot
            ? snapshot.NodePilot is null ? [] : new[] { snapshot.NodePilot.Name }
            : snapshot.SystemCenterServices.Select(service => service.Name);
        var dialog = new SwitchConfirmationWindow(_strings, target, stop, start)
        {
            Owner = Application.Current.MainWindow,
        };
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public void ShowError(string error) => MessageBox.Show(
        _strings.ErrorMessage(error),
        _strings.ErrorTitle,
        MessageBoxButton.OK,
        MessageBoxImage.Error);

    public Task<NodePilotCredentials?> PromptAsync(
        string profile,
        string? previousError,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.CheckAccess()
            ? Task.FromResult(ShowCredentialDialog(profile, previousError))
            : dispatcher.InvokeAsync(
                () => ShowCredentialDialog(profile, previousError),
                DispatcherPriority.Normal,
                cancellationToken).Task;
    }

    private NodePilotCredentials? ShowCredentialDialog(string profile, string? previousError)
    {
        var dialog = new NodePilotLoginWindow(_strings, profile, previousError)
        {
            Owner = Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Credentials : null;
    }
}
