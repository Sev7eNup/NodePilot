# Team review completion acceptance

Date: 2026-09-20. Branch: feature/ai-agent-activities.

## Behavior

The runtime now owns a completion gate for team runs. A member's needs_input or
failed response stays open until the same member returns completed. Another
specialist completing work does not clear the question. Optional technical
reviewers must complete review of the current work revision; later specialist
responses or supervisor tool calls invalidate previous reviews.

Premature final answers emit team_completion_blocked and cause at most two
additional supervisor turns. Existing run budgets and cancellation still apply.
If unresolved, the step fails with member IDs and questions, including for JSON
results. There is no tool replay or automatic permission expansion. A member may
complete a bounded review that explicitly acknowledges evidence limitations.

The reviewer function is independent of the free role name and is not inferred
from existing names. Designer selection is persisted inside the existing member
configuration. A supervisor cannot also be a reviewer. A reviewer cannot delegate
or gain extra tools. All selected reviewers are required, but none is mandatory
for teams that have no reviewer configured.

## Verification

- 28 targeted backend tests passed: runtime, configuration, completion state.
- Tests cover skipped reviewers, stale reviews after new work, a question that
  another member cannot clear, two reviewers, supervisor evidence invalidation,
  failed members, bounded refusal to follow up, shared budget and JSON gating.
- Three designer/settings E2E tests passed against the built UI, including reviewer
  selection, promotion to supervisor, persistence and reload with only outer nodes.
- API build passed; the development instance was rebuilt. Installed service unchanged.

Actual-model execution `ac28fdd3-6bd0-495b-a551-8ff7f1f537e6` used three members
and no host tools: planner calculated 10+15+20, the reviewer returned needs_input
about breaks, the supervisor sent a follow-up, and the reviewer completed before
the final answer. Result 45 minutes explicitly retained the no-extra-breaks
assumption. Seven model calls, three delegations, 13.8 seconds. The workflow was
disabled afterward. This is a live protocol test, not a Windows diagnosis test;
forced premature-completion cases are covered by deterministic runtime tests.

## Limits

The host cannot verify the truth or completeness of a natural-language completed
verdict. Unstructured objections inside a completed response do not open a question;
the reviewer protocol instructs members to use needs_input for material objections.
These changes close the earlier protocol-enforcement gap, not all diagnosis-quality
acceptance items. No Windows/SCCM fault was introduced for this change.
