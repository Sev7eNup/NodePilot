using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using NodePilot.ServiceSwitcher.Localization;
using NodePilot.ServiceSwitcher.Models;
using NodePilot.ServiceSwitcher.Services;

namespace NodePilot.ServiceSwitcher.ViewModels;

internal sealed record ActivityItemViewModel(
    DateTimeOffset Timestamp,
    string Message,
    bool IsError,
    bool IsSuccess)
{
    public string Time => Timestamp.ToString("HH:mm");
    public string CopyText => $"{Timestamp:HH:mm:ss}  {Message}";
}

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SwitchCoordinator _coordinator;
    private readonly IActivityLogger _logger;
    private readonly IUserInteraction _interaction;
    private readonly SynchronizationContext? _uiContext;
    private ManagedEnvironmentSnapshot _snapshot = new(null, []);
    private EnvironmentState _state = EnvironmentState.Unavailable;
    private bool _isBusy;
    private bool _initialRefreshLogged;
    private bool _lastSwitchFailed;
    private DateTimeOffset? _lastSuccessfulSwitchAt;
    private SwitchTarget? _activeSwitchTarget;
    private string _progressText;

    public MainWindowViewModel(
        SwitchCoordinator coordinator,
        IActivityLogger logger,
        IUserInteraction interaction,
        StringCatalog strings)
    {
        _coordinator = coordinator;
        _logger = logger;
        _interaction = interaction;
        Strings = strings;
        _progressText = strings.Refreshing;
        _uiContext = SynchronizationContext.Current;
        SwitchToNodePilotCommand = new AsyncCommand(
            () => SwitchAsync(SwitchTarget.NodePilot),
            () => CanSwitchToNodePilot);
        SwitchToSystemCenterCommand = new AsyncCommand(
            () => SwitchAsync(SwitchTarget.SystemCenterOrchestrator),
            () => CanSwitchToSystemCenter);
        CopyActivityCommand = new AsyncCommand(CopyActivityAsync, () => ActivityItems.Count > 0);
        _logger.EntryWritten += OnEntryWritten;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public StringCatalog Strings { get; }
    public AsyncCommand SwitchToNodePilotCommand { get; }
    public AsyncCommand SwitchToSystemCenterCommand { get; }
    public AsyncCommand CopyActivityCommand { get; }
    public ObservableCollection<ActivityItemViewModel> ActivityItems { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            RaiseCommandState();
            OnPropertyChanged(nameof(CanSwitchToNodePilot));
            OnPropertyChanged(nameof(CanSwitchToSystemCenter));
            RaiseFooterProperties();
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set
        {
            if (Set(ref _progressText, value)) OnPropertyChanged(nameof(FooterDetailText));
        }
    }

    public string StateTitle => Strings.StateTitle(_state);
    public bool NodePilotRunning => _snapshot.NodePilot?.State == ServiceRuntimeState.Running;
    public bool SystemCenterRunning => _snapshot.SystemCenterServices.Count > 0
                                       && _snapshot.SystemCenterServices.All(service => service.State == ServiceRuntimeState.Running);
    public bool NodePilotAvailable => _snapshot.NodePilot is not null;
    public bool SystemCenterAvailable => _snapshot.SystemCenterServices.Count > 0;
    public string NodePilotAvailabilityText => NodePilotAvailable ? Strings.Yes : Strings.No;
    public string SystemCenterAvailabilityText => SystemCenterAvailable ? Strings.Yes : Strings.No;
    public bool HasActivity => ActivityItems.Count > 0;
    public bool CanSwitchToNodePilot => !IsBusy && NodePilotAvailable && _state != EnvironmentState.NodePilotActive;
    public bool CanSwitchToSystemCenter => !IsBusy && SystemCenterAvailable && _state != EnvironmentState.SystemCenterActive;
    public bool FooterStatusIsBusy => IsBusy;
    public bool FooterStatusIsError => !IsBusy && _lastSwitchFailed;
    public bool FooterStatusIsWarning => !IsBusy && !_lastSwitchFailed
                                         && _state is not EnvironmentState.NodePilotActive
                                         and not EnvironmentState.SystemCenterActive;
    public string FooterStatusText => IsBusy
        ? Strings.SwitchInProgress
        : _lastSwitchFailed
            ? Strings.SwitchFailedStatus
            : FooterStatusIsWarning ? Strings.CheckRequired : Strings.Ready;
    public string FooterDetailText => IsBusy && _activeSwitchTarget is { } target
        ? $"{Strings.SwitchDestination(target)} · {ProgressText}"
        : _lastSwitchFailed ? Strings.DetailsInActivityHistory : StateTitle;
    public string FooterLastSwitchText => _lastSuccessfulSwitchAt is { } timestamp
        ? Strings.LastSuccessfulSwitch(timestamp)
        : Strings.NoSuccessfulSwitch;

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        try
        {
            _snapshot = await Task.Run(_coordinator.Refresh);
            _state = EnvironmentStateEvaluator.Assess(_snapshot);
            ProgressText = Strings.Ready;
            if (!_initialRefreshLogged)
            {
                _initialRefreshLogged = true;
                _logger.Info(Strings.SystemCheckCompleted);
            }
        }
        catch (Exception exception)
        {
            _snapshot = new ManagedEnvironmentSnapshot(null, []);
            _state = EnvironmentState.Unavailable;
            ProgressText = exception.Message;
        }
        RaiseSnapshotProperties();
        RefreshActivityHistory();
    }

    internal async Task SwitchAsync(SwitchTarget target)
    {
        await RefreshAsync();
        if (!await _interaction.ConfirmSwitchAsync(target, _snapshot)) return;

        _activeSwitchTarget = target;
        _lastSwitchFailed = false;
        IsBusy = true;
        var progress = new Progress<SwitchProgress>(value => ProgressText = Strings.Progress(value));
        try
        {
            var result = await _coordinator.SwitchAsync(target, progress, CancellationToken.None);
            _snapshot = result.Snapshot;
            _state = EnvironmentStateEvaluator.Assess(_snapshot);
            _lastSwitchFailed = !result.Succeeded;
            if (result.Succeeded) _lastSuccessfulSwitchAt = DateTimeOffset.Now;
            if (!result.Succeeded) _interaction.ShowError(result.Error ?? Strings.Unavailable);
        }
        finally
        {
            _activeSwitchTarget = null;
            IsBusy = false;
            ProgressText = Strings.Ready;
            RaiseSnapshotProperties();
            RefreshActivityHistory();
        }
    }

    private void OnEntryWritten(object? sender, ActivityEntry entry)
    {
        if (_uiContext is null) RefreshActivityHistory();
        else _uiContext.Post(_ => RefreshActivityHistory(), null);
    }

    private void RefreshActivityHistory()
    {
        var entries = _logger.Entries.Reverse().ToArray();
        ActivityItems.Clear();
        foreach (var entry in entries)
        {
            var message = entry.ServiceName is null ? entry.Message : $"{entry.ServiceName} · {entry.Message}";
            ActivityItems.Add(new ActivityItemViewModel(
                entry.Timestamp,
                message,
                entry.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase),
                entry.Level.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)));
        }
        OnPropertyChanged(nameof(HasActivity));
        CopyActivityCommand.RaiseCanExecuteChanged();
    }

    private Task CopyActivityAsync()
    {
        Clipboard.SetText(string.Join(
            Environment.NewLine,
            ActivityItems.Select(item => item.CopyText)));
        return Task.CompletedTask;
    }

    private void RaiseSnapshotProperties()
    {
        foreach (var property in new[]
                 {
                     nameof(StateTitle), nameof(NodePilotRunning),
                     nameof(SystemCenterRunning), nameof(NodePilotAvailable), nameof(SystemCenterAvailable),
                     nameof(NodePilotAvailabilityText), nameof(SystemCenterAvailabilityText),
                     nameof(CanSwitchToNodePilot), nameof(CanSwitchToSystemCenter),
                 })
            OnPropertyChanged(property);
        RaiseFooterProperties();
        RaiseCommandState();
    }

    private void RaiseFooterProperties()
    {
        foreach (var property in new[]
                 {
                     nameof(FooterStatusIsBusy), nameof(FooterStatusIsError), nameof(FooterStatusIsWarning),
                     nameof(FooterStatusText), nameof(FooterDetailText), nameof(FooterLastSwitchText),
                 })
            OnPropertyChanged(property);
    }

    private void RaiseCommandState()
    {
        SwitchToNodePilotCommand.RaiseCanExecuteChanged();
        SwitchToSystemCenterCommand.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
