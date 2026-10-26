# NodePilot.Cli (`np`) — Conventions

Applies to `src/NodePilot.Cli/`. Project-wide rules are in the root `CLAUDE.md`.

Pure HTTP client against the REST endpoints (Spectre.Console.Cli), shipped with both installers (`<install>\tools\np`, machine PATH via `deploy/MachinePath.ps1`), from the checkout via `dotnet publish` — **not** a `dotnet global tool` (`PackAsTool` × `net10.0-windows` = NETSDK1146). Command areas: `auth`, `workflow`, `exec`, `machine`, `credential`, `globals`, `user`, `shared-folder`, `maintenance`, `alerting`, `system-alert`, `audit`, `backup`, `db`, `cron`, `health`, `dashboard`, `failure-causes`, `operations`, `observability`, `settings`, `secrets`, `config`. Details: `docs/claude-reference.md`.

**Architecture convention:** New API endpoint → add a method in `NodePilotApiClient.cs` + a command in parallel. Duplicate DTOs in `Cli/Api/Dtos/` (no ProjectReference).

**Login paths:** `np auth login` uses the password endpoint (local + LDAP); `--windows` signs in the current Windows account via Negotiate/Kerberos. On this path the API returns the JWT **only** in the httpOnly cookie `np_auth` (deliberate, see `AuthController.WindowsLogin`) — the client therefore builds a handler with `UseDefaultCredentials` + `CookieContainer` (`ApiClientFactory.CreateForWindowsSso`) and reads the token from the jar instead of the body. The expiry comes from the `exp` claim via `ClientSessionSecurity.TryResolveExpiration`; the API is not touched for this.

**Shared client infrastructure:** `ApiException`, the response plumbing (`ApiResponseReader`), the read side of `config.json` (`ClientConfigStore` + `CliConfig`), the DPAPI session store and token rotation (`TokenStore`, `StoredSession`, `TokenRefreshHandler`) as well as the TLS layer (`CertificatePin`, `ClientTlsOptions`, `PinnedCertificateHandlerFactory`, `TlsObservationHandler`, `NetworkFailureAnalyzer`) live in `NodePilot.Core.Clients` — shared with the MCP server. **Only the DTOs remain duplicated on purpose** (see above, `ApiDtoParityTests`); do not copy new infrastructure again (guard: `CliSessionInteropTests` in Mcp.Tests).
