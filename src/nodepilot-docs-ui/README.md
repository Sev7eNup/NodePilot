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
Deploy die vorige Fassung stillschweigend weiterveröffentlichen, ohne dass etwas rot wird. Genau dieses Verzeichnis veröffentlicht
`.github/workflows/docs-pages.yml` auf GitHub Pages; der Workflow ruft dafür dasselbe Skript
`scripts/assemble-site.mjs` auf.

| In `_site/` | Quelle | Adresse |
|---|---|---|
| Wurzel | `dist-site/` | https://sev7enup.github.io/NodePilot/ |
| `docs/` | `dist/` | https://sev7enup.github.io/NodePilot/docs/ |
| `demo/` | `../nodepilot-ui/dist-demo/` | https://sev7enup.github.io/NodePilot/demo/ |
| `media/` | `pages-media/` | https://sev7enup.github.io/NodePilot/media/nodepilot-product-tour.mp4 |
| `og-image.png` | `public/og-image.png` | Vorschaubild der Website |

Deep Links in die Doku haben die Form `https://sev7enup.github.io/NodePilot/docs/#/<sprache>/<seite>`.

- **`NP_SITE_ORIGIN`:** Fast alles ist ortsunabhängig (`base: './'`, Hash-Routen). Nur wenige URLs
  können nicht relativ sein: `canonical`, `og:url` und `og:image` im Website-Head sowie der
  Doku-Link auf die Demo. Die stehen im Quelltext auf der Pages-Adresse; ist beim Build
  `NP_SITE_ORIGIN` gesetzt, schreiben beide Vite-Configs sie auf diesen Ursprung um
  (`scripts/site-origin.mjs`, dieselbe Konstante für Website-Head und `lib/content.ts`). Ohne die
  Variable ist die Umschreibung ein No-op. `deploy/Publish-Site.ps1` setzt sie aus seiner
  Konfiguration, wenn die Seite auf eigenem Webspace statt auf Pages liegt.
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
  eigenen Titel, eigene Beschreibung und eigene `canonical`-URL — vorher lieferte jede Adresse
  dieselbe Hülle, ein Crawler sah also eine einzige Seite.

  Weil die Website auch in einem Unterverzeichnis liegen kann (GitHub Pages), ist in der Quelle
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
- **Texte und Sprache:** Die Texte der Website, auch die Blogbeiträge, stehen in
  `src/site/i18n/de.ts` und `en.ts`. Website und Doku teilen sich die Sprachwahl über
  `LANG_STORAGE_KEY` aus `src/i18n/languages.ts`, weil beide auf derselben Origin liegen.
- **Keine Drittanfragen:** Schriften (`@fontsource/ibm-plex-sans`, `@fontsource/ibm-plex-mono`),
  App-Icon und Screenshots aus `docs/images/` werden mitgebaut, das Video liegt unter `media/`.
  Google Fonts und `raw.githubusercontent.com` sind tabu; die Datenschutzerklärung verlässt sich
  darauf.
- **Rechtstexte:** Impressum (`#/impressum`) und Datenschutz (`#/datenschutz`) rendern
  `src/site/legal/impressum.de.html` und `src/site/legal/datenschutz.de.html`. Die Texte liefert
  der Projektinhaber. Sie liegen nur auf Deutsch vor und erscheinen auch in der englischen Ansicht
  auf Deutsch. Fehlt eine Datei oder ist sie leer, schlägt der Test fehl.
- **Eigene Domain (später):** Die Website ist domain-unabhängig gebaut (`base: './'`, relative
  Links). Die Domain wird im Konto verifiziert und dann im Repository unter *Settings → Pages →
  Custom domain* eingetragen; eine `CNAME`-Datei braucht es beim Deploy per Actions nicht. Danach
  die absoluten `sev7enup.github.io/NodePilot`-Adressen in README und Doku sowie die OG- und
  Canonical-Tags beider `index.html` nachziehen.

## Geführter Produkteinstieg

Die Website-Route `#/erleben` bietet zwei Aufgaben direkt in der Browserdemo an:
eine Konfigurationsdatei bereitstellen und einen fehlgeschlagenen Kopiervorgang untersuchen.
Die Links `demo/?tour=file&lang=de` und `demo/?tour=diagnose&lang=de` öffnen die Führung;
`lang=en` verwendet Englisch. Die Übersichtsgrafik bleibt auf der Startseite.

Die Führung in `../nodepilot-ui/demo/ui/tour.ts` begleitet den echten Startdialog und die
Ausführungshistorie. Der Beispiel-Workflow kommt aus `scripts/example-guided-file-workflow.json`;
Registry, Dienst und Dateisystem sind simuliert. Eingaben werden im Lauf gespeichert und bestimmen
die erzeugten Inhalte und Ausgaben. `Protected` simuliert fehlende Schreibrechte beim Kopieren.

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
- `src/lib/content.ts` — lädt via `import.meta.glob` alle `content/**/*.md` als Raw-Strings
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

HashRouter (`#/getting-started/introduction`) — funktioniert ohne serverseitige Rewrites auf jedem Host (auch Subpfad, da `base: './'`). Vola `Ctrl/Cmd+K` öffnet die Suche.
