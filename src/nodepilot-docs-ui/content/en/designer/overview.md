# Designer — overview

The workflow designer shows triggers and activities as nodes on a working surface. Edges connect the nodes and determine the execution path. The implementation is in `src/nodepilot-ui` and uses React Flow.

## Two modes

| Mode | Focus |
|---|---|
| **Standard** | Core functions, automatic left-to-right layout and a reduced toolbar |
| **Expert** | The full toolbar, overlays and additional keyboard shortcuts |

Features that are only visible in expert mode are marked **(Expert)** on the following pages.

## Main parts

- **Canvas** with pan/zoom, minimap, background variants and viewport virtualization.
- **Node library / palette** on the left (categories: triggers, actions, control flow, logic, annotations + snippets).
- **Workflow browser** on the left (folder view or trigger view, search/filter, info card).
- **Properties panel** on the right (context-sensitive per selected node/edge). The AI assistant shares this area: it overlays the properties and gives way again as soon as something is selected on the canvas.
- **Header/toolbar** with seven clusters (history, layout, inspect, view, run, lifecycle, export) + an AI assistant button + a **colour skin switcher** (palette icon — a popover with 7 skins + `system`, in sync with the settings).
- **Live execution panel** at the bottom (tabs: live / history / output / watch).

## The edit lifecycle in the designer

- `canWrite = roleCanWrite && isLockedByMe` — both conditions are required.
- **Lock** `Ctrl+E` → atomically sets `IsEnabled=false` + the lock fields. 409 if already locked.
- **Unlock** `Ctrl+U` → releases the lock (`IsEnabled` stays as it is).
- **Publish** `Ctrl+Shift+S` → atomically save + enable + unlock (through the pre-publish checklist).
- **Disable/Enable** — the kill switch (disable is **not** lock-gated).
- **Force unlock** (admin) `Ctrl+Shift+U` — breaks someone else's lock.

Without `canWrite`, dragging, connecting, deleting, context menus and inline edits are disabled (read-only).

## Where to go next

- [Canvas, nodes & edges](./canvas-nodes-edges) — viewport, node types, edge routing, overlays.
- [Properties, modes & shortcuts](./properties-modes) — the properties panel, variable autocomplete, configurations, keyboard shortcuts.
