# Hardening flags

Hardening flags default to `true` in `appsettings.json`. A missing key is also treated as `true`. `appsettings.Development.json` sets selected flags to `false` for local development.

The exception: `PrometheusScrapeAllowAnonymous` is a relaxation and defaults to `false`.

| Key | Default | Effect |
|---|---|---|
| `Remote:RequireWinRmSsl` | `true` | WinRM without SSL → exception (development: `false`) |
| `RestApi:BlockPrivateNetworks` | `true` | Blocks RFC 1918/loopback in `restApi` (development: `false`) |
| `RestApi:AllowedHosts` | `[]` | An exact host/IP list for `restApi` targets and redirects that actually go through a proxy — an exception to `BlockPrivateNetworks`; link-local/metadata addresses stay blocked always |
| `WaitForCondition:AllowedHosts` | `["localhost"]` | A separate list for the PowerShell probes `portOpen`/`httpOk`; an empty list rejects every probe. Kept apart from `RestApi:AllowedHosts` so that a permitted probe does not also open `restApi` to loopback — and conversely it decides alone: `RestApi:*` is not consulted for probes |
| `FileSystemOperation:RejectTraversal` | `true` | Rejects `..` in file-system operation paths (development: `false`) |
| `SqlActivity:RequireConnectionRef` | `true` | Only a named `connectionRef` instead of an inline `connectionString` (development: `false`) |
| `StartProgram:DisallowShellExecute` | `true` | Rejects `useShellExecute=true` (development: `false`) |
| `Trigger:Database:RequireConnectionRef` | `true` | Only a named `connectionRef` for `databaseTrigger` (development: `false`) |
| `Security:StrictAllowedHosts` | `true` | Aborts the boot on an unsafe `AllowedHosts` (e.g. `*`) (development: `false`). The installers always add `localhost` to `AllowedHosts` — their own health probe goes to `https://localhost:<port>/healthz/ready`, which the host filter would otherwise reject with 400 |
| `Webhook:RequireSecret` | `true` | `webhookTrigger` requires a configured secret — verified as an `X-Webhook-Secret` header or an HMAC signature depending on `signatureMode` (development: `false`) |
| `OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` | `false` | Makes `/metrics` reachable anonymously |

> **Missing key = hardened.** A missing hardening key reads as `true` (or, for `PrometheusScrapeAllowAnonymous`, as `false`). In production it is better to set them explicitly, to avoid any misunderstanding.

## The database-admin query console

Read mode accepts exactly one read-only SQL statement. PostgreSQL additionally uses a READ ONLY
transaction; SQL Server and SQLite rely on the single-statement guard, a rollback, and the database
principal.

The NodePilot database login therefore has to stay least-privilege: no `sysadmin`, no `db_owner`, no
rights on `xp_cmdshell`, OLE Automation or SQL Agent/OS command procedures. Database-admin read mode
is defence in depth, not a substitute for a hardened database principal.

## File path roots

`FileSystemOperation:AllowedRoots` compares paths only within explicitly permitted roots. Beforehand,
every existing path segment is checked for link-local attributes: symlinks, junctions and other
reparse points are rejected — not resolved and not followed. That reparse block applies even with an
empty or missing root list. Remote activities repeat the check in the PowerShell context of the actual
WinRM target. A non-empty configured root has to exist there.

Root arrays are read atomically from the highest-priority configuration provider; a shorter runtime
array therefore does not inherit old indices from `appsettings.json`. `AllowedRoots: []` retains the
existing semantics of "no containment restriction" while the reparse block stays active. Sparse or
otherwise malformed arrays are rejected fail-closed.

The check closes existing junction bypasses but does not replace target-side ACLs: path-based
PowerShell/WinRM operations cannot atomically bind a parent directory that another process renames
concurrently to a previously checked handle. Permitted target trees must therefore not be writable by
less privileged users.

ZIP compression accepts wildcards only in the last path segment. The expansion and the directory walk
are performed in a controlled, non-recursive way per step; every manifest entry is re-checked for
reparse points before it is opened. Square brackets are literal file-name characters here, not
PowerShell provider wildcards. ZIP extraction validates before and after every directory creation and
writes files with `CreateNew`.

With recursive file watching, the existing tree is checked before the watcher is opened, without
following reparse points. The manual scan is iterative as well, and event paths are re-validated
before dispatch. A concurrent parent rename by a privileged process still cannot be ruled out
atomically with path-based APIs; the ACL of the watched root therefore remains security-relevant.
Ordinary UNC shares remain permitted for the file watcher, but Windows device/extended paths are
rejected before every file-system access, so that the hard-block list cannot be bypassed through
`\\?\C:\...`. Local administrative UNC aliases such as `\\localhost\C$\...` are canonicalized to the
local drive path before comparison. Windows/SMB normalization rules apply first, so that alternative
share spellings and `..` segments clamped at the share root hit the same policy. A watch root that
contains a blocked system tree is rejected as well. Unknown named shares of the local machine are
fail-closed when `AllowSystemPaths=false`; remote UNC shares are not rewritten.

## Rate limiting

Per IP, sliding window:

| Area | Limit |
|---|---|
| login | 50/min |
| refresh | 20/min |
| webhook | 60/min |
| trigger | 30/min |
| ai-generate | 20/min |
| audit | 60/min |
| backup | 10/min |

`ai-generate` is hard-coded in `RateLimitingSetup.cs` and sits as `[EnableRateLimiting]` on the three AI controllers — it therefore applies to every AI endpoint: `POST /api/ai/generate-script`, `POST /api/ai/generate-workflow`, the workflow chat (`POST /api/ai/chat` including `/chat/applied` and `/chat/activity/{workflowId}`) and the global knowledge chat (`POST /api/ai/knowledge/ask`, `GET /api/ai/knowledge/capabilities`).
