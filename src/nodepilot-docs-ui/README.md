# NodePilot Docs UI

Dokumentations-Website für NodePilot — eine React 19 SPA (Vite + Tailwind CSS 4), die Markdown-Inhalte aus `content/` rendert und die Optik des NodePilot-App-Shell spiegelt: dieselbe Sidebar (292px-Rail mit Brand-Header, Suchfeld, Section-Titles und Icon-Pills), dieselbe schlanke TopBar und dieselben Design-Tokens (Light = Blau, Dark = das Default-Skin **„Azur"**, kühles Graphit + Azur-Akzent).

**Design-Tokens und Sidebar-CSS sind aus `src/nodepilot-ui/src/index.css` kopiert**, nicht importiert (getrennte Vite-Roots, getrenntes `npm ci` in CI). Beim Kopieren werden zwei mechanische Rewrites angewendet, damit ein Re-Sync ein trivialer Diff bleibt — beide sind im Kopf des kopierten Blocks in `src/index.css` dokumentiert:

1. `[data-skin="dark"]` entfällt (die Docs haben nur ein Dark-Skin, kein 7-Skin-System).
2. Blankes `aside` in Selektoren wird zu `.np-sidebar` (sonst erbt die TOC-Rail in `Toc.tsx` den Rail-Gradient).

Im selben Paket liegt außerdem die Projekt-Website (`src/site/`), siehe [Projekt-Website](#projekt-website).

## Entwickeln

```powershell
cd src\nodepilot-docs-ui
npm install
npm run dev      # http://localhost:5174/docs/
```

## Build

```powershell
npm run build    # statischer Output in dist/
npm run preview  # Build lokal vorschauen
```

## Projekt-Website

Die Projekt-Website liegt in `src/site/`: Vanilla-TypeScript ohne React und Tailwind, zweisprachig
(DE/EN), mit eigenem Vite-Build (`vite.site.config.ts`, Output `dist-site/`). Sie ist nie Teil von
`dist/` und damit nie Teil des Server-Artefakts oder des Desktop-Pakets.

```powershell
npm run dev:site       # nur die Website, http://localhost:5175
npm run build:site     # Website-Build nach dist-site/
npm run build:demo     # Browser-Demo im Nachbarpaket bauen (src/nodepilot-ui -> dist-demo/)
npm run assemble:site  # _site/ aus dist-site/, dist/, dist-demo/ und pages-media/ zusammensetzen (Builds vorher)
npm run preview:site   # build + build:site + build:demo + assemble:site, danach _site/ auf http://localhost:5175
```

`preview:site` ist die einzige vollständige lokale Vorschau: `docs/`, `demo/` und `media/` gibt es
nur im zusammengesetzten `_site/`. Jede dieser Eingaben ist Pflicht — eine optionale ließe einen
Deploy die vorige Fassung stillschweigend weiterveröffentlichen, ohne dass etwas rot wird. Dieses Verzeichnis veröffentlicht `deploy/Publish-Site.ps1` auf dem Webspace.
GitHub Pages erhält ausschließlich `_pages-redirects/` aus `npm run build:pages-redirects`.
Der Pages-Workflow baut weder Website noch Demo und benötigt deren Assets nicht.

| In `_site/` | Quelle | Adresse |
|---|---|---|
| Wurzel | `dist-site/` | https://www.nodepilot.run/ |
| `docs/` | `dist/` | https://www.nodepilot.run/docs/ |
| `demo/` | `../nodepilot-ui/dist-demo/` | https://www.nodepilot.run/demo/ |
| `media/` | `pages-media/` | https://www.nodepilot.run/media/nodepilot-product-tour.mp4 |
| `og-image.png` | `public/og-image.png` | Vorschaubild der Website |

**Media-Bereich** (`/tutorials/`, „NodePilot in 2 Minuten"): Die Adresse bleibt für
bestehende Such- und Direktlinks erhalten; `/media/` liefert die Videodateien.
`npm run site:videos` liest die
gerenderten Folgen aus `out/nodepilot-training/` und erledigt drei Dinge:

- Es kopiert die MP4s nach `pages-media/training/`. Der Ordner ist gitignored.
- Es erzeugt die Poster unter `src/site/public/training/`.
- Es schreibt `src/site/videos.json`.

Die Dateinamen tragen einen Content-Hash. So bekommt ein neu gerendertes Video eine neue Adresse.
`publishedAt` und `youtube` pflegt man in `videos.json` von Hand; ein neuer Lauf übernimmt diese
Werte. `deploy/Publish-Site.ps1` lädt nur Videos hoch, die auf dem Server noch fehlen. Das
geschieht vor allen anderen Dateien. Fehlt danach eines, bricht der Deploy ab, bevor eine Seite
darauf verweist.

Deep Links in die Doku haben die Form `https://www.nodepilot.run/docs/<sprache>/<seite>/`.

- **`NP_SITE_ORIGIN`:** Standard ist `https://www.nodepilot.run`; Canonical, Open Graph,
  `hreflang`, Sitemaps und der Demo-Link nutzen diesen Ursprung. Die Variable überschreibt
  ihn für lokale Vorschauen oder andere Webspace-Ziele. `deploy/Publish-Site.ps1` setzt sie
  aus seiner Konfiguration. Die Demo verwendet feste Pfade unter `/demo/`.
- **`np-site-root`:** Beim Kopieren nach `_site/docs/` stempelt `assemble-site.mjs` ein
  `<meta name="np-site-root" content="../">` in die `index.html` — und schlägt fehl, wenn der
  `</head>`-Anker fehlt. Dasselbe `dist/` wird nämlich ein zweites Mal ausgeliefert, als
  `wwwroot/docs` im Produkt, wo weder Website noch Demo danebenliegen. `src/lib/siteContext.ts`
  liest das Meta, und nur wenn es da ist, zeigt die Sidebar die Rückwege zu `../` und `../demo/`.
  Ein `<meta>` und kein Inline-`<script>`, weil die Doku unter `script-src 'self'` läuft.

- **Routen sind echte Adressen.** `/product/`, `/blog/scorch-import/` und so weiter. Die
  Segmente sind englisch, weil eine Adresse beide Sprachfassungen bedient; `impressum` und
  `datenschutz` bleiben deutsch, weil es diese Seiten nur auf Deutsch gibt. Nach dem
  Vite-Build schreibt das Plugin `np-site-prerender` (`vite.site.config.ts`) aus der einen
  gebauten Hülle je Route eine eigene Datei, dazu `404.html`, `sitemap.xml` und `robots.txt`.
  Die Regeln dafür stehen als reine Funktionen in `src/site/prerender.ts`. Jede Datei bekommt
  eigenen Titel, eigene Beschreibung und eigene `canonical`-URL und enthält nur ihren eigenen
  Seitenabschnitt (`keepPage`); Seiten- und Sprachwechsel sind normale Seitenaufrufe.
  `npm run dev:site` liefert jede Routenadresse mit derselben Hülle, zugeschnitten auf die Route
  (Plugin `siteDevRoutes`); Artikeltexte gibt es nur im Build.

  Weil die Website auch in einem Unterverzeichnis liegen kann, ist in der Quelle
  jede interne URL relativ zur Wurzel geschrieben. Der Prerender hebt sie je Tiefe an und setzt
  `<meta name="np-site-base">` auf denselben Präfix; `main.ts` liest das Meta, um aus der Adresse
  eine Route zu machen, und `setBasePrefix()` gibt es an die Doku-Links weiter. **Wer eine neue
  Route anlegt, trägt sie in `ROUTE_PATHS` bzw. `ARTICLE_SLUGS` und in `routePages()` ein** —
  sonst entsteht keine Datei und die Adresse landet auf der 404-Seite.

- **Alte Doku- und Website-Links:** Früher lag die Doku an der Pages-Wurzel
  (`…/NodePilot/#/en/deployment/logs`), und die Website selbst benutzte Hash-Routen (`#/produkt`).
  `src/site/public/legacy-docs-redirect.js` läuft als erstes klassisches Script im `<head>` und
  leitet beides per `location.replace` weiter: einen Hash, dessen erstes Segment keine
  Website-Route ist, nach `docs/`, jeden anderen auf die echte Adresse — dabei benennt er die
  früheren deutschen Segmente um (`produkt` → `product`, `erleben` → `walkthrough`,
  `warum-nodepilot` → `why-nodepilot`). Dieselben Umbenennungen stehen als 301 in
  `src/site/public/.htaccess`, für Aufrufe ohne Hash. Website-Routen sind
  `SITE_ROUTE_SEGMENTS` aus `src/site/router.ts` (`walkthrough`, `product`, `blog`, `impressum`,
  `datenschutz`); dieselbe Liste steht wörtlich im Redirect-Script. Eine neue Route gehört in
  beide Listen und darf mit keinem Doku-Pfad und keiner Sprache kollidieren.
- **Texte und Sprache:** Website-Texte stehen in `src/site/i18n/de.ts` und `en.ts`.
  Blogtexte liegen separat in `content/blog/`; `src/site/blog-catalog.json` enthält Metadaten,
  Verlinkung und Veröffentlichungsstand. Sie werden als vollständiges HTML ausgeliefert und
  gehören nicht ins gemeinsame JavaScript. Deutsche URLs bleiben bestehen, englische Fassungen
  liegen unter `/en/`, jeweils mit eigenem Canonical und gegenseitigem `hreflang`.
  Die URL bestimmt die Sprache. `LANG_STORAGE_KEY` bewahrt die Auswahl für weitere Einstiege.
  `NP_BLOG_PREVIEW=1` bindet Entwürfe nur zur lokalen Prüfung ein (`noindex`, ohne Sitemap-Eintrag).
  Vor der Veröffentlichung die Variable entfernen oder auf `0` setzen. Für freigegebene Artikel
  `status: published` und das tatsächliche `publishedAt` setzen; `modifiedAt` nur bei einer
  wirklichen Aktualisierung. `npm run images:site` erzeugt responsive WebP-Varianten automatisch
  vor dem Website-Build. Einzelheiten: [SEO-Umsetzung](../../docs/seo-local-review.md).
- **Zwei Grafiken auf der Startseite**, beide als Inline-SVG ohne Bilddatei: das Workflow-Beispiel
  im Hero (`src/site/graph.ts`) und das Architekturdiagramm darunter (`src/site/topology.ts`). Die
  Geometrie steht in diesen beiden Dateien als reine Funktionen, `main.ts` schreibt die Koordinaten
  ins Markup und wählt je nach Breite das weite oder das gestapelte Layout. Beide Animationen
  laufen nur, solange die Grafik im Sichtfeld ist, und die globale
  `prefers-reduced-motion`-Regel in `site.css` schaltet sie ab — die Grafiken müssen deshalb ohne
  Bewegung vollständig lesbar bleiben.
- **Keine Drittanfragen:** Schriften (`@fontsource/ibm-plex-sans`, `@fontsource/ibm-plex-mono`),
  App-Icon und Screenshots aus `docs/images/` werden mitgebaut, das Video liegt unter `media/`.
  Google Fonts und `raw.githubusercontent.com` sind tabu; die Datenschutzerklärung verlässt sich
  darauf.
- **Rechtstexte:** Impressum (`/impressum/`) und Datenschutz (`/datenschutz/`) rendern
  `src/site/legal/impressum.de.html` und `src/site/legal/datenschutz.de.html`. Die Texte liefert
  der Projektinhaber. Sie liegen nur auf Deutsch vor und erscheinen auch in der englischen Ansicht
  auf Deutsch. Fehlt eine Datei oder ist sie leer, schlägt der Test fehl.
- **Eigene Domain:** Die Seite läuft unter <https://www.nodepilot.run/>, ausgeliefert von
  `deploy/Publish-Site.ps1` auf den eigenen Webspace. GitHub Pages leitet alte Links auf die
  entsprechende Adresse dort weiter, einschließlich historischer Hash-Links und Doku-Deep-Links.
  Bekannte Pfade erhalten HTML-Seiten mit Canonical, JavaScript-Weiterleitung und Meta-Refresh
  ohne JavaScript; `404.html` übernimmt übrige Pfade. Es handelt sich um Browser-Weiterleitungen,
  nicht um HTTP-301. Ziel-Origin ist fest `https://www.nodepilot.run`; Query und Anker bleiben erhalten.
- **Demo:** Browser-Routing unter `/demo/`, Apache-Fallback nur für Seitenpfade; fehlende Assets
  bleiben 404. Alte `#/…`-Links werden vor App-Start umgeschrieben. Die Demo bleibt `noindex`.
- **Umstellung veröffentlichen:** Erst den vollständigen Webspace-Build hochladen und Demo-Deep-Links
  samt Reload prüfen, danach den Pages-Workflow mit dem Weiterleitungsartefakt veröffentlichen.
  GitHub-Pages-Einstellung und DNS bleiben unverändert.

## Geführter Produkteinstieg

Die Website-Route `/walkthrough/` bietet zehn Aufgaben direkt in der Browserdemo an:
Workflow-Bau, Fehleranalyse und Dateibereitstellung stehen am Anfang. Danach folgen
Entscheidung, parallele Arbeit, Dienst-Recovery, Live Ops, Versionsvergleich,
Maschinenprüfung und Wartungsfenster.
Jede Karte öffnet `demo/?tour=<id>&lang=de` oder `lang=en`. Die Übersichtsgrafik bleibt
auf der Startseite.

Die Führung in `../nodepilot-ui/demo/ui/tour.ts` begleitet den echten Startdialog und die
Ausführungshistorie. Die acht weiteren Aufgaben liegen in `../nodepilot-ui/demo/ui/additionalTours.ts`.
Alle Änderungen und Ausführungen bleiben im Browser-Tab; ein Reload stellt die Seed-Daten wieder her.
Der Datei-Workflow kommt aus `scripts/example-guided-file-workflow.json`; Registry, Dienst und
Dateisystem sind simuliert. `Protected` simuliert fehlende Schreibrechte beim Kopieren.

`npm run test:site:e2e` prüft Einstieg, Sprachwechsel und drei Bildschirmgrößen.
Die eigentliche Führung wird mit `npm --prefix ../nodepilot-ui run test:e2e:demo` geprüft.
Vor dem ersten Lauf Chromium mit `npx playwright install chromium` installieren.

## Struktur

Marketingmedien liegen in `pages-media/`. Der normale Build für Server und Desktop enthält sie
nicht. Erst `scripts/assemble-site.mjs` kopiert sie beim Zusammensetzen von `_site/` nach
`_site/media/` (siehe [Projekt-Website](#projekt-website)). Die Video-Adresse
`…/NodePilot/media/nodepilot-product-tour.mp4` und damit der README-Video-Link bleiben dadurch
stabil. `.gitattributes` schließt `pages-media/` per `export-ignore` aus dem `git archive`-Snapshot
für `knowledge/source` aus.

- `src/data/nav.ts` — Seitenbaum, Gruppierung, Sidebar-Icon je Seite, Prev/Next-Logik, `groupOf()` für den Breadcrumb. Das `icon`-Feld ist **required**: `tsc -b` schlägt fehl, sobald eine neue Seite ohne Icon eingetragen wird.
- `src/lib/content.ts` — lädt nur `content/de/**/*.md` und `content/en/**/*.md` als Raw-Strings; Blogquellen bleiben außerhalb des Doku-/Installer-Bundles
- `src/lib/useTheme.ts` — Light/Dark-Toggle (LocalStorage). Die Erstauflösung passiert in der externen Datei `public/theme-init.js`, die `index.html` als klassisches Script ohne `defer`/`async` **vor** dem ersten Paint lädt (kein Theme-Flash, kompatibel mit `script-src 'self'`); der Hook seedet aus der gesetzten `html.dark`-Klasse.
- `src/components/` — `TopBar`, `Sidebar`, `DocPage`, `Toc`, `SearchModal`
- `src/index.css` — Tailwind + Design-Tokens (Material-3-Tonal-Palette / Azur) + portierte `.np-sidebar`-, `.np-nav`- und `.np-card`-Blöcke + `.np-prose`
- `index.html` — SPA-Root (`#root`) + Pre-Hydration-Theme-Script

Icons: `@carbon/icons-react` — dieselbe Bibliothek wie die Haupt-UI, und wo eine Docs-Seite auf eine App-Seite abbildet, ist auch dasselbe Glyph gewählt.

Die Sidebar ist ein einziges `<aside>` für beide Layouts: ab `lg` eine sticky 292px-Rail, darunter ein Off-Canvas-Drawer. Der Mobile-Zweig ist bewusst mit `max-lg:`-Utilities geschrieben (nicht mit `lg:`-Overrides) — ein bis ins Desktop-Layout überlebendes `translate` würde das Element zum Containing-Block für `position: fixed` machen.

Inhalte in Markdown, gegliedert nach `getting-started/`, `concepts/`, `designer/`, `api/`, `security/`, `enterprise/`, `configuration/`, `deployment/` plus Top-Level-Referenzseiten (`activities-reference`, `triggers`, `cli`, `ai-features`, `observability`, `import-export`).

Inhaltliche Quelle: `CLAUDE.md` + `docs/` im Repo-Root.

## Schreibstil der Inhalte

Die Seiten unter `content/` sind technische Dokumentation:

- keine direkte Anrede mit „du“, „Sie“ oder besitzanzeigenden Anredeformen;
- neutrale Handlungsformen wie „Öffnen“, „Ausführen“, „Eintragen“ und „Prüfen“;
- Zweck, Ergebnis und Voraussetzungen vor einer Schrittfolge;
- Begriffe beim ersten Auftreten erklären;
- kurze Sätze und jeweils eine technische Aussage pro Absatz;
- Befehle immer mit Ausführungsort und erwartbarem Ergebnis dokumentieren;
- Einschränkungen und nicht unterstützte Betriebsformen ausdrücklich nennen;
- sicherheitsrelevante Beispielwerte als Beispiele kennzeichnen.

## Routing

Echte Adressen (`/docs/de/getting-started/introduction/`). Jede Adresse ist eine eigene Datei, die
`scripts/prerender-docs.mjs` nach dem Build schreibt — mit eigenem Titel, eigener Beschreibung,
kanonischer Adresse und `hreflang`-Paar, dazu `dist/sitemap.xml`. Weil jede Adresse existiert,
braucht kein Host eine Rewrite-Regel; im Produkt bedient `DocsSiteSetup.cs` sie als Endpunkte,
weil dort der SPA-Auffangpfad jede endungslose Adresse abfangen würde.

Denselben Bundle-Stand gibt es an zwei Präfixen (`/docs/` und `/NodePilot/docs/`), deshalb steht
in jeder Datei ein `np-docs-base`-Meta mit ihrer Tiefe; `src/lib/docsBase.ts` löst es **einmal
beim Laden** in den Basispfad des Routers auf. Später wäre falsch: der Router ändert die Adresse,
ohne ein neues Dokument zu laden.

Alte `#/…`-Adressen leitet `public/legacy-hash-redirect.js` weiter (erstes, klassisches Skript);
ohne Sprachsegment auf die Standardsprache. `Ctrl/Cmd+K` öffnet die Suche.
