using System.Windows;
using NodePilot.ServiceSwitcher.Localization;
using NodePilot.ServiceSwitcher.Models;

namespace NodePilot.ServiceSwitcher.ViewModels;

internal interface IUserInteraction
{
    Task<bool> ConfirmSwitchAsync(SwitchTarget target, ManagedEnvironmentSnapshot snapshot);
    void ShowError(string error);
}

internal sealed class MessageBoxUserInteraction : IUserInteraction
{
    private readonly StringCatalog _strings;

    public MessageBoxUserInteraction(StringCatalog strings) => _strings = strings;

    public Task<bool> ConfirmSwitchAsync(SwitchTarget target, ManagedEnvironmentSnapshot snapshot)
    {
        var stop = target == SwitchTarget.NodePilot
            ? snapshot.SystemCenterServices.Select(service => service.Name)
            : snapshot.NodePilot is null ? [] : new[] { snapshot.NodePilot.Name };
        var start = target == SwitchTarget.NodePilot
            ? snapshot.NodePilot is null ? [] : new[] { snapshot.NodePilot.Name }
            : snapshot.SystemCenterServices.Select(service => service.Name);
        var answer = MessageBox.Show(
            _strings.ConfirmMessage(target, stop, start),
            _strings.ConfirmTitle,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return Task.FromResult(answer == MessageBoxResult.OK);
    }

    public void ShowError(string error) => MessageBox.Show(
        _strings.ErrorMessage(error),
        _strings.ErrorTitle,
        MessageBoxButton.OK,
        MessageBoxImage.Error);
}
