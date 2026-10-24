# Technische Referenz

Diese Referenz hält die Laufzeitverträge fest, die beim Ändern von NodePilot zu
beachten sind. Arbeitsregeln stehen in [CLAUDE.md](../CLAUDE.md), Entscheidungen in
den [ADRs](adr/) und die Bedienung im [Designer-Überblick](workflow-designer-features.md).

## Activities und Skriptausführung

Activity-Typen und ihre Eingaben sind im [Konfigurationskatalog](../src/NodePilot.Core/Activities/Embedded/activity-config-reference.json)
definiert. Die [Activity-Referenz](../src/nodepilot-docs-ui/content/en/activities-reference.md)
beschreibt die Verwendung. Neue Typen werden über `IActivityExecutor` erkannt.

`runScript`, `waitForCondition`, `xmlQuery` und `jsonQuery` sind hybrid: Ohne Zielmaschine laufen sie
auf dem API-Host, mit Zielmaschine über WinRM. Bei `xmlQuery` und `jsonQuery` wird die Datei am
Ausführungsort gelesen; UNC-Pfade bleiben überall abgelehnt.

<a id="runscript--ausführungsort-local-vs-remote--self-managed-remoting"></a>

### runScript: Ausführungsort

Mit einer nicht lokalen Maschine oder einem Credential verwendet `runScript` den
verwalteten WinRM-Pfad. Ohne Maschine beziehungsweise bei Loopback ohne Credential
läuft es lokal auf dem API-Host. `engine: auto` startet Nutzerskripte in Windows
PowerShell 5.1. Eingebaute Activities verwenden lokal den In-Process-Pool.

`Engine:MaxCapturedOutputChars` begrenzt die während einer Skriptausführung gesammelte
Ausgabe über alle Streams auf insgesamt 16.777.216 Zeichen (positiver Wert, Neustart erforderlich).
Der Standard lässt Platz für die beiden auf je 1 MiB begrenzten Ausgaben von `startProgram`
einschließlich JSON-Escaping. Kleinere Werte begrenzen auch diese Aktivität entsprechend.
Das gemeinsame Limit gilt für Prozess-, Runspace- und WinRM-Ausführung. Überschreitung
beendet den Schritt mit einem Ausgabelimit-Fehler; das spätere Kürzen gespeicherter Ergebnisse
ist davon unabhängig. Prozess-Pipes werden auch nach Erreichen des Limits weiter geleert.

Selbst aufgebautes Remoting innerhalb eines lokalen Skripts liegt außerhalb der
verwalteten WinRM-Verbindung. Dabei entfallen deren Credential-Anbindung,
Target-Zuordnung, Telemetrie und Transport-Hardening. Das Skript muss seine
Verbindungen selbst verwalten und aufräumen.

Der In-Process-Pool verwendet das PowerShell-SDK mit gebündelten Modulen.
Implizite Windows-PowerShell-Kompatibilität ist deaktiviert. Explizit aufgebaute
Kompatibilitätssessions gehören dem Skript. Bei `engine: runspace` können über
`cmd`, `Start-Process` oder `Process` gestartete PowerShell-Prozesse den vom SDK
veränderten Modulpfad erben. Der direkte Aufruf `& powershell.exe` behandelt diesen Pfad selbst.

`config.isolated: true` führt lokale Skripte in einem eigenen Prozess mit Windows
Job Object aus. `memoryLimitMb` und `maxProcesses` begrenzen dessen Ressourcen.
Remote-Ausführung verwendet diese lokale Isolation nicht.

Fehlereinträge bestimmen den Skripterfolg. `successExitCodes` aktiviert zusätzlich
eine Exit-Code-Prüfung. Ein skripteigenes `exit N` ist im Prozesspfad beobachtbar,
im Runspace-Pool nicht. Parse-Fehler bleiben in beiden Fällen Fehler.

## Variablen und Kontrollfluss

Ein Step sieht ausschließlich Ergebnisse seiner Graph-Vorgänger. Fehlende Ergebnisse
bleiben unaufgelöst, auch wenn ein anderer paralleler Zweig bereits fertig ist.
Ein neuer Variablen-Namespace braucht eigene Parser- und Resolver-Unterstützung.

| Ausdruck | Bedeutung |
|---|---|
| `{{step.output}}`, `{{step.error}}` | Standard- und Fehlerausgabe. |
| `{{step.success}}` | Erfolg als String. |
| `{{step.param.name}}` | Veröffentlichter Parameter dieses Steps. |
| `{{globals.NAME}}` | Globale Variable. |
| `{{manual.NAME}}` | Trigger- oder Aufrufparameter. |

`outputVariable` kann die Step-ID ersetzen. Ein unqualifiziertes `$name` im Skript
wird nur bei einem eindeutigen Publisher gebunden. Selbst zugewiesene Skriptvariablen
erscheinen als `param.*`, durchgereichte Variablen und PowerShell-Automatiken nicht.
Templates werden im Skript bereits gequotet: `$x = {{step.output}}` verwenden.

Nur aktive Trigger sind Workflow-Roots. Fehlende Trigger lassen die Ausführung
scheitern. Mehrere Eingänge erfordern eine Junction. Ungültige Bedingungen werden
beim Speichern und Veröffentlichen abgelehnt. Ein fehlender Variablenwert erfüllt
auch unter NOT keine Bedingung. Details: [ADR 0006](adr/0006-trigger-only-workflow-roots.md),
[ADR 0013](adr/0013-explicit-junction-fan-in.md) und [ADR 0015](adr/0015-fail-closed-edge-conditions.md).

## Trigger und Kind-Workflows

Trigger-Konfiguration wird in Core definiert und von Engine und Scheduler gemeinsam
verwendet. Ereignisdaten erscheinen unter `manual.*` sowie als Parameter des Triggers.
`databaseTrigger` reagiert auf eine Änderung des Sentinel-Werts. Der Trigger-Scan läuft
alle 5 Sekunden. Ausfallzeiten werden nicht nachträglich als Ereignisse eingespielt.

`startWorkflow` kann jeden aktivierten Workflow aufrufen. `parameters` werden zu
`manual.*`. Bei `waitForCompletion=true` wartet der Aufrufer und übernimmt die
`returnData`-Ausgabe. Die maximale Aufruftiefe beträgt 10, einschließlich `forEach`.
Die gemeinsame Slot-Verwaltung ist in [ADR 0018](adr/0018-active-subworkflow-capacity.md) beschrieben.

Der abgeleitete Vertrag berücksichtigt aktive `manualTrigger`- und `returnData`-Nodes.
Mehrere Return-Nodes garantieren nicht sämtliche deklarierten Outputs: der zuletzt
geschriebene vollständige JSON-Wert gewinnt. Konflikte zwischen Input-Deklarationen
werden markiert, `required` wird zusammengeführt. Reservierte Outputs wie
`__executionId` und `__status` setzt die Engine selbst.

Ohne Folder-Edit sind Input-Defaults in der Vertragsanzeige maskiert, zur Laufzeit
gelten die echten Werte. Namensauflösung bevorzugt exakte Groß-/Kleinschreibung,
danach einen eindeutigen Vergleich ohne diese Unterscheidung. Mehrdeutige Namen
werden abgelehnt. Sicherheitsgeschützte externe Routen können diese Ablehnung als 404 verbergen.

## Ausführung und Parallelität

`Workflow.MaxConcurrentExecutions` erlaubt 1 bis 1000 oder `null` für unbegrenzt.
Alle Startpfade teilen den Zähler. Dispatch-Aufträge bleiben am Limit `Pending`,
synchrone Kind-Aufrufe warten. `PUT /concurrency-limit` benötigt das Feld
`maxConcurrentExecutions`, auch wenn es `null` ist. Das Limit gehört weder zum
Graph-Save noch zur Versionierung und ändert sich nicht durch einen Graph-Rollback.

Eine Step-Zeile wird vor der Activity-Ausführung persistiert. Terminale Executions
beenden verbleibende laufende oder pausierte Steps. `StepsTotal` zählt die bereits
materialisierten Zeilen und ist bei laufenden Workflows kein verlässlicher
Fortschrittsnenner. Loops und spätere Zweige können weitere Steps erzeugen.

`WorkflowExecution.ErrorMessage` ist eine maskierte, begrenzte Zusammenfassung für
Listen und Benachrichtigungen. Fehlerauswertung verwendet `StepExecution.ErrorOutput`.
Bei mehreren Fehlern entscheidet die Reihenfolge aus `StartedAt` und `Id`.

## Step-Test

`POST /api/workflows/{id}/steps/{stepId}/test` führt eine einzelne Activity aus,
ohne eine WorkflowExecution anzulegen. `mockVariables` liefert künstliche
Step-Ergebnisse. `stepOverride` übergibt den vollständigen aktuellen `node.data`-Stand
einschließlich Activity-Typ, Konfiguration, Maschine, Credential und Output-Variable.
Die drei Testaktionen im Designer verwenden diesen konsistenten Snapshot.
Alternativ ersetzt `configOverride` ausschließlich `data.config` des gespeicherten
Nodes. Beide Overrides sind gegenseitig exklusiv und verlangen eine eigene aktive
Edit-Sperre. Ein nicht mehr vorhandenes ausgewähltes Maschinenziel wird abgelehnt.

`test-context` liefert Vorgängerwerte aus einem gewählten oder dem letzten terminalen
Lauf. Bei nicht ausgeführten Vorgängern bleibt nur das Schema. Globals kommen aus
dem GlobalVariableStore und dürfen nicht als Mock zurückgesendet werden.
Implementierung: [StepTester.cs](../src/NodePilot.Engine/StepTester.cs).

## SCOrch-Import

`POST /api/workflows/import-scorch` erzeugt deaktivierte Workflows und liefert ihre
IDs im Importreport. Erst nach fachlicher Prüfung aktivieren. Das Limit beträgt
300 MiB pro Request und 500 importierte Workflows und Variablen zusammen.
Große XML-Dateien benötigen beim Parsen deutlich mehr Speicher als ihre Dateigröße.

`MaxParallelRequests` wird als Workflow-Limit übernommen, einschließlich `1`.
Fehlende Werte bedeuten kein Limit, unbrauchbare Werte erzeugen eine Warnung.
Runbooks ohne Trigger erhalten einen manuellen Trigger. Zielnamen werden über den
MachineResolver aufgelöst. Connection Strings werden nicht übernommen.

Der Import muss Ausführungsabsicht und Datenfluss erhalten. Nicht abbildbare Semantik
im Report ausweisen, statt einen scheinbar funktionierenden Ersatz zu erzeugen.
`Run Program` bleibt `startProgram`, eingebettete Skripte werden gesondert abgebildet.
Kind-Verweise verwenden Ordnerpfad und Namen, damit Umbenennungen beim Import nicht
auf einen falschen Workflow zeigen. Externe oder mehrdeutige Referenzen werden gemeldet.

Formatbehandlung und synthetische Fixtures stehen in
[ScorchImporter.cs](../src/NodePilot.Engine/Scorch/ScorchImporter.cs),
[ScorchActivityMapper.cs](../src/NodePilot.Engine/Scorch/ScorchActivityMapper.cs) und
[den Importtests](../tests/NodePilot.Engine.Tests/Scorch/).
Reale Kundendaten gehören nicht in Fixtures.

## Settings, Statistik und Betrieb

Settings-Saves schreiben atomar nach `appsettings.runtime.json`. Das
`IsHotReloadable`-Flag in [SettingsSchema.cs](../src/NodePilot.Api/Configuration/SettingsSchema.cs)
bestimmt den Restart-Hinweis. Gemischte Sektionen bleiben restart-pflichtig,
wenn ein Teil ihrer Werte beim Boot gebunden wird.

| Verhalten | Sektionen |
|---|---|
| Hot-Reload | `Smtp`, `Llm`, `AiKnowledge`, `Retention`, `Stats`, `Threading`, `FileSystemOperation`, `WaitForCondition`, `SqlActivity`, `StartProgram`, `Webhook`, `ExternalTrigger`, `DbAdmin` |
| Neustart | `Authentication`, `Logging`, `OpenTelemetry`, `Security`, `RestApi`, `Remote`, `Performance`, `Engine`, `ExecutionDispatch`, `Agents` |

`Threading` übernimmt Änderungen nur bei manuell gestarteter Dimensionierung.
`Agents` enthält trotz einzelner live gelesener Werte eine beim Boot gebundene
Run-Grenze. Effektive Dimensionierung: [Performance](performance-improvements.md).
Der Dienstneustart aus der UI benötigt SCM-Recovery mit ausschließlich Restart-Aktionen.

Dashboard und Workflow-Listen lesen vorberechnete Statistikwerte. Standardmäßig
aktualisiert `WorkflowStatsRefresher` alle 5 Minuten ein 7-Tage-Fenster und verwendet
höchstens 1000 Dauer-Samples je Workflow. Live-Ops hält laufende Executions unabhängig
vom gewählten Historienfenster sichtbar. Gedeckelte Aggregate kennzeichnen fehlende
Abdeckung, sie dürfen nicht als vollständige Summen dargestellt werden.

Der Support-Log liegt als Datei und als `SupportEvents`-Projektion vor. Beide haben
standardmäßig 90 Tage Retention. Der vollständige Diagnoselog rotiert täglich und
bei 100 MB, mit höchstens 7 Dateien. Für einen Vorfall deshalb frühzeitig sichern.

## Weitere Referenzen

| Thema | Verbindliche Quelle |
|---|---|
| Edit-Lock, Publish und Canvas | [Designer](workflow-designer-features.md) |
| KI und Tool-Zugriff | [KI-Features](ai-features.md), [Agenten](ai-agents.md) |
| Authentifizierung und Hardening | [Threat Model](threat-model.md), [LDAP/SSO](ldap-windows-sso.md) |
| Audit-Codes | [AuditActions.cs](../src/NodePilot.Core/Audit/AuditActions.cs) |
| Deployment und Fehlerdiagnose | [Deployment-Guide](deployment-guide.md), [Desktop](../deploy/desktop/README.md) |
| Konfigurationsbackup | [ADR 0001](adr/0001-system-configuration-backup-restore.md), zusätzlich native DB- und Schlüsselbackups für vollständige Wiederherstellung |
| HA und Maintenance | [Enterprise-Features](enterprise-features.md), [HA](ha-active-passive.md) |
| Coverage | [coverage.runsettings](../coverage.runsettings), [CI](../.github/workflows/ci.yml) |

Beim Coverage-Merge die Assemblies `np` und `nodepilot-mcp` einschließen und
unterschiedliche `src/`-Präfixe normalisieren. Migrationen und begründet markierter
Code sind über die Runsettings ausgeschlossen. Scheduler-, Remote- und Telemetrie-Tests
liegen in Engine.Tests beziehungsweise Api.Tests.
