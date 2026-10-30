# E2E Test Catalogue

The catalogue contains 367 manual test cases across 86 areas. The procedures and
verification criteria are unchanged in the linked sub-catalogues. Run only the affected areas
unless a full acceptance run has been ordered.

## Preparation

Check [environment, login and prerequisites](e2e/00-vorbereitung.md) before the run.
The original test descriptions, including steps, verification criteria,
test data and coverage notes, have been carried over in full.

Automated specs and their coverage are listed in the
[E2E README](../../src/nodepilot-ui/e2e/README.md). The complete
final checklist and known limitations are in the
[last sub-catalogue](e2e/81-86.md#checkliste-für-vollständigen-e2e-test-run).

## Part 1: Workflow Management

[Procedure and verification criteria](e2e/01-10.md#teil-1).

## Part 2: Node Management: Activity Types

[Procedure and verification criteria](e2e/01-10.md#teil-2).

## Part 3: Node Operations & Designer Interactions

[Procedure and verification criteria](e2e/01-10.md#teil-3).

## Part 4: Edges (Connections) & Conditions

[Procedure and verification criteria](e2e/01-10.md#teil-4).

## Part 5: Properties Panel & Variables

[Procedure and verification criteria](e2e/01-10.md#teil-5).

## Part 6: Workflow Execution & Debugging

[Procedure and verification criteria](e2e/01-10.md#teil-6).

## Part 7: Error Handling & Edge Cases

[Procedure and verification criteria](e2e/01-10.md#teil-7).

## Part 8: Saving & Persistence

[Procedure and verification criteria](e2e/01-10.md#teil-8).

## Part 9: Special Scenarios

[Procedure and verification criteria](e2e/01-10.md#teil-9).

## Part 10: UI/UX Tests

[Procedure and verification criteria](e2e/01-10.md#teil-10).

## Part 11: Dashboard

[Procedure and verification criteria](e2e/11-20.md#teil-11).

## Part 12: Keyboard Shortcuts & Productivity

[Procedure and verification criteria](e2e/11-20.md#teil-12).

## Part 13: Special Node Types

[Procedure and verification criteria](e2e/11-20.md#teil-13).

## Part 14: All Trigger Types

[Procedure and verification criteria](e2e/11-20.md#teil-14).

## Part 15: Admin & User Management

[Procedure and verification criteria](e2e/11-20.md#teil-15).

## Part 16: Audit Log

[Procedure and verification criteria](e2e/11-20.md#teil-16).

## Part 17: Theme & UX Features

[Procedure and verification criteria](e2e/11-20.md#teil-17).

## Part 18: Workflow Organisation

[Procedure and verification criteria](e2e/11-20.md#teil-18).

## Part 19: Workflow Diff / Version Compare

[Procedure and verification criteria](e2e/11-20.md#teil-19).

## Part 20: Machines & Credentials

[Procedure and verification criteria](e2e/11-20.md#teil-20).

## Part 21: Global Variables

[Procedure and verification criteria](e2e/21-30.md#teil-21).

## Part 22: SCOrch Import (SCORCH-XML)

[Procedure and verification criteria](e2e/21-30.md#teil-22).

## Part 23: External Trigger API & Idempotency

[Procedure and verification criteria](e2e/21-30.md#teil-23).

## Part 24: Real-time SignalR

[Procedure and verification criteria](e2e/21-30.md#teil-24).

## Part 25: Authentication Lifecycle (Bootstrap, JWT, CSRF, Lockout)

[Procedure and verification criteria](e2e/21-30.md#teil-25).

## Part 26: RBAC: Role Crossings

[Procedure and verification criteria](e2e/21-30.md#teil-26).

## Part 27: Edit Lock Lifecycle (4-State Toggle)

[Procedure and verification criteria](e2e/21-30.md#teil-27).

## Part 28: Step Test with Context

[Procedure and verification criteria](e2e/21-30.md#teil-28).

## Part 29: Sub-Workflow Contracts (V1)

[Procedure and verification criteria](e2e/21-30.md#teil-29).

## Part 30: Coverage Heatmap

[Procedure and verification criteria](e2e/21-30.md#teil-30).

## Part 31: DB-Admin Viewer

[Procedure and verification criteria](e2e/31-40.md#teil-31).

## Part 32: AI Features (LLM)

[Procedure and verification criteria](e2e/31-40.md#teil-32).

## Part 33: Hardening Flags

[Procedure and verification criteria](e2e/31-40.md#teil-33).

## Part 34: Migration Drift + Backend Smoke (Backend-only)

[Procedure and verification criteria](e2e/31-40.md#teil-34).

## Part 35: Secrets Re-Encryption (SecretsController)

[Procedure and verification criteria](e2e/31-40.md#teil-35).

## Part 36: Activity Catalog

[Procedure and verification criteria](e2e/31-40.md#teil-36).

## Part 37: Diagnostics / Support Log

[Procedure and verification criteria](e2e/31-40.md#teil-37).

## Part 38: Admin Settings

[Procedure and verification criteria](e2e/31-40.md#teil-38).

## Part 39: Debug: Variable Overrides

[Procedure and verification criteria](e2e/31-40.md#teil-39).

## Part 40: Edit Lock: Force-Unlock Audit

[Procedure and verification criteria](e2e/31-40.md#teil-40).

## Part 41: Rollback with Reason

[Procedure and verification criteria](e2e/41-50.md#teil-41).

## Part 42: Coverage Heatmap: Extended Parameters

[Procedure and verification criteria](e2e/41-50.md#teil-42).

## Part 43: Observability Query Endpoints

[Procedure and verification criteria](e2e/41-50.md#teil-43).

## Part 44: Rate-Limiting Headers

[Procedure and verification criteria](e2e/41-50.md#teil-44).

## Part 45: Shared Folder Permissions: Grant / Revoke

[Procedure and verification criteria](e2e/41-50.md#teil-45).

## Part 46: Unresolved Template Variables (Failure Behaviour)

[Procedure and verification criteria](e2e/41-50.md#teil-46).

## Part 47: External Trigger: Idempotency Key TTL

[Procedure and verification criteria](e2e/41-50.md#teil-47).

## Part 48: Output Redaction: Viewer Perspective

[Procedure and verification criteria](e2e/41-50.md#teil-48).

## Part 49: Execution Retry & Cancel-All

[Procedure and verification criteria](e2e/41-50.md#teil-49).

## Part 50: Workflow Duplicate, By-Name Lookup & Bulk Export

[Procedure and verification criteria](e2e/41-50.md#teil-50).

## Part 51: Step Stats & Step Health

[Procedure and verification criteria](e2e/51-60.md#teil-51).

## Part 52: Folder Move

[Procedure and verification criteria](e2e/51-60.md#teil-52).

## Part 53: Schedule Next Fires & AI Generate Workflow

[Procedure and verification criteria](e2e/51-60.md#teil-53).

## Part 54: Designer Overlays: Command Palette, Quick Switcher, Find & Replace

[Procedure and verification criteria](e2e/51-60.md#teil-54).

## Part 55: Extended Keyboard Shortcuts

[Procedure and verification criteria](e2e/51-60.md#teil-55).

## Part 56: Error Notifications & Empty States

[Procedure and verification criteria](e2e/51-60.md#teil-56).

## Part 57: Gantt Chart & Execution Timeline

[Procedure and verification criteria](e2e/51-60.md#teil-57).

## Part 58: Variable Autocomplete & Preview Tooltip

[Procedure and verification criteria](e2e/51-60.md#teil-58).

## Part 59: PausedVariablesInspector

[Procedure and verification criteria](e2e/51-60.md#teil-59).

## Part 60: BulkEditPanel & ActivityTypeFilter

[Procedure and verification criteria](e2e/51-60.md#teil-60).

## Part 61: Edge Reshape Handles

[Procedure and verification criteria](e2e/61-70.md#teil-61).

## Part 62: Designer Status Banner

[Procedure and verification criteria](e2e/61-70.md#teil-62).

## Part 63: Workflows List View UI

[Procedure and verification criteria](e2e/61-70.md#teil-63).

## Part 64: Audit Log Pagination & Multi-Filter

[Procedure and verification criteria](e2e/61-70.md#teil-64).

## Part 65: Credential & Machine Pickers in the Properties Panel

[Procedure and verification criteria](e2e/61-70.md#teil-65).

## Part 66: SignalR Connection Status

[Procedure and verification criteria](e2e/61-70.md#teil-66).

## Part 67: Import Dialog with Drag & Drop

[Procedure and verification criteria](e2e/61-70.md#teil-67).

## Part 68: Activity Palette & Node Context Menu

[Procedure and verification criteria](e2e/61-70.md#teil-68).

## Part 69: Editor Toolbar View Toggles

[Procedure and verification criteria](e2e/61-70.md#teil-69).

## Part 70: Lint Panel

[Procedure and verification criteria](e2e/61-70.md#teil-70).

## Part 71: LiveConsole: Filter & Pause

[Procedure and verification criteria](e2e/71-80.md#teil-71).

## Part 72: WorkflowBreadcrumbs (Calls → Navigation)

[Procedure and verification criteria](e2e/71-80.md#teil-72).

## Part 73: Manual Edge Label Override

[Procedure and verification criteria](e2e/71-80.md#teil-73).

## Part 74: Workflow Snippets (NodeLibrary)

[Procedure and verification criteria](e2e/71-80.md#teil-74).

## Part 75: Quick Interactions in the Designer

[Procedure and verification criteria](e2e/71-80.md#teil-75).

## Part 76: Admin Settings UI (SettingsPage Sections)

[Procedure and verification criteria](e2e/71-80.md#teil-76).

## Part 77: AI Workflow Assistant + Streaming (Explain + Edit)

[Procedure and verification criteria](e2e/71-80.md#teil-77).

## Part 78: Alerting (Notification Rules)

[Procedure and verification criteria](e2e/71-80.md#teil-78).

## Part 79: Toolbar Layout Toggle (compact ⇄ classic)

[Procedure and verification criteria](e2e/71-80.md#teil-79).

## Part 80: Global AI Chat (`/ai-chat`)

[Procedure and verification criteria](e2e/71-80.md#teil-80).

## Part 81: Custom Activities (`/custom-activities`)

[Procedure and verification criteria](e2e/81-86.md#teil-81).

## Part 82: Database Outage at Runtime (Banner, Fail-Fast, Self-Healing)

[Procedure and verification criteria](e2e/81-86.md#teil-82).

## Part 83: Live-Ops Mission Control (`/operations`)

[Procedure and verification criteria](e2e/81-86.md#teil-83).

## Part 84: Script Editor Against the Minified Bundle

[Procedure and verification criteria](e2e/81-86.md#teil-84).

## Part 85: Browser Demo Against the Published Bundle

[Procedure and verification criteria](e2e/81-86.md#teil-85).

## Part 86: Guided Tasks of the Browser Demo

[Procedure and verification criteria](e2e/81-86.md#teil-86).
