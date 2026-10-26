# NodePilot Docs UI

Documentation website for NodePilot — a React 19 SPA (Vite + Tailwind CSS 4) that renders Markdown content from `content/` and mirrors the look of the NodePilot app shell: the same sidebar (292px rail with brand header, search field, section titles and icon pills), the same slim TopBar and the same design tokens (light = blue, dark = the default skin **"Azur"**, cool graphite + azure accent).

**Design tokens and sidebar CSS are copied from `src/nodepilot-ui/src/index.css`**, not imported (separate Vite roots, separate `npm ci` in CI). Two mechanical rewrites are applied when copying so that a re-sync stays a trivial diff — both are documented in the header of the copied block in `src/index.css`:

1. `[data-skin="dark"]` is dropped (the docs have only one dark skin, no 10-skin system).
2. A bare `aside` in selectors becomes `.np-sidebar` (otherwise the TOC rail in `Toc.tsx` inherits the rail gradient).

The same package also contains the project website (`src/site/`), see [Project website](#project-website).

## Development

```powershell
cd src\nodepilot-docs-ui
npm install
npm run dev      # http://localhost:5174/docs/
```

## Build

```powershell
npm run build    # static output in dist/
npm run preview  # preview the build locally
```

## Project website

The project website lives in `src/site/`: vanilla TypeScript without React and Tailwind, bilingual
(DE/EN), with its own Vite build (`vite.site.config.ts`, output `dist-site/`). It is never part of
`dist/` and therefore never part of the server artifact or the desktop package.

```powershell
npm run dev:site       # website only, http://localhost:5175
npm run build:site     # website build into dist-site/
npm run build:demo     # build the browser demo in the neighbouring package (src/nodepilot-ui -> dist-demo/)
npm run assemble:site  # assemble _site/ from dist-site/, dist/, dist-demo/ and pages-media/ (run the builds first)
npm run preview:site   # build + build:site + build:demo + assemble:site, then serve _site/ on http://localhost:5175
npm run build:pages-redirects  # redirect pages for GitHub Pages into _pages-redirects/
npm run test:site:e2e  # Playwright suite e2e-site/ against a freshly built website and docs
```

`preview:site` is the only complete local preview: `docs/`, `demo/` and `media/` exist
only in the assembled `_site/`. Every one of these inputs is mandatory — an optional one would let a
deploy silently keep publishing the previous version without anything turning red. `deploy/Publish-Site.ps1` publishes this directory to the web space.
GitHub Pages receives only `_pages-redirects/` from `npm run build:pages-redirects`.
The Pages workflow builds neither the website nor the demo and does not need their assets.

| In `_site/` | Source | Address |
|---|---|---|
| Root | `dist-site/` | https://www.nodepilot.run/ |
| `docs/` | `dist/` | https://www.nodepilot.run/docs/ |
| `demo/` | `../nodepilot-ui/dist-demo/` | https://www.nodepilot.run/demo/ |
| `media/` | `pages-media/` | https://www.nodepilot.run/media/nodepilot-product-tour.mp4 |
| `og-image.png` | `public/og-image.png` | Preview image of the website |

**Media section** (`/tutorials/`, "NodePilot in 2 Minuten"): The address is kept for
existing search results and direct links; `/media/` serves the video files.
`npm run site:videos` reads the
rendered episodes from `out/nodepilot-training/` and does three things:

- It copies the MP4s to `pages-media/training/`. The folder is gitignored.
- It generates the posters under `src/site/public/training/`.
- It writes `src/site/videos.json`.

The file names carry a content hash. This way a newly rendered video gets a new address.
`publishedAt` and `youtube` are maintained by hand in `videos.json`; a new run keeps these
values. `deploy/Publish-Site.ps1` uploads only videos that are still missing on the server. This
happens before all other files. If one is still missing afterwards, the deploy aborts before a page
references it.

Deep links into the docs have the form `https://www.nodepilot.run/docs/<language>/<page>/`.

- **`NP_SITE_ORIGIN`:** The default is `https://www.nodepilot.run`; canonical, Open Graph,
  `hreflang`, sitemaps and the demo link use this origin. The variable overrides
  it for local previews or other web space targets. `deploy/Publish-Site.ps1` sets it
  from its configuration. The demo uses fixed paths under `/demo/`.
- **`np-site-root`:** When copying to `_site/docs/`, `assemble-site.mjs` stamps a
  `<meta name="np-site-root" content="../">` into `index.html` — and fails if the
  `</head>` anchor is missing. The same `dist/` is shipped a second time, as
  `wwwroot/docs` in the product, where neither the website nor the demo sit next to it. `src/lib/siteContext.ts`
  reads the meta, and only if it is present does the sidebar show the links back to `../` and `../demo/`.
  A `<meta>` and not an inline `<script>`, because the docs run under `script-src 'self'`.

- **Routes are real addresses.** `/product/`, `/blog/scorch-import/` and so on. The
  segments are English because one address serves both language versions; `impressum` and
  `datenschutz` stay German because these pages exist only in German. After the
  Vite build, the plugin `np-site-prerender` (`vite.site.config.ts`) writes a separate file per route from the one
  built shell, plus `404.html`, `sitemap.xml`, `robots.txt` and `llms.txt`.
  The rules for this are pure functions in `src/site/prerender.ts`. Each file gets
  its own title, its own description and its own `canonical` URL and contains only its own
  page section (`keepPage`); page and language switches are normal page loads.
  `npm run dev:site` serves every route address with the same shell, tailored to the route
  (plugin `siteDevRoutes`); article texts exist only in the build.

  Because the website may also live in a subdirectory, every internal URL in the source is written
  relative to the root. The prerender raises it per depth and sets
  `<meta name="np-site-base">` to the same prefix; `main.ts` reads the meta to turn the address
  into a route, and `setBasePrefix()` passes it on to the docs links. **Whoever adds a new
  route registers it in `ROUTE_PATHS` or `ARTICLE_SLUGS` and in `routePages()`** —
  otherwise no file is created and the address ends up on the 404 page.

- **Old docs and website links:** The docs used to live at the Pages root
  (`…/NodePilot/#/en/deployment/logs`), and the website itself used hash routes (`#/produkt`).
  `src/site/public/legacy-docs-redirect.js` runs as the first classic script in the `<head>` and
  redirects both via `location.replace`: a hash whose first segment is not a
  website route goes to `docs/`, every other one to the real address — in doing so it renames the
  former German segments (`produkt` → `product`, `erleben` → `walkthrough`,
  `warum-nodepilot` → `why-nodepilot`). The same renames are in
  `src/site/public/.htaccess` as 301s, for requests without a hash. Website routes are
  `SITE_ROUTE_SEGMENTS` from `src/site/router.ts` (`walkthrough`, `product`, `blog`, `impressum`,
  `datenschutz`); the same list appears verbatim in the redirect script. A new route belongs in
  both lists and must not collide with any docs path or any language.
- **Light/dark:** The website has a toggle in the header next to the language switch. `:root` carries the
  light tokens (those of the app), `html.dark` the dark ones; `src/site/public/theme-init.js` sets the
  class before the first paint from `np-docs-theme` (otherwise the OS setting), the same key as in the
  docs. Add new colors as a token in **both** blocks (`theme-tokens.test.mjs`).
- **Texts and language:** Website texts live in `src/site/i18n/de.ts` and `en.ts`.
  Blog texts are kept separately in `content/blog/`; `src/site/blog-catalog.json` contains metadata,
  linking and publication status. They are shipped as complete HTML and
  do not belong in the shared JavaScript. German URLs stay in place, English versions
  live under `/en/`, each with its own canonical and mutual `hreflang`.
  Entry points without a language segment use the deliberately stored choice (`LANG_STORAGE_KEY`),
  which website and docs share; only the language switches store a preference. Without a stored
  choice, the website stays on the German address: a redirect by browser language
  would also hit search engine crawlers, which would then see German pages as English ones. Docs addresses
  without a language segment take the first supported browser language
  (DE/EN) when there is no stored choice, otherwise English. Explicit language segments
  (`/en/` on the website, `/de/` and `/en/` in the docs) remain authoritative and do not override
  the stored choice. Impressum and Datenschutz (imprint and privacy policy) stay exclusively German.
  `NP_BLOG_PREVIEW=1` includes drafts only for local review (`noindex`, no sitemap entry).
  Remove the variable or set it to `0` before publishing. For approved articles, set
  `status: published` and the actual `publishedAt`; set `modifiedAt` only for a
  real update. `npm run images:site` generates responsive WebP variants automatically
  before the website build. Details: [SEO implementation](../../docs/seo-local-review.md).
- **Two graphics on the home page**, both as inline SVG without an image file: the workflow example
  in the hero (`src/site/graph.ts`) and the architecture diagram below it (`src/site/topology.ts`). The
  geometry lives in these two files as pure functions, `main.ts` writes the coordinates
  into the markup and chooses the wide or the stacked layout depending on the width. Both animations
  run only while the graphic is in view, and the global
  `prefers-reduced-motion` rule in `site.css` turns them off — the graphics must therefore remain
  fully readable without motion.
- **No third-party requests:** Fonts (`@fontsource/ibm-plex-sans`, `@fontsource/ibm-plex-mono`),
  app icon and screenshots from `docs/images/` are built in, the video lives under `media/`.
  Google Fonts and `raw.githubusercontent.com` are off limits; the privacy policy relies
  on this.
- **Legal texts:** Impressum (`/impressum/`) and Datenschutz (`/datenschutz/`) render
  `src/site/legal/impressum.de.html` and `src/site/legal/datenschutz.de.html`. The texts are supplied by
  the project owner. They exist only in German and also appear in German in the English view. If a file is missing or empty, the test fails.
- **Own domain:** The site runs at <https://www.nodepilot.run/>, delivered by
  `deploy/Publish-Site.ps1` to its own web space. GitHub Pages redirects old links to the
  corresponding address there, including historical hash links and docs deep links.
  Known paths get HTML pages with canonical, JavaScript redirect and meta refresh
  without JavaScript; `404.html` handles the remaining paths. These are browser redirects,
  not HTTP 301. The target origin is fixed to `https://www.nodepilot.run`; query and anchor are preserved.
- **Demo:** Browser routing under `/demo/`, Apache fallback only for page paths; missing assets
  stay 404. Old `#/…` links are rewritten before the app starts. The demo stays `noindex`.
- **Publishing the switch:** First upload the complete web space build and check demo deep links
  including reload, then publish the Pages workflow with the redirect artifact.
  GitHub Pages setting and DNS stay unchanged.

## Website E2E tests

`e2e-site/` tests website and docs in the browser, each against a fresh build. Invoked with
`npm run test:site:e2e`, config `playwright.site.config.ts`. It builds redirects, website and
docs and serves website (port 5176) and docs (port 5187) separately via `vite preview`.

- `docs-routing.spec.ts`: docs deep links as separate pages with reachable assets, own
  description and translation per page, address and title while navigating, old hash addresses.
- `experience.spec.ts`: guided introduction with both tasks in the current language and at
  narrow widths, links stay correct after page changes.
- `language-preference.spec.ts`: website addresses keep their language, the docs take the
  browser language as default, only the language switches store a choice, German-language
  legal pages stay reachable.
- `pages-redirects.spec.ts`: old GitHub Pages links land on the matching canonical address,
  docs pages also without JavaScript via meta refresh.
- `seo.spec.ts`: static page content, canonical and language links without JavaScript,
  article navigation, responsive images (light and dark), the light/dark toggle and the
  training video dialogs.

## Guided product tour

The website route `/walkthrough/` offers ten tasks directly in the browser demo:
workflow building, error analysis and file provisioning come first. They are followed by
decision, parallel work, service recovery, Live Ops, version comparison,
machine check and maintenance window.
Each card opens `demo/?tour=<id>&lang=de` or `lang=en`. The overview graphic stays
on the home page.

The tour in `../nodepilot-ui/demo/ui/tour.ts` accompanies the real start dialog and the
execution history. The eight further tasks live in `../nodepilot-ui/demo/ui/additionalTours.ts`.
All changes and executions stay in the browser tab; a reload restores the seed data.
The file workflow comes from `scripts/example-guided-file-workflow.json`; registry, service and
file system are simulated. `Protected` simulates missing write permissions when copying.

`npm run test:site:e2e` checks the introduction, language switching and three screen sizes.
The tour itself is checked with `npm --prefix ../nodepilot-ui run test:e2e:demo`.
Before the first run, install Chromium with `npx playwright install chromium`.

## Structure

Marketing media live in `pages-media/`. The normal build for server and desktop does not contain
them. Only `scripts/assemble-site.mjs` copies them to
`_site/media/` when assembling `_site/` (see [Project website](#project-website)). The video address
`…/NodePilot/media/nodepilot-product-tour.mp4` and with it the README video link therefore stay
stable. `.gitattributes` excludes `pages-media/` via `export-ignore` from the `git archive` snapshot
for `knowledge/source`.

- `src/data/nav.ts` — page tree, grouping, sidebar icon per page, prev/next logic, `groupOf()` for the breadcrumb. The `icon` field is **required**: `tsc -b` fails as soon as a new page is registered without an icon.
- `src/lib/content.ts` — loads only `content/de/**/*.md` and `content/en/**/*.md` as raw strings; blog sources stay outside the docs/installer bundle
- `src/lib/useTheme.ts` — light/dark toggle (LocalStorage). The initial resolution happens in the external file `public/theme-init.js`, which `index.html` loads as a classic script without `defer`/`async` **before** the first paint (no theme flash, compatible with `script-src 'self'`); the hook seeds from the set `html.dark` class.
- `src/components/` — `TopBar`, `Sidebar`, `DocPage`, `Toc`, `SearchModal`
- `src/index.css` — Tailwind + design tokens (Material 3 tonal palette / Azur) + ported `.np-sidebar`, `.np-nav` and `.np-card` blocks + `.np-prose`
- `index.html` — SPA root (`#root`) + pre-hydration theme script

Icons: `@carbon/icons-react` — the same library as the main UI, and where a docs page maps to an app page, the same glyph is chosen as well.

The sidebar is a single `<aside>` for both layouts: from `lg` upward a sticky 292px rail, below that an off-canvas drawer. The mobile branch is deliberately written with `max-lg:` utilities (not with `lg:` overrides) — a `translate` that survives into the desktop layout would turn the element into the containing block for `position: fixed`.

Content in Markdown, organised into `getting-started/`, `concepts/`, `designer/`, `api/`, `security/`, `enterprise/`, `configuration/`, `deployment/` plus top-level reference pages (`activities-reference`, `triggers`, `cli`, `ai-features`, `observability`, `import-export`).

Content source: `CLAUDE.md` + `docs/` in the repo root.

**Delivery in the product:** `deploy/Build-Artifact.ps1` and `deploy/desktop/Build-DesktopInstaller.ps1` build the docs along with everything else and place them in `wwwroot/docs`; the API serves them under `/docs` so that an installation without internet has the runbooks. That is why the Vite `base` stays relative. `-SkipFrontend` skips both npm builds, `-SkipNpmCi` applies to both.

## Writing style of the content

The pages under `content/` are technical documentation:

- no direct address with "du", "Sie" or possessive forms of address;
- neutral action forms such as "Öffnen" (open), "Ausführen" (run), "Eintragen" (enter) and "Prüfen" (check);
- purpose, result and prerequisites before a sequence of steps;
- explain terms at their first occurrence;
- short sentences and one technical statement per paragraph;
- always document commands with the place of execution and the expected result;
- state restrictions and unsupported operating modes explicitly;
- mark security-relevant example values as examples.

## Routing

Real addresses (`/docs/de/getting-started/introduction/`). Each address is a separate file that
`scripts/prerender-docs.mjs` writes after the build — with its own title, its own description,
canonical address and `hreflang` pair, plus `dist/sitemap.xml`. Because every address exists,
no host needs a rewrite rule; in the product, `DocsSiteSetup.cs` serves them as endpoints,
because there the SPA catch-all would otherwise intercept every address without a file extension.

The same bundle build is available under two prefixes (`/docs/` and `/NodePilot/docs/`), so
every file contains an `np-docs-base` meta with its depth; `src/lib/docsBase.ts` resolves it **once
at load time** into the router's base path. Later would be wrong: the router changes the address
without loading a new document.

Old `#/…` addresses are redirected by `public/legacy-hash-redirect.js` (first, classic script);
without a language segment to the default language. `Ctrl/Cmd+K` opens the search.
