using System.Windows.Input;

namespace NodePilot.EngineSwitcher.ViewModels;

internal sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _executing;

    public AsyncCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_executing && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _executing = true;
        RaiseCanExecuteChanged();
        try { await _execute(); }
        // Execute is async void, so an exception that escapes here reaches the WPF dispatcher and
        // terminates the process. Report it instead; the command must never take the app down.
        catch (Exception exception) { _onError?.Invoke(exception); }
        finally
        {
            _executing = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
