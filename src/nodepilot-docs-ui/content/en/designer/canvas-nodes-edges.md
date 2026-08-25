# Canvas, nodes & edges

The canvas is the workflow designer's working surface. Nodes represent triggers and activities. Edges connect the nodes and determine the execution path.

To make changes, the workflow has to be locked with **Edit**.

## Working with the canvas

| Action | How |
|---|---|
| Pan the view | Hold the middle or right mouse button and drag |
| Zoom | Mouse wheel |
| Show the whole workflow | `Home` |
| Toggle full screen | `F11` |
| Move the selection | Drag the nodes with the mouse |

The minimap shows the current position within the workflow. **Auto-layout** arranges the nodes automatically.

## Adding a node

1. Choose the trigger or activity type you want in the node library.
2. Drag the entry onto the canvas.
3. Select the node.
4. Enter its settings in the properties panel.

The most important node types:

| Node type | Purpose |
|---|---|
| **Trigger** | Starts the workflow |
| **Activity** | Performs a work step |
| **Group** | Groups nodes visually only |
| **Sticky note** | Adds a note and is not executed |

## Editing nodes

- Select a node to open its settings.
- Select several nodes with `Ctrl` or `Shift`.
- `Ctrl+D` duplicates the selection.
- `Delete` or `Backspace` deletes the selection.
- `Ctrl+C` and `Ctrl+V` use an in-memory buffer within the current editor tab only. It can still be used when switching directly to another workflow in the same mounted editor; a reload, closing the tab or unmounting the editor clears it. No workflow data is written to `sessionStorage`.
- With several nodes selected, shared values such as machine, timeout or enabled state can be changed together.

## Connecting nodes

1. Drag from a node's output port.
2. Drop the connection on the target node's input port.
3. Select the edge to set a label or a condition.

The arrow direction shows the direction of execution. An edge can run always, only on success, only on failure, or based on a condition of your own. Details are in [Edge conditions](../concepts/edge-conditions). An edge that runs always carries no label — only a condition, or a label you write yourself, is shown on the canvas.

An edge's plus symbol inserts a new activity between two existing nodes.

An existing edge can be re-routed to a different target without recreating it: either drag its target end onto the new node, or — more convenient on large graphs — right-click the edge, choose **Detach target**, and then click the new target node. The preview line continuously shows which of the four connection points it will land on: whichever one is nearest to the click. This includes the node the edge is already attached to — clicking it again simply moves the connection point. Label, condition and the disabled state move along; Esc or a click on empty canvas cancels.

## State during an execution

| State | Appearance |
|---|---|
| Running | Animated highlight |
| Succeeded | Green |
| Failed | Red |
| Skipped | Grey and dashed |
| Paused | Orange |

Disabled nodes and edges are dimmed and are not used during execution.

## Adjusting the view

The view options provide the following aids when needed:

- Grid and snap-to-grid
- Different node representations and sizes
- Automatic layout directions
- Machine colours
- Error and execution coverage
- The critical path
- Data flow on edges

These options do not change the execution logic. Auto-layout does, however, move the stored node positions.

## Important keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Z` | Undo |
| `Ctrl+Y` | Redo |
| `Ctrl+A` | Select all nodes |
| `Ctrl+C` / `Ctrl+V` | Copy / paste |
| `Ctrl+D` | Duplicate |
| `Delete` | Delete |
| `Home` | Show the whole workflow |

Further settings and testing functions are under [Properties, modes & shortcuts](./properties-modes).
