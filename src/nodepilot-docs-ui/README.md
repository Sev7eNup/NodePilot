# NodePilot Docs UI

Dokumentations-Website für NodePilot — eine React 19 SPA (Vite + Tailwind CSS 4), die Markdown-Inhalte aus `content/` rendert und die Optik des NodePilot-App-Shell spiegelt: dieselbe Sidebar (292px-Rail mit Brand-Header, Suchfeld, Section-Titles und Icon-Pills), dieselbe schlanke TopBar und dieselben Design-Tokens (Light = Blau, Dark = das Default-Skin **„Azur"**, kühles Graphit + Azur-Akzent).

**Design-Tokens und Sidebar-CSS sind aus `src/nodepilot-ui/src/index.css` kopiert**, nicht importiert (getrennte Vite-Roots, getrenntes `npm ci` in CI). Beim Kopieren werden zwei mechanische Rewrites angewendet, damit ein Re-Sync ein trivialer Diff bleibt — beide sind im Kopf des kopierten Blocks in `src/index.css` dokumentiert:

1. `[data-skin="dark"]` entfällt (die Docs haben nur ein Dark-Skin, kein 7-Skin-System).
2. Blankes `aside` in Selektoren wird zu `.np-sidebar` (sonst erbt die TOC-Rail in `Toc.tsx` den Rail-Gradient).

## Entwickeln

```powershell
cd src\nodepilot-docs-ui
npm install
npm run dev      # http://localhost:5174
```

## Build

```powershell
npm run build    # statischer Output in dist/
npm run preview  # Build lokal vorschauen
```

## Struktur

- `src/data/nav.ts` — Seitenbaum, Gruppierung, Sidebar-Icon je Seite, Prev/Next-Logik, `groupOf()` für den Breadcrumb. Das `icon`-Feld ist **required**: `tsc -b` schlägt fehl, sobald eine neue Seite ohne Icon eingetragen wird.
- `src/lib/content.ts` — lädt via `import.meta.glob` alle `content/**/*.md` als Raw-Strings
- `src/lib/useTheme.ts` — Light/Dark-Toggle (LocalStorage). Die Erstauflösung passiert im Inline-Script in `index.html` **vor** dem ersten Paint (kein Theme-Flash); der Hook seedet aus der gesetzten `html.dark`-Klasse.
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
