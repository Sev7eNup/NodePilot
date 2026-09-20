# Live demo

The [live demo](https://sev7enup.github.io/NodePilot/demo/) is the real NodePilot web UI with
sample data, running entirely in your browser. Nothing is installed, no account is created and no
data leaves your machine.

## What you can do

Everything the frontend does on its own works exactly as it does in a real installation:

- **Browse five sample workflows** in three folders: a daily disk-space check, a Windows Update
  health check, a Print Spooler watchdog that restarts the service and escalates if it stays down,
  an SCCM package and AD group provisioning run, and a temp-file cleanup with trigger parameters.
- **Open the designer** and use the full authoring surface: the canvas linter, the variable picker,
  auto-layout, the version diff, the workflow simulation and the pre-publish check. None of these
  need a server even in a real installation — they run in the browser there too.
- **Edit and publish.** Check a workflow out, change it, publish it. The edit lock behaves the way
  it does in the product: checking out disables the workflow, so you publish before you run.
- **Run a workflow** and watch the canvas light up step by step while the live console fills.
- **Create, edit and delete** machines, credentials, global variables and their folders, custom
  activities, users, maintenance windows and folder permissions. Every one of those writes really
  happens — in your tab.
- **Look around the rest of the app** — the execution history, the audit log and the support log are
  all derived from the same sample runs, so no two screens contradict each other.

## What it cannot do

Anything that needs a real server says so rather than pretending, naming the action instead of
failing quietly. That covers running PowerShell on a host, testing a WinRM connection, sending mail,
importing a runbook, exporting or restoring a backup, downloading the support log, running SQL and
the AI assistant.

## Your own copy

The demo keeps its state in the browser tab and nothing else. Two visitors never see each other's
changes, and neither do two tabs of your own browser. A reload restores the sample data, and the
**Reset** button in the demo bar does the same thing.

Ready for the real thing? Start with the [installation](./installation).
