# Introduction

NodePilot is a workflow orchestrator for Windows environments. Processes are modelled graphically and executed automatically. To run steps on remote Windows systems, NodePilot uses WinRM. No additional agent is required on the target systems.

## Basic terms

| Term | Meaning |
|---|---|
| **Workflow** | A complete process consisting of a starting point, work steps and connections |
| **Trigger** | The starting point of a workflow, for example a schedule or a manual start |
| **Activity** | A single work step, for example a PowerShell script or a REST call |
| **Node** | The representation of a trigger or an activity in the designer |
| **Edge** | A connection between two nodes; it can carry a condition |
| **Execution** | A single run of a workflow |

Example: a workflow checks the free disk space on several servers every night. A schedule starts the run. An activity reads the disk space. An edge condition forwards only critical results to an email activity.

## Main components

- **Designer:** graphical creation and editing of workflows.
- **Engine:** execution of activities, parallel paths, retries, timeouts and sub-workflows.
- **Trigger system:** automatic start by schedule, file change, database query, Windows event log or HTTP call.
- **API and CLI:** automation of the management and execution functions over REST or the `np` command-line tool.
- **Database:** storage of workflows, configuration, executions and audit data.

## Execution locations

An activity runs in one of two places:

| Execution location | Examples |
|---|---|
| **The NodePilot host** | REST, SQL, email, conditions, local PowerShell execution |
| **A remote Windows system** | Services, registry, files, WMI and PowerShell over WinRM |

Some activities support both locations. The mapping for every type is under [Activity types and scopes](../concepts/activities).

## Data between steps

Every activity can place a result on the data bus. Later activities access it through variables:

```text
{{hostInfo.output}}    # output of the activity with outputVariable "hostInfo"
{{hostInfo.success}}   # execution status as "true" or "false"
{{globals.NAME}}       # global variable
```

Further rules and examples are in [Data bus and variables](../concepts/data-bus).

## Supported operating modes

NodePilot has three supported operating modes:

| Operating mode | Purpose |
|---|---|
| **Installation from source** | Development and testing from the repository |
| **Windows Server deployment** | Production operation for teams, APIs, webhooks and optionally high availability |
| **Desktop app** | Productive single-machine use on Windows 11, reachable locally only |

The workflow engine is the same in all operating modes. The differences are in installation, network access, service account, database, authentication and high availability. The full comparison is under [Operating modes](../deployment/overview).

## Recommended path in

1. [Choose an operating mode](../deployment/overview).
2. Open [Installation](./installation) and run the variant that fits.
3. [Run your first workflow](./quickstart).
4. Go deeper into [Architecture](./architecture) and [Concepts](../concepts/workflows) as needed.
