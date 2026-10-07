# Agenteneffizienz: Vergleich und Qualitätsgrenze

Stand: 07.10.2026. Die erste Serie und die beiden gezielten Wiederholungen sind
abgeschlossen und aufgeräumt. Die entdeckte Protokollregression ist korrigiert,
durch Regressionstests abgesichert und im korrigierten Dev-Build nachgeprüft.
Die untersuchten Abschlussberichte zeigen keinen Verlust an Diagnosequalität.
Die schwankende Einstufung zusätzlicher Prüfungen als Pflicht bleibt ein offener
Robustheitspunkt; eine allgemeine Garantie gleicher Ergebnisqualität ist aus
diesen Einzelmessungen nicht ableitbar. Der Rücksetzstand bleibt erhalten.

## Verbindliche Vorgabe

Ergebnisse dürfen zugunsten geringerer Kosten oder Laufzeit nicht schlechter werden.
Relevante Alternativen, Gegenbelege und Mehrfachfehler bleiben Teil der Untersuchung.
Eine erste plausible Erklärung reicht nicht. Keine Reduzierung von Werkzeugen,
Modellen oder Budgets. Bei belegter Regression nachbessern oder zurücksetzen.

## Rücksetzstand

Unter `.runlogs/efficiency-checkpoint-20261006/` sind zehn betroffene Dateien vor
der Optimierung, der erste Kandidat und der korrigierte Kandidat getrennt gesichert.
Der Vorher-Stand wurde aus den gezielten Sitzungsänderungen rekonstruiert, isoliert
gebaut und mit allen 103 damaligen ausgewählten AI-Tests geprüft. Er enthält die
vorherigen Parallelisierungs- und Abschlussbericht-Fixes; der schon vorher offene
Merge und alle anderen Arbeitsbaumänderungen bleiben unberührt.

`manifest.json` enthält SHA-256-Prüfsummen. `restore.ps1` prüft ohne Parameter nur;
mit `-Apply` stellt es ausschließlich diese zehn Dateien wieder her, nachdem alle
Hashes geprüft wurden. Zwischenzeitliche Änderungen führen zum Abbruch vor dem
ersten Schreibvorgang. `optimization-only.patch` lässt sich rückwärts auf den
gesicherten Kandidaten anwenden; `git apply --reverse --check` war erfolgreich.
Keine Commits, Pushes oder Git-Resets wurden ausgeführt.

## Erste Vergleichsserie

Unveränderter Workflow `[Agent Check] Hardcore Five 20261006`, Version 2,
ID `d98a9963-18ff-4675-8163-aee4fc4db09d`. Alle fünf Mitglieder `gpt-6-luna`;
Limits 200 Modellaufrufe, 500 Werkzeugaufrufe, 30 Delegationen, 1800 Sekunden.
Die Agenten erhielten Symptome und Sollvertrag, keine injizierten Ursachen.

| Fall | Modellaufrufe vorher → Kandidat | Laufzeit vorher → Kandidat | Inhaltliche Bewertung |
|---|---:|---:|---|
| Falsche Host-Bindung | 131 → 106 | 11:04 → 10:56 | Ursache und minimale Binding-Korrektur richtig; `partial` statt `completed` wegen zusätzlicher direkter Client-Nachprüfung. Abweichung in der Aufgabendeckung bleibt ausdrücklich offen. |
| Zwei Datei-ACL-Sperren | 89 → 72 | 8:01 → 6:46 | Beide Deny-Einträge erkannt. Reviewer korrigieren zu breite/ungenügende Allow-Empfehlung; vollständiger Abschluss. |
| Zwei Request-Filter | 65 → 68 | 6:33 → 6:32 | 404.7 und 404.8 richtig unterschieden; MIME-Gegenprüfung; vollständiger Abschluss. |
| Zwei Konfigurationsfehler | 73 → 81 | 7:29 → 8:41 | Unbekannter Abschnitt und doppelter MIME-Schlüssel belegt; gezielte Reparaturen; vollständiger Abschluss. |
| Zwei MIME-Abweichungen | 167 → 119 | 13:32 → 9:16 | Beide Ursachen erkannt und Bericht korrekt; neue Protokollregression verursachte dennoch unnötige Review-Schleifen. |

Summe der fünf Agentenlaufzeiten: 46:39 → 42:11 (rund 9,6 % weniger).
Modellaufrufe: 525 → 446 (rund 15 % weniger). Werkzeugaufrufe: 1088 → 1010.
Delegationen: 39 → 41; Review-Aufträge: 25 → 24. Einsparungen sind uneinheitlich
und kompensieren keine Qualitätsabweichung.

Die Filterreferenz ist der erfolgreiche Wiederholungslauf; der vorangegangene
Modell-Timeout bleibt in der ursprünglichen Historie erhalten. Es ist jeweils nur
eine Messung je Fehlerbild. Die erhaltene IIS-Loghistorie wächst zwischen Versuchen;
Modelllaufzeiten variieren. Dies ist keine allgemeine Leistungsgarantie.

Alle fünf neuen Läufe endeten technisch erfolgreich, vier mit `completed`, einer
mit `partial`. Alle injizierten Ursachen wurden in den Endberichten erkannt.
Das ersetzt keine Freigabe der offenen Klassifikationsabweichung. Nach jedem Fall
wurden alle sechs HTTP-Verträge mit Status, Body und Content-Type unabhängig geprüft.
Die ursprünglichen zwei ACLs stimmen exakt mit ihren SDDL-Sicherungen überein.
Eigene Site, Pool, Firewallregel, Inhalte und Recovery-Tasks wurden entfernt;
CLIENT1 ist wieder aus, CM1/GW1/DC1 laufen. Workflow deaktiviert, Historie erhalten.

## Gefundene Regression und Korrektur

Im MIME-Lauf (`f4da84da-bce5-4804-b8ad-90974535b00a`, Ereignisse 607/609)
verwarfen die neuen Abhängigkeitsprüfungen zwei Reviewerantworten. Der allgemeine
Fehlerpfad registrierte sie anschließend als Fehler mit neuem Evidenzbedarf. Reine
Protokollfehler lösten damit wiederholte Systemabfragen aus, obwohl eine
Formatkorrektur genügt hätte.

Abhängigkeiten werden jetzt bereits beim Parsen gegen den zugewiesenen Snapshot
validiert und einmal ohne Werkzeuge korrigiert. Ein weiterhin ungültiges Format
blockiert den Abschluss, erzeugt aber keinen fachlichen Evidenz-Einwand. Vorherige
echte Evidenz-Einwände bleiben wirksam. Die zwei neuen Laufzeittests scheiterten
vor der Korrektur und bestehen danach; ein weiterer Test schützt bestehende Einwände.
Nach Korrektur: 116 gezielte AI-Tests und 12 Engine-Tests bestanden; API-Build erfolgreich.

Die fünf Liveergebnisse gehören zum **ersten** Kandidaten. Die gezielten
Wiederholungen des korrigierten Builds liegen getrennt in
`.runlogs/efficiency-mime-corrected-20261006/`; sie überschreiben keine Referenz.

## Wiederholung nach der Korrektur

Nach Benutzer-Neustart wurden Dev-Prozess, Health-Endpunkt und die Ai-DLL geprüft.
SHA-256: `4BF10578A6BA12C014C64FA7231C3C1259E15CCD0BB95046580DF2EADB72B1BC`.
Workflow, Modelle, Werkzeugrechte und Budgets blieben unverändert.

| Fall | Ursprüngliche Referenz | Erster Kandidat | Korrigierter Kandidat |
|---|---|---|---|
| MIME | 13:32 / 167 Modellaufrufe | 9:16 / 119 | 4:44 / 50 |
| Binding | 11:04 / 131 Modellaufrufe | 10:56 / 106 | 6:45 / 67 |

Beide Wiederholungen: `Succeeded`, `completed`, vollständiger Abschlussbericht,
parallele Spezialisten und unabhängige Reviews. MIME benötigte 146 Werkzeugaufrufe
und 5 Delegationen; Binding 159 und 6. Keine Mitglieds- oder Formatfehler in diesen
beiden Journalen. Die gezielten Unit-Tests prüfen weiterhin den Fehlerpfad, der
im neuen Liveversuch nicht erneut ausgelöst wurde. Keine Zusammenrechnung dieser
zwei Wiederholungen mit drei älteren Läufen als vermeintliche neue Fünfer-Serie.

**MIME:** Beide unabhängigen Ursachen bleiben korrekt: entfernte `.json`-Map und
lokal falsche `.npmanifest`-Map. Effektive Konfiguration, Originaldateien und
404.3/50 sind belegt. Der Bericht trennt HTTP-Status, Substatus und Win32-Status,
erklärt den Health-Gegenbefund und empfiehlt ausschließlich die zwei gezielten
Konfigurationskorrekturen samt anschließender Prüfung aller sechs Verträge.

**Binding:** Falsche Site, alter Release und vier fehlende Objekte sind erkannt.
Der Client-Reviewer beanstandete den zunächst falschen Logzeitpunkt und eine
unbelegte Deutung von Win32-Status 64. Diese sachlichen Einwände führten zur
Berichtskorrektur und erneuten Freigabe; sie wurden nicht aus Effizienzgründen
übergangen. Der Endbericht verwendet den passenden W3SVC3-Block vom 21:54:23 UTC,
belässt Status 64 ohne unbelegte Ursachenbehauptung und nennt eine gezielte,
bedingte Binding-Korrektur sowie eine ausdrücklich bedingte alternative Lösung.

**Einordnung der vorherigen `partial`-Abweichung:** Der ursprüngliche Auftrag
verlangt Diagnose und Reparaturvorschlag mit Erfolgskontrolle, keine ausgeführte
Reparatur oder zusätzliche frische TCP-/HTTP-Probe. Im ersten Kandidaten erhob
das selbst angelegte Register eine solche Probe zur offenen Anforderung, obwohl
die Diagnose bereits gestützt war. Inhaltlich fehlte dort kein belegter Fehler;
die Einstufung war konservativer und die selbst gesetzte Anforderung unnötig.
Die Wiederholung erklärt korrekt, weshalb der passende Serverlog die beobachteten
Anfragen belegt und die momentane Socket-Abfrage keine weitere Pflicht begründet.
Das beweist weder eine dauerhafte Behebung noch, dass die Effizienzänderungen die
frühere Abweichung verursacht haben. Die bestehende Sperre bei echten blockierten
Checks wurde nicht gelockert. Aufgabenabdeckung versus optionale Prüfmethode bleibt
als gezieltes Thema für strukturierte Übergaben und spätere Regressionstests offen.

Alle sechs nativen HTTP-Verträge wurden nach jedem Wiederholungsfall wiederhergestellt
und unabhängig geprüft. Eigene Site, Pool, Inhalte, Firewallregel und Recovery-Tasks
sind entfernt; ursprüngliche VM-Zustände wiederhergestellt. Der Testworkflow ist
deaktiviert, seine Definition und Historie bleiben erhalten. `verification.json`
bestätigt diese strukturellen Prüfungen. Die Rücksetz-Prüfsummenprüfung bestand erneut.

## Alle sechs Optimierungsvorschläge bleiben erhalten

Fortsetzung ab 07.10.: Der Benutzer beauftragte ausdrücklich Punkte 4 und 6.
Umsetzung und Vergleich stehen in [Strukturierte Übergaben und Modellwiederholung](agent-handoffs-retry-2026-10-07.md).
Die folgende Liste dokumentiert den Abschlussstand der ersten Optimierungsrunde;
Punkte 3 und 5 bleiben auch in der Fortsetzung offen.

1. Abschlusskriterien ohne Abschneiden relevanter Alternativen: implementiert,
   fünf Fehlerbilder und zwei gezielte Wiederholungen ausgewertet.
2. Abhängige Reviews gezielt erneuern: implementiert, gefundene Protokollregression
   korrigiert; gezielte Livewiederholungen bestanden.
3. Budget für Diagnose, Rückfragen, Reviews und Bericht reservieren: offen.
4. Kurze strukturierte Übergaben mit Originalbelegen, Unsicherheit, nächster Frage
   und tatsächlichen Fähigkeitsgrenzen; zulässige Abfrageformen klarer vermitteln: offen.
5. Minimale Reparatur und notwendige Rechte explizit prüfen: offen; ACL-Fall bleibt
   Referenz gegen unnötige zusätzliche Allow-Rechte.
6. Begrenzte Wiederholung bestätigter transienter Modellfehler, ohne Werkzeuge oder
   Seiteneffekte erneut auszuführen; bei dauerhaftem Fehler Befunde erhalten: offen.

Bei weiteren Optimierungen die dokumentierte `completed`/`partial`-Abweichung
gegen die tatsächlich angeforderten Ergebnisse absichern. Keine offenen fachlichen Fragen
unterdrücken, aber zusätzliche freiwillige Prüfungen auch nicht stillschweigend
als neue Pflichtanforderungen bewerten.

Detailbelege: `.runlogs/efficiency-five-20261006/` mit vollständigen Journalen,
Berichten, `comparison.json`, `verification.json`, Vorher-/Nachher-Testlogs und
Wiederherstellungsnachweisen. Die ursprüngliche Referenz bleibt unter
`.runlogs/hardcore-five-20261006/` sowie `.runlogs/hardcore-five-filter-repeat-20261006/`.
