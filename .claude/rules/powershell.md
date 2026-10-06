---
paths:
  - "**/*.ps1"
  - "**/*.psm1"
---

# PowerShell

Gilt zusätzlich zur Root-`CLAUDE.md` (5.1-Ziel, keine Inline-SQL, kein `$args`-Splatting).

- Ziel ist Windows PowerShell 5.1. Keine 7.x-Syntax (`??`, `?.`, Ternary, `&&`/`||`).
- Skriptkopf wie in `deploy/`: `[CmdletBinding()]`, `$ErrorActionPreference = 'Stop'`, `Set-StrictMode -Version 3.0`.
- Approved Verbs, volle Cmdlet- und Parameternamen, keine Aliase.
- Credentials als `PSCredential` oder über den Secret-Provider, nie im Klartext.
- Installer-Skripte unter `deploy/desktop/` und `deploy/server/` bleiben ASCII-only: PS 5.1 liest UTF-8 ohne BOM als ANSI.
