using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NodePilot.EngineSwitcher.Localization;
using NodePilot.EngineSwitcher.Configuration;
using NodePilot.EngineSwitcher.Services;
using NodePilot.EngineSwitcher.ViewModels;

namespace NodePilot.EngineSwitcher;

public partial class MainWindow : Window
{
    private readonly ThemeService _theme = new();
    private readonly StringCatalog _strings;
    private readonly DispatcherTimer _timer;
    private readonly MainWindowViewModel _viewModel;
    private bool _refreshing;

    public MainWindow()
    {
        var light = _theme.ApplyCurrentTheme();
        InitializeComponent();
        _strings = new StringCatalog();
        var gateway = new WindowsServiceControlGateway();
        var discovery = new ServiceDiscovery(gateway);
        var logger = new ActivityLogger();
        var interaction = new DialogUserInteraction(_strings);
        var configurationLoader = new SwitcherConfigurationLoader();
        var workloads = new WorkloadReconciler(
            configurationLoader,
            new AllowListReader(),
            new NodePilotWorkflowReconciler(new ProcessCommandRunner(), logger, interaction),
            new ScorchRunbookReconciler(new ScorchApiClientFactory(), logger),
            logger);
        var coordinator = new SwitchCoordinator(gateway, discovery, logger, workloads);
        _viewModel = new MainWindowViewModel(
            coordinator,
            logger,
            interaction,
            new SwitcherConfigurationProbe(configurationLoader),
            _strings);
        DataContext = _viewModel;
        UpdateThemeToggle(light);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += RefreshTimerOnTick;
        Loaded += OnLoaded;
        SourceInitialized += (_, _) => UpdateThemeToggle(_theme.ApplyCurrentTheme(this));
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateThemeToggle(_theme.ApplyCurrentTheme(this));
        await RefreshSafelyAsync();
        _timer.Start();
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        await RefreshSafelyAsync();
    }

    private async Task RefreshSafelyAsync()
    {
        if (_refreshing || _viewModel.IsBusy) return;
        _refreshing = true;
        try { await _viewModel.RefreshAsync(); }
        finally { _refreshing = false; }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        UpdateThemeToggle(_theme.ToggleTheme(this));
        Keyboard.ClearFocus();
    }

    private void UpdateThemeToggle(bool light)
    {
        ThemeToggleThumb.HorizontalAlignment = light ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var accessibleName = light ? _strings.SwitchToDarkMode : _strings.SwitchToLightMode;
        ThemeToggleButton.ToolTip = accessibleName;
        System.Windows.Automation.AutomationProperties.SetName(ThemeToggleButton, accessibleName);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            WindowState == WindowState.Maximized ? "Restore" : "Maximize");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true;
        var german = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        MessageBox.Show(
            german
                ? "Der Dienstwechsel läuft noch. Das Fenster kann nach Abschluss geschlossen werden."
                : "The service switch is still running. The window can be closed when it finishes.",
            _viewModel.Strings.AppTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
