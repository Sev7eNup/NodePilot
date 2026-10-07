# Lokale SEO-Umsetzung

> **Protokoll, Stand 27.09.2026.** Die Zahlen unten wurden an diesem Build gemessen und werden nicht nachgeführt. Aktuelle Seitenzahlen stehen im `README.md`.

Branch: `fix/seo-local`, basierend auf veröffentlichtem `origin/main` (`3ba63cb`).
Worktree: `E:/NodePilot-seo`. Am 27.09.2026 wurden Veröffentlichung und PR beauftragt. Der ursprüngliche Arbeitsordner und seine vorhandenen Änderungen bleiben unberührt. Kein neues Produktrelease.

## Abgleich mit den sieben Empfehlungen

1. **Vollständiges HTML:** Blogartikel und Dokumentationskapitel werden samt Überschriften, Text, Ankern und internen Links beim Build gerendert. Jeder Artikel liefert nur seinen eigenen Text in seinem HTML. Artikeltexte sind weder im gemeinsamen Website-JavaScript noch im Doku-/Installer-Bundle enthalten. Artikelnavigation lädt das passende Dokument; Sprachwechsel, Reload und Verlauf funktionieren weiterhin.
2. **Sprach-URLs:** Bestehende deutsche Adressen bleiben stabil, Englisch verwendet `/en/`. Jede Fassung hat eigenen Inhalt, Titel, Canonical sowie gegenseitige `de`, `en` und `x-default`-Verweise. Keine unnötige Verschiebung der deutschen URLs nach `/de/`. Doku bleibt unter `/docs/de/` und `/docs/en/`. Die nur deutschen Rechtstexte bekommen keine falsche englische Fassung.
3. **Einstiegsseiten:** Drei zweisprachige Seiten für PowerShell-Automatisierung, SCOrch-Alternativen und selbst gehostete Windows-Automatisierung. Jede enthält ein konkretes Beispiel, einen eigenen Screenshot, Einschränkungen, passende Doku-Links und einen Demo-Einstieg. Die SCOrch-Seite enthält zusätzlich einen Vergleich der zu prüfenden Migrationsaspekte.
4. **Titel:** Produkt-, Blog- und Einstiegsseiten haben konkrete Themen im Titel; der Dienstkontenartikel nennt ausdrücklich gMSA. Die bestehenden Artikel-URLs bleiben erhalten.
5. **Artikelplan und Verlinkung:** Alle 15 vorhandenen Artikel liegen auf Deutsch und Englisch vor. Der Katalog ordnet jedem Artikel Hauptfrage, Zielgruppe, Einstiegsseite, passende Doku und verwandte Beiträge zu. Sichtbare Links benennen die Ziele. Details: [Artikelplan](seo-editorial-plan.md).
6. **Marke und Urheberschaft:** Die lokale README und vorbereitete Profiltexte beschreiben NodePilot einheitlich als Windows-/PowerShell-Automatisierung. Sichtbare Urheberangabe und `BlogPosting.author` nennen vorerst das Projekt NodePilot; keine unbestätigte Person wird eingetragen. Für die zwei bestehenden Artikel ist das erste erfolgreiche Pages-Deployment vom 20.09.2026 der Beleg für das Veröffentlichungsdatum. Entwürfe haben kein erfundenes Datum; `modifiedAt` wird nur bei einer tatsächlichen veröffentlichten Inhaltsänderung gesetzt. Weitere strukturierte Daten: WebSite, WebPage, SoftwareApplication, BreadcrumbList und TechArticle. [Profiltexte](project-profile-copy.md) sind lokal vorbereitet, externe Profile nicht geändert.
7. **Messung und Technik:** Google Search Console und Bing Webmaster Tools wurden bereits vor diesen lokalen Änderungen verifiziert und die beiden öffentlichen Sitemaps eingereicht. Beim Setup hatte Google 8 Website- und 88 Doku-URLs gelesen. Kein Tracking-Skript hinzugefügt. WebP-Bildvarianten mit `srcset`, `sizes`, Bildmaßen und Lazy Loading, kleineres Website-Logo, Latin-Fonts sowie Apache-Kompression für `text/javascript` und `application/xml` sind umgesetzt. Lange Cache-Zeiten gelten nur für versionierte Assets, Weiterleitungsskripte werden revalidiert.

## Vorschau und Veröffentlichung

Lokale Vorschau: **http://127.0.0.1:5190/blog/**

| Inhalt | Deutsche Vorschau | Englische Vorschau |
| --- | --- | --- |
| PowerShell | `/powershell-automation/` | `/en/powershell-automation/` |
| SCOrch-Alternative | `/scorch-alternative/` | `/en/scorch-alternative/` |
| Eigener Betrieb | `/self-hosted-automation/` | `/en/self-hosted-automation/` |
| Nächster Artikel | `/blog/first-workflow/` | `/en/blog/first-workflow/` |

Alle 15 Artikel können gemeinsam geprüft werden. **Erster Workflow** ist für den 27.09.2026 als dritter öffentlicher Artikel freigegeben, zwölf weitere bleiben Entwürfe. Vorhandene Quellen, konstruierte Fallbeispiele und Hinweise auf abweichende Screenshots wurden erhalten. Die beiden älteren Artikel behalten ihr ursprüngliches Veröffentlichungsdatum und erhalten für die jetzige redaktionelle Aktualisierung `modifiedAt: 2026-09-27`.

`NP_BLOG_PREVIEW=1` aktiviert alle Entwürfe. Sie sind sichtbar als Vorschau gekennzeichnet, tragen `noindex, follow` und stehen nicht in der Sitemap. Ohne die Variable enthält der normale Build nur veröffentlichte Artikel. Links zu noch nicht veröffentlichten Tutorials führen dann zum passenden Doku-Kapitel. Eine gemeinsame Vorschau bedeutet nicht, dass alle Artikel gleichzeitig veröffentlicht werden sollen.

Der normale Stand hat 22 Website- und 88 Doku-Sitemap-URLs. Die Vorschau ergänzt 24 Artikeladressen (zwölf pro Sprache), insgesamt 134 geprüfte Inhaltsseiten. Demo, 404 und Doku-Sprachauswahl werden nicht zusätzlich als Inhaltsseiten indexiert.

## Reproduzieren

Abhängigkeiten mit `npm ci` in `src/nodepilot-ui` und `src/nodepilot-docs-ui` installieren. Anschließend in `src/nodepilot-docs-ui`:

```powershell
$env:NP_SITE_ORIGIN = 'http://127.0.0.1:5190'
$env:NP_BLOG_PREVIEW = '1'
npm run preview:site -- --host 127.0.0.1 --port 5190
```

Die bestehende Vorschau vorher beenden, wenn der Port belegt ist. Der Befehl baut Website, Dokumentation und Demo und setzt sie lokal zusammen. `npm run images:site` erzeugt die Bildvarianten reproduzierbar aus den Originalen und läuft automatisch vor dem Website-Build.

Vor einem späteren Produktionsbuild `NP_BLOG_PREVIEW` entfernen oder auf `0` setzen und `NP_SITE_ORIGIN` auf `https://www.nodepilot.run` setzen. Einen freigegebenen Artikel im Katalog auf `published` stellen und sein tatsächliches Veröffentlichungsdatum eintragen. Der Build prüft Status und Datumsangaben. Lokale Titel-/Textänderungen an bereits veröffentlichten Artikeln erhalten erst beim tatsächlichen Veröffentlichen ein entsprechendes `modifiedAt`.

## Prüfung

- ESLint ohne Warnungen, TypeScript sowie Website-, Doku- und Demo-Builds erfolgreich.
- 204 Unit-/Build-Tests erfolgreich, einschließlich Produktionsausschluss von Entwürfen, Preview-Noindex, Artikelquellen, Metadaten, Verlinkung, Sprachpfaden, SSR und Weiterleitungen.
- 27 Browserprüfungen erfolgreich: alle 15 Artikel in beiden Sprachen ohne JavaScript, Sprache/Reload/Verlauf, Artikelwechsel, Suche, mobile Breiten aller Artikel, responsive Bildauswahl, Tour und historische Pages-/Reddit-Links. Der abschließende Kategorienabgleich (drei Hintergrundartikel) ist zusätzlich im Build-Test abgesichert.
- Produktionsstand: drei freigegebene Artikel, keine Entwurfsdateien oder Links zu deren Artikeladressen. Die Trennung zwischen Produktionsbuild und lokaler Vorschau wird im Build-Test geprüft.
- Fertige lokale Dateien: 134 Inhaltsseiten, eindeutige Titel, passende Canonicals und genau eine sichtbare H1. Alle 150 gefundenen lokalen Link-, Bild-, Skript- und Stylesheet-Ziele existieren.
- Manuelle Sichtprüfung in Edge: Blogübersicht, Vorschaukennzeichnung und SCOrch-Beispiel mit Vergleich und Screenshot.
- Der Designer-Screenshot lädt bei 390 px als 480-px-WebP mit 9.116 Bytes, gegenüber der ursprünglich etwa 694 KB großen PNG. Das Website-Logo hat ungefähr 4,9 KB. Reale Core Web Vitals lassen sich daraus nicht ableiten.

## Veröffentlichung am 27.09.2026

Das vollständige Produktionspaket wurde auf den bestehenden Webspace übertragen. Die 183 vorhandenen Dateien an den Zielpfaden wurden vorab lokal gesichert. Von 290 Build-Dateien mussten 212 übertragen werden; identische Dateien und zusätzliche Dateien auf dem Webspace blieben erhalten. Übertragen wurde mit der hinterlegten SFTP-Konfiguration und geprüftem RSA-Hostschlüssel. Dateien wurden zunächst unter einem temporären Namen hochgeladen und anschließend atomar ersetzt.

Live geprüft: Alle 110 Inhaltsseiten antworten mit HTTP 200 und stimmen bytegenau mit dem Produktionsbuild überein. Drei Artikel sind öffentlich; alle 24 Sprachadressen der zwölf Entwürfe liefern HTTP 404. Beide Sitemaps und robots.txt entsprechen dem Build. JavaScript wird mit gzip und dem vorgesehenen Cache-Header ausgeliefert. Die drei Kombinationen aus HTTP/HTTPS und Domain ohne www leiten korrekt auf HTTPS mit www weiter, einschließlich Query-Parametern. Demo-Deep-Links funktionieren; fehlende Demo-Assets bleiben HTTP 404.

Die abschließende Browserkontrolle deckte zusätzliche React-Vorladelinks auf, die noch auf die ursprünglichen Markdown-Bildpfade zeigten. Lazy Loading wird jetzt bereits im Markdown-Renderer gesetzt, bevor responsive URLs eingetragen werden. Die beiden betroffenen Artikelseiten wurden nachgesichert und korrigiert übertragen; der Build-Test verhindert solche Vorladelinks künftig.

Indexierung und tatsächliche Ranking-/Klickentwicklung werden anschließend in Search Console und Bing beobachtet. Die vorbereiteten externen Profiltexte sind weiterhin Vorschläge; zusätzliche externe Profile wurden nicht geändert. Kein Produktrelease und kein Tag angelegt.

IndexNow war eine optionale spätere Ergänzung, kein Ersatz für die eingerichteten Sitemaps. Unveröffentlichte Vorschauseiten werden nicht an Suchmaschinen gemeldet. Die bestehenden großen Doku-/Demo-JavaScript-Dateien erzeugen weiterhin Vite-Größenhinweise; die Artikeltexte sind daraus entfernt, eine vollständige Neuaufteilung dieser Anwendungen ist nicht Teil der SEO-Empfehlung.

## Referenzen

- [Google: Sprachversionen auf getrennten URLs](https://developers.google.com/search/docs/specialty/international/managing-multi-regional-sites)
- [Google: crawlbare Links und beschreibende Linktexte](https://developers.google.com/search/docs/crawling-indexing/links-crawlable)
- [Google: Article-Markup](https://developers.google.com/search/docs/appearance/structured-data/article)
