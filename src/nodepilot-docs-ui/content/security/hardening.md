# Hardening-Flags

Guard-Flags sind **hardened by default**: `appsettings.json` shipped sie als `true`, und ein **fehlender** Key liest ebenfalls als `true`. `appsettings.Development.json` relaxt sie auf `false` für lokale Iteration.

Ausnahme: `PrometheusScrapeAllowAnonymous` ist eine Relaxation und defaultet auf `false`.

| Key | Default | Effect |
|---|---|---|
| `Remote:RequireWinRmSsl` | `true` | WinRM ohne SSL → Exception (Dev: `false`) |
| `RestApi:BlockPrivateNetworks` | `true` | Blockt RFC1918/Loopback in `restApi` (Dev: `false`) |
| `RestApi:AllowedHosts` | `[]` | Exakte Host-/IP-Liste; zwingend für PowerShell-`waitForCondition` und tatsächlich proxied `restApi`-Ziele/Redirects; Link-Local/Metadata bleibt immer gesperrt |
| `FileSystemOperation:RejectTraversal` | `true` | Rejects `..` in Filesystem-Op-Paths (Dev: `false`) |
| `SqlActivity:RequireConnectionRef` | `true` | Nur named `connectionRef` statt inline `connectionString` (Dev: `false`) |
| `StartProgram:DisallowShellExecute` | `true` | Verwirft `useShellExecute=true` (Dev: `false`) |
| `Trigger:Database:RequireConnectionRef` | `true` | Nur named `connectionRef` für `databaseTrigger` (Dev: `false`) |
| `Security:StrictAllowedHosts` | `true` | Boot-Abbruch bei unsafe `AllowedHosts` (z. B. `*`) (Dev: `false`) |
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

`ai-generate` ist hardcodiert in `RateLimitingSetup.cs` und deckt alle drei AI-Endpunkte: `POST /api/ai/generate-script`, `POST /api/ai/generate-workflow` und `POST /api/ai/chat`.
