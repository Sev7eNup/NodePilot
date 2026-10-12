# Runde 6 – unabhängige Gegenprüfung Clients/Delivery

Datum: 12. Oktober 2026. Branch `audit/practical-impact-2026-10-12`.
Prüfer: Root, nach der Runde-5-Prüfung durch `clients_explore`.
Beide Skills: `improve-codebase-architecture` und `audit-ai-slop-code`.
Ergebnis: **keine neuen bestätigten Critical-/High- oder alltagsrelevanten Medium-Funde**
in diesem Teilumfang. Der projektweite Abschluss steht im Rundenbericht.

## Frisch gegengeprüfte Pfade

| Familie | Architektur- und Sicherheitsprüfung |
| --- | --- |
| CLI | SessionResolver und ClientFactory: Identität/Pin an Origin gebunden, Switcher-Key ohne Redirect/Fallback. Workflowstart, Parameterübergabe, Polling und SignalR-Watcher einschließlich Streamabbruch und Catch-up. Backup-Export/Preview/Restore, Passphrasenquellen, Import-/Exportverträge gezielt. Finale begrenzte Execution-Paginierung und tatsächliche HTTP-Regressionen von #140. |
| MCP | Session/Config/ClientFactory mit TLS-Fehlern und Originbindung; Execution-Listen/Fehlerkontext, Vertrags- und Einzelknotenredaktion nach #138/#140. DefinitionRedactor und WorkflowDefinitionPatcher: gemeinsame Redaktionsregeln, maskierte Werte beim Roundtrip, verschachtelte Arrays, Validierung vor Save. Edit-/Publish-/Import-/Previewpfade und serverseitige Berechtigungsverantwortung. |
| Switcher | NodePilotWorkflowReconciler einschließlich initialem Readiness-Budget, Auth-/Maschinenkey-Vertrag, Allowlist-Auflösung und abschließender Verifikation. SwitchCoordinator: Start-/Stopreihenfolge, Fail-closed und Deadline. SCOrch-Client/Reconciler: Originprüfung bei Redirect/Paging, Windows-Identität, Mutation/Verifikation und regulär abgeschlossene Jobs. |
| Desktop | main/config/security/setup-preload/backendRestart frisch gelesen: ausschließlich lokales HTTPS-Ziel, Zertifikatbindung, Cookie-/Bootstrapübergabe, IPC-Absender, getrennte Setup-/Appfenster, Sandbox, Navigation, Downloads und Doku-Unterfenster. Kein zusätzlicher Rendererzugriff auf Servicebefehle gefunden. |
| Installation/Updates | ArtifactSecurity-Verzeichnisrechte und Eigentümer; Installationspfadprüfung, sichere Staging-/Dateiverifikation, Dienstidentitätswechsel und geschützte Datendateien. Update vor Stop verifiziert, gehärtetes Ziel, unveränderte Konfiguration, Switcher-Konfigurationsübernahme, Healthprobe und begrenzter Binär-Rollback. Setupadapter leitet Überinstallation an denselben Updatepfad. Administrators/SYSTEM behalten benötigte Rechte. |
| Betrieb/Build | CI-Trigger, fehlertolerante Pfadklassifizierung, Paketaudit und Deployment-Vertragsprüfungen; Pages-Publishgrenze. Grafana/Prometheus nur Loopback, Passwortpflicht und deaktivierter anonymer Zugang. Publish-Site: explizite Konfiguration, SFTP-Pin/FTPS, Buildfehlerpropagierung. Python-Adminhelfer: explizites Login, Namensauflösung, Lock-/Publish-Vertrag und HTTP-Fehlerprüfung. Weitere Harness-/Fixtureverträge anhand des Runde-5-Berichts und Inventars stichprobenartig abgeglichen. |

Die Prüfung verfolgt reale Nutzungs- und Vertrauenspfade über die Modulgrenzen.
Sie ist keine erneute Vollzeilenlektüre jedes Fixtures, jeder Ressource oder jedes
Installer-Dialoglayouts. Gemeinsame Regeln bleiben in Core/API; die unterschiedlichen
Lebenszyklen von CLI, MCP und Switcher rechtfertigen ihre eigenen Adapter. Keine
zusätzliche Abstraktion nur wegen ähnlicher HTTP-Aufrufe eingeführt.

## Gegenbelege und Verifikation

- #135 wiederholt nur den ersten lesenden Inventarabruf bei Exitcode 5; TLS/Auth,
  Workflowschreibzugriffe und abschließende Verifikation erhalten keine neue Wiederholung.
- #138 redigiert vor der Teilprojektion. Maskierte Vertragsdefaults werden weggelassen,
  damit der Server seinen tatsächlichen Default verwendet.
- #140 CLI behält eine konstante Seitengröße über mehrere Seiten; das verhindert
  Überschneidungen beim Limit 250. Ohne positives Limit bleibt der Vollabruf erhalten.
- Die bereits ausgeführten Tests sind im [Runde-5-Bericht](round-5-clients-delivery.md)
  nachgewiesen: 132 Switcher, 12 CLI-Basisfälle, 136 spätere CLI-Fälle, 215 MCP.
  Diese Zahlen dürfen wegen überlappender Läufe nicht einfach summiert werden.
- Hier keine neuen Änderungen und daher keine unnötige Wiederholung der Suites.
  Kein CM1-, Installer-, SCOrch-, WinRM- oder Browser-Liveversuch behauptet.
