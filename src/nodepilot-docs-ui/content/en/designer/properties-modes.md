# Properties, modes and keyboard shortcuts

The properties panel contains the settings of the selected node or the selected connection. Changes are only possible with write permission and an active edit lock.

## Editing properties

After selecting a node, the panel shows the fields available for that activity type. Frequently used settings are:

- **Label:** the visible name of the step
- **Output variable:** the name used to access the result in subsequent steps
- **Description:** an optional explanation of the step
- **Target machine and credentials:** the execution target for remote activities
- **Timeout:** the maximum runtime of the step
- **Disabled:** skips the step during execution
- **Breakpoint:** pauses a debug run before this step; expert mode only

Mandatory fields are marked in the panel. The available fields differ per activity. A complete overview is in the [Activity reference](../activities-reference).

After selecting a connection, you can change the label, condition and appearance among other things. Further information is in [Edge conditions](../concepts/edge-conditions).

## Standard and expert mode

**Standard mode** contains the functions for creating, configuring, testing and publishing workflows.

**Expert mode** adds functions for larger workflows:

- Breakpoints and debug runs
- Simulation and version comparison
- Find and replace
- Grouping and precise positioning of nodes
- Additional view and appearance options
- JSON export and extended navigation

The mode can be switched in the designer. Existing workflow data is not changed by it.

## Using variables

Variables carry values between triggers and activities. Typing `{{` opens the variable picker.

| Input | Function |
|---|---|
| `↑` / `↓` | Select an entry |
| `Enter` / `Tab` | Accept the selection |
| `Esc` | Close the picker |

Available values come from:

- The outputs of previous steps, for example `{{script.output}}`
- Global variables, for example `{{globals.API_URL}}`
- The inputs of a manual trigger, for example `{{manual.customerId}}`

A field can contain a fixed value, a variable, or a combination of both. Which values are available in a given place is described in [Data bus and variables](../concepts/data-bus).

## Configuring triggers

Triggers are also configured through the properties panel.

| Trigger | Key settings |
|---|---|
| Manual trigger | Title, description and input parameters |
| Schedule | A cron expression or template, and a description |
| Webhook | HTTP method, path and an optional secret |
| File watcher | Directory, file filter, event type and subdirectories |
| Database | Connection, polling interval and query |
| Windows event log | Log, event type, source, event ID and look-back period |

## Checking and running

Several levels are available for checking a workflow:

- **Step test:** runs only the selected step with test data.
- **Test run:** runs the workflow as a test. Parameters of a manual trigger are requested before the start.
- **Debug run:** runs the workflow with breakpoints; expert mode only.
- **Simulation:** shows the possible flow without executing activities; expert mode only.
- **Lint:** shows missing mandatory values, unreachable nodes and other problems.

Errors from the lint check prevent publishing. Warnings have to be acknowledged before publishing.

Unsaved changes are saved before a test or debug run. A running test can be cancelled.

## Monitoring a run

The execution panel contains:

- **Live:** the current state and output of the running steps
- **History:** past executions
- **Output:** the available trigger, variable and step data
- **Watch:** watched expressions; expert mode only

At a breakpoint, the debug run pauses before the step concerned. The following actions are then available:

- **Continue:** resume execution until the next breakpoint
- **Step over:** execute exactly one step and pause again
- **Stop:** end the execution

## Saving, publishing and exporting

Changes are saved automatically as a draft after a short editing pause. `Ctrl+S` saves the draft immediately.

**Publishing** promotes the current draft to the executable version. Depending on the workflow's state, the same action can enable or disable the workflow. Earlier versions remain available through the version history.

The workflow can be exported as JSON. A PNG file captures the current canvas view.

## Keyboard shortcuts

`Ctrl` corresponds to `Cmd` on macOS. The built-in quick reference opens with `?`.

### Standard mode

| Shortcut | Function |
|---|---|
| `?` | Show or hide the quick reference |
| `Esc` | Close an open window or overlay |
| `Home` | Fit the whole workflow into the view |
| `F11` | Toggle designer full screen |
| `Ctrl+P` | Open the workflow quick switcher |
| `Ctrl+Shift+P` | Open the command palette |
| `Ctrl+F` | Find nodes |
| `Ctrl+S` | Save the draft |
| `Ctrl+Shift+S` | Publish, enable or disable |
| `Ctrl+E` | Request the edit lock |
| `Ctrl+U` | Release the edit lock |
| `Ctrl+Enter` | Start a test run |
| `Ctrl+Shift+X` | Cancel the running execution |
| `Ctrl+Z` | Undo a change |
| `Ctrl+Y` / `Ctrl+Shift+Z` | Redo a change |
| `Ctrl+A` | Select all elements |
| `Ctrl+C` | Copy the selection |
| `Ctrl+V` | Paste the copied elements |
| `Ctrl+D` | Duplicate the selection |
| `Delete` / `Backspace` | Delete the selection |
| `Ctrl+Shift+T` | Lay out the workflow automatically |
| `Ctrl+Shift+L` | Show or hide the lint panel |
| `Ctrl+Alt+P` | Export the canvas as PNG |

### Expert mode

The following shortcuts add to those of standard mode.

| Shortcut | Function |
|---|---|
| `Ctrl+Shift+Enter` | Start a debug run |
| `Ctrl+Shift+U` | Break someone else's edit lock; requires administrator rights |
| `Ctrl+G` | Group the selected nodes |
| `Ctrl+H` | Open find and replace |
| `Tab` / `Shift+Tab` | Select the next or previous connected node |
| `Arrow keys` | Move the selected nodes by 10 pixels |
| `Shift+arrow keys` | Move the selected nodes by 1 pixel |
| `Ctrl+Shift+E` | Fit the selection into the view |
| `Ctrl+Shift+O` | Restore the original layout |
| `Ctrl+Shift+D` | Open a comparison with a version |
| `Ctrl+Shift+R` | Start or reset the simulation |
| `Ctrl+Alt+X` | Reset the activity filter |
| `A` | Toggle edge animation |
| `R` | Change the edge routing |
| `M` | Colour nodes by machine |
| `H` | Toggle the error heatmap |
| `C` | Show or hide the critical path |
| `G` | Toggle snap-to-grid |
| `D` | Enable or disable the selected node |
| `B` | Toggle a breakpoint on the selected node |
| `Ctrl+Shift+N` | Change the node representation |
| `Ctrl+]` / `Ctrl+[` | Increase or decrease the edge width |
| `Ctrl+Shift+>` / `Ctrl+Shift+<` | Increase or decrease the node size |
| `Ctrl+Alt+.` / `Ctrl+Alt+,` | Increase or decrease the label font size |
| `Ctrl+Shift+J` | Export the workflow as JSON |
| `Ctrl+Shift+1` | Open workflows |
| `Ctrl+Shift+2` | Open executions |
| `Ctrl+Shift+3` | Open machines |
| `Ctrl+Shift+4` | Open global variables |
| `Ctrl+Shift+5` | Open the audit log |

### Script editor

These shortcuts apply in the open script editor.

| Shortcut | Function |
|---|---|
| `Ctrl+S` | Accept the current content |
| `Ctrl+F` | Find text |
| `Ctrl+H` | Find and replace text |
| `Ctrl+G` | Go to a line |
| `Ctrl+/` | Comment or uncomment a line |
| `Ctrl+Space` | Open autocomplete |
| `Esc` | Close the script editor |

### Canvas gestures

| Gesture | Function |
|---|---|
| Drag on an empty area with the left mouse button | Draw a selection rectangle |
| Drag with the middle or right mouse button | Pan the canvas |
| `Shift` + click | Add an element to or remove it from the selection |
