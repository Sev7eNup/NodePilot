# Designer Back Button & Browser Navigation — Konsistenz-Fix

## Problem

Zwei unabhängige "Zurück"-Mechanismen driften auseinander:
- **Designer-Zurück-Button**: Custom LIFO-Stack (`useWorkflowHistoryStore`, sessionStorage, max 20) + Dirty-Guard
- **Browser-Zurück**: Kein Dirty-Guard, navigiert auf `window.history` — kein `useBlocker`

Konkrete Folgen:
1. Browser-Zurück verliert ungespeicherte Änderungen ohne Warnung
2. Custom-Stack und Browser-History divergieren (z.B. WorkflowsPage-Einstieg erzeugt keinen Stack-Eintrag)
3. Designer-Zurück pusht via `navigate(path)` einen **neuen** History-Eintrag statt zurückzugehen — Browser-Stack wächst statt zu schrumpfen

## Lösung

Custom LIFO-Stack ersetzen durch React Router `location.state`. Dirty-Guard via `useBlocker`.

## Schritte

### Schritt 1: `useBlocker` für Dirty-Guard auf Browser-Navigation

- `WorkflowEditorPage`: `useBlocker` aktivieren wenn `isDirty === true`
- Bestätigungsdialog analog `confirmDiscardIfDirty`
- `beforeunload` bleibt für Tab-Schließen/Refresh (kann `useBlocker` nicht abfangen)
- **Standalone deploybar** — ändert kein bestehendes Verhalten außer dass Browser-Zurück jetzt warnt

### Schritt 2: `useWorkflowHistoryStore` komplett entfernen

- Store-File `workflowHistoryStore.ts` löschen
- Alle Importe und Verwendungen entfernen

### Schritt 3: Alle `navigate('/workflows/:id')`-Stellen bekommen `state: { fromWorkflow }`

Betroffene Stellen:
- `WorkflowEditorPage` — Sidebar-Open (`onOpenWorkflow`)
- `WorkflowEditorPage` — Sub-Workflow-Preview (`onOpenInEditor`)
- `WorkflowQuickSwitcher` — Cmd+K
- `WorkflowsPage` — Workflow-Liste → Editor
- `DashboardPage` — Links → Editor

Jeweils: `navigate(`/workflows/${id}`, { state: { fromWorkflow: { id: currentId, name: currentName } } })`

### Schritt 4: Designer-Zurück-Button umstellen

- `EditorHeader`: Stack-Referenzen entfernen
- `handleBack` → `navigate(-1)` statt `navigate(path)`
- Tooltip: `location.state?.fromWorkflow?.name` statt Stack-Eintrag
- Disabled-State: kein `fromWorkflow` im `location.state` → Button deaktiviert oder mit generischem "Zurück zur Liste"

### Schritt 5: Cleanup

- `workflowHistoryStore.ts` löschen
- Ungenutzte Imports entfernen
- `confirmDiscardIfDirty` bleibt bestehen (wird weiterhin für in-App-Nav wie Sidebar genutzt)
- Tests prüfen/ergänzen

## Dateien

| Datei | Änderung |
|---|---|
| `src/nodepilot-ui/src/stores/workflowHistoryStore.ts` | Löschen |
| `src/nodepilot-ui/src/components/designer/EditorHeader.tsx` | Stack-Referenzen → `location.state` + `navigate(-1)` |
| `src/nodepilot-ui/src/pages/WorkflowEditorPage.tsx` | `useBlocker` + `state: { fromWorkflow }` bei Sidebar/Sub-Workflow |
| `src/nodepilot-ui/src/components/designer/WorkflowQuickSwitcher.tsx` | `state: { fromWorkflow }` bei Navigation |
| `src/nodepilot-ui/src/pages/WorkflowsPage.tsx` | `state: { fromWorkflow }` bei Navigation |
| `src/nodepilot-ui/src/pages/DashboardPage.tsx` | `state: { fromWorkflow }` bei Navigation |