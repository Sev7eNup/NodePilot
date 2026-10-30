# Artikelpaket für den 01.11.2026

Termin und vorbereiteter Umfang am 25.10.2026 vom Nutzer bestätigt. Die gemeinsame Wiederaufnahme am 01.11. und 08.11. ist im [Redaktionsplan](seo-editorial-plan.md#wiederaufnahme-am-0111-und-08112026) mit Arbeitsbaum, Nachweisen und offenen Veröffentlichungsschritten festgehalten.

Vorbereitet am 25.10.2026, Zeitzone Europe/Berlin. Auftrag: Artikel vollständig schreiben und anpassen, einschließlich Suchmetadaten, Verlinkung und überprüfbarer Beispiele. Der Stand liegt lokal im Arbeitsbaum `E:/NodePilot-worktrees/seo-gmsa`, Branch `docs/seo-gmsa-expansion`. Es wurde nichts veröffentlicht oder automatisch terminiert.

## Enthaltene Artikel

| Thema | Deutsch | Englisch | Vorbereitung |
| --- | --- | --- | --- |
| PowerShell als geplante Aufgabe mit gMSA | [DE](../src/nodepilot-docs-ui/content/blog/gmsa-scheduled-task.de.md) | [EN](../src/nodepilot-docs-ui/content/blog/gmsa-scheduled-task.en.md) | Bereits im Windows-Labor geprüfter Entwurf; passende verwandte Artikel ergänzt |
| PowerShell try/catch | [DE](../src/nodepilot-docs-ui/content/blog/powershell-try-catch.de.md) | [EN](../src/nodepilot-docs-ui/content/blog/powershell-try-catch.en.md) | Neu geschrieben; Continue/Stop, finally, Prozess-Exitcodes und NodePilot-Fehlerverhalten getrennt |
| AI Workflow Automation für Windows | [DE](../src/nodepilot-docs-ui/content/blog/ai-workflows.de.md) | [EN](../src/nodepilot-docs-ui/content/blog/ai-workflows.en.md) | Bestehenden Beitrag überarbeitet; Referenzskript, JSON-Bericht und Gegenproben ergänzt; Datenfreigabe präzisiert |
| PowerShell Execution Policy | [DE](../src/nodepilot-docs-ui/content/blog/powershell-execution-policy.de.md) | [EN](../src/nodepilot-docs-ui/content/blog/powershell-execution-policy.en.md) | Neu geschrieben; Kontext, Vorrangfolge, Dateimarkierung und gezielte Korrektur erklärt |

Alle vier stehen im Katalog weiterhin auf `draft`, ohne `publishedAt` oder vorgetäuschtes Aktualisierungsdatum. Der Normalbuild schließt sie aus; der Vorschaubuild enthält sie mit `noindex, follow`. Das Paket bereitet acht Sprachseiten vor. Die gMSA-Diagnose bleibt separat für **08.11.2026** vorgesehen und wird vom Patch nicht freigeschaltet.

## Suchabsicht und Abgrenzung

- **try/catch:** Primär die Frage beantworten, warum ein Skript trotz Fehler weiterläuft. Allgemeine Ergebnisabnahme bleibt das Thema des späteren Entwurfs `verify-results`.
- **AI workflow automation:** Den bestehenden Slug `ai-workflows` beibehalten. Der Artikel zeigt KI-gestützte Windows-/PowerShell-Arbeit; er verspricht keine Desktop-Steuerung oder autonome Reparatur.
- **execution policy:** Ein Skript startet nicht. Davon trennen wir Fehler während eines bereits gestarteten Skripts und fehlende Datei-/SMB-Rechte.
- **gMSA:** Identität, Anmeldung und Zugriff einer geplanten Aufgabe bleiben die Hauptfrage.

Die [kostenlose Erhebung](seo-free-keywords-2026-10-25.md) liefert für try/catch und AI workflow automation jeweils die Klassen >1.000 USA und >100 Deutschland. Execution Policy wurde anschließend auf Nutzerhinweis aufgenommen; sein Suchvolumen wurde in dieser Sitzung nicht gemessen. Keine der Zahlen ist eine Besucherprognose. Es wurden keine kostenpflichtigen SEO-Abfragen ausgelöst.

## Vorbereiteter Veröffentlichungspatch

[seo-release-2026-11-01.patch](seo-release-2026-11-01.patch) ist ein noch nicht angewendeter Patch für den aktuellen Arbeitsstand. Er setzt die vier Katalogeinträge auf `published` mit Datum 01.11.2026, hebt die gMSA-Aufgabenanleitung als nächsten Beitrag hervor und ergänzt Kontextlinks in den veröffentlichten gMSA-Grundlagen sowie dem PowerShell-5.1/7-Vergleich. Deren `modifiedAt` wird dafür auf 01.11.2026 gesetzt. Die erwartete Zahl veröffentlichter Artikel im bestehenden Build-Test steigt von acht auf zwölf.

Der Patch enthält keine Artikeltexte: Die oben verlinkten Markdown-Dateien und die heutigen Katalog-/Teständerungen müssen gemeinsam mit ihm erhalten bleiben. Er ist kein eigenständiges Deployment-Artefakt. Kontextlinks zu den anderen Beiträgen des Pakets setzen deren gemeinsame Veröffentlichung voraus. Bei einer Auswahl einzelner Artikel den Patch und die Links entsprechend anpassen.

Am 01.11. im Arbeitsbaum:

```powershell
git apply --check --ignore-space-change docs/seo-release-2026-11-01.patch
git apply --ignore-space-change docs/seo-release-2026-11-01.patch
```

`--ignore-space-change` berücksichtigt die CRLF/LF-Unterschiede des vorhandenen Arbeitsbaums. Die Prüfung ist auf dem vorbereiteten Stand erfolgreich. Bei inzwischen veränderten Dateien einen Konflikt fachlich auflösen, nicht mit `--reject` oder erzwungener Übernahme übergehen. Erfolgt die tatsächliche Veröffentlichung später, die Veröffentlichungs- und Änderungsdaten vorher anpassen.

Anschließend im Paket `src/nodepilot-docs-ui` die betroffenen Tests und den Produktionsbuild ausführen:

```powershell
npx vitest run src/site/blog-build.test.mjs src/site/prerender.test.ts src/site/seo.test.ts
npx tsc -b
$env:NP_BLOG_PREVIEW = '0'
npm run build:site
```

Vor dem später beauftragten Website-Deployment müssen die acht URLs im Produktionsoutput vorhanden und indexierbar sein, mit passenden Canonicals, DE/EN-Verweisen, Metabeschreibungen und tatsächlichem Datum. Die gMSA-Diagnose vom 08.11. bleibt ausgeschlossen. Für die vollständige Website gelten anschließend die bestehenden Build-/Assemble-Schritte aus [README](../src/nodepilot-docs-ui/README.md) und der vorhandene Veröffentlichungsprozess; der Patch startet keinen Upload.

## Nachweise und verbleibender Veröffentlichungsschritt

Die [Inhaltsvalidierung](testing/seo-november-content-validation.md) dokumentiert die ausgeführten Skriptfälle und Website-Checks. Sie trennt lokale Windows-PowerShell-5.1-Nachweise von nicht ausgeführten PowerShell-7-, Remote-, Modell- und Gruppenrichtlinienfällen.

Nach der tatsächlichen Veröffentlichung Sitemap und Indexierungsstatus in Search Console prüfen; vier und acht Wochen später die Artikel nach Sprache, Land und Suchanfrage auswerten. Eine vorzeitige Indexierungsanforderung für Entwürfe ist nicht vorgesehen. Keine Benachrichtigung, Zeitsteuerung, Veröffentlichung, Git-Commit oder Übertragung ins Remote-Repository wurde eingerichtet.
