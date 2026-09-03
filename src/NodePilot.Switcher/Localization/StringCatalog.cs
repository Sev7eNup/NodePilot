using System.Globalization;
using NodePilot.Switcher.Models;

namespace NodePilot.Switcher.Localization;

internal sealed class StringCatalog
{
    private readonly bool _german = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

    public string AppTitle => Text("NodePilot Switcher", "NodePilot Switcher");
    public string PageTitle => Text("Switcher", "Switcher");
    public string ChooseEngine => Text("Wählen Sie die aktive Orchestrierungs-Engine", "Choose the active orchestration engine");
    public string NodePilot => "NodePilot";
    public string SystemCenter => "System Center Orchestrator";
    public string ServiceAvailability => Text("DIENSTVERFÜGBARKEIT", "SERVICE AVAILABILITY");
    public string NodePilotInstallationFound => Text("NodePilot-Installation gefunden", "NodePilot installation found");
    public string OrchestratorInstallationFound => Text("Orchestrator-Installation gefunden", "Orchestrator installation found");
    public string Yes => Text("Ja", "Yes");
    public string No => Text("Nein", "No");
    public string NoServices => Text("Keine", "None");
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
        "NodePilot Switcher wird bereits ausgeführt.",
        "NodePilot Switcher is already running.");
    public string SwitchToNodePilot => Text("Zu NodePilot wechseln", "Switch to NodePilot");
    public string SwitchToSystemCenter => Text("Zu System Center wechseln", "Switch to System Center");
    public string Unavailable => Text("Nicht verfügbar", "Unavailable");
    public string Refreshing => Text("Dienststatus wird gelesen …", "Reading service status…");
    public string NoActivity => Text("Noch keine Umschaltaktivität.", "No switch activity yet.");
    public string NodePilotSignInTitle => Text("Bei NodePilot anmelden", "Sign in to NodePilot");
    public string Username => Text("Benutzername", "Username");
    public string Password => Text("Kennwort", "Password");
    public string SignIn => Text("Anmelden und fortfahren", "Sign in and continue");
    public string Cancel => Text("Abbrechen", "Cancel");
    public string CredentialsRequired => Text(
        "Benutzername und Kennwort sind erforderlich.",
        "Username and password are required.");

    public string NodePilotSignInExplanation(string profile) => Text(
        $"Die gespeicherte Sitzung für das CLI-Profil '{profile}' ist abgelaufen. Melden Sie sich erneut an; der laufende Wechsel wird danach automatisch fortgesetzt. Das Kennwort wird nicht gespeichert.",
        $"The saved session for CLI profile '{profile}' has expired. Sign in again and the current switch will resume automatically. The password is not stored.");

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
    public string ConfirmExplanation => Text(
        "Es ist immer nur eine Orchestrierungs-Engine aktiv.",
        "Only one orchestration engine is active at a time.");
    public string ServicesToStop => Text("WIRD GESTOPPT", "WILL BE STOPPED");
    public string ServicesToStart => Text("WIRD AKTIVIERT", "WILL BE ACTIVATED");
    public string ConfirmSwitchNotice => Text(
        "Die Allowlist wird exakt angewendet. Nicht gelistete Workflows oder Runbooks werden beendet. Die Auswahl bleibt nach einem Neustart aktiv.",
        "The allowlist is applied exactly. Unlisted workflows or runbooks are stopped. The selection remains active after a restart.");
    public string ConfirmSwitchHeading(SwitchTarget target) => Text(
        $"{Target(target)} aktivieren?",
        $"Activate {Target(target)}?");
    public string ActivateTarget(SwitchTarget target) => Text(
        target == SwitchTarget.NodePilot ? "NodePilot aktivieren" : "Orchestrator aktivieren",
        target == SwitchTarget.NodePilot ? "Activate NodePilot" : "Activate Orchestrator");
    public string ConfigurationInvalidStatus => Text("Konfiguration fehlerhaft", "Configuration invalid");
    public string ConfigurationUnusable(string error) => Text(
        $"Konfiguration nicht verwendbar: {error}",
        $"Configuration unusable: {error}");
    public string ErrorTitle => Text("Wechsel fehlgeschlagen", "Switch failed");
    public string ErrorMessage(string error) => Text(
        $"Der Wechsel wurde abgebrochen. Bei einem Fehler nach Beginn des Dienstwechsels werden alle erreichbaren verwalteten Dienste gestoppt und auf manuellen Start gesetzt. Ein Fehler in der Vorprüfung verändert keine Dienste.\n\n{error}",
        $"The switch was aborted. A failure after service switching begins stops all reachable managed services and sets them to manual start. A preflight failure does not change any service.\n\n{error}");

    private string Target(SwitchTarget target) => target == SwitchTarget.NodePilot ? NodePilot : SystemCenter;
    private string Text(string german, string english) => _german ? german : english;
}
