# Agent communication history

Date: 2026-09-20. Branch: `feature/ai-agent-activities`.

The live step inspector and execution history now project the durable event journal
into assignments, replies and member actions. Assignment sequence numbers identify
individual handoffs; the current sequential runtime pairs their start and completion.
Member filters include incoming and outgoing handoffs. Original payloads remain
available in the technical view and a complete, paginated JSON support export.
Exports made while running are marked incomplete. No extra model is used to invent
a narrative from the events.

New runs capture member IDs, display roles, team functions, configured models and
target IDs in `run_context`, without credential references or secrets. The supervisor's
delegation schema requires a brief public rationale. Assignments, rationale, tool
parameters, evidence, questions and results use the existing persisted/redacted event
path and owning-workflow permissions. Rejected tool arguments are journaled before
execution is refused. Failed/cancelled runs no longer leave members displayed as running.

The large view uses a native modal dialog, including keyboard focus containment and
Escape handling. Existing runs can be inspected through their recorded assignments
and member IDs; names or explanations absent from their original journal are not invented.

## Verification

- Runtime tests cover the member snapshot, two successive assignments with rationale,
  a member question and its answer, and refused assignments without member execution.
- UI tests cover repeated delegation, pairing tool evidence and model failures,
  interrupted runs, member filters and malformed/unrecognized payloads.
- Browser tests cover the actual history drill-down and large view, stored roles
  differing from today's workflow, reviewer outcomes, questions, escaped tool output,
  REST catch-up beyond 500 events and full export despite a member filter. Escape closes
  the dialog. Existing team authoring and registry browser tests remain green.
- Existing API tests verify journal redaction, save-before-notify ordering, contiguous
  sequence numbers and authorization through the owning workflow.

A real three-member run completed in 21.5 seconds with 5 model calls and 2 delegations:
`36bb2562-c1d8-46b8-b4ff-fd6f1089230b`. Planning and independent review both answered;
every delegation contained a rationale and a matching reply. The task used only
synthetic meeting times supplied in the prompt and selected no external tools.
The retained workflow is `f7e716f4-227a-4ef6-aab2-4e847cc858c2`; it was disabled after
the test, while its history remains available. Evidence and screenshots are under
the ignored `.runlogs/team-trace*` and `.runlogs/team-communication*` paths.
No VM settings were changed.
