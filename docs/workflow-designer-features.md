# Workflow Designer

The designer creates, edits and runs workflows on a canvas. Standard mode exposes the
main controls, while Expert mode adds debugging, bulk editing and analysis overlays.
The toolbar switches between these modes and offers compact or classic layouts.

## Editing and publishing

Editing requires a role with write permission and the workflow's edit lock.
Acquiring the lock disables the workflow. Saving retains the edit lock, while publishing
saves, enables and releases it. Releasing the lock alone leaves the workflow disabled.
Admins can force-release another user's lock.

Changes are saved automatically after 5 seconds without further edits when write
permission is available. Manual save and a warning before leaving with unsaved changes
remain available. If the workflow was saved elsewhere in the meantime, the save is rejected:
the draft stays on the canvas, autosave stops and a reload shows the current version.
Lint errors block publishing, while warnings require confirmation.
The lint panel links each finding to the affected node or edge.

## Canvas and workflow structure

Activities can be added from the searchable library by clicking or dragging. Dropping
a connection on empty canvas opens an activity picker. An existing connection also
offers an insert button. The library includes triggers, control flow, enabled custom
activities and reusable snippets.

Nodes support selection, duplication, copy/paste and undo/redo. Groups move their members
together and can be collapsed. Sticky notes provide annotations. Groups and notes do
not execute. Expert mode adds bulk editing and configuration copying between activities
of the same type.

Copy/paste transfers workflow JSON, including connections between selected nodes.
Pasting assigns new IDs and updates their references. Named secret fields are masked
and need to be supplied again, while scripts and other free text are copied as shown.
Copying remains available in read-only mode.

Connections support labels, conditions and manual reshaping. Their target can be
detached and reattached without losing the condition or label. Multiple incoming
branches require a Junction. When a second branch is connected to another activity,
the designer offers to insert a `waitAll` Junction.

Conditions include success, failure and unconditional execution. Expert mode adds a
visual expression builder with comparisons, AND/OR groups and NOT. A disabled edge
does not turn its target into an entry point.

## Properties and variables

The properties panel contains the selected activity's configuration, target machine,
credential and supported timeout or retry settings. Required fields are marked.
Remote activities provide a connection test. Double-clicking a node opens quick editing,
while script activities open the script editor with variable selection and step testing.

Typing `{{` offers variables from ancestor steps, globals and trigger inputs:

| Expression | Value |
|---|---|
| `{{step.output}}` | Standard output. |
| `{{step.error}}` | Error output. |
| `{{step.success}}` | Step success. |
| `{{step.param.name}}` | A published output parameter. |
| `{{globals.NAME}}` | Global variable. |
| `{{manual.NAME}}` | Manual input or trigger data. |

An activity's output-variable name can replace its step ID. Outputs from unrelated
parallel branches are unavailable. Where several activities publish the same name,
the reference must identify its owner, such as `{{stepA.param.name}}`.
Sub-workflows with known contracts show typed input mappings and their outputs.

Field details are covered by the [activity reference](../src/nodepilot-docs-ui/content/en/activities-reference.md)
and [trigger reference](../src/nodepilot-docs-ui/content/en/triggers.md).
[Custom activities](custom-activities.md) and [AI agents](ai-agents.md) have separate guides.

## Running and debugging

Run starts the workflow or opens the input dialog when the manual trigger declares
parameters. With write permission, pending changes are saved first. Without the edit
lock, the run uses the saved definition and its input parameters. An execution started
from the designer is pinned to the canvas so its progress remains visible.

Expert mode provides debug runs with optional conditional breakpoints. At a breakpoint,
the variable inspector supports overrides before continuing, skipping the paused step
or stopping the execution. A step test executes a single activity using empty context,
previous results or manually supplied values. Simulation checks the control-flow path
without executing activities.

Live monitoring and history provide the following views:

| View | Content |
|---|---|
| Live | Active runs, progress and step details, including output, errors and duration. |
| History | Previous runs with step details, a timeline or Gantt chart, and replay. |
| Output | Data-bus values grouped by source, with filtering and copying. |
| Watch, Expert mode | Selected expressions resolved against the live data bus. |

Node status, health indicators and execution paths appear on the canvas. AI team nodes
show their members and live status. Their run details include the report and journal.

## Navigation and display

The workflow browser supports folders, trigger grouping and search. Folder breadcrumbs
and sub-workflow previews allow navigation between related workflows. Search locates
nodes, while the command palette lists available actions. Expert mode adds find and
replace across labels and configuration values.

Display options cover node styles and sizes, connection routing, grid snapping and
automatic layout. Expert overlays show target machines, failure rates, execution
coverage, variable flow and the critical path. On phones and portrait tablets, the mobile view
provides a read-only graph with live status, pan and zoom. A desktop editor that is already
open stays mounted when the window narrows, so its unsaved draft is kept.

## Versions, export and AI

Version history compares changes to nodes and connections. Restoring a version requires
write permission and the edit lock. Workflows can be exported as JSON or as a PNG of
the visible canvas.

The workflow assistant explains the current workflow and proposes changes. All roles
may ask questions, while Admins and Operators can apply proposals with write permission.
Applying a proposal changes the canvas, followed by the normal save and publish process.
The script editor also supports AI generation. Configuration and data sharing are
described under [AI features](ai-features.md).

## Common shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+S` | Save. |
| `Ctrl+E` / `Ctrl+U` | Acquire or release the edit lock. Releasing asks before it discards unsaved changes. |
| `Ctrl+Shift+S` | Publish, enable or disable, depending on state. |
| `Ctrl+Enter` | Run. |
| `Ctrl+Shift+Enter` | Debug run, Expert mode. |
| `Ctrl+Shift+X` | Cancel run. |
| `Ctrl+F` / `Ctrl+Shift+P` | Search nodes or open the command palette. |
| `Ctrl+P` | Switch workflows. |
| `Ctrl+C` / `Ctrl+V` / `Ctrl+D` | Copy, paste or duplicate. |
| `Ctrl+Z` / `Ctrl+Y` | Undo or redo. |
| `Ctrl+Shift+T` | Tidy the layout. |
| `Ctrl+Shift+L` | Open lint findings. |
| `Home` / `F11` | Fit all nodes or toggle fullscreen. |

Left-drag selects an area, middle/right-drag pans, and Shift-click extends the selection.
The help overlay provides the full shortcut reference.
