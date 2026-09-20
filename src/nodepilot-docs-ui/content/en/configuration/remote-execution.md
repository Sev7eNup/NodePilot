# Remote execution

Remote activities are executed over WinRM without an additional agent. `Remote:Provider` selects the provider.

## Providers

| Provider | Value | Behaviour |
|---|---|---|
| WinRM | `winrm` | PowerShell SDK / WinRM sessions to the target machine |
| NoOp | `noop` | No remote execution — has to be acknowledged with `Remote:AllowNoop=true` or `NODEPILOT_ALLOW_NOOP_REMOTE=1`, otherwise the boot is aborted |

## Localhost bypass & self-managed remoting

Without a machine set (or with `targetMachineId: "localhost"` and no credential), `runScript` runs engine-local in the API host instead of through a managed WinRM session. **This is a product feature; do not introduce a guard against it.** Ideal for demos — and the escape hatch for the SCOrch style: the script can establish the remote connection **itself** (`Invoke-Command -ComputerName SRV01 -Credential $c { … }` / `New-PSSession`), for example for dynamic target lists or fan-out to N machines from one node.

The trade-offs of managing it yourself: it runs on the **API host** (which then needs network/WinRM access itself); the DPAPI credential store is **not** wired up (build the `PSCredential` in the script, take the secret from `{{globals.NAME}}`); there is no machine targeting, testing or auditing; and hardening such as `Remote:RequireWinRmSsl` and the session pool **do not apply** — those hang off the managed WinRM path.

## Identity and rights on the target system

Which account carries the WinRM connection depends solely on whether a credential was resolved for the step — there is no authentication-mode switch.

| Case | The connection runs as |
|---|---|
| Credential set on the node or as the machine default | The stored account (`Domain\User`) — Negotiate with an explicit password |
| No credential, real target machine | **The service identity of the API** — the gMSA, or the computer account `DOMAIN\HOST$` when the service runs as LocalSystem (integrated Windows authentication) |
| No credential, loopback host name | No WinRM at all — the node runs engine-local in the API process and therefore also under the service identity (see the section above) |

Credential resolution has **two levels**: credential on the node → the machine's `DefaultCredentialId` → the service identity. There is no global default credential. Deleting a credential silently clears the machine's reference to it, and the machine falls back to the service identity.

On the target system the account in use — stored credential or service identity alike — needs a reachable WinRM endpoint and access to it:

```powershell
Enable-PSRemoting -Force
winrm quickconfig -transport:https   # for Remote:RequireWinRmSsl=true
```

By default only local administrators and members of `Remote Management Users` may use that endpoint. Without one of those memberships the step fails at the endpoint even though the sign-in itself succeeds.

The credential-less path additionally requires **resource-based constrained delegation** on every target machine so that the service identity is passed on over Kerberos — recipe under [Production deployment](../deployment/production). With stored credentials no delegation is needed; the service identity is then irrelevant for access.

A machine's connection test (`POST /api/machines/{id}/test`) requires a credential and otherwise answers `400 MACHINE_CREDENTIAL_REQUIRED`. It therefore cannot exercise the credential-less path — workflow steps still run in that situation.

## Hardening

`Remote:RequireWinRmSsl` (default `true`) — WinRM without SSL throws an exception. Relaxed to `false` in development through `appsettings.Development.json`. See [Hardening flags](../security/hardening).

## REST API proxy (for the `restApi` activity)

`RestApi:Proxy:Enabled` (default `false`). Per-step override via `proxyMode` (`default`/`direct`/`custom`), `proxyAddress`, `noProxy`. `RestApi:BlockPrivateNetworks` (default `true`) blocks RFC 1918/loopback. `RestApi:AllowedHosts` contains exact host names/IPs only and is mandatory for every initial target and redirect target that actually goes through a default or custom proxy. The PowerShell-based `waitForCondition` modes `portOpen`/`httpOk` have their **own** list, `WaitForCondition:AllowedHosts` (default `["localhost"]`) — they cannot re-check the target when the connection is established and are therefore deliberately kept separate, so that a permitted probe does not also open `restApi` to loopback. That list alone decides for both probe modes: `RestApi:BlockPrivateNetworks`/`RestApi:AllowedHosts` are not consulted, so a loopback probe needs no restApi exception. `direct` targets, and targets bypassed through `noProxy`, remain protected by the IP check when the connection is established. The allow-list can permit private/loopback targets, but never link-local or cloud-metadata addresses.
