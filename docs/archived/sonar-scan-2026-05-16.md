# SonarQube Code Quality Scan — 2026-05-16

**Branch:** `fix/dark-mode-simulation-panel`
**SonarQube Version:** Community Build 26.5.0.122743 (Docker `sonarqube:community`)
**Scanner:** dotnet-sonarscanner 11.2.1 (Backend) + sonar-scanner-cli (Frontend)
**Dashboard:** http://localhost:9000

Der Scan läuft als zwei separate Sonar-Projekte (die JS/TS-Bridge des .NET-Scanners erfasst die TypeScript-Files nicht selbständig):

| Projekt-Key | Stack | Dashboard |
|---|---|---|
| `nodepilot` | .NET 10 / C# (Backend) | http://localhost:9000/dashboard?id=nodepilot |
| `nodepilot-ui` | React 19 / TypeScript (Frontend) | http://localhost:9000/dashboard?id=nodepilot-ui |

Dieser Report dokumentiert **drei Scan-Iterationen**:
1. **V1 (12:02)** — Erst-Scan, Baseline
2. **V2 (12:48)** — Nach Code-Fixes der echten Bugs + Regex-Hardening + A11y
3. **V3 (12:54)** — Nach CSS-Bug-Fix + False-Positive-Cleanup via Sonar-API

## Headline-Vergleich V1 → V3

### Backend (`nodepilot`)

| Metrik | V1 | V3 | Δ |
|---|---:|---:|---:|
| **Quality Gate** | ✅ OK | ✅ OK | — |
| 🐞 **Bugs** | 2 | **0** | −2 ✓ |
| 🛡️ **Vulnerabilities** | 10 | **0** | −10 ✓ |
| 🔥 **Security Hotspots** | 28 | 8 | −20 ✓ |
| 🧹 **Code Smells** | 859 | 815 | −44 ✓ |
| **Reliability Rating** | C | **A** | ✓ |
| **Security Rating** | C | **A** | ✓ |
| **Maintainability Rating** | A | A | — |
| Coverage | 74.3 % | 74.3 % | — |
| Sqale-Index | 4 082 min | 3 817 min | −265 min (~4.4 h) |
| Duplication | 2.0 % | 2.0 % | — |
| LOC | 34 030 | 34 034 | +4 |

### Frontend (`nodepilot-ui`)

| Metrik | V1 | V3 | Δ |
|---|---:|---:|---:|
| **Quality Gate** | ✅ OK | ✅ OK | — |
| 🐞 **Bugs** | 42 | **0** | −42 ✓ |
| 🛡️ **Vulnerabilities** | 0 | 0 | — |
| 🔥 **Security Hotspots** | 8 | 8 | — |
| 🧹 **Code Smells** | 909 | 924 | +15 (eingeführte JSX-Props) |
| **Reliability Rating** | D | **A** | ✓ |
| **Security Rating** | A | A | — |
| **Maintainability Rating** | A | A | — |
| Coverage | 63.8 % | 63.4 % | −0.4 |
| Sqale-Index | 4 517 min | 4 607 min | +90 min |
| Duplication | 3.7 % | 3.7 % | — |
| LOC | 30 539 | 30 920 | +381 (A11y-Attribute) |

## Was wurde gefixt

### Echte Bugs

| # | Datei:Zeile | Rule | Was war kaputt | Fix |
|---|---|---|---|---|
| 1 | [ScorchActivityMapper.cs:91](src/NodePilot.Api/Services/Scorch/ScorchActivityMapper.cs#L91) | S3923 | Ternary returned `"auto" : "auto"` — beide Branches identisch | `"powershell" : "auto"` — SCOrch-Imports mit `ScriptType=PowerShell` landen explizit auf Windows PowerShell statt auf Auto-Heuristik |
| 2 | [ScheduleTriggerSource.cs:125](src/NodePilot.Scheduler/Sources/ScheduleTriggerSource.cs#L125) | S3887 | `public static readonly` mutable `ConcurrentDictionary` exponiert State | Encapsulation: `private static readonly _callbacks` + `Register()`/`Unregister()`-Methoden. Tests entsprechend umgestellt |
| 3 | [variableUsageScan.ts:187](src/nodepilot-ui/src/lib/variableUsageScan.ts#L187) | S2871 | `.sort()` ohne Compare-Function | `.sort((a, b) => a.localeCompare(b))` |
| 4 | [useNodeAnnotations.ts:201](src/nodepilot-ui/src/hooks/useNodeAnnotations.ts#L201) | S2871 | dito | dito |
| 5 | [useSignalR.ts:219](src/nodepilot-ui/src/hooks/useSignalR.ts#L219) | S2871 | dito | dito |
| 6 | [index.css:13](src/nodepilot-ui/src/index.css#L13) | css:S4649 | `font-family` ohne generic Fallback | `'Material Symbols Outlined Variable', sans-serif` |

### Security Hardening

**Regex-Timeouts (S6444) — 18 Call-Sites** über folgende Files. Pattern: jeder Regex bekommt einen 1-Sekunden-Timeout um DoS via Backtracking zu verhindern:

- [AuthController.cs:161](src/NodePilot.Api/Controllers/AuthController.cs#L161), [DbAdminController.cs:308](src/NodePilot.Api/Controllers/DbAdminController.cs#L308), [GlobalVariablesController.cs:34](src/NodePilot.Api/Controllers/GlobalVariablesController.cs#L34), [SharedFolderPermissionsController.cs:121](src/NodePilot.Api/Controllers/SharedFolderPermissionsController.cs#L121), [WorkflowImportExportController.cs:472](src/NodePilot.Api/Controllers/WorkflowImportExportController.cs#L472)
- [DbContextSetup.cs:123,125](src/NodePilot.Api/Hosting/DbContextSetup.cs#L123)
- [WorkflowScriptLinter.cs:24,31,35](src/NodePilot.Api/Security/WorkflowScriptLinter.cs#L24) (zentrale `RegexTimeout = TimeSpan.FromSeconds(1)` extrahiert)
- [ScorchImporter.cs:361,363,467](src/NodePilot.Api/Services/Scorch/ScorchImporter.cs#L361)
- [WmiQueryActivity.cs:33](src/NodePilot.Engine/Activities/WmiQueryActivity.cs#L33), [ConditionEvaluator.cs:97](src/NodePilot.Engine/Conditions/ConditionEvaluator.cs#L97), [VariableResolver.cs:15,16](src/NodePilot.Engine/Execution/VariableResolver.cs#L15), [ParameterKeyValidator.cs:12](src/NodePilot.Engine/PowerShell/ParameterKeyValidator.cs#L12)

### Accessibility (S1082) — 38 Call-Sites über 21 Files

Modal-Backdrops, Stop-Propagation-Wrapper und Click-bare List-Items haben jetzt:
- Backdrops: `onKeyDown` für ESC + `role="presentation"` + `tabIndex={-1}`
- Wrapper: `onKeyDown` für `stopPropagation` + `role="presentation"`
- Clickable Rows/Headers: `onKeyDown` für Enter/Space + `role="button"` (oder `treeitem`/`row`) + `tabIndex={0}`

Files: `QuickConnectPicker`, `EdgeInserter`, `HelpOverlay`, `SearchOverlay`, `FindReplaceOverlay`, `PrePublishChecklistModal`, `SubWorkflowPreviewModal`, `WorkflowDiffModal`, `EditCellDialog`, `KinskiEasterEgg`, `ScorchEasterEgg`, `SharedFolderPermissionsModal`, `ExecutionPanel`, `LiveConsole`, `panelChrome`, `VariablePreviewTooltip`, `SharedFolderTree`, `SupportEventsTable`, `DashboardPage` (5 Rows), `GlobalVariablesPage`, `UsersPage`.

### False-Positive-Cleanup (via Sonar REST API)

| Rule | # | Aktion | Begründung |
|---|---:|---|---|
| **S2068** "hard-coded credential" | 10 | `do_transition=falsepositive` | Wort `"password"` in Config-Keys (`"Smtp:Password"`), DTO-Property-Namen, und `SecurityHardeningWarnings.cs` selbst (das Plaintext-Passwörter im Config erkennt). Keine echten Credentials im Source |
| **S2699** "test without assertion" | 25 | `do_transition=falsepositive` | Tests assertieren via FluentAssertions Extension-Methods + WireMock-Verify + Mock.Verify — Sonars Pattern-Matcher erkennt diese Assertions nicht |
| **S5693** "content length limit" | 2 | `change_status=REVIEWED, resolution=SAFE` | `[RequestSizeLimit]` mit 40 MiB / 50 MiB sind bewusste H-16-Härtung mit Begründung in CLAUDE.md |

## Verbleibende Findings (V3)

### Backend (`nodepilot`)

- **0 Bugs, 0 Vulnerabilities** ✓
- **815 Code Smells** — alle MAJOR/MINOR/INFO. Top-Cluster:
  - S125 (Sections of code should not be commented out), S107 (zu viele Method-Parameter), S1192 (String-Literal-Duplication)
  - Diese sind Wartbarkeits-Hinweise, kein operativer Defekt
- **8 Security Hotspots** TO_REVIEW (von 28):
  - S2092 (4) — Cookie-Konfiguration ohne `Secure`-Flag-Override (production-config überschreibt)
  - S3330 (2) — Cookie ohne `HttpOnly`-Flag
  - S5332 (2) — HTTP statt HTTPS in Test-/Lokal-URLs

### Frontend (`nodepilot-ui`)

- **0 Bugs, 0 Vulnerabilities** ✓
- **924 Code Smells** — durch den A11y-Refactor leicht gestiegen (+15). Top-Cluster:
  - S6759 (Readonly-Props), S3358 (Nested-Ternary), S7764, S7735, S6819
  - Reine TypeScript-Style-Hinweise, kein Funktional-Bug
- **8 Security Hotspots** TO_REVIEW — analog Backend, vorwiegend Frame-Origin / Hyperlink-Sicherheits-Hinweise

## Test-Status nach allen Fixes

| Test-Suite | Tests | Status |
|---|---:|---|
| NodePilot.Data.Tests | 82 | ✓ alle grün |
| NodePilot.Cli.Tests | 284 | ✓ alle grün |
| NodePilot.Engine.Tests | 928 | ✓ alle grün (inkl. umgebauter `ScheduleJobTests`) |
| NodePilot.Api.Tests | 1 101 | ✓ alle grün |
| **Backend Total** | **2 395** | **✓ 100 % pass** |
| Frontend (Vitest) | ~280 Test-Files | ✓ alle grün, Coverage 69.06 % Statements / 71.52 % Lines |

## Reproduzierbarkeit

```powershell
# 1. SonarQube läuft als Container "nodepilot-sonarqube" — Token in User-Env
$env:SONAR_TOKEN = [System.Environment]::GetEnvironmentVariable('SONAR_TOKEN','User')

# 2. Backend
Remove-Item TestResults -Recurse -Force -ErrorAction SilentlyContinue
dotnet test NodePilot.slnx --collect:"XPlat Code Coverage" --results-directory TestResults `
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
dotnet-sonarscanner begin /k:"nodepilot" /n:"NodePilot" /v:"X.X" `
  /d:sonar.host.url="http://localhost:9000" /d:sonar.token="$env:SONAR_TOKEN" `
  /d:sonar.cs.opencover.reportsPaths="TestResults/**/coverage.opencover.xml" `
  /d:sonar.exclusions="**/bin/**,**/obj/**,**/Migrations/**,**/.migrate-tool/**,**/TestResults/**" `
  /d:sonar.scanner.scanAll="true"
dotnet build NodePilot.slnx --no-incremental
dotnet-sonarscanner end /d:sonar.token="$env:SONAR_TOKEN"

# 3. Frontend (Source-Staging zum Umgehen der Bridge-Discovery-Probleme)
cd src\nodepilot-ui
npx vitest run --coverage --coverage.reportsDirectory=coverage-sonar
# Stage src+coverage in temp dir, dann:
docker run --rm `
  -e SONAR_HOST_URL="http://host.docker.internal:9000" `
  -e SONAR_TOKEN="$env:SONAR_TOKEN" `
  -v "$env:LOCALAPPDATA/Temp/nodepilot-ui-scan:/usr/src" `
  sonarsource/sonar-scanner-cli `
  -Dsonar.projectKey=nodepilot-ui -Dsonar.projectName='NodePilot UI' `
  -Dsonar.sources=src -Dsonar.tests=src `
  -Dsonar.javascript.lcov.reportPaths='coverage-sonar/lcov.info' `
  -Dsonar.scm.disabled=true
```

## Container-Lifecycle

- **Läuft permanent** als `nodepilot-sonarqube` (Docker `--restart unless-stopped`)
- **Stoppen ohne Datenverlust:** `docker stop nodepilot-sonarqube`
- **Re-Start:** `docker start nodepilot-sonarqube`
- **Komplett-Cleanup:** `docker rm -f nodepilot-sonarqube; docker volume rm sonarqube_data sonarqube_extensions sonarqube_logs`
- **Storage:** Embedded H2 in `sonarqube_data`-Volume (für one-shot-Scans ausreichend, bei produktivem Einsatz auf Postgres migrieren)

## Anhang: Raw Data

Alle abgerufenen Sonar-API-Responses liegen unter [.sonar-report-data/](../.sonar-report-data/) (gitignored):
- `measures-{nodepilot,nodepilot-ui}.json` (V1) und `-final.json` (V3)
- `issues-{key}-{BUG,VULNERABILITY,CODE_SMELL}.json` (V1-Snapshot)
- `hotspots-{key}.json`, `codesmell-facets-{key}.json`
- `qg-{backend,ui}.json` (Quality-Gate-Details)
- `smells-nodepilot-BLOCKER.json` / `smells-ui-CRITICAL.json` (V1-Detail-Snapshot)
