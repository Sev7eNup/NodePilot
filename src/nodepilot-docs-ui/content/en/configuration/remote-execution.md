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

## WinRM authentication in production

`NegotiateWithImplicitCredential` in `WinRmSessionFactory.cs` allows Kerberos to the target machine without stored credentials — provided resource-based constrained delegation is configured (gMSA identity).

## Hardening

`Remote:RequireWinRmSsl` (default `true`) — WinRM without SSL throws an exception. Relaxed to `false` in development through `appsettings.Development.json`. See [Hardening flags](../security/hardening).

## REST API proxy (for the `restApi` activity)

`RestApi:Proxy:Enabled` (default `false`). Per-step override via `proxyMode` (`default`/`direct`/`custom`), `proxyAddress`, `noProxy`. `RestApi:BlockPrivateNetworks` (default `true`) blocks RFC 1918/loopback. `RestApi:AllowedHosts` contains exact host names/IPs only and is mandatory for every initial target and redirect target that actually goes through a default or custom proxy. The PowerShell-based `waitForCondition` modes `portOpen`/`httpOk` have their **own** list, `WaitForCondition:AllowedHosts` (default `["localhost"]`) — they cannot re-check the target when the connection is established and are therefore deliberately kept separate, so that a permitted probe does not also open `restApi` to loopback. That list alone decides for both probe modes: `RestApi:BlockPrivateNetworks`/`RestApi:AllowedHosts` are not consulted, so a loopback probe needs no restApi exception. `direct` targets, and targets bypassed through `noProxy`, remain protected by the IP check when the connection is established. The allow-list can permit private/loopback targets, but never link-local or cloud-metadata addresses.
