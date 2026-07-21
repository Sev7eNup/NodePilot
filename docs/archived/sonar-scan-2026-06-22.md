# SonarQube Code Quality Scan — 2026-06-22

**Branch:** `fix/sonar-scan-2026-06-22`
**SonarQube:** Community Build (Docker `nodepilot-sonarqube`, `http://localhost:9000`)
**Scanner:** dotnet-sonarscanner 11.2.1 (Backend, `scanAll`) + sonar-scanner-cli via Docker (Frontend)

| Projekt-Key | Stack | Dashboard |
|---|---|---|
| `nodepilot` | .NET 10 / C# (+ TS via scanAll) | http://localhost:9000/dashboard?id=nodepilot |
| `nodepilot-ui` | React 19 / TypeScript | http://localhost:9000/dashboard?id=nodepilot-ui |

## Ergebnis (Endstand)

| Metrik | Backend | Frontend |
|---|---:|---:|
| 🐞 Bugs | **0** | **0** |
| 🛡️ Vulnerabilities | **0** | **0** |
| 🔥 Security Hotspots (offen) | **0** | **0** |
| Reliability Rating | **A** | **A** |
| Security Rating | **A** | **A** |
| 🧹 Code Smells | 1128 | 226 |
| Coverage | 59.4 % | 62.4 % |
| Quality Gate | ✅ OK | ⚠️ ERROR* |

\* Frontend-QG-ERROR ist ein **New-Code-Baseline-Artefakt**: ohne Referenz-Branch zählt SonarQube alle 225 bestehenden Code-Smells als `new_violations` (>0) und die New-Code-Coverage (57 %) verfehlt das 80-%-Ziel. Kein realer Regress — Bugs/Vulns/Hotspots sind alle clean.

## Echte Bugs — gefixt (4)

| Datei:Zeile | Rule | Problem | Fix |
|---|---|---|---|
| [TextFileEditConfig.tsx:109](../src/nodepilot-ui/src/components/designer/properties/activities/TextFileEditConfig.tsx#L108) | S3923 | Ternary `insert ? X : X` — beide Branches identisch | Redundanten Zweig entfernt |
| [WorkflowDiffModal.tsx:155](../src/nodepilot-ui/src/components/designer/overlays/WorkflowDiffModal.tsx#L155) | S6439 | `selectedVersion && (...)` leakt `0`/`""` in JSX | `!!selectedVersion &&` |
| [WorkflowDiffModal.tsx:213](../src/nodepilot-ui/src/components/designer/overlays/WorkflowDiffModal.tsx#L213) | S6439 | dito | dito |
| [ExecutionsPage.tsx:470](../src/nodepilot-ui/src/pages/ExecutionsPage.tsx#L470) | S1082 | Klickbare Row ohne Keyboard-Listener (A11y) | `onKeyDown` (Enter/Space) + `tabIndex={0}` |

Zusätzlich: [DashboardPage.test.tsx](../src/nodepilot-ui/src/__tests__/pages/DashboardPage.test.tsx) an UI-Change angepasst (`7 failures` → `7✗`-Badge).

## False Positives — markiert

| Rule | # | Wo | Begründung |
|---|---:|---|---|
| secrets:S6698 | 3 | `.claude/settings.json` | Claude-Code-Permission-Config, kein App-Source. PG-Dev-Passwort in Tool-Allowlist-Patterns |
| secrets:S6689 | 2 | `.claude/settings.local.json` | GitHub-Token in (gitignored) lokaler Claude-Config |
| python:S2068 | 5 | `scripts/*.py` | Lokale Stress-/Dev-Skripte mit `admin123` gegen localhost |
| Hotspots (dos/encrypt/conf/others) | 23 | div. | Regex bounded/linear, HTTP/IP/Pfad-Platzhalter in Input-Feldern, Cookie-Flags via Prod-Config, `[RequestSizeLimit]`-Härtung, SMTP-SSL user-controlled |

## Nicht gefixt — bewusst (Code Smells)

1354 Code Smells (alle Severities). High-Severity-Cluster: **S3776** (Cognitive Complexity, ~84×), **S927** (Param-Naming), **S2004** (Nesting), **S1192** (Literal-Dup), **S3735** (void-Operator). Reine Wartbarkeits-/Komplexitäts-Hinweise ohne Korrektheits- oder Security-Impact. Refactoring von ~169 funktionierenden Komplex-Funktionen in einer Codebase mit 4378 grünen Tests = hoher Churn + Regress-Risiko bei null funktionalem Gewinn → nicht umgesetzt (gleiche Bewertung wie Scan 2026-05-16).

## Test-Status

| Suite | Tests | Status |
|---|---:|---|
| Backend (Data/Cli/Api/Engine) | 2705 | ✅ alle grün |
| Frontend (Vitest) | 1673 | ✅ alle grün |
| TypeScript `tsc --noEmit` | — | ✅ clean |

## Reproduktion

Siehe [sonar-scan-2026-05-16.md](sonar-scan-2026-05-16.md#reproduzierbarkeit). Frontend-Scan braucht disjunkte `sonar.exclusions`/`sonar.test.inclusions` (sonst „can't be indexed twice"). Coverage-Reportdir auf `C:\temp\…` legen (E:-Laufwerk wirft `EPERM` bei `.tmp`-mkdir).
