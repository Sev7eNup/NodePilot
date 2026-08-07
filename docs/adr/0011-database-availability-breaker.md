# ADR 0011 – Database-Availability Breaker

**Status:** Accepted – 2026-08-07  
**Scope:** Ausfall oder Hängen der Anwendungsdatenbank im laufenden Betrieb. Der Boot-Pfad bleibt
fail-closed.

## Kontext

Ohne gemeinsamen Ausfallzustand warteten HTTP-Requests und Hintergrunddienste jeweils bis zu ihren
eigenen Datenbank-Timeouts. Ein hängender Server konnte dadurch den Prozess minutenlang unbenutzbar
machen, obwohl Liveness weiter 200 meldete. Gleichzeitig darf eine einzelne langsame Abfrage nicht
die gesamte Installation als ausgefallen markieren.

## Entscheidung

Ein prozessweiter Breaker verwaltet vier Zustände:

| Zustand | Bedeutung | API | Readiness |
|---|---|---|---|
| `Booting` | Migration und Startup-Recovery laufen | noch keine Pipeline | – |
| `Available` | Datenbank ist bestätigt nutzbar | normal | 200 |
| `Armed` | Timeout beobachtet, Sonde entscheidet | normal | 503 |
| `Unavailable` | Ausfall bestätigt | 503 | 503 |

Nach dem Boot darf ausschließlich die Recovery-Sonde wieder `Available` veröffentlichen.
Interceptors dürfen den Zustand nur verschlechtern.

### Erkennung und Recovery

- Klassifizierte Verbindungsfehler öffnen den Breaker. Unbekannte Open-Fehler und Command-Timeouts setzen zunächst nur `Armed`.
- Eine separate, ungepoolte Verbindung prüft mit `SELECT 1`. Open, Command und Cleanup besitzen
  harte Zeitgrenzen.
- Zwei erfolgreiche Probes schließen den Breaker. Der Application-Pool wird genau einmal pro
  tatsächlicher Ausfall-Episode geleert, nicht bereits bei `Armed`.
- `RejectedByServer` ist für die Episode sticky und kennzeichnet Zugangsdaten-, Datenbank- oder
  TLS-Fehler. Der Zustand benötigt einen Administrator; geänderte NodePilot-Verbindungsdaten werden
  erst nach einem Neustart wirksam.

### HTTP, Health und UI

- Die Availability-Middleware läuft vor Authentifizierung und beendet `/api`, die Hub-HTTP-Fläche,
  `/signin-oidc` und geschützte `/metrics` ohne Datenbankzugriff.
- Die statische SPA-Shell und Assets bleiben erreichbar. Die UI zeigt Banner und Status-Ampel,
  unterdrückt Toast-Stürme und lädt Daten nach Recovery neu.
- `/healthz/live` bleibt 200. `/healthz/ready` ist das Traffic-Gate. `/healthz/database`
  antwortet immer 200 mit `ok | armed | unavailable` und grober Ursache.
- HTTP-Fehler verwenden den gemeinsamen Body
  `{code, message, retryAfterSeconds, reason, retryable}`.
  `DATABASE_TIMEOUT` bleibt die Antwort für eine langsame, aber nicht bestätigte ausgefallene
  Datenbank; die Fehlerform ist in [ADR 0007](0007-api-validation-and-error-contract.md) definiert.
- Etablierte WebSockets werden durch einen Hub-Filter geschützt. Generische I/O- oder Timeout-Fehler
  werden nur mit Breaker- oder Datenbank-Provider-Evidenz als Datenbankfehler übersetzt.

### Workflows und Hintergrunddienste

- Vor einer Activity ist der `Running`-Step mit stabiler GUID dauerhaft gespeichert.
- Nach der Activity ist der terminale Step-Write eine Barriere: Bei Ausfall wartet die Engine,
  verwendet einen frischen DbContext und betritt erst danach die nächste Kante.
- Auch der terminale Workflow-CAS wartet bis Recovery oder Host-Shutdown. Benutzer-Cancellation
  beendet nicht den abschließenden Persistenzversuch.
- Der Dispatcher reiht nur Fehler erneut ein, die sicher vor Engine-Start auftraten.
- Datenbankabhängige Hosted Services parken am gemeinsamen Availability-Signal. Support-Events
  werden während des Ausfalls gezählt verworfen und nach Recovery einmal zusammengefasst.
- Trigger-Fires, die aktive Sources während des Ausfalls beobachten, werden gezählt verworfen und
  nicht nachgeholt. Leadership-Loss entsorgt Sources; Persistenz und Dispatch prüfen die Lease-Epoch.
- Notification Delivery bleibt at-least-once. Webhooks tragen `eventKey` im JSON und in
  `X-NodePilot-Event-Key` zur Empfänger-Deduplizierung.

### Konfiguration und Betrieb

Alle Availability-Werte sind positive, restart-pflichtige Boot-Konfiguration:

| Einstellung | Default |
|---|---:|
| `Database:ConnectTimeoutSeconds` | 5 |
| `Database:AuthReadTimeoutSeconds` | 3 |
| `Database:ReadinessProbeTimeoutSeconds` | 5 |
| `Database:Probe:ConnectTimeoutSeconds` / `CommandTimeoutSeconds` / `CleanupTimeoutSeconds` | 2 / 2 / 2 |
| `Database:Probe:IdleIntervalSeconds` / `OutageIntervalSeconds` | 5 / 5 |
| `Database:Probe:SuccessesToRecover` / `FailureThreshold` | 2 / 2 |

`0` wird abgelehnt, weil Provider damit teilweise unbegrenzte Timeouts aktivieren.

Wichtige Signale:

- Metriken: `nodepilot.database.requests_rejected`, `nodepilot.database.outages`,
  `nodepilot.database.probe_cleanup_timeouts` und
  `nodepilot.scheduler.triggers.dropped_db_unavailable`.
- Audit: genau ein `DATABASE_RECOVERED` pro prozesslokaler Recovery-Episode; kein Trip-Audit,
  weil die Datenbank beim Öffnen nicht zuverlässig schreibbar ist.
- Logs: Öffnen, echte Reason-Wechsel und Schließen einer Episode, nicht jeder Probe-Tick.

## Konsequenzen und Grenzen

- Kaltstart ohne Datenbank bleibt fail-fast; der Service-Control-Manager übernimmt den Neustart.
- Prozessverlust oder HA-Failover während einer mehrdeutigen Activity führt weiterhin zu
  `Interrupted/Cancelled`, nicht zu automatischer Wiederholung externer Side Effects.
- Bereits laufende, im Provider blockierte Commands werden durch ein späteres Öffnen des Breakers
  nicht global abgebrochen. Neue Zugriffe werden dagegen sofort gegatet.
- Trigger besitzen bewusst kein Catch-up; Notification-Empfänger müssen Deduplizierung unterstützen.
