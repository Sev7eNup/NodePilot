# Continuous Test (2 minutes)

This package installs ten visible, enabled Workflow orchestrators in the root of the
NodePilot Workflow list. They fire together every two minutes.

Each orchestrator performs a local `generateText`, `runScript`, and JSON/XML query,
then starts three existing activity-test Workflows in parallel. The child calls are
fire-and-forget so a slow external integration cannot block the next cadence. After
dispatch, a 20-second `delay` keeps every parent Workflow Execution active for at
least 20 seconds. One cadence therefore creates:

- 10 parent Workflow Executions
- 30 direct child Workflow Executions

The 30 calls cover the existing safe activity variants from
`scripts/muster-einzeltests.json`, plus the Variable-Pipe and Sub-Workflow topology
tests from `scripts/muster-kombinationen.json`. Destructive activity variants remain
disabled inside those existing test Workflows.

## Prerequisites

Import these packages first if their Workflows are not already visible in the UI:

- `scripts/muster-einzeltests.json`
- `scripts/muster-kombinationen.json`

The installer verifies every referenced Workflow name before changing anything.

## Build and install

```powershell
./scripts/continuous-test-2min/Build-ContinuousTest2MinBundle.ps1
./scripts/continuous-test-2min/Test-ContinuousTest2MinBundle.ps1
./scripts/continuous-test-2min/Install-ContinuousTest2Min.ps1 -Password '<admin-password>'
```

Both scripts are idempotent. The installer creates missing orchestrators and publishes
updates to existing ones. Explicit UTF-8 request bodies preserve Unicode Workflow
names when the installer runs under Windows PowerShell 5.1.

Failures in external activity tests are intentional observability signals. For example,
the email test fails when SMTP is not configured; this does not fail or delay its parent
orchestrator.
