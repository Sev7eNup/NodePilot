# Diagnose und belastbare Belege

## Operation statt Fehlerwort verfolgen

Ordne den Ablauf: Policy/Zuweisung → Scan/Anwendbarkeit → Contentlocation → Transfer
→ Integritäts-/Signaturprüfung → Ausführung → Detection → Statusmeldung/Verarbeitung.
Ein Fehler in einer später erfolgreichen Fallback-Verbindung erklärt keinen vorherigen
leeren Location-Response. HTTP 200 belegt weder vollständige Bytes noch gültige Signatur.
Ein Fehlercode gibt die fehlschlagende Schicht an, nicht automatisch die Ursache.

Objekttabelle führen: Maschine, Request/Job, Update-GUID/CI, Assignment, ContentID,
PackageID/SourceVersion, Application- und DeploymentType-ID samt jeweiliger Version.
Namen dienen der Suche, nicht als Identitätsbeweis. Kein lokales CI_ID-Feld erfinden,
weil eine Serverklasse eines besitzt. Inventar und aktuelle Beobachtung unterscheiden.

## Logkarte

Pfade aus Installation und tatsächlichen Verzeichnissen ermitteln; diese üblichen
Orte sind Einstiegspunkte, keine Garantie für eine bestimmte Maschine:

| Phase | Quellen |
|---|---|
| Clientinstallation/-dienst | Windows\ccmsetup\Logs\ccmsetup.log; CCM\Logs\CcmExec.log, CcmEval.log; Systemereignisse |
| Scan | CCM\Logs\WUAHandler.log, ScanAgent.log; vorhandenes WindowsUpdate.log; WindowsUpdateClient-Ereignisse |
| Updateinstallation | UpdatesDeployment.log, UpdatesHandler.log, UpdatesStore.log, ServiceWindowManager.log; CBS/DISM |
| Content | CAS.log, ContentTransferManager.log, DataTransferService.log, LocationServices.log |
| Policy und Reporting | PolicyAgent.log, PolicyEvaluator.log, CcmMessaging.log, StateMessage.log; Server statesys.log |
| Anwendung/Programm | AppIntentEval.log, AppDiscovery.log, AppEnforce.log, execmgr.log |
| Tasksequenz | smsts.log, je Phase unter CCM\Logs\SMSTSLog, CCM\Logs, _SMSTaskSequence\Logs oder WinPE Windows\Temp\SMSTSLog |
| Site/SUP/DP | tatsächliches ConfigMgr-Logs-Verzeichnis: distmgr.log, PkgXferMgr.log, SMSDPProv.log, WCM.log, WSUSCtrl.log, wsyncmgr.log; IIS-Sitelogs |
| Betriebssystem | Windows\Logs\CBS, Windows\Logs\DISM, Windows\Panther, $WINDOWS.~BT\Sources\Panther |

`files_list` kann nur die direkte Ebene und einen Filter abdecken. Unterverzeichnisse
gezielt auflisten; IIS-Site-ID vor Wahl von W3SVC-Unterordnern feststellen. Fehlende
Logs am geratenen Ort beweisen keinen fehlenden Dienst. Vorhandene Windows-Update-
Textkonvertierungen dürfen anders heißen: Dateiliste, Änderungszeit und Inhalt prüfen.

## Umfang, Zeit und unvollständige Daten

- Große Logs vorfiltern, blockweise durchsuchen und nur relevante Ausschnitte laden.
  NodePilot begrenzt gesammelte Rohlogs gemeinsam pro Lauf auf 250 MB. Nicht alles in
  den Modellkontext kopieren. Quellenpfad, Zeile/Offset, Encoding und Suchumfang erhalten.
- Suchtreffer mit Kontext lesen. Abgeschnittene Token oder negative Suche in einem
  gespeicherten Ausschnitt nicht als Abwesenheit im Original werten. Originalseite
  nachladen. Literale und Regex unterscheiden; bei fehlender Regex-Unterstützung
  mehrere Literale verwenden. Negative Treffer nur für tatsächlich geprüften Umfang.
- Rotation, Trunkierung, Anhängen und sehr lange Zeilen beachten. Alte und neue Datei
  nicht zu einer scheinbar durchgehenden Momentaufnahme zusammensetzen. Geänderte
  Dateilänge/-zeit notieren und entscheidende Passage erneut lesen.
- Rohzeit, Format, Zielzeitzone und Beobachtungszeit separat behalten. IIS-W3C-Zeit ist
  UTC. CMTrace verwendet Minuten-Bias: bei bestätigt +420 bedeutet 14:00 lokale Zeit
  21:00 UTC, nicht 07:00 UTC. DMTF `+000` ist dagegen ein UTC-Offset. Unbekannte oder
  widersprüchliche historische Offsets nicht erraten; Intervall statt Scheingenauigkeit.
- Scheduling-DMTF mit unbekanntem Offset (`+***`) und UseGMTTimes=false als lokale
  Wanduhrzeit behandeln. CIM-Konvertierung kann irreführen; Rohwerte und Provider-
  Semantik mit ServiceWindowManager/Assignment-Logs abgleichen.
- Doppelte Herbststunde ohne Offset ist mehrdeutig, eine übersprungene Frühlingsstunde
  kann ungültig sein. Aktuelle Zeitzone beweist keine historische Einstellung.
  Legacy-JSON-Epochwerte nicht ohne verlässliche Konvertierung erraten. Dateizeit und
  Ereigniszeit sind unterschiedliche Belege, keine austauschbaren Zeitstempel.
- Fehlende Spalte, lazy property, nicht vorhandene Klasse, Zugriff verweigert,
  Ausgabelimit, leere erfolgreiche Abfrage und echtes fehlendes Objekt unterscheiden.
  Schema lesen oder eng begrenzte ungefilterte Gegenprobe, statt dieselbe falsche
  Abfrage zu wiederholen. Eine nicht prüfbare Quelle als Grenze benennen.
- Inventare als Mengen nach stabiler Identität vergleichen, nicht nach Zeilenreihenfolge.
  Fehlend in einer ausdrücklich unvollständigen Liste bedeutet nicht fehlend im System.
  Ohne vollständige Gegenquelle bleibt der Vergleich teilweise; keine definitive
  Lösch-/Publikationsreparatur daraus ableiten. Unveränderte Reihenfolge ist unwichtig.
- Quellenpfad/Identifier und Position getrennt speichern. Wörtliche Zitate müssen
  zusammenhängend im Original vorkommen; keine erfundenen Zeilen oder zusammengeklebten
  Ausschnitte. Bei strukturiertem Ergebnis die verlangten Feldtypen exakt einhalten.

## 40 — Historische Fehler trotz aktuell gesundem Ablauf

**Symptom:** Alte Fehler stehen neben späteren Erfolgen; Reviewer verlangen Reparatur.
**Prüfen:** Ist der spätere Erfolg dieselbe Operation auf demselben Ziel/Objekt/Version?
Scanerfolg beweist keine Berichtszustellung. Ein leerer TCP-Snapshot zwischen Scans
beweist keinen Verbindungsfehler. Unvollständige Logs beweisen keinen Erfolg.
**Behebung:** Bei belegtem aktuellem Erfolg keine Reparatur empfehlen. Historische
Ursache nur mit zeitgenössischen Belegen erklären; nicht aus gesundem Jetzt ableiten.
**Kontrolle:** Letzter passender Vorgang erfolgreich, keine danach belegte gleiche
Störung. Ungeprüfte Funktionen ausdrücklich offen lassen.

## Review und Abschluss

Reviewer prüft die entscheidende Objektzuordnung, rohe Werte, Kausalkette und gezielte
Maßnahme. Eine Rückfrage braucht eine diskriminierende nächste Leseprüfung. Bekannte
Feststellungen nicht ständig umformulieren oder geschlossene Einträge erneut ändern.
Neue Quelle/Version verändert nur betroffene Schlussfolgerungen. Budget für Abschluss
reservieren. „Ursache unbekannt“ ist ehrlich, wenn kein weiterer erlaubter Beleg verfügbar
ist; ein nachgewiesener Blocker darf trotz unbekanntem Änderungsautor benannt werden.
Eine Reparaturerfolgskontrolle ist nachgelagert und kein Pflichtbeweis vor Beendigung
der lesenden Diagnose. Vorliegende Teilbefunde bei Timeout erhalten.

Quelle: [IIS-W3C-Zeitfelder](https://learn.microsoft.com/en-us/windows/win32/http/w3c-logging).
