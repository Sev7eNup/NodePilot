# Ein PowerShell-Skript als geplante Aufgabe mit gMSA ausführen

Eine geplante PowerShell-Aufgabe kann unter einem gruppenverwalteten Dienstkonto laufen, ohne dass jemand dessen Kennwort einträgt. Dazu den Principal als `DOMÄNE\Konto$` mit `-LogonType Password` konfigurieren, die ausführbare Datei ausdrücklich wählen und den Auftrag unter dieser Identität prüfen. `Password` bezeichnet den Anmeldetyp, keinen einzutragenden Kennwortwert.

Diese Anleitung schreibt eine kleine JSON-Datei auf eine SMB-Freigabe und liest sie zurück. Konto, Laufzeit und Ergebnis werden lokal protokolliert. So lässt sich die erfolgreiche Registrierung von einem tatsächlich erfolgreichen Export unterscheiden.

Kontotest, Aufgabenregistrierung sowie erfolgreicher und verweigerter SMB-Zugriff wurden am 25. Oktober 2026 auf Windows Server 2025 mit Windows PowerShell 5.1.26100.1591 ausgeführt. Die Aufgabe lief mit `RunLevel Limited` ohne interaktive Benutzersitzung. Namen und Pfade unten sind Beispielwerte. PowerShell 7 und ältere Windows-Server-Versionen waren nicht Teil dieses Tests.

## Konto und drei getrennte Berechtigungen vorbereiten

Vorausgesetzt werden ein vorhandenes gMSA, ein berechtigter Ausführungshost in der Domäne und das ActiveDirectory-Modul. Die [gMSA-Grundlagen](/blog/gmsa-service-accounts/) erklären Kontoanlage und KDS-Voraussetzungen. Auf dem Ausführungshost eine erhöhte Windows-PowerShell-Sitzung öffnen:

```powershell
Import-Module ActiveDirectory
Install-ADServiceAccount -Identity 'gmsaExport'
Test-ADServiceAccount -Identity 'gmsaExport'
```

Erst bei `True` fortfahren. Bei einem Fehler zunächst die Hostfreigabe und die Voraussetzungen aus den [gMSA-Grundlagen](/blog/gmsa-service-accounts/) prüfen. Microsoft beschreibt den Test des Kontos auf dem lokalen Computer; die Exportfreigabe wird damit nicht geprüft: [Test-ADServiceAccount](https://learn.microsoft.com/en-us/powershell/module/activedirectory/test-adserviceaccount?view=windowsserver2025-ps).

| Berechtigung | Empfänger | Zweck |
|---|---|---|
| Verwaltetes Kennwort abrufen | Computerkonto des Ausführungshosts, häufig über eine Gruppe | gMSA auf diesem Host verwenden |
| Anmelden als Stapelverarbeitungsauftrag | `CONTOSO\gmsaExport$` | Geplanten Prozess starten |
| Skript, Protokoll und Ziel öffnen | `CONTOSO\gmsaExport$` | Diesen konkreten Auftrag ausführen |

Die wirksamen Richtlinien **Anmelden als Stapelverarbeitungsauftrag** und **Anmelden als Stapelverarbeitungsauftrag verweigern** einschließlich Gruppenmitgliedschaften prüfen. Eine nötige Freigabe in der für den Host zuständigen Richtlinie ergänzen; die vorhandene Kontenliste nicht ersetzen. Eine Verweigerung hat Vorrang. Siehe Microsofts [Dokumentation zum Stapelanmelderecht](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/log-on-as-a-batch-job).

Eine eigene Testfreigabe ohne Produktivdaten vorbereiten, hier `\\files01.contoso.com\GmsaProbe`. Das gMSA erhält auf der Freigabe **Ändern** und auf ihrem Testverzeichnis das NTFS-Recht **Ändern**. Beide Ebenen prüfen. Das Konto braucht dafür keine Mitgliedschaft in den lokalen Administratoren. Einen DNS-Namen und einen UNC-Pfad verwenden, kein Laufwerk aus der persönlichen Sitzung.

Auf dem Ausführungshost ein neues Verzeichnis `C:\Ops\GmsaProbe` mit dem Unterverzeichnis `Results` anlegen. Die ACLs wie folgt konfigurieren; Skript und beschreibbare Ausgabe bleiben getrennt:

| Lokaler Pfad | SYSTEM und Administratoren | gMSA |
|---|---|---|
| `C:\Ops\GmsaProbe` und das Skript | Vollzugriff | Lesen und Ausführen |
| `C:\Ops\GmsaProbe\Results` | Vollzugriff | Ändern |

Geerbte Schreibrechte gewöhnlicher Benutzer auf diesem eigenen Testverzeichnis entfernen. Das gMSA darf sein Skript nicht selbst ersetzen können. Das lokale Protokollverzeichnis muss auch bei nicht erreichbarem Netzwerkziel beschreibbar bleiben.

## Eine Probe mit eigenem Ergebnisprotokoll speichern

Das folgende Skript als `C:\Ops\GmsaProbe\Probe.ps1` speichern. Dabei die Ausführungsrichtlinie und den Signierungsprozess der eigenen Umgebung einhalten. Das Beispiel ändert keine Execution Policy und setzt keinen Bypass-Schalter.

```powershell
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$runId = [guid]::NewGuid().ToString()
$record = [ordered]@{
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('o')
    identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    powershell = $PSVersionTable.PSVersion.ToString()
    executable = (Get-Process -Id $PID).Path
    destination = $Destination
    success = $false
    error = $null
}
$exitCode = 1
try {
    $target = Join-Path $Destination ($runId + '.json')
    $payload = [ordered]@{ runId = $runId; identity = $record.identity }
    $payload | ConvertTo-Json | Set-Content -LiteralPath $target -Encoding UTF8
    $readBack = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
    if ($readBack.runId -ne $runId) { throw 'Read-back does not match this run.' }
    $record.success = $true
    $exitCode = 0
} catch {
    $record.error = $_.Exception.Message
} finally {
    $reportPath = Join-Path $PSScriptRoot ('Results\' + $runId + '.json')
    $record | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
exit $exitCode
```

Jeder Versuch erhält eine eigene Lauf-ID. Fehlt das lokale Protokoll, müssen Ausführung oder Protokollzugriff untersucht werden; das ist kein leerer Erfolgsfall. Für einen echten Export die Probe durch fachliche Datenprüfungen ersetzen und eine Aufbewahrungsfrist für Ausgabe und Protokolle festlegen.

## Die Aufgabe ohne Kennworteingabe registrieren

Die folgenden Befehle als Administrator auf dem Ausführungshost ausführen. Domäne und Ziel vorher anpassen:

```powershell
$action = New-ScheduledTaskAction `
    -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument '-NoProfile -NonInteractive -File "C:\Ops\GmsaProbe\Probe.ps1" -Destination "\\files01.contoso.com\GmsaProbe"' `
    -WorkingDirectory 'C:\Ops\GmsaProbe'
$principal = New-ScheduledTaskPrincipal `
    -UserId 'CONTOSO\gmsaExport$' -LogonType Password -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'GmsaExportProbe' `
    -Action $action -Principal $principal -Settings $settings
```

Die Aufgabe hat zunächst keinen Zeittrigger: zuerst einen gezielten Testlauf abnehmen. Ein `-Force`, das eine vorhandene Aufgabe still ersetzen könnte, fehlt ebenfalls. Die Grenze von zwei Minuten gilt für diese kleine Probe, nicht pauschal für produktive Exporte.

`ServiceAccount` ist nicht allein deshalb der passende Anmeldetyp, weil gMSA ein Dienstkonto ist: Microsoft beschreibt diesen Wert für LocalSystem, LocalService und NetworkService. Auch `S4U` hat andere Netzwerkzugriffseigenschaften. Siehe [Principal.LogonType](https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-logontype) und [New-ScheduledTaskPrincipal](https://learn.microsoft.com/en-us/powershell/module/scheduledtasks/new-scheduledtaskprincipal?view=windowsserver2025-ps). Die Kombination aus gMSA und `Password` wurde oben ohne Kennwortparameter im Labor geprüft.

Diese Aufgabe startet ausdrücklich Windows PowerShell 5.1. Für einen PowerShell-7-Auftrag den installierten `pwsh.exe`-Pfad wählen und die Abnahme mit dessen Modulen wiederholen. Die Installation von PowerShell 7 ändert die ausführbare Datei einer bestehenden Aufgabe nicht. Der [Laufzeitvergleich](/blog/powershell-51-and-7/) erklärt die nötigen Prüfungen.

## Einen neuen Lauf statt eines alten Ergebnisses prüfen

Vorherige Laufzeit merken, Aufgabe starten und auf den Abschluss dieses neuen Laufs warten:

```powershell
$previousRun = (Get-ScheduledTaskInfo -TaskName 'GmsaExportProbe').LastRunTime
Start-ScheduledTask -TaskName 'GmsaExportProbe'
$deadline = (Get-Date).AddSeconds(45)
do {
    Start-Sleep -Milliseconds 400
    $info = Get-ScheduledTaskInfo -TaskName 'GmsaExportProbe'
    $task = Get-ScheduledTask -TaskName 'GmsaExportProbe'
} while (($info.LastRunTime -le $previousRun -or $task.State -eq 'Running') `
    -and (Get-Date) -lt $deadline)
if ($info.LastRunTime -le $previousRun -or $task.State -eq 'Running') {
    throw 'No completed new run within 45 seconds; inspect the task.'
}
$info | Select-Object LastRunTime, LastTaskResult
```

Die 45-Sekunden-Grenze beendet nur diese Beobachtung; sie bricht die Aufgabe nicht ab. Im entstandenen lokalen JSON `identity`, `executable`, `success` und `error` prüfen. Die `runId` muss zur Datei auf der Freigabe passen. Dort den Inhalt und auf dem Dateiserver den Eigentümer kontrollieren. Ein Ergebnis von `0` hat hier Aussagekraft, weil die Probe es erst nach Schreiben und Zurücklesen ihrer eigenen Datei liefert.

Im Labor entstanden diese gegensätzlichen Ergebnisse:

| Fall | Kontotest | Aufgabenergebnis | Beleg |
|---|---|---|---|
| gMSA hat Zugriff auf das Ziel | `True` | `0` | Passende Datei, gMSA als Eigentümer, lokales Erfolgsprotokoll |
| gMSA fehlen NTFS-Rechte am Testziel | `True` | `1` | Lokales Protokoll nennt den verweigerten Zugriff |

Beide Läufe verwendeten das gMSA ohne Administratormitgliedschaft. Die Zielberechtigung änderte das Auftragsergebnis, nicht den Kontotest. Für eine Gegenprobe ein eigenes Testverzeichnis verwenden; einem produktiven Auftrag keine Rechte zum Nachstellen entziehen.

Erst nach der Abnahme einen Zeitplan ergänzen, beispielsweise:

```powershell
$trigger = New-ScheduledTaskTrigger -Daily -At '02:00'
Set-ScheduledTask -TaskName 'GmsaExportProbe' -Trigger $trigger
```

Die lokale Zeitzone des Hosts und den gewünschten Umgang mit verpassten Starts prüfen. Dieses Beispiel aktiviert kein Nachholen.

## Die Probe entfernen und die Nachweise aufbewahren

Die nicht mehr benötigte Probe deaktivieren, ihren Zustand prüfen und bei Bedarf nur diese Testaufgabe stoppen. Anschließend die Registrierung entfernen:

```powershell
Disable-ScheduledTask -TaskName 'GmsaExportProbe'
Get-ScheduledTask -TaskName 'GmsaExportProbe' | Select-Object TaskName, State
# Only if this test task is still running:
# Stop-ScheduledTask -TaskName 'GmsaExportProbe'
Unregister-ScheduledTask -TaskName 'GmsaExportProbe' -Confirm:$true
```

Testprotokolle archivieren und die eigenen Testdateien, Freigabe und Rechte entfernen, sobald sie nicht mehr gebraucht werden. Das gMSA erst nach Prüfung seiner übrigen Verbraucher deinstallieren oder entfernen.

Für einen unabhängigen Auftrag kann die Aufgabenplanung ausreichen. Kommen Abhängigkeiten, Wiederholungen und eine gemeinsame Betriebshistorie hinzu, sind [PowerShell-Workflows in NodePilot](/powershell-automation/) ein passender nächster Schritt. Ausführungsidentität und Zielberechtigungen auch dort im tatsächlichen Lauf erneut prüfen.
