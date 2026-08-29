using System.Globalization;
using NodePilot.ServiceSwitcher.Models;

namespace NodePilot.ServiceSwitcher.Localization;

internal sealed class StringCatalog
{
    private readonly bool _german = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

    public string AppTitle => Text("NodePilot Engine Switcher", "NodePilot Engine Switcher");
    public string PageTitle => Text("Engine Switcher", "Engine Switcher");
    public string ChooseEngine => Text("Wählen Sie die aktive Orchestrierungs-Engine", "Choose the active orchestration engine");
    public string NodePilot => "NodePilot";
    public string SystemCenter => "System Center Orchestrator";
    public string ServiceAvailability => Text("DIENSTVERFÜGBARKEIT", "SERVICE AVAILABILITY");
    public string NodePilotInstallationFound => Text("NodePilot-Installation gefunden", "NodePilot installation found");
    public string OrchestratorInstallationFound => Text("Orchestrator-Installation gefunden", "Orchestrator installation found");
    public string Yes => Text("Ja", "Yes");
    public string No => Text("Nein", "No");
    public string SwitchToDarkMode => Text("Zum Dark Mode wechseln", "Switch to dark mode");
    public string SwitchToLightMode => Text("Zum Light Mode wechseln", "Switch to light mode");
    public string LastActivities => Text("Aktivitätsverlauf", "Activity history");
    public string SystemCheckCompleted => Text("System-Check erfolgreich durchgeführt", "System check completed successfully");
    public string Activity => Text("Aktivität", "Activity");
    public string Copy => Text("Kopieren", "Copy");
    public string Ready => Text("Bereit", "Ready");
    public string SwitchInProgress => Text("Wechsel läuft", "Switch in progress");
    public string SwitchFailedStatus => Text("Wechsel fehlgeschlagen", "Switch failed");
    public string CheckRequired => Text("Prüfung erforderlich", "Check required");
    public string DetailsInActivityHistory => Text(
        "Details im Aktivitätsverlauf",
        "See activity history for details");
    public string NoSuccessfulSwitch => Text(
        "Noch kein erfolgreicher Wechsel",
        "No successful switch yet");
    public string AlreadyRunning => Text(
        "NodePilot Engine Switcher wird bereits ausgeführt.",
        "NodePilot Engine Switcher is already running.");
    public string SwitchToNodePilot => Text("Zu NodePilot wechseln", "Switch to NodePilot");
    public string SwitchToSystemCenter => Text("Zu System Center wechseln", "Switch to System Center");
    public string Unavailable => Text("Nicht verfügbar", "Unavailable");
    public string Refreshing => Text("Dienststatus wird gelesen …", "Reading service status…");
    public string NoActivity => Text("Noch keine Umschaltaktivität.", "No switch activity yet.");

    public string StateTitle(EnvironmentState state) => state switch
    {
        EnvironmentState.NodePilotActive => Text("NodePilot ist aktiv", "NodePilot is active"),
        EnvironmentState.SystemCenterActive => Text("System Center Orchestrator ist aktiv", "System Center Orchestrator is active"),
        EnvironmentState.BothStopped => Text("Beide Orchestratoren sind gestoppt", "Both orchestrators are stopped"),
        EnvironmentState.Conflict => Text("Konflikt: Beide Seiten sind aktiv", "Conflict: both sides are active"),
        EnvironmentState.SystemCenterPartial => Text("System Center ist nur teilweise aktiv", "System Center is only partially active"),
        EnvironmentState.Transitioning => Text("Ein Dienst wechselt gerade den Zustand", "A service is changing state"),
        _ => Text("Dienste nicht vollständig verfügbar", "Services are not fully available"),
    };

    public string Progress(SwitchProgress progress) => progress.Kind switch
    {
        SwitchProgressKind.Preparing => Text("Wechsel wird vorbereitet …", "Preparing switch…"),
        SwitchProgressKind.LoadingAllowList => Text("Allowlist wird geladen …", "Loading allowlist…"),
        SwitchProgressKind.SettingManual => Text("Automatischen Start deaktivieren", "Disabling automatic start"),
        SwitchProgressKind.Stopping => Text("Dienst wird gestoppt", "Stopping service"),
        SwitchProgressKind.SettingAutomatic => Text("Automatischen Start aktivieren", "Enabling automatic start"),
        SwitchProgressKind.Starting => Text("Dienst wird gestartet", "Starting service"),
        SwitchProgressKind.ReconcilingWorkloads => Text("Allowlist wird angewendet", "Applying allowlist"),
        SwitchProgressKind.Verifying => Text("Dienst- und Allowlist-Status wird geprüft …", "Verifying service and allowlist status…"),
        SwitchProgressKind.Completed => Text("Wechsel abgeschlossen", "Switch completed"),
        SwitchProgressKind.FailClosed => Text("Fehlerzustand wird sicher gestoppt …", "Stopping services after failure…"),
        _ => Ready,
    } + (progress.ServiceName is null ? string.Empty : $" · {progress.ServiceName}");

    public string SwitchDestination(SwitchTarget target) => Text(
        $"Ziel: {Target(target)}",
        $"Target: {Target(target)}");

    public string LastSuccessfulSwitch(DateTimeOffset timestamp) => Text(
        $"Letzter erfolgreicher Wechsel: {timestamp:HH:mm}",
        $"Last successful switch: {timestamp:HH:mm}");

    public string ConfirmTitle => Text("Orchestrator wechseln?", "Switch orchestrator?");
    public string ConfirmMessage(SwitchTarget target, IEnumerable<string> stop, IEnumerable<string> start) =>
        Text(
            $"Ziel: {Target(target)}\n\nStoppen: {Join(stop)}\nStarten: {Join(start)}\n\nDie Allowlist wird exakt angewendet: Nicht gelistete NodePilot-Workflows werden deaktiviert und abgebrochen; nicht gelistete SCOrch-Jobs werden gestoppt. Nicht sauber stoppende Dienste dürfen nach 30 Sekunden gezielt beendet werden. Die Auswahl bleibt nach einem Neustart aktiv.",
            $"Target: {Target(target)}\n\nStop: {Join(stop)}\nStart: {Join(start)}\n\nThe allowlist is applied exactly: unlisted NodePilot workflows are disabled and cancelled; unlisted SCOrch jobs are stopped. Services that do not stop cleanly may be terminated after 30 seconds. The selection persists after restart.");
    public string ErrorTitle => Text("Wechsel fehlgeschlagen", "Switch failed");
    public string ErrorMessage(string error) => Text(
        $"Der Wechsel wurde abgebrochen. Bei einem Fehler nach Beginn des Dienstwechsels werden alle erreichbaren verwalteten Dienste gestoppt und auf manuellen Start gesetzt. Ein Fehler in der Vorprüfung verändert keine Dienste.\n\n{error}",
        $"The switch was aborted. A failure after service switching begins stops all reachable managed services and sets them to manual start. A preflight failure does not change any service.\n\n{error}");

    private string Target(SwitchTarget target) => target == SwitchTarget.NodePilot ? NodePilot : SystemCenter;
    private string Join(IEnumerable<string> names) => string.Join(", ", names.DefaultIfEmpty(Text("keine", "none")));
    private string Text(string german, string english) => _german ? german : english;
}
