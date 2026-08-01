# Continuous Test (1 minute)

This package installs ten visible, enabled Workflow orchestrators in the root of the
NodePilot Workflow list. They fire together every minute.

Each orchestrator performs a local `generateText`, `runScript`, and JSON/XML query,
then starts three existing activity-test Workflows in parallel. The child calls are
fire-and-forget so a slow external integration cannot block the next cadence. After
dispatch, a 20-second `delay` keeps every parent Workflow Execution active for at
least 20 seconds — comfortably inside the one-minute window, so consecutive cadences
never overlap at the parent. One cadence therefore creates:

- 10 parent Workflow Executions
- 30 direct child Workflow Executions

The 30 calls cover the existing safe activity variants from
`scripts/muster-einzeltests.json`, plus the Variable-Pipe and Sub-Workflow topology
tests from `scripts/muster-kombinationen.json`. Destructive activity variants remain
disabled inside those existing test Workflows.

## What this package does NOT cover

The five background triggers (`scheduleTrigger`, `webhookTrigger`, `databaseTrigger`,
`fileWatcherTrigger`, `eventLogTrigger`) have their own `Test — <trigger>` Workflows but
cannot be driven from here: `startWorkflow` starts a child through the engine, which is
exactly the path a trigger does not take. They are exercised by enabling them and letting
their own source fire.

## Prerequisites

Import these packages first if their Workflows are not already visible in the UI:

- `scripts/muster-einzeltests.json`
- `scripts/muster-kombinationen.json`

The installer verifies every referenced Workflow name before changing anything.

**Every referenced target Workflow must be enabled.** `startWorkflow` fails a step when the
child is disabled, so a single disabled activity test turns its orchestrator red every minute.

## Build and install

```powershell
./scripts/continuous-test-1min/Build-ContinuousTest1MinBundle.ps1
./scripts/continuous-test-1min/Test-ContinuousTest1MinBundle.ps1
./scripts/continuous-test-1min/Install-ContinuousTest1Min.ps1 -Password '<admin-password>'
```

Both scripts are idempotent. The installer creates missing orchestrators and publishes
updates to existing ones. Explicit UTF-8 request bodies preserve Unicode Workflow
names when the installer runs under Windows PowerShell 5.1.

`Test-ContinuousTest1MinBundle.ps1` also asserts the cadence itself: every orchestrator must
ship enabled and on `0 0/1 * * * ? *`. An orchestrator that is disabled or on a slower cron
silently stops driving its three activity tests, and nothing in the UI surfaces that.

Failures in external activity tests are intentional observability signals. For example,
the email test fails when SMTP is not configured; this does not fail or delay its parent
orchestrator.
