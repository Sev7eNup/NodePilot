# Deployment troubleshooting

Use this guide when installation, update or first access fails.
[Deployment overview](deployment-guide.md), [script parameters](../deploy/README.md)
and [server setup](../deploy/server/README.md) describe the normal path.
Replace default paths and service names below when the installation uses custom values.

## Find the current evidence

| Evidence | Location and retention |
|---|---|
| Full application diagnostics | `C:\ProgramData\NodePilot\logs\nodepilot-<date>.log`, 7 files, rolling daily and at 100 MB. This can cover less than seven days. A redacted view is available at `/support-log?source=server`. |
| Curated support log | `nodepilot-support-<date>.log` in the same directory, 90 files. The `SupportEvents` database projection has 90-day retention through `Retention:SupportEvents`. The browser viewer is at `/support-log`. |
| Setup transcript | `%TEMP%\nodepilot-server-setup.log`, appended across runs. Failed starts include a `Service-Start-Diagnose` section. |
| Windows diagnostics | Application log for .NET/runtime errors and System log for service-control errors. |

Use an elevated shell for the data directory. `%TEMP%` belongs to the account that
elevated setup, which may differ from the signed-in desktop user. Compare timestamps
with the current attempt. Preserve full logs early because their rotation is short.
The [log inventory](../src/nodepilot-docs-ui/content/en/deployment/logs.md) describes
which events are recorded and which are deliberately excluded.

## Service startup and ports

| Symptom | Action |
|---|---|
| `No such host is known` | Resolve the SQL server and public hostname using their FQDNs. Correct DNS or the configured names before retrying. |
| Runtime requirement failure | Run `dotnet --list-runtimes`. Both x64 `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` must meet the artifact's versions. A newer ASP.NET runtime alone is insufficient. |
| Service stops immediately | Read the runtime exception and setup diagnostics. Check configuration, file permissions and certificate private-key access before retrying. |
| Event 7000, gMSA logon failure at boot | Check DC/Netlogon access and `Test-ADServiceAccount`. Current installers add a Netlogon dependency. For older installations, inspect `sc.exe qc NodePilot`; set the required dependency without dropping other existing dependencies. |
| `SocketException (10013)` | Check `netsh interface ipv4 show excludedportrange protocol=tcp`. HTTP.SYS reservations can prevent binding with no visible listener. Use a free HTTPS port, such as 8443, and HTTP 0. |
| Port already occupied | `Get-NetTCPConnection -LocalPort 443` identifies a listener and PID. Distinguish this from a reserved port before stopping anything. |
| Database startup wait messages | Check reachability and database recovery. `Database:StartupWaitSeconds` defaults to 300 and permits up to 600. On expiry, later startup checks report the connection failure. |
| `Write-Output` is not recognized | Older artifacts may lack the PowerShell modules under `<InstallPath>\Modules`. Rebuild with the current artifact script, which stages the built-in modules. |

For an older module-staging failure, the original repair copied
`<InstallPath>\runtimes\win\lib\net10.0\Modules` into `<InstallPath>\Modules` and
restarted the service. Verify those paths against the installed artifact first.
Artifacts built before 2026-08-03 used delayed automatic start, which could add about
120 seconds after boot. Current builds start immediately and wait for the database.

## Database

SQL Server requires 2022 CU1 or later, build `16.0.4003.1` or newer. RTM can fail
with error 8005 under `Encrypt=Strict`. Verify the version on the SQL host:

```sql
SELECT SERVERPROPERTY('ProductVersion') AS Version,
       SERVERPROPERTY('ProductUpdateLevel') AS CU;
```

For a failed TLS handshake, check the certificate assigned to SQL Server, its
RSA KeyExchange key, hostname and trust chain. For PostgreSQL, check the root PEM,
hostname and certificate revocation. Follow the [database TLS steps](../src/nodepilot-docs-ui/content/en/deployment/database-tls.md).

A successful administrator connection does not prove service access. SQL Server
needs a login, target-database user and `db_owner` for the actual runtime identity:
the gMSA, the remote machine account, or `NT AUTHORITY\SYSTEM` for same-host LocalSystem.
For PostgreSQL, verify the role password, database ownership and `pg_hba.conf`.

`/healthz/ready` returns 503 when the database is unavailable. `/healthz/database`
returns HTTP 200 with the database state and reason, even during an outage, so inspect
its body. `np health` provides the same operational entry point. `RejectedByServer`
requires correcting credentials, database selection or TLS. Server-side repairs are
picked up by probes; changes to NodePilot's connection configuration require a restart.

During an outage, the service stays up and API requests return `503 DATABASE_UNAVAILABLE`.
Workflows wait at their durability boundary. Triggers observed during the outage are
counted and discarded without catch-up. Recovery follows successful probes, see
[ADR 0011](adr/0011-database-availability-breaker.md).

On a shared SQL host, check memory grants as well as query execution time:

```sql
SELECT name, value_in_use FROM sys.configurations
WHERE name = 'max server memory (MB)';
SELECT wait_type, waiting_tasks_count, wait_time_ms, max_wait_time_ms
FROM sys.dm_os_wait_stats WHERE wait_type = 'RESOURCE_SEMAPHORE';
```

A recorded 4 GB lab VM shared with SCCM needed only 85 ms CPU and 2,585 logical reads
for a workflow-list query, but waited 55 seconds for a 22 MB grant. The longest single
wait was 119 seconds. This was a host memory-budget problem. NodePilot now returns
`503 DATABASE_TIMEOUT` for database command timeouts. Set an appropriate SQL memory
limit before tuning the query.

## Certificates, identity and file permissions

| Symptom | Action |
|---|---|
| Missing HTTPS certificate or private key | Check `Get-ChildItem Cert:\LocalMachine\My\<thumbprint>` and `HasPrivateKey`. Re-import with a persisted machine private key. |
| Failure after certificate replacement | Grant the gMSA read access to the new private key. Trusting its issuer does not grant key access. LocalSystem already has MachineKeys access. See [certificate replacement](../src/nodepilot-docs-ui/content/en/deployment/production.md#replacing-the-https-certificate). |
| Browser or CLI rejects a self-signed certificate | Trust the verified issuer on the client, or use the expected SHA-256 pin for CLI/MCP. `np auth login --server <URL> --tls-thumbprint <SHA-256>` stores the pin. MCP uses `NODEPILOT_MCP_TLS_THUMBPRINT`. |
| Certificate name mismatch | Use a URL listed in the certificate. `np config set server https://<certificate-name>:<port>` corrects the profile. Root trust alone cannot fix a name mismatch. |
| JWT parent-directory security failure | Run `icacls C:\ProgramData\NodePilot` and identify untrusted write grants, including an obsolete service identity. Current installers repair and recheck these ACLs. On older installations, remove only the identified obsolete grant. `Jwt:RotateInsecureKeyFile=true` does not repair a parent-directory ACL. |
| DPAPI decryption fails after an identity change | Existing `CurrentUser`-encrypted credentials must be entered again. The production template uses `Credentials:DpapiScope=LocalMachine`; there is no migration helper for old credentials. |
| Service cannot read configuration after manual changes | Inspect the ACL on `<InstallPath>\appsettings.Production.json`. The service needs Read access. Updates reapply it; avoid broad write grants as a repair. |

The CLI's `--insecure-tls` skips verification for one invocation and is never stored.
Use verified trust or a certificate pin for regular access.

## First login and directory authentication

For `admin-setup.token` access denied, use the elevated `robocopy /B` procedure in
[first access](../deploy/README.md#first-access-and-protected-files). Do not change
the original token's ACL. A setup exit code of 0 does not prove automatic bootstrap
succeeded. Check the generated credential file or the seeded account's sign-in,
as described in [turnkey rollout](../deploy/server/README.md#turnkey-rollout).

| Symptom or audit reason | Action |
|---|---|
| API returns 401 before first login | Redeem the setup token. The login form reveals its field on the first attempt. |
| `Token is no longer valid` replaces the app | On builds before 2026-08-02, an expired 8-hour session could also block SPA navigation. Clear site cookies and update. |
| `ldap_user_object_not_found` after successful bind | Verify the account's `userPrincipalName` and that `BaseDn` includes its OU. Binding with an implicit UPN does not prove that attribute is set. |
| `local_login_policy` despite LDAP configuration | Confirm LDAP was enabled and saved successfully, then apply the required authentication restart and retry. |
| `no_allowed_directory_group` / `USER_DIRECTORY_ACCESS_REFUSED` | Compare actual group membership and SIDs with `AllowedGroupSids`. Correct credentials alone do not authorize directory access. |
| Login returns 503, directory unavailable | Check `/healthz/directory` and DC access. Builds before 2026-08-02 could omit an audit row for this case. |
| Windows SSO prompts for credentials | Check the HTTP SPN on the service identity and browser allowlist/intranet policy. Test after a browser restart without stored credentials or password-filling software. |

The [authentication guide](ldap-windows-sso.md) covers directory configuration and
break-glass access. New SSO configuration cannot replace the first local administrator.

## Updates, rollback and removal

For a locked DLL, `tasklist /m <dll>` identifies the holder. Confirm that any process
to be stopped belongs to the installation. Current updates wait up to 30 seconds
after service stop, terminate remaining installation processes and abort before
deletion if the files remain locked. An identified process that cannot exit requires
resolving its permissions or rebooting before retrying.

A rollback restores binaries only. Check its result and readiness probe in the log.
If the old binary cannot use a migrated schema, restore the pre-update database backup.
A tampered or insecure binary backup is rejected. If the production JSON is missing,
reinstall with the correct parameters; update cannot reconstruct that configuration.

Uninstall waits up to 90 seconds by default before deleting the service. If processes
remain, it leaves the service registered. Remaining files or binary backups produce
exit code 1. The database is always retained; data-directory deletion requires explicit
`-PurgeData` or `/PURGEDATA=1`. See [update and removal parameters](../deploy/README.md#updates-and-removal).

Earlier update failures had a different cause: before 2026-08-03 the process wait
was missing, so the updater could report the process it had just stopped. In one
uninstall lab measurement, shutdown continued for 31 seconds after SCM reported
Stopped. That is why SCM status alone is insufficient evidence of process exit.

## Live connection

In browser DevTools, filter Network for `hubs/execution` and inspect what follows a
successful `negotiate` request:

| Response | Active transport |
|---|---|
| `101 Switching Protocols` | WebSockets. |
| Long-running GET with `text/event-stream` | Server-Sent Events fallback. |
| Repeating GET/POST pairs | Long polling. |

Continuing live updates mean that a fallback works. Before blaming a proxy, check
`/healthz/database` and `/healthz/leader`: the database breaker blocks all hub transports,
and HA followers return 503. Hub state is per process, with no SignalR backplane,
so traffic must reach the leader with connection affinity.

Compare the same unauthenticated diagnostic request directly and through the proxy.
Use a trusted certificate or `--cacert <CA-file>` for an internal CA:

```powershell
curl.exe -sSi --noproxy '*' --http1.1 `
    -H 'Connection: Upgrade' -H 'Upgrade: websocket' `
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' `
    'https://nodepilot.contoso.local:8443/hubs/execution?id=x'

curl.exe -sSi --proxy http://proxy.contoso.local:8080 --http1.1 `
    -H 'Connection: Upgrade' -H 'Upgrade: websocket' `
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' `
    'https://nodepilot.contoso.local:8443/hubs/execution?id=x'
```

An application 401, 400 or 404 is expected here because there is no valid authenticated
connection ID. It shows that the request reached the application, not that an actual
WebSocket session succeeded. If only the proxy path hangs or returns an HTML error
page, inspect that path and confirm from a client without the proxy. TLS inspection
must support the actual WebSocket transport, including HTTP/2 Extended CONNECT where used.

For `Bad Request - Invalid Hostname`, inspect `AllowedHosts` and `install-report.txt`.
Add the required certificate-covered name to the semicolon-separated list and restart.
Strict host policy rejects `*`, while a scoped wildcard such as `*.example.com` is accepted.

Browser extensions can inject unrelated scripts. Retest in a clean profile and identify
the failed request. CSP failures use a distinct refusal message and should be diagnosed
from that message. For blocked scripts or file swaps, consult the [AV guide](av-exclusions.md).
