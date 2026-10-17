# Projektanalyse NodePilot — 2026-10-16

**Basis:** Branch `audit/project-analysis-2026-10-15`, inhaltsgleich mit dem lokalen `main` (Commit `b883737`). Analysiert wurden ausschließlich versionierte Dateien (3 306).
**Art:** statische Code-Analyse. Es wurden keine Builds, Tests, Coverage-Läufe oder Laufzeitmessungen ausgeführt.

> **Kurzfassung:** Gesamtnote **7,5 / 10**. Nach adversarieller Gegenprüfung bleibt **kein High- oder Critical-Befund**. Bestätigt sind 23 Medium- und 183 Low-Befunde. Die Leitplanken des Projekts (maschinell erzwungener Abhängigkeitsgraph, rund 40 Guard-Tests, 18 ADRs, durchgängiges Fail-closed-Design) sind für eine Codebasis dieser Größe überdurchschnittlich. Die Schwächen liegen an Semantikgrenzen der Engine, in still verschluckten Fehlern, in einigen konkreten Härtungslücken und in wenigen sehr großen Frontend- und Konfigurationsdateien.

**Vorgehen:** 33 Prüfbereiche haben je Bereich Stärken und Befunde mit Datei, Zeile und wörtlichem Zitat erhoben. Jeder Befund wurde von mindestens einem unabhängigen, auf Widerlegung angesetzten Prüfer gegen den aktuellen Code geprüft, jeder Medium-Befund von zwei. Dokumentierte Entscheidungen aus `docs/roadmap.md`, den ADRs, `CLAUDE.md` und den bisherigen Audit-Runden sind berücksichtigt; bewusst zurückgestellte Schulden stehen gesammelt in Abschnitt 7.5. Einzelheiten stehen in Anhang A, alle Befunde in Anhang B.

Befund-IDs (z. B. `engine-execution-core#1`) verweisen auf Anhang B. Fundstellen sind repo-relativ als `pfad:zeile` angegeben.

---

## 1. Gesamtbewertung

### **7,5 / 10**

Median dreier unabhängiger Bewertungen (7,4 · 7,5 · 7,6) aus den Perspektiven langfristige Wartung, Architektur und Betrieb/Sicherheit. Die Bewertungen stützen sich auf die verifizierten Befunde, nicht auf deren Anzahl.

| Dimension | Note | Begründung |
|---|---|---|
| Projektstruktur | 7,4 | Klare Assembly-Schichtung mit exakt erzwungener Abhängigkeitsrichtung. Abzug: Core ist kein reiner Kernel, der Switcher liegt außerhalb der Solution, die Kopplung innerhalb von `Api` prüft kein Guard. |
| Codequalität | 7,2 | Defensiv, konsequent fail-closed, kaum liegengebliebene Marker. Abzug: still verschluckte Fehler, Duplikate mit beginnender Drift, die eigene Kommentarregel wird verletzt. |
| Logik & Architektur | 7,5 | Durchdachte Persistenz- und Nebenläufigkeitsinvarianten (CAS, Outbox, Ledger, Fencing). Abzug: Semantikfehler an Engine-Grenzen, prozessweiter statischer Zustand. |
| Wartbarkeit | 6,7 | Hohe Testdichte und dichtes Guard-Netz. Abzug: Monolith-Hotspots (`WorkflowEditorPage`, `SettingsSections`, `ScorchImporter`), dreifach handgepflegte Abbildungen, Tests mit blinden Flecken. |
| Risiko (10 = gering) | 7,6 | Kein High/Critical, zentralisierte Sicherheitsprimitive. Abzug: einige konkrete Härtungslücken (UNC-Erkennung, Redaction-Patterns, Switcher-Konfiguration, Backup-ACL). |

**Befundlage nach Dimension und endgültiger Schwere:**

| Dimension | Medium | Low | ohne Auswirkung | gesamt |
|---|---:|---:|---:|---:|
| Codequalität | 5 | 50 | – | 55 |
| Wartbarkeit | – | 33 | 2 | 35 |
| Logik/Architektur | 9 | 19 | – | 28 |
| Testlücken | 2 | 22 | – | 24 |
| Performance | 1 | 22 | – | 23 |
| Sicherheit | 6 | 16 | – | 22 |
| Struktur | – | 12 | 1 | 13 |
| Anti-Pattern | – | 9 | – | 9 |
| **Summe** | **23** | **183** | **3** | **209** |

**Einordnung:** Ein erheblicher Teil der Low-Befunde betrifft Themen, die in Roadmap, ADRs oder `CLAUDE.md` bereits als bekannt oder bewusst zurückgestellt geführt sind (Abschnitt 7.5). Bei 100 Befunden haben die Prüfer die ursprüngliche Behauptung präzisiert oder abgeschwächt. Die Reviewer haben die Schwere systematisch höher angesetzt als die Prüfer: Alle drei vorgeschlagenen High-Befunde landeten bei Medium, 76 der 96 bestätigten Medium-Vorschläge bei Low.

---

## 2. Stärken

### 2.1 Architektur-Leitplanken werden maschinell erzwungen

- Der dokumentierte Projektgraph wird exakt und in beide Richtungen per Test erzwungen (`tests/NodePilot.Api.Tests/Architecture/DependencyDirectionTests.cs:77`). Eine falsche Projektreferenz bricht den Build, statt erst im Review aufzufallen.
- Rund 40 Guard- und Paritätstests halten weit auseinanderliegende Dateien synchron: Activity-Katalog, Settings-Schema und Admin-UI, Trigger-Vertrag, Template-Grammatik, Migrations-Nachbearbeitung, Endpoint-Abdeckung beider Clients. Ihre Ausnahmelisten prüfen sich selbst und können nicht verrotten (`tests/NodePilot.Cli.Tests/Api/EndpointClientCoverageTests.cs:211`).
- Architekturentscheidungen stehen in 18 ADRs, darunter die bewusst gewählte Topologie mit Einzelprozess und Active/Passive-HA (`docs/adr/0010-single-process-hosting.md:18`, `docs/adr/0002-active-passive-ha.md:15`). `docs/roadmap.md` trennt gesetzte, trigger-gated und verworfene Vorhaben und verhindert so, dass widerlegte Ideen erneut aufschlagen.

### 2.2 Persistenz- und Nebenläufigkeitsinvarianten

- Terminale Execution-Zustände werden per Compare-and-Swap mit DB-seitigem Prädikat, Cluster-Fence und Reload geschrieben (`src/NodePilot.Engine/WorkflowEngine.cs:750`).
- Step-Lifecycle-Writes bilden eine Durability-Barriere mit erzwungenem `CancellationToken.None` (`src/NodePilot.Engine/Execution/StepRunner.cs:176`), abgesichert durch `StepStatePersistenceInvariantTests`.
- Publish und Rollback laufen als atomarer CAS mit korrektem Re-Read-Verdikt (`src/NodePilot.Api/Controllers/WorkflowEditingController.cs:83`). Externe Auslösungen laufen über eine durable Outbox mit klarer Ownership-Grenze und provider-nativen Claims (`src/NodePilot.Api/ExecutionDispatch/ExecutionDispatchService.cs:281`, `src/NodePilot.Data/ExecutionDispatchOutboxClaimer.cs:37`).
- Alerting persistiert jeden Zustellversuch vor dem Netzwerk-I/O (`src/NodePilot.Scheduler/NotificationDispatcher.cs:295`). Vor jedem externen Seiteneffekt wird die Leader-Epoche geprüft (`src/NodePilot.Scheduler/TriggerOrchestrator.cs:778`).
- Der Datenbank-Breaker setzt ADR 0011 mit expliziter Invariante und Episodenschutz um (`src/NodePilot.Data/Availability/DatabaseAvailabilityTracker.cs:192`).

### 2.3 Fail-closed als durchgängiges Prinzip

- Edge-Conditions werden dreiwertig ausgewertet. Eine unentscheidbare Bedingung ist nie erfüllt (`src/NodePilot.Engine/Conditions/ConditionEvaluator.cs:49`, ADR 0015).
- Webhook-Admission antwortet einheitlich mit 404, authentifiziert vor dem Body-Buffering und setzt den Replay-Claim erst nach allen Gates (`src/NodePilot.Api/Controllers/WebhooksController.cs:243`, `:307`).
- Platzhalter in Skripten werden über den echten PowerShell-Parser aufgelöst (`src/NodePilot.Engine/PowerShell/PowerShellActivitySupport.cs:41`).
- Das Backup scheitert bei unvollständigem Export, der Restore-Commit wird verifiziert (`src/NodePilot.Api/Services/Backup/BackupService.cs:101`, `src/NodePilot.Api/Services/Backup/BackupRestoreService.cs:312`). Der SCOrch-Import erzeugt bei Unklarheit deaktivierte Platzhalter, statt zu raten (`src/NodePilot.Engine/Scorch/ScorchActivityMapper.cs:231`).

### 2.4 Zentralisierte Sicherheitsprimitive

- Ein SSRF-Guard deckt benutzerkonfigurierbare HTTP-Ziele ab und wird zusätzlich zur Connect-Zeit durchgesetzt (`src/NodePilot.Engine/Security/NetworkGuard.cs:121`). Eine Lücke bei IPv6-Metadata-Adressen steht in Abschnitt 7.1.
- Raw-SQL im Anwendungscode ist durchgehend parametrisiert (`src/NodePilot.Api/Controllers/WorkflowsController.cs:222`). Secret-Vergleiche laufen konstant-zeitig über einen zentralen Helfer (`src/NodePilot.Api/Security/SecretComparer.cs:37`). Die XML-Parser sind gegen XXE gehärtet (`src/NodePilot.Engine/Activities/XmlQueryActivity.cs:99`, `src/NodePilot.Engine/Scorch/ScorchImporter.cs:93`).
- Die Autorisierungsabdeckung aller Endpunkte wird per Laufzeittest über die echte Host-Pipeline erzwungen (`tests/NodePilot.Api.Tests/Hosting/AuthGatingSmokeTests.cs:71`). text2sql ist doppelt auf globale Admins begrenzt (`src/NodePilot.Ai/Knowledge/KnowledgeAssistantService.cs:81`).

### 2.5 Auslieferung und Build

- Die Artefakt-Vertrauenskette reicht vom ZIP bis ins Install-Root (`deploy/Install-NodePilot.ps1:249`, `deploy/ArtifactSecurity.ps1:944`). Der Installations-Rollback ist transaktional und schließt einen Service-Snapshot ein (`deploy/Install-NodePilot.ps1:970`).
- Die CI-Änderungserkennung ist fail-open: Jeder Fehlerpfad endet bei „alles ausführen“ (`.github/workflows/ci.yml:58`). Zentrales Package-Management bringt begründete Security-Pins und einen transitiven NuGet-Audit mit (`Directory.Packages.props:69`, `Directory.Build.props:45`).

### 2.6 Frontend und Desktop

- Ein generationsbasierter Auth-Boundary verwirft nach einem Identitätswechsel veraltete Antworten global (`src/nodepilot-ui/src/security/authBoundary.ts:259`). Sensibler Browser-State ist an die User-ID gebunden (`src/nodepilot-ui/src/security/sensitiveBrowserState.ts:54`).
- Der Designer folgt dem Prinzip „Rohgraph als Source of Truth plus read-only Projektion“ mit Identitätserhalt (`src/nodepilot-ui/src/hooks/useDisplayedGraph.ts:10`). Wo das Prinzip verletzt wird, steht in Abschnitt 3.5.
- Die Electron-Shell isoliert ihre Fenster und prüft den Absender jeder IPC-Nachricht (`src/nodepilot-desktop/src/main.ts:142`, `:258`).

### 2.7 Teststrategie und Hygiene

- Rund 6 300 C#-Testmethoden (gezählt als `[Fact]`/`[Theory]`-Attribute), 257 Vitest-Dateien und 84 Playwright-Specs. API-Tests laufen über die echte Host-Pipeline statt über handgebaute Principals (`tests/NodePilot.Api.Tests/TestSupport/ApiPipelineFactory.cs:27`). Die Migrations-Guards prüfen beide Nachbearbeitungsschritte und das Provider-DDL (`tests/NodePilot.Data.Tests/MigrationDriftTests.cs:31`). Das Backend-Coverage-Gate (85 % Line, 70 % Branch) erzwingt `.github/workflows/ci.yml`.
- Im Produktionscode steht genau ein TODO/FIXME/HACK-Marker. Bewusste Duplikation ist dokumentiert statt verstreut (`src/NodePilot.Cli/CLAUDE.md:7`).

---

## 3. Schwächen

Dieser Abschnitt enthält alle 23 Medium-Befunde, jeweils von zwei unabhängigen Prüfern bestätigt. Low-Befunde stehen nach Thema in den Abschnitten 5 und 7, vollständig in Anhang B.

### 3.1 Semantikfehler im Ausführungskern

- **Ein Abbruch wird als Fehlschlag verbucht.** Bei einem Execution-Abbruch (Benutzer-Cancel, Lauf-Timeout, Host-Shutdown, HA-Fencing) greift der Cancel-Pfad des StepRunner nicht, weil er `!executionCts.IsCancellationRequested` verlangt (`src/NodePilot.Engine/Execution/StepRunner.cs:359`). Der laufende Step wird als `Failed` persistiert, schreibt `STEP_FAILED` ins Support-Log und meldet sich per SignalR als fehlgeschlagen. Die Execution selbst steht auf `Cancelled`. Step-Historie, Support-Log und Execution-Status widersprechen sich, und jede Triage, die nach `STEP_FAILED` filtert, wird verfälscht. (engine-execution-core#1)
- **restApi-Timeouts werden nie wiederholt.** `RestApiActivity` setzt `timeoutSeconds` per `CancelAfter` um (`src/NodePilot.Engine/Activities/RestApiActivity.cs:56`). Die entstehende `OperationCanceledException` reicht `ActivityExecution.RunAsync` unverändert weiter, und der Retry-Filter nimmt sie aus (`src/NodePilot.Engine/Execution/StepRunner.cs:583`). Ein Schritt mit `config.retry` scheitert beim ersten Timeout endgültig, mit der wenig hilfreichen Meldung „A task was canceled.“ Ausgerechnet der häufigste transiente HTTP-Fehler bleibt unwiederholt. (engine-activities-powershell-remote#1)
- **Die Junction-Logik existiert zweimal, und die Tests treffen die ungenutzte Variante.** `EvaluateSuccessorReadiness` hat keinen Produktionsaufrufer, wird aber getestet. Die tatsächlich genutzte `EvaluateSuccessorDecision` weicht davon ab (`src/NodePilot.Engine/Execution/WorkflowScheduler.cs:348`). `junction.mode` und `requiredCount` prüft kein Validator. Ein Tippfehler im Modus läuft zur Laufzeit als `waitAny` und bricht noch laufende Zweige ab, entgegen dem Fail-closed-Prinzip aus ADR 0015. (engine-execution-core#4)
- **Der SCOrch-Import ändert bei bedingtem Fan-in die Semantik.** Der Import leitet jeden Fan-in durch eine `waitAll`-Junction (`src/NodePilot.Engine/Scorch/ScorchImporter.cs:588`). Typischer Fall: Mehrere Fehler-Links laufen in einen Handler, ein früher Schritt gelingt, ein späterer scheitert. Dann wird der Handler übersprungen; in SCOrch liefe er einmal je gefeuertem Link. Die Execution endet sichtbar als `Failed`, verloren geht die Arbeit des Handlers (Cleanup, Benachrichtigung). Die Importwarnung erwähnt die Abweichung nicht. (scorch-import#1)
- **Trigger-Eingaben werden ein zweites Mal als Template expandiert.** Die Platzhalter-Namespaces werden in getrennten Durchläufen ersetzt. Der Step-Durchlauf liest dabei Text, in den `{{manual.X}}`-Werte bereits eingesetzt sind (`src/NodePilot.Engine/Execution/VariableResolver.cs:273`, `:283`; ebenso `src/NodePilot.Engine/Conditions/ConditionEvaluator.cs:374`). Enthält ein Trigger-Wert, etwa ein Webhook-Feld, selbst `{{step.output}}`, wird er durch Ausgaben vorgelagerter Steps ersetzt. Das unterläuft die Vertrauensgrenze für Trigger-Daten. Webhook-Authentifizierung und Ahnen-Scope begrenzen die Wirkung. (engine-execution-core#3)

### 3.2 Alerting verliert Signale

- **Das Retry-Fenster beträgt rund zwei Minuten, ohne Backoff.** Fehlgeschlagene Zustellungen werden in jedem Dispatcher-Pass erneut versucht. Der Pass läuft fest alle 30 s, nach fünf Versuchen ist der Versuch endgültig `Failed` (`src/NodePilot.Scheduler/NotificationDispatcher.cs:44`, `:103`). Fällt ein Webhook- oder SMTP-Empfänger länger als etwa 2,5 Minuten aus, etwa bei Wartung oder Neustart, gehen alle Alerts dieses Zeitraums verloren. Der At-least-once-Anspruch deckt damit Crashs ab, aber keine realistischen Empfängerausfälle. (scheduler#4)
- **System-Alert-Quellen scheitern lautlos.** `SystemAlertEvaluator` fängt Fehler aus `ObserveAsync` und aus der Rekonstruktion mit einem leeren `catch` ab (`src/NodePilot.Scheduler/SystemAlerts/SystemAlertEvaluator.cs:75`, `:207`), und die Klasse hat keinen Logger. Eine deterministisch scheiternde Quelle alarmiert nie wieder, und ausgerechnet das Monitoring meldet seinen eigenen Ausfall nicht. Rekonstruktionsfehler landen mit der irreführenden Ursache „source execution no longer available“ im Ledger. (scheduler#2)
- **Der FileWatcher persistiert bei jedem rohen Dateiereignis den ganzen Verzeichnis-Snapshot**, auch für Ereignistypen, die gar nicht feuern (`src/NodePilot.Scheduler/Sources/FileWatcherTriggerSource.cs:473`, `:483`). Jedes Ereignis erzeugt einen eigenen Task am Gate. Bei laufend beschriebenen Ordnern wachsen DB-Last und Warteschlange unbegrenzt, während eines DB-Ausfalls zusätzlich. (scheduler#1)

### 3.3 Härtungslücken

- **Die UNC-Sperre erkennt gemischte Trenner nicht.** `PathGuard.IsUncPath` prüft nur auf `\\` oder `//` am Anfang (`src/NodePilot.Engine/Security/PathGuard.cs:326`). `Path.GetFullPath` normalisiert gemischte Formen trotzdem zu einem UNC-Pfad, und der Reparse-Walk fragt den Share-Root noch vor der AllowedRoots-Prüfung ab (`:86`, `:272`). Der PowerShell-Guard hat dieselbe Lücke (`src/NodePilot.Engine/Security/TargetPathGuardScript.cs:63`). Erreicht ein nicht vertrauenswürdiger Wert ein Pfadfeld, baut der Host eine ausgehende SMB-Verbindung mit NTLM-Authentifizierung auf. Genau das schließt die Klassendoku („always rejected regardless of any flag“) aus. (gap-engine-remote-activities-guards#1)
- **Ungültige Redaction-Patterns werden still verworfen.** `OutputRedactor` kompiliert `Logging:Redaction:Patterns` in einem leeren `catch` (`src/NodePilot.Engine/Security/OutputRedactor.cs:133`). Ein Tippfehler im Regex schaltet die verlangte Maskierung ohne jedes Signal ab. (gap-engine-remote-activities-guards#2)
- **Der elevierte Switcher liest eine ungeprüfte Konfiguration.** Der Switcher läuft mit `requireAdministrator` (`src/NodePilot.Switcher/app.manifest:7`). Er lädt `%ProgramData%\NodePilot\Switcher\switcher.json` und startet den dort konfigurierten `cliPath` (`src/NodePilot.Switcher/Configuration/SwitcherConfiguration.cs:141`, `src/NodePilot.Switcher/Services/NodePilotWorkflowReconciler.cs:401`), ohne Besitzer und ACL zu prüfen. Ausnutzbar ist das in einem engen Fenster: Ein Nicht-Admin muss das Verzeichnis anlegen, bevor der Switcher dort zum ersten Mal eleviert läuft und bevor ein Installer es geschützt hat. Typisch dafür ist der Standalone-Drop auf SCOrch-Servern. Die Folge wäre eine lokale Rechteausweitung. (switcher-desktop#1)
- **Backup- und Rollback-Verzeichnisse erben die Eltern-ACL.** Im Fehlerfall werden sie ohne Hash-Abgleich ins Install-Root zurückkopiert (`deploy/Update-NodePilot.ps1:299`, `:516`; `deploy/Install-NodePilot.ps1:988`). Bei Install-Roots außerhalb von Program Files, etwa `D:\NodePilot`, erbt das Geschwisterverzeichnis laut dem eigenen Kommentar des Installers `Users:(M)` (`deploy/Install-NodePilot.ps1:346`). Der Signaturschutz des Vorwärtspfads entfällt dann auf dem Rückweg. (deploy-installers#1)
- **Die OIDC-JIT-Anlage schreibt kein Audit, die OIDC-Reconciliation leitet nicht ans SIEM weiter** (`src/NodePilot.Api/Security/Oidc/OidcIdentityMapper.cs:187`, `:475`). LDAP, Windows und SCIM auditieren und leiten weiter. OIDC fehlt als dritter externer Identitätsweg im SIEM-Strom, auch bei einer JIT-Anlage direkt als Admin. (gap-external-identity#2)

### 3.4 Identität und Provisioning

- **Das SCIM-Deprovisioning eines bereits deaktivierten Admins scheitert dauerhaft mit 409.** Die Last-Admin-Sperre prüft nur die Rolle des Zielbenutzers, nicht ob er noch aktiv ist (`src/NodePilot.Api/Security/Scim/ScimProvisioningService.cs:279`, `:546`). Der übliche IdP-Ablauf (erst deaktivieren, später löschen) schließt das Offboarding deshalb nie ab, und der Provisioning-Client bekommt in jedem Zyklus Fehler. Ein Rechte-Leck entsteht nicht, weil der Benutzer inaktiv bleibt. (gap-external-identity#1)

### 3.5 Frontend: Fehlerrückmeldung und Zustand

- **Mutationen scheitern lautlos, auch bei der Edit-Lock-Steuerung.** `queryClient.ts` registriert nur einen `QueryCache`-Handler (`src/nodepilot-ui/src/queryClient.ts:10`). Ohne Fehleranzeige laufen die fünf Mutationen in `useWorkflowLock` (`src/nodepilot-ui/src/hooks/useWorkflowLock.ts:27`), „Ausführen“ und „Workflow anlegen“ in `WorkflowsPage.tsx` (`:567`, `:211`) sowie das Löschen in `GlobalVariablesPage`. Ein 409 beim Sperren oder ein 403 beim Ausführen bleibt ohne Rückmeldung, der Button kehrt einfach in den Ruhezustand zurück. Der Code benennt die Lücke selbst (`src/nodepilot-ui/src/pages/WorkflowsPage.tsx:245`). (ui-app-pages-components#1, x-robustness#1)
- **Das Critical-Path-Overlay macht Workflows „dirty“.** `useCriticalPath` schreibt über `useReactFlow().setNodes` projizierte Knoten in den Rohgraphen zurück (`src/nodepilot-ui/src/hooks/useCriticalPath.ts:23`). Bei aktivem Overlay (der Schalter wird persistiert) wird ein Workflow schon beim Öffnen dirty. Folgen: Navigations-Blocker, beforeunload-Warnung und ein Autosave ohne `canWrite`-Prüfung, der für Leser mit einem Fehler-Toast endet. Lint- und Simulations-Badges werden dabei autoritativ und bleiben bis zum Neuladen stehen. Der gespeicherte Stand bleibt sauber, weil der Request-Body Laufzeitfelder entfernt. (ui-designer#1)
- **Escape im Skripteditor verwirft ungespeicherte Änderungen**, auch wenn es im eingebetteten KI-Dialog gedrückt wird. Das Event steigt ohne `stopPropagation` zum äußeren Handler auf (`src/nodepilot-ui/src/components/designer/ScriptEditorDialog.tsx:372`, `src/nodepilot-ui/src/components/ai/AiPromptDialog.tsx:77`). (gap-ui-designer-subcomponents#2)
- **KI-Vorschläge verlieren Änderungen an Decision-Cases und Bedingungsgruppen still.** Enthält ein vorgeschlagenes Array eine Maske, stellt der Merge das komplette Original-Array wieder her, ohne Hinweis (`src/NodePilot.Ai/WorkflowDefinitionMerge.cs:182`). Weil Literal-Operanden immer maskiert werden, betrifft das praktisch jede KI-Änderung an Verzweigungslogik. Der Assistent behauptet in seiner Antwort eine Änderung, die der Vorschlag nicht enthält. (gap-llm-trust-boundary#2)
- **Die Spaltensortierung der Ausführungsliste wirkt nur innerhalb der aktuellen Seite.** Der Server sortiert fest nach Startzeit, das Frontend sortiert nur die geladenen 200 Zeilen um (`src/nodepilot-ui/src/pages/ExecutionsPage.tsx:188`, `src/NodePilot.Api/Controllers/ExecutionsController.cs:162`). Wer nach Dauer sortiert, sieht nur das Maximum der jüngsten 200 Läufe, ohne dass die UI darauf hinweist. (ui-app-pages-components#3)
- **Die Dialog-Primitive hat keine Dialog-Semantik.** `ModalShell` setzt `role="presentation"` auf Backdrop und Panel, aber weder `role="dialog"` noch `aria-modal` (`src/nodepilot-ui/src/components/common/ModalShell.tsx:30`). Daneben gibt es zahlreiche handgebaute Overlays, einige ohne Escape-Handler (z. B. `src/nodepilot-ui/src/components/workflows/ConcurrencyLimitDialog.tsx:42`). Die Modal-Konsolidierung steht als kosmetischer Sammelposten in der Roadmap (`docs/roadmap.md:268`); dass ModalShell selbst die Dialogrolle fehlt, steht dort nicht. (x-duplication-reuse#3)

### 3.6 Build- und Betriebslücken

- **Switcher und Switcher.Tests liegen außerhalb von `NodePilot.slnx`.** CI, CodeQL, Vulnerability-Audit und Nightly bauen und testen sie deshalb nie (`.github/workflows/codeql.yml:147`). Beide Einträge hat der Commit `004d45b` entfernt, dessen Betreff nur eine Doku-Synchronisation nennt („docs: sync the Engine Switcher server URL …“). Das spricht für ein Versehen. Die sicherheitsrelevante Logik des elevierten Switchers (Redirect-Prüfung vor Credential-Weitergabe, fail-closed Umschaltung) läuft damit ohne automatisches Netz. (build-ci-scripts#1)
- **Der Switcher-Stopp scheitert bei Diensten im Pending-Zustand.** `StopAsync` toleriert nur den Fehler 1062. Den Fehler 1061 (Dienst in `START_PENDING`/`STOP_PENDING`) wirft es sofort, sodass Wartephase und PID-verifizierter Forced-Stop nie erreicht werden (`src/NodePilot.Switcher/Services/WindowsServiceControlGateway.cs:106`). Betroffen ist genau der Fall, für den der Forced-Stop existiert. (switcher-desktop#3)

---

## 4. Architektur-Review

### 4.1 Architekturstil

NodePilot ist ein **modularer Monolith in einem Prozess** (ADR 0010) mit geschichteten Assemblies:

- `Core` ist das Blatt mit Modellen, Interfaces und Validatoren.
- Darüber liegen `Data`, `Remote`, `Ai` und `Telemetry`.
- `Engine` und `Scheduler` bilden die Application-Schicht.
- `Api` ist Composition Root und Host.
- `Cli` und `Mcp` sind reine HTTP-Clients, `Switcher` hat keine Projektabhängigkeiten.

Innerhalb der Schichten finden sich etablierte Muster:

- Activities sind eine Strategy mit Auto-Discovery, Remote-Activities eine Template Method (`BaseRemoteActivity`).
- Trigger-Quellen sind eine Strategy hinter `ITriggerSource`.
- Hintergrunddienste folgen einem einheitlichen Gerüst: Verfügbarkeits-Gate, dann Leader-Gate, dann Iteration, dann Delay.
- Punktuell gibt es Ports & Adapters, etwa `IWorkflowExecutionDispatcher` in Core mit der Implementierung in Api.

Eine Clean Architecture im engeren Sinn ist es nicht. Das Domänenmodell ist anämisch: Core enthält Property-Container, die Logik liegt in Stores, Services und Controllern. Dicke Controller sind laut Roadmap ausdrücklich zulässig, solange die Logik testbar bleibt.

### 4.2 Modulgrenzen und Abhängigkeiten

Der Projektgraph ist exakt erzwungen (Abschnitt 2.1). Blinde Flecken:

- **Kopplung innerhalb von `Api`.** Der Guard sieht nur ProjectReferences. Sieben Dateien außerhalb von `Controllers/` importieren den Namespace `NodePilot.Api.Controllers`, darunter Dashboard-Services, `WorkflowPortability`, `CsrfMiddleware` und `ApiProblemDetailsResultFilter` (x-architecture-dependencies#3).
- **Core ist kein reiner Shared Kernel.** `Core/Clients` enthält 14 Dateien Client-Infrastruktur samt DPAPI-Abhängigkeit (`ProtectedData`). `SqlStatementInspector` nutzt außerhalb von Core nur Api (core-data#1, x-architecture-dependencies#4). Die Client-Infrastruktur liegt bewusst in Core, um Duplikation zwischen CLI und MCP zu vermeiden; der Preis ist ein breiterer Kernel.
- **Die Solution-Grenze ist lückenhaft:** Der Switcher fehlt (build-ci-scripts#1).
- **Im Frontend fehlt eine Domänen-API-Schicht.** `src/api` ist nur eine HTTP-Fassade. Endpunkte und Query-Keys stehen in 51 Dateien unter `components/`, `pages/` und `hooks/`; `['workflows'` etwa kommt 33-mal in 16 Dateien vor. Eine Query-Key-Fabrik fehlt (x-architecture-dependencies#1). Daraus erklären sich Invalidierungslücken wie die nicht live aktualisierte Ausführungshistorie (x-performance#1).

### 4.3 Verantwortlichkeiten und Komplexität

Die Komplexität ist in wenigen, meist gut getesteten Hotspots konzentriert, der Rest ist mittel. Die größten Produktionsdateien:

| Datei | Zeilen | Bemerkung |
|---|---:|---|
| `deploy/Preflight.ps1` | 2 100 | 42 Funktionen, bewusst seiteneffektfrei |
| `src/nodepilot-ui/src/pages/WorkflowEditorPage.tsx` | 2 059 | rund 40 State-/Callback-Blöcke, `EditorOverlaysProps` mit 90 Feldern; der Wiederaufnahme-Trigger der Roadmap (rund 1 700 Zeilen) ist überschritten (ui-designer#3) |
| `src/NodePilot.Api/Configuration/SettingsSections.cs` | 1 817 | jede Sektion dreifach von Hand abgebildet, 140 Fallback-Literale (x-readability-complexity#5) |
| `deploy/Install-NodePilot.ps1` | 1 727 | bewusst zurückgestellt (Roadmap) |
| `src/NodePilot.Api/Services/Backup/BackupRestoreService.cs` | 1 646 | bewusst zurückgestellt (Roadmap) |
| `src/NodePilot.Engine/Scorch/ScorchImporter.cs` | 1 639 | fünf Verantwortlichkeiten, jede Definition wird viermal konvertiert (scorch-import#6) |
| `src/nodepilot-ui/src/pages/WorkflowsPage.tsx` | 1 586 | 12 `useMutation`, Zeilen-Rendering für Mobil und Desktop doppelt (ui-app-pages-components#5) |
| `src/NodePilot.Engine/WorkflowEngine.cs` | 1 509 | bewusst zurückgestellt (Roadmap) |
| `src/nodepilot-ui/src/components/designer/nodes/ActivityNode.tsx` | 1 429 | vier Render-Varianten in einer Funktion mit rund 820 Zeilen (ui-designer#4) |
| `src/NodePilot.Api/Controllers/AuthController.cs` | 1 305 | Login-Methode mit rund 250 Zeilen und über 15 Early Returns; dicke Controller bewusst akzeptiert |

**Methodenlängen:** Eine Heuristik des Querschnittsprüfers findet 47 Methoden mit über 150 Zeilen. Die längsten: `WorkflowImportExportController.ImportScorch` (437 Zeilen), `DashboardController.BuildStatsAsync` (413), `WebhooksController.Hit` (322) und `TokenValidityMiddleware.Invoke` (312) (x-readability-complexity#3).

**Änderungs-Hotspot Nr. 1** ist `deploy/Test-DeploymentTemplates.ps1` mit 72 Commits in sechs Monaten. Die Installer-Prüfung koppelt per Regex an den Quelltext; 31 der 35 Commits an `Install-NodePilot.ps1` haben diese Testdatei mitgeändert (x-readability-complexity#2, deploy-installers#3).

### 4.4 Datenfluss und State Management

**Backend.** Der Execution-Lebenszyklus ist eine Zustandsmaschine mit CAS und Orphan-Sweep, die externe Auslösung eine durable Outbox, das Alerting ein Ledger mit Persistenz vor dem I/O. Schwachstellen:

- Prozessweiter statischer Zustand steckt nicht nur in `WorkflowEngine` (dokumentiert). Er findet sich auch in `ExecutionHub` (vier statische `ConcurrentDictionary`), `WorkflowScheduler` (statischer Step-Semaphor), `ScheduleTriggerSource` und `WorkflowDefinitionCache` (x-architecture-dependencies#2, api-alerting-ai-realtime#2). Das erzwingt serialisierte Testkollektionen. Welche Testklasse dazugehören muss, ist reine Konvention ohne Guard (tests-engine-data-ai#6).
- Edge-Conditions sehen die ungefilterte Ergebnis-Map statt der Ahnen-Sicht, die Step-Templates bekommen (engine-execution-core#2, Low).

**Frontend.** Server-State liegt in TanStack Query, Client-State in kleinen Zustand-Stores, Echtzeit kommt über eine persistente SignalR-Verbindung. Der Designer folgt „Rohgraph plus read-only Projektion“, verletzt das Prinzip aber an zwei Stellen:

- `useCriticalPath` schreibt projizierte Knoten zurück (ui-designer#1).
- `useNodeAnnotations` und `useCoverageHeatmap` schreiben Anzeige-Annotationen in den Rohgraphen, den die Undo-Historie snapshottet (ui-designer#2).

Dazu kommt: Das History-/Dirty-Protokoll wird an über 20 Stellen von Hand wiederholt (ui-designer#5).

### 4.5 Skalierbarkeit

Die Grenzen sind bewusst gewählt und dokumentiert. Es gibt einen aktiven Engine-Prozess, Active/Passive-HA dient der Verfügbarkeit, Active/Active ist erst bei Lastbedarf vorgesehen (Roadmap R2). Gemessene Tuning-Grenzen stehen im Sperrvermerk der Roadmap. Im Code fallen auf:

- Das Dashboard setzt rund 30 sequenzielle Roundtrips pro Aufruf ab. Fünf 7/14-Tage-Scans laufen ungecacht, obwohl Stunden-Rollups vorhanden sind (api-admin-ops-surface#2, x-performance#2).
- Die Executions-Suche nutzt ein nicht sargierbares Prädikat und wertet es doppelt aus (x-performance#3).
- Der Execution-Collector verarbeitet höchstens 200 terminale Executions pro 30-s-Pass (scheduler#5).

Die tatsächliche Lastgrenze ist ohne Messung nicht beurteilbar (Abschnitt 8).

### 4.6 Testbarkeit der Komponenten

Interfaces stehen überwiegend an echten Nähten (Remote, Uhr, LLM, SCM-Gateway). Einzelne haben nur eine Implementierung und keine Test-Naht (core-data#3). Das hält Tests weitgehend frei von Mock-Wildwuchs. Der SCOrch-Importer besteht aus reinen Funktionen ohne DI und ist ohne Mocks testbar. Einschränkungen:

- Der statische Zustand erzwingt serielle Testkollektionen (siehe 4.4).
- Das SQLite-Harness teilt eine Verbindung zwischen parallelen Step-Scopes und erzwingt Sleeps gegen Races (tests-engine-data-ai#2, dokumentiert).
- Produktionscode trägt Pfade, die nur Tests brauchen. Am heikelsten: `AuthController` baut ohne DI-Konfiguration einen offenen `LocalLoginMode`-Default; in Produktion greift `BreakGlassOnly` (api-security-identity#1). Weitere Fälle: ein synchroner Testpfad in `CredentialStore` (core-data#4), Test-Seams im Trigger-Vertrag (scheduler#7) und Kompatibilitätszweige für Testmocks im Frontend-Transport (ui-state-data-layer#7).

---

## 5. Wartbarkeits-Review

### 5.1 Tests und Qualitätssicherung

Die Testbasis ist breit; Zahlen in Abschnitt 2.7 und Anhang A. Bestätigte Lücken:

- **Guards mit blinden Flecken:**
  - Die Admin-only-Gates von neun Controllern pinnt kein Rollentest (tests-api-clients#1).
  - Der Audit-Guard übersieht per Regex mutierende Aktionen, wenn zwischen den Attributen ein Kommentar steht oder die Methode einen Expression-Body hat (tests-api-clients#2).
  - Endpoint- und DTO-Paritätsguards sehen die Agents-API nicht (tests-api-clients#3).
  - Der DTO-Guard übersieht DTOs außerhalb von `Api/Dtos` (x-duplication-reuse#1).
- **Tests, die weniger prüfen als ihr Name sagt:**
  - Der waitAny-Engine-Test läuft ohne Trigger-Knoten und besteht trivial (tests-engine-data-ai#1).
  - Ein Test namens „SwallowsAuditFailure“ prüft das Schlucken nicht (tests-engine-data-ai#4).
  - Die zentralen Save-/Publish-Tests prüfen nur, dass ein Request kam (tests-frontend-e2e#7).
- **Determinismus:** In E2E-Specs und Backend-Tests synchronisieren feste Wartezeiten, allein in `e2e/` stehen 28 `waitForTimeout`. In CI maskieren Retries die Flakes (tests-frontend-e2e#3, tests-engine-data-ai#3, tests-api-clients#5, build-ci-scripts#3).
- **Provider:** SQL-Server-Pfade laufen in CI nie gegen eine echte Instanz, auch nicht der HA-kritische Outbox-Claim (core-data#2, tests-engine-data-ai#7; die Container-Matrix ist Roadmap-Posten 26).
- **Installer:** Kerninvarianten werden überwiegend per Regex auf den Quelltext geprüft, mit 383 Pattern-Asserts. Das schreibt sogar Kommentarformulierungen fest (deploy-installers#3).
- **Ungetestete Laufzeitkerne:** das P/Invoke-Gateway des Switchers und `main.ts` der Electron-Shell (switcher-desktop#4, tests-frontend-e2e#8).

### 5.2 Logging und Fehlerbehandlung

**Stärken:** Der HTTP-Fehlervertrag ist über `ApiProblemDetailsResultFilter` einheitlich (ADR 0007). Logging ist strukturiert und nutzt Korrelations-Scopes. Bei Remote-Fehlern gelangen keine Exception-Texte an Clients. Die 262 `catch (Exception` in 134 Dateien sind überwiegend dokumentierte Überlebensgrenzen von Hintergrunddiensten.

**Schwächen:**

- **Still verschluckte Fehler:** `SystemAlertEvaluator` (scheduler#2), `OutputRedactor` (gap-engine-remote-activities-guards#2), das Dispose im WinRM-Pool (x-robustness#2), Cancel und PNG-Export im Designer (ui-designer#6), Cancel und Resume im Debug-Panel (gap-ui-designer-subcomponents#5) sowie die Frontend-Mutationen (x-robustness#1).
- **Zu breite Exception-Typen** werden samt `ex.Message` an Clients durchgereicht (x-robustness#5, api-alerting-ai-realtime#3). Der Backup-Controller bildet breite Framework-Exceptions pauschal auf 400 ab (api-hosting-config-backup#1).
- **Reihenfolge von Audit/SIEM und Commit:**
  - Events werden vor dem Commit als „committed“ weitergeleitet (api-admin-ops-surface#1, scheduler#6).
  - Audit- und Lifecycle-Writes nutzen das Request-Token statt `CancellationToken.None`, entgegen der Regel in `CLAUDE.md` (api-workflow-surface#4, api-admin-ops-surface#3, api-alerting-ai-realtime#4, engine-execution-core#5).
- **Span-Redaction** ist nur punktuell durchgesetzt (x-robustness#3). OpenTelemetry ist standardmäßig aus.

### 5.3 Dokumentation im Code

**Stärken:** Kommentare begründen nicht offensichtliche Entscheidungen. ADRs und `docs/claude-reference.md` tragen die Tiefe, sodass Code-Kommentare kurz bleiben können.

**Schwächen:**

- Die eigene Kommentarregel (kurz, nur was und warum, keine Rückblenden) wird auch in Code verletzt, der nach ihrer Einführung entstand. Beispiel: `src/NodePilot.Engine/Execution/VariableResolver.cs:126` (x-readability-complexity#4).
- Kommentare sind veraltet, etwa die Sitzungsdauer „12 h“ statt der geltenden 8 h (api-security-identity#7).
- Im Breaker-Code sind Kommentare mechanisch zerbrochen (core-data#6).
- Ein Kommentar sagt eine SignalR-Invalidierung zu, die nicht stattfindet (x-performance#1).
- Die Doku driftet: Die Roadmap führt bereits erledigte Custom-Activity-Invarianten als offen (api-alerting-ai-realtime#7).

### 5.4 Konfigurierbarkeit und Deployment-Fähigkeit

**Stärken:** Das Settings-Schema ist metadatengetrieben, mit Hot-Reload-Flag je Sektion. Die Consumer-Regel (`IOptionsMonitor`) wird eingehalten, Settings-Writes laufen atomar mit ETag, die Boot-Validierung sammelt alle Fehler. Artefakte sind signiert, das GUI-Setup arbeitet über eine Antwortdatei, der Rollback ist transaktional.

**Schwächen:**

- `SettingsSections.cs` bildet jede Sektion dreimal von Hand ab und dupliziert dabei die Default-Werte (x-readability-complexity#5).
- Härtungsflags werden an mehreren Stellen unterschiedlich geparst, z. B. `StartProgram:DisallowShellExecute` (api-hosting-config-backup#2). `Program.cs` liest Flags ad hoc; `Cluster:Enabled` steht in neun Dateien (api-hosting-config-backup#4).
- **Fehlkonfiguration ohne Signal:**
  - Ungültige Redaction-Patterns verschwinden still (gap-engine-remote-activities-guards#2).
  - Ungültige Restore-Policies fallen still auf „Skip“ zurück (api-hosting-config-backup#5).
  - Konfiguration wird außerhalb der inneren try-Grenze gelesen; ein nicht konvertierbarer Wert kann so den Host stoppen (scheduler#3).
- **Deploy-Skripte:**
  - Install und Update duplizieren Kernhelfer, schon mit Drift (deploy-installers#4).
  - Ein stiller Downgrade auf ein älteres signiertes Artefakt ist möglich (deploy-installers#5).
  - Das Setup-Transcript enthält den Legacy-Trigger-Key im Klartext (deploy-installers#2).

### 5.5 Duplikate und Wiederverwendung

Bewusste, guard-gesicherte Spiegel (Activity-Katalog, Analyzer, Trigger-Vertrag) sind kein Problem. Ungeschützte Duplikate mit beginnender Drift:

- **Clients:** Die Typed-Clients von CLI und MCP teilen 76 gleichnamige Methoden und 37 identische Routen-Literale (x-duplication-reuse#2, clients-cli-mcp#3). Diese Duplikation ist dokumentiert.
- **Engine:**
  - Ziel- und Credential-Auflösung samt Localhost-Bypass steht in fünf Activities und ist bereits auseinandergelaufen (engine-activities-powershell-remote#4).
  - Es gibt vier verschiedene Parser für `timeoutSeconds` (engine-activities-powershell-remote#6).
  - Die Pfad-Guard-Logik existiert in drei Kopien: in C#, in `TargetPathGuardScript` und im Zip-Skript. Die UNC-Lücke zeigt, warum das riskant ist (gap-engine-remote-activities-guards#4).
- **Sicherheitscode:** Das Gerüst für Sicherheitsmutationen ist fünffach von Hand kopiert (gap-external-identity#4), Autorisierungs-Primitive sind mehrfach implementiert (api-security-identity#4).
- **Frontend:**
  - Der Alerting-Routen-Editor existiert zweimal und ist auseinandergelaufen. Im System-Editor lässt sich ein gespeichertes Secret nicht löschen (gap-ui-admin-ops-pages#3).
  - Settings-Widgets sind kopiert statt importiert (gap-ui-admin-ops-pages#4).
  - Es gibt sechs Dauer-Formatierer (x-duplication-reuse#4) und zwei Chat-Implementierungen (gap-ui-admin-ops-pages#8).

---

## 6. Konkrete Verbesserungen

**Priorisierung:** Kein Befund hat nach der Gegenprüfung die Schwere High. Die Priorität unten richtet sich deshalb nach Wirkung pro Aufwand und Alltagsrelevanz. **High** heißt konkreter Fehler, Signalverlust oder Härtungslücke mit kleinem Aufwand. **Medium** heißt begrenzte Wirkung oder größerer Umbau. **Low** heißt Hygiene.

**Aufwand:** S < 1 Tag, M = 1–3 Tage, L > 3 Tage.

Alle Vorschläge folgen den Projektregeln: keine Shims oder Feature-Flags, Interfaces nur an echten Nähten, `CancellationToken.None` für Lifecycle-Writes, Migrationen mit der Pflicht-Nachbearbeitung. Jede Änderung braucht einen Regressionstest. Die Codebeispiele sind Skizzen, keine fertigen Patches.

### Priorität High

**H1 — Standard-Fehlerrückmeldung für Mutationen** · S · ui-app-pages-components#1, x-robustness#1

```ts
// src/nodepilot-ui/src/queryClient.ts
export const queryClient = new QueryClient({
  queryCache: new QueryCache({ onError: handleQueryError }),
  // Show a toast for mutations that do not render their own error.
  mutationCache: new MutationCache({
    onError: (error, _variables, _context, mutation) => {
      if (mutation.options.onError || mutation.meta?.handlesError) return;
      handleMutationError(error); // same filters as handleQueryError: abort, auth boundary, DB outage
    },
  }),
  defaultOptions: { /* unchanged */ },
});
```

Es gibt Mutationen, die ihren Fehler selbst anzeigen oder `onError` erst beim `mutate()`-Aufruf übergeben; laut Prüfung etwa in `useWorkflowExecution.ts` und `aiCreateMutation` in `WorkflowsPage.tsx`. Sie bekommen `meta: { handlesError: true }`, sonst entstehen doppelte Toasts. Regressionstest: ein 409 beim Sperren in `useWorkflowLock` erwartet einen Toast.

**H2 — Execution-Abbruch als `Cancelled` statt `Failed` verbuchen** · S · engine-execution-core#1

```csharp
// StepRunner.ExecuteAsync — before the generic catch
catch (OperationCanceledException) when (executionCts.IsCancellationRequested)
{
    // Execution-level cancel: the step was cancelled, it did not fail.
    stepExecution.Status = ExecutionStatus.Cancelled;
    stepExecution.CompletedAt = DateTime.UtcNow;
    await PersistStepStateDurablyAsync(stepDb, stepExecution, "step.cancelled", CancellationToken.None);
    await _notifier.StepCompletedAsync(/* … */ ExecutionStatus.Cancelled /* … */);
    throw;
}
```

Zusätzlich vor dem Einreihen von Nachfolgern in `RunLoopAsync` `ct.ThrowIfCancellationRequested()` aufrufen. Der Regressionstest prüft den Step-Status nach `CancelAsync`; heute prüft der Test nur den Execution-Status.

**H3 — Den eigenen Timeout von restApi als Fehlerergebnis zurückgeben** · S · engine-activities-powershell-remote#1

```csharp
// RestApiActivity.ExecuteAsync
try
{
    var response = await SendWithManualRedirectsAsync(client, config, initialUrl, method, body, headers, cts.Token);
    return await BuildResultAsync(response, cts.Token);
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    // Own timeout, not a caller cancel: a failed result lets the retry policy apply.
    return new ActivityResult { Success = false, ErrorOutput = $"REST API: request timed out after {timeoutSeconds}s" };
}
```

Regressionstest: ein verzögerter Stub-Handler mit `retry.maxAttempts = 2` erwartet zwei Versuche und die Meldung „timed out“.

**H4 — Alert-Zustellung mit Backoff** · M · scheduler#4

```csharp
// NotificationDeliveryAttempt: new nullable column NextAttemptAt (EF migration incl. both post-processing steps)
attempt.NextAttemptAt = result.Success
    ? null
    : now + TimeSpan.FromSeconds(30 * Math.Pow(4, attempt.Attempt - 1)); // 30 s, 2 min, 8 min, 32 min

// RecoverPendingAsync
.Where(a => a.Status == NotificationDeliveryStatus.Pending
         && a.Attempt < MaxAttempts
         && (a.NextAttemptAt == null || a.NextAttemptAt <= now))
```

Das resultierende Zeitfenster gehört in `docs/alerting.md` und auf die Doku-Website (DE und EN).

**H5 — Fehler von System-Alert-Quellen protokollieren** · S · scheduler#2

```csharp
// SystemAlertEvaluator.EvaluateAsync (the evaluator gets an ILogger)
catch (Exception ex) when (ex is not OperationCanceledException)
{
    _logger.LogWarning(ex, "System alert source {SourceId} failed; its policies are skipped this pass", source.SourceId);
    continue;
}
```

Wiederholte Fehler drosseln wie in `EventLogTriggerSource` (erster Fehler, danach jeder zwanzigste). Bei der Rekonstruktion „nicht gefunden“ von „Fehler“ unterscheiden und die echte Ursache speichern. Einen Versuch dabei nicht endlos auf `Pending` lassen: Der Versuchszähler steigt nur in `SendOneAsync`, ein deterministischer Fehler würde sonst nie enden.

**H6 — Den Switcher zurück in die Solution holen und gegen erneutes Herausfallen sichern** · S · build-ci-scripts#1

```xml
<!-- NodePilot.slnx -->
<Project Path="src/NodePilot.Switcher/NodePilot.Switcher.csproj" />
<Project Path="tests/NodePilot.Switcher.Tests/NodePilot.Switcher.Tests.csproj" />
```

```csharp
// tests/NodePilot.Api.Tests/Architecture — every tracked project belongs to the solution
var inSolution = XDocument.Load(slnxPath).Descendants("Project")
    .Select(p => (string)p.Attribute("Path")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
var onDisk = new[] { "src", "tests" }
    .SelectMany(d => Directory.EnumerateFiles(Path.Combine(repoRoot, d), "*.csproj", SearchOption.AllDirectories))
    .Select(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'));
onDisk.Should().BeSubsetOf(inSolution);
```

Mitzuziehen sind der Kommentar in `coverage.runsettings`, die erwartete Zahl in `TestRunSettingsTests` und ein Blick auf die Coverage-Floors, weil `+NodePilot.*` den Switcher künftig mitzählt.

**H7 — UNC-Erkennung unabhängig von der Trennerart** · S · gap-engine-remote-activities-guards#1

```csharp
// PathGuard
static bool IsSeparator(char c) => c == '\\' || c == '/';

internal static bool IsUncPath(string path) =>
    path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);
```

```powershell
# TargetPathGuardScript — same rule for the candidate and for configured roots
if ($Candidate -match '^[\\/]{2}') { throw "UNC paths are not allowed: $Candidate" }
```

Zusätzlich nach `Path.GetFullPath` prüfen, dass `Path.GetPathRoot` ein Laufwerks-Root ist. Regressionstests mit beiden gemischten Formen gehören in `PathGuardTests` und die Tests des Ziel-Guards.

**H8 — Ungültige Redaction-Patterns beim Start ablehnen** · S · gap-engine-remote-activities-guards#2

```csharp
// OutputRedactor constructor
for (var i = 0; i < patterns.Count; i++)
{
    try { custom.Add(new Regex(patterns[i], RxOpts, RegexTimeout)); }
    catch (ArgumentException ex)
    {
        throw new InvalidOperationException($"Logging:Redaction:Patterns:{i} is not a valid regular expression.", ex);
    }
}
```

Die Prüfung gehört in die vorhandene Boot-Validierung, damit sie den Start abbricht und nicht erst den ersten Step. Ob ein ungültiges Pattern den Start verhindern oder nur laut protokolliert werden soll, ist als offene Frage in Abschnitt 8 notiert.

**H9 — Template-Platzhalter in einem einzigen Durchlauf auflösen** · M · engine-execution-core#3

```csharp
// VariableResolver: one pass over all three namespaces — substituted text is never scanned again
static readonly Regex AnyTemplate = new(
    $"(?<g>{GlobalsBody})|(?<m>{ManualBody})|(?<s>{StepBody})", RegexOptions.Compiled, RegexTimeout);

configJson = AnyTemplate.Replace(configJson, m =>
    m.Groups["g"].Success ? ResolveGlobal(m)
  : m.Groups["m"].Success ? ResolveManual(m)
  : ResolveStep(m));
```

`GlobalsBody`, `ManualBody` und `StepBody` sind die Ausdrücke der bestehenden `GlobalsPattern`, `ManualPattern` und `StepPattern`. `ResolveStringValue` und `ConditionEvaluator.ResolveTemplates` bekommen denselben Einzeldurchlauf. Regressionstest: Ein Manual-Wert `{{a.output}}` bleibt wörtlich stehen und fällt in die T-7.1-Diagnose. `TemplateGrammarParityTests` muss mitlaufen.

**H10 — Besitzer und ACL der Switcher-Konfiguration prüfen** · M · switcher-desktop#1

```csharp
// SwitcherConfiguration.Load — fail closed before reading the file
static void RequireAdminOnlyWritable(FileSystemSecurity security, string path)
{
    var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!;
    if (!IsAdministratorsOrSystem(owner))
        throw new InvalidOperationException($"{path} must be owned by Administrators or SYSTEM.");

    const FileSystemRights Dangerous = FileSystemRights.Write | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
    {
        if (rule.AccessControlType == AccessControlType.Allow
            && !IsAdministratorsOrSystem((SecurityIdentifier)rule.IdentityReference)
            && (rule.FileSystemRights & Dangerous) != 0)
            throw new InvalidOperationException($"{path} is writable by non-administrators.");
    }
}
```

Die Prüfung muss im Switcher selbst entstehen. Der Abhängigkeitsgraph (`Switcher → ∅`) verbietet die Wiederverwendung von `RestrictedFileWriter`. Prüfen: die Datei, die Verzeichnisse `NodePilot` und `NodePilot\Switcher` sowie die aufgelöste `np.exe`. Zusätzlich sollte `EnsureProtectedDirectory` den Besitzer auf Administrators setzen. Den PATH-Fallback auf `%ProgramFiles%` und `%SystemRoot%` beschränken oder streichen. Laut Prüfung liest er den Prozess-PATH, nicht wie dokumentiert den Maschinen-PATH.

**H11 — Sicherungsverzeichnisse mit geschützter DACL anlegen und vor dem Zurückspielen prüfen** · S · deploy-installers#1

```powershell
New-Item -ItemType Directory -Path $backupDir | Out-Null
Set-DirectoryAclForService -Path $backupDir -ServiceAccount $svcAccount -ReadOnlyForService
Assert-NodePilotInstallRootHardened -Path $backupDir -RequireProtectedRules

# rollback path
Assert-NodePilotInstallRootHardened -Path $backupDir -RequireProtectedRules
Copy-DirectoryContents -Source $backupDir -Destination $InstallPath
Assert-NodePilotExtractedFiles -RootPath $InstallPath
```

Das Rollback-Verzeichnis der Installation erst nach `Assert-SafeInstallRoot` anlegen. `Uninstall-NodePilot.ps1` sollte die Sicherungen mit entfernen.

**H12 — Die SCIM-Last-Admin-Sperre nur für aktive Admins auslösen** · S · gap-external-identity#1

```csharp
// ScimProvisioningService.DeleteUserCoreAsync (analog in UpdateExistingUserAsync)
if (identity.User.IsActive
    && identity.User.Role == UserRole.Admin
    && await db.Users.CountAsync(x => x.IsActive && x.Role == UserRole.Admin, ct) <= 1)
```

Regressionstests: ein deaktivierter Admin plus Break-Glass-Admin, dann erwartet DELETE ein 204 und ein wiederholtes `active=false` ein 200.

### Priorität Medium

| # | Maßnahme | Aufwand | Befund |
|---|---|---|---|
| M1 | Junction: `EvaluateSuccessorReadiness` löschen, die Tests auf die echte `EvaluateSuccessorDecision` umstellen. Im `WorkflowDefinitionStructuralValidator` `mode` (`waitAll`/`waitAny`/`waitNofM`) und `1 ≤ requiredCount ≤ aktive Eingänge` prüfen, Code `invalid-junction-config`. | S | engine-execution-core#4 |
| M2 | SCOrch-Import: Bedingten Fan-in erkennen und eine eigene Warnung mit Ziel-Label ausgeben, die den Unterschied zu SCOrch erklärt. Dazu ein Regressionstest mit zwei `failed`-Kanten. Mittelfristig per ADR klären, ob ein Junction-Modus für Fehler-Links gebraucht wird. | S | scorch-import#1 |
| M3 | OIDC: Neuen Code `UserOidcJitCreated` in `AuditActions` anlegen (Guard `AuditActionsCatalogTests`) und im selben `SaveChanges` schreiben. Nach dem Commit `AuditEventForwarder.ForwardCommitted` aufrufen, auch in `ReconcileExistingAsync`. | S | gap-external-identity#2 |
| M4 | Designer-Projektion durchsetzen: `useCriticalPath` liefert nur noch eine Map `nodeId → Annotation`, die `useDisplayedGraph` einmischt; kein Hook ruft `useReactFlow().setNodes` auf. Danach `useNodeAnnotations` und `useCoverageHeatmap` ebenso auf Maps umstellen. | S/M | ui-designer#1, ui-designer#2 |
| M5 | Skripteditor: `AiPromptDialog` stoppt Escape per `stopPropagation`. Der Editor ignoriert `defaultPrevented` und fragt bei geändertem Text nach (`confirmDialog`). | S | gap-ui-designer-subcomponents#2 |
| M6 | KI-Merge: Weicht der Vorschlag strukturell vom Original ab und wird das Array trotzdem wiederhergestellt, eine Note ausgeben. Die Notes-Liste existiert bereits. | S | gap-llm-trust-boundary#2 |
| M7 | FileWatcher: Nicht feuernde Ereignisse nur im In-Memory-Snapshot nachführen. Persistiert wird beim Fire (dort ist der Snapshot ohnehin Teil der Admit-Transaktion) und gedrosselt im vorhandenen Probe-Takt. | M | scheduler#1 |
| M8 | Switcher: Fehler 1061 wie einen laufenden Stopp behandeln (`IsStopAlreadyUnderway(error)` als reine, testbare Funktion) und in Wartephase und Forced-Stop übergehen. | S | switcher-desktop#3 |
| M9 | Ausführungsliste: `sortBy`/`sortDir` serverseitig gegen eine Whitelist auswerten und CLI und MCP mitziehen. Alternativ die Sortierung bei mehreren Seiten sichtbar als „nur diese Seite“ kennzeichnen. | M | ui-app-pages-components#3 |
| M10 | `ModalShell`: Das Panel bekommt `role="dialog"`, `aria-modal="true"` und `aria-labelledby`. Danach die Formulardialoge außerhalb des Designers migrieren, die Designer-Overlays bleiben beim Roadmap-Sammelposten. | S | x-duplication-reuse#3 |
| M11 | `ci.yml`: Workflow-weit `permissions: contents: read` setzen (der Job `e2e-report` behält seine Erweiterung) und in allen Checkouts `persist-credentials: false`. | S | build-ci-scripts#2 |
| M12 | Guard-Lücken schließen: einen Rollen-Sweep für Admin-only-Endpunkte über den ApiExplorer, Roslyn statt Regex im Audit-Guard (Roslyn nutzt dieselbe Datei schon), Attributlisten in Endpoint- und DTO-Guards. | M | tests-api-clients#1, #2, #3 |
| M13 | Electron-Fuses nach dem Packaging und vor dem Signieren härten (u. a. `RunAsNode` aus, ASAR-Integrität an). Dafür braucht es `@electron/fuses` als neue Dev-Abhängigkeit, die laut `CLAUDE.md` vorher abgestimmt werden muss. | S | switcher-desktop#2 |
| M14 | Setup: Der Installer gibt Secrets nicht aus, wenn der Adapter ihn aufruft. Der Adapter liest Key und Token ohnehin selbst ins Ergebnis. | S | deploy-installers#2 |
| M15 | `WorkflowEditorPage` zerlegen, denn der Roadmap-Trigger ist überschritten. Statt 90 Einzel-Props zwei gruppierte Werte übergeben: ein einmal berechnetes `EditorLifecycle`-Objekt (Lock-, Publish- und Save-Zustand samt abgeleiteter Flags) und gebündelte Aktionen. Das History-/Dirty-Protokoll in einen Hook ziehen. | L | ui-designer#3, ui-designer#5, x-readability-complexity#1 |
| M16 | Frontend-Domänenschicht: je Ressource ein Modul unter `src/api/` mit Query-Key-Fabrik und Fetchern (siehe Skizze unten) und schrittweise Migration der 51 Aufrufer. | M/L | x-architecture-dependencies#1 |

Skizze zu M16:

```ts
// src/api/executions.ts
export const executionKeys = {
  all: ['executions'] as const,
  list: (filter: ExecutionFilter) => [...executionKeys.all, 'list', filter] as const,
  steps: (id: string) => ['execution-steps', id] as const,
};

export const fetchExecutions = (filter: ExecutionFilter, signal?: AbortSignal) =>
  getPage<Execution>('/executions', toSearchParams(filter), { signal });
```

### Priorität Low

| # | Maßnahme | Aufwand | Befund |
|---|---|---|---|
| L1 | Lifecycle- und Audit-Writes konsequent mit `CancellationToken.None` schreiben (Cancel-Zombie-Fallback, DbAdmin-Write-Ergebnis, Secret-Rotation, Test-Fire-Ledger, `execution.start`) | S | api-workflow-surface#4, api-admin-ops-surface#3, api-alerting-ai-realtime#4, engine-execution-core#5 |
| L2 | SIEM-Weiterleitung erst nach dem Transaktions-Commit | S | api-admin-ops-surface#1, scheduler#6 |
| L3 | Retry-Backoff auch bei `maxDelayMs = 0` deckeln | S | engine-execution-core#6 |
| L4 | Globale Query-Retry-Policy: deterministische 4xx nicht wiederholen; React-Query-`signal` an den API-Client durchreichen | S/M | ui-state-data-layer#2, ui-state-data-layer#3 |
| L5 | Regex-Cache der Edge-Conditions mit Verdrängung | S | x-performance#5 |
| L6 | `SettingsSections`: Defaults einmal aus den Options-Klassen ableiten statt 140 Fallback-Literale | M | x-readability-complexity#5 |
| L7 | Härtungsflags einmal zentral parsen (`DisallowShellExecute`, `RejectTraversal`, `Cluster:Enabled`) | S | api-hosting-config-backup#2, api-hosting-config-backup#4 |
| L8 | Update: Downgrade auf eine ältere Version nur mit ausdrücklichem Schalter erlauben | S | deploy-installers#5 |
| L9 | LLM-Grenze: Profil-Key nicht an eine abweichende `baseUrl` senden; IPv6-Metadata-Adressen im Literal- und im Connect-Guard sperren; Nicht-String-Werte unter Secret-Keys maskieren | S | gap-llm-trust-boundary#1, #5, #4 |
| L10 | Retry: RBAC-Gate vor der Statusprüfung, wie bei allen anderen Execution-Endpunkten | S | api-workflow-surface#5 |
| L11 | Dashboard: 7/14-Tage-Aggregate aus den vorhandenen Stunden-Rollups lesen. Nach der Roadmap-Regel erst bei gemessener Latenz angehen. | M | x-performance#2, api-admin-ops-surface#2 |
| L12 | Erzählende Kommentare nach der `CLAUDE.md`-Regel kürzen, beginnend mit den nach Einführung der Regel entstandenen | S/M | x-readability-complexity#4 |

---

## 7. Risiken / Technical Debt

### 7.1 Sicherheit

Kein Befund erreicht Critical oder High. Bewertet wurde unter dem dokumentierten Vertrauensmodell: Operatoren sind vertrauenswürdige Automation-Autoren, lokale Ausführung unter der Dienstidentität ist ein Produktmerkmal (`CLAUDE.md`, Abschnitt „Localhost-Bypass / Operator-Trust“). Befunde, die Operator als Sicherheitsgrenze behandeln, sind deshalb keine Befunde.

| Risiko | Mechanismus | Voraussetzung | Schwere | Befund |
|---|---|---|---|---|
| Ausgehende SMB/NTLM-Verbindung trotz UNC-Sperre | `IsUncPath` erkennt gemischte Trenner nicht; der Reparse-Walk läuft vor der Containment-Prüfung | Ein nicht vertrauenswürdiger Wert erreicht ein Pfadfeld, z. B. über `{{manual.X}}` aus einem Webhook-Feld | Medium | gap-engine-remote-activities-guards#1 |
| Unmaskierte Secrets | Ungültiges Custom-Pattern wird still verworfen | Tippfehler in `Logging:Redaction:Patterns` | Medium | gap-engine-remote-activities-guards#2 |
| Second-Order-Expansion von Trigger-Daten | Getrennte Replace-Durchläufe | Authentifizierter Trigger-Aufrufer; Ausgabe landet an einem für ihn sichtbaren Ziel | Medium | engine-execution-core#3 |
| Lokale Rechteausweitung über die Switcher-Konfiguration | Keine Prüfung von Besitzer und ACL; der Switcher läuft eleviert | `%ProgramData%\NodePilot` wurde nicht vom Installer angelegt (Standalone-Drop) | Medium | switcher-desktop#1 |
| Manipulierte Binaries beim Rollback | Sicherungsverzeichnis erbt die Eltern-ACL; Restore ohne Hash-Abgleich | Install-Root außerhalb von Program Files und ein fehlgeschlagenes Update | Medium | deploy-installers#1 |
| Lücke im SIEM-Strom | OIDC-JIT ohne Audit; Reconciliation ohne Weiterleitung | OIDC ist aktiv | Medium | gap-external-identity#2 |
| Legacy-Trigger-Key im Setup-Transcript | `Write-Host` landet im Transcript | Setup über den Adapter | Low | deploy-installers#2 |
| CI-Token ohne ausdrückliche Least-Privilege-Rechte | Kein `permissions`-Block, persistierte Credentials vor `npm ci` | Repo-Voreinstellung ändert sich (derzeit laut Prüfung „read“) | Low | build-ci-scripts#2 |
| Electron-Fuses mit Default-Werten | Keine Fuse-Härtung beim Packaging | Lokaler Angreifer auf dem Desktop-Host | Low | switcher-desktop#2 |
| Härtungsflag uneinheitlich interpretiert | Drei Leser parsen `StartProgram:DisallowShellExecute` unterschiedlich | Nicht standardkonformer Flag-Wert | Low | api-hosting-config-backup#2 |
| Natives Argument an `sc.exe` | Dienstname nur PowerShell-gequotet | Nicht vertrauenswürdiger Dienstname, Windows PowerShell 5.1 am Ziel | Low | gap-engine-remote-activities-guards#3 |
| IPv6-Metadata-Endpunkt erreichbar | Literal- und Connect-Guard decken nur IPv4 bzw. `fe80::/10` ab | LLM-Endpunkt oder Ziel in der Cloud | Low | gap-llm-trust-boundary#5 |
| Profil-Key an fremde `baseUrl` | BaseUrl und ApiKey werden feldweise unabhängig aufgelöst | Ein Node überschreibt nur `baseUrl`; Hygiene unter dem Vertrauensmodell | Low | gap-llm-trust-boundary#1 |
| Statusauskunft ohne Ordnerrecht | Retry prüft den Status vor dem RBAC-Gate | Operator ohne Zugriff auf den Ordner | Low | api-workflow-surface#5 |
| Seed-Passphrase im Speicher | BSTR wird nicht mit `ZeroFreeBSTR` gelöscht | Speicherzugriff während des Setups | Low | deploy-installers#8 |

Bereits bewusst entschieden und hier nicht erneut bewertet: der Abflusskanal `http_request` ohne `allowedHosts` bei Prompt-Injection (ADR 0016, ai-agents#4), die Site-CSP mit `'unsafe-eval'` für die Browser-Demo (docs-ui-website#4), die lexikalische Read-only-Prüfung im DbAdmin auf SQL Server (Roadmap R2).

> **Hinweis zur Veröffentlichung:** Die offenen Punkte in dieser Tabelle sind nicht behoben. Das Repository ist öffentlich. Vor dem Push dieses Berichts sollten die Medium-Punkte behoben oder ihre Beschreibung auf „was wird geändert“ gekürzt werden, wie es `docs/roadmap.md` für Sicherheitsbefunde handhabt.

### 7.2 Performance

- **Dashboard:** rund 30 sequenzielle Roundtrips je Aufruf, fünf ungecachte 7/14-Tage-Scans (api-admin-ops-surface#2, x-performance#2).
- **Executions-Suche:** nicht sargierbares OR-Prädikat, für Count und Seite doppelt ausgewertet (x-performance#3).
- **Hintergrunddienste:**
  - Der FileWatcher persistiert je Ereignis den vollen Snapshot (scheduler#1).
  - Der Execution-Collector schafft höchstens 200 terminale Executions pro 30-s-Pass (scheduler#5).
  - Das Löschen einer Regel lädt alle ihre Ledger-Zeilen in den Change Tracker, statt set-basiert zu löschen (core-data#5).
- **Synchrone DNS-Auflösung ohne Timeout** auf asynchronen Pfaden: im SSRF-Guard und bei der Loopback-Erkennung (x-security#1, engine-activities-powershell-remote#5).
- **Speicher:** Backup-Preview, -Restore und -Export puffern das Artefakt mehrfach (api-hosting-config-backup#3).
- **Frontend und Website:**
  - Queries lassen sich nicht abbrechen (ui-state-data-layer#3).
  - Das Doku-Bundle lädt den kompletten zweisprachigen Korpus eager (docs-ui-website#1).
  - Das Prerendering wird verworfen statt hydriert (docs-ui-website#8).

Im Sperrvermerk der Roadmap stehen Hebel, die bereits gemessen widerlegt sind, etwa höhere oder niedrigere Concurrency-Caps oder ein Runspace-Pre-Warm. Sie sind hier nicht erneut vorgeschlagen.

### 7.3 Architektur-Anti-Patterns

- **Prozessweiter statischer Zustand** über `WorkflowEngine` hinaus, mit dokumentierten Test-Flakes (x-architecture-dependencies#2).
- **Der Rohgraph als Ablage für Laufzeit-Annotationen** im Designer (ui-designer#1, ui-designer#2).
- **Testcode im Produktionscode:** test-only Fallbacks, darunter ein offener `LocalLoginMode`-Default (api-security-identity#1, core-data#4, scheduler#7, ui-state-data-layer#7).
- **Transaction Scripts** in sehr langen Controller- und Middleware-Methoden (x-readability-complexity#3; dicke Controller sind laut Roadmap akzeptiert).
- **Handgepflegte Mehrfachabbildungen** ohne Guard: Settings dreifach, Ziel-Auflösung fünffach, Sicherheits-Mutationsgerüst fünffach (x-readability-complexity#5, engine-activities-powershell-remote#4, gap-external-identity#4).

### 7.4 Fehlende Tests und kritische Stellen

- **Kein automatisches Netz:** Der Switcher fehlt in der Solution (build-ci-scripts#1). P/Invoke-Gateway und Electron-`main.ts` sind ungetestet (switcher-desktop#4, tests-frontend-e2e#8).
- **Kritische Pfade ohne zielgenauen Test:**
  - der Step-Status nach einem Execution-Cancel (engine-execution-core#1),
  - der restApi-Timeout mit Retry (engine-activities-powershell-remote#1),
  - bedingter Fan-in beim SCOrch-Import (scorch-import#1),
  - die XML-Härtung des SCOrch-Imports (scorch-import#3),
  - die einmalige Expansion von Trigger-Werten (engine-execution-core#3),
  - die gemischte UNC-Form (gap-engine-remote-activities-guards#1),
  - das SCIM-Deprovisioning eines deaktivierten Admins (gap-external-identity#1).
- **Provider:** SQL Server läuft in CI nie gegen eine echte Instanz (core-data#2, tests-engine-data-ai#7).
- **Guards mit blinden Flecken und vakuöse Tests** (Abschnitt 5.1).

### 7.5 Bekannte, bewusst zurückgestellte Schulden

Diese Punkte sind dokumentiert und tragen einen Auslöser oder eine Begründung. Sie bleiben technische Schuld, sind aber keine neuen Entdeckungen.

| Thema | Wo dokumentiert |
|---|---|
| Größe von `WorkflowEngine` (1 509 Zeilen) und ihr prozessweiter Zustand | Roadmap, Anhang „Refactoring ohne Anlass“; R2 „Qualität & Betrieb“ |
| Dicke Controller (`WorkflowsController`, `ExecutionsController`) | Roadmap, Anhang „Refactoring ohne Anlass“ |
| `BackupRestoreService` (1 646) und `Install-NodePilot.ps1` (1 727) | Roadmap, Anhang „Refactoring ohne Anlass“ |
| `WorkflowEditorPage`: Wiederaufnahme bei über ~1 700 Zeilen, **heute 2 059** | Roadmap, Anhang und R2. Der Auslöser ist erreicht, siehe M15. |
| Mehrere Mutationspfade für Workflow-Definitionen | R2 „`IWorkflowDefinitionMutator`“ |
| Doppelte Ordnerbaum-Algorithmen | R2 „Qualität & Betrieb“ |
| N+1 bei Notifications, Workflow-Statistiken und Ordner-Grants | R2, Auslöser: gemessene Latenz |
| 500er-Cap auf `GET /api/workflows` | R2 „Daten & Performance“ |
| E2E-Catch-all `200 []`, nur hermetische Suiten | R2 „Qualität & Betrieb“ |
| Migrationen nur auf SQLite, SQL Server nicht in CI | R1-Posten 26 |
| Kein DB-Fencing der Step-Writes im HA-Betrieb | R2 „Qualität & Betrieb“ |
| Keine Audit-Outbox (erfolgreiche Mutation ohne garantierten Audit-Eintrag) | R2 „Qualität & Betrieb“ |
| Laufzeit-Identität braucht `db_owner` | R2 „Qualität & Betrieb“ |
| xUnit1051 und Nullable-Warnungen sind kein Gate | R2 „Qualität & Betrieb“ (build-ci-scripts#6) |
| React Compiler nicht aktiv, Locales im Eager-Bundle | R2 „Daten & Performance“ |
| Modal-Konsolidierung | R2 „Designer“, Sammelposten |

---

## 8. Offene Fragen

### Nicht beurteilbar anhand des vorhandenen Codes

- **Laufzeit und Last:**
  - die tatsächliche Lastgrenze des Einzelprozesses,
  - Dashboard- und Suchlatenz bei großen Datenmengen, inklusive Indexnutzung auf PostgreSQL und SQL Server,
  - die Render-Performance des Designers bei mehr als 200 Knoten,
  - die Schreiblast des FileWatchers,
  - der Speicherbedarf eines großen Restores bzw. eines 300-MiB-SCOrch-Imports,
  - die Bundle-Größen.
- **Testwirksamkeit:** die erreichte Coverage je Projekt (lokal nicht gemessen; CI erzwingt 85/70), die Flake-Rate, die CI-Laufzeiten.
- **Verhalten gegen echte Infrastruktur:**
  - WinRM-Abbruch auf einem unerreichbaren Ziel und die Blockierdauer von `Runspace.Close`,
  - LDAP, OIDC und SCIM gegen echte IdPs, die Kerberos-/NTLM-Policy,
  - HA-Failover und Fencing unter Last,
  - das SCM-Verhalten der SCOrch-Dienste,
  - die Job-Object-Isolation auf echten Hosts.
- **Konfiguration außerhalb des Repositorys:**
  - Branch-Protection und Required Checks,
  - die Default-Rechte des `GITHUB_TOKEN`,
  - die Webhoster-Konfiguration der Website,
  - die ACL von `%ProgramData%\NodePilot` auf realen Installationen,
  - Fuse-Belegung und Signatur der ausgelieferten Desktop-EXE.
- **Kryptografische Betriebsparameter:** der Schutz des AES-GCM-Master-Keys, die DPAPI-Bindung, die JWT-Schlüsselverwaltung im Betrieb, die Wirksamkeit der Log-Redaction in ausgelieferten Logdateien.
- **KI:** die inhaltliche Wirksamkeit der Agenten-Prompts und Reviewer-Protokolle, die Token-Kosten, die Datenaufbewahrung beim LLM-Provider.
- **Oberfläche:** Screenreader- und Tastaturbedienung, die visuelle Konsistenz über alle Skins.
- **SCOrch:**
  - Property-Namen von Activities, die nicht an einem realen Export verifiziert sind,
  - die Semantik der Link-Filter „matches pattern“,
  - das Verhalten der SCOrch-Web-API.

### Fragen an das Team

1. War das Entfernen von Switcher und Switcher.Tests aus `NodePilot.slnx` im Commit `004d45b` beabsichtigt?
2. Soll der SCOrch-Import für bedingten Fan-in einen eigenen Junction-Modus bekommen (Ergänzung zu ADR 0013), oder reicht eine deutliche Warnung?
3. Ist das Alert-Retry-Fenster von rund zwei Minuten eine bewusste Entscheidung? Dann gehört es in `docs/alerting.md`.
4. Soll ein ungültiges Redaction-Pattern den Start abbrechen (fail-closed) oder nur laut protokolliert werden?
5. Bleibt die Duplikation der CLI- und MCP-Endpoint-Wrapper und -DTOs gewollt, oder lohnt sich eine gemeinsame Schicht?
6. Gilt der Roadmap-Auslöser für die Zerlegung von `WorkflowEditorPage` (über ~1 700 Zeilen) mit 2 059 Zeilen als eingetreten?

---

## Anhang A — Methodik und Kennzahlen

### Vorgehen

1. **Inventur:** Projekte, Dateien, Zeilen, Testanzahlen, größte Dateien.
2. **28 Prüfbereiche**, davon 22 Subsysteme und 6 Querschnittsthemen. Jeder Prüfer arbeitete mit begrenztem Werkzeugbudget und lieferte:
   - Überblick, Architekturstil und Komplexitätseinschätzung,
   - Teilnoten,
   - bis zu 6 Stärken und bis zu 8 Befunde, jeweils mit Datei, Zeile und wörtlichem Zitat,
   - eine Liste nicht beurteilbarer Themen.
   Bekannte Entscheidungen und frühere Fehlbefunde waren als Kontext vorgegeben.
3. **Verifikation je Bereich:** Ein unabhängiger, auf Widerlegung angesetzter Prüfer kontrollierte jeden Befund auf Zitat, Fakten, Dokumentationslage, „bereits behoben?“, Schwere und Qualität des Vorschlags. Ein weiterer Prüfer kontrollierte die Stärken.
4. **High-Vorschläge** bekamen einen zusätzlichen Widerlegungsprüfer, bei Widerspruch einen Stichentscheid.
5. **Vollständigkeitsprüfung:** Ein Kritiker suchte Code, den kein Bereich gründlich gelesen hatte. Daraus entstanden fünf Nachprüfbereiche mit demselben Verfahren: Remote-Activities und Guards, externe Identität, Designer-Unterkomponenten, Admin- und Ops-Seiten, LLM-Vertrauensgrenze.
6. **Zweitprüfung:** Alle 50 nach Schritt 3 als Medium eingestuften Befunde prüfte ein weiterer unabhängiger Prüfer, der das erste Urteil nicht kannte. Bei Widerspruch gab es einen Stichentscheid. Gewertet wird die niedrigere Schwere.
7. **Bewertung:** Drei unabhängige Bewerter mit den Perspektiven Wartung, Architektur und Betrieb/Sicherheit. Die Gesamtnote ist der Median.
8. **Eigene Stichprobe:** Neun zentrale Fundstellen wurden zusätzlich direkt am Code nachgeprüft, alle bestätigt.

**Prüfbereiche:**

- **API:** Workflow-, Ordner-, Execution- und Trigger-Endpunkte · Administration, DbAdmin, Dashboard, Benutzer, Maschinen, Credentials, Diagnose · Alerting, Custom Activities, KI-Endpunkte, Agenten, SignalR · Authentifizierung, Sessions, LDAP/OIDC/SCIM, Autorisierung · Composition Root, Hosting, Konfiguration, Backup/Restore
- **Engine und Backend:** Ausführungskern · Activities, PowerShell, WinRM · SCOrch-Import · KI-Stack und Agenten · Scheduler · Core, Data, Telemetry
- **Clients und Desktop:** CLI und MCP · Switcher und Electron
- **Frontend und Website:** Designer · Seiten und Komponenten · API-Schicht, Stores, Hooks, SignalR · Doku- und Projekt-Website
- **Auslieferung und Infrastruktur:** Deployment-Skripte · Build, CI, Konfiguration, Skripte, Samples, Grafana
- **Tests:** Engine/Data/Ai · Api/Cli/Mcp/Switcher · Frontend und E2E
- **Querschnitt:** Architektur und Abhängigkeiten · Duplikate · Robustheit (Fehlerbehandlung, Logging, Nebenläufigkeit) · Sicherheit · Performance · Lesbarkeit und Komplexität
- **Nachprüfung:** Remote-Activities und Guards · externe Identität · Designer-Unterkomponenten · Admin- und Ops-Seiten · LLM-Vertrauensgrenze

**Verifikationsstatistik:**

- 212 Befunde, davon 209 bestätigt (49 von zwei Prüfern, 160 von einem) und 3 verworfen (Anhang C).
- Von den Reviewern vorgeschlagene Schwere: 3 High, 97 Medium, 112 Low.
- Endgültige Schwere der bestätigten Befunde: 23 Medium, 183 Low, 3 ohne praktische Auswirkung.
- 194 Stärken geprüft: 161 bestätigt, 33 teilweise bestätigt, keine widerlegt.

**Berücksichtigter Kontext:** `docs/roadmap.md` (R2 und Sperrvermerk), `docs/adr/`, `CLAUDE.md`, `docs/security-findings.md`, `docs/audit-rounds/` sowie die bekannten Fehlbefunde früherer Audits.

**Grenzen:**

- Keine Builds, Tests, Coverage- oder Laufzeitmessungen.
- Keine Prüfung gegen echte Infrastruktur.
- Die Prüfer lasen mit Budget; nicht jede Datei wurde Zeile für Zeile gelesen. Den Lesestand je Bereich haben die Prüfer selbst dokumentiert.
- Low-Befunde sind einfach verifiziert.

### Kennzahlen (versionierte Dateien, gezählt am 2026-10-15)

| Bereich | Dateien | Zeilen | Tests |
|---|---:|---:|---|
| `NodePilot.Api` | 295 | 50 082 | Api.Tests: 248 Dateien, 61 424 Zeilen, 2 457 Testmethoden |
| `NodePilot.Engine` | 118 | 26 145 | Engine.Tests: 215 Dateien, 52 285 Zeilen, 2 171 Testmethoden |
| `NodePilot.Core` | 143 | 11 152 | — |
| `NodePilot.Cli` | 63 | 9 450 | Cli.Tests: 573 Testmethoden |
| `NodePilot.Scheduler` | 51 | 8 038 | in Engine.Tests |
| `NodePilot.Ai` | 50 | 7 898 | Ai.Tests: 515 Testmethoden |
| `NodePilot.Data` | 53 (davon 17 Migrationen) | 5 598 (ohne Migrationen) | Data.Tests: 297 Testmethoden |
| `NodePilot.Mcp` | 30 | 4 650 | Mcp.Tests: 174 Testmethoden |
| `NodePilot.Switcher` | 25 | 3 500 | Switcher.Tests: 94 Testmethoden (laufen nicht in CI) |
| `NodePilot.Remote` / `NodePilot.Telemetry` | 6 / 5 | 818 / 657 | — |
| **C#-Produktivcode gesamt** | **839** | **≈ 128 000** (ohne Migrationen) | **6 281 Testmethoden** (`[Fact]`/`[Theory]`) |
| `nodepilot-ui` (TS/TSX inkl. Tests) | 782 | 147 158 | 257 Vitest-Dateien, 80 E2E- und 4 Demo-Specs |
| `nodepilot-docs-ui` / `nodepilot-desktop` | 82 / 21 | 7 735 / 1 933 | 16 / 8 Testdateien |
| PowerShell (alle versionierten Skripte, überwiegend `deploy/` und `scripts/`) | 82 | 25 192 | deploy-Testskripte, u. a. `Test-DeploymentTemplates.ps1` |

Weitere Kennzahlen: 50 Dateien unter `src/NodePilot.Api/Controllers/` mit 206 HTTP-Aktionsattributen, 24 Dateien mit gehosteten Hintergrunddiensten, 18 ADRs, rund 40 Guard- und Paritätstests, 1 TODO/FIXME/HACK-Treffer im Produktionscode.


---

## Anhang B — Alle bestätigten Befunde

Schwere = endgültige Einstufung nach Gegenprüfung (bei zwei Prüfern die niedrigere). Prüfung = Zahl der unabhängigen Prüfer, die den Befund bestätigt haben. „korr.“ = die Prüfer haben die ursprüngliche Behauptung präzisiert oder abgeschwächt; maßgeblich ist die korrigierte Fassung. Welche Themen bereits als bewusst zurückgestellte Schuld dokumentiert sind, steht in Abschnitt 7.5.

| ID | Schwere | Prüfung | Dimension | Befund | Fundstelle |
|---|---|---|---|---|---|
| build-ci-scripts#1 | Medium | zweifach | Testlücke | Switcher und Switcher.Tests liegen außerhalb von NodePilot.slnx und werden nie automatisch gebaut, getestet oder analysiert | `NodePilot.slnx:22` |
| deploy-installers#1 | Medium | zweifach | Sicherheit | Backup- und Rollback-Verzeichnisse erben die Eltern-ACL und werden ungeprüft als Dienst-Binaries zurückgespielt | `deploy/Update-NodePilot.ps1:299` |
| engine-activities-powershell-remote#1 | Medium | zweifach | Logik/Architektur | restApi-Timeout wird als Abbruch durchgereicht und von der Retry-Policy nie wiederholt | `src/NodePilot.Engine/Activities/RestApiActivity.cs:56` |
| engine-execution-core#1 | Medium | zweifach | Logik/Architektur | Abbruch auf Execution-Ebene wird pro Step als Failed verbucht | `src/NodePilot.Engine/Execution/StepRunner.cs:359` |
| engine-execution-core#3 | Medium | zweifach | Sicherheit | Trigger-Eingaben werden ein zweites Mal als Step-Template expandiert | `src/NodePilot.Engine/Execution/VariableResolver.cs:273` |
| engine-execution-core#4 | Medium | zweifach | Testlücke | Junction-Semantik doppelt implementiert, Tests decken die ungenutzte Variante ab, keine Save-Validierung | `src/NodePilot.Engine/Execution/WorkflowScheduler.cs:348` |
| gap-engine-remote-activities-guards#1 | Medium | zweifach | Sicherheit | Unbedingter UNC-Block lässt sich mit gemischten Trennern umgehen; der lokale Reparse-Walk fragt den UNC-Root dann direkt ab | `src/NodePilot.Engine/Security/PathGuard.cs:326` |
| gap-engine-remote-activities-guards#2 | Medium | zweifach | Sicherheit | Ungültige Operator-Patterns für die Redaction werden stillschweigend verworfen | `src/NodePilot.Engine/Security/OutputRedactor.cs:133` |
| gap-external-identity#1 | Medium | zweifach | Logik/Architektur | SCIM-Deprovisioning eines bereits deaktivierten Admins scheitert dauerhaft mit 409 | `src/NodePilot.Api/Security/Scim/ScimProvisioningService.cs:279` |
| gap-external-identity#2 | Medium | zweifach | Sicherheit | OIDC-Pfad: JIT-Anlage ohne Audit-Eintrag, Reconciliation-Audit ohne SIEM-Weiterleitung | `src/NodePilot.Api/Security/Oidc/OidcIdentityMapper.cs:187` |
| gap-llm-trust-boundary#2 | Medium | zweifach | Logik/Architektur | Array-Merge verwirft KI-Änderungen an Decision-Cases und Condition-Gruppen still | `src/NodePilot.Ai/WorkflowDefinitionMerge.cs:182` |
| gap-ui-designer-subcomponents#2 | Medium | zweifach | Codequalität | Escape im Skripteditor verwirft ungespeicherte Änderungen, auch wenn es im eingebetteten KI-Dialog gedrückt wird | `src/nodepilot-ui/src/components/designer/ScriptEditorDialog.tsx:372` |
| scheduler#1 | Medium | zweifach | Performance | FileWatcher persistiert bei jedem rohen Dateiereignis den vollständigen Verzeichnis-Snapshot | `src/NodePilot.Scheduler/Sources/FileWatcherTriggerSource.cs:386` |
| scheduler#2 | Medium | zweifach | Codequalität | SystemAlertEvaluator verschluckt Quellen- und Rekonstruktionsfehler ohne Log oder Metrik | `src/NodePilot.Scheduler/SystemAlerts/SystemAlertEvaluator.cs:75` |
| scheduler#4 | Medium | zweifach | Logik/Architektur | Alert-Retry erschöpft sich nach rund zwei Minuten ohne Backoff | `src/NodePilot.Scheduler/NotificationDispatcher.cs:44` |
| scorch-import#1 | Medium | zweifach | Logik/Architektur | Automatische waitAll-Junction ändert bei bedingtem Fan-in die Semantik still (z. B. Fehler-Handler) (korr.) | `src/NodePilot.Engine/Scorch/ScorchImporter.cs:588` |
| switcher-desktop#1 | Medium | zweifach | Sicherheit | Elevierter Switcher führt den cliPath aus einer nicht auf Besitzer und ACL geprüften Konfiguration aus (korr.) | `src/NodePilot.Switcher/app.manifest:7` |
| switcher-desktop#3 | Medium | zweifach | Logik/Architektur | StopAsync bricht bei Diensten im Pending-Zustand ab, statt den Forced-Stop zu nutzen (korr.) | `src/NodePilot.Switcher/Services/WindowsServiceControlGateway.cs:103` |
| ui-app-pages-components#1 | Medium | zweifach | Codequalität | Mutationen ohne Fehlerbehandlung scheitern still, darunter „Ausführen“ und „Workflow anlegen“ | `src/nodepilot-ui/src/pages/WorkflowsPage.tsx:245` |
| ui-app-pages-components#3 | Medium | zweifach | Logik/Architektur | Spaltensortierung der Ausführungsliste wirkt nur innerhalb der aktuellen Serverseite | `src/nodepilot-ui/src/pages/ExecutionsPage.tsx:47` |
| ui-designer#1 | Medium | zweifach | Logik/Architektur | useCriticalPath schreibt projizierte Knoten über den React-Flow-Store in den Rohgraphen zurück und markiert den Workflow dirty (korr.) | `src/nodepilot-ui/src/hooks/useCriticalPath.ts:23` |
| x-duplication-reuse#3 | Medium | zweifach | Codequalität | Modal-Overlays: 25 Eigenbauten neben ModalShell, ModalShell selbst ohne Dialog-Semantik (korr.) | `src/nodepilot-ui/src/components/common/ModalShell.tsx:30` |
| x-robustness#1 | Medium | zweifach | Codequalität | Mutationen ohne onError scheitern lautlos, auch die Edit-Lock-Steuerung | `src/nodepilot-ui/src/queryClient.ts:10` |
| ai-agents#1 | Low | einfach | Wartbarkeit | AgentRuntime: 300-Zeilen-Methode mit ca. 27,6 KB fest eingebautem Prompt-Text am PromptCatalog vorbei (korr.) | `src/NodePilot.Ai/Agents/AgentRuntime.cs:34` |
| ai-agents#2 | Low | einfach | Struktur | ConfigMgr/IIS-spezifisches Diagnosewissen fest in allgemeiner Runtime und Policy (korr.) | `src/NodePilot.Ai/Agents/AgentRuntime.cs:25` |
| ai-agents#3 | Low | einfach | Codequalität | Sicherheitskritische Read-Policy in schwer prüfbaren Einzeilern (korr.) | `src/NodePilot.Engine/Agents/AgentPermissionPolicy.cs:230` |
| ai-agents#4 | Low | einfach | Sicherheit | http_request ohne AllowedHosts ist ein Abflusskanal bei Prompt-Injection (korr.) | `src/NodePilot.Engine/Agents/AgentToolHost.cs:173` |
| ai-agents#5 | Low | einfach | Codequalität | Kompatibilitäts-Fallback-Schleife in den LLM-Clients mehrfach kopiert | `src/NodePilot.Ai/OpenAiCompatibleLlmClient.cs:58` |
| ai-agents#6 | Low | einfach | Struktur | Ai-Projektwurzel ohne Unterteilung: Transport, Assistenten und Drafting gemischt | `src/NodePilot.Ai/LlmServiceCollectionExtensions.cs:21` |
| api-admin-ops-surface#1 | Low | einfach | Logik/Architektur | UsersController leitet Audit-Events vor dem Commit als committed weiter | `src/NodePilot.Api/Controllers/UsersController.cs:297` |
| api-admin-ops-surface#2 | Low | einfach | Performance | Dashboard: etwa 30 sequentielle Roundtrips pro Aufruf, 7- und 14-Tage-Scans ungecacht (korr.) | `src/NodePilot.Api/Controllers/DashboardController.cs:245` |
| api-admin-ops-surface#3 | Low | einfach | Codequalität | Audit nach einer Mutation mit dem Request-Token (DbAdmin-Write-Ergebnis, Secret-Rotation) | `src/NodePilot.Api/Controllers/DbAdminController.cs:656` |
| api-admin-ops-surface#4 | Low | einfach | Codequalität | DbAdmin.GetTables: exakter COUNT über jede Tabelle, Fehler werden still zu 0 | `src/NodePilot.Api/Controllers/DbAdminController.cs:88` |
| api-admin-ops-surface#5 | Low | einfach | Sicherheit | Secret-Spalten-Policy in DbAdmin ist opt-in; NotificationRoute.Secret wird ausgeliefert | `src/NodePilot.Api/Services/DbAdmin/DbAdminPolicy.cs:63` |
| api-admin-ops-surface#6 | Low | einfach | Performance | Support-Log-Tail liest ganze Tagessegmente synchron ohne Byte-Budget | `src/NodePilot.Api/Controllers/DiagnosticsController.cs:55` |
| api-alerting-ai-realtime#1 | Low | zweifach | Sicherheit | SSE-Fehlerpfad überspringt das Audit; Admin-SQL aus dem Wissens-Chat bleibt dann unprotokolliert | `src/NodePilot.Api/Controllers/AiKnowledgeController.cs:188` |
| api-alerting-ai-realtime#2 | Low | einfach | Struktur | ExecutionHub als statische, prozessweite Registry: globale Kopplung und serialisierte Tests (korr.) | `src/NodePilot.Api/Hubs/ExecutionHub.cs:45` |
| api-alerting-ai-realtime#3 | Low | einfach | Codequalität | CustomActivities.Create fängt jede InvalidOperationException ab und meldet sie als 409 mit Rohtext | `src/NodePilot.Api/Controllers/CustomActivitiesController.cs:82` |
| api-alerting-ai-realtime#4 | Low | einfach | Logik/Architektur | Test-Fire speichert die Ledger-Zeilen nach der externen Zustellung mit dem Request-Token | `src/NodePilot.Api/Controllers/AlertingController.cs:241` |
| api-alerting-ai-realtime#5 | Low | einfach | Wartbarkeit | AgentRecoveryService: ein gesperrtes Workspace-Verzeichnis blockiert das Aufräumen aller folgenden | `src/NodePilot.Api/Services/AgentRecoveryService.cs:62` |
| api-alerting-ai-realtime#6 | Low | einfach | Codequalität | Custom-Activity-Import überspringt fehlerhafte Einträge stillschweigend und erlaubt leere Skripte | `src/NodePilot.Api/Controllers/CustomActivitiesController.cs:216` |
| api-alerting-ai-realtime#7 | Low | einfach | Wartbarkeit | Doku-Drift: Roadmap führt erledigte Custom-Activity-Invarianten als offen; Hub-Kommentare nennen 12 h Token-Laufzeit | `docs/roadmap.md:285` |
| api-alerting-ai-realtime#8 | Low | einfach | Codequalität | AgentRun-Status als Freitext-Strings in Abfragen und Logik | `src/NodePilot.Api/Controllers/AgentsController.cs:33` |
| api-hosting-config-backup#1 | Low | einfach | Logik/Architektur | Backup-Controller mappt breite Framework-Exceptions pauschal auf 400 „Malformed backup content“ (korr.) | `src/NodePilot.Api/Controllers/BackupController.cs:169` |
| api-hosting-config-backup#2 | Low | einfach | Sicherheit | Härtungs-Flag StartProgram:DisallowShellExecute wird an drei Stellen unterschiedlich interpretiert (korr.) | `src/NodePilot.Engine/Activities/StartProgramActivity.cs:86` |
| api-hosting-config-backup#3 | Low | einfach | Performance | Backup-Preview/-Restore/-Export puffern das gesamte Artefakt mehrfach im Speicher | `src/NodePilot.Api/Controllers/BackupController.cs:86` |
| api-hosting-config-backup#4 | Low | einfach | Struktur | Program.cs nur teilweise modularisiert; Konfigurationsflags werden ad hoc und uneinheitlich gelesen (korr.) | `src/NodePilot.Api/Program.cs:248` |
| api-hosting-config-backup#5 | Low | zweifach | Codequalität | Ungültige Restore-Policy oder unbekannter Sektionsschlüssel fällt still auf Skip zurück | `src/NodePilot.Api/Controllers/BackupController.cs:182` |
| api-security-identity#1 | Low | einfach | Wartbarkeit | Test-only Fallbacks im sicherheitskritischen Produktionscode, darunter ein offener LocalLoginMode-Default (korr.) | `src/NodePilot.Api/Controllers/AuthController.cs:105` |
| api-security-identity#3 | Low | einfach | Logik/Architektur | Vertrag für Sitzungsinvalidierung verteilt; AuthSession.AuthorizationVersion-Prüfung wirkungslos (korr.) | `src/NodePilot.Api/Security/UserSessionInvalidation.cs:10` |
| api-security-identity#4 | Low | einfach | Codequalität | Autorisierungs-Primitive mehrfach dupliziert (Ahnenkette, Staleness, Identitätssuche) (korr.) | `src/NodePilot.Api/Security/ResourceAuthorizationService.cs:243` |
| api-security-identity#5 | Low | einfach | Logik/Architektur | AuditWriter lässt fehlgeschlagene Einträge im geteilten DbContext als Added zurück (korr.) | `src/NodePilot.Api/Audit/AuditWriter.cs:57` |
| api-security-identity#6 | Low | einfach | Codequalität | ExternalUserMapper: irreführender Bootstrap-Kommentar, Legacy-Upgrade-Zweig und unbegrenzte statische Gate-Map (korr.) | `src/NodePilot.Api/Security/Ldap/ExternalUserMapper.cs:316` |
| api-security-identity#7 | Low | einfach | Codequalität | Veraltete Lebensdauer-Kommentare, doppelte Konstanten und tote Prüfzweige | `src/NodePilot.Api/Security/AuthSessionIssuer.cs:345` |
| api-workflow-surface#1 | Low | einfach | Logik/Architektur | Aktivierungs-Validatoren dreifach von Hand gepflegt, Enable lässt AgentPublishValidation aus | `src/NodePilot.Api/Controllers/WorkflowEditingController.cs:466` |
| api-workflow-surface#2 | Low | einfach | Performance | Interaktive Executions-Liste ohne DatabaseCommandBudget trotz dokumentierter Retry-Verstärkung (korr.) | `src/NodePilot.Api/Controllers/WorkflowsController.cs:237` |
| api-workflow-surface#3 | Low | einfach | Codequalität | Unique-Verletzungen werden in drei Pfaden unterschiedlich erkannt, Publish/Rollback per locale-abhängiger Textsuche | `src/NodePilot.Api/Controllers/WorkflowEditingController.cs:533` |
| api-workflow-surface#4 | Low | einfach | Codequalität | Cancel/Cancel-all schreiben Lifecycle-Zustand mit dem Request-Token über zwei Statements | `src/NodePilot.Api/Controllers/ExecutionsController.cs:531` |
| api-workflow-surface#5 | Low | einfach | Sicherheit | Retry prüft den Execution-Status vor dem RBAC-Gate | `src/NodePilot.Api/Controllers/ExecutionsController.cs:617` |
| build-ci-scripts#2 | Low | zweifach | Sicherheit | ci.yml ohne Least-Privilege-Token-Rechte, und der Checkout persistiert das Token für npm-Install-Skripte (korr.) | `.github/workflows/ci.yml:354` |
| build-ci-scripts#3 | Low | einfach | Testlücke | Vitest wiederholt fehlgeschlagene Tests in CI still, ohne Reliability-Erfassung | `src/nodepilot-ui/vitest.config.ts:34` |
| build-ci-scripts#4 | Low | einfach | Codequalität | Electron-Desktop-Shell ohne Linter in CI (korr.) | `src/nodepilot-desktop/package.json:21` |
| build-ci-scripts#5 | Low | einfach | Wartbarkeit | Veraltete Begründungskommentare und Zahlen in Paket- und Sample-Doku | `Directory.Packages.props:38` |
| build-ci-scripts#6 | Low | einfach | Codequalität | Compiler-Warnungen sind kein Gate, und es gibt kein Analyzer-Niveau | `Directory.Build.props:17` |
| clients-cli-mcp#1 | Low | zweifach | Logik/Architektur | MCP-Server übernimmt ein späteres `np auth login` erst nach einem Neustart (korr.) | `src/NodePilot.Mcp/Api/ApiClientFactory.cs:32` |
| clients-cli-mcp#2 | Low | einfach | Codequalität | `np exec list` paginiert per Offset über eine laufende Liste, ohne Dedupe; der Hilfetext behauptet eine falsche Obergrenze (korr.) | `src/NodePilot.Cli/Api/NodePilotApiClient.cs:304` |
| clients-cli-mcp#3 | Low | einfach | Wartbarkeit | Doppelte Typed-Clients driften im Kleinen; der Coverage-Guard prüft nur die Pfadform (korr.) | `src/NodePilot.Mcp/Api/NodePilotApiClient.cs:13` |
| clients-cli-mcp#4 | Low | einfach | Codequalität | CLI bildet Timeouts nicht auf einen eigenen Exit-Code ab | `src/NodePilot.Cli/Commands/BaseCommand.cs:74` |
| clients-cli-mcp#5 | Low | einfach | Testlücke | get_safety_status pflegt die Liste destruktiver Tools von Hand, ohne exakten Parity-Test | `src/NodePilot.Mcp/Tools/DiscoveryTools.cs:110` |
| core-data#1 | Low | einfach | Struktur | Core ist kein reiner Shared Kernel: Client-Infrastruktur und Api-only-Code liegen im Kernel (korr.) | `src/NodePilot.Core/NodePilot.Core.csproj:4` |
| core-data#2 | Low | zweifach | Testlücke | SQL-Server-Zweig des HA-kritischen Outbox-Claims wird in CI nur textuell geprüft (korr.) | `src/NodePilot.Data/ExecutionDispatchOutboxClaimer.cs:63` |
| core-data#3 | Low | einfach | Codequalität | Interfaces mit genau einer Implementierung und ohne Test-Naht | `src/NodePilot.Api/Program.cs:199` |
| core-data#4 | Low | einfach | Codequalität | CredentialStore trägt einen synchronen Testpfad in der Produktionsklasse (korr.) | `src/NodePilot.Data/CredentialStore.cs:189` |
| core-data#5 | Low | einfach | Performance | NotificationRuleStore.DeleteAsync materialisiert ganze Ledger-Tabellen vor dem Löschen | `src/NodePilot.Data/NotificationRuleStore.cs:228` |
| core-data#6 | Low | einfach | Wartbarkeit | Mechanisch zerbrochene Kommentare im heikelsten Concurrency-Code | `src/NodePilot.Data/Availability/DatabaseAvailabilityTracker.cs:117` |
| deploy-installers#2 | Low | zweifach | Sicherheit | Setup-Transcript speichert den Legacy-Trigger-Key (und ggf. das Setup-Token) dauerhaft im Klartext (korr.) | `deploy/Invoke-NodePilotSetup.ps1:681` |
| deploy-installers#3 | Low | einfach | Testlücke | Kerninvarianten des Installers werden überwiegend per Regex auf Quelltext getestet, was Kommentare festschreibt (korr.) | `deploy/Test-DeploymentTemplates.ps1:154` |
| deploy-installers#4 | Low | einfach | Wartbarkeit | Install und Update duplizieren Kernhelfer, schon mit Drift, und Bibliotheken hängen an Logging-Funktionen der Aufrufer (korr.) | `deploy/Install-NodePilot.ps1:577` |
| deploy-installers#5 | Low | einfach | Logik/Architektur | Update erlaubt stillen Downgrade auf ein älteres signiertes Artefakt | `deploy/Update-NodePilot.ps1:430` |
| deploy-installers#6 | Low | einfach | Wartbarkeit | Desktop-Vollupdate: Rollback-Kopien wachsen unbegrenzt, Payload wird ungeprüft ausgeführt | `deploy/desktop/Update-Desktop.ps1:123` |
| deploy-installers#7 | Low | einfach | Codequalität | Ausgelieferte Installer-Bibliothek enthält Nicht-ASCII-Text; der ASCII-Guard deckt nur die .iss ab | `deploy/ServiceControl.ps1:122` |
| deploy-installers#8 | Low | einfach | Sicherheit | Klartext der Seed-Passphrase bleibt im nicht verwalteten Speicher | `deploy/Install-NodePilot.ps1:1385` |
| docs-ui-website#1 | Low | einfach | Performance | Doku-Bundle lädt den kompletten zweisprachigen Markdown-Korpus eager (korr.) | `src/nodepilot-docs-ui/src/lib/content.ts:7` |
| docs-ui-website#2 | Low | einfach | Wartbarkeit | Website-Controller und renderSiteContent sind monolithisch und nur per E2E prüfbar (korr.) | `src/nodepilot-docs-ui/src/site/seo.ts:29` |
| docs-ui-website#3 | Low | einfach | Codequalität | Sprachtexte als Ternaries im Code umgehen Dictionaries und Paritäts-Guards | `src/nodepilot-docs-ui/src/site/seo.ts:109` |
| docs-ui-website#4 | Low | einfach | Sicherheit | Site-CSP erlaubt 'unsafe-eval' und blob:-Skripte für die ganze Document-Root | `src/nodepilot-docs-ui/src/site/public/.htaccess:28` |
| docs-ui-website#5 | Low | einfach | Wartbarkeit | Publish-Site: keine Bereinigung entfernter Dateien, Retry-Block dreifach kopiert, Abweichung vom Skriptkopf-Standard | `deploy/Publish-Site.ps1:324` |
| docs-ui-website#6 | Low | einfach | Performance | Website rendert das komplette Prerendering im Client erneut und bündelt dafür alle Seiteninhalte | `src/nodepilot-docs-ui/src/site/main.ts:799` |
| docs-ui-website#7 | Low | einfach | Logik/Architektur | Doku-Head-Vertrag doppelt implementiert (Prerender per Regex vs. Runtime per DOM) | `src/nodepilot-docs-ui/src/lib/docs-head.ts:5` |
| docs-ui-website#8 | Low | einfach | Performance | Doku-Prerender wird beim Booten verworfen statt hydriert, mit abweichendem Layout | `src/nodepilot-docs-ui/src/main.tsx:9` |
| engine-activities-powershell-remote#2 | Low | einfach | Performance | WinRM-Abbruch ruft ps.Stop() synchron im Token-Callback auf und hat keine Grace-Frist (korr.) | `src/NodePilot.Remote/WinRmSession.cs:98` |
| engine-activities-powershell-remote#3 | Low | einfach | Logik/Architektur | Gepoolte WinRM-Runspaces geben Sitzungszustand an fremde Schritte und Workflows weiter | `src/NodePilot.Remote/WinRmSessionPool.cs:61` |
| engine-activities-powershell-remote#4 | Low | einfach | Wartbarkeit | Ziel- und Credential-Auflösung samt Localhost-Bypass ist fünffach implementiert und driftet bereits (korr.) | `src/NodePilot.Engine/Activities/BaseActivity.cs:74` |
| engine-activities-powershell-remote#5 | Low | einfach | Performance | Synchrone DNS-Auflösung ohne Timeout in asynchronen Ausführungspfaden | `src/NodePilot.Engine/Activities/BaseActivity.cs:173` |
| engine-activities-powershell-remote#6 | Low | einfach | Codequalität | Uneinheitliche Config-Verarbeitung: vier Timeout-Parser, stiller Engine-Fallback, doppelte Konstanten (korr.) | `src/NodePilot.Engine/Activities/ForEachActivity.cs:158` |
| engine-execution-core#2 | Low | zweifach | Logik/Architektur | Kantenbedingungen sehen die ungefilterte Ergebnis-Map und hängen damit vom Timing ab (korr.) | `src/NodePilot.Engine/Execution/WorkflowScheduler.cs:430` |
| engine-execution-core#5 | Low | einfach | Codequalität | Vorphase von ExecuteAsync liegt außerhalb des einzigen Cleanup-Pfads | `src/NodePilot.Engine/WorkflowEngine.cs:499` |
| engine-execution-core#6 | Low | einfach | Performance | Retry-Backoff ist bei maxDelayMs=0 ungedeckelt und hält währenddessen den globalen Step-Slot (korr.) | `src/NodePilot.Engine/Execution/RetryPolicy.cs:48` |
| engine-execution-core#7 | Low | einfach | Wartbarkeit | StepRunner.ExecuteAsync wiederholt die Abschlusssequenz dreifach | `src/NodePilot.Engine/Execution/StepRunner.cs:345` |
| gap-engine-remote-activities-guards#3 | Low | zweifach | Sicherheit | ServiceManagement übergibt den als nicht vertrauenswürdig eingestuften Dienstnamen als natives Argument an sc.exe (korr.) | `src/NodePilot.Engine/Activities/ServiceManagementActivity.cs:170` |
| gap-engine-remote-activities-guards#4 | Low | einfach | Wartbarkeit | PowerShell-Guard-Logik liegt mehrfach dupliziert vor; gleichnamige Funktion wird im selben Skript zweimal definiert | `src/NodePilot.Engine/Activities/ZipOperationActivity.cs:25` |
| gap-engine-remote-activities-guards#5 | Low | einfach | Performance | Ordnerkopie walkt pro Datei mehrfach den vollständigen Pfad ab Volume-Root | `src/NodePilot.Engine/Activities/FolderOperationActivity.cs:124` |
| gap-engine-remote-activities-guards#6 | Low | einfach | Codequalität | ForEach lädt die Parent-Execution doppelt und überspringt bei leerer Liste die Prüfung des Child-Workflows | `src/NodePilot.Engine/Activities/ForEachActivity.cs:74` |
| gap-external-identity#3 | Low | einfach | Performance | LDAP-Login: Endpoint-Failover-Schleife vervielfacht den All-DC-Lookup und enthält toten Optional-Service-Bind-Code (korr.) | `src/NodePilot.Api/Security/Ldap/SystemLdapConnectionAdapter.cs:418` |
| gap-external-identity#4 | Low | einfach | Wartbarkeit | Fünffach handkopiertes Sicherheits-Mutationsgerüst mit divergierenden Details (korr.) | `src/NodePilot.Api/Security/Scim/ScimProvisioningService.cs:808` |
| gap-external-identity#5 | Low | einfach | Struktur | ScimProvisioningService vermischt Protokoll-Parsing, Domänenregeln und Transaktionssteuerung (korr.) | `src/NodePilot.Api/Security/Scim/ScimProvisioningService.cs:30` |
| gap-external-identity#6 | Low | einfach | Performance | SCIM-Gruppenliste: N+1-Abfragen mit vollständigen Mitgliederlisten | `src/NodePilot.Api/Security/Scim/ScimProvisioningService.cs:404` |
| gap-external-identity#7 | Low | einfach | Codequalität | Login-Throttle nutzt die IdempotencyKeys-Tabelle mit Magic-Scope Guid.Empty (korr.) | `src/NodePilot.Api/Security/ExternalLoginThrottle.cs:21` |
| gap-llm-trust-boundary#1 | Low | zweifach | Sicherheit | Globaler Profil-API-Key wird an per-Node überschriebene baseUrl gesendet (korr.) | `src/NodePilot.Ai/ILlmClientFactory.cs:63` |
| gap-llm-trust-boundary#3 | Low | einfach | Performance | Uneinheitliche Eingabe-Obergrenzen der Generierungs-Endpoints | `src/NodePilot.Api/Controllers/AiController.cs:166` |
| gap-llm-trust-boundary#4 | Low | einfach | Sicherheit | Secret-Keys mit Nicht-String-Werten werden nicht maskiert | `src/NodePilot.Core/WorkflowDefinitions/WorkflowSecretRedactor.cs:111` |
| gap-llm-trust-boundary#5 | Low | einfach | Sicherheit | IPv6-Metadata-Endpunkte weder im Literal- noch im Connect-Guard abgedeckt | `src/NodePilot.Ai/LlmEndpointGuard.cs:133` |
| gap-llm-trust-boundary#6 | Low | einfach | Codequalität | Duplizierte JSON-Retry- und Token-Akkumulationsschleife | `src/NodePilot.Ai/WorkflowGenerationService.cs:122` |
| gap-ui-admin-ops-pages#1 | Low | zweifach | Codequalität | OIDC-Rollen-Mapping: Eingabefeld verliert bei jedem Tastendruck den Fokus | `src/nodepilot-ui/src/components/admin-settings/AuthenticationSection.tsx:709` |
| gap-ui-admin-ops-pages#2 | Low | zweifach | Logik/Architektur | Aufgeklappte Step-Details auf der Executions-Seite werden nie aktualisiert (korr.) | `src/nodepilot-ui/src/pages/ExecutionsPage.tsx:516` |
| gap-ui-admin-ops-pages#3 | Low | zweifach | Wartbarkeit | Alerting-Routen-Editor doppelt implementiert und auseinandergelaufen: Secret im System-Editor nicht löschbar (korr.) | `src/nodepilot-ui/src/components/alerting/AlertingRuleEditor.tsx:346` |
| gap-ui-admin-ops-pages#4 | Low | einfach | Codequalität | Settings-Formular-Widgets mehrfach kopiert statt aus SectionFormHelpers importiert (korr.) | `src/nodepilot-ui/src/components/admin-settings/SectionFormHelpers.tsx:305` |
| gap-ui-admin-ops-pages#5 | Low | einfach | Codequalität | Formular-Labels sind überwiegend nicht mit ihren Eingabefeldern verknüpft | `src/nodepilot-ui/src/components/admin-settings/SectionFormHelpers.tsx:294` |
| gap-ui-admin-ops-pages#6 | Low | einfach | Wartbarkeit | Hartcodierte helle Tailwind-Farbliterale trotz Token-Regel | `src/nodepilot-ui/src/components/admin-settings/AuthenticationSection.tsx:519` |
| gap-ui-admin-ops-pages#7 | Low | einfach | Codequalität | Uneinheitliche Mutationsmuster: Credential-Speichern ohne Pending-Sperre, Inline-api-Aufrufe in Dialogen (korr.) | `src/nodepilot-ui/src/pages/SettingsPage.tsx:318` |
| gap-ui-admin-ops-pages#8 | Low | einfach | Anti-Pattern | Zwei Chat-Implementierungen mit kopiertem Thread-Gerüst und unterschiedlicher Stream-Ownership | `src/nodepilot-ui/src/components/ai/AiWorkflowChatPanel.tsx:94` |
| gap-ui-designer-subcomponents#1 | Low | zweifach | Logik/Architektur | ConditionBuilder bildet gültige Bedingungsformen nicht ab (not(group), source 'manual', Verweise auf nicht mehr vorgelagerte Steps) (korr.) | `src/nodepilot-ui/src/components/designer/ConditionBuilder.tsx:344` |
| gap-ui-designer-subcomponents#3 | Low | einfach | Anti-Pattern | Konfigurationseditoren schreiben Defaults per useEffect in den Knoten, auch im Read-only-Modus (korr.) | `src/nodepilot-ui/src/components/designer/properties/activities/ScheduledTaskConfig.tsx:43` |
| gap-ui-designer-subcomponents#4 | Low | einfach | Logik/Architektur | Verbindungstest zeigt nach Knoten- oder Maschinenwechsel das alte Ergebnis | `src/nodepilot-ui/src/components/designer/PropertiesPanel.tsx:660` |
| gap-ui-designer-subcomponents#5 | Low | einfach | Codequalität | Fehler bei Cancel und Resume werden verschluckt bzw. nicht angezeigt (korr.) | `src/nodepilot-ui/src/components/designer/header/RunControls.tsx:32` |
| gap-ui-designer-subcomponents#6 | Low | einfach | Wartbarkeit | Doppelte Endpunkt-Aufrufe ohne gemeinsame API-Funktion (Step-Test, Global-Variablen) (korr.) | `src/nodepilot-ui/src/components/designer/properties/StepTestPanel.tsx:135` |
| gap-ui-designer-subcomponents#7 | Low | einfach | Codequalität | Formular- und Dialogsemantik: Labels nicht verknüpft, Designer-Modals ohne gemeinsame Hülle (korr.) | `src/nodepilot-ui/src/components/designer/properties/shared.tsx:187` |
| gap-ui-designer-subcomponents#8 | Low | einfach | Wartbarkeit | activity-config-reference.json kennt die REST-Proxy-Schlüssel nicht, die Editor und Engine verwenden | `src/NodePilot.Core/Activities/Embedded/activity-config-reference.json:303` |
| scheduler#3 | Low | zweifach | Anti-Pattern | Konfigurationslesen außerhalb der inneren try-Grenze kann den Host stoppen (korr.) | `src/NodePilot.Scheduler/WorkflowStatsRefresher.cs:89` |
| scheduler#5 | Low | einfach | Performance | Execution-Collector verarbeitet höchstens 200 terminale Executions pro 30-s-Pass | `src/NodePilot.Scheduler/Notifications/ExecutionEventSupport.cs:27` |
| scheduler#6 | Low | einfach | Codequalität | SIEM-Weiterleitung von TRIGGER_FIRE_SUPPRESSED vor dem Transaktions-Commit | `src/NodePilot.Scheduler/TriggerOrchestrator.cs:880` |
| scheduler#7 | Low | einfach | Wartbarkeit | Test-Seams und Doppelpfad im produktiven Trigger-Vertrag | `src/NodePilot.Scheduler/ITriggerSource.cs:61` |
| scorch-import#2 | Low | einfach | Codequalität | Abgelehnte generierte Definition: Runbook fällt weg, der Validierungsgrund wird verworfen (korr.) | `src/NodePilot.Api/Controllers/WorkflowImportExportController.cs:453` |
| scorch-import#3 | Low | einfach | Testlücke | XML-Härtung (M-14), Encoding-Erkennung und Entity-Abwehr sind durch keinen Test abgesichert (korr.) | `src/NodePilot.Engine/Scorch/ScorchImporter.cs:91` |
| scorch-import#4 | Low | einfach | Performance | Item-Cap greift erst nach der kompletten Übersetzung, der Parse ist synchron und nicht abbrechbar | `src/NodePilot.Api/Controllers/WorkflowImportExportController.cs:358` |
| scorch-import#5 | Low | einfach | Wartbarkeit | Warnungen sind ein flaches String-Protokoll ohne Schwere, Code und Ort (korr.) | `src/NodePilot.Engine/Scorch/ScorchImporter.cs:1591` |
| scorch-import#6 | Low | einfach | Struktur | ScorchImporter bündelt fünf Verantwortlichkeiten und konvertiert jede Definition viermal | `src/NodePilot.Engine/Scorch/ScorchImporter.cs:498` |
| switcher-desktop#2 | Low | zweifach | Sicherheit | Electron-Fuses werden beim Paketieren nicht gehärtet (korr.) | `src/nodepilot-desktop/scripts/package.mjs:25` |
| switcher-desktop#4 | Low | einfach | Testlücke | Sicherheits- und Prozess-Runtime ohne Tests: P/Invoke-Gateway und main.ts (korr.) | `src/NodePilot.Switcher/Services/SwitchCoordinator.cs:22` |
| switcher-desktop#5 | Low | einfach | Codequalität | "Restart backend" meldet einen fehlgeschlagenen Restart-Service nicht | `src/nodepilot-desktop/src/backendRestart.ts:14` |
| switcher-desktop#6 | Low | einfach | Wartbarkeit | Switcher-Dateilog fällt still aus, obwohl er die einzige dauerhafte Spur des Umschaltens ist | `src/NodePilot.Switcher/Services/ActivityLogger.cs:37` |
| tests-api-clients#1 | Low | zweifach | Testlücke | Admin-only-Rollengates der administrativen Controller sind durch keinen Test gepinnt | `tests/NodePilot.Api.Tests/Hosting/RoleMatrixSmokeTests.cs:73` |
| tests-api-clients#2 | Low | zweifach | Testlücke | Audit-Guard übersieht still 8 von 111 mutierenden Aktionen (korr.) | `tests/NodePilot.Api.Tests/Architecture/MutatingActionAuditCoverageTests.cs:12` |
| tests-api-clients#3 | Low | zweifach | Testlücke | Endpoint- und DTO-Paritäts-Guards sehen die gesamte Agents-API nicht (korr.) | `tests/NodePilot.Cli.Tests/Api/EndpointClientCoverageTests.cs:257` |
| tests-api-clients#4 | Low | einfach | Testlücke | RBAC-IL-Guard hat eine veraltete Ausnahme und keine Selbstprüfung | `tests/NodePilot.Api.Tests/Rbac/RbacControllerCoverageGuardTests.cs:43` |
| tests-api-clients#5 | Low | einfach | Anti-Pattern | Feste Task.Delay-Fenster für Negativ-Assertions und unserialisierte Env-Mutation (korr.) | `tests/NodePilot.Api.Tests/Hosting/ClusterFailoverRecoveryHostTests.cs:125` |
| tests-api-clients#6 | Low | einfach | Wartbarkeit | Wiederholte Controller-Konstruktion in großen Testdateien | `tests/NodePilot.Api.Tests/Controllers/AuthControllerTests.cs:83` |
| tests-engine-data-ai#1 | Low | einfach | Testlücke | waitAny-Engine-Test ist vakuös: ohne Trigger-Knoten läuft kein Step, die Stoppuhr besteht trivial | `tests/NodePilot.Engine.Tests/WorkflowEngineTests.cs:1072` |
| tests-engine-data-ai#2 | Low | einfach | Testlücke | Engine-Harness teilt eine SQLite-Verbindung zwischen parallelen Step-Scopes und erzwingt Sleeps gegen Races (korr.) | `tests/NodePilot.Engine.Tests/Helpers/TestDbContext.cs:42` |
| tests-engine-data-ai#3 | Low | einfach | Testlücke | Zeitbasierte Negativbeweise und lange feste Wartezeiten in Barrier- und Watcher-Tests (korr.) | `tests/NodePilot.Engine.Tests/WorkflowEngineUnpersistedFailureTests.cs:483` |
| tests-engine-data-ai#4 | Low | einfach | Testlücke | CredentialStore-Test behauptet "SwallowsAuditFailure", prüft das Schlucken aber nicht | `tests/NodePilot.Data.Tests/CredentialStoreCoverageTests.cs:125` |
| tests-engine-data-ai#5 | Low | einfach | Codequalität | Agent-Testcluster weicht von Namens- und Assertion-Konvention ab und konzentriert sich in einer Riesendatei | `tests/NodePilot.Ai.Tests/AgentRuntimeTests.cs:13` |
| tests-engine-data-ai#6 | Low | einfach | Wartbarkeit | Zugehörigkeit zur SerialEngineTests-Collection ist reine Konvention ohne Guard | `tests/NodePilot.Engine.Tests/SerialEngineTestCollection.cs:12` |
| tests-engine-data-ai#7 | Low | einfach | Testlücke | SQL-Server-Provider-Tests laufen in CI nie (dokumentiert) (korr.) | `.github/workflows/ci.yml:336` |
| tests-frontend-e2e#1 | Low | einfach | Testlücke | MSW lässt ungemockte Requests in fast allen Suites still durch, auch im zentralen Designer-Test (korr.) | `src/nodepilot-ui/src/__tests__/pages/WorkflowEditorPage.test.tsx:183` |
| tests-frontend-e2e#2 | Low | einfach | Wartbarkeit | E2E-Mocks sind untypisiert und werden von keinem Typecheck erfasst (korr.) | `src/nodepilot-ui/e2e/fixtures/mockApi.ts:55` |
| tests-frontend-e2e#3 | Low | einfach | Anti-Pattern | Feste Wartezeiten als Synchronisation, Flakes werden in CI durch Retries maskiert (korr.) | `src/nodepilot-ui/e2e/operations.spec.ts:320` |
| tests-frontend-e2e#4 | Low | einfach | Testlücke | Live-Ausführungspfad (SignalR) hat keine Browser-Abdeckung (korr.) | `src/nodepilot-ui/e2e/fixtures/mockApi.ts:207` |
| tests-frontend-e2e#5 | Low | einfach | Codequalität | Testgerüst pro Datei kopiert statt geteilt | `src/nodepilot-ui/src/__tests__/pages/WorkflowsPage.test.tsx:16` |
| tests-frontend-e2e#6 | Low | einfach | Testlücke | Assertion-freier Screenshot-Helfer läuft in jedem CI-E2E-Lauf mit | `src/nodepilot-ui/e2e/_all-screens.spec.ts:97` |
| tests-frontend-e2e#7 | Low | einfach | Codequalität | Zentrale Save-/Publish-Tests prüfen nur, dass ein Request kam (korr.) | `src/nodepilot-ui/src/__tests__/pages/WorkflowEditorPage.test.tsx:604` |
| tests-frontend-e2e#8 | Low | einfach | Testlücke | webPreferences des Desktop-Hauptfensters ohne Test | `src/nodepilot-desktop/src/main.ts:236` |
| ui-app-pages-components#2 | Low | zweifach | Logik/Architektur | Render-Fehler in Routen umgehen die lokalisierte App-ErrorBoundary (korr.) | `src/nodepilot-ui/src/App.tsx:99` |
| ui-app-pages-components#4 | Low | zweifach | Wartbarkeit | Gemeinsame Dialog-Primitive ohne Dialog-Semantik, viele handgebaute Overlays (korr.) | `src/nodepilot-ui/src/components/common/ModalShell.tsx:30` |
| ui-app-pages-components#5 | Low | einfach | Struktur | WorkflowsPage dupliziert das Zeilen-Rendering für Mobil und Desktop und bündelt zu viele Zuständigkeiten (korr.) | `src/nodepilot-ui/src/pages/WorkflowsPage.tsx:885` |
| ui-app-pages-components#6 | Low | einfach | Anti-Pattern | Token-Regel breit verletzt, Validierungsbanner trotz vorhandenem Helper kopiert (korr.) | `src/nodepilot-ui/src/components/admin-settings/AuthenticationSection.tsx:519` |
| ui-app-pages-components#7 | Low | einfach | Codequalität | Dashboard bleibt bei fehlgeschlagenem Erstaufruf im Ladezustand hängen | `src/nodepilot-ui/src/pages/DashboardPage.tsx:184` |
| ui-app-pages-components#8 | Low | einfach | Struktur | Tabellenkopf-Muster vielfach kopiert, Seiten importieren Primitive aus dem Designer-Baum (korr.) | `src/nodepilot-ui/src/pages/MachinesPage.tsx:433` |
| ui-designer#2 | Low | zweifach | Anti-Pattern | Laufzeit-Annotationen werden als State im autoritativen Rohgraphen gespeichert (korr.) | `src/nodepilot-ui/src/hooks/useNodeAnnotations.ts:76` |
| ui-designer#3 | Low | zweifach | Struktur | WorkflowEditorPage überschreitet den dokumentierten Wiederaufnahme-Trigger, Prop-Drilling in 90-Prop-Komponenten (korr.) | `docs/roadmap.md:365` |
| ui-designer#4 | Low | zweifach | Wartbarkeit | ActivityNode: vier Render-Varianten mit mehrfach duplizierter Overlay-Logik in einer ~820-Zeilen-Funktion (korr.) | `src/nodepilot-ui/src/components/designer/nodes/ActivityNode.tsx:345` |
| ui-designer#5 | Low | einfach | Codequalität | History-/Dirty-Protokoll wird an über 20 Stellen manuell wiederholt, ein Handler hat Seiteneffekte im State-Updater (korr.) | `src/nodepilot-ui/src/pages/WorkflowEditorPage.tsx:588` |
| ui-designer#6 | Low | einfach | Codequalität | Fehlschläge werden still verschluckt: Cancel-Shortcut und PNG-Export | `src/nodepilot-ui/src/pages/WorkflowEditorPage.tsx:1211` |
| ui-designer#7 | Low | einfach | Codequalität | Konventionsdrift: hartkodierter deutscher Hinweis und Tailwind-Farbliterale | `src/nodepilot-ui/src/pages/WorkflowEditorPage.tsx:513` |
| ui-designer#8 | Low | einfach | Codequalität | Sortier-Komparator mit nur einem Argument trägt die Invariante 'Gruppen vor Kindern' | `src/nodepilot-ui/src/pages/WorkflowEditorPage.tsx:369` |
| ui-state-data-layer#1 | Low | einfach | Logik/Architektur | Operations-/Dashboard-Feed tritt nach DB-Ausfall bei stehendem WebSocket nicht erneut bei (korr.) | `src/nodepilot-ui/src/hooks/useLiveOpsFeed.ts:82` |
| ui-state-data-layer#2 | Low | einfach | Codequalität | Globale Retry-Policy wiederholt deterministische 4xx-Fehler (korr.) | `src/nodepilot-ui/src/queryClient.ts:13` |
| ui-state-data-layer#3 | Low | einfach | Performance | Keine AbortSignal-Weitergabe für Queries: React Query kann Requests nicht abbrechen (korr.) | `src/nodepilot-ui/src/api/client.ts:202` |
| ui-state-data-layer#4 | Low | einfach | Wartbarkeit | Frontend-DTO-Synchronität nur für zwei Interfaces abgesichert; immer gelieferte Felder als optional mit Fail-open-Fallback typisiert (korr.) | `tests/NodePilot.Cli.Tests/Api/ApiDtoParityTests.cs:110` |
| ui-state-data-layer#5 | Low | einfach | Codequalität | Parallel-Transport in adminSettings.ts dupliziert authedFetch mit abweichendem Verhalten (korr.) | `src/nodepilot-ui/src/api/adminSettings.ts:27` |
| ui-state-data-layer#6 | Low | einfach | Wartbarkeit | Toter Refresh-Pfad im authStore mit veraltetem Lebensdauer-Hinweis | `src/nodepilot-ui/src/stores/authStore.ts:206` |
| ui-state-data-layer#7 | Low | zweifach | Anti-Pattern | Produktiver Transport trägt Kompatibilitätszweige für Testmocks und Casing (korr.) | `src/nodepilot-ui/src/api/paging.ts:16` |
| x-architecture-dependencies#1 | Low | einfach | Wartbarkeit | Frontend-Datenzugriff: src/api ist nur HTTP-Fassade, Endpunkte und Query-Keys sind über 51 Dateien verstreut (korr.) | `src/nodepilot-ui/src/components/admin-settings/AgentsSection.tsx:116` |
| x-architecture-dependencies#2 | Low | einfach | Anti-Pattern | Prozessweiter statischer Zustand über WorkflowEngine hinaus, mit dokumentierten Test-Flakes (korr.) | `src/NodePilot.Api/Hubs/ExecutionHub.cs:45` |
| x-architecture-dependencies#3 | Low | einfach | Struktur | Architektur-Guard sieht nur ProjectReferences: Kopplung innerhalb von Api auf den Namespace Controllers bleibt ungeprüft (korr.) | `tests/NodePilot.Api.Tests/Architecture/DependencyDirectionTests.cs:36` |
| x-duplication-reuse#1 | Low | einfach | Testlücke | DTO-Parity-Guard übersieht API-DTOs außerhalb von Api/Dtos und umbenannte Client-DTOs (korr.) | `tests/NodePilot.Cli.Tests/Api/ApiDtoParityTests.cs:45` |
| x-duplication-reuse#2 | Low | einfach | Wartbarkeit | Endpoint-Wrapper von CLI und MCP doppelt gepflegt: 76 gleichnamige Methoden | `src/NodePilot.Cli/Api/NodePilotApiClient.cs:163` |
| x-duplication-reuse#4 | Low | einfach | Codequalität | Sechs Dauer-Formatierer mit abweichender Ausgabe, exportiertes formatMs zweimal kopiert | `src/nodepilot-ui/src/components/designer/execution/ExecutionPanelParts.tsx:59` |
| x-duplication-reuse#5 | Low | einfach | Sicherheit | pg_dump/pg_restore im Desktop-Update umgehen den sicheren psql-Helfer und setzen PGPASSWORD prozessweit | `deploy/desktop/Update-Desktop.ps1:82` |
| x-duplication-reuse#6 | Low | einfach | Codequalität | Sechs Loopback-Prädikate im Backend, zwei davon identische Literal-Prüfungen | `src/NodePilot.Ai/LlmEndpointGuard.cs:94` |
| x-duplication-reuse#7 | Low | einfach | Testlücke | Ungeschützter Spiegel: Operations-Fensterliste an vier Stellen, dazu ein veralteter Spiegel-Kommentar (korr.) | `src/NodePilot.Api/Controllers/OperationsController.cs:57` |
| x-performance#1 | Low | zweifach | Logik/Architektur | Executions-Historie aktualisiert sich nicht live, obwohl der Kommentar SignalR-Invalidierung zusagt | `src/nodepilot-ui/src/pages/ExecutionsPage.tsx:111` |
| x-performance#2 | Low | einfach | Performance | Fünf ungecachte 7/14-Tage-GROUP-BY-Scans pro Dashboard-Request trotz vorhandener Stunden-Rollups | `src/NodePilot.Api/Controllers/DashboardController.cs:244` |
| x-performance#3 | Low | einfach | Performance | Executions-Suche: nicht sargierbares OR-Prädikat, doppelt ausgewertet (Count und Seite) | `src/NodePilot.Api/Controllers/ExecutionsController.cs:149` |
| x-performance#5 | Low | einfach | Performance | Regex-Cache der Edge-Conditions füllt sich einmal und verdrängt nie | `src/NodePilot.Engine/Conditions/ConditionEvaluator.cs:309` |
| x-readability-complexity#1 | Low | einfach | Wartbarkeit | WorkflowEditorPage: dokumentierter Wiederaufnahme-Auslöser überschritten, eine Komponente mit ca. 1890 Zeilen (korr.) | `src/nodepilot-ui/src/pages/WorkflowEditorPage.tsx:172` |
| x-readability-complexity#2 | Low | einfach | Testlücke | Installer-Prüfung koppelt per Regex an Quelltext: Churn-Hotspot Nr. 1 und Hemmschuh für den geplanten Installer-Schnitt (korr.) | `deploy/Test-DeploymentTemplates.ps1:134` |
| x-readability-complexity#3 | Low | einfach | Logik/Architektur | Transaktionale Orchestrierung in sehr langen Controller- und Middleware-Methoden (korr.) | `src/NodePilot.Api/Controllers/WorkflowImportExportController.cs:327` |
| x-readability-complexity#4 | Low | einfach | Codequalität | Kommentarregel aus CLAUDE.md wird auch in neuem Code verletzt | `src/NodePilot.Engine/Execution/VariableResolver.cs:126` |
| x-readability-complexity#5 | Low | einfach | Wartbarkeit | SettingsSections: dreifache Handabbildung je Sektion mit duplizierten Default-Literalen | `src/NodePilot.Api/Configuration/SettingsSections.cs:746` |
| x-readability-complexity#6 | Low | einfach | Struktur | Dateiname verdeckt eigenständige Typen | `src/NodePilot.Api/Diagnostics/RedactedLogFileResult.cs:12` |
| x-robustness#2 | Low | zweifach | Logik/Architektur | WinRM-Pool: scheinbares Fire-and-forget entsorgt synchron und schluckt Fehler ohne Log (korr.) | `src/NodePilot.Remote/WinRmSessionPool.cs:146` |
| x-robustness#3 | Low | einfach | Sicherheit | Span-Redaction-Invariante nur punktuell durchgesetzt (korr.) | `src/NodePilot.Scheduler/TriggerOrchestrator.cs:242` |
| x-robustness#4 | Low | einfach | Codequalität | Workflow-Export ersetzt bei jedem Fehler still die Definition | `src/NodePilot.Api/Controllers/WorkflowImportExportController.cs:859` |
| x-robustness#5 | Low | einfach | Codequalität | Breite Exception-Typen werden als Fachfehler samt ex.Message an Clients durchgereicht (korr.) | `src/NodePilot.Api/Controllers/CustomActivitiesController.cs:82` |
| x-security#1 | Low | einfach | Performance | SSRF-Guard löst DNS synchron auf asynchronen Hot-Paths auf (korr.) | `src/NodePilot.Engine/Security/NetworkGuard.cs:53` |
| api-workflow-surface#6 | ohne | einfach | Wartbarkeit | Geschäftslogik und Abfrage-Aggregation in sehr langen Controller-Methoden (korr.) | `src/NodePilot.Api/Controllers/WorkflowImportExportController.cs:327` |
| x-architecture-dependencies#4 | ohne | einfach | Struktur | Core vermischt Server-Domänenkern mit Client-SDK-Infrastruktur (korr.) | `src/NodePilot.Core/Clients/TokenStore.cs:16` |
| x-security#3 | ohne | einfach | Wartbarkeit | Provider-spezifische Dauer-Ausdrücke in Raw-SQL mehrfach dupliziert (korr.) | `src/NodePilot.Api/Services/WorkflowListOrdering.cs:39` |

## Anhang C — Verworfene Kandidaten

Diese Kandidaten haben die Gegenprüfung nicht bestanden. Sie stehen hier, damit sie nicht erneut gejagt werden.

- **api-security-identity#2 — AuthController ist ein 1305-Zeilen-Transaction-Script mit Domänenlogik im Controller**: Die Zitate und die 1305 Zeilen stimmen. Im Kern ist der Befund aber falsch und ohne Defekt. (1) git grep findet alle DummyHash-Aufrufe zwischen Z. 375 und 561, also im lokalen Login. Der LDAP-Pfad (Z. 702-871) hat nur Audit plus 401/503. Die Behauptung, das Muster wiederhole sich auch in TryLdapLoginAsync, ist falsch. (2) Ich habe jeden Exit-Pfad im lokalen Login durchgesehen. Alle Ablehnungen vor dem echten …
- **x-performance#4 — Designer lädt bei jedem Terminalstatus den vollständigen Step-Payload des letzten Laufs neu**: Die Zitate stimmen, die tragende Behauptung nicht. Die einzige Produktiv-Instanz von PropertiesPanel wird in EditorRightPanel nur bei ausgewähltem Node gerendert. Ohne Beobachter lädt TanStack Query bei invalidateQueries nicht nach. Der behauptete Verkehr "auch ohne ausgewählten Step" entsteht also nicht. Bei offenem Panel ist das Nachladen des neuesten terminalen Laufs die eigentliche Funktion, nämlich …
- **x-security#2 — DbAdmin-Lesemodus auf SQL Server nur per Keyword-Denylist durchgesetzt**: Die Zitate stimmen, und der Code verhält sich wie beschrieben. Die Roadmap führt diese Grenze aber bereits als zurückgestellten R2-Posten mit bewusster Einordnung als Deployment-Thema. Nach den Vorgaben ist das dokumentierte, akzeptierte Schuld und kein neuer Befund. Zudem ist DbAdmin Admin-only, und Admins haben ohnehin einen eigenen Schreibmodus.
