# Test reliability and release evidence

## CI measurements

The Playwright shard jobs collect JSON results for the built SPA and browser demo. They
retain compact records in `e2e-reliability-shard-*`; the reporting job publishes the combined
`e2e-reliability` artifact and a GitHub job summary. Records contain test identities, expected
status and attempt statuses only, without error text, stdout, source snippets or attachments.
Artifacts are retained for 30 days.

The summary distinguishes:

- **Unexpected first attempts:** first attempt differs from the declared expected status.
- **Retried cases / additional attempts:** number of affected cases and total retry attempts.
- **Flaky:** Playwright's outcome for a case that reaches its expected result only on retry.
- **Finally unexpected:** cases that still fail after their retry budget.
- **Skipped, runner errors and missing reports:** visible separately, never counted as passes.

Rates use executed test cases as denominator. Expected-failure tests are evaluated against
their expected status. The main suite keeps two CI retries and the demo one; local retries
remain zero. This measures instability without making a failed first attempt disappear.

The reporting job attempts to download records from the latest ten completed push runs on
`main`. It groups by suite, project, file and full test title. At least two distinct flaky runs
establish recurrence. Rerunning a workflow replaces the corresponding run/shard observation;
it does not count as another independent run. A renamed/moved test starts a new identity.
Unavailable, pre-instrumentation or expired artifacts reduce the displayed sample; no sample
means unknown, not stable. The HTML report and retry traces remain the debugging evidence.

Reporting does not change the existing CI gate: terminal test failures still fail their shard.
The report job is informational and never turns a failed test job green. Check its status if
the summary is absent. Once each week, the maintainer reviews recurring entries, inspects the
first failing trace and fixes the cause. Do not increase retries or skip tests to remove an
entry. There is no automatic flake threshold until a representative baseline exists.

## Local reproduction

From `src/nodepilot-ui`, in PowerShell:

```powershell
node --test scripts/test-reliability.test.mjs
$env:PLAYWRIGHT_JSON_OUTPUT_FILE = 'test-results/reliability-ui.json'
npx playwright test e2e/script-editor.spec.ts --reporter=list,json
node scripts/test-reliability.mjs collect test-results/reliability-ui.json reliability-current/ui-1.json ui 1
node scripts/test-reliability.mjs report reliability-current reliability-history reliability-current/summary.md
```

Use an empty `reliability-current` directory for a new local observation. To compare several
local runs, set a distinct `GITHUB_RUN_ID` before each collection and place each record under
its own history directory. Downloaded CI artifacts can be used directly as history. Do not
reuse the same directory for unrelated suites or count synthetic tests as production evidence.

## What green tests establish

| Layer | Establishes | Does not establish |
|---|---|---|
| UI Playwright | Built SPA behaviour against intercepted API responses | Real authentication, database, SignalR delivery, WinRM or production security headers |
| Browser demo | Demo bundle, routing and its in-memory backend | Server deployment or real workflow execution |
| Backend tests | Tested policy, controller, engine and provider cases | All real directory/network/OS configurations |
| Release lab | Signed artifact installation/update/uninstall under the tested identities and DB providers | Untested identities, deployments or complete disaster recovery |
| Live workflow suite | Executed workflows on the hardened lab installation | Correctness of every possible user workflow |

Before a release, keep the existing [release gate](../../RELEASING.md) mandatory: signed
server matrix, desktop matrix and live workflow suite must all pass. Preserve results with
the tested commit/artifact version, scenario matrix, pass/fail/skip counts and evidence paths.
An unavailable scenario is an explicit limitation, not a successful run. The
[release-lab guide](../../scripts/release-lab/README.md) defines its real acceptance criteria.
This change does not run installers, change lab machines or claim a new release acceptance.
