# Behobene Punkte – verständlich erklärt

Arbeitsstand auf `audit/architecture-security-2026-10-09`. Diese Liste wird während der Prüfung
fortgeschrieben; sie ist noch keine Abschlussfreigabe. Die Auswirkungen beschreiben mögliche
Alltagssituationen, keine behaupteten Vorfälle auf deiner Installation. Technische Nachweise,
Schweregrade und Grenzen stehen im [Gesamtbericht](../architecture-security-review-2026-10-09.md)
und den einzelnen Rundennachweisen.

### 1. Unterworkflows behalten den tatsächlichen Auftraggeber.

Ein aufgerufener Workflow
übernimmt nicht mehr die Identität seines Herausgebers. Sonst hätte ein gewöhnlicher Nutzer
über einen vom Administrator veröffentlichten Unterworkflow zusätzliche Rechte erhalten können.

### 2. Auch im gleichen Ordner gelten die aktuellen Benutzerrechte.

Die bisherige Abkürzung
übersprang eine Rollenprüfung. Ein inzwischen zum Betrachter herabgestufter Nutzer hätte
dadurch weiterhin Unterworkflows ausführen können.

### 3. Wartende Unterworkflows werden vor dem Start erneut geprüft.

Berechtigungen und
Aktivierung werden nach Wartezeiten frisch geladen. Ein zwischenzeitlich gesperrter Workflow
oder ein entzogener Zugriff wird dadurch auch bei bereits vorgemerkten Aufrufen berücksichtigt.

### 4. Verschieben beendet nicht mehr erlaubte Live-Abonnements.

Das Verschieben eines
Workflows und die Abmeldung bisheriger Zuschauer folgen einer gemeinsamen Reihenfolge.
Sonst hätte ein geöffnetes Browserfenster nach dem Verschieben in einen privaten Ordner
weiterhin Live-Ausgaben erhalten können.

### 5. LLM-Antworten werden vor dem vollständigen Einlesen begrenzt.

Die Größenprüfung greift
beim Lesen statt erst danach. Ein fehlerhafter oder manipulierter KI-Endpunkt hätte sonst
mit einer riesigen Antwort den Arbeitsspeicher des Dienstes füllen können.

### 6. Die Wissenssuche folgt keinen versteckten Verzeichnis-Abzweigungen.

Links und Junctions
innerhalb des gewählten Verzeichnisses werden kontrolliert ausgeschlossen; der ausdrücklich
konfigurierte Einstieg bleibt nutzbar. So liest die Suche keine außerhalb liegenden Dateien
ein und bleibt nicht in einem Verzeichniszyklus hängen.

### 7. Pausierte Schritte werden nach einem Neustart sauber abgeschlossen.

Die
Wiederanlaufbehandlung berücksichtigt auch diese Schritte. In der Oberfläche wären sonst
verwaiste Pausen stehen geblieben, obwohl die ursprüngliche Ausführung nicht mehr existiert.

### 8. Ein abgewiesener Start bleibt nicht als laufend gespeichert.

Auch das Erreichen eines
Kapazitätslimits durchläuft den Abschluss der Ausführung. Andernfalls hätte die Übersicht
einen laufenden Job angezeigt, der tatsächlich nie gestartet wurde.

### 9. SCOrch-Folgeseiten bleiben beim ursprünglichen Server.

Pagination prüft Zielhost,
Protokoll und Port vor dem nächsten authentifizierten Aufruf. Eine manipulierte Antwort
hätte sonst Windows-Anmeldeinformationen an ein anderes Ziel weiterreichen können.

### 10. Verschachtelte Workflows blockieren sich nicht selbst am Ausführungslimit.

Wartende
Eltern geben Kapazität kontrolliert für ihre Kinder frei. Bei vielen gleichzeitigen
Unterworkflow-Aufrufen konnten zuvor alle Plätze durch wartende Eltern belegt sein,
während kein Kind mehr starten konnte.

### 11. Bereits empfangene PowerShell-Ausgabe geht bei einem offenen Ausgabekanal nicht verloren.

Die Ausgabe wird fortlaufend gesammelt. Ein zurückgebliebener Prozess mit offenem Kanal
hätte sonst dazu führen können, dass schon geschriebene Ergebnisse leer zurückkamen.

### 12. Die abschließende KI-Bewertung erhält den vollständigen Auftrag.

Bei Platzmangel werden
zuerst Belege gekürzt; passt der Auftrag selbst nicht, gibt es einen ausdrücklichen Fehler.
Zuvor konnten bei langen Aufgaben Anforderungen am Ende verschwinden und das Ergebnis
trotzdem als vollständig bewertet werden.

### 13. PowerShell und WinRM wiederholen Skripte nicht heimlich.

Wiederholungen gehören zur
ausdrücklich eingestellten Retry-Regel. Ein Skript, das erst eine Änderung ausführte und
anschließend einen bestimmten Fehler meldete, konnte vorher ohne Freigabe dreimal laufen.

### 14. Die CLI erkennt einen späteren Verbindungsabbruch.

Fällt die bereits aufgebaute
Live-Verbindung aus, wird der Status über die vorhandene API abgefragt. Das Terminal
konnte sonst unbegrenzt warten, obwohl der Job längst fertig war.

### 15. Veröffentlichen bleibt an den gewählten Workflow gebunden.

Nach verzögertem Speichern
wird geprüft, ob der ursprüngliche Editor noch aktuell ist. Beim schnellen Wechsel von
Workflow A zu B hätte sonst die alte Aktion versehentlich B veröffentlichen können.

### 16. Ausführungsdaten verraten keine geschützten Elternworkflows.

Namen und IDs des
übergeordneten Laufs werden nur bei entsprechender Leseberechtigung geliefert. Ein Nutzer
mit Zugriff auf den Unterworkflow hätte sonst Details eines privaten Elternworkflows gesehen.

### 17. Ungültige Wartungsfenster-Typen werden abgelehnt.

Auch unbekannte numerische Enum-Werte
werden geprüft. Zuvor ließ sich ein scheinbar gespeichertes Wartungsfenster anlegen,
dessen Typ die Ausführung gar nicht auswerten konnte.

### 18. Ein Import prüft keine sachfremden Webhooks mehr.

Der unnötige globale Scan wurde
entfernt; importierte Workflows bleiben wie vorgesehen zunächst deaktiviert. Ein Fehler
in einem anderen Workflow konnte vorher einen völlig unabhängigen Import verhindern.

### 19. Desktop-Rollback startet die API erst nach erfolgreicher Datenwiederherstellung.

PostgreSQL wird separat bereitgestellt und der Restore-Erfolg geprüft. Sonst konnten
Hintergrundjobs bereits in eine teilweise wiederhergestellte Datenbank schreiben oder
ein fehlgeschlagener Restore als erfolgreich erscheinen.

### 20. HTTP-Aufrufer verwenden dieselbe vollständige Zielprüfung.

REST-Aktivitäten,
Agent-Werkzeuge, MCP-Verbindungen und Webhooks teilen die vorhandene Policy-Seam.
Ein freigegebener Proxy-Aufruf konnte zuvor bei einzelnen Aufrufern Schutzprüfungen
umgehen, die an anderer Stelle Cloud-Metadaten oder nicht freigegebene Ziele sperrten.

### 21. Abgebrochene Dateiüberwachungen räumen später eintreffende Ressourcen auf.

Das gilt
auch bei Fehlern während des Aufbaus. Wenn ein Netzlaufwerk nach einem Serverwechsel
verspätet antwortete, konnte sonst ein nicht mehr benötigter Wächter zurückbleiben.

### 22. Die Archivprüfung besucht nach und nach alle Archive.

Ein begrenzter Durchlauf merkt
sich seinen Fortschritt und beginnt nach einem vollständigen Umlauf wieder vorn.
Zuvor wurden immer dieselben ältesten Dateien geprüft; spätere Schäden blieben unentdeckt.

### 23. Nach einer Wiederverbindung werden abgeschlossene Jobs nachgeladen.

Die Oberfläche
gleicht bekannte aktive Läufe mit aktuellen Einzelabfragen ab. Nach einem WLAN-Ausfall
konnte ein inzwischen fertiger Job sonst dauerhaft als laufend erscheinen.

### 24. SCIM-Entzug von Ordnergruppen beendet auch bestehende Sitzungsrechte.

Änderungen
berücksichtigen nicht nur globale Rollen, sondern auch Gruppen mit Ordnerzugriff.
Ein bereits geöffnetes Fenster konnte nach dem Entzug vorher weiter private Live-Daten sehen.

### 25. Der Lasttest verwendet gültige Workflows und den aktuellen API-Vertrag.

Trigger,
Verzweigungen, Veröffentlichung und die paginierte Anzahl laufender Jobs sind korrigiert.
Der dokumentierte Test konnte sonst schon an seiner Vorbereitung scheitern und keine
brauchbare Aussage über die tatsächliche Systemlast liefern.

### 26. Ungültige extreme Zeitzonenwerte verursachen keinen Zahlenüberlauf.

Die Prüfung nutzt
einen direkten Wertebereich. Ein speziell fehlerhafter Clientwert konnte sonst eine
Wissensabfrage mit einem internen Fehler abbrechen.

### 27. Direktes Verbinden und Einfügen im Designer löst Speichern aus.

Beide Wege markieren
den Workflow als geändert. Zuvor konnten sichtbar eingefügte Knoten oder Kanten beim
Verlassen des Editors verschwinden, weil Autosave keine Änderung erkannte.

### 28. Ein Backup-Restore aktualisiert Live-Berechtigungen nach dem Commit.

Wiederhergestellte
Ordnerzuordnungen und Gruppenmitgliedschaften werden auch für bestehende Verbindungen
wirksam. Sonst konnten frühere Rechte nach dem Restore weiterbestehen.

### 29. Neu angelegte Backup-Kopien verweisen auf die richtigen Unterworkflows.

Referenzen
werden erst umgeschrieben, wenn alle neuen IDs bekannt sind. Ein als Kopie
wiederhergestellter Ablauf konnte sonst versehentlich den ursprünglichen Unterworkflow starten.

### 30. Der Alarm für ausgefallene Zeitpläne erkennt auch häufige Termine.

Er sucht den letzten
relevanten Termin außerhalb der Toleranzzeit und vermeidet einen abgeschnittenen Vorwärtslauf.
Ein minütlicher Job konnte sonst seit Stunden ausfallen, ohne diesen Alarm auszulösen.

### 31. Der Entwicklungs-Neustart beendet nur Prozesse dieses Projekts.

Lokale Listener und
ausführbare Pfade werden vor dem Stoppen geprüft. Vorher konnte der Helfer auch andere
Node-Projekte oder ein unbeteiligtes Werkzeug schließen.

### 32. Die Website-Veröffentlichung stoppt bei einem fehlgeschlagenen Datei-Upload.

Nach
ausgeschöpften Wiederholungen wird keine abhängige HTML-Seite mehr veröffentlicht.
Besucher hätten sonst eine Seite bekommen können, deren JavaScript oder Styles fehlen.

### 33. Datenbankpasswörter landen nicht in Provisionierungsargumenten und SQL-Fehlertexten.

Die vorhandene sichere Übergabe über Standardeingabe und Kindprozess-Umgebung wird verwendet;
credentialhaltige Fehler werden nicht ausgegeben. Ein Installationsfehler konnte sonst
das neue Passwort im Prozessaufruf oder Installationsprotokoll hinterlassen.

### 34. Die Alarmbearbeitung erhält erweiterte Einstellungen.

Auftretensschwelle,
Zeitfenster und Routenfilter bleiben bei einfachen UI-Änderungen erhalten. Wer eine
aufwendig konfigurierte Regel nur umbenannte, konnte sonst plötzlich ungefilterte
oder wesentlich häufigere Benachrichtigungen auslösen.

### 35. Ein Skill-Import gehört weiterhin zur ursprünglichen Anmeldung.

Nach dem asynchronen
Lesen der Datei wird die Anmeldesitzung erneut geprüft. Bei Logout/Login währenddessen
konnte die alte Importabsicht sonst unter dem neuen Administrator ausgeführt werden.

### 36. Dashboard-Zwischenspeicher beachten verschobene Workflows.

Alte Berechnungen bleiben
nach der Invalidierung in ihrer alten Cache-Generation. Sonst konnten Namen und
Fehlerausgaben eines inzwischen privaten Workflows noch aus dem Cache zurückkommen.

### 37. Live-Ereignisse können keine veraltete Ordnerzuordnung zurück in den Cache schreiben.

Auflösung, Versand und Verschieben sind koordiniert und zeitlich begrenzt. Bei einem
ungünstigen gleichzeitigen Ablauf hätten Statusmeldungen sonst weiter die früheren Zuschauer erreicht.

### 38. Auch manuelle Warteschlangen prüfen Rechte direkt vor dem Start.

Das gilt ebenso
für Debug und Retry. Ein vor Stunden vorgemerkter Job konnte sonst noch starten,
obwohl dem Auftraggeber inzwischen die Ausführungsrechte entzogen wurden.

### 39. SCIM-Fehler behalten ihr vorgeschriebenes Antwortformat.

Der allgemeine REST-Filter
verändert diese typisierten Protokollantworten nicht mehr. Ein angebundenes
Benutzerverwaltungssystem konnte Fehler sonst falsch einordnen und die Provisionierung abbrechen.

### 40. Entfernte Anmeldegruppen und Rollen bleiben entfernt.

Eine höhere Konfigurationsquelle
ersetzt Berechtigungslisten vollständig, einschließlich geleerter Listen und einzelner
Zuordnungsfelder. Eine aus den Einstellungen entfernte Gruppe konnte vorher aus der
Basisdatei weiterwirken und sogar ihre alte Admin-Zuordnung behalten.

### 41. Gelöschte MCP-Lesefreigaben werden tatsächlich widerrufen.

Dieselbe gemeinsame
Listenlogik gilt für Werkzeugfreigaben. Ein KI-Agent hätte sonst ein Werkzeug weiter
aufrufen können, dessen Freigabe der Administrator gerade entfernt hatte.

### 42. Entfernte Netzwerk-Ausnahmen bleiben gesperrt.

REST- und Prüfziel-Listen verwenden
ebenfalls die vollständige wirksame Liste. Ein intern erreichbarer Dienst konnte sonst
trotz Löschung der Ausnahme weiter angesprochen werden. Die Anzeige von Dateisystem-Freigaben
nutzt zudem denselben strikten Leser wie die Ausführung.

### 43. Verworfene Support-Ereignisse werden richtig gezählt.

Der nicht blockierende
Kanal meldet einen vollen Puffer jetzt korrekt. Bei hoher Last konnte zuvor ein Teil
der Support-Ereignisse fehlen, während die Verlustanzeige fälschlich unauffällig blieb.

### 44. Leerzeichen ändern nicht heimlich das Logformat.

Prüfung und Formatter normalisieren
die Einstellung gleich. Ein versehentliches Leerzeichen konnte vorher statt JSON
normalen Text erzeugen und damit die Logauswertung stören.

### 45. Tageslogs enthalten auch ihre Folgedateien.

Download und Tail berücksichtigen
größenbedingt aufgeteilte Dateien in richtiger Reihenfolge und den lokalen Roll-Tag.
Die Anzeige liest nur so viele neue Segmente wie nötig. Nach Erreichen der Dateigröße
konnten sonst die neuesten Meldungen in Anzeige und Tagesdownload fehlen.

### 46. Der LLM-Verbindungstest liest nur die benötigte Antwort.

Erfolgsantworten werden
nicht vollständig gepuffert; Fehlertexte sind begrenzt und unterliegen einer Deadline.
Ein hängender oder sehr großer Antwortinhalt konnte sonst den Test unnötig belasten.

### 47. Der experimentelle Abnahmekatalog passt wieder zur Anwendung.

Agent-Aktivitäten,
Agent-Einstellungen und die geänderten Editor-/Reconnect-Fälle sind erfasst.
Andernfalls hätte eine spätere Abnahme wichtige neue Funktionen ausgelassen. Die
Katalogprüfung ist grün; eine tatsächliche Computer-Use-Abnahme wurde dadurch nicht ersetzt.

### 48. Wiederhergestellte Agenten verwenden die neu angelegten Maschinen und Credentials.

Auch verschachtelte Agent-/Team-Konfigurationen werden über die vorhandene Referenz-Seam
validiert und umgeschrieben. Eine Backup-Kopie konnte sonst auf dem ursprünglichen
Rechner oder mit den ursprünglichen Zugangsdaten laufen.

### 49. Entfernte Proxy-Ausnahmen gelten wirklich nicht mehr.

REST- und LLM-Verbindungen
ersetzen die Bypass-Liste vollständig, auch beim erneuten Laden der Einstellungen.
Sonst konnte ein Ziel weiterhin direkt angesprochen werden, obwohl der Administrator
den Weg über den Firmenproxy wieder vorgeschrieben hatte.

### 50. Der Lasttest liest die tatsächliche Zahl laufender Ausführungen.

Sein Client verwendet
das Feld `total` aus dem gemeinsamen API-Vertrag. Vorher brach die abschließende
Auswertung trotz erfolgreicher Anfragen ab; die Regression serialisiert jetzt den echten
Antworttyp statt einer nachgebauten Testantwort.

### 51. Umbenennen einer Custom Activity erhält ihre Ressourcenlimits.

Speicher- und
Prozessgrenzen bleiben erhalten, auch wenn das Formular dafür keine eigenen Felder zeigt.
Wer eine isolierte Aktivität nur umbenannte, konnte vorher unbemerkt ihre Begrenzung verlieren.

### 52. CLI-Trigger beachten den konfigurierten TLS-Pin.

Profil und Kommandozeilenoption
wirken nun auch bei der Anmeldung per Trigger-API-Key. Auf einer Desktop-Installation
mit selbst signiertem Zertifikat scheiterte vorher genau dieser Befehl, obwohl die anderen
CLI-Befehle funktionierten. Ein falscher Pin wird weiterhin abgelehnt.

### 53. Lange gültige Custom-Activity-Namen passen in Ausführungsdaten (Medium)

Ein erlaubter Schlüssel mit 64 Zeichen benötigt mit `custom:` insgesamt 71 Zeichen. Die Datenbankfelder für Schritttyp und Supportereignis waren kleiner; PostgreSQL/SQL Server konnten deshalb gültige Läufe nicht speichern. Gemeinsame Längengrenzen und eine additive Migration erhalten nun die vollständigen Typnamen. Im Alltag läuft auch eine lange, aussagekräftig benannte Custom Activity. Zwei Modellregressionen waren rot; native Migrationsskripte, Up/Down/Up und Erhalt bestehender SQLite-Daten wurden geprüft.

### 54. Gleichzeitige Änderungen überschreiben keine bereits freigegebene Custom Activity (Medium)

Der vorhandene Versionstoken wird jetzt auch beim Datenbank-Commit geprüft. Löschen, Rollback und Aktivieren verlangen den beim Lesen gesehenen Stand; veraltete Änderungen enden mit dem vorhandenen Konflikthinweis. Im Alltag kann eine parallel geöffnete Bearbeitung eine zwischenzeitliche Freigabe nicht unbemerkt überschreiben. Eine echte Save-Barriere reproduzierte den Fehler; sechs weitere Storefälle und der gemeinsame Data-Fokus mit 34/34 Tests waren grün. Dies ist ein Governance-/Integritätsfehler, keine behauptete allgemeine Operator-Sandbox.

### 55. Frisch bestätigte Anmelderechte bleiben gültig.

Die automatische Ablaufprüfung
bewertet den aktuellen Verzeichnisstand noch einmal innerhalb ihrer Datenbankänderung.
Bestätigte das Verzeichnis die Rechte während einer laufenden Prüfung erneut, konnte
diese vorher trotzdem die gültige Sitzung widerrufen und einen gerade gestarteten Job abbrechen.

### 56. Unvollständige KI-Antworten lösen keine Werkzeuge aus.

Auch wenn bereits ein
vollständiger Werkzeugaufruf in der Antwort steht, bleibt ein Abbruch wegen des
Ausgabelimits erkennbar. Sonst konnte ein Agent einen Teil einer noch unvollständigen
Aktionsfolge ausführen, etwa einen Job starten, bevor die Antwort vollständig war.

### 57. Umbenannte SQL-Spalten verraten keine geschützten Inhalte über Sternabfragen.

Datenbankverwaltung und KI-Abfragen lehnen das automatische Auslesen aller Spalten
geschützter Tabellen ab. Benötigte normale Spalten und Zählabfragen bleiben verfügbar.
Eine umbenannte Ergebnistabelle konnte vorher etwa einen Passwort-Hash vor der
Spaltenmaskierung verstecken und an den KI-Kontext weiterreichen.

### 58. Layout-Wiederherstellung erhält spätere Workflow-Änderungen.

Sie setzt nur Positionen
noch vorhandener Knoten zurück. Nach „Aufräumen“, Bearbeiten und „Original-Layout“ konnten
vorher neue Knoten verschwinden, gelöschte zurückkehren und geänderte Einstellungen
überschrieben werden; Autosave hätte diesen alten Stand anschließend gespeichert.

### 59. Verwaltungs-, Demo- und Lasttest-Skripte passen wieder zur API.

Anmeldung,
Bearbeitungssperre und Veröffentlichung folgen dem aktuellen Vertrag. Die gemeinsame
Python-Anbindung verhindert weitere Abweichungen zwischen den Clients. Auch mehr als
500 Workflows werden gefunden; fehlerhafte oder unfertige Läufe liefern einen Fehlercode.
Alarm-Seeds melden sich ausdrücklich an. Vorher konnten die Hilfen sofort abbrechen,
unveröffentlichte Workflows hinterlassen oder trotz fehlgeschlagener Arbeit Erfolg melden.
PowerShell versendet Definitionen zudem als sauberen UTF-8-Text statt Provider-Metadaten
aufwendig mitzuverarbeiten.

### 60. Globale Secrets überstehen einen CLI-Export mit anschließendem Import.

Der Export
lässt ihren Wert als `null` aus. `--upsert` bewahrt damit ein vorhandenes Secret, auch bei
der alten `***`-Maske. Neue Secrets brauchen einen echten Wert; sonst endet der Befehl
mit einem Fehler. Vorher konnte ein Konfigurations-Roundtrip das echte SMTP-Passwort
unbemerkt durch drei Sternchen ersetzen.

### 61. Geheime Rückgabefelder bleiben auch im Verlauf verborgen

**Medium, Geheimnisschutz.** Rückgabedaten werden beim Speichern jetzt anhand von Feldname und Wert bereinigt. Vorher konnten etwa `smtpPassword` oder `accessToken` ihren Namensschutz verlieren und im Ausführungsverlauf landen. Ein nachfolgender Arbeitsschritt erhält intern weiterhin die unveränderten Daten; der gespeicherte Verlauf enthält die geschützte Darstellung.

### 62. Windows-Dienstaktionen erzeugen ausführbare PowerShell

**Medium, Funktion.** Die Fehlerprüfung dreier `sc.exe`-Aktionen verwendet jetzt eine korrekt abgegrenzte Exitcode-Expression, die auch die Workflow-Templateersetzung übersteht. Vorher scheiterte das erzeugte Skript bereits beim Parsen. Im Alltag kann ein Dienst beispielsweise wieder konfiguriert werden; echte Windows-Fehler bleiben als Fehler sichtbar. Parser- und anschließende Activity-Integrationstests sichern beide Verarbeitungsstufen ab.

### 63. Ein abgebrochener Wartezweig wird nicht zum Workflowfehler

**Medium, Funktion.** `WaitForCondition` reicht den Abbruch während einer Pollpause als Abbruch weiter. Vorher wurde daraus ein Timeout oder Fehler. Wenn bei „auf den ersten fertigen Zweig warten“ ein anderer Zweig gewinnt, beendet der unterlegene Wartezweig jetzt ruhig seine Arbeit, statt den gesamten erfolgreichen Ablauf fehlschlagen zu lassen.

### 64. Schwierige Suchmuster blockieren die Textbearbeitung nicht unbegrenzt

**Medium, Verfügbarkeit.** Beide Regex-Pfade der Textdateibearbeitung besitzen jetzt ein begrenztes Auswertungsbudget. Vorher konnte ein ungünstiges Muster auf einer passenden langen Datei unbegrenzt rechnen. Solche Eingaben liefern nun einen strukturierten Fehler, ohne die Datei zu verändern; zwei isolierte Prozessregressionen belegen das Verhalten.

### 65. Output-Aliase werden so geprüft, wie sie später aufgelöst werden.

`shared` und
`SHARED` gelten beim Prüfen nun ebenso als derselbe Name wie während der Ausführung.
Sonst sah ein Workflow gültig aus, obwohl zwei Schritte unter demselben Namen lieferten
und ein Folgeschritt dadurch den falschen Output verwenden konnte.

### 66. Das Dashboard zeigt den tatsächlichen Datenbank-Prüftakt.

Es verwendet jetzt
denselben Konfigurationsleser wie der Trigger. Abweichende Schreibweisen, Standardwerte
und Mindestintervalle werden damit gleich behandelt. Ein alle fünf Sekunden prüfender
Trigger konnte vorher mit einem anderen oder fehlenden Takt angezeigt werden.

### 67. „Run“ im Skripteditor führt den gerade sichtbaren Text aus.

Sowohl das große
Dialogfenster als auch die Eigenschaftenansicht übergeben den aktuellen Text direkt an
den Schritttest. Vorher konnte nach einer Änderung noch die vorherige Skriptversion auf
der Zielmaschine laufen, weil die gespeicherte React-Konfiguration erst danach aktualisiert wurde.

### 68. Nachträglich angelegte Dateilinks umgehen den überwachten Ordner nicht

**Medium, Dateigrenze.** FileWatcher-Snapshot, Nachabgleich und Zustellung verwenden dieselbe Reparse-Prüfung wie direkte Dateiereignisse. Vorher konnte ein erst nach dem Start angelegter Link beim Nachabgleich Dateien außerhalb des erlaubten Verzeichnisses einbeziehen. Ein fehlgeschlagener Scan erhält außerdem den bisherigen Snapshot. Reale nachträglich angelegte Datei- und Verzeichnislinks reproduzierten den Unterschied.

### 69. Metrikabfragen bleiben vollständig im freigegebenen Bereich.

Jeder einzelne
Prometheus-Selector muss zu einer erlaubten Metrik gehören. Eine erlaubte Metrik am
Anfang der Abfrage genügt nicht mehr, um weitere beliebige Messwerte beizumischen.
Bei gemeinsam genutztem Prometheus konnten sonst Daten anderer Dienste mitgeliefert werden.

### 71. Zahlen in Alarmrichtlinien werden zuverlässig übernommen

**Medium, Funktion und Struktur.** Der Systemalarm-Auswerter verwendet jetzt die gemeinsame Parameterumwandlung statt einer zweiten, unvollständigen JSON-Auswertung. Vorher konnten numerische Werte still ausfallen. Eine eingestellte Beobachtungsdauer von 60 Minuten wirkt damit tatsächlich auch dann, wenn sie als JSON-Zahl gespeichert ist, statt unbemerkt auf zehn Minuten zurückzufallen.

### 72. Auch hinter vielen alten Läufen werden neue Problemfälle geprüft

**Medium, Funktion.** Die Prüfung zu lange wartender oder laufender Ausführungen setzt ihren begrenzten Scan mit einem zyklischen Cursor fort. Vorher belegten dieselben ältesten 200 Einträge jeden Durchlauf. Wenn viele Jobs hängen, werden damit auch später gestartete überfällige Jobs untersucht und gegebenenfalls gemeldet. Zwei Tests mit jeweils 205 Läufen sichern das ab.

### 73. Deaktivierte Benachrichtigungen werden nicht weiter zugestellt

**Medium, Funktion.** Ein ausstehender Zustellversuch lädt die Regel erneut und berücksichtigt ihren Aktivierungsschalter. Vorher konnten bereits vorgemerkte Wiederholungen nach dem Abschalten weiter versenden. Wer eine störende Benachrichtigung deaktiviert, stoppt damit jetzt auch deren ausstehende Wiederholungen.

### 74. Ein erneut ausgefallener Trigger löst wieder einen Alarm aus

**Medium, Funktion.** Ein vollständiger gesunder Trigger-Snapshot beendet die bisherige Fehlerphase. Vorher blieb deren Kennung bestehen; ein späterer echter Ausfall konnte deshalb als bereits gemeldet unterdrückt werden. Teilweise oder nicht verfügbare Snapshots gelten weiterhin nicht als Entwarnung, insbesondere auf passiven Clusterknoten.

### 75. Gespeicherte Host-Freigaben wirken tatsächlich auf den Webserver.

Die Einstellung
landet jetzt an der Stelle, die ASP.NET beim Laden verwendet; gleichzeitige Änderungen
sowie Vorgaben aus Umgebung und Kommandozeile bleiben berücksichtigt. Vorher konnte
die Oberfläche eine neue Firmenadresse bestätigen, während nach dem Neustart weiterhin
die alte Liste erlaubter Adressen galt.

### 76. Gleichzeitiges Anlegen erzeugt keine doppelten aktiven Custom-Schlüssel (Medium)

Ein gefilterter Unique-Index schützt aktive Schlüssel einschließlich Entwürfen. Import überspringt den konkurrierenden Verlierer kontrolliert; Restore rollt den Konflikt zurück und verlangt ein neues Preview. Im Alltag führen zwei gleichzeitige Anlageversuche nicht mehr zu doppelten Definitionen, die beispielsweise den Workflowchat beim Aufbau seines Katalogs abbrechen ließen. Vorhandene Duplikate werden nicht gelöscht: Die Migration hält mit einer Diagnose an. Echter Create-Race zunächst rot, anschließend inklusive drei Provider-Skripten, Tombstones sowie Import-/Restore-Verhalten grün; Migrationshilfe dokumentiert.

### 77. Ein Passwortwechsel bleibt auch bei gleichzeitigem Login oder Refresh wirksam.

Die neue Sitzung bleibt an die Sicherheitsversion gebunden, für die das Passwort
beziehungsweise der vorhandene Token geprüft wurde. Ändert ein Administrator genau
währenddessen das Passwort, übernimmt die alte Anfrage nicht versehentlich die neue
Freigabe. Normale Anmeldungen und Tokenverlängerungen funktionieren weiterhin.

### 78. Die Wissenssuche erkennt die exakte Schreibweise eines Workflow-Namens zuverlässig.

Gibt es „Job“ und „job“, findet die Suche nach „Job“ den richtigen Workflow auch auf
einer Datenbank, die Groß- und Kleinschreibung sonst gleich behandelt. Die Suche verwendet
jetzt dieselbe Namensauflösung wie die anderen Zugänge und berücksichtigt weiterhin die
erlaubten Ordner. Vorher erschien ein vorhandener Workflow unnötig als mehrdeutig.

### 79. Alte Verzeichnisgruppen behalten keine Ordnerrechte durch eine andere frische Gruppe.

Wird beispielsweise die allgemeine Anmeldegruppe per SCIM aktualisiert, bleibt eine
überalterte Mitgliedschaft im Finanzordner trotzdem ungültig. Das gilt für API-Aufrufe,
Unterworkflows und bereits geöffnete Live-Ansichten. Direkte Benutzerfreigaben sowie
ausreichend frische Gruppen bleiben nutzbar; die Live-Verbindung verliert nur die
nicht mehr erlaubten Abonnements.

### 80. CLI-Liveausgaben verwenden dieselben TLS-Einstellungen wie REST

**Medium, Funktion.** Ein per Zertifikat-Pin oder ausdrücklicher unsicherer Ausnahme erreichbarer Server funktioniert jetzt auch für Liveausgaben. Vorher ignorierte die SignalR-Verbindung diese Einstellungen und fiel auf Polling zurück. Die vorhandene Entscheidung „gültige Zertifikatskette oder passender Pin“ bleibt erhalten. Zwei echte lokale TLS-Hub-Fälle waren rot; der kombinierte CLI-Fokus bestand mit 89 Tests.

### 81. Ein Schlüsselwechsel erfasst alle gespeicherten Secret-Bereiche und Rollbackdateien.

Der Server kann beim Start bereits mit dem vorübergehend konfigurierten Altschlüssel lesen.
Anschließend verschlüsselt der Sweep auch Benachrichtigungsrouten, wartende Startparameter
und Laufzeiteinstellungen samt Rollbackdateien neu. Vorher konnte der Server nach dem
Entfernen des Altschlüssels nicht starten oder eine wartende Ausführung ihr Passwort
nicht lesen – trotz zuvor gemeldetem Erfolg. Gleichzeitige Änderungen werden nicht
überschrieben; beschädigte Einträge werden in API, CLI und Oberfläche als Teilfehler
angezeigt. In HA muss jeder Node seine eigenen Laufzeitdateien umstellen.

**Medium, Funktion, gemeinsame API-/Clientkorrektur.** Rotation berücksichtigt außerdem Benachrichtigungsrouten, wartende Ausführungsparameter sowie aktive und gesicherte Laufzeiteinstellungen. Die CLI zeigt die zusätzlichen Erfolgs- und Fehlerzahlen samt Ausnahmen an, sodass Teilerfolge erkennbar bleiben. Zwei CLI-Vertragsfälle waren rot; kombinierter CLI-Fokus 89 grün. Die API-Änderung und ihre eigenen Nachweise dokumentiert der Runtime-Prüfbericht.

### 82. Die Operations-Ansicht verwirft alten Livestatus beim Benutzerwechsel.

Noch unterwegs befindliche Meldungen einer geschlossenen Verbindung werden ignoriert;
beim erneuten Abonnieren funktionieren auch die Aktualisierungstimer wieder.
Vorher konnten alte Statusdaten im Browserspeicher verbleiben oder Aktualisierungen
ausbleiben. Die bestehenden Berechtigungsfilter verhinderten dabei bereits die Anzeige
fremder Workflows; behoben wurde die Verlässlichkeit des lokalen Zustands.

### 83. SCOrch-Weiterleitungen bleiben beim konfigurierten Server

**Medium, Sicherheit.** Der Switcher prüft nun jeden HTTP-Weiterleitungsschritt vor dem nächsten Request. Ein anderer Server erhält keine Anmeldedaten oder Start-/Stoppdaten. Begrenzte Weiterleitungen innerhalb derselben Origin bleiben möglich. Drei lokale Redirectfälle waren rot; alle 109 Switcher-Tests sind grün, ohne echte Orchestrator-Aktionen.

### 84. Ein neuer Schritt aktiviert keine stillgelegte Verbindung mehr.

Beim Einfügen
bleiben beide Teile einer zuvor deaktivierten Verbindung deaktiviert. Vorher konnte
der eingefügte Schritt beim nächsten Lauf ausgeführt werden, obwohl man den Zweig
abgeschaltet hatte – beispielsweise ein Skript zum Neustarten eines Dienstes.

### 85. Installationsverzeichnisse benötigen einen vertrauenswürdigen Besitzer

**High, Sicherheit.** Ein fremder Besitzer konnte trotz zunächst sicherer Zugriffsrechte diese Rechte wieder ändern und Dienstdateien austauschen. Prüfung und Reparatur berücksichtigen jetzt auch den Besitzer, bevor neue Dateien kopiert werden. Ein isoliertes temporäres Windows-Verzeichnis reproduzierte die Lücke; die ArtifactSecurity-Prüfungen sind grün. Keine Installation wurde verändert.

### 86. Serverupdates erhalten die vollständige Switcher-Konfiguration

**Medium, Funktion.** Unterstützte lokale Einstellungen bleiben bytegetreu erhalten. Vorher überlebte nur die Server-URL, während weitere Optionen durch die Vorlage ersetzt wurden. Ungültige oder unlesbare Konfiguration stoppt vor dem Austausch; Wiederherstellungsfehler erreichen den Rollback. Die tatsächlichen Skriptteile wurden gegen isolierte temporäre Dateien geprüft.

### 87. LLM-Verbindungsfehler behalten ihre konkrete Ursache.

Scheitert etwa die
Namensauflösung, bleibt der Hinweis auf DNS erhalten, statt durch einen allgemeinen
Netzwerkfehler ersetzt zu werden. Portfehler, Zertifikatsprobleme und TLS-Zeitüberschreitungen
bleiben unterscheidbar. So sucht man nicht bei Modell oder Antwortzeit, wenn bereits
die Verbindung zum Modellserver scheitert.

### 88. Die Unterworkflow-Vorschau verwendet die gemeinsame API-Anbindung.

Eine eigene
Kopie der Anmeldung und Fehlerbehandlung entfällt. Dadurch bleiben konkrete Fehlercodes
und Wiederholungsfristen erhalten, und Datenbankprobleme erreichen dieselbe
Zustandsprüfung wie auf anderen Seiten. Vorher konnte die Vorschau nur einen allgemeinen
HTTP-Fehler zeigen, obwohl die API bereits eine hilfreiche Erklärung geliefert hatte.

### 89. Die schreibgeschützte Workflow-Ansicht bietet kein Einfügen mehr an.

Der
Plusbutton auf Verbindungen beachtet dieselbe Bearbeitungsfreigabe wie andere
Designer-Aktionen. Vorher konnte man scheinbar einen Schritt hinzufügen, dessen
Speicherung dann wegen fehlender Rechte oder Bearbeitungssperre scheiterte.

### 90. Zeitplan-Vorschau zeigt die Termine, die der Server tatsächlich berechnet

**Medium, Funktion und Struktur.** Die Vorschau im Knoten und im Einstellungsfenster nutzt jetzt gemeinsam die vorhandene Quartz-Berechnung des Servers. Vorher konnte eine Zahl als Wochentag einen anderen Tag anzeigen, eine Jahresbegrenzung verschwinden oder die Browserzeitzone vom Server abweichen. Im Alltag hätte man so einen Job für den falschen Zeitpunkt eingeplant oder nach Ablauf seines Zeitplans weiter mit Ausführungen gerechnet. Schnelle Eingaben erzeugen keine Anfrage pro Tastendruck; veraltete Vorschauen verschwinden sofort. Die Offline-Demo weist auf nicht unterstützte Syntax hin. UI-, Cache-, Demo- und Backendprüfungen einschließlich Zeitumstellung sind grün.

### 91. Die Parameterübersicht verrät keine verborgenen Standardwerte.

Leser ohne Bearbeitungsrecht sehen vorhandene Standardwerte eines Workflows jetzt als
`***`; Namen, Typen und Pflichtangaben bleiben erhalten. Ein im Standardwert abgelegtes
Dienstkennwort ließ sich vorher über die Vertragsabfrage lesen, obwohl die normale
Workflowansicht es bereits verbarg. Der tatsächliche Wert bleibt für die Ausführung
gespeichert, und die Oberfläche erkennt weiterhin, dass ein Standard vorhanden ist.

### 92. Datenbankzugangsdaten werden erst nach Absicherung der Registry geschrieben

**Medium, Sicherheit.** Installer und LocalDB-Provisionierung setzen zuerst die sicheren Registry-Berechtigungen und veröffentlichen anschließend die Zugangsdaten. Das bisherige kurzzeitige Lesefenster entfällt. Regressionen führen die tatsächlichen Skriptteile mit einer isolierten Registry-Nachbildung aus; keine produktive Registry wurde geändert.

### 93. Desktop-Vollupdates tauschen den gesamten Lieferumfang aus

**Medium, Funktion.** Neben Anwendung, Desktop und PostgreSQL werden auch Deploymentskripte und Werkzeuge geprüft, aktualisiert und bei Fehlern zurückgesetzt. Vorher konnte anschließend das alte Installationsskript laufen. Alle fünf Komponenten werden vor dem Stoppen validiert; beim Rollback verschwinden auch neu hinzugekommene Komponenten, die vorher fehlten. Isolierte Dateisystemtests sind grün.

### 94. Beim Start des Eventlog-Triggers alte Ereignisse sicher überspringen

**Medium, Funktion.** Nach einem Neustart konnte ein gerade eintreffendes Windows-Ereignis die Verarbeitung starten, bevor die alten Ereignisse aus der Ausfallzeit übersprungen waren. Dadurch konnten unerwartet frühere Aktionen ausgelöst werden. Die Start-Sperre bleibt jetzt bis zum gespeicherten neuen Ausgangspunkt geschlossen. Neue Ereignisse danach werden weiterhin verarbeitet. Ein kontrollierter Paralleltest war vorher rot; der gemeinsame Trigger-/Alarm-Testlauf ist mit 119 Fällen grün.

### 95. Offline-Runtimepakete lassen sich aus dem angegebenen Verzeichnis kopieren

**Low, Funktion.** Der Buildhelfer verwendet tatsächliche Dateipfade statt eines wirkungslosen Sternchens mit LiteralPath. Der dokumentierte Offline-Buildpfad funktioniert dadurch auch mit Sonderzeichen im Verzeichnisnamen. Eine temporäre Dateistruktur mit Klammern im Pfad belegt den Fehler und die Korrektur.

### 96. Alarm-Wiederholungen behalten Filter und Dringlichkeit

**Medium, Funktion.** Wenn beispielsweise ein Alarm die letzte Stunde überwacht und der Mailversand kurz ausfällt, nutzte der Wiederholungsversuch plötzlich nur noch fünf Minuten. Ein vorhandenes Ereignis wurde dann als verschwunden verworfen; außerdem konnte aus „Critical“ unbemerkt „Warning“ werden. Wiederholungen verwenden jetzt wieder die Einstellungen der passenden aktiven Alarmregel. Zwei echte fehlgeschlagene Erstzustellungen mit anschließendem Retry waren vorher rot; Filter, Dringlichkeit und Abbruch sind anschließend geprüft.

### 97. Backup überschreibt Alarmempfänger und Geltungsbereiche vollständig

**Medium, Funktion.** Beim Wiederherstellen einer vorhandenen Alarmregel blieben bisher ihre alten Empfänger und überwachten Workflows erhalten. Dadurch konnte eine Mail weiterhin an einen entfernten Empfänger gehen; bei identischen Geltungsbereichen konnte die Wiederherstellung ganz scheitern. Der Restore lädt jetzt die vorhandenen Routen und Ziele und ersetzt sie beim Überschreiben vollständig. „Überspringen“ erhält die Regel weiterhin unverändert. Zwei Fehlerfälle waren zuerst rot; anschließend sind alle 84 Backup-Tests grün.

### 98. Fehlgeschlagene SQL-Vorprüfungen löschen keine vorhandene Datenbank

**High, Datenverlust.** Der Remote-SQL-Test bereinigt seine Datenbank und Anmeldung nur nach einer ausdrücklich erfolgreich erfassten leeren Ausgangslage. Vorher konnte ein gescheiterter Vorabcheck vorhandene Ressourcen als neu angelegt behandeln. Fehlende, ungültige oder fehlerhafte Remoteantworten brechen sicher ab. Sieben isolierte Skriptfälle prüfen die Zustände; kein SQL-Server wurde verändert.

### 99. TestSuite-Installation findet auch ältere vorhandene Workflows

**Medium, Funktion.** Der Installer verwendet die vollständige Namensliste und lädt benötigte Details gezielt. Die vorherige Übersicht war auf 500 Einträge begrenzt; Wiederholungen konnten deshalb vorhandene Workflows erneut anlegen statt aktualisieren. Ein realer Consumer-Test mit 501 Workfloweinträgen ist grün und erhält die Checkout-Prüfung.

### 100. Ordnerwechsel im Datenbankeditor beendet alte Live-Abonnements

**Medium, Sicherheit.** Verschiebt ein Administrator einen Workflow über die FolderId-Zelle in einen geschützten Ordner, erhalten bisherige Leser jetzt keine weiteren Live-Ausgaben mehr. Vorher konnte ein bereits geöffnetes Fenster weiter mithören, obwohl ein erneuter Zugriff auf denselben Lauf schon verweigert wurde. Der Zellpfad verwendet jetzt dieselbe Sperre und Abonnementbereinigung wie der normale Ordnerwechsel; die Zellbearbeitung und ihr Audit bleiben erhalten. Ein echter Viewer-/Hub-/Notifier-Test war vorher rot; anschließend bestehen alle 229 geprüften Datenbankeditor-, Move- und Notifier-Fälle.

### 101. Failover-Prüfung verwendet gültige Remote-Serviceaufrufe

**Low, Funktion.** Starten und Stoppen laufen über den vorhandenen Remoteausführungspfad statt ungültiger ComputerName-Parameter. HTTP-503-Antworten behalten ihren Diagnoseinhalt. Offlineprüfungen decken die Windows-PowerShell- und simulierte PowerShell-7-Fehlerform ab; keine Dienste wurden angehalten.

### 102. Service- und Aufgabenplaner-Testfixtures räumen sich nicht gegenseitig auf

**Low, Funktion.** Janitor und Teardown beschränken ihre Bereinigung auf die eigene Fixturefamilie. Vorher konnte ein gleichzeitig laufender Test Ressourcen der anderen Familie entfernen. Generator- und isolierte Bereinigungsprüfungen sind grün; die vorhandene Parallelitätsgrenze innerhalb derselben Familie bleibt erhalten.

### 103. Try/Catch-Vorlage kann den tatsächlichen Fehler protokollieren

**Low, Funktion.** Der Skriptschritt der Vorlage stellt seine Ergebnisse jetzt unter dem Namen bereit, auf den ihr Fehlerprotokoll verweist. Vorher änderte das Einfügen die interne Schritt-ID, während der Fehlertext noch auf den alten Namen zeigte. Im Alltag konnte die Vorlage deshalb gerade im Fehlerfall keinen brauchbaren Fehlertext weitergeben. Eine Regression am tatsächlich eingefügten Graphen war rot; 40 Vorlagen-/Einfügetests sind grün.

### 104. Eine sichtbare Debug-Pause lässt sich sofort fortsetzen

**Low, Funktion.** Die Fortsetzen-Funktion ist jetzt bereit, bevor die Pause gespeichert und angezeigt wird. Vorher konnte ein schneller Klick auf „Fortsetzen“ noch abgelehnt werden, obwohl der Schritt bereits als pausiert erschien. Abbruch und fehlgeschlagene Benachrichtigungen räumen den wartenden Eintrag ebenfalls zuverlässig auf. Tests lösen das Fortsetzen direkt beim Speichern und bei der Benachrichtigung aus; beide Fälle waren vorher rot.

### 105. HA-Konfigurationsvorlage startet mit dem echten .NET-JSON-Provider

**Medium, Funktion.** Kommentarähnliche JSON-Eigenschaften besitzen eindeutige Namen. Doppelte `//`-Schlüssel verhinderten vorher das Einlesen der generierten Konfiguration. Eine Prüfung der tatsächlich gerenderten Vorlage mit dem echten Provider war rot und ist jetzt grün.

### 106. Einstellungsnamen haben eine gemeinsame Quelle

**Low, Struktur und Bedienung.** Die Registerkarten und der Navigationspfad lesen ihre Abschnittsnamen jetzt aus derselben Zuordnung. Vorher fehlte „KI-Wissen“ in der separaten Navigationstabelle und der Pfad zeigte stattdessen „Integrationen“. Neue Abschnitte müssen damit nicht mehr in zwei verschiedenen Namenstabellen nachgetragen werden.

### 107. Grafana erhält den angekündigten Dashboardkatalog

**Medium, Funktion.** Die vorhandene Providerdatei wird durch eine gezielte Gitignore-Ausnahme tatsächlich ausgeliefert. Nur Dashboard-JSON-Dateien zu mounten genügte für den Autoimport nicht. Offlineprüfungen bestätigen Mount, Provider und zehn Dashboardkennungen gegen den offiziellen Provisionierungsvertrag; kein Containerlauf behauptet.

### 108. Fehlgeschlagene Energieaktionen melden einen Fehler

**Medium, Funktion.** Herunterfahren, Neustarten, Abmelden und Ruhezustand prüfen jetzt den Rückgabecode von Windows. Lehnt Windows beispielsweise einen Neustart wegen fehlender Rechte ab, gilt der Schritt als fehlgeschlagen und behält die Fehlermeldung. Vorher konnte der Workflow trotzdem Erfolg melden und weitere Schritte ausführen. Tests ersetzen das Windows-Programm durch eine harmlose Funktion und prüfen Erfolg sowie Fehler für alle vier Aktionen; es wird kein Rechner heruntergefahren.

### 109. Workflow-Auswahl berücksichtigt auch ältere Workflows großer Installationen

**Medium, Funktion.** Wartungsfenster, beide Alarmregel-Editoren und die Unterworkflow-Prüfung verwenden jetzt die vorhandene vollständige Namensliste mit gemeinsamem Cache. Die vorher genutzte Übersicht liefert höchstens 500 Workflows. In großen Installationen konnte ein älterer Workflow deshalb nicht für Wartung oder Alarme ausgewählt werden oder erschien im Designer fälschlich als unbekannt. Die Namensliste beachtet weiterhin die serverseitigen Leserechte. Eine echte Auswahlprüfung war vorher rot; 151 Tests der betroffenen Seiten sind grün.

### 110. Namen geplanter Aufgaben bleiben auch in Fehlermeldungen reiner Text

**Medium, Sicherheit.** Der Fehlerpfad des lokalen Aufgabenplaner-Ersatzes setzt den Aufgabennamen jetzt als Text in die Meldung ein. Vorher konnten besondere Zeichen in einem dynamischen Namen den erzeugten PowerShell-Code verändern oder einen eingebetteten Ausdruck ausführen. Zwei Tests mit Anführungszeichen und einem harmlosen Markerausdruck reproduzierten das Problem. Der gemeinsame Debug-, Energieaktions- und Aufgabenplaner-Testlauf ist mit 118 Fällen grün.

### 111. Audit-Verlauf bleibt beim Filtern und Nachladen zusammenhängend

**Medium, Funktion und Struktur.** Alle geladenen Seiten gehören jetzt gemeinsam zum jeweiligen Filter im Query-Cache. Verspätete Antworten eines alten Filters können keine fremden Einträge mehr anhängen; schnelle Doppelklicks laden dieselbe Seite nur einmal. Beim Aktualisieren wird die Seitenfolge anhand der neuen Fortsetzungsmarken aufgebaut. Vorher konnten bei der Untersuchung eines Vorfalls Einträge doppelt erscheinen oder in der Anzeige fehlen. Drei echte UI-Regressionen waren rot; anschließend bestehen alle 23 Audit-Seitentests sowie TypeScript und fokussierter ESLint.

### 112. Inhaltsverzeichnis folgt dem Sprachwechsel der Dokumentation

**Low, Funktion.** Beim Wechsel zwischen Deutsch und Englisch liest das Inhaltsverzeichnis die neuen Überschriften und Sprungziele ein. Vorher konnte es auf derselben Dokumentationsseite in der alten Sprache stehen bleiben und auf nicht mehr vorhandene Abschnitte zeigen. Der kleine Fix ist durch Quellgegenprüfung, TypeScript und die bestehende Dokumentationssuite mit 221 Tests geprüft; ein gezielter Browsernachweis steht noch aus.

### 113. Eine verlorene Datenbankbestätigung macht einen fertigen Restore nicht rückgängig

**Medium, Datenkonsistenz und Berechtigungen.** Speichert die Datenbank ein Backup erfolgreich, aber die Verbindungsbestätigung geht verloren, prüft der Restore jetzt eine Kennung in derselben Transaktion. Vorher konnten die Daten bereits geändert sein, während die Einstellungen zurückgesetzt wurden und alte Live-Abonnements bestehen blieben. Ein bestätigter Commit behält seine Einstellungen und entzieht überholte Abonnements; ein tatsächlich gescheiterter Commit setzt die Einstellungen zurück. Ist auch die Prüfung nicht erreichbar, wird nicht blind erneut importiert oder zurückgeschrieben: bestehende Livezugriffe werden vorsorglich entzogen und der Betreiber erhält eine konkrete Prüfmeldung. Drei lokale Fehlerfälle decken diese Zustände ab; alle 87 Backup-Tests sind grün.

### 114. LoadHarness-Dashboard verwendet vorhandene Metriken und wird eingebunden

**Low, Funktion.** Histogrammnamen und Millisekundeneinheiten stimmen mit dem Producer überein; die Warteschlangenlänge bleibt ein Momentanwert statt einer Rate. Provider, Datasource und Mount ergänzen den dokumentierten Autoimport. Drei Konfigurationsprüfungen sind grün; kein Grafana-Lauf wurde ausgeführt.

### 115. Ein fehlgeschlagenes ZIP-Backup erhält das vorige Archiv

**Medium, Funktion.** Die Komprimierung erstellt zunächst eine temporäre Datei und ersetzt das alte Archiv erst nach vollständigem Erfolg. Vorher wurde die alte Sicherung sofort gelöscht. Besonders ein Backup im eigenen Quellordner scheiterte beim zweiten Lauf und verlor dabei die vorherige Sicherung. Das Ziel wird jetzt aus den Eingabedateien ausgeschlossen; eine identische einzelne Quelle und Zieldatei wird abgelehnt. Tests prüfen den wiederholten Lauf, gesperrte Eingaben und unveränderte alte Dateien bei Fehlern.

### 116. Eine Textersetzung erhält den letzten Zeilenumbruch

**Low, Funktion.** Die Ersetzung arbeitet jetzt mit dem vollständigen ursprünglichen Text einschließlich seines letzten Zeilenumbruchs. Vorher konnte schon eine Suche ohne Treffer diesen Umbruch entfernen, obwohl der Schritt null Änderungen meldete. Bewusst hinzugefügte oder entfernte Umbrüche bleiben weiterhin möglich; Tests prüfen LF und CRLF an echten temporären Dateien.

### 117. Geführte Demo behandelt deaktivierte Schritte nicht als ausgeführt

**Low, Funktion.** Der Missionsplan prüft die aktive Schrittmenge und meldet bei fehlenden benötigten Schritten den vorhandenen Graphänderungsfehler. Drei Missionsfälle waren rot; sämtliche 139 Demo-Tests sind grün.

### 118. Weitere Seiten einer Logdatei bleiben lesbar

**Low, Funktion.** Das Agent-Dateilesewerkzeug erkennt die Zeichenkodierung am Dateianfang und beendet eine Seite erst nach einem vollständigen Zeichen. Vorher war die zweite Seite einer UTF-16-Datei falsch dekodiert; UTF-8-Zeichen konnten an der Seitengrenze zerfallen. Beispielsweise bleiben damit Umlaute und andere mehrteilige Zeichen auch beim Weiterlesen eines längeren Logs erhalten. Die Rohdatenübertragung für gesammelte Logdateien bleibt unverändert.

### 119. Eine erfolglose Ausführungssuche lässt sich direkt korrigieren

**Low, Funktion.** Suchfeld und Filter bleiben auch ohne Treffer sichtbar. Vorher verschwanden sie gerade dann, wenn ein Tippfehler oder Statusfilter keine Läufe fand; zum Zurücksetzen musste man die Seite neu öffnen. Ein echter Nulltreffer mit anschließender Rückkehr zur Liste war rot und ist jetzt grün.

### 120. Ausführungssuche berücksichtigt Namen und verkürzte IDs vor dem Blättern (Low)

- **Problem:** Die Oberfläche suchte zusätzlich nach dem auslösenden Benutzer und ID-Teilen, nachdem der Server bereits eine Seite ausgewählt hatte. Ältere passende Ausführungen blieben dadurch unsichtbar.
- **Korrektur:** Diese Suchfelder gehören jetzt zur serverseitigen Suche vor Zählung und Seitenauswahl. Die redundante Textsuche auf der einzelnen Browserseite entfällt; Berechtigungsfilter bleiben erhalten.
- **Im Alltag:** Wer nach einem Benutzer oder einer kopierten kurzen Ausführungs-ID sucht, findet auch passende ältere Läufe und erhält eine passende Trefferzahl.
- **Nachweis:** Zwei ursprüngliche Regressionen rot, danach 79 Ausführungs-/Berechtigungstests und 42 kombinierte UI-Fälle grün. PostgreSQL-/SQL-Server-Übersetzung zusätzlich anhand der offiziellen Providerquellen geprüft; kein Live-Datenbanktest behauptet.

### 121. Der Schnell-Editor erhält Rückgabedaten als Objekt

**Medium, Funktion.** Ein Doppelklick auf einen Rückgabeschritt zeigt seine Daten jetzt als lesbares JSON und speichert wieder ein Objekt. Vorher verwandelte bereits unverändertes Speichern die vorhandenen Felder in Text; der nächste Workflowlauf konnte damit keine Rückgabedaten mehr liefern. Ungültiges JSON, Arrays und einzelne Textwerte bleiben mit einer verständlichen Fehlermeldung im Editor. Fünf zuvor rote Fälle sind grün, einschließlich unverändertem Speichern und echter Änderung.

### 122. Mehrfachauswahl umgeht die Schreibsperre nicht mehr in der Oberfläche

**Low, Funktion.** Leser und Benutzer ohne eigene Bearbeitungssperre können weiterhin mehrere Schritte auswählen, aber die Sammeländerung bleibt gesperrt. Vorher ließ sich die lokale Darstellung damit verändern, obwohl der Server das spätere Speichern verweigerte. Das echte rechte Designerpanel prüft nun dieselbe Schreibfähigkeit wie einzelne Eigenschaften; der Page-Handler prüft sie zusätzlich. Der Leserfall war rot, der Bearbeiterfall bleibt funktionsfähig.

Dasselbe erforderliche Schreibrecht gilt für Ersetzen sowie Tidy/Restore im kompakten Header. Lesendes Suchen bleibt verfügbar. Es gibt keinen permissiven Standardwert für canWrite; der produktive Besitzer muss ihn ausdrücklich übergeben.

### 123. Laufende Jobs öffnen ihre Liveansicht

**Low, Funktion.** Ein Klick auf einen laufenden Job im Dashboard öffnet jetzt seinen Workflow mit der bestehenden Liveanzeige. Vorher führte der Link zur Historie, die ausschließlich abgeschlossene Läufe lädt und diesen Job daher nicht zeigte. Abgeschlossene Jobs öffnen weiter ihre Historie. Eine Navigationsprüfung war vorher rot.

### 124. Custom-Activity-Warnungen lassen sich ohne Versionskonflikt korrigieren

**Low, Funktion.** Bleibt der Editor nach dem Speichern wegen Hinweisen geöffnet, übernimmt er jetzt die gespeicherte ID und neue Versionskennung. Vorher schlug die nächste Korrektur mit einem Konflikt fehl oder versuchte erneut, dieselbe Aktivität anzulegen. Die Warnungen und eingegebenen Felder bleiben erhalten. Ein Test belegte die veraltete Kennung; der gemeinsame Seitenlauf für #119/#123/#124 besteht mit 90 Tests.

### 125. Geschlossene Schnell-Editoren hinterlassen keinen Außenklick-Listener

**Low, Lebenszyklus.** Wird der Schnell-Editor sofort wieder geschlossen, wird auch die geplante Registrierung seines Außenklick-Listeners entfernt. Vorher konnte der Listener erst nach dem Schließen angelegt werden und bis zum Neuladen der Seite hängen bleiben. Eine Regression schließt vor Ablauf des Timers; zusammen mit den Rückgabedatenfällen bestehen alle 17 Schnell-Editor-Tests.

### 126. Maschinen-Sammeltest hat eine feste Parallelitätsgrenze (Medium)

- **Problem:** „Alle testen“ startete für jede Maschine gleichzeitig einen Remote-Verbindungsversuch. Der Verbindungspool begrenzt nur Versuche pro Ziel, nicht die Anzahl unterschiedlicher Ziele.
- **Korrektur:** Der vorhandene Sammeloperationsmechanismus verteilt die Arbeit auf höchstens vier gleichzeitige Versuche. Doppelklicks starten keinen zweiten Stapel; nach Verlassen der Seite oder einem Identitätswechsel beginnen keine weiteren Tests.
- **Im Alltag:** Auch große Maschinenlisten verursachen keinen plötzlichen Schwung hunderter Remote-Verbindungen. Ein einzelner unerreichbarer Rechner verhindert die übrigen Ergebnisse nicht.
- **Nachweis:** Zwölf gleichzeitige Versuche vor der Korrektur reproduziert; anschließend maximal vier und alle zwölf eindeutigen Ziele einschließlich eines Fehlerfalls geprüft. Kombinierter Seitenfokus 42/42 grün; unabhängige Gegenprüfung des WinRM-Pools.

### 127. Gemeinsamer Lebenszyklus für die Spaltenbreiten von fünf Tabellen (Low)

- **Problem:** Fünf Seiten enthielten fast denselben Maus-Handler. Beim Verlassen der Seite während des Ziehens blieben dessen globale Ereignislistener bestehen.
- **Korrektur:** Ein gemeinsamer Hook verwaltet Breiten und aktive Ziehbewegung; er entfernt Listener beim Loslassen, Wechsel der Spalte und beim Entfernen der Tabelle. Individuelle Mindestbreiten bleiben erhalten.
- **Im Alltag:** Tabellen reagieren nach Seitenwechseln nicht mehr auf eine alte Ziehbewegung. Künftige Korrekturen müssen nur noch an einer Stelle erfolgen.
- **Nachweis:** Echter Maschinen-Seitenwechsel während des Ziehens zunächst rot, danach 74 Seitentests grün. Mindestbreiten, Wechsel der aktiven Spalte und Aufräumen zusätzlich im gemeinsamen Hook geprüft; zusammen mit Editor/Koordinatenbibliothek 129/129 grün.

### 128. Escape schließt Dialoge auch aus ihrem Eingabefeld

**Low, Funktion.** ModalShell, Bestätigungsdialog und Zelleditor behandeln Escape vor dem bewussten Stoppen der Ereignisweitergabe. Verschachtelte Dialoge schließen nur innen; Editoren können Escape weiter selbst beanspruchen, und laufendes Speichern bleibt geschützt. Drei Regressionen waren rot; 41 Modal-/DbViewer-Tests sind grün.

### 129. Große Workflowbestände bleiben vollständig erreichbar (Medium)

- **Problem:** Die Liste lieferte höchstens die 500 zuletzt geänderten Workflows. Ordnerfilter und Suche erfolgten danach im Browser; ältere Workflows konnten im eigenen Ordner unsichtbar bleiben.
- **Korrektur:** Der gemeinsame Seitenendpunkt filtert Berechtigungen, Ordner und Suchtext und sortiert vor der Seitenauswahl. Statistik-/Capabilities-Projektion bleiben gemeinsam; höchstens 200 Einträge pro Seite. Workflowseite, Designer-Browser, Ordner-Popovers und Schnellwechsel nutzen Seiten. Die letzten zehn besuchten Workflows werden in einem begrenzten Batch geladen. Der tatsächlich von CLI/MCP genutzte ältere Arrayvertrag bleibt begrenzt erhalten.
- **Im Alltag:** Auch der jahrelang unveränderte Monatsabschluss lässt sich finden und öffnen. Sortieren nach Laufzeit oder Erfolgsquote gilt für den gesamten berechtigten Bestand.
- **Nachweis:** Elf ursprüngliche API-Fälle rot, danach 86/86 Workflow-/RBAC-Fälle grün. Zusätzlich 17/17 Pagingfälle inklusive Offline-Übersetzung mit echten PostgreSQL-/SQL-Server-Providern. Zwei Designer-Pagingfälle und ein Schnellwechsel-Fall zuerst rot; final 52/52 UI-/FindReplace-Fälle, 93/93 Workflowseiten- und 62/62 Demofälle grün. TSC und fokussierter ESLint ohne Fehler/Warnung. Keine Live-Datenbank-/Browserprüfung behauptet.

### 130. Einstellungsentwürfe und ihre Versionskennung bleiben bei Reconnect erhalten

**Medium, Funktion und Struktur.** Ein erneutes Laden im Hintergrund überschreibt keine ungespeicherten Einstellungen mehr. Eine gemeinsame Formular-Seam besitzt Entwurf, dazugehörigen ETag, Speichervorgang und exakten Payload für Konfliktwiederholungen. Erst ein bestätigter Save oder ausdrücklich übernommener Serverstand ersetzt den Entwurf. Das gilt auch für neue Geheimnisse und wiederholte 412-Konflikte. Ein echter Reconnect reproduzierte den Verlust; 97 unterschiedliche Settings-Tests und TypeScript sind grün.

### 131. Suchsprünge berücksichtigen verschobene Gruppen (Low)

- **Problem:** Der Designer verwendete die Position eines Gruppenkindes relativ zur Gruppe als absolute Canvasposition. Bei verschobenen Gruppen sprang die Ansicht deshalb an die falsche Stelle.
- **Korrektur:** Sprünge zu Knoten und Verbindungen verwenden die bereits vorhandene Umrechnung in absolute Koordinaten.
- **Im Alltag:** Ein gefundener Arbeitsschritt oder eine Verbindung wird auch innerhalb einer verschobenen Gruppe tatsächlich in die Bildschirmmitte gebracht.
- **Nachweis:** Unabhängige Gegenprüfung von Koordinatenvertrag und vorhandener Umrechnung; gemeinsamer Editor-/Koordinaten-/Resize-Fokus 129/129 grün. Kein gesonderter Browsernachweis behauptet.

### 132. Offene Operations-Details laden die endgültige Fehlerdiagnose nach

**Low, Funktion.** Wechselt ein beobachteter Lauf von Running zu Failed, werden Fehlertext, fehlgeschlagene Schritte und Endzählung erneut geladen. Vorher änderte sich nur die Statusanzeige. Eine gemountete Komponente reproduzierte die fehlende Diagnose; anschließend bestehen alle 39 Operations-Detail-/Seitentests.
