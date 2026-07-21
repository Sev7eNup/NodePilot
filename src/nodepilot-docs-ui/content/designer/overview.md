# Designer — Überblick

Der NodePilot Workflow-Designer ist eine React-SPA (`src/nodepilot-ui`), gebaut auf @xyflow/react (React Flow v12). Workflows werden auf einer Canvas aus Nodes und Edges visuell zusammengesetzt.

## Zwei Modi

| Modus | Fokus |
|---|---|
| **Standard** | Reduzierter Feature-Set, einsteigerfreundlich. LR-Auto-Layout, wesentliche Toolbar. |
| **Expert** | Volle Toolbar, alle Overlays, Single-Key-Shortcuts. Aktivierbar über `designerMode` im `designStore`. |

Features, die nur im Expert-Modus sichtbar sind, sind in den folgenden Seiten mit **(Expert)** markiert.

## Hauptbestandteile

- **Canvas** mit Pan/Zoom, MiniMap, Background-Varianten, Viewport-Virtualisierung.
- **Node-Library / Palette** links (Kategorien: Triggers, Actions, Control Flow, Logic, Annotations + Snippets).
- **Workflow-Browser** links (Folder-View oder Trigger-View, Search/Filter, Info-Card).
- **Properties-Panel** rechts (kontextsensitiv pro selektiertem Node/Edge).
- **Header/Toolbar** mit sieben Clustern (History, Layout, Inspect, View, Run, Lifecycle, Export) + KI-Assistent-Button + **Farb-Skin-Umschalter** (Palette-Icon — Popover mit 8 Skins + `system`, synchron mit Einstellungen).
- **Live Execution Panel** unten (Tabs Live / History / Output / Watch).

## Edit-Lifecycle im Designer

- `canWrite = roleCanWrite && isLockedByMe` — beide Bedingungen nötig.
- **Lock** `Ctrl+E` → atomar `IsEnabled=false` + Lock-Fields. 409 wenn schon gelockt.
- **Unlock** `Ctrl+U` → Lock freigeben (`IsEnabled` bleibt).
- **Publish** `Ctrl+Shift+S` → atomar Save + Enable + Unlock (durch Pre-Publish-Checklist).
- **Disable/Enable** — Kill-Switch (Disable **nicht** lock-gegated).
- **Force-Unlock** (Admin) `Ctrl+Shift+U` — bricht fremden Lock.

Ohne `canWrite` sind Drag, Connect, Delete, Kontextmenüs und Inline-Edits deaktiviert (Read-Only).

## Wo es weitergeht

- [Canvas, Nodes & Edges](./canvas-nodes-edges) — Viewport, Node-Typen, Edge-Routing, Overlays.
- [Properties, Modi & Shortcuts](./properties-modes) — Properties-Panel, Variablen-Autocomplete, Konfigurationen, Tastenkürzel.
