# Auffindbarkeit in KI-Suchen — 25.10.2026

## Ergebnis

NodePilot ist technisch gut für das Lesen durch Suchsysteme vorbereitet. Bei einer direkten Markenfrage erkennen die zwei getesteten Modelle das richtige Windows-/PowerShell-Projekt. Bei einer allgemeinen Werkzeugfrage empfehlen beide andere Produkte. Die Fähigkeit, Inhalte abzurufen, ist damit besser belegt als die spontane Empfehlung oder Zitierung bei allgemeinen Fachfragen.

Zusätzlich trat während der HTTP-Prüfung ein Verfügbarkeitsproblem auf: zunächst erfolgreiche Abrufe wechselten zu HTTP 503, anschließend auch mit neutralem User-Agent. Eine gezielte Sperre nur gegen Perplexity oder Bing ist damit nicht belegt. Die Ursache muss anhand der Hosting-Logs geklärt werden.

Die Veröffentlichungstermine der gMSA-Vertiefungen sind vom Nutzer bestätigt: **01.11.2026 Aufgabenplanung**, **08.11.2026 Diagnose**, jeweils DE/EN. Sie stehen im Redaktionsplan und im OpenSEO-Projektkontext. Keine automatische Veröffentlichung oder Benachrichtigung wurde eingerichtet.

## Umfang und Grenzen

- Live-HTTP-Abrufe von robots.txt, llms.txt, beiden Sitemaps sowie elf HTML-Seiten: Startseiten DE/EN, Produktseite, drei Anwendungsseiten, gMSA DE/EN, PowerShell-Vergleich EN und zwei Dokumentationsseiten EN.
- HTML ohne JavaScript ausgewertet: Text, Überschriften, Canonical, Robots-Metadaten, X-Robots-Tag und JSON-LD. Keine vollständige Website- oder Barrierefreiheitsprüfung.
- Sechs zusätzliche Abrufe desselben gMSA-Artikels mit Crawler-Kennungen. Diese kommen vom lokalen Prüfgerät, **nicht von verifizierten Anbieter-IP-Adressen**. Erfolg oder Fehler simuliert daher keinen vollständigen echten Crawler-Besuch.
- Vorhandene Search-Console-Indexprüfung aus der [SEO-Erhebung desselben Tages](seo-research-2026-10-25.md) wiederverwendet.
- OpenSEO Prompt Explorer: drei feste Prompts, jeweils OpenAI und Perplexity, Websuche erlaubt, kein Länderfilter. Fünf Antworten erfolgreich, eine mit Anbieterfehler. Modelle laut Antwortmetadaten: `gpt-5.6-luna` und `sonar-reasoning-pro`.
- Dies sind **Modell-API-Antworten**, keine Messung der ChatGPT-/Perplexity-Weboberflächen und kein Ranking aller LLMs. Nur eine Beobachtung pro Prompt/Modell; Formulierung, Modell, Suchstand und Wiederholung können Ergebnisse ändern.
- OpenSEOs AI Visibility Tracker ist noch nicht eingerichtet (`configured: false`); Provider ist verfügbar. Kein wiederkehrendes kostenpflichtiges Tracking wurde aktiviert.

Rohdaten: `.tmp/llm-2026-10-25/` im Arbeitsbaum, nicht versioniert. Die entscheidenden Ergebnisse und die exakten Prompts sind hier festgehalten.

## Technische Grundlage

| Prüfung | Befund | Bewertung |
| --- | --- | --- |
| robots.txt | HTTP 200; User-agent: * und Allow: /; beide Sitemaps aufgeführt | Kein Ausschluss von Such-Crawlern durch diese Datei |
| Sitemaps | HTTP 200; Website 34 URLs, Dokumentation 90 URLs | Entdeckungspfad vorhanden; gMSA und aktueller PowerShell-Artikel enthalten |
| Inhalte ohne JavaScript | Alle elf HTML-Seiten beim ersten Abruf HTTP 200 und mit Haupttext; gMSA EN ca. 967 Wörter im extrahierten Hauptbereich | Inhalt liegt nicht nur in einer leeren SPA-Hülle |
| Indexierungs-/Snippet-Regeln | Keine X-Robots-Sperren oder data-nosnippet-Markierungen in den geprüften HTML-Seiten; Doku index/follow | Kein beobachteter Ausschluss; keine Aussage über ungeprüfte Seiten |
| Strukturierte Daten | Organization, WebSite, SoftwareApplication auf der Startseite; BlogPosting/BreadcrumbList bei Artikeln; TechArticle in der Doku | Struktur und Projektzuordnung vorhanden; kein spezielles KI-Schema erforderlich |
| Projektidentität | Website und Schema verlinken Sev7eNup/NodePilot auf GitHub; Lizenz und Windows-Ausrichtung genannt | Gute vorhandene Grundlage zur Unterscheidung gleichnamiger Produkte |
| llms.txt | HTTP 200; englische Projektbeschreibung und Links zu veröffentlichten Seiten/Doku | Vorhandene Orientierungshilfe; kein Beleg für Aufnahme oder Ranking in KI-Antworten |
| Google-Index | 10/16 Blog-Sprach-URLs indexiert, beide gMSA-Grundlagen enthalten | Sechs fehlende URLs bleiben ein konkreter Entdeckungsengpass |

Die extrahierten Wortzahlen enthalten auch Navigation und gegebenenfalls im DOM vorhandene ausgeblendete Texte; sie sind keine redaktionelle Mindestlänge oder Qualitätskennzahl.

Für die Suchfunktion ist bei OpenAI **OAI-SearchBot** relevant; GPTBot betrifft einen anderen Zweck, das mögliche Modelltraining. Anthropic unterscheidet entsprechend Claude-SearchBot/Claude-User von ClaudeBot. PerplexityBot dient Suchergebnissen. Die vorhandene Wildcard-Regel erlaubt diese Zugriffe bereits; separate identische Allow-Blöcke würden keinen nachgewiesenen Vorteil schaffen. Trainingsfreigaben wurden nicht verändert. Quellen: [OpenAI](https://developers.openai.com/api/docs/bots), [Anthropic](https://support.claude.com/en/articles/8896518-does-anthropic-crawl-data-from-the-web-and-how-can-site-owners-block-the-crawler), [Perplexity](https://docs.perplexity.ai/docs/resources/perplexity-crawlers).

Google verlangt für seine generativen Suchfunktionen die üblichen Suchvoraussetzungen, insbesondere Indexierung und Snippet-Eignung. Es nennt llms.txt ausdrücklich nicht als Rankinghebel und verlangt kein besonderes schema.org-Markup für KI-Antworten. Deshalb ist eine neue „KI-Datei“ hier nicht der vorrangige nächste Schritt. [Google Search Central](https://developers.google.com/search/docs/fundamentals/ai-optimization-guide).

## Verfügbarkeit: offener Befund

Die ersten 15 Abrufe (vier Text/XML-Ressourcen und elf HTML-Seiten) waren erfolgreich. Danach antwortete der englische gMSA-Artikel auf die Kennungen OAI-SearchBot, ChatGPT-User, Claude-SearchBot und Claude-User mit 200; PerplexityBot und bingbot erhielten 503. Diese sechs Requests liefen gleichzeitig.

In der anschließenden seriellen Gegenprobe zwischen **21:52:40 und 21:52:44 MEZ** erhielten neutraler Audit-Client, PerplexityBot, bingbot und erneut der neutrale Client durchgehend 503. Ein einzelner neutraler Abruf um **21:54:20 MEZ** lieferte ebenfalls 503. Die Antwort kam von Apache, enthielt eine 339 Byte lange Fehlerseite und keinen Retry-After-Header.

Das Muster ist mit einem Zugriffslimit oder einer vorübergehenden Hosting-Störung vereinbar. Es beweist keine konkrete Ursache und keine allgemeine Nichterreichbarkeit aus allen Netzen. In der geprüften Repository-.htaccess steht keine auf diese Bots gerichtete Sperre; die tatsächlich wirksame Hosting-Konfiguration wurde nicht eingesehen. Weitere schnelle Abrufe wurden gestoppt.

**Priorität:** Hosting-Access-/Error-Logs für diesen Zeitraum prüfen, Rate-Limit und Bot-Regeln nachvollziehen, echte Crawler anhand der offiziellen Identitätsprüfung verifizieren. Keine Schutzmaßnahmen pauschal deaktivieren und keine Freigabe allein anhand eines leicht fälschbaren User-Agent-Namens einrichten. Danach mit wenigen seriellen Abrufen nachweisen, dass normaler Zugriff und verifizierte Such-Crawler zuverlässig die Inhalte erhalten.

## Tatsächliche Antwortstichprobe

| Prompt | OpenAI-Modell über OpenSEO | Perplexity-Modell über OpenSEO |
| --- | --- | --- |
| Allgemeine PowerShell-Werkzeugauswahl, DE | NodePilot fehlt; Antwort nennt Rundeck, Jenkins und Kestra | NodePilot fehlt; Antwort nennt u. a. Rundeck, Windmill, Jenkins, AWX und Kestra |
| gMSA-Aufgabe und SMB-Zugriff, EN | Keine NodePilot-Erwähnung oder eigene Domain in den Quellen | Anbieterfehler UPSTREAM_ERROR; **nicht** als fehlende Erwähnung werten |
| Was ist NodePilot?, DE | Richtiges Projekt, Windows/PowerShell/WinRM, Apache-2.0; verlinkt im Antworttext GitHub | Richtiges Projekt und Lizenz; Antworttext verweist auf GitHub/Projektlizenz; eigene Website zusätzlich in der gelieferten Quellenliste |

Die Inhalte der Wettbewerberantworten wurden nicht als geprüfte Produktempfehlung übernommen. Die Stichprobe untersucht nur, welche Quellen und Projekte die Modelle auswählen.

Wichtig für die Auswertung: OpenSEO markiert auch gleichnamige fremde Produkte mit `matchedBrand: true`. In Perplexitys Quellenliste standen etwa nodepilot.app und nodepilot.dev. Ein Markenwort-Treffer allein zählt daher nicht als Erfolg für dieses Projekt. Eine gelieferte Quellenliste kann außerdem mehr Treffer enthalten als die ausdrücklich im Antworttext verwendeten Belege. Eigene Domain im Quellenpool, tatsächlich gesetztes Zitat und korrekte Produktempfehlung werden getrennt gezählt.

### Exakte Prompts für Wiederholungen

1. „Welche selbst gehosteten Open-Source-Werkzeuge eignen sich, um PowerShell-Skripte als mehrstufige Windows-Workflows mit Zeitplänen und Fehlerbehandlung auszuführen? Nenne konkrete Optionen und verlinke Quellen.“
2. „How do I run a PowerShell scheduled task with a gMSA and verify that it can access an SMB share? Cite practical guides and official documentation.“
3. „Was ist NodePilot, wofür wird es verwendet und unter welcher Lizenz ist es verfügbar? Bitte belege die Antwort mit Quellen.“

Der Markenname wurde nur im dritten Prompt vorgegeben. OpenSEOs `highlightBrand` dient laut Schnittstelle der Auswertung und verändert den Prompt nicht. Antworten zu allgemeinen Fragen wurden nicht nachträglich auf NodePilot gelenkt.

## Nächste Schritte nach Wirkung

1. **Zuverlässigen Abruf und Indexierung absichern.** 503-Befund anhand Hosting-Logs klären; die sechs fehlenden Blog-URLs und den alten Canonical-Crawl des deutschen Orchestrator-Artikels in Search Console nachverfolgen. Gute Inhalte helfen nicht, wenn die Antwortsysteme sie nicht zuverlässig abrufen oder finden können.
2. **Projektzuordnung konsistent halten.** In sichtbaren Kurzbeschreibungen die Kombination „NodePilot – Open-Source-Workflow-Automatisierung für Windows und PowerShell“ sowie nodepilot.run und Sev7eNup/NodePilot konsistent verwenden. Bestehende GitHub-/Schema-Verknüpfungen erhalten. Keine Umbenennung oder Vielzahl zusätzlicher Aliasnamen nötig. Der Test zeigt bereits richtige Zuordnung, aber die Namenskollision muss bei Messungen berücksichtigt werden.
3. **Die geprüften Fachartikel wie bestätigt veröffentlichen.** Das konkrete gMSA-/SMB-Beispiel und die Diagnose liefern eigene praktische Belege. Die bestehende PowerShell-Einstiegsseite bei der nächsten Inhaltsrunde um einen knappen, überprüfbaren Entscheidungsabschnitt ergänzen: geeignete Einsätze, Grenzen, Ausführungsmodell, Fehlerbehandlung, Lizenz und Betriebsanforderungen; auf die jeweilige Doku verweisen. Keine erfundene Vergleichstabelle oder unbelegte Überlegenheit.
4. **Echte Referenzen statt bloßer Namensnennungen aufbauen.** Öffentliche Anwendungsbeispiele und nachvollziehbare Erfahrungen anderer Nutzer sind sinnvolle Ergänzungen. Diese Prüfung hat keine neuen Beiträge, Nachrichten oder Verzeichniseinträge veröffentlicht. Eine Wirkung solcher Referenzen auf ein bestimmtes Modell ist hier nicht gemessen.
5. **Mit festen Fragen nachmessen.** Nach Indexierung der November-Artikel dieselben Prompts unter dokumentierten Modell-/Suchbedingungen wiederholen. Erfassung: richtiger Projektbezug, echte Nennung im Antworttext, direkte Website-Zitate, GitHub-Zitate, verwendete Zielseite, Anbieterfehler. Allgemeine Fachfragen und vorgegebene Markenfragen getrennt auswerten. Wiederholte Beobachtungen sind aussagekräftiger als ein einzelner „LLM-Sichtbarkeitsscore“.

Bei späterem laufendem Tracking Länder und Sprachen getrennt festlegen und den Ressourcenbedarf vor Aktivierung bestimmen. Website-Referrer von KI-Diensten können zusätzliche Besuchssignale liefern, beweisen aber weder vollständige Zitierhäufigkeit noch sämtliche Empfehlungen. Ein allgemeines Training eines Modells auf die Website kann aus diesen Tests nicht abgeleitet werden.
