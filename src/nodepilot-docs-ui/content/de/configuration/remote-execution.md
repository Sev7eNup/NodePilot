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

## Identität & Rechte auf dem Zielsystem

Welches Konto die WinRM-Verbindung trägt, hängt allein davon ab, ob für den Schritt ein Credential aufgelöst wurde — einen Auth-Modus-Schalter gibt es nicht.

| Fall | Die Verbindung läuft als |
|---|---|
| Credential am Node oder als Machine-Default hinterlegt | Das hinterlegte Konto (`Domain\User`) — Negotiate mit explizitem Passwort |
| Kein Credential, echte Zielmaschine | **Dienstidentität der API** — gMSA, bzw. Computerkonto `DOMAIN\HOST$` wenn der Dienst als LocalSystem läuft (integrierte Windows-Auth) |
| Kein Credential, Loopback-Hostname | Kein WinRM — der Node läuft engine-local im API-Prozess, damit ebenfalls unter der Dienstidentität (siehe Abschnitt oben) |

Die Credential-Auflösung ist **zweistufig**: Credential am Node → `DefaultCredentialId` der Maschine → Dienstidentität. Ein globales Default-Credential gibt es nicht. Wird ein Credential gelöscht, verliert die Maschine ihren Verweis darauf still und fällt auf die Dienstidentität zurück.

Auf dem Zielsystem braucht das verwendete Konto — Credential wie Dienstidentität — einen erreichbaren WinRM-Endpunkt und Zugriff darauf:

```powershell
Enable-PSRemoting -Force
winrm quickconfig -transport:https   # für Remote:RequireWinRmSsl=true
```

Zugriff auf den Endpunkt haben per Default nur lokale Administratoren und Mitglieder von `Remote Management Users`. Ohne eine dieser Mitgliedschaften scheitert der Schritt am Endpunkt, obwohl die Anmeldung selbst gelingt.

Der credential-lose Pfad braucht zusätzlich **resource-based constrained delegation** auf jeder Zielmaschine, damit die Dienstidentität per Kerberos durchreicht — Rezept unter [Produktions-Deployment](../deployment/production). Mit hinterlegten Credentials ist keine Delegation nötig; die Dienstidentität ist für den Zugriff dann irrelevant.

Der Verbindungstest einer Maschine (`POST /api/machines/{id}/test`) verlangt ein Credential und antwortet sonst `400 MACHINE_CREDENTIAL_REQUIRED`. Er kann den credential-losen Pfad also nicht prüfen — Workflow-Schritte laufen in dieser Lage trotzdem.

## Hardening

`Remote:RequireWinRmSsl` (default `true`) — WinRM ohne SSL wirft eine Exception. In Dev über `appsettings.Development.json` auf `false` relaxt. Siehe [Hardening-Flags](../security/hardening).

## REST-API-Proxy (für `restApi`-Activity)

`RestApi:Proxy:Enabled` (default `false`). Per-Step-Override via `proxyMode` (`default`/`direct`/`custom`), `proxyAddress`, `noProxy`. `RestApi:BlockPrivateNetworks` (default `true`) blockt RFC1918/Loopback. `RestApi:AllowedHosts` enthält ausschließlich exakte Hostnamen/IPs und ist für jedes initiale Ziel und Redirect-Ziel verpflichtend, das tatsächlich über einen Default- oder Custom-Proxy läuft. Die PowerShell-basierten `waitForCondition`-Modi `portOpen`/`httpOk` haben eine **eigene** Liste, `WaitForCondition:AllowedHosts` (default `["localhost"]`) — sie können das Ziel beim Verbindungsaufbau nicht erneut prüfen und sind deshalb bewusst getrennt, damit eine erlaubte Probe nicht zugleich `restApi` zu Loopback öffnet. Diese Liste entscheidet für beide Probe-Modi allein: `RestApi:BlockPrivateNetworks`/`RestApi:AllowedHosts` werden nicht mitgeprüft, eine Loopback-Probe braucht also keine restApi-Ausnahme. `direct` und durch `noProxy` umgangene Ziele bleiben durch die IP-Prüfung beim Verbindungsaufbau geschützt. Die Allowlist kann Private-/Loopback-Ziele freigeben, niemals jedoch Link-Local-/Cloud-Metadata-Adressen.
