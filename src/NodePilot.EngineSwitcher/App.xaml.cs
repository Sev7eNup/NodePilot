using System.Threading;
using System.Windows;
using NodePilot.EngineSwitcher.Localization;

namespace NodePilot.EngineSwitcher;

public partial class App : Application
{
    private const string MutexName = @"Global\NodePilot.EngineSwitcher";
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            var strings = new StringCatalog();
            MessageBox.Show(
                strings.AlreadyRunning,
                strings.AppTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
