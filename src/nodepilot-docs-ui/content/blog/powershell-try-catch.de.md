# PowerShell try/catch: Fehler erkennen und Aufträge zuverlässig abbrechen

PowerShell `try/catch` fängt terminierende Fehler ab. Viele Cmdlets melden Fehler zunächst nichtterminierend: Eine rote Fehlermeldung erscheint, während das Skript weiterläuft. Mit `-ErrorAction Stop` lässt sich ein solcher Fehler für den betreffenden Aufruf in den `catch`-Pfad bringen. Dort muss feststehen, ob der Auftrag wiederhergestellt werden kann oder als fehlgeschlagen enden soll.

Das ist bei geplanten Aufgaben besonders sichtbar. Eine Eingabedatei fehlt, der nächste Befehl schreibt trotzdem „Fertig“, und morgens muss jemand herausfinden, welche Aussage stimmt. Die folgenden Beispiele zeigen dieses Verhalten ohne Änderungen an Diensten, Konten oder Systemeinstellungen.

## Warum catch trotz Fehlermeldung nicht läuft

Dieses Beispiel erzeugt einen zufälligen Dateinamen im temporären Verzeichnis, legt die Datei aber nicht an. `Continue` steht hier ausdrücklich am Befehl, damit das Verhalten auch bei einer abweichenden Sitzungseinstellung nachvollziehbar ist.

```powershell
$missingPath = Join-Path ([IO.Path]::GetTempPath()) ('np-missing-' + [guid]::NewGuid() + '.json')
try {
    Get-Item -LiteralPath $missingPath -ErrorAction Continue
    'AFTER'
}
catch {
    'CAUGHT'
}
```

Neben der Fehlermeldung erscheint `AFTER`. `CAUGHT` erscheint nicht: Der nichtterminierende Fehler hat den `try`-Block nicht verlassen. Die Farbe einer Konsolenausgabe ist deshalb kein geeignetes Kriterium für den Erfolg eines Skripts.

## Den Fehler mit ErrorAction Stop abfangen

```powershell
$missingPath = Join-Path ([IO.Path]::GetTempPath()) ('np-missing-' + [guid]::NewGuid() + '.json')
try {
    Get-Item -LiteralPath $missingPath -ErrorAction Stop
    'AFTER'
}
catch {
    'CAUGHT'
    $_.Exception.Message
}
finally {
    'FINALLY'
}
```

Jetzt erscheinen `CAUGHT`, die Fehlermeldung und `FINALLY`; `AFTER` wird übersprungen. Im `catch`-Block bezeichnet `$_` den aktuellen Fehlerdatensatz. Für die Diagnose sind neben `Exception.Message` auch `FullyQualifiedErrorId`, `CategoryInfo` und `ScriptStackTrace` hilfreich.

`finally` ist für Aufräumarbeiten wie das Schließen eines selbst geöffneten Streams gedacht. Es läuft beim normalen Verlassen der Fehlerbehandlung auch nach einem Fehler. Ein hart beendeter Prozess kann solche Aufräumarbeiten jedoch nicht garantieren. Die Marker dienen hier der Demonstration; in einem Skript mit JSON-Ausgabe würden zusätzliche Textzeilen deren Verarbeitung stören. [Microsoft: try, catch und finally](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_try_catch_finally).

Für einen einzelnen kritischen Aufruf ist `-ErrorAction Stop` gut sichtbar. `$ErrorActionPreference = 'Stop'` setzt das Verhalten für nachfolgende Befehle im betreffenden Gültigkeitsbereich, soweit sie es nicht selbst überschreiben. Der gemeinsame Parameter behandelt nichtterminierende PowerShell-Fehler; daraus folgt keine allgemeine Auswertung beliebiger Programm-Exitcodes. [Microsoft: ErrorAction](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_commonparameters#erroraction).

## Ein vollständiges Skript für einen geplanten Prozess

Das folgende Skript liest einen kleinen JSON-Auftrag. Es akzeptiert ausschließlich `state: ready`, gibt bei Erfolg ein JSON-Ergebnis aus und beendet den Prozess mit `0`. Eine fehlende Datei, ungültiges JSON oder ein anderer Zustand führt zu `1`. Diese Zustandsregel ist ein bewusst einfaches Beispiel für eine fachliche Vorbedingung.

Als `Test-JobInput.ps1` speichern:

```powershell
param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath
)

$ErrorActionPreference = 'Stop'
try {
    $job = Get-Content -LiteralPath $InputPath -Raw -ErrorAction Stop |
        ConvertFrom-Json -ErrorAction Stop
    if ($job.state -ne 'ready') {
        throw 'Input state must be ready.'
    }

    [pscustomobject]@{
        inputPath = $InputPath
        state = 'accepted'
    } | ConvertTo-Json -Compress
    exit 0
}
catch {
    [Console]::Error.WriteLine(('Input check failed: ' + $_.Exception.Message))
    exit 1
}
```

Der Fehlerpfad schreibt auf Standardfehler und setzt einen eindeutigen Prozess-Exitcode. Das ist ein Einstiegsskript für einen separaten Prozess, keine Bibliotheksfunktion zum Dot-Sourcing in einer bestehenden Sitzung. Dateipfad, Laufzeit und Identität gehören ausdrücklich in die Aufgabenplanung. Einen entsprechenden Identitätsfall behandelt die [Anleitung zur geplanten Aufgabe mit gMSA](https://www.nodepilot.run/blog/gmsa-scheduled-task/).

Für den Test vier getrennte Eingaben verwenden:

| Eingabe | Erwartete Beobachtung |
| --- | --- |
| Vorhandene Datei mit `{"state":"ready"}` | JSON mit `state: accepted`, Prozesscode 0 |
| Nicht vorhandene Datei | Fehlermeldung, Prozesscode 1, keine Erfolgsausgabe |
| Vorhandene Datei mit ungültigem JSON | Parserfehler, Prozesscode 1 |
| Vorhandene Datei mit `{"state":"blocked"}` | Meldung zur Vorbedingung, Prozesscode 1 |

Die Fälle wurden mit dem Artikelcode unter Windows PowerShell 5.1 ausgeführt. Eine PowerShell-7-Ausführung oder ein Lauf unter eurem Dienstkonto ist damit nicht belegt. Die gewünschte Laufzeit lässt sich mit dem [Vergleich von PowerShell 5.1 und 7](https://www.nodepilot.run/blog/powershell-51-and-7/) gezielt prüfen.

## Ein catch-Block darf den Fehler nicht versehentlich verschlucken

Ein `catch`, das lediglich Text ausgibt, hat den Fehler behandelt. Danach kann das Skript normal weiterlaufen. Das ist sinnvoll, wenn eine vorgesehene Alternative tatsächlich funktioniert hat. Fehlt die notwendige Eingabe weiterhin, wäre eine Erfolgsmeldung falsch.

Für wiederverwendbare Skriptteile ist `throw` im `catch` meist die passende Weitergabe: Der Aufrufer entscheidet über den endgültigen Abbruch. Das Prozess-Einstiegsskript oben übersetzt den Fehler dagegen bewusst in `exit 1`. Nicht jeder Aufrufer bewertet diese beiden Signale auf dieselbe Weise.

In NodePilots `runScript`-Activity ist die Exitcode-Prüfung optional. Ohne `successExitCodes` entscheidet das Fehlerverhalten; ein nacktes `exit 1` ist daher kein verlässlicher Ersatz für `throw`. In einer Activity einen nicht behobenen Fehler weiterwerfen oder die erlaubten Exitcodes ausdrücklich konfigurieren und den gewählten Ausführungspfad prüfen. Die [Activity-Referenz](https://www.nodepilot.run/docs/de/activities-reference/) beschreibt diese Einstellungen. Ein erfolgreich beendeter Schritt belegt außerdem nur die Kriterien, die das Skript tatsächlich geprüft hat.

## Native Programme brauchen ihre eigene Erfolgsregel

Dieses Windows-Beispiel lässt einen neuen `cmd.exe`-Prozess kontrolliert mit `7` enden. Es führt keine Änderung am System aus.

```powershell
$ErrorActionPreference = 'Stop'
try {
    & $env:ComSpec /d /c 'exit 7'
    $nativeExitCode = $LASTEXITCODE
    if ($nativeExitCode -ne 0) {
        throw "Native command returned $nativeExitCode."
    }
}
catch {
    'CAUGHT: ' + $_.Exception.Message
}
```

`$LASTEXITCODE` unmittelbar nach dem Programmaufruf sichern. Ein späterer nativer Befehl kann den Wert überschreiben. Welche Codes Erfolg bedeuten, bestimmt das aufgerufene Programm; die Regel „nur 0“ ist hier Teil des Beispiels. Neuere PowerShell-Versionen können native Fehler zusätzlich über `$PSNativeCommandUseErrorActionPreference` behandeln. Das Verhalten hängt dann von Version und Einstellung ab; unter Windows PowerShell 5.1 wurde hier die ausdrückliche Codeprüfung getestet. [Microsoft: LASTEXITCODE](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_automatic_variables#lastexitcode).

Vor dem unbeaufsichtigten Betrieb sollten ein erfolgreicher Lauf, eine fehlende Voraussetzung und eine fehlerhafte Eingabe dasselbe Skript durchlaufen. Entscheidend sind jeweils Ausgabe, Fehler und beobachteter Status des Aufrufers. Erst diese Kombination zeigt, ob die Fehlerbehandlung zum tatsächlichen Auftrag passt.
