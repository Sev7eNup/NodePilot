# Ein grüner Status ist noch kein erfolgreicher Auftrag

Der Installer meldet `3010`. Die Softwareverteilung markiert den Rechner als fehlerhaft, obwohl Windows Installer den Vorgang als erfolgreich mit erforderlichem Neustart einordnet. Auf einem anderen Server endet derselbe Auftrag mit `0`, während die Anwendung weiterhin ihre alte Konfiguration verwendet. Beide Anzeigen passen zum ausgewerteten Signal. Keine beantwortet vollständig, ob der Auftrag sein Ziel erreicht hat.

Für die folgenden Beispiele sind Anwendung und Versionsnummern frei gewählt. Die Frage dahinter betrifft jeden Ablauf, dessen Ergebnis außerhalb des ausführenden Prozesses liegt.

## Drei Aussagen mit unterschiedlicher Reichweite

Bei der Bereitstellung von ContosoAgent 4.2 könnte der Betrieb folgende Nachweise verlangen:

| Aussage | Geeigneter Nachweis | Offene Frage |
|---|---|---|
| Das Installationsprogramm wurde beendet | Dokumentierter Exitcode des gestarteten Prozesses | Ist noch ein Neustart erforderlich? |
| Die vorgesehene Version ist vorhanden | Produktbezogene Versionsabfrage auf dem Ziel | Verwendet der laufende Prozess diesen Stand? |
| Die Anwendung erfüllt ihren Auftrag | Definierte Funktionsprüfung mit erwartetem Ergebnis | Gilt der Befund für alle betroffenen Instanzen? |

Diese Prüfungen gehören zu verschiedenen Zeitpunkten. Wenn ein notwendiger Neustart erst im nächsten Wartungsfenster stattfinden darf, bleibt die Bereitstellung bis zur anschließenden Prüfung fachlich offen. Ein zusätzlicher Zustand wie „Neustart ausstehend“ beschreibt das genauer als ein endgültiges Erfolgsurteil.

Windows Installer unterscheidet unter anderem `0` für Erfolg, `3010` für Erfolg mit erforderlichem Neustart und `1641` für einen bereits eingeleiteten Neustart. Diese Bedeutung gilt für Windows Installer. Sie darf nicht ungeprüft auf andere Programme übertragen werden. [Microsoft: Windows-Installer-Rückgabecodes](https://learn.microsoft.com/en-us/windows/win32/msi/error-codes).

Die Ablaufsteuerung braucht dafür eine ausdrückliche Zuordnung. Ein unbekannter Code sollte seine ursprüngliche Zahl behalten und zur Untersuchung führen. Ihn pauschal als „wahrscheinlich erfolgreich“ zu behandeln, entfernt genau die Information, die später fehlt.

## Der Nachweis darf sich nicht selbst bestätigen

Eine Datei zu kopieren und anschließend nur festzustellen, dass der Kopierbefehl keinen Fehler geliefert hat, ist eine schwache Abnahme. Aussagekräftiger ist der Blick auf das Ziel: Stimmt der Inhalt mit der freigegebenen Datei überein, und hat die Anwendung die Änderung tatsächlich übernommen?

Auch eine Versionsdatei kann täuschen, wenn sie unabhängig vom laufenden Dienst aktualisiert wird. Für das Beispiel wäre eine anwendungseigene Statusabfrage geeignet, die die geladene Version zurückgibt. Welche Prüfung trägt, muss aus dem Verhalten der Anwendung abgeleitet werden. Ein allgemeiner HTTP-Status `200` genügt nicht, wenn die betreffende Route lediglich die Erreichbarkeit des Webservers bestätigt.

Beim Backup ist diese Grenze besonders deutlich. `RESTORE VERIFYONLY` prüft in SQL Server die Vollständigkeit und Lesbarkeit eines Sicherungssatzes, stellt die Datenbank aber nicht wieder her und überprüft nicht die Struktur sämtlicher enthaltenen Daten. Eine geplante Wiederherstellungsprobe beantwortet andere Fragen, etwa nach benötigten Schlüsseln, Abhängigkeiten und der tatsächlich erreichten Wiederherstellungszeit. [Microsoft: RESTORE VERIFYONLY](https://learn.microsoft.com/en-us/sql/t-sql/statements/restore-statements-verifyonly-transact-sql).

## Ein Ergebnis, das weitere Entscheidungen erlaubt

Der Abschlussbericht sollte die Beobachtung mit dem Zeitpunkt und dem betroffenen Ziel verbinden. „Version 4.2 auf srv-app01 nach Neustart bestätigt“ ist belastbarer als „Installation erfolgreich“. Wenn die Prüfung ausfällt, bleibt der Zustand unbekannt. Ein fehlender Nachweis ist nicht automatisch der Nachweis eines Fehlers der Anwendung.

Für die Wiederaufnahme ist außerdem entscheidend, welche Änderung bereits stattgefunden hat. Wurde die Software installiert und lediglich die Funktionsprüfung unterbrochen, sollte der nächste Versuch zunächst diese Prüfung nachholen. Ein erneuter Installationslauf wäre eine andere Entscheidung.

In NodePilot können solche Prüfungen als eigene Schritte hinter der Änderung stehen. Der technische Abschluss einer Activity und die fachliche Freigabe des Ergebnisses erhalten damit jeweils einen nachvollziehbaren Platz im Ablauf.
