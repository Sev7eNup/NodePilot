# Ein fehlgeschlagener Lauf, vollständig rekonstruiert

```text
02:14:03  run=EX-1042  phase=read-source   result=ok
02:14:05  run=EX-1042  phase=write-export  result=AccessDenied
02:14:05  run=EX-1042  phase=complete      result=failed
```

Diese drei Zeilen stammen aus einem konstruierten Diagnosefall. Rechnernamen, Kennung und Beobachtungen dienen der Erklärung. Es handelt sich weder um einen Kundenvorfall noch um einen gemessenen Testlauf.

Im Beispiel startet ein geplanter Export auf `srv-job01`. Er liest seine Quelle erfolgreich, scheitert aber beim Schreiben nach `\\srv-file01\Exports\Finance`. Ein Administrator führt das Skript später interaktiv aus und erhält eine Datei. Die erste Vermutung lautet deshalb, der Fehler sei nur vorübergehend gewesen.

## Der erfolgreiche Gegenversuch passt nicht zum Auftrag

Zunächst werden die Bedingungen verglichen. Beide Aufrufe verwenden dieselbe Skriptversion und denselben Zielpfad. Der manuelle Versuch läuft jedoch unter dem persönlichen Administratorkonto, während der geplante Export `CONTOSO\svcExport` verwendet. Damit hat der Gegenversuch gerade die Bedingung verändert, die für einen Zugriffsfehler wesentlich ist.

Die Fehlermeldung grenzt die Untersuchung ein, beweist aber noch keine bestimmte ACL-Ursache. Auch die Freigabeberechtigung, die konkrete Zielauflösung oder eine abweichende Identität müssen berücksichtigt werden. Der Ablauf braucht daher einen Test unter seiner tatsächlichen Ausführungsidentität.

Ein begrenzter Diagnoseauftrag kann die Identität unmittelbar vor dem betroffenen Schritt ausgeben:

```powershell
[System.Security.Principal.WindowsIdentity]::GetCurrent().Name
```

Die Ausgabe gehört in dasselbe Ausführungsprotokoll wie der Zielpfad. Bei Remoting muss die Abfrage innerhalb des betreffenden entfernten Prozesses stehen. Die Identität des aufrufenden Terminals wäre erneut die falsche Beobachtung.

## Vom Verdacht zur belegten Ursache

Für das konstruierte Beispiel ergeben die weiteren Prüfungen folgendes Bild:

| Beobachtung | Bedeutung |
|---|---|
| Dienstaufruf bestätigt `CONTOSO\svcExport` | Die erwartete Identität wird verwendet |
| Freigabe erlaubt dieser Identität Änderungen | Die Freigabe erklärt die Ablehnung nicht allein |
| NTFS am Zielordner gewährt nur Lesen | Das Erstellen einer Datei ist nicht erlaubt |
| Ein freigegebener Schreibtest unter derselben Identität scheitert | Der Fehler ist unter den Auftragsbedingungen reproduzierbar |

Die ACL wird anschließend durch den zuständigen Administrator auf den benötigten Umfang korrigiert. Derselbe begrenzte Schreibtest gelingt danach. Erst der Vergleich vor und nach dieser einzelnen Änderung stützt im Beispiel die Ursache: Dem Betriebskonto fehlte die erforderliche NTFS-Berechtigung auf dem Exportordner.

Der Test verwendet eine eigens benannte Datei, deren Erstellung und Entfernung genehmigt sind. Ein Produktionsdokument zu überschreiben wäre kein geeigneter Diagnoseschritt. Die eigentliche Exportfunktion wird danach ebenfalls geprüft, denn ein gelungener Schreibzugriff bestätigt noch nicht den Inhalt des Exports.

## Der Befund verändert auch die Fehlerbehandlung

Wenn ein PowerShell-Cmdlet einen nicht terminierenden Fehler ausgibt, kann das Skript weiterlaufen. Ein `try/catch` allein stellt nicht sicher, dass dieser Fehler abgefangen wird. Für einen Schreibschritt, dessen Erfolg Voraussetzung für alles Weitere ist, ermöglicht `-ErrorAction Stop` eine gezielte Behandlung als terminierender Fehler. [Microsoft: Fehlerbehandlung in PowerShell](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_error_handling).

Der Ablauf sollte in diesem Fall den ursprünglichen Fehler mit Ziel und Auftragskennung erhalten und nach außen einen Fehlschlag melden. Ein automatisch wiederholter Versuch mit unveränderten Rechten würde keine Korrektur leisten. Ebenso darf eine abschließende Meldung wie „Export beendet“ nicht als fachlicher Erfolg ausgewertet werden.

Bei einem über NodePilot gestarteten Export liefert die Ausführungshistorie den Einstieg in dieselbe Untersuchung. Die Ursache entsteht aus zusammenpassenden Beobachtungen unter den tatsächlichen Ausführungsbedingungen. Eine einzelne Fehlermeldung und ein erfolgreicher Administratorversuch reichen dafür nicht.
