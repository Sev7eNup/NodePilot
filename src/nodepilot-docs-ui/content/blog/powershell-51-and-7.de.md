# PowerShell 5.1 und 7: Die Laufzeit gehört zum Auftrag

Eine zusätzliche PowerShell-7-Installation stellt bestehende Skriptaufrufe nicht automatisch um. Windows PowerShell 5.1 startet über `powershell.exe`, PowerShell 7 über `pwsh.exe`. Beide können nebeneinander laufen, während eine geplante Aufgabe weiterhin die bisherige Laufzeit verwendet. [Microsoft: Migration zu PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/whats-new/migrating-from-windows-powershell-51-to-powershell-7).

## Die Laufzeit am Ausführungsort feststellen

Die folgende Abfrage gehört in den tatsächlichen Auftrag, beispielsweise als vorübergehende Diagnoseausgabe einer geplanten Aufgabe. Sie erfasst auch das ausführende Konto, dessen Berechtigungen das Ergebnis beeinflussen können.

```powershell
[pscustomobject]@{
    Version = $PSVersionTable.PSVersion.ToString()
    Edition = $PSVersionTable.PSEdition
    Process = (Get-Process -Id $PID).Path
    Account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    Is64Bit = [Environment]::Is64BitProcess
}
```

Unter 5.1 steht bei `Edition` der Wert `Desktop`, unter 7 der Wert `Core`. Prozesspfad und Architektur helfen, abweichende Aufrufe einzugrenzen. Eine Prüfung im eigenen Terminal bestätigt allerdings nur dessen Umgebung.

## Dasselbe Skriptergebnis unter 5.1 und 7 vergleichen

Für einen ersten Vergleich genügt ein kleines Inventarskript. Als `Inventory.ps1` gespeichert, liest es den lokalen Computernamen sowie Bezeichnung und Version des Betriebssystems. Es verändert keine Systemeinstellungen und gibt die ausgewählten Felder als JSON zurück.

```powershell
$ErrorActionPreference = 'Stop'
Get-CimInstance -ClassName Win32_OperatingSystem |
    Select-Object CSName, Caption, Version |
    ConvertTo-Json -Compress
```

Der folgende Aufruf läuft auf demselben Windows-Rechner im Verzeichnis der Datei. Beide PowerShell-Versionen müssen über `PATH` erreichbar sein. `-NoProfile` verhindert Einflüsse persönlicher Profile, `-NonInteractive` unterbindet interaktive Rückfragen.

```powershell
$ErrorActionPreference = 'Stop'
$ps51 = & powershell.exe -NoProfile -NonInteractive -File .\Inventory.ps1
if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell failed.' }

$ps7 = & pwsh.exe -NoProfile -NonInteractive -File .\Inventory.ps1
if ($LASTEXITCODE -ne 0) { throw 'PowerShell 7 failed.' }

Compare-Object ($ps51 | ConvertFrom-Json) ($ps7 | ConvertFrom-Json) `
    -Property CSName, Caption, Version -CaseSensitive
```

Ohne Ausgabe stimmen die drei verglichenen Werte überein. Abweichungen erscheinen mit `<=` für die 5.1-Seite und `=>` für die 7-Seite. Das prüft die ausgewählten Daten nach der JSON-Konvertierung, nicht die ursprünglichen .NET-Objekttypen. [Microsoft: Compare-Object](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/compare-object).

Beim eigenen Skript gehören auch Modulversionen, fehlende Zugriffsrechte und unerreichbare Ziele in die Prüfung. Dateiausgaben brauchen zusätzlich einen Vergleich von Format und Kodierung. Ein erfolgreicher Lauf allein deckt diese Fälle nicht ab.

Besondere Aufmerksamkeit brauchen Module, die mit `Import-Module -UseWindowsPowerShell` geladen werden. Sie laufen in einer lokalen Windows-PowerShell-5.1-Hintergrundsitzung. Durch die Serialisierung können zurückgegebene Objekte ihre Methoden verlieren, obwohl das Lesen ihrer Eigenschaften funktioniert. Ein erfolgreicher Import bestätigt daher noch nicht die Kompatibilität des Skripts. [Microsoft: Windows PowerShell Compatibility](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_windows_powershell_compatibility).

## Den Remote-Endpunkt ausdrücklich wählen

Bei Remoting über WinRM bestimmt der Endpunkt die entfernte Laufzeit. Das folgende Beispiel setzt einen bereits registrierten Endpunkt `PowerShell.7`, erreichbares WinRM und die erforderlichen Zugriffsrechte voraus. `srv-app01` ist durch den vorgesehenen Testhost zu ersetzen.

```powershell
Invoke-Command -ComputerName 'srv-app01' `
    -ConfigurationName 'PowerShell.7' -ErrorAction Stop `
    -ScriptBlock { $PSVersionTable.PSVersion.ToString() }
```

Erwartet wird eine Versionsnummer aus der Reihe `7.x`. Ein lokal gestartetes `pwsh.exe` reicht dafür nicht aus. Die Einrichtung erfolgt auf dem Ziel mit `Enable-PSRemoting` aus der vorgesehenen PowerShell-Version und mit Administratorrechten. Dabei werden auch WinRM und Firewallregeln konfiguriert. [Microsoft: Enable-PSRemoting](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/enable-psremoting).

Für NodePilot gilt die Engine-Auswahl nur bei lokalen Skriptschritten:

| Ausführung | Verwendete Laufzeit |
|---|---|
| Lokal, `engine: auto` | Standardmäßig Windows PowerShell 5.1 |
| Lokal, `engine: powershell` | Ausdrücklich Windows PowerShell 5.1 |
| Lokal, `engine: pwsh` | Separater PowerShell-7-Prozess, Installation erforderlich |
| Lokal, `engine: runspace` | Eingebettete PowerShell 7 mit Einschränkungen bei der Modulnutzung |
| Remote über NodePilot | Derzeit Windows PowerShell 5.1 über `Microsoft.PowerShell` |

Das Remoting-Beispiel zeigt somit einen eigenständigen PowerShell-Aufruf. Eine PowerShell-7-Installation auf dem Ziel ändert die Remote-Ausführung in NodePilot nicht. Einzelheiten stehen in der [Activity-Referenz](https://www.nodepilot.run/docs/de/activities-reference/).
