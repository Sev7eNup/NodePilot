# Trigger

Trigger sind die Roots eines Workflow-Laufs. Der `TriggerOrchestrator` scannt alle 5 s alle Trigger-Quellen. Trigger-Daten landen als `{{manual.<name>}}`-Variablen im Run und zusätzlich als `param.*` des Trigger-Nodes (`{{<triggerVar>.param.<name>}}`).

> **Kein `trigger.*`-Namespace.** Es gibt **kein** `{{trigger.*}}`-Namespace. `{{trigger.file.path}}` bleibt ein unresolvetes Literal. Immer `{{manual.<name>}}` oder `{{<triggerVar>.param.<name>}}` verwenden.

## Trigger-Typen

| Trigger | Backing | Injizierte Keys |
|---|---|---|
| `manualTrigger` | UI / API | User-deklarierte Parameternamen → `{{manual.<name>}}` |
| `scheduleTrigger` | Quartz cron | `firedAt`, `nextFireAt` (ISO-8601 UTC) |
| `fileWatcherTrigger` | FileSystemWatcher | `fileAction` (created/changed/deleted/renamed), `filePath`, `fileName` |
| `databaseTrigger` | Timer + SELECT-Polling | `dbSentinel` (neuer Sentinel-Wert), `dbPrevious` |
| `eventLogTrigger` | `EventLog.EntryWritten` | `eventSource`, `eventEntryType`, `eventId`, `eventMessage`, `eventTimeWritten` |
| `webhookTrigger` | HTTP `POST/GET/PUT/DELETE /api/webhooks/{workflow}/{path}` | `webhookBody`, `webhookMethod`, `webhookPath`, `webhookQuery_<key>`, `webhookHeader_<key>` + pro `fieldMappings`-Eintrag der gemappte Name |

## Webhook-Trigger

`POST|GET|PUT|DELETE /api/webhooks/{workflow}/{path}` — das HTTP-Verb muss `webhookTrigger.method` matchen. Mit `Webhook:RequireSecret` (default `true`) wird ein konfiguriertes Secret erzwungen.

**Verifizierung:** Default = Shared-Secret-Header (`X-Webhook-Secret`). Der replay-sichere Modus heißt explizit `signatureMode: "nodepilot-hmac-v2"` und verlangt ein CSPRNG-Secret mit mindestens 32 UTF-8-Bytes, `X-NodePilot-Timestamp` (UNIX-Sekunden) und eine eindeutige `X-NodePilot-Delivery-Id`. Der HMAC-SHA256-Eingabestream ist exakt:

```text
NodePilot-HMAC-v2\n
timestamp\n
deliveryId\n
METHOD\n
escapedPath\n
canonicalQuery\n
rawBodyBytes
```

`METHOD` ist uppercase; `escapedPath` enthält den von NodePilot gesehenen `PathBase`. Für `canonicalQuery` werden alle decodierten Key/Value-Paare einzeln UTF-8/RFC3986-percent-encodet, ordinal nach encodetem Key sortiert und per `&` verbunden; die Reihenfolge doppelter Werte bleibt erhalten. Keine Query ergibt einen Leerstring. Der Digest steht als `{signaturePrefix}{hex}` im konfigurierten `signatureHeader` (Defaults: `sha256=` und `X-NodePilot-Signature`). Freshness-Fenster: fünf Minuten; jede Delivery-ID wird über den gemeinsamen DB-Unique-Guard clusterweit nur einmal angenommen. Beliebige Request-Header werden im v2-Modus nicht als Workflowparameter weitergereicht, weil sie nicht signiert sind.

> **Breaking Security Change:** Legacy-`signatureMode: "hmac"` mit Body-only-Signatur wird abgelehnt. Provider-native GitHub-/GitLab-/Alertmanager-Signaturen sind nicht direkt kompatibel, weil diese Provider die zusätzlichen NodePilot-Felder nicht signieren können. Dafür ist ein Adapter erforderlich, der zuerst die Provider-Signatur prüft und anschließend einen frischen NodePilot-HMAC-v2-Request erzeugt.

**Feld-Mappings:** `fieldMappings` (`[{name, path}]`) extrahiert Felder aus einem JSON-Body per JSONPath (gleicher Dialekt wie `jsonQuery`) als eigene Parameter — Downstream liest `{{manual.ticketId}}` statt `webhookBody` zu parsen. Non-JSON-Bodies oder nicht-matchende Pfade degradieren still zu Leerstring.

## External Trigger

`POST /api/trigger/{workflowNameOrId}` ist anonym, gated via `X-Api-Key`-Header (nur aktiv wenn `ExternalTrigger:ApiKey` gesetzt). Akzeptiert optionale `Idempotency-Key`-Header für Replay-Schutz. Rate-Limit: 30/Min/IP.

## Idempotency-Keys

`IdempotencyKeyCleanupService` pruned Idempotency-Keys nach 24 h TTL — läuft **immer**, nicht abschaltbar.
