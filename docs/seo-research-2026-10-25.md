# SEO-Erhebung und gMSA-Erweiterung vom 25.10.2026

## Entscheidung

Zwei neue, im Windows-Labor geprüfte Anleitungen ergänzen den vorhandenen gMSA-Grundlagenartikel: Aufgabenplanung und Diagnose von `Test-ADServiceAccount`. Beide liegen auf Deutsch und Englisch als Entwurf vor. Eine weitere allgemeine Einführung in gMSA oder PowerShell 7 würde die vorhandenen Artikel überlappen. Eigene PowerShell-7-Vertiefungen bleiben von konkreter Nachfrage und einem passenden Testaufbau abhängig.

gMSA ist ein starkes Nachfrage-Thema, aber nicht der alleinige Spitzenreiter: Die Erhebung vom 18.10. nannte für die USA sowohl gMSA als auch PowerShell 7 mit je 6.600 geschätzten Suchen pro Monat; in Deutschland PowerShell 7 mit 2.400 und gMSA mit 720. Die neue Detailerhebung bestätigt die gMSA-Schätzung. Die bisherige tatsächliche Sichtbarkeit der Website ist für den WinRM-Grundlagenartikel höher als für gMSA.

## Datenstand und Methode

- OpenSEO-Projekt: `2e3743a3-2a45-4077-b238-6f207f6971fa`, verbundene Search-Console-Property `sc-domain:nodepilot.run`.
- Abruf am 25.10.2026. Die zunächst abgelaufene Google-Verbindung wurde vom Nutzer erneuert; die anschließenden Leistungs- und Indexabfragen waren erfolgreich.
- Search Console: Web-Suche, endgültige Daten, 25.09.–22.10.2026, weltweit und alle Geräte. Datumsgrenzen gelten nach Pacific Time. Vergleich: 28.08.–24.09.2026.
- Abfragen nach Tag, Seite sowie Suchanfrage/Seite/Land, jeweils maximal 1.000 Zeilen. Alle Antworten melden `hasMore: false`; keine weitere Seite erforderlich.
- DataForSEO über OpenSEO: US/en (`2840`) und DE/de (`2276`), separat abgefragt. Volumen und Schwierigkeit sind Anbieter-Schätzungen; fehlende Werte sind keine Nullwerte.
- Acht SERP-Abfragen: sechs Detailfragen mit je zehn SERP-Elementen und gMSA in beiden Märkten mit je zwanzig Elementen. Elemente enthalten auch Sonderformate und sind nicht gleichbedeutend mit organischen Treffern.
- Technische HTTP-Prüfung aller 16 veröffentlichten Blog-URLs mit englischem Accept-Language; zusätzlich bestehender Google-Indexstatus dieser URLs über `inspect_urls`. Die URL-Prüfung löst weder einen Live-Crawl noch einen Indexierungsantrag aus.

Rohantworten liegen lokal unter `.tmp/seo-2026-10-25/` im Arbeitsbaum. Dieser Ordner wird nicht versioniert. Die folgenden Tabellen sichern die entscheidenden Ergebnisse dauerhaft.

## Search-Console-Ausgangslage

Die Abfrage nach Datum ergibt **11 Klicks, 360 Impressionen, 3,06 % CTR**. Der nach Impressionen gewichtete Positionsmittelwert der bereits gerundeten Tageswerte beträgt ungefähr **15,6**. Seitenimpressionen dürfen nicht zur Property-Gesamtsumme addiert werden: Mehrere URLs derselben Website können in einer Suche erscheinen. [Google erläutert die unterschiedliche Aggregation](https://support.google.com/webmasters/answer/7576553?hl=en).

| Blogseite | Sprache | Klicks | Impressionen | Durchschnittliche Position |
| --- | --- | ---: | ---: | ---: |
| gmsa-service-accounts | DE | 0 | 4 | 7,5 |
| gmsa-service-accounts | EN | 0 | 40 | 49,0 |
| winrm | DE | 0 | 26 | 16,6 |
| winrm | EN | 0 | 51 | 11,7 |
| winrm-https | DE | 0 | 4 | 5,5 |
| winrm-https | EN | 0 | 12 | 10,8 |
| orchestrator-2016-end-of-support | DE | 0 | 12 | 16,5 |
| scorch-import | DE | 0 | 4 | 4,8 |
| why-nodepilot | DE | 0 | 4 | 6,3 |
| why-nodepilot | EN | 0 | 4 | 8,8 |

Für weitere Blog-URLs fehlen Zeilen in dieser Antwort; das ist kein Nachweis fehlender Indexierung. Der PowerShell-Vergleich wurde erst am 25.10. veröffentlicht und liegt außerhalb des Messfensters. Die gMSA- und WinRM-Artikel sind erst seit 18.10. online: Ihr beobachtbarer Teil dieses Fensters ist entsprechend kurz.

Die 105 gelieferten Suchanfrage/Seite/Land-Zeilen enthalten 4 Klicks auf Markenanfragen und 0 Klicks auf die übrigen sichtbaren Anfragen. Markenfilter: `nodepilot`, `node pilot` oder `sev7enup`, unabhängig von Groß-/Kleinschreibung. Die übrigen 7 Property-Klicks lassen sich in diesem Export keiner Suchanfrage zuordnen. Deshalb ist „alle 11 Klicks sind Marke“ aus dieser Erhebung **nicht** belegt. Sichtbare Nichtmarken-Zeilen summieren sich auf 78 Seitenimpressionen, Marken-Zeilen auf 85; auch diese Summe ist keine Property-Gesamtsumme.

Konkrete Signale:

- `gmsa`: DE-Artikel 1 Impression aus Österreich, Position 11; EN-Artikel 1 Impression aus den USA, Position 69.
- `test gmsa account`: EN-Artikel insgesamt 4 Impressionen aus Australien, Belgien und Deutschland, Positionen 4, 7 und 8. Kleine Stichprobe, aber passende Diagnoseabsicht.
- WinRM erscheint unter `test winrm`, `test winrm connection` und `test-wsman`. Damit bleibt die bestehende WinRM-Gruppe in der nächsten Auswertung relevant.
- Vergleichsfenster 28.08.–24.09.: keine Seitenzeilen geliefert. Die Website ging erst am 20.09. live. Keine Prozentsteigerung aus einer leeren Ausgangsbasis berechnen.

## Nachfrage und momentane Suchergebnisse

| Suchbegriff | Markt | Geschätzte Suchen/Monat | Anbieter-KD | Verwendung |
| --- | --- | ---: | ---: | --- |
| gmsa | US/en | 6.600 | 0 | bestehende Grundlagenseite |
| gmsa | DE/de | 720 | 0 | bestehende Grundlagenseite |
| gmsa scheduled task | US/en | 10 | 0 | neue Aufgabenanleitung |
| powershell 7 scheduled task | US/en | 10 | unbekannt | später prüfen; bisheriger Laufzeitvergleich bleibt Einstieg |
| gmsa troubleshooting | US/en | unbekannt | unbekannt | ergänzende Diagnoseabsicht |
| test-adserviceaccount false | US/en | unbekannt | unbekannt | neue Diagnoseanleitung |

Weitere abgefragte Formulierungen wurden teilweise gar nicht zurückgegeben, darunter deutsche Detailfragen und `powershell 7 module compatibility`. Daraus lässt sich keine Nachfrage von null ableiten. KD 0 ist keine Zusage leichter Rankings. Der Anbieter klassifiziert `gmsa scheduled task` als navigational; die sichtbaren Anleitungen und Fragen sprechen hier für einen praktischen Informationsbedarf. Die redaktionelle Einordnung folgt den Ergebnissen.

**gMSA in Deutschland:** NodePilots deutsche Grundlagenseite erschien am 25.10. auf **organischem Rang 17**, absolutem SERP-Rang 20. In der US-Abfrage war NodePilot unter den zwanzig gelieferten SERP-Elementen nicht enthalten. Das ist eine einzelne Marktaufnahme, keine Search-Console-Durchschnittsposition und kein Beleg eines stabilen Ranggewinns. Zeitpunkt, Land, Gerät und Suchkontext beeinflussen die Ergebnisse.

Ausgewählte Ergebnisse der Detailabfragen:

| Suchabsicht / Markt | Beobachtete Ergebnisse | Konsequenz für den neuen Artikel |
| --- | --- | --- |
| gmsa scheduled task / US | [Stack Overflow: Aufgabe erstellen](https://stackoverflow.com/questions/67888223/create-a-scheduled-task-with-powershell), [SQLCan: Task Scheduler](https://sqlcan.com/2022/12/14/windows-task-scheduler-for-sqlopsdb/) | Nicht bei Registrierung stoppen; Konto, Laufzeit, Dateiergebnis und Fehlerfall nachvollziehbar prüfen. |
| test-adserviceaccount false / US | [NIM: Managed Service Account konfigurieren](https://docs.nimsuite.com/en/integrations/microsoft-active-directory/configure-managed-service-account-for-active-directory), [Cloudtech: gMSA action account issues](https://cloudtech.nu/2023/06/06/gmsa-action-account-issues-mdi/) | Lokale Kontobereitschaft von Produkt- und Ressourcenberechtigungen trennen. |
| gmsa geplante aufgabe / DE | [Specops: gMSA](https://specopssoft.com/support/de/specops-authentication/reference-material/gmsa/), [Der Windows Papst: Remote-Einrichtung](https://www.der-windows-papst.de/2022/03/10/gmsa-account-per-remote-erstellen-und-einrichten/) | Allgemeine Aufgabenplanung mit eigenem kleinem SMB-Test statt produktspezifischer Installation. |
| test-adserviceaccount false / DE | [Windows OS Hub: gMSA](https://woshub.com/group-managed-service-accounts-in-windows-server-2012/), [Beatcracker: ohne AD-Modul](https://beatcracker.wordpress.com/2017/02/03/using-group-managed-service-accounts-without-active-directory-module/) | Fehlerklassen geordnet eingrenzen; Hostfreigabe tatsächlich reproduzieren, keine pauschalen KDS-/Kerberos-Reparaturen. |

Das ist eine Auswahl, kein vollständiger Inhaltsvergleich sämtlicher Treffer. Einzelne Seiten, etwa Quest und Spiceworks, waren nicht vollständig abrufbar; dort wurden nur SERP-Angaben gesehen. Die Aussagen der neuen Artikel stützen sich auf Microsoft-Dokumentation und eigene Tests, nicht auf ungeprüfte Treffertexte. Eigene praktische Nachweise folgen auch Googles Empfehlung zu [hilfreichen Inhalten mit eigener Erfahrung](https://developers.google.com/search/docs/fundamentals/creating-helpful-content).

## Indexierung und technische Befunde

Alle 16 veröffentlichten Sprach-URLs antworten mit HTTP 200, unveränderter Ziel-URL, einer H1, selbstreferenziertem Canonical sowie de/en/x-default-hreflang. Kein Robots-Meta-Tag wurde im HTML gefunden. Diese Prüfung allein sagt nichts über Googles Index aus und umfasst keinen vollständigen technischen Crawl.

Google meldet **10 von 16 URLs indexiert**:

- DE und EN: why-nodepilot, gmsa-service-accounts, winrm, winrm-https.
- Nur DE: scorch-import, orchestrator-2016-end-of-support.

| Noch nicht indexierte URL | Google-Status am 25.10. |
| --- | --- |
| https://www.nodepilot.run/en/blog/scorch-import/ | Discovered – currently not indexed |
| https://www.nodepilot.run/blog/first-workflow/ | URL is unknown to Google |
| https://www.nodepilot.run/en/blog/first-workflow/ | URL is unknown to Google |
| https://www.nodepilot.run/blog/powershell-51-and-7/ | URL is unknown to Google |
| https://www.nodepilot.run/en/blog/powershell-51-and-7/ | URL is unknown to Google |
| https://www.nodepilot.run/en/blog/orchestrator-2016-end-of-support/ | Discovered – currently not indexed |

Beim deutschen Orchestrator-Supportartikel enthält Googles letzter Crawl vom **12.10.** noch die englische URL als vom Nutzer deklarierten Canonical. Googles gewählter Canonical ist bereits die deutsche URL. Die Live-Prüfung am 25.10. liefert korrekt die deutsche Selbstreferenz. Das alte Crawldatum liegt vor der Korrektur vom 18.10.; daraus folgt kein neuer Canonical-Fehler im aktuellen Code.

Nächster Indexierungsschritt: Die sechs fehlenden URLs und den alten deutschen Orchestrator-Crawl in Search Console live prüfen und gegebenenfalls „Indexierung beantragen“. Dieser Antrag wurde hier **nicht** ausgeführt; OpenSEOs verwendete Schnittstelle liest nur den bestehenden Status. Nach sieben Tagen erneut prüfen, statt fortlaufend dieselben URLs einzureichen.

## Umgesetzte Inhalte und interne Links

| Suchabsicht | Zielpfad, jeweils auch unter /en/ | Stand |
| --- | --- | --- |
| gMSA verstehen und einrichten | `/blog/gmsa-service-accounts/` | bestehend, veröffentlicht |
| PowerShell-Aufgabe mit gMSA ausführen | `/blog/gmsa-scheduled-task/` | neuer Entwurf, Laborprüfung bestanden |
| Test-ADServiceAccount False eingrenzen | `/blog/gmsa-troubleshooting/` | neuer Entwurf, Laborprüfung bestanden |
| Laufzeit 5.1 versus 7 wählen und prüfen | `/blog/powershell-51-and-7/` | bestehend, seit 25.10. veröffentlicht |

Die neuen Anleitungen verweisen auf die Grundlagen und auf passende NodePilot-Einstiegsseiten; die Diagnose verlinkt zusätzlich die zuerst erscheinende Aufgabenanleitung. Gegenseitige Empfehlungen im Katalog werden nach Veröffentlichungsstatus gefiltert. Im Katalog verweist der gMSA-Grundlagenartikel auf beide Vertiefungen, der PowerShell-Vergleich auf die Aufgabenanleitung. Entwürfe sind in der Produktionsausgabe weiterhin ausgeblendet, tragen in der Vorschau `noindex, follow` und stehen nicht in der Sitemap. Kontextlinks aus bereits veröffentlichtem Fließtext werden erst beim jeweiligen Release ergänzt, einschließlich des Diagnose-Links aus der Aufgabenanleitung beim zweiten Release.

Die Inhalte dokumentieren Kontobereitschaft, Batch-Logon, SMB-/NTFS-Rechte, eingeschränkte Ausführung, Ergebnisprüfung und einen absichtlich verweigerten Testzugriff. Keine zweite allgemeine gMSA-Einführung. Technische Nachweise und Grenzen stehen im [Laborprotokoll](testing/gmsa-content-validation.md).

## Veröffentlichung und Messung

Vorgesehene Reihenfolge: Aufgabenanleitung am **01.11.2026**, Diagnose am **08.11.2026**, jeweils DE und EN zusammen. Das sind vorgeschlagene Termine; keine automatische Veröffentlichung wurde eingerichtet. Entwürfe bleiben ohne Veröffentlichungsdatum. Erst beim ausdrücklich beauftragten Release Status und tatsächliches Datum setzen, Kontextlinks ergänzen, Produktionsbuild prüfen und deployen.

Wöchentlich die Indexierung und neue Suchanfragen beobachten. Aussagekräftige erste Vergleiche nach vier und acht Wochen je Artikel:

| Bei Veröffentlichung wie geplant | Vier Wochen vollständig | Acht Wochen vollständig |
| --- | --- | --- |
| Aufgabenanleitung 01.11. | 01.–28.11.; Abruf ab 02.12. | 29.11.–26.12.; Abruf ab 30.12. |
| Diagnose 08.11. | 08.11.–05.12.; Abruf ab 09.12. | 06.12.–02.01.; Abruf ab 06.01.2027 |

Das zweite Fenster ist der Vergleichszeitraum der Wochen fünf bis acht. Bei anderem Release verschieben sich alle Fenster. Jeweils endgültige Search-Console-Daten verwenden, nach DE-/EN-URL und Land trennen; Sprache ist keine Search-Console-Dimension.

Erfassen: Indexstatus, nichtmarkenbezogene Klicks in sichtbaren Suchanfragen, Seitenimpressionen, CTR, Positionsverteilung relevanter Suchanfragen und die tatsächlich rankende Ziel-URL. Query-Auslassungen offenhalten. Bei Überschneidungen zwischen Grundlagen und Vertiefungen erst Suchabsicht und URL prüfen, dann Text oder Links nachschärfen. Bei fehlender Indexierung hat die technische Prüfung Vorrang. Bei Impressionen ohne Klicks Titel und Antwortpassung prüfen, bevor neue Artikel ergänzt werden.

Es gibt noch keine belastbare Messung von Demo-/Walkthrough-Starts; aus Search Console lassen sich solche Aktionen nicht ableiten. Keine Conversion- oder Ranking-Zusage und keine willkürliche Prozentvorgabe. Ein wöchentlicher Publikationsrhythmus dient der redaktionellen Arbeit und ist kein gemessener Rankingfaktor.
