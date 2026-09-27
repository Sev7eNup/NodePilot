# PowerShell 5.1 und 7: Die Laufzeit gehört zum Auftrag

```powershell
[pscustomobject]@{
    Version = $PSVersionTable.PSVersion.ToString()
    Edition = $PSVersionTable.PSEdition
    Process = (Get-Process -Id $PID).Path
    Is64Bit = [Environment]::Is64BitProcess
}
```

Diese Abfrage gehört in den Prozess, der das Skript später tatsächlich ausführt. Ihre Ausgabe auf dem eigenen Arbeitsplatz beantwortet nicht, welche Laufzeit eine geplante Aufgabe, ein Dienst oder eine Remotesitzung verwendet.

Windows PowerShell 5.1 und PowerShell 7 können nebeneinander installiert sein. Dabei bezeichnet `powershell.exe` die Windows-PowerShell-Laufzeit, während PowerShell 7 über `pwsh.exe` gestartet wird. Eine zusätzliche Installation stellt bestehende Aufträge nicht automatisch um. [Microsoft: Migration von Windows PowerShell zu PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/whats-new/migrating-from-windows-powershell-51-to-powershell-7).

## Ein kleiner Vergleich mit klaren Grenzen

Für ein bestehendes Inventarskript lässt sich eine Prüfung aufbauen, ohne sofort den gesamten Betrieb zu verändern. Eine Kopie läuft gegen dieselben freigegebenen Testziele unter beiden Laufzeiten. Zu jedem Ergebnis werden die Modulversionen und die Identität festgehalten. Andernfalls könnte ein Berechtigungsunterschied fälschlich als Laufzeitproblem erscheinen.

| Prüfpunkt | Was verglichen wird |
|---|---|
| Modulimport | Lässt sich die vorgesehene Modulversion im tatsächlichen Prozess laden? |
| Ausgabe | Stimmen Eigenschaften, Datentypen und die Anzahl der Ergebnisse überein? |
| Fehlerfall | Erkennt die übergeordnete Steuerung fehlende Rechte und unerreichbare Ziele? |
| Dateiaustausch | Entsprechen Kodierung und Format den Erwartungen des Empfängers? |
| Unbeaufsichtigter Start | Funktioniert der Aufruf ohne das persönliche PowerShell-Profil? |

Die Tabelle ist eine Abnahmehilfe, keine Behauptung, dass jedes Skript an diesen Stellen scheitert. Sie lenkt die Prüfung auf jene Eigenschaften, die ein nachfolgender Prozess tatsächlich verwendet. Ein optisch gleiches Konsolenergebnis kann beispielsweise unterschiedliche Objekttypen verbergen.

## Ein importiertes Modul kann in einem anderen Prozess arbeiten

PowerShell 7 bietet unter Windows eine Kompatibilitätsfunktion für Windows-PowerShell-Module. Mit `Import-Module -UseWindowsPowerShell` kann ein Modul über eine Hintergrundsitzung in Windows PowerShell 5.1 eingebunden werden. Dabei kommt implizites Remoting zum Einsatz. Die zurückgegebenen Objekte können deserialisiert sein und damit ihre ursprünglichen Methoden verlieren. [Microsoft: Windows PowerShell Compatibility](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_windows_powershell_compatibility).

Für ein Skript, das nur ausgewählte Eigenschaften liest, kann das ausreichen. Ruft es dagegen Objektmethoden auf oder erwartet einen bestimmten lebenden .NET-Typ, muss genau dieser Teil geprüft werden. Ein erfolgreicher Import ist noch kein vollständiger Kompatibilitätsnachweis.

Im Prüfprotokoll sollte deshalb stehen, ob ein Modul nativ geladen wird oder über die Kompatibilitätssitzung arbeitet. Diese Unterscheidung erklärt später auch, weshalb zwei vermeintlich gleiche PowerShell-7-Aufträge unterschiedliche Voraussetzungen besitzen.

## Die Umstellung endet beim letzten Aufrufer

Nach erfolgreicher Prüfung wird der ausführende Prozess ausdrücklich umgestellt. Bei einer geplanten Aufgabe betrifft das den Programmpfad und die Argumente. Bei Remoting entscheidet der ausgewählte Endpunkt über die entfernte Laufzeit. Ein lokal gestartetes `pwsh.exe` erzwingt keine PowerShell-7-Sitzung auf dem Zielserver.

`Enable-PSRemoting` richtet Endpunkte für die Installation ein, in der der Befehl ausgeführt wird. Die Registrierung und die Berechtigung des vorgesehenen Endpunkts müssen daher Teil der Bereitstellung sein. [Microsoft: Enable-PSRemoting](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/enable-psremoting).

Für einen begrenzten Übergangszeitraum können geprüfte Aufträge unter PowerShell 7 laufen, während dokumentierte Abhängigkeiten unter 5.1 verbleiben. Zu dieser Ausnahme gehören ein Verantwortlicher und ein nachvollziehbarer Grund. Sie sollte nicht nur deshalb bestehen bleiben, weil niemand den alten Aufruf mehr zuordnen kann.

Auch bei einem in NodePilot eingebundenen Skript gehört die tatsächlich verwendete Laufzeit in die Abnahme. Maßgeblich ist der ausführende Prozess beziehungsweise Remote-Endpunkt, nicht die zuletzt auf dem Server installierte PowerShell-Version.
