# PowerShell Execution Policy: warum ein Skript nicht startet

Bei „running scripts is disabled on this system“ zuerst `Get-ExecutionPolicy -List` und `Get-ExecutionPolicy` im betroffenen PowerShell-Prozess ausführen. Die Liste zeigt die Gültigkeitsbereiche, der zweite Befehl die wirksame Richtlinie. Erst daraus lässt sich ableiten, ob eine lokale Einstellung, eine Gruppenrichtlinie oder die Herkunft der Datei untersucht werden muss.

Ein typischer Fall: Ein Skript funktioniert in der eigenen Konsole, aber nicht als geplante Aufgabe. Statt die ganze Maschine umzustellen, vergleichen wir Laufzeit, Identität, Richtlinie und dieselbe Skriptdatei. Der Artikel behandelt Windows PowerShell und PowerShell unter Windows.

## 1. Im richtigen Prozess nachsehen

```powershell
$PSVersionTable.PSVersion
$PSVersionTable.PSEdition
[Security.Principal.WindowsIdentity]::GetCurrent().Name
Get-ExecutionPolicy -List
Get-ExecutionPolicy
```

Diese Befehle lesen den aktuellen Kontext. Eine Ausgabe aus der Administratorkonsole belegt nicht die Einstellung des Kontos einer geplanten Aufgabe. Für einen Vergleich die Befunde unter der tatsächlich verwendeten Identität und Laufzeit erfassen. Windows PowerShell 5.1 und PowerShell 7 werden getrennt verwaltet; `powershell.exe` und `pwsh.exe` sind deshalb nicht austauschbar. [Microsoft: Get-ExecutionPolicy](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/get-executionpolicy) und [Set-ExecutionPolicy](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/set-executionpolicy).

Die Vorrangfolge lautet:

| Vorrang | Bereich | Geltung |
| ---: | --- | --- |
| 1 | MachinePolicy | Gruppenrichtlinie für den Computer |
| 2 | UserPolicy | Gruppenrichtlinie für den Benutzer |
| 3 | Process | Aktuelle PowerShell-Sitzung |
| 4 | CurrentUser | Aktueller Benutzer |
| 5 | LocalMachine | Lokale Computereinstellung |

`Undefined` bedeutet, dass dort keine Richtlinie festgelegt ist. Es bedeutet nicht, dass Skripte beliebig erlaubt wären. Entscheidend ist der erste festgelegte Bereich in dieser Reihenfolge; ohne Festlegung greifen plattformspezifische Standardwerte. Es gewinnt nicht automatisch die strengste Einstellung. [Microsoft: Execution Policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies).

## 2. Die konkrete Fehlermeldung einordnen

| Befund | Nächste Prüfung |
| --- | --- |
| Skriptausführung deaktiviert; wirksam ist Restricted | Ist Skriptausführung für diesen Rechner und diesen Auftrag vorgesehen? |
| Datei nicht digital signiert; wirksam ist RemoteSigned | Herkunftsmarkierung und Signatur genau dieser Datei prüfen |
| Datei nicht digital signiert; wirksam ist AllSigned | Ein freigegebenes, passend signiertes Artefakt verwenden |
| Lokale Änderung bleibt ohne Wirkung | MachinePolicy und UserPolicy sowie den wirklich gestarteten Prozess prüfen |
| Skript startet, Zugriff auf eine Freigabe scheitert | Dateirechte und Ausführungsidentität untersuchen; die Richtlinie erteilt keine SMB-Rechte |

`Restricted` verhindert Skriptdateien. `RemoteSigned` erlaubt lokale unsignierte Skripte, fordert aber für als Internetdownload markierte Skripte grundsätzlich eine vertrauenswürdige Signatur, sofern die Markierung nicht nach Prüfung entfernt wurde. `AllSigned` verlangt Signaturen auch bei lokal erstellten Skripten. Eine gültige Signatur ersetzt die Prüfung der gewünschten Wirkung nicht. [Microsoft: Skriptsignierung](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_signing).

## 3. Eine heruntergeladene Datei gezielt prüfen

`C:\Ops\Test-JobInput.ps1` ist ein Beispielpfad; vor dem Aufruf durch die tatsächlich betroffene Datei ersetzen.

```powershell
$scriptPath = 'C:\Ops\Test-JobInput.ps1'
Get-Item -LiteralPath $scriptPath -Stream *
Get-AuthenticodeSignature -LiteralPath $scriptPath |
    Select-Object Status, StatusMessage
```

Auf einem Dateisystem mit alternativen Datenströmen kann `Zone.Identifier` die Downloadherkunft kennzeichnen. Nicht jeder Übertragungsweg bewahrt diese Information. Eine fehlende Markierung ist deshalb kein Vertrauensnachweis.

Nach Prüfung von Herkunft, Inhalt und betrieblicher Freigabe lässt sich die Markierung einer einzelnen Datei entfernen. Die erste Zeile zeigt nur die beabsichtigte Aktion, die zweite verändert die Datei:

```powershell
Unblock-File -LiteralPath $scriptPath -WhatIf
Unblock-File -LiteralPath $scriptPath
```

`Unblock-File` ändert keine Execution Policy und erzeugt keine Signatur. Unter `AllSigned` löst es eine fehlende Signatur daher nicht. Keine ungeprüfte Downloadsammlung pauschal entsperren. [Microsoft: Unblock-File](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file).

## 4. Eine lokale Einstellung bewusst wählen

Wenn keine übergeordnete Richtlinie greift und für den Benutzer `RemoteSigned` betrieblich vorgesehen ist, lautet die gezielte Einstellung:

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
Get-ExecutionPolicy -List
Get-ExecutionPolicy
```

Der erste Befehl verändert die Einstellung dauerhaft für diesen Benutzer. Den vorherigen Wert dokumentieren, den vorgesehenen Bereich ausdrücklich nennen und das Ergebnis anschließend prüfen. Ohne `-Scope` würde `Set-ExecutionPolicy` standardmäßig `LocalMachine` betreffen. Eine Gruppenrichtlinie wird durch eine lokale Änderung nicht überstimmt; in diesem Fall gehört die Anpassung in die verwaltete Richtlinie. [Microsoft: Set-ExecutionPolicy](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/set-executionpolicy).

`Bypass` schaltet die entsprechenden Prüfungen aus. Es erklärt weder die Ursache noch ersetzt es eine passende Bereitstellung. Die Execution Policy selbst ist keine Sicherheitsgrenze und kein Ersatz für Zugriffsrechte oder Anwendungskontrolle. [Microsoft: Execution Policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies).

## 5. Den ursprünglichen Auftrag erneut prüfen

Für eine geplante Aufgabe nach der Korrektur denselben ausführbaren Pfad, dieselben Argumente, dieselbe Skriptdatei und dasselbe Konto verwenden. Zuerst muss das Skript tatsächlich starten. Danach braucht es seine eigene Fehlerbehandlung und Ergebnisprüfung: [PowerShell try/catch](https://www.nodepilot.run/blog/powershell-try-catch/) und die [gMSA-Aufgabenanleitung](https://www.nodepilot.run/blog/gmsa-scheduled-task/) behandeln diese nächsten Schritte. Beim Wechsel der Laufzeit hilft der [Vergleich von PowerShell 5.1 und 7](https://www.nodepilot.run/blog/powershell-51-and-7/).

Die lesenden Prüfungen sowie Downloadmarkierung und gezieltes Entsperren wurden unter Windows PowerShell 5.1 an einer eigenen temporären Testdatei geprüft. Eine dauerhafte Richtlinienänderung wurde dabei nicht vorgenommen. Gruppenrichtlinienfälle und PowerShell 7 sind anhand der Microsoft-Dokumentation beschrieben, nicht als lokal durchgeführte Tests dargestellt.
