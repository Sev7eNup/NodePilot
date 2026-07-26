# SonarQube Code Quality Scan — 2026-07-26

**Branch:** `main` @ `08fe37ad` (sauberer Working-Tree)
**SonarQube:** Community Build 26.5.0.122743 (Docker `nodepilot-sonarqube`, `http://localhost:9000`)
**Scanner:** dotnet-sonarscanner 11.2.1 (Backend) + sonar-scanner-cli via Docker (Frontend)

| Projekt-Key | Stack | Dashboard |
|---|---|---|
| `nodepilot` | .NET 10 / C# | http://localhost:9000/dashboard?id=nodepilot |
| `nodepilot-ui` | React 19 / TypeScript + CSS | http://localhost:9000/dashboard?id=nodepilot-ui |

**Scope-Änderung gegenüber den Vorgänger-Scans:** `sonar.scanner.scanAll=false`. Damit erfasst der
Backend-Scan **ausschließlich** die C#-Dateien der MSBuild-Projekte — keine Deploy-/Hilfsskripte,
kein `.claude/`, keine `scripts/*.py`, kein `grafana/`. Genau deshalb entfällt der komplette
False-Positive-Block der Scans 2026-05-16 / 2026-06-22 (`secrets:S6698`, `python:S2068` etc.).

## Ergebnis

Erst-Scan (Spalte „vorher", `main` @ `08fe37ad`) und Stand nach der Remediation auf
`fix/sonar-findings-2026-07-26` (Spalte „nachher"). Details zur Remediation: Abschnitt
[Remediation](#remediation) am Ende.

| Metrik | Backend vorher | Backend nachher | Frontend vorher | Frontend nachher |
|---|---:|---:|---:|---:|
| 🐞 Bugs | **6** | **0** | **4** | **0** |
| 🛡️ Vulnerabilities | **3** | **0** | 0 | 0 |
| 🔥 Security Hotspots (TO_REVIEW) | 9 | **0** | 4 | **0** |
| Hotspots reviewed | 47.1 % | **100 %** | 66.7 % | **100 %** |
| 🧹 Code Smells | 2 283 | 2 227 | 515 | 509 |
| Reliability Rating | **E** | **A** | **D** | **A** |
| ↳ Clean-Code-Variante | E | **A** | D | **C**\*\* |
| Security Rating | **C** | **A** | A | A |
| Security Review Rating | D | **A** | C | **A** |
| Maintainability Rating | A | A | A | A |
| Coverage (Line) | 80.4 % (84.4 %) | 80.5 % (84.4 %) | 67.8 % (73.0 %) | 67.8 % (73.0 %) |
| Duplication | 2.7 % | 2.7 % | 4.6 % | 4.6 % |
| Sqale-Index | 3 415 min | 3 090 min | 2 844 min | 2 815 min |
| Quality Gate | ⚠️ ERROR\* | ⚠️ ERROR\* | ⚠️ ERROR\* | ⚠️ ERROR\* |

\*\* Backend hat nach der Remediation **0** Issues mit RELIABILITY-Impact. Im Frontend bleiben
30 (20 MEDIUM, 10 LOW) — es sind ausnahmslos Code Smells (12× `S6848`, 7× `S7773`, 4× `S7781`,
2× `S6847`, 2× `S6811`, je 1× `S6845`/`S7758`/`S6772`) und lagen damit außerhalb des
Remediation-Scopes. Die klassische Reliability-Note steht dennoch auf A, weil sie nur
Issues vom Typ BUG zählt.

\* **New-Code-Baseline-Artefakt, kein Regress.** Ohne Referenz-Branch zählt SonarQube den gesamten
Bestand als „new code": `new_violations` = 2 139 / 327 (Schwelle 0) und
`new_security_hotspots_reviewed` = 0 % (Schwelle 100 %). Die Coverage-Bedingung ist im Backend
mit 80.5 % **erfüllt**; im Frontend verfehlt sie mit 69.3 % das 80-%-Ziel.

## 🐞 Bugs — exakte Fundstellen

### Backend (6)

| # | Datei:Zeile | Rule | Sev | Befund | Bewertung |
|---|---|---|---|---|---|
| 1 | [IsolatedProcessLauncher.cs:111](../../src/NodePilot.Engine/PowerShell/IsolatedProcessLauncher.cs#L111) | S3869 | BLOCKER | `nulIn.DangerousGetHandle()` | **Akzeptiertes Interop** |
| 2 | [IsolatedProcessLauncher.cs:112](../../src/NodePilot.Engine/PowerShell/IsolatedProcessLauncher.cs#L112) | S3869 | BLOCKER | `outPipe.ClientSafePipeHandle.DangerousGetHandle()` | **Akzeptiertes Interop** |
| 3 | [IsolatedProcessLauncher.cs:113](../../src/NodePilot.Engine/PowerShell/IsolatedProcessLauncher.cs#L113) | S3869 | BLOCKER | `errPipe.ClientSafePipeHandle.DangerousGetHandle()` | **Akzeptiertes Interop** |
| 4 | [IsolatedProcessLauncher.cs:127](../../src/NodePilot.Engine/PowerShell/IsolatedProcessLauncher.cs#L127) | S3869 | BLOCKER | `job.DangerousGetHandle()` für `PROC_THREAD_ATTRIBUTE_JOB_LIST` | **Akzeptiertes Interop** |
| 5 | [IsolatedProcessLauncher.cs:454](../../src/NodePilot.Engine/PowerShell/IsolatedProcessLauncher.cs#L454) | S3869 | BLOCKER | `_process.DangerousGetHandle()` in `WaitForExitAsync` | **Akzeptiertes Interop** |
| 6 | [WorkflowReviewAnalyzer.cs:114](../../src/NodePilot.Core/WorkflowDefinitions/WorkflowReviewAnalyzer.cs#L114) | S4143 | MAJOR | `state[id] = 2;` überschreibt `state[id] = 1;` (Z. 104) | **False Positive** |

**Zu 1–5:** `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` / `STARTUPINFOEX` verlangen rohe `HANDLE`-Werte —
`SafeHandle` lässt sich hier prinzipiell nicht durchreichen. Die Lebensdauer ist bewusst
abgesichert: die Value-Buffer bleiben bis `DeleteProcThreadAttributeList` am Leben, der
`SafeWaitHandle` in Z. 454 ist explizit `ownsHandle: false`, und `GC.KeepAlive(_process)`
(Z. 468) verhindert die vorzeitige Finalisierung. Kein Defekt — aber der Grund, warum das
Reliability-Rating auf **E** steht (5× BLOCKER).

**Zu 6:** Klassisches White/Gray/Black-DFS-Coloring. `state[id]=1` (grau) wird zwischen den beiden
Zuweisungen in der Rekursion via `state.TryGetValue(t, …)` (Z. 109) gelesen — genau darauf beruht
die Zyklenerkennung. Der Regel-Matcher sieht den Read über den Rekursionspfad nicht.

### Frontend (4)

| # | Datei:Zeile | Rule | Sev | Befund | Bewertung |
|---|---|---|---|---|---|
| 1 | [CustomActivitiesPage.tsx:331](../../src/nodepilot-ui/src/pages/CustomActivitiesPage.tsx#L331) | S3923 | MAJOR | `{e.isEnabled ? <Power size={16} /> : <Power size={16} />}` | **Echter Bug** |
| 2 | [MetricsPage.tsx:117](../../src/nodepilot-ui/src/pages/MetricsPage.tsx#L117) | S2871 | CRITICAL | `[...new Set(timestamps)].sort()` auf `number[]` | **Echter Bug (latent)** |
| 3 | [MetricsPage.tsx:59](../../src/nodepilot-ui/src/pages/MetricsPage.tsx#L59) | S3923 | MAJOR | Ternary-Kette endet auf `… <= 12 ? 'xl:col-span-12' : 'xl:col-span-12'` | **Toter Code** |
| 4 | [workflowDiff.ts:29](../../src/nodepilot-ui/src/lib/workflowDiff.ts#L29) | S2871 | CRITICAL | `Object.keys(obj).sort()` | **False Positive** |

**Zu 1 — echter UI-Defekt.** Der Enable/Disable-Toggle in der Custom-Activities-Tabelle rendert in
beiden Zuständen dasselbe Icon. Der Button gibt damit **keine visuelle Rückmeldung**, ob die
Activity aktiviert ist; erkennbar ist der Zustand nur über das `title`-Attribut. Gewollt war
offensichtlich ein zustandsabhängiges Icon bzw. eine Farbdifferenzierung.

**Zu 2 — echter, aktuell maskierter Defekt.** `MetricsPoint.timestamp` ist laut
[types/api.ts:137](../../src/nodepilot-ui/src/types/api.ts#L137) ein `number`. `Array.sort()` ohne
Comparator sortiert **lexikografisch über die String-Repräsentation** — `[9, 100, 20]` → `[100, 20, 9]`.
Die Heatmap-X-Achse hängt genau an dieser Sortierung. Praktisch fällt es heute nicht auf, weil alle
Unix-Sekunden-Timestamps 10-stellig sind und lexikografische = numerische Ordnung bei gleicher
Stellenzahl zusammenfallen. Fix: `.sort((a, b) => a - b)`.

**Zu 3 — kein Verhaltensfehler.** Die letzte Bedingung ist tot: beide Zweige liefern
`'xl:col-span-12'`. Das Ergebnis ist korrekt, die Bedingung überflüssig.

**Zu 4 — False Positive, der Fix wäre schlechter.** `stableStringify` braucht eine **deterministische**
Schlüsselordnung für reproduzierbare Diffs. Der Default-Sort über UTF-16-Code-Units ist genau das;
`localeCompare` wäre locale-abhängig und damit über Umgebungen hinweg **nicht** stabil.

## 🛡️ Vulnerabilities (Backend, 3) — alle False Positive

| Datei:Zeile | Rule | Befund |
|---|---|---|
| [AuthDtos.cs:10](../../src/NodePilot.Api/Dtos/AuthDtos.cs#L10) | S2068 | Property-Name enthält `password` |
| [CredentialDtos.cs:5](../../src/NodePilot.Api/Dtos/CredentialDtos.cs#L5) | S2068 | dito |
| [UserDtos.cs:19](../../src/NodePilot.Api/Dtos/UserDtos.cs#L19) | S2068 | dito |

DTO-Property-Namen, keine hartkodierten Credentials. Gleiche Bewertung wie 2026-05-16 — die
FP-Markierung ging beim Projekt-Reset verloren und müsste erneut gesetzt werden. Security-Rating **C**
kommt ausschließlich hierher.

## 🔥 Security Hotspots (TO_REVIEW)

**Backend (9)**

| Datei:Zeile | Rule | Thema |
|---|---|---|
| [WorkflowCallGraphBuilder.cs:55](../../src/NodePilot.Core/Operations/WorkflowCallGraphBuilder.cs#L55) | S6444 | Regex ohne Timeout |
| [Mcp/Analysis/VariableResolver.cs:17,18](../../src/NodePilot.Mcp/Analysis/VariableResolver.cs#L17) | S6444 | dito |
| [Mcp/Analysis/WorkflowAnalyzer.cs:226,227,228](../../src/NodePilot.Mcp/Analysis/WorkflowAnalyzer.cs#L226) | S6444 | dito |
| [CanvasAssistantTools.cs:23](../../src/NodePilot.Mcp/Tools/CanvasAssistantTools.cs#L23) | S6444 | dito |
| [EmailActivity.cs:51](../../src/NodePilot.Engine/Activities/EmailActivity.cs#L51) | S5332 | `EnableSsl` user-controlled |
| [SmtpNotificationSink.cs:43](../../src/NodePilot.Engine/Notifications/SmtpNotificationSink.cs#L43) | S5332 | dito |

Die S6444-Fundstellen sind **neu gegenüber 2026-05-16**: das damalige Regex-Timeout-Hardening
(18 Call-Sites) deckt `NodePilot.Mcp` und `Core/Operations` nicht ab — beide sind erst danach
entstanden. Das ist die einzige Hotspot-Gruppe mit echtem Nachzieh-Bedarf.

**Frontend (4)**

| Datei:Zeile | Rule | Thema |
|---|---|---|
| [AiWorkflowChatPanel.tsx:349](../../src/nodepilot-ui/src/components/ai/AiWorkflowChatPanel.tsx#L349) | S5852 | Backtracking-anfälliger Regex |
| [WorkflowNameField.tsx:21](../../src/nodepilot-ui/src/components/designer/header/WorkflowNameField.tsx#L21) | S5852 | dito |
| [AiChatPage.tsx:250](../../src/nodepilot-ui/src/pages/AiChatPage.tsx#L250) | S5852 | dito |
| [aiChatStore.ts:90](../../src/nodepilot-ui/src/stores/aiChatStore.ts#L90) | S2245 | `Math.random()` (Client-seitige ID) |

## 🧹 Code Smells

**Backend: 2 283** — davon **1 524 (67 %) allein `external_roslyn:xUnit1051`**, ausschließlich in
`tests/`. Das ist ein direktes Artefakt der xunit.v3-Migration aus `08fe37ad`: der v3-Analyzer
verlangt `TestContext.Current.CancellationToken` an jedem Aufruf, der ein `CancellationToken`
entgegennimmt. Rein mechanisch, ohne Korrektheitsbezug.

| Rule | # | Thema |
|---|---:|---|
| `external_roslyn:xUnit1051` | 1 524 | xunit.v3-CancellationToken (nur `tests/`) |
| `csharpsquid:S3776` | 123 | Cognitive Complexity |
| `csharpsquid:S1192` | 92 | String-Literal-Duplikation |
| `external_roslyn:CA1861` | 68 | Konstante Arrays als Argument |
| `csharpsquid:S927` | 42 | Parameter-Naming |
| `external_roslyn:CA1859` | 42 | Konkreter Typ statt Interface |
| `csharpsquid:S6964` | 41 | Model-Binding: Value-Type ohne `[BindNever]` |

Severity-Verteilung: 8 BLOCKER · 169 CRITICAL · 1 704 MAJOR · 171 MINOR · 231 INFO.

**Frontend: 515**

| Rule | # | Thema |
|---|---:|---|
| `typescript:S3358` | 193 | Verschachtelte Ternaries |
| `typescript:S7735` | 37 | — |
| `typescript:S7748` | 35 | — |
| `typescript:S3776` | 30 | Cognitive Complexity |
| `typescript:S4325` | 24 | Redundante Type-Assertion |
| `typescript:S6819` | 23 | ARIA-Rolle statt semantischem Tag |
| `typescript:S6479` | 20 | Array-Index als React-Key |

Severity-Verteilung: 0 BLOCKER · 51 CRITICAL · 278 MAJOR · 185 MINOR · 1 INFO.

Bewertung unverändert gegenüber 2026-05-16 / 2026-06-22: Wartbarkeits-Hinweise ohne Korrektheits-
oder Security-Impact, Maintainability-Rating steht beidseitig auf **A**. Kein Refactoring-Bedarf.

## 📏 Lines of Code — exakt

Zählweise = SonarQube-`ncloc`: eine Zeile zählt, wenn nach Abzug von Kommentaren und Whitespace
mindestens ein Zeichen übrig bleibt. Gezählt wurde **nur** reines Backend (`src/NodePilot.*`) und
reines Frontend (`src/nodepilot-ui`) — keine Deploy-Skripte, keine Hilfsskripte, kein `grafana/`,
kein `docs/`.

### Zusammenfassung

| Bucket | Dateien | **ncloc** | Physische Zeilen |
|---|---:|---:|---:|
| **Backend — Produktivcode** | 602 | **65 353** | 89 137 |
| **Backend — Tests** | 403 | **69 759** | 86 547 |
| **Frontend — Produktivcode** | 317 | **53 973** | 64 295 |
| **Frontend — Tests (vitest)** | 185 | **26 282** | 32 891 |
| **Frontend — Tests (Playwright e2e)** | 72 | **10 518** | 14 967 |
| Summe Produktivcode | 919 | **119 326** | 153 432 |
| Summe Testcode | 660 | **106 559** | 134 405 |
| **Gesamt** | **1 579** | **225 885** | **287 837** |

Test-zu-Produktivcode-Verhältnis: Backend **1.07 : 1**, Frontend **0.68 : 1** (mit e2e), gesamt **0.89 : 1**.

**Nicht enthalten (bewusst separat):** EF-Core-Migrationen unter `src/NodePilot.Data/Migrations/` —
46 Dateien, **27 152 ncloc** (36 957 physisch). Generierter Code, im Scan über
`sonar.exclusions=**/Migrations/**` ausgeschlossen.

### Backend — Produktivcode je Projekt

| Projekt | Dateien | ncloc | Physisch |
|---|---:|---:|---:|
| NodePilot.Api | 227 | 28 792 | 38 278 |
| NodePilot.Engine | 83 | 12 448 | 17 568 |
| NodePilot.Cli | 59 | 6 647 | 8 072 |
| NodePilot.Scheduler | 43 | 3 990 | 5 645 |
| NodePilot.Mcp | 34 | 3 981 | 4 911 |
| NodePilot.Core | 102 | 3 178 | 5 831 |
| NodePilot.Ai | 23 | 2 771 | 3 891 |
| NodePilot.Data | 22 | 2 633 | 3 656 |
| NodePilot.Remote | 5 | 523 | 758 |
| NodePilot.Telemetry | 4 | 390 | 527 |
| **Summe** | **602** | **65 353** | **89 137** |

### Backend — Testcode je Projekt

| Projekt | Dateien | ncloc | Physisch |
|---|---:|---:|---:|
| NodePilot.Api.Tests | 159 | 30 176 | 37 055 |
| NodePilot.Engine.Tests | 130 | 22 848 | 28 849 |
| NodePilot.Cli.Tests | 29 | 6 616 | 7 936 |
| NodePilot.Data.Tests | 25 | 3 299 | 4 327 |
| NodePilot.Ai.Tests | 23 | 3 106 | 3 830 |
| NodePilot.Mcp.Tests | 25 | 2 915 | 3 518 |
| NodePilot.LoadTests\* | 7 | 653 | 796 |
| NodePilot.TestCommons\* | 5 | 146 | 236 |
| **Summe** | **403** | **69 759** | **86 547** |

\* `NodePilot.LoadTests` ist ein Konsolen-Harness (`OutputType=Exe`), kein Unit-Test-Projekt;
`NodePilot.TestCommons` ist eine Test-Hilfsbibliothek. Beide enthalten keine ausführbaren Tests.

### Frontend — Produktivcode je Ordner

| Ordner | Dateien | ncloc | Physisch |
|---|---:|---:|---:|
| components | 192 | 30 979 | 35 999 |
| pages | 21 | 10 569 | 11 849 |
| lib | 44 | 4 190 | 5 442 |
| hooks | 28 | 2 801 | 3 759 |
| index.css | 1 | 2 389 | 3 197 |
| api | 13 | 1 113 | 1 440 |
| stores | 11 | 786 | 1 102 |
| styles | 1 | 466 | 642 |
| types | 1 | 295 | 407 |
| i18n\*\* | 2 | 162 | 176 |
| App.tsx | 1 | 126 | 157 |
| telemetry | 1 | 80 | 102 |
| main.tsx | 1 | 17 | 23 |
| **Summe** | **317** | **53 973** | **64 295** |

\*\* Nur die beiden TS-Dateien der i18n-Verdrahtung. Die 60 Übersetzungs-JSONs unter
`src/i18n/locales/` sind Daten, kein Code, und daher nicht gezählt.

### Abgleich mit Sonars eigener `ncloc`-Metrik

| | Eigene Zählung | Sonar `ncloc` | Delta |
|---|---:|---:|---:|
| Backend Produktivcode | 65 353 | 65 352\* | **1 Zeile** (0.002 %) |
| Frontend Produktivcode | 53 973 | 54 336 | 363 (0.67 %) |

\* Sonar meldet für `nodepilot` **66 151** über 614 Dateien. Darin stecken **12 Dateien / 799 ncloc**
aus `tests/NodePilot.LoadTests` und `tests/NodePilot.TestCommons`, die die MSBuild-Test-Erkennung des
Scanners **nicht** als Testprojekte klassifiziert (LoadTests ist ein `Exe`, TestCommons eine Library —
keins von beiden matcht das Default-Pattern). Bereinigt: 66 151 − 799 = **65 352**. Der Restunterschied
zur eigenen Zählung ist eine einzige Zeile in
[EventLogTriggerSource.cs](../../src/NodePilot.Scheduler/Sources/EventLogTriggerSource.cs).

Die Frontend-Abweichung verteilt sich auf 87 Dateien und ist eine reine Parser-Definitionsfrage
(Sonar zählt CSS-Zeilen konservativer, mehrzeilige JSX-Ausdrücke großzügiger). Sonar zählt
`src/lib/monacoTypes.d.ts` gar nicht (`.d.ts` = reine Deklaration), daher 316 statt 317 Dateien.

Für Testcode liefert SonarQube **keine** `ncloc`-Metrik — Dateien mit `sonar.tests`-Klassifikation
werden indiziert und auf Issues geprüft, aber nicht in die LOC-Bilanz aufgenommen. Die Testzahlen
oben stammen daher durchgängig aus der eigenen Zählung, die im Backend gegen Sonar auf 1 Zeile
genau validiert ist.

## Test-Status

| Suite | Tests | Status |
|---|---:|---|
| NodePilot.Api.Tests | 1 840 | ✅ |
| NodePilot.Engine.Tests | 1 498 | ✅ |
| NodePilot.Cli.Tests | 432 | ✅ |
| NodePilot.Ai.Tests | 296 | ✅ |
| NodePilot.Data.Tests | 210 | ✅ |
| NodePilot.Mcp.Tests | 163 | ✅ |
| **Backend gesamt** | **4 439** | **✅ 0 Fehler, 0 übersprungen** |
| Frontend (Vitest, 184 Dateien) | 2 354 | ✅ alle grün |

Frontend-Coverage laut Vitest: Statements 73.7 %, Branches 61.97 %, Functions 60.64 %, Lines 76.48 %.

## Reproduktion

```powershell
$env:SONAR_TOKEN = [System.Environment]::GetEnvironmentVariable('SONAR_TOKEN','User')
docker start nodepilot-sonarqube   # bootet ~60-90 s bis /api/system/status = UP

# --- Backend ---
# Vorher: alle NodePilot.Api-Prozesse killen (der Windows-Dienst "NodePilot" aus
# C:\Program Files\NodePilot startet sich sonst selbst neu und sperrt die Debug-DLLs)
Remove-Item TestResults, .sonarqube -Recurse -Force -ErrorAction SilentlyContinue
dotnet-sonarscanner begin /k:"nodepilot" /n:"NodePilot" /v:"2026-07-26" `
  /d:sonar.host.url="http://localhost:9000" /d:sonar.token="$env:SONAR_TOKEN" `
  /d:sonar.cs.opencover.reportsPaths="TestResults/**/coverage.opencover.xml" `
  /d:sonar.exclusions="**/bin/**,**/obj/**,**/Migrations/**,**/TestResults/**" `
  /d:sonar.scanner.scanAll=false
dotnet build NodePilot.slnx --no-incremental
dotnet test NodePilot.slnx --no-build --settings coverage.runsettings `
  --collect:"XPlat Code Coverage" --results-directory TestResults `
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
dotnet-sonarscanner end /d:sonar.token="$env:SONAR_TOKEN"

# --- Frontend ---
cd src\nodepilot-ui
npx vitest run --coverage --coverage.reportsDirectory=coverage-sonar
# src\, e2e\ und coverage-sonar\lcov.info nach C:\temp\nodepilot-ui-scan stagen;
# im lcov die SF:-Pfade auf Forward-Slashes normalisieren, dann:
docker run --rm -e SONAR_HOST_URL="http://host.docker.internal:9000" `
  -e SONAR_TOKEN="$env:SONAR_TOKEN" -v "C:\temp\nodepilot-ui-scan:/usr/src" `
  sonarsource/sonar-scanner-cli `
  "-Dsonar.projectKey=nodepilot-ui" "-Dsonar.sources=src" `
  "-Dsonar.tests=src/__tests__,e2e" "-Dsonar.exclusions=src/__tests__/**" `
  "-Dsonar.javascript.lcov.reportPaths=coverage-sonar/lcov.info" "-Dsonar.scm.disabled=true"
```

**Stolperfallen:**
- Die `-D…`-Argumente **müssen** in PowerShell einzeln gequotet werden (`"-Dsonar.projectKey=…"`),
  sonst zerlegt der Argument-Parser sie am ersten Punkt → `Unrecognized option: .projectKey=…`.
- `sonar.exclusions` und `sonar.tests` müssen disjunkt sein, sonst „can't be indexed twice".
- Der Dienst `NodePilot` (Auto-Start, LocalSystem) muss vor dem Backend-Build gestoppt sein.

## Remediation

Branch `fix/sonar-findings-2026-07-26`. Verifiziert mit Backend 4 439 Tests / Frontend 2 354 Tests /
`tsc --noEmit` — alle grün, danach Re-Scan mit identischer Scanner-Konfiguration.

### Im Code behoben

| Was | Wo | Fix |
|---|---|---|
| `S3923` — Toggle ohne Zustandsanzeige | [CustomActivitiesPage.tsx:331](../../src/nodepilot-ui/src/pages/CustomActivitiesPage.tsx#L331) | Icon-Farbe zustandsabhängig, Muster aus `AlertingPage` übernommen |
| `S2871` — lexikografischer Sort auf `number[]` | [MetricsPage.tsx:117](../../src/nodepilot-ui/src/pages/MetricsPage.tsx#L117) | `.sort((a, b) => a - b)` |
| `S3923` — tote Ternary-Bedingung | [MetricsPage.tsx:59](../../src/nodepilot-ui/src/pages/MetricsPage.tsx#L59) | letzte Bedingung entfernt |
| `S2871` — `Object.keys().sort()` | [workflowDiff.ts:29](../../src/nodepilot-ui/src/lib/workflowDiff.ts#L29) | expliziter Code-Unit-Comparator statt `localeCompare` (das wäre locale-abhängig und damit **weniger** deterministisch) |
| `S5852` — Backtracking-Regex (2×) | AiWorkflowChatPanel / AiChatPage | gemeinsamer `chatFilenameSlug()` in [chatExport.ts](../../src/nodepilot-ui/src/lib/chatExport.ts) mit linearem Dash-Trim; entfernt nebenbei eine Duplikation |
| `S5852` — Backtracking-Regex | [WorkflowNameField.tsx](../../src/nodepilot-ui/src/components/designer/header/WorkflowNameField.tsx#L20) | Index-Scan statt `/^(\S+)\s+(.+)$/` |
| `S2245` — `Math.random()` | [aiChatStore.ts:90](../../src/nodepilot-ui/src/stores/aiChatStore.ts#L90) | `crypto.randomUUID()` (im Repo bereits etabliert) |
| `S6444` — Regex ohne Timeout (7×) | `Core/Operations`, `Mcp/Analysis`, `Mcp/Tools` | `TimeSpan.FromSeconds(1)` — zieht das Mai-Hardening auf die danach entstandenen Projekte nach |
| `S6966` — sync statt async Overload (19×) | CLI-Commands, `Program.cs`, `DbAdminQueryExecutor`, `RestApiActivity` | `ConfirmAsync`/`AskAsync`/`PromptAsync`, `AnyAsync`, `IsDBNullAsync`, `WriteAsync` |
| `S6964` — Under-Posting auf Value-Types (41×) | 12 Request-DTOs | `[property: JsonRequired]` |

**Ausnahme bei `S6964`:** `LlmSettingsDto.EnableToolCalling` und `LdapAuthenticationDto.Enabled`
haben `bool?` statt `[JsonRequired]` bekommen. Beide sind bewusst optionale Opt-in-Flags — die
Mapper lesen sie seit jeher als `?? false` ([SettingsSections.cs:374](../../src/NodePilot.Api/Configuration/SettingsSections.cs#L374)).
`[JsonRequired]` hätte jeden Settings-POST ohne diese Felder auf 400 laufen lassen; genau das hat
`AdminSettingsControllerSectionTests.PutSection_Llm_HappyPath_PersistsEncryptedApiKey` aufgedeckt.
`bool?` drückt die Optionalität im Typ aus und erfüllt die Regel, ohne den Contract zu brechen.

### In SonarQube quittiert (nicht code-fixbar)

| Rule | # | Transition | Begründung |
|---|---:|---|---|
| `csharpsquid:S3869` | 5 | `accept` | `STARTUPINFOEX`/`PROC_THREAD_ATTRIBUTE_HANDLE_LIST` verlangen rohe HANDLEs; Lebensdauer über `ownsHandle: false`, `GC.KeepAlive` und bis `DeleteProcThreadAttributeList` lebende Value-Buffer abgesichert |
| `csharpsquid:S4143` | 1 | `falsepositive` | White/Gray/Black-DFS — der Read zwischen den beiden Writes passiert über die Rekursion |
| `csharpsquid:S2068` | 3 | `falsepositive` | DTO-Property-Namen mit „password", keine Credentials |
| `csharpsquid:S6932` | 5 | `falsepositive` | liest `Authorization`/Custom-Header, um Auth-Pfade zu unterscheiden — per Model-Binding nicht ausdrückbar |
| `csharpsquid:S5332` | 2 (Hotspot) | `REVIEWED/SAFE` | `SmtpOptions.EnableSsl` ist Operator-Setting mit sicherem Default `true` + Boot-Warnung |

Jede Transition trägt einen Kommentar mit derselben Begründung am Issue.

### Nicht umgesetzt: `xUnit1051` (1 524×, nur `tests/`)

Der Versuch, die xunit.v3-Warnung mechanisch zu beheben, ist **gescheitert und wurde zurückgesetzt**:

- `dotnet format analyzers --diagnostics xUnit1051` bricht ab — der mitgelieferte
  `UseCancellationTokenFixer` hat **keinen FixAll-Provider**.
- Eigener Rewriter, Variante „Token positionell anhängen" → 16 Compilerfehler: der
  `CancellationToken` ist bei vielen Methoden nicht der letzte Parameter.
- Eigener Rewriter, Variante „benanntes Argument", Parametername aus 631 Repo-Deklarationen
  aufgelöst → 618 Compilerfehler: 226× ist bereits ein Token positionell übergeben (der Fix wäre
  *ersetzen*), 234× kollidieren Repo-Methodennamen mit EF-Core-Methoden (`CountAsync(ct:)` vs.
  `cancellationToken:`), 158× lässt die `params`-Überladung von `FindAsync` gar kein benanntes
  Token-Argument zu.

Der Fix braucht Overload-Resolution pro Aufrufstelle — genau der Grund für das fehlende FixAll.
Die Regel ist rein test-seitig, ohne Korrektheits- oder Security-Bezug; Maintainability steht
beidseitig auf A. Optionen für später: pro Aufrufstelle von Hand, oder eine bewusste
Projektentscheidung als `dotnet_diagnostic.xUnit1051.severity` in einer `.editorconfig`.

### Weitere Fallstricke aus diesem Lauf

- Der Auto-Start-Dienst `NodePilot` (LocalSystem, `C:\Program Files\NodePilot\app`) startet
  `NodePilot.Api` nach jedem `Stop-Process` sofort neu und sperrt dann `src/**/bin|obj`. Für den
  Build muss `Stop-Service NodePilot` laufen — danach wieder `Start-Service`.
- Das vitest-Coverage-Verzeichnis **darf nicht** unter `src/nodepilot-ui/` liegen: der v8-Reporter
  wirft auf dem E:-Laufwerk `EPERM` beim Anlegen der Unterordner und hinterlässt Verzeichnisse, die
  niemand mehr enumerieren kann. Das bringt anschließend `AuditActionsCatalogTests` mit
  `UnauthorizedAccessException` zu Fall, weil der Test `src/**/*.cs` durchläuft. Reportdir auf
  `C:\temp\nodepilot-ui-cov` legen.
- Backend-Scan und vitest-Coverage **nicht parallel** fahren — die vitest-Forks laufen sonst in
  `Timeout waiting for worker to respond` und 22 Testdateien fallen still aus dem Lauf.
