# Getting started with example workflows

These examples run on **localhost**, meaning the NodePilot server itself.
They require no registered target machines, additional credentials, global variables,
external services, email configuration, or AI configuration.

| Example | Nodes¹ | Different node types | PowerShell nodes | Demonstrates |
|---|---:|---:|---:|---|
| [01 - Server Overview](01-server-overview.workflow.json) | 5 | 5 | 0 | Read Windows details using WMI and the Event Log service status, log and return findings |
| [02 - Event Log Review](02-event-log-review.workflow.json) | 10 | 8 | 1 | Group events, query JSON, branch on a threshold, and join the selected branch |
| [03 - Diagnostic Package](03-diagnostic-package.workflow.json) | 18 | 11 | 3 | Run WMI queries in parallel, write files, create a ZIP archive, extract and verify its contents |
| [04 - System Health Observatory](04-system-health-observatory.workflow.json) | 45 | 16 | 6 | Five parallel system checks, threshold-based assessment, JSON/XML queries, and a verified report archive |

¹ Includes the manual trigger and excludes sticky notes and visual groups. Examples
01–03 introduce the basics; Example 04 is a larger canvas demonstration.

## Import and run

1. Download one of the linked JSON files. Start with Example 01.
2. In NodePilot, open **Workflows → Import** and select the file.
3. Open the imported workflow. The sticky notes describe its behavior.
4. Enable the workflow and choose **Run**. The default inputs work without changes.
5. Inspect individual node outputs and the final Return node in the execution.

Imported workflows are initially disabled. Each example has only a manual trigger and
allows one execution at a time. All operations use the NodePilot service account:
it needs permission to query local WMI, read the selected Windows event log, and,
for the ZIP example, write to its own temporary directory.

## Event Log Review inputs

| Input | Default | Meaning |
|---|---|---|
| `logName` | `System` | `System` or `Application` |
| `hours` | `24` | Last 1-168 hours |
| `maxEvents` | `100` | Newest 1-500 matching events |
| `threshold` | `1` | Event count that selects the review branch; accepts 1-501 |

The workflow reads **errors and critical events**, excluding warnings. It returns the
five most frequent combinations of provider and event ID, their counts, and their
latest timestamps in UTC. These are findings, not an automated root cause diagnosis
or repair.

No matching events is a valid result. If `truncated=True`, additional events were
available: counts, groups, and the threshold comparison describe only the limited
selection. Set `threshold=501` to try the below-threshold branch. The review branch
requires matching events. The unused Log node is expected to be skipped.

## Diagnostic Package output

Each execution creates a new `NodePilot-Demo-<ID>` directory under the service account
temporary directory. It contains two text files, `ServerDiagnostics.zip`, and an
extracted verification copy. The workflow verifies that the archive contains exactly
the two expected files and that their SHA256 hashes match the originals.

The `folder` and `archive` outputs contain paths **on the server**. The `sha256` output
contains the archive checksum, and `verified` reports successful content verification.
Files remain available for inspection. The returned output directory can be removed
manually afterwards. The workflow does not change services or clean up unrelated files.

Stopped auto-start services are findings: trigger-start and delayed-start services may
be stopped by design. Disk `Size` and `FreeSpace` values are reported in bytes.

## Validation on CM1

Tested on October 8, 2026 using `localhost` on the installed NodePilot instance:
import and activation, all three default runs, both event log branches, empty results,
a one-event limit, and ZIP content verification. Default runs and additional positive
event log cases completed with `Succeeded`.

Example 04 was also tested on CM1: default execution, the Review branch with
`errorThreshold=0`, a one-event-per-log limit, and invalid input rejection. Successful
executions verified all three archived files against their originals. An invalid
`eventHours=0` fails at input validation and skips collection and file creation.

## System Health Observatory

Five parallel lanes collect Windows and memory information, system volume capacity,
core service states, network and restart indicators, and System/Application error
events. Seven colored groups separate collection, assessment, and report packaging.
The report lane follows the arrows from right to left.

| Input | Default | Accepted values |
|---|---|---|
| `eventHours` | `24` | 1–168 hours |
| `eventLimit` | `100` | 1–500 newest matching records per log |
| `errorThreshold` | `10` | 0–1001; 0 always selects Review |
| `minimumFreeMemory` | `10` | 1–99 percent |
| `minimumFreeDisk` | `15` | 1–99 percent |

Review is selected for low available memory or system disk space, an error count
at or above the threshold, any critical event, a stopped checked service, a detected
restart flag, no IP-enabled network adapter, or a truncated event selection. Clear
means none of these conditions matched. It is a local snapshot, not a full health
certification or root cause diagnosis. Stopped automatic services are informational;
restart detection covers servicing and Windows Update, and firewall service status
does not validate firewall rules or connectivity.

Each execution writes `SystemReport.txt`, `Findings.json`, and `Readme.txt` to a new
`NodePilot-SystemCheck-<ID>` folder in the service account's temporary directory.
It creates `SystemCheck.zip`, calculates its SHA256 hash, extracts it, and verifies
all three files against their originals. Return values include the outcome, findings
count, report, server-side output paths, archive hash, and verification status.
The output folder remains available and may be removed manually after review.
Collection is read-only; the workflow does not change services or configuration.

The `agent-skills` directory and `agent-diagnosis-result.schema.json` are separate
resources for advanced agent workflows. The numbered examples need neither.
