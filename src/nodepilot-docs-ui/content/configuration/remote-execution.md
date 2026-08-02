# Remote-Execution

Remote-Activities werden ohne zusätzlichen Agent über WinRM ausgeführt. `Remote:Provider` wählt den Provider.

## Provider

| Provider | Wert | Verhalten |
|---|---|---|
| WinRM | `winrm` | PowerShell-SDK / WinRM-Sessions auf die Zielmaschine |
| NoOp | `noop` | Keine Remote-Ausführung — muss mit `Remote:AllowNoop=true` bzw. `NODEPILOT_ALLOW_NOOP_REMOTE=1` quittiert werden, sonst Boot-Abbruch |

## Localhost-Bypass & Self-Managed-Remoting

Ohne gesetzte Maschine (bzw. `targetMachineId: "localhost"` ohne Credential) läuft `runScript` engine-local im API-Host statt über eine managed WinRM-Session. **Produkt-Feature, kein Guard einziehen.** Ideal für Demos — und der Escape-Hatch für den SCOrch-Stil: das Script kann die Remote-Verbindung **selbst** aufbauen (`Invoke-Command -ComputerName SRV01 -Credential $c { … }` / `New-PSSession`), z. B. für dynamische Ziellisten oder Fan-out auf N Maschinen in einem Node.

Trade-offs beim Self-Managen: läuft auf dem **API-Host** (der braucht Netz-/WinRM-Zugriff selbst); der DPAPI-Credential-Store ist **nicht** verdrahtet (`PSCredential` im Script bauen, Secret via `{{globals.NAME}}`); kein Machine-Targeting/-Test/-Audit; und Hardening wie `Remote:RequireWinRmSsl` + der Session-Pool **greifen nicht** — die hängen am managed WinRM-Pfad.

## WinRM-Auth in Produktion

`NegotiateWithImplicitCredential` in `WinRmSessionFactory.cs` erlaubt Kerberos zur Zielmaschine ohne gespeicherte Credentials — vorausgesetzt resource-based constrained delegation ist konfiguriert (gMSA-Identity).

## Hardening

`Remote:RequireWinRmSsl` (default `true`) — WinRM ohne SSL wirft eine Exception. In Dev über `appsettings.Development.json` auf `false` relaxt. Siehe [Hardening-Flags](../security/hardening).

## REST-API-Proxy (für `restApi`-Activity)

`RestApi:Proxy:Enabled` (default `false`). Per-Step-Override via `proxyMode` (`default`/`direct`/`custom`), `proxyAddress`, `noProxy`. `RestApi:BlockPrivateNetworks` (default `true`) blockt RFC1918/Loopback. `RestApi:AllowedHosts` enthält ausschließlich exakte Hostnamen/IPs und ist für jedes initiale Ziel und Redirect-Ziel verpflichtend, das tatsächlich über einen Default- oder Custom-Proxy läuft. Die PowerShell-basierten `waitForCondition`-Modi `portOpen`/`httpOk` haben eine **eigene** Liste, `WaitForCondition:AllowedHosts` (default `["localhost"]`) — sie können das Ziel beim Verbindungsaufbau nicht erneut prüfen und sind deshalb bewusst getrennt, damit eine erlaubte Probe nicht zugleich `restApi` zu Loopback öffnet. `direct` und durch `noProxy` umgangene Ziele bleiben durch die IP-Prüfung beim Verbindungsaufbau geschützt. Die Allowlist kann Private-/Loopback-Ziele freigeben, niemals jedoch Link-Local-/Cloud-Metadata-Adressen.
