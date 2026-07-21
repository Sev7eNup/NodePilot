# Remote-Execution

Agentless via WinRM. `Remote:Provider`: `winrm` (default) | `noop`.

## Provider

| Provider | Wert | Verhalten |
|---|---|---|
| WinRM | `winrm` | PowerShell-SDK / WinRM-Sessions auf die Zielmaschine |
| NoOp | `noop` | Keine Remote-Ausführung — muss mit `Remote:AllowNoop=true` bzw. `NODEPILOT_ALLOW_NOOP_REMOTE=1` quittiert werden, sonst Boot-Abbruch |

## Localhost-Bypass

Ohne Credentials läuft `runScript` in-process. **Produkt-Feature, kein Guard einziehen.** Ideal für Demos (`targetMachineId: "localhost"`).

## WinRM-Auth in Produktion

`NegotiateWithImplicitCredential` in `WinRmSessionFactory.cs` erlaubt Kerberos zur Zielmaschine ohne gespeicherte Credentials — vorausgesetzt resource-based constrained delegation ist konfiguriert (gMSA-Identity).

## Hardening

`Remote:RequireWinRmSsl` (default `true`) — WinRM ohne SSL wirft eine Exception. In Dev über `appsettings.Development.json` auf `false` relaxt. Siehe [Hardening-Flags](../security/hardening).

## REST-API-Proxy (für `restApi`-Activity)

`RestApi:Proxy:Enabled` (default `false`). Per-Step-Override via `proxyMode` (`default`/`direct`/`custom`), `proxyAddress`, `noProxy`. `RestApi:BlockPrivateNetworks` (default `true`) blockt RFC1918/Loopback. `RestApi:AllowedHosts` enthält ausschließlich exakte Hostnamen/IPs. Die Liste ist für die PowerShell-basierten `waitForCondition`-Modi `portOpen`/`httpOk` sowie für jedes initiale Ziel und Redirect-Ziel verpflichtend, das tatsächlich über einen Default- oder Custom-Proxy läuft. `direct` und durch `noProxy` umgangene Ziele bleiben durch die IP-Prüfung beim Verbindungsaufbau geschützt. Die Allowlist kann Private-/Loopback-Ziele freigeben, niemals jedoch Link-Local-/Cloud-Metadata-Adressen.
