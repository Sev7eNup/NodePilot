# Alerting

Benutzerdefinierte **Notification-Rules**, die bei passenden Ereignissen über einen oder mehrere
Kanäle benachrichtigen. **Opt-in per Daten** — der Dispatcher ist immer registriert, tut aber nichts,
bis mindestens eine Regel existiert. Deckt Execution-Events, Live-Zustände (läuft lange / wartet lange),
globale Signale (Service, Maschine, Backlog, Pending, Abbruch-Rate) und workflow-bezogene Signale
(Zeitplan verpasst, kein aktueller Erfolg) ab, zugestellt über die Kanäle **E-Mail** + **Generic-Webhook**.

## Eine Regel besteht aus

| Teil | Bedeutung |
|---|---|
| **Ereignistypen** | Grober Vorfilter: z. B. `ExecutionFailed`, `ExecutionSucceeded`, `ExecutionCancelled`, `ExecutionRunningLong`, `ExecutionQueuedLong`, `ScheduleMissed`, `WorkflowNoRecentSuccess`, `CredentialFailure`, `ServiceStale`, `MachineUnreachable`, `BacklogHigh`, `PendingHigh`, `CancelRateHigh`, `CredentialExpiring`. |
| **Geltungsbereich** | `Global` (alle Workflows), `Ordner` oder `Workflows` (explizite Ziele). |
| **Filter** *(optional)* | Composable AND/OR/NOT-Ausdruck über Event-Felder — **derselbe** Condition-Builder wie für Edge-Bedingungen, Operanden mit Quelle `event`. Leer = jedes Ereignis der gewählten Typen im Bereich. |
| **Gruppierung** *(optional)* | Dedup-Template für Cooldown/Flap, z. B. `{{eventType}}:{{workflowId}}`; leer nutzt die Standard-Gruppierung pro Workflow bzw. Signalquelle. |
| **Kanäle (Routes)** | Je ein Ziel: Kanal (`E-Mail` / `Webhook`) + Ziel (Empfänger / URL) + optionales verschlüsseltes Secret (Webhook-HMAC). Jeder Kanal kann zusätzlich eine eigene Event-Feld-Bedingung haben. |
| **Throttle** | `Cooldown` (Rate-Limit pro Dedup-Schlüssel), `Min. Vorkommen` + `Zeitfenster` (Flap-Suppression). |

## Wie es funktioniert

Ein **leader-gated Dispatcher** (Hintergrunddienst, ~30 s) scannt terminale Executions ab einem
persistierten Watermark (beim ersten Lauf auf „jetzt" geseedet → **keine** Historie wird
nachalarmiert), matcht sie gegen aktivierte Regeln (Typ-Vorfilter → Scope → Filter → Kanal-Filter), wendet
Cooldown/Flap-Suppression an, persistiert **vor jedem Versand** einen Pending-Zustellversuch pro
Kanal (idempotent), schiebt das Watermark vor und sendet dann. Ein Absturz zwischen Persistieren und
Senden hinterlässt einen wiederholbaren Eintrag → mindestens-einmal-Zustellung, genau-einmal pro
(Regel, Kanal, Vorkommen).

Die **Sinks** sind self-isolating (ein Fehler wird als fehlgeschlagenes Ergebnis zurückgegeben, nie
geworfen). E-Mail nutzt die SMTP-Konfiguration (TLS-default-on, Einzelempfänger,
Header-Injection-Schutz); der Webhook-Sink postet durch den SSRF-gehärteten HTTP-Client und signiert
den Body optional per HMAC-SHA256 (`X-NodePilot-Signature`).

## Gauge-Events (Infra-Signale)

Neben Execution-Events alarmiert ein zweiter Collector auf **Zustands-Übergänge** der Infrastruktur:

- **Service veraltet** — der Heartbeat eines Hintergrunddienstes ist älter als das 3-fache seines
  erwarteten Intervalls (dieselbe Formel wie im Dashboard).
- **Backlog hoch** — die Zahl laufender/wartender Executions (`Pending + Running`) übersteigt
  `Alerting:Gauge:BacklogThreshold` (Default 500).
- **Pending-Backlog hoch** — nur **wartende** Executions (`Pending`, laufende ausgenommen) übersteigen
  `Alerting:Gauge:PendingThreshold` (Default 40).
- **Abbruch-Rate hoch** — abgebrochene Executions **über alle Workflows** im Trailing-Fenster
  (`Alerting:Gauge:CancelRateWindowMinutes`, Default 10) übersteigen `Alerting:Gauge:CancelRateThreshold`
  (Default 10) — die globale Rate, die die pro-Workflow-Flap-Unterdrückung nicht ausdrücken kann.
- **Maschine nicht erreichbar** — der letzte gespeicherte Connectivity-Check einer Maschine ist fehlgeschlagen.
- **Credential läuft ab** (`CredentialExpiring`) — ein Credential mit optionalem `ExpiresAt` liegt im
  Warnfenster `Alerting:Gauge:CredentialExpiryWarnDays` (Default 14) oder ist bereits abgelaufen (dann
  Critical). `signalValue` = Tage bis Ablauf (negativ nach Ablauf). Alarmiert beim ersten ungesunden
  Zustand **sofort** (Operator hat das Ablaufdatum selbst gepflegt → `AlertOnFirstUnhealthy`).
  Credentials ohne `ExpiresAt` werden nicht getrackt. Global-only.
- **Zeitplan verpasst** — ein `scheduleTrigger` hätte feuern müssen, aber es wurde keine schedule-Ausführung angelegt.
- **Kein aktueller Workflow-Erfolg** — ein aktivierter geplanter Workflow hatte innerhalb des konfigurierten Fensters keinen erfolgreichen Lauf.

Pro **ungesunder Episode** wird jede Regel **höchstens einmal** alarmiert (der Übergang gesund→ungesund
eröffnet die Episode). Der Filter wird dabei in jedem Durchlauf neu geprüft — eine Regel mit z. B.
`Messwert > 1000` feuert also auch dann noch, wenn der Wert die Schwelle erst später überschreitet
(der Provider wird z. B. schon ab Backlog > 500 ungesund), statt für immer verschluckt zu werden. Die
Erholung wird still getrackt (keine „wieder ok"-Meldung), und ein bereits beim ersten Blick ungesunder
Zustand wird standardmäßig stumm geseedet (kein Nachalarmieren); Maschinen-/Schedule-/Workflow-Health
dürfen beim ersten ungesunden Zustand sofort alarmieren. Globale Signale gelten **global** (kein
Ordner-/Workflow-Scope). `Zeitplan verpasst` und `Kein aktueller Workflow-Erfolg` tragen Workflow-Kontext
und dürfen daher auch Ordner-/Workflow-Scope verwenden. Das numerische Filterfeld `Messwert`
(signalValue) erlaubt Verfeinerungen wie `Messwert > 50`.

## Live-Langläufer & manueller Abbruch

- **Ausführung läuft lange** (`ExecutionRunningLong`) — alarmiert **live**, während eine Execution schon
  länger als `Alerting:LongRunningSeconds` (Default 600) läuft (nicht erst nachträglich über `durationMs`).
  Jede Execution alarmiert **genau einmal**. Anders als Gauge-Events ist dies **execution-scoped** —
  eine Regel darf Global, Ordner- oder Workflow-Scope haben.
- **Ausführung wartet lange** (`ExecutionQueuedLong`) — alarmiert **live**, wenn eine Execution länger als
  `Alerting:QueuedLongSeconds` (Default 300) im Status `Pending` hängt.
- **Credential-Fehler** (`CredentialFailure`) — wird zusätzlich zu `ExecutionFailed` erzeugt, wenn die
  Fehlermeldung nach Authentifizierungs-/Credential-Problem aussieht (z. B. Access denied, Unauthorized,
  Logon failure, invalid password).
- **Manueller Abbruch** — das Event-Feld `Abgebrochen von` (`cancelledBy`) unterscheidet, wer einen Lauf
  abgebrochen hat: `user` (manueller Einzel-Abbruch), `cancelAll`, `failover`, `reconciler`, `dispatch`
  oder `system` (Timeout/Shutdown). Eine Regel auf `Ausführung abgebrochen` mit Filter
  `Abgebrochen von = user` alarmiert nur bei von Hand beendeten Jobs.

## Bedienung

- **UI:** Seite **Alerting** (`/alerts`) — Regel-Liste + Ein-Seiten-Editor mit Live-**Test-Fire**.
  Der Filter nutzt den wiederverwendeten Condition-Builder im Event-Feld-Modus. Der Button
  **Zustellungen** öffnet das `DeliveriesModal` — das Zustell-Ledger mit letzten Versuchen und Status-Filter.
- **Rollen:** Lesen Admin/Operator; Anlegen/Ändern/Löschen/Test-Fire **Admin-only**.
- **CLI:** `np alerting list|get|create|update|delete|test-fire|deliveries` (Routen via `--email`/`--webhook`; `deliveries` filtert per `--rule`/`--status`/`--limit`).
- **MCP:** `list/get/create/update/test_fire_alerting_rule` + `list_alerting_deliveries` (+ `delete_alerting_rule`, gated). Route-Secrets werden nie ausgegeben.

## Zustell-Ledger & Aufbewahrung

Jeder Sendeversuch schreibt einen `NotificationDeliveryAttempt`-Eintrag — gleichzeitig
Idempotenz-Guard (genau-einmal-Zustellung pro Regel/Kanal/Ereignis) und Audit-Protokoll. Schlägt
ein Versand fehl, bleibt der Eintrag `Pending` und wird in folgenden Durchläufen wiederholt
(max. 5 Versuche, danach `Failed`). Gelesen wird das Ledger über `GET /api/alerting/deliveries`
(UI-**Zustellungen**-Modal, `np alerting deliveries`, MCP `list_alerting_deliveries`). Damit die
Tabelle nicht unbegrenzt wächst, löscht `NotificationRetentionService` (leader-gated,
`Retention:Notifications:*`, Default 90 Tage / alle 6 h, per `Enabled:false` abschaltbar) terminale
Einträge sowie veraltete Suppression-States.

## Sicherheit

Route-Secrets sind at-rest verschlüsselt und in API-Antworten **redigiert** (Sentinel, nie Cipher).
Webhook-URLs sind SSRF-gefiltert, E-Mail ist Einzelempfänger + Header-Injection-geschützt. Audit:
`ALERT_RULE_CREATED/UPDATED/DELETED/TEST_FIRED`.

## Ausblick

Optionale „wieder ok"-Recovery-Meldungen, Eskalationsstufen sowie weitere Kanäle
(PagerDuty / Opsgenie). Ein automatischer WinRM-Reachability-Poller, der `MachineUnreachable`
füttert, ist ebenfalls geplant (heute wird `IsReachable` nur vom manuellen Maschinen-Test geschrieben).
