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

`FileSystemOperation:AllowedRoots` loest lokale Symlinks/Junctions fuer existierende
Pfadsegmente auf, bevor der Root-Vergleich passiert. Ein Link innerhalb eines erlaubten Roots
auf ein Ziel ausserhalb wird dadurch lokal blockiert.

Remote-WinRM-Ziele bleiben eine explizite Grenze: die API kann die Reparse-Point-Map des
Remote-Hosts nicht lokal aufloesen. Remote-Workflows brauchen target-seitige ACLs,
eingeschraenkte Arbeitsverzeichnisse und keine breit beschreibbaren Link/Junction-Pfade.

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
