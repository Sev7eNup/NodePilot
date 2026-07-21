# NodePilot Docs UI

Dokumentations-Website für NodePilot — eine React 19 SPA (Vite + Tailwind CSS 4), die Markdown-Inhalte aus `content/` rendert und das visuelle Theming des NodePilot-App-Shell spiegelt (Light = Blau, Dark = Warm-Charcoal + Orange-Akzent).

## Entwickeln

```powershell
cd src\nodepilot-docs-ui
npm install
npm run dev      # http://localhost:5173
```

## Build

```powershell
npm run build    # statischer Output in dist/
npm run preview  # Build lokal vorschauen
```

## Struktur

- `src/data/nav.ts` — Seitenbaum, Gruppierung, Prev/Next-Logik, Top-Nav
- `src/lib/content.ts` — lädt via `import.meta.glob` alle `content/**/*.md` als Raw-Strings
- `src/lib/useTheme.ts` — Light/Dark-Toggle (LocalStorage, `prefers-color-scheme`)
- `src/lib/icons.tsx` — Inline-SVG-Iconset (keine Icon-Dependency)
- `src/components/` — `TopBar`, `Sidebar`, `DocPage`, `Toc`, `SearchModal`
- `src/index.css` — Tailwind + Design-Tokens (Material-3-Tonal-Palette) + `.np-prose`
- `index.html` — SPA-Root (`#root`)

Inhalte in Markdown, gegliedert nach `getting-started/`, `concepts/`, `designer/`, `api/`, `security/`, `enterprise/`, `configuration/`, `deployment/` plus Top-Level-Referenzseiten (`activities-reference`, `triggers`, `cli`, `ai-features`, `observability`, `import-export`).

Inhaltliche Quelle: `CLAUDE.md` + `docs/` im Repo-Root.

## Routing

HashRouter (`#/getting-started/introduction`) — funktioniert ohne serverseitige Rewrites auf jedem Host (auch Subpfad, da `base: './'`). Vola `Ctrl/Cmd+K` öffnet die Suche.