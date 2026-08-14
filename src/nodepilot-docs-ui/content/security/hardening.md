# Hardening-Flags

Hardening-Flags sind in `appsettings.json` standardmäßig `true`. Ein fehlender Schlüssel wird ebenfalls als `true` behandelt. `appsettings.Development.json` setzt ausgewählte Flags für lokale Entwicklung auf `false`.

Ausnahme: `PrometheusScrapeAllowAnonymous` ist eine Relaxation und defaultet auf `false`.

| Key | Default | Effect |
|---|---|---|
| `Remote:RequireWinRmSsl` | `true` | WinRM ohne SSL → Exception (Dev: `false`) |
| `RestApi:BlockPrivateNetworks` | `true` | Blockt RFC1918/Loopback in `restApi` (Dev: `false`) |
| `RestApi:AllowedHosts` | `[]` | Exakte Host-/IP-Liste für tatsächlich proxied `restApi`-Ziele/Redirects — Ausnahme von `BlockPrivateNetworks`; Link-Local/Metadata bleibt immer gesperrt |
| `WaitForCondition:AllowedHosts` | `["localhost"]` | Eigene Liste für die PowerShell-Probes `portOpen`/`httpOk`; leere Liste lehnt jede Probe ab. Getrennt von `RestApi:AllowedHosts`, damit eine erlaubte Probe nicht zugleich `restApi` zu Loopback öffnet — und umgekehrt allein ausschlaggebend: `RestApi:*` wird für Proben nicht mitgeprüft |
| `FileSystemOperation:RejectTraversal` | `true` | Rejects `..` in Filesystem-Op-Paths (Dev: `false`) |
| `SqlActivity:RequireConnectionRef` | `true` | Nur named `connectionRef` statt inline `connectionString` (Dev: `false`) |
| `StartProgram:DisallowShellExecute` | `true` | Verwirft `useShellExecute=true` (Dev: `false`) |
| `Trigger:Database:RequireConnectionRef` | `true` | Nur named `connectionRef` für `databaseTrigger` (Dev: `false`) |
| `Security:StrictAllowedHosts` | `true` | Boot-Abbruch bei unsafe `AllowedHosts` (z. B. `*`) (Dev: `false`). Die Installer schreiben `localhost` immer mit in `AllowedHosts` — ihre eigene Health-Probe geht an `https://localhost:<port>/healthz/ready`, die der Host-Filter sonst mit 400 abweist |
| `Webhook:RequireSecret` | `true` | `webhookTrigger` erzwingt ein konfiguriertes Secret — verifiziert je nach `signatureMode` als `X-Webhook-Secret`-Header oder HMAC-Signatur (Dev: `false`) |
| `OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` | `false` | `/metrics` anonym erreichbar |

> **Missing key = hardened.** Ein fehlender Hardening-Key liest als `true` (bzw. bei `PrometheusScrapeAllowAnonymous` als `false`). In Produktion also lieber explizit setzen, um kein Missverständnis zu riskieren.

## DbAdmin Query Console

Read-Mode akzeptiert genau ein read-only SQL-Statement. PostgreSQL nutzt zusaetzlich eine
READ-ONLY-Transaktion; SQL Server und SQLite verlassen sich auf Single-Statement-Guard,
Rollback und den DB-Principal.

Der NodePilot-DB-Login muss deshalb least-privilege bleiben: kein `sysadmin`, kein `db_owner`,
keine Rechte auf `xp_cmdshell`, OLE Automation oder SQL-Agent/OS-Command-Prozeduren. DbAdmin
Read-Mode ist Defense-in-Depth, kein Ersatz fuer einen gehaerteten Datenbank-Principal.

## File Path Roots

`FileSystemOperation:AllowedRoots` vergleicht Pfade nur innerhalb explizit erlaubter Roots.
Vorher werden alle vorhandenen Pfadsegmente ueber link-lokale Attribute geprueft: Symlinks,
Junctions und andere Reparse Points werden abgelehnt, nicht aufgeloest oder verfolgt. Diese
Reparse-Sperre gilt auch bei leerer oder fehlender Root-Liste. Remote-Aktivitaeten wiederholen
die Pruefung im PowerShell-Kontext des tatsaechlichen WinRM-Ziels. Ein nicht-leerer
konfigurierter Root muss dort existieren.

Root-Arrays werden atomar aus dem hoechstprioren Configuration-Provider gelesen; ein kuerzeres
Runtime-Array erbt daher keine alten Indizes aus `appsettings.json`. `AllowedRoots: []` behaelt
die bestehende Semantik "keine Containment-Einschraenkung" bei, waehrend die Reparse-Sperre
aktiv bleibt. Sparse oder anderweitig fehlerhafte Arrays werden fail-closed abgelehnt.

Die Pruefung schliesst vorhandene Junction-Bypaesse, ersetzt aber keine target-seitigen ACLs:
pfadbasierte PowerShell-/WinRM-Operationen koennen eine gleichzeitig durch einen anderen
Prozess umbenannte Parent-Directory nicht atomar an einen zuvor geprueften Handle binden.
Erlaubte Zielbaeume duerfen deshalb nicht fuer weniger privilegierte Benutzer beschreibbar sein.

ZIP-Kompression akzeptiert Wildcards nur im letzten Pfadsegment. Die Expansion und der
Verzeichnis-Walk erfolgen kontrolliert und nicht rekursiv pro Schritt; jeder Manifest-Eintrag
wird vor dem Oeffnen erneut auf Reparse Points geprueft. Eckige Klammern sind dabei literale
Dateinamenzeichen und keine PowerShell-Provider-Wildcards. ZIP-Extraktion validiert vor und nach
jeder Verzeichniserstellung und schreibt Dateien mit `CreateNew`.

Bei rekursiver Dateiueberwachung wird der vorhandene Baum vor dem Oeffnen des Watchers ohne
Folgen von Reparse Points geprueft. Auch der manuelle Scan laeuft iterativ, und Ereignispfade
werden vor dem Dispatch erneut validiert. Eine gleichzeitig durch einen privilegierten Prozess
ausgefuehrte Parent-Umbenennung kann mit pfadbasierten APIs weiterhin nicht atomar ausgeschlossen
werden; die ACL des Watched Roots bleibt deshalb sicherheitsrelevant. Gewoehnliche UNC-Shares
sind fuer FileWatcher weiterhin zulaessig, Windows-Device-/Extended-Pfade werden jedoch vor jedem
Filesystem-Zugriff verworfen, damit die Hard-Block-Liste nicht ueber `\\?\\C:\\...` umgangen wird.
Lokale administrative UNC-Aliase wie `\\localhost\\C$\\...` werden vor dem Vergleich auf den
lokalen Laufwerkspfad kanonisiert. Dabei gelten zuerst die Windows-/SMB-Normalisierungsregeln,
damit alternative Share-Schreibweisen und am Share-Root geklemmte `..`-Segmente dieselbe Policy
treffen. Ein Watch-Root, der einen gesperrten Systembaum enthält, wird ebenfalls abgelehnt.
Unbekannte benannte Shares des lokalen Rechners sind bei `AllowSystemPaths=false` fail-closed;
Remote-UNC-Shares werden nicht umgeschrieben.

## Rate-Limiting

Per-IP, Sliding-Window:

| Bereich | Limit |
|---|---|
| login | 50/Min |
| refresh | 20/Min |
| webhook | 60/Min |
| trigger | 30/Min |
| ai-generate | 20/Min |
| audit | 60/Min |
| backup | 10/Min |

`ai-generate` ist hardcodiert in `RateLimitingSetup.cs` und liegt als `[EnableRateLimiting]` auf den drei AI-Controllern — es gilt damit für jeden AI-Endpunkt: `POST /api/ai/generate-script`, `POST /api/ai/generate-workflow`, den Workflow-Chat (`POST /api/ai/chat` samt `/chat/applied` und `/chat/activity/{workflowId}`) und den globalen Wissens-Chat (`POST /api/ai/knowledge/ask`, `GET /api/ai/knowledge/capabilities`).
