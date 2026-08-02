# nodepilot-desktop — the Electron shell

The window around NodePilot's desktop installation. It is a **thin, hardened viewer**: it starts no
backend, holds no business logic, and has an empty `dependencies` block. Everything it renders is
the ordinary SPA served by the local API service.

Packaging, service identities, provisioning and the first-run token handoff are documented in
[`deploy/desktop/README.md`](../../deploy/desktop/README.md). This file covers working on the shell
itself.

## What it does

1. Reads `%ProgramData%\NodePilot\desktop.json` — the non-secret handoff the installer writes:
   which loopback origin to load, which certificate fingerprint to pin, which Windows service the
   "Restart backend" tray action may control. Every field is validated strictly (`src/config.ts`);
   anything unexpected throws rather than falling back to something permissive.
2. Pins that certificate by SHA-256 and contains navigation (`src/security.ts`). The system root CA
   store is **not** trusted for this origin, and the shell will not follow a link off it.
3. Polls `GET {origin}/healthz/ready` for up to 120 seconds behind a splash screen — the backend is
   a separate Windows service and may still be starting.
4. Loads the SPA, or the first-run setup window when the installer left an admin handoff behind.
5. Follows the SPA's skin: the window and tray icon change with the favicon the page reports
   (`page-favicon-updated`), resolved through `src/skins.ts`.

The origin is always **https on localhost or 127.0.0.1**. There is no configuration that points
this shell at a remote NodePilot server — a server installation is used through a browser.

## Working on it

```powershell
npm install
npm run icons     # generates assets/ — gitignored, and empty in a fresh clone
npm start         # builds and runs against the INSTALLED backend (reads desktop.json)
```

`npm start` needs a desktop installation present on the machine, because that is what writes
`desktop.json`. Without one the shell exits with `Configuration file not found`.

| Script | Purpose |
|---|---|
| `npm run build` | `tsc` + copy static assets into `dist/` |
| `npm run typecheck` | type-check only |
| `npm run test` / `test:run` | vitest — pure logic (config validation, cert pinning, navigation containment, skin resolution) |
| `npm run package` | `electron-forge package` → `out/NodePilot-win32-x64/` (what the installer build consumes) |
| `npm run make` | `electron-forge make` → a zip; **not** used by the installer |

`assets/` is generated from the tracked brand images by
[`scripts/generate-desktop-icons.ps1`](../../scripts/generate-desktop-icons.ps1). The generator is
built on GDI+ and is invoked through Windows PowerShell (5.1) both here and in the installer build.

## Conventions

- **No runtime dependencies.** `dependencies` is empty and stays that way; everything that ships is
  Electron itself. Anything else belongs in the SPA or the backend.
- **Electron is pinned exactly** (`"electron": "43.2.0"`), and `@types/node` tracks the Node version
  that Electron bundles rather than the newest release — typing against APIs the shipped runtime
  does not have is how this breaks silently.
- **Security-relevant code is unit-tested.** `config.ts`, `security.ts` and `skins.ts` have vitest
  coverage and run in their own CI job (`desktop`, on ubuntu — the tests are pure logic and need no
  Electron runtime).
- **No auto-update.** Updates ship through the signed all-in-one installer.
