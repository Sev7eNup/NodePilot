# Strukturierte Übergaben und begrenzte Modellwiederholung

Status: Punkt 4 nach negativem Livevergleich vollständig zurückgenommen.
Punkt 6 bleibt implementiert; abschließender Smoke-Test des isolierten Endstands ausgewertet.
Benutzerauftrag: Punkte 4 und 6 umsetzen, die fünf bisherigen Fehlerfälle erneut
testen, Unterschiede messen und den bisherigen Stand wiederherstellbar halten.
Punkt 5 wurde ausdrücklich aus diesem Auftrag entfernt; Punkte 3 und 5 bleiben offen.

## Entscheidung und verbleibender Endstand

Die Qualitätsvorgabe hat Vorrang. Die Varianten für Punkt 4 erzeugten zusätzliche
Untersuchungs-/Reviewrunden, teils unvollständige Freigaben und beim Konfigurationsfall
weniger präzise Begründungen als die Referenz. Vier vollständige Fünferserien und
der fünfte Binding-Lauf werden einschließlich negativer Ergebnisse aufbewahrt.
Der bereits gestartete fünfte ACL-Lauf wurde nach Ablehnung des Kandidaten bei
126 Aufrufen gezielt abgebrochen; die übrigen drei Fälle dieser Variante wurden
nicht gestartet. Er zählt weder als bestandener noch als natürlicher Fehlerlauf.

`AgentRuntime.cs`, `AgentInvestigation.cs`, `AgentConclusion.cs` und
`AgentRuntimeTests.cs` sind wieder byteidentisch mit dem Stand vor 4/6.
Auch die zusätzlichen technischen Rückgabewege sind entfernt. Die früheren
Optimierungen 1/2 bleiben erhalten. Sämtliche Testfehler, ACLs, eigene Testassets
und ursprünglichen VM-Zustände sind wiederhergestellt; der Hardcore-Testworkflow
ist unverändert deaktiviert. Nachweis der abgebrochenen letzten Serie:
`.runlogs/handoff-retry-baseline-guidance-20261007/verification-selected.json`.

Übernommen wird ausschließlich Punkt 6: maximal eine Wiederholung desselben
Modellrequests bei Timeout, 408/429/500/502/503/504 oder positiv identifiziertem
Socket-Reset/Abbruch bzw. vorzeitig beendetem HTTP-Response. TLS/Authentifizierung,
DNS und ConnectionRefused bleiben terminal. Keine Werkzeug-/Workflowwiederholung,
keine geänderten Rollen, Werkzeuge, Berechtigungen oder allgemeinen Prompts.
Gemeinsame Aufruf-/Zeitlimits und Abschlussreserve bleiben bindend.

Reale Recovery-Belege: vierter Binding-Lauf Journal 263 (Timeout) und 803
(Unreachable), vierter Filter-Lauf Journal 355 (Unreachable); jeweils fortgesetzt
und completed, ohne Transport-Replay der Werkzeuge. Die Diagnosebewertungen dieser
Kandidaten bleiben dennoch eigenständig und werden nicht allein dadurch freigegeben.
Der isolierte Endstand hat 167 gezielte AI-Tests bestanden. Finale Artefakte und
unveränderter ursprünglicher `[Agent Check] luna smoke`: `.runlogs/model-retry-final-20261007/`.

## Abschließender Smoke-Test: nur Punkt 6

Der unveränderte Workflow `[Agent Check] luna smoke` (Version 18, weiterhin aktiviert)
beendete Ausführung `89694d35-015a-446b-bd44-8cf9d05d6399` am 07.10.2026
um 03:24:06 UTC technisch erfolgreich: `Succeeded`, fachlich `partial`, ohne
Lauffehler. Dauer 343,6 Sekunden, 74 Modellaufrufe, 123 Werkzeugaufrufe,
6 Delegationen. Beide Reviewer lieferten abgeschlossene Bewertungen; der
Belegreview wurde nach einer Rückfrage korrigiert abgeschlossen. Das Journal
ist lückenlos, der Abschlussbericht gültiges JSON mit allen geforderten Feldern.

Der Bericht belegt zwei 0xD1-Bugchecks im konfigurierten 90-Tage-Fenster und zwei
weitere unerwartete Neustartfolgen ohne Bugcheck-Nachweis. Ereigniszeiten und
gemeldete frühere Abschaltzeiten werden unterschieden. Die Ursache bleibt offen;
Dumps wurden nicht analysiert und Application-Ereignisse nur unvollständig
erfasst. Diese Grenzen stehen ausdrücklich im Bericht und begründen `partial`.
Dies ist kein Nachweis einer vollständig abgeschlossenen Ursachenanalyse oder
einer garantierten Qualitätsgleichheit sämtlicher künftiger Modellläufe.

Im Smoke gab es keinen transienten Modellfehler und somit keinen Retry. Die
Retry-Wirkung ist durch die oben genannten realen Recovery-Ereignisse und die
Regressionstests belegt. Zehn zurückgewiesene Werkzeugaufrufe (ungültige
Get-WinEvent-Kombinationen, Read-only-Regeln, zu lange Registertexte) bleiben
sichtbar; sie wurden nicht als Transportfehler wiederholt. Der Smoke ist daher
kein Beleg für fehlerfreie Werkzeugplanung oder eine Effizienzverbesserung.

Endstand: 167 ausgewählte AI-Tests und 13 Engine-Tests bestanden. API-Build
erfolgreich mit 55 vorhandenen Warnungen außerhalb dieser Änderungen.
AI-DLL SHA256: `10CD343E0F193DB1B681E5EAEECB18BD0ED674944A393B821DF632B9C81BF3C4`.
Artefakte: `.runlogs/model-retry-final-20261007/` (`smoke-result.json`,
`smoke-report.txt`, `smoke-audit.json`, Test- und Buildlogs).

## Rücksetzpunkt

`.runlogs/handoff-remedy-checkpoint-20261007/` enthält vierzehn exakte Vorher-Dateien,
SHA-256-Prüfsummen, Nachher-Dateien, Änderungspatch und `restore.ps1`.
Der Verzeichnisname stammt von vor der Auftragskorrektur auf 4 und 6.
Das Skript prüft standardmäßig nur; `-Apply` stellt ausschließlich die aufgeführten
Dateien wieder her, wenn alle erwarteten Hashes stimmen. Spätere Änderungen führen
vor jedem Schreiben zum Abbruch. Der offene Merge und andere Änderungen bleiben
unberührt. Der ältere Rücksetzpunkt vor Optimierungen 1 und 2 bleibt erhalten.
Aktive Läufe müssen vor dem Rücksetzen beendet und Testfehler entfernt werden;
danach ist ein Dev-Neubau und Neustart erforderlich.

## Erprobte erste Umsetzung (historisch, Punkt 4 zurückgenommen)

Übergaben strukturieren den bestehenden Antworttext in belegte Befunde mit
Originalquellen und Ziel/Zeit/Umfang, Unsicherheit und Gegenbelege, die nächste
fehlende Information samt betroffener Aufgabenanforderung und tatsächliche
Fähigkeitsgrenzen. Der Supervisor prüft geeignete Mitglieder und erlaubte Methoden,
bevor er eine Information für unerreichbar erklärt. Optionale zusätzliche Methoden
oder vorgeschlagene Erfolgskontrollen werden nicht zu neuen Pflicht-Ergebnissen.
Echte Informationslücken und blockierte Registerfragen bleiben verbindlich.
Dies ist Modellführung im vorhandenen Protokoll, kein neues starres Antwortschema.

Modell-Timeouts und HTTP 408/429/500/502/503/504 erlauben maximal einen weiteren
Versuch desselben Modellrequests nach einer abbrechbaren Sekunde. Jeder Versuch
zählt zum gemeinsamen Budget. Werkzeuge, Delegationen und Workflow-Aktivitäten
werden nicht wiederholt. Authentifizierungsfehler, andere HTTP-Fehler, unerreichbare
Endpunkte einschließlich Zertifikatsproblemen, ungültige/trunkierte Antworten und
Werkzeugfehler lösen keine Wiederholung aus. Dauerhafte Fehler bleiben Fehler;
ein vorhandener Berichtsentwurf bleibt als vorläufiger Befund erhalten.

Die Retry-Zulassung erhält die Abschlussbericht-Reserve. Bei Werkzeugantworten und
Kontext-/Evidenzzusammenfassungen bleibt zusätzlich ein Aufruf für die Agentenantwort
frei, atomar gegenüber gleichzeitig laufenden Mitgliedern geprüft.

## Tests und Builds

161 gezielte AI-Tests und 13 Engine-Tests bestanden. Neue Retry-Tests wurden vor der
Implementierung rot geprüft; der zusätzliche Zusammenfassungs-Budgetfall ebenfalls.
Abgedeckt: identischer Request nach vorübergehendem Fehler, Timeout vor Headern und
im Body, erfolgreicher vollständiger Teamabschluss ohne erneutes Lesen, dauerhafter
Fehler, Abbruch während Wartezeit, parallele Budgetzulassung und Berichtserhalt.

Erster Live-Build: Ai-SHA256
`1E43A2DADA5A21605ADA50622C2CECF4572B44CEF9A7C00E5769C272DC0E17D9`.
Während der unverändert laufenden Serie wurde die zusätzliche Budgetabsicherung
für Arbeitszusammenfassungen und parallele Retry-Zulassung fertiggestellt.
Sie ist separat getestet und gebaut; die erste Serie verwendete sie noch nicht.
Die beiden Quellstände sind separat gesichert (`after-live-first/` und `after/`).
Liveergebnisse dürfen nicht als Ausführung des späteren Budget-Grenzfalls ausgegeben werden.

## Vergleichsmethode

Unveränderter Workflow `[Agent Check] Hardcore Five 20261006`, Version 2,
fünf Luna-Mitglieder, 200 Modell-/500 Werkzeugaufrufe, 30 Delegationen, 1800 Sekunden.
Reale Fehler: Binding, ACL, Request-Filter, Konfiguration und MIME. Die Agenten sehen
Symptome und Sollvertrag, nicht die injizierten Ursachen. Nach jedem Fall unabhängige
Wiederherstellung und Prüfung aller sechs HTTP-Verträge.

Referenz ist je Fall der letzte erhaltene erfolgreiche Lauf: Binding und MIME aus
dem korrigierten Vergleich von 1/2; ACL, Filter und Konfiguration aus dessen erster
Serie. Insbesondere der Filter-Referenzlauf enthält noch den später korrigierten
Fehler bei Review-Abhängigkeiten. Ein Unterschied dazu darf nicht allein 4/6
zugeschrieben werden. Dies ist keine frische Fünferserie mit einem einzigen
Referenzbuild. IIS-Loghistorie wächst, Modellverhalten und Laufzeiten schwanken.

Messung: Laufzeit, Modell-/Werkzeugaufrufe, Delegationen, Reviews, Tokenverbrauch,
exakte Wiederholungen von Originalabfragen, Fehler und Retries. Fachliche Prüfung:
richtige Ursachen, Gegenbelege, minimale bedingte Reparatur, vollständiger Bericht,
echte Rückfragen und keine künstlichen Pflichtprüfungen. Niedrigere Kosten allein
reichen nicht zur Abnahme; relevante alternative Ansätze bleiben erlaubt.

Belege und laufende Messwerte: `.runlogs/handoff-retry-five-20261007/`.
Die erste Serie ist vollständig beendet, alle Fehler zurückgenommen, Original-ACLs
geprüft und eigene Labobjekte entfernt. Alle fünf Journale sind lückenlos; die
fachlichen Abweichungen unten verhindern trotzdem eine Abnahme dieses Erststands.

| Fall | Referenz: Modell / Werkzeug / Sekunden | Erster Kandidat: Modell / Werkzeug / Sekunden | Bewertung Erstkandidat |
|---|---:|---:|---|
| Binding | 67 / 159 / 405 | 175 / 348 / 1061 | completed, korrekt, deutlich teurer |
| ACL | 72 / 155 / 406 | 71 / 159 / 324 | partial wegen stehengebliebenem Check |
| Filter | 68 / 143 / 392 | 75 / 196 / 314 | completed, beide Filterursachen |
| Konfiguration | 81 / 200 / 521 | 96 / 251 / 529 | completed, aber private Ursache zu ungenau |
| MIME | 50 / 146 / 284 | 51 / 143 / 286 | completed, beide MIME-Ursachen |

Kein natürlicher Modellfehler/Retry in dieser ersten Serie; die Retry-Wirksamkeit
ist durch injizierte Transportfehler in den Regressionstests belegt.

Endgültiger Vergleich: `.runlogs/handoff-retry-final-20261007/`.
Dev-API PID 5700, Health `Healthy`, Ai-SHA256
`7B877BEC4233742AD9C8406667287B15B7FEA43223C6A154089CA9B052F9C6AE`.
Der Build enthält die unten beschriebenen Nachbesserungen. Alle sechs gesunden
Ausgangsverträge wurden vor Beginn erneut geprüft. Kein Build während dieser
zweiten Messreihe; gleicher Workflow, gleiche Limits und Modelle.

## Befund während der ersten Serie

Binding endete vollständig mit korrekter Diagnose und präziser bedingter Reparatur,
benötigte aber 175 statt 67 Modellaufrufe der letzten Referenz. Reviewer fanden
eine berechtigte Grenze der alternativen Reparatur über Site 3. Danach wurde eine
Berichtskorrektur als offene Registerfrage behandelt; eine Änderung dieses Checks
während des Reviews machte beide Freigaben erneut veraltet (Journal 1019, 1079,
1125). Zusätzlich entstanden Schema-Korrekturen und wiederholte Quellenabfragen.
Das ist kein nachgewiesener Effizienzgewinn.

Die Supervisor-Übergabe wurde deshalb präzisiert: Eine reine Korrektur der Deutung,
Formulierung oder bedingten Reparatur wird im Review und Bericht behandelt; eine
fachliche Registerfrage wird nur bei tatsächlich falscher/unvollständiger Grundlage
wieder geöffnet. Nötige sachliche Check-Korrekturen erfolgen vor finalem Review.
Die bestehende Prüfung veralteter Freigaben bleibt unverändert. Diese Präzisierung
war in der ersten Serie noch nicht aktiv und wird in der endgültigen Serie geprüft.

ACL endete mit korrekten Ursachen und korrigiertem Deny-Reparaturvorschlag, aber
`partial` wegen eines stehengebliebenen blockierten Endpunkt-Checks. Spätere
Serveroriginale und Review stützten die Zuordnung; die Runtime gab den blockierten
Check jedoch ohne erneute Übergabe direkt an die finale Einstufung weiter.
Die Promptänderung allein behob diese Übergabelücke nicht.

Die Runtime gibt blockierte Checks deshalb einmal begrenzt an den Supervisor zurück,
nachdem offene Fragen und erforderliche Reviews bearbeitet sind. Er prüft die
tatsächliche Informationslücke gegen inzwischen verfügbare Originale und kann an
einen geeigneten Agenten delegieren. Auflösung braucht weiterhin echte Originalbelege;
danach nötige neue Reviews bleiben Pflicht. Bei fehlender Information bleibt der
Check blockiert und der Bericht ehrlich unvollständig. Kein automatisches Schließen
und keine zusätzliche Endlosschleife. Zwei neue Runtime-Tests reproduzierten die
fehlende Rückfrage vor der Korrektur. Auch ACL muss im endgültigen Build wiederholt werden.

Konfiguration endete zwar `completed`, blieb beim privaten Pfad aber weniger konkret
als die Referenz: ungültige Unterverzeichnis-Konfiguration statt Nachweis des nicht
registrierten Abschnitts. Diese Qualitätsabweichung wird nicht als bestandener
Vergleich gewertet. Die Rückfrage beschränkte sich auf Parser-/FREB-Nachweise;
eine alternative Registrierungsprüfung war in der Referenz erfolgreich gewesen.
Die Übergabeführung verlangt deshalb eine fehlende Tatsache und mögliche alternative
Quellen statt genau einer Pflicht-Messmethode. Verfügbare Konfiguration und Regeln
können geeignete Originalbelege liefern; echte Kausallücken bleiben explizit offen.

Die Endfassung wird erneut mit ALLEN fünf Fällen geprüft, nicht nur Binding/ACL.
Übergabeabschnitte sind Orientierung, keine Pflicht zur zusätzlichen Datensammlung;
Nachfassantworten beschränken sich auf geänderte Befunde und offene Sachfragen.

## Zweiter Kandidat: weitere Regression gefunden

Binding endete mit 200 Modellaufrufen und `partial`. Hauptursache und aktuelle
Request-Zuordnung waren korrekt, aber der alternative Physical-Path-Fix ließ die
effektive MIME-Konfiguration der Shadow-Site offen; erforderliche aktuelle Reviews
waren beim Budgetende nicht vollständig. Dieser Lauf besteht die Qualitätsvorgabe
nicht. Die übrigen vier Fälle laufen unverändert weiter und bleiben im Vergleich.

Der nächste Quellkandidat stellt die frühere knappe Übergabeform wieder stärker
her: keine Wiederholung der Aufgabe, unveränderter Berichte oder Schemas; Fokus auf
die zugewiesene Frage. Befund, Unsicherheit und konkrete fehlende Information bleiben
strukturiert; alternative Originalquellen und tatsächliche Fähigkeitsgrenzen bleiben
erhalten. Breite zusätzliche Formulierungsvorgaben wurden verkürzt. Dies ist zunächst
eine zu prüfende Hypothese zur Regression, kein behaupteter Wirksamkeitsnachweis.
Der zweite Live-Quellstand ist in `after-live-second/` separat gesichert.
Der kompaktere Kandidat wurde nach Abschluss und Aufräumen der zweiten Serie
erneut mit 161 AI- und 13 Engine-Tests geprüft. Build: null Warnungen/Fehler.
Er läuft seit 00:31 UTC unter Dev-PID 44748, Health `Healthy`, Ai-SHA256
`0693084476936452D2E8FE2416276DDD0BBB3913564B240F50DE89324AAEC84A`.
Die dritte vollständige Fünferserie wird in
`.runlogs/handoff-retry-compact-20261007/` erfasst. Kein Erfolgsnachweis vor deren
fachlicher Prüfung; beide vorherigen Kandidaten bleiben vollständig ausgewiesen.

Erster Fall der kompakten Serie: Binding `completed`, 114 Modellaufrufe.
Bericht korrekt: spezifische Shadow-Bindung, getrennte Roots und Inhalte, aktuelle
W3SVC3-Fehler gegenüber früheren W3SVC2-Erfolgen, richtige Statusfelder, Site-2-MIME
und bedingte Entfernung nur der exakten falschen Bindung. Historischer Hostheader
bleibt ehrlich begrenzt. Das behebt die Teilbewertung des zweiten Kandidaten, ist
aber gegenüber 67 Referenzaufrufen weiterhin kein nachgewiesener Effizienzgewinn.

ACL der kompakten Serie endete mit 96 Modellaufrufen und `partial`. Alle Reviews
waren abgeschlossen und die ACL-Empfehlung war korrekt bedingt. Das Abschlussmodell
behauptete jedoch eine ungeklärte Client-IP-Abweichung zwischen Verwaltungsadresse
192.168.240.101 und interner Adresse 10.0.0.100. Der letzte Serverreview hatte ausdrücklich
belegt, dass CLIENT1 die interne Adresse besitzt (ev-00007). Die anfängliche Hypothese
einer Kontextkürzung erklärt diesen Lauf NICHT: das tatsächliche Limit ist 250.000
Zeichen, die vier letzten Mitgliedertexte sind 2.123/1.724/2.503/2.912 Zeichen lang
und passen vollständig in die 5.041 Zeichen pro Mitglied. Die Adresse steht beim
Serverreview schon an Position 509. Der Fehler ist eine übersehene, bereits geklärte
Tatsache im letzten Modellschritt, der keine Rückfragemöglichkeit mehr hatte.

Vorbereitete Korrektur: Eine neu im Abschlussassessment benannte unerledigte
Anforderung einmal an den Supervisor zur Originalprüfung zurückgeben, sofern alle
bisherigen Team-/Registerpflichten erfüllt sind und ausreichend Budget bleibt.
Keine automatische Hochstufung zu completed; echte fehlende Informationen bleiben
partial/blocked, geänderte Befunde erfordern weiterhin aktuelle Reviews. Tests für
erfolgreiche Klärung ohne erneuten Originalzugriff, verbleibende Lücke und knappes
Budget sind vorbereitet, aber während der unveränderten dritten Live-Serie noch
nicht gebaut oder ausgeführt. Der Quellstand dieser Serie ist `after-live-third/`.

Filter der kompakten Serie liefert erstmals einen natürlichen Retry-Beleg:
Journal 137, Server-Modellaufruf 10 nach Timeout einmal wiederholt. Weitere
Werkzeugarbeit ging danach weiter; kein Werkzeug zwischen Fehlversuch und dessen
Modellwiederholung wurde erneut ausgeführt. Abschluss `completed`, 86 Modellaufrufe;
beide Filterursachen und gezielte Korrekturen stimmen. Eine zunächst blockierte
Endpunktfrage wurde durch die neue einmalige Rückgabe mit späteren Originalen
aufgelöst (Journal 636/644/649/671), ohne die Hostheader-Grenze zu verschweigen.

Konfiguration brach nach sechs Modellaufrufen ab: Server-Aufruf 3 erhielt einen
vom Remotehost geschlossenen Transport. Die bisherige pauschale Ausnahme für
`Unreachable` verhinderte den Retry. MIME endete `completed` mit 89 Aufrufen und
beiden korrekten MIME-Ursachen, korrekter Vererbung/Site-Ebene und bedingter gezielter
Reparatur. Der Bericht sagt allerdings nach Aufzählung von zwei URLs „übrige fünf“
statt vier; kein Ursachenverlust, aber eine redaktionelle Ungenauigkeit.
Die dritte Serie ist vollständig beendet, alle sechs Verträge je Fall wiederhergestellt,
ACL-SDDL bestätigt, eigene Testassets entfernt, CLIENT1 wieder aus und Workflow
unverändert deaktiviert. `verification.json` besteht strukturell; ACL partial und
Konfiguration Failed bleiben ausdrücklich erhalten, also keine Qualitätsfreigabe.

## Vierter Kandidat: konkrete Rückfrage- und Transportkorrekturen

Nach Ende der dritten Serie reproduzierten die neuen Tests sieben erwartete Fehler.
Nach Implementierung bestehen alle 18 gezielten Fälle sowie 172 ausgewählte AI-Tests.
Die einmalige Rückfrage aus der finalen Coverage verwendet die bestehende Supervisor-
Session und normale Register-/Review-Gates. Ein vollständiger Zwischenbericht bleibt
erhalten, die Abschlussreserve wird erneuert, echte Lücken bleiben partial/blocked.
Kein wiederholtes Erzwingen eines positiven Assessments, keine Werkzeugwiederholung.

Der Retry erkennt zusätzlich Socket ConnectionReset/ConnectionAborted/NetworkReset
und HttpRequestError.ResponseEnded anhand der Exception-Kette. TLS/Authentifizierung,
DNS und ConnectionRefused werden nicht wiederholt; selbst ein innerer Reset macht
einen TLS-Fehler nicht vorübergehend. Der tatsächliche Transportfehler bleibt im
Vergleich sichtbar. Neue Serie und Testlogs: `.runlogs/handoff-retry-reconcile-20261007/`.
Der Live-Qualitätsvergleich dieses Kandidaten steht noch aus.

Vierter Binding-Lauf: `completed`, 146 Modellaufrufe. Ein natürlicher Server-Timeout
(Journal 263, Aufruf 24) und ein späterer Verbindungsabbruch beim Clientreview
(Journal 803, Aufruf 107, Unreachable) wurden jeweils einmal erfolgreich wiederholt;
kein Werkzeug-Replay. Damit ist auch die enge neue Transportklassifikation im realen
Lauf belegt. Bericht benennt Site 3,
konkrete Hostbindung, Shadow-Root, Legacy-Inhalt, fehlende Sollobjekte und richtige
W3C-Felder. Er wahrt die Hostheader-/Win32-64-Grenze und trennt die MIME-Konfiguration
der Basis-Site. Die vorgeschlagene Verlegung der konkreten Bindung zu Site 2 ist
gezielt und bedingt, benötigt aber gegenüber bloßer Entfernung der konkurrierenden
Bindung einen zusätzlichen Konfigurationsschritt. Keine zusätzliche MIME-Änderung
oder Dateikopie empfohlen. Kein Effizienzgewinn gegenüber 67 Referenzaufrufen;
Registerformatkorrekturen, Kontextverdichtung und erneute Reviews sind sichtbar.

Vierter ACL-Lauf: `completed`, 74 statt 72 Referenzaufrufen. Beide expliziten
IUSR-Deny-Read-ACEs, effektive anonyme Identität, exakte Dateiinhalte und 401.3/5
korrekt. Der Clientreview beanstandete den allgemeinen Vorschlag, Lesezugriff
herzustellen; nach gezielter Rückfrage beschränkt der Endbericht die Korrektur
auf die zwei Deny-ACEs und erklärt, warum ein zusätzliches Allow allein nicht reicht.
Keine offene Zielidentität, keine unnötige Teilbewertung. Aktuelle und frühere
Abrufe bleiben getrennt. Zitiertes Manifest-200/Win32-64 in ev-00062 Zeile 392
gegen Originaljournal geprüft. Keine pauschale Rechte- oder Authentifizierungsänderung.

Vierter Filter-Lauf: `completed`, 177 Modellaufrufe statt 68 in der gemischten Referenz.
Beide Ursachen vollständig: `.json` effektiv verboten (404.7), `packages` verborgen
(404.8), exakte Dateien/Inhalte vorhanden, MIME bereits korrekt. Passender aktueller
Logblock wurde nach gezielter Rückfrage gefunden; ältere 401.3 und frühere 200 bleiben
zeitlich getrennt. Der Serverreview entdeckte anschließend einen veralteten Text im
gemeinsamen Register, öffnete die Frage und verlangte dessen Korrektur. Weitere
Nachprüfungen und Formatkorrekturen steigerten den Aufwand erheblich. Endbericht
korrekt mit eng begrenzter Freigabe beider Filterregeln und ehrlicher Log-Scope-Grenze.
Kein Effizienzgewinn; die Ursache des Mehrverbrauchs darf bei gewachsener Loghistorie
und einem Lauf pro Variante nicht ausschließlich der Codeänderung zugeschrieben werden.

Vierter Konfigurationslauf: `completed`, 109 Aufrufe. Das erste Abschlussassessment
markierte die Chronologie der vorherigen 200 gegenüber späteren 500 als offene
Anforderung. Die neue einmalige Rückgabe wurde real ausgelöst (Journal 702), ging
gezielt an den Server (710/711) und lieferte die exakten Schreibzeiten beider Dateien
(ev-00085/86, 02:05:30.6260764Z zwischen Erfolgen um 02:05:27 und Fehlern um 02:05:31).
Normale Reviews und zweites Assessment folgten; Endergebnis vollständig, keine
behauptete Ermittlung des Änderungsakteurs. Der vorläufige Bericht blieb erhalten.

Trotzdem keine volle Qualitätsfreigabe: `missingDeliveryModule` wird als ungültiger
Eintrag benannt, aber die fehlende Abschnittsregistrierung wird wie in den frühen
Kandidaten nicht separat belegt. Die Referenz war hier präziser. Außerdem enthält
der Endbericht eine kleine Zählungenauigkeit („vier weitere“ 200 neben Health;
tatsächlich vier inklusive Health). Die breiten zusätzlichen Übergabe-/Assessment-
Promptformulierungen werden daher nach Ende dieser Serie zurückgenommen. Der
nächste Kandidat übernimmt exakt die ursprünglichen allgemeinen Instruktionen und
behält nur die gezielten technischen Rückfragewege sowie den isolierten Retry.
Die vier bisherigen Varianten werden nicht als Erfolg umgedeutet oder gelöscht.

Vierter MIME-Lauf: `partial`, 162 Aufrufe. Beide MIME-Ursachen korrekt, aber nach
zusätzlicher Untersuchung von Win32-Status 64 und erneuten effektiven Abfragen fehlen
abschließende aktuelle Reviewerfreigaben. Alle Registerfragen resolved reicht dafür
nicht; der Bericht weist die fehlende Freigabe ehrlich aus. Keine Qualitätsfreigabe.
Alle fünf Fälle und ursprünglichen ACLs wurden wiederhergestellt, Testassets entfernt,
VM-Zustände restauriert und der unveränderte Workflow deaktiviert. Strukturaudit grün.

## Fünfter Kandidat: ursprüngliche Anleitung, gezielte technische Übergaben

Alle allgemeinen Runtime-Instruktionen stimmen wieder exakt mit `before/` überein;
die gesamte Datei `AgentConclusion.cs` ist bytegleich zum Ausgangsstand. Die vier
bisherigen Quellvarianten sind archiviert, auch `after-live-fourth/`.
Punkt 4 konzentriert sich auf strukturierte Hostübergaben an konkret blockierten
Checks und einmalig neu aufgeworfenen Abschlussfragen. Keine neue allgemeine
Berichtsschablone, keine Änderung von Werkzeugen, Modellen oder Berechtigungen.
Punkt 6 bleibt unverändert. Artefakte: `.runlogs/handoff-retry-baseline-guidance-20261007/`.
Labsteuerung startet die zeitlich begrenzte CLIENT1-Evaluierungs-VM zwischen Fällen
bereits ab 25 Minuten Laufzeit neu, damit ein weiterer 30-Minuten-Lauf vor ihrem
normalen Abschaltfenster enden kann. Kein Neustart innerhalb eines Agentenlaufs.
Erneute vollständige Validierung steht aus; keine Freigabe allein durch Rücknahme.

ACL im zweiten Kandidaten: `completed`, 84 Modellaufrufe statt 72 in der Referenz.
Beide Datei-Deny-ACEs und IIS 401.3/5 korrekt, keine stehengebliebene Clientfrage.
Bericht grenzt Logkorrelation und ungeprüften HTTP-Body ab. Der Reparaturtext nennt
die gezielte Deny-Korrektur, aber zusätzlich allgemein den erforderlichen Lesezugriff;
das ist weniger präzise als nur den beobachteten Deny zu entfernen. Punkt 5 bleibt
unverändert ein separates offenes Thema. Kein Leistungsgewinn für diesen Einzelfall.
