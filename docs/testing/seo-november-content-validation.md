# Validation of the November SEO article package

Prepared 2026-10-25 for the 2026-11-01 release preparation. The articles remain drafts. This is content validation, not evidence of a deployed site or a completed model-assisted workflow.

## Executed PowerShell checks

Runtime: Windows PowerShell **5.1.26100.9711** on Windows. Nine distinct PowerShell blocks were extracted from `powershell-try-catch`, `ai-workflows` and `powershell-execution-policy`. All parsed successfully, and corresponding German/English blocks were identical.

Twelve checks passed:

| Check | Observation |
| --- | --- |
| Syntax and language parity | Nine unique blocks parsed; DE/EN examples match |
| Missing file with ErrorAction Continue | Error emitted; AFTER printed; catch not entered |
| Missing file with ErrorAction Stop | catch and finally entered; AFTER absent |
| Valid ready JSON input | JSON state accepted; process exit 0 |
| Invalid JSON input | Error on stderr; process exit 1; no success output |
| Blocked JSON state | Precondition error; process exit 1 |
| Missing input file | File error; process exit 1 |
| Native child process returns 7 | Explicit exit-code test enters catch |
| Existing service query | JSON metadata present; EventLog state agrees with an independent Get-Service call |
| Existing plus nonexistent service | Existing result retained; missing entry Unknown with error; process exit 1 |
| Policy context inspection | Read-only version, identity and policy commands execute |
| Download marker and targeted unblock | Own unsigned temporary probe blocked under child-process RemoteSigned; WhatIf preserves marker; Unblock-File removes it; probe then exits 0; script hash and persistent policies unchanged |

The AI reference script was exercised with the service-name list replaced by EventLog and a uniquely named nonexistent service. No service was started, stopped or modified. Its default three-service list is illustrative and may contain services absent from a particular machine. The stopped-service case is a described rule, not an induced outage.

Only the dedicated temporary probe was unblocked. `Set-ExecutionPolicy -Scope CurrentUser` in the article was parsed but **not executed**. The test's `RemoteSigned` setting applied only to a child process; no persistent policy was changed. Files remained within the task's ignored `.tmp/seo-november-content` directory.

Not executed: PowerShell 7, domain Group Policy changes, service-account execution of these new examples, remote NodePilot execution, a model invocation, model-generated workflow creation, or a real application outage. Product behavior was checked against `docs/ai-features.md`, `RunScriptActivity.cs` and its PowerShell wrapper; PowerShell semantics were checked against the Microsoft references linked in each article. Existing gMSA laboratory evidence is separate: [gMSA validation](gmsa-content-validation.md).

## Website and release preparation

- `npx vitest run src/site/blog-build.test.mjs src/site/prerender.test.ts src/site/seo.test.ts`: **34 passed**, including production/preview boundaries, metadata, multilingual rendering and internal article targets. The existing build test now checks article-body links for unknown or unpublished article destinations in production.
- `npx tsc -b`: passed.
- `npm run lint:ci`: passed without warnings.
- Existing Playwright SEO tests selected by `every page contains|new pages and third article`: **4 passed**. Fresh site and documentation builds completed; DE/EN content, canonical/hreflang metadata without JavaScript and layouts at 390/1440 pixels were checked across all articles.
- Catalog: 21 articles, eight published and 13 drafts; new titles are at most 60 characters and descriptions 120–160 characters. The gMSA diagnosis remains a draft for the separate 08.11. preparation.
- Publication patch dry run passed with `git apply --check --ignore-space-change`; the patch remains unapplied. The candidate has 12 published articles (24 language pages), and 28 internal article links resolve to articles included in that candidate. The 08.11. gMSA diagnosis remains excluded.

Existing Vite configuration-loader/chunk-size warnings did not fail the builds. No production HTTP requests, publishing, paid keyword calls or repository uploads were part of this preparation.

Raw local evidence: `.tmp/seo-november-content/results.json`, `release-check.json`, `release-files.json`. The temporary validators extract the actual article snippets; this record is the durable summary.
