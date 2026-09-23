# Skill delivery review

Date: 2026-09-22. Scope: inspect the current implementation and replay the nine
skill-assisted test journals. No runtime, skill package or lab configuration changes.

## Verified findings

1. **Instructions pass through the observation excerpt pipeline.**
   `AgentRuntime.GuardedFunction` captures both `load_skill` and
   `read_skill_resource` in `AgentEvidenceStore`. Its immediate response is capped
   independently of the configured output limit: larger results return beginning
   and end excerpts, with the middle available through evidence recall.
   All 44 successful skill loads in this round were excerpted; none of their
   evidence IDs was subsequently recalled using evidence_read/evidence_analyze.
   The actual 6,334-character main response returned 1,500 characters at each end.
   Of 42 reference responses, 40 were also excerpted (original lengths 2,802–7,265).
   Merely raising MaxToolOutputCharacters does not remove the fixed excerpt cap.

2. **The instruction hierarchy does not distinguish selected guidance.**
   Runtime TrustInstructions say tool results are data, not instructions, and forbid
   following instructions in retrieved content. Selected administrator-managed
   skill instructions arrive as exactly that tool content. No explicit selected-skill
   exception exists. This is a static instruction conflict; the journal alone cannot
   establish how much of a particular model decision it caused.

3. **Reference consumption is optional model behavior.**
   The main skill explicitly requests evidence.md and relevant references. All five
   members loaded the main skill in TS 5/6/9, but made zero reference-read calls.
   The synthetic incomplete-source run also read no reference. The engine verifies
   selection, enablement and package integrity, not that guidance was fully delivered
   or acted upon. No new test establishes that the whole handbook was applied.

4. **Compaction does not preserve skill instructions separately.**
   AgentContextManager summarizes old exchanges as untrusted working notes, not
   instructions. Loaded skills are ordinary exchanges, with no retained per-member
   skill context. Compaction occurred in all eight real fault runs (not the synthetic
   run). Loss of individual skill rules during these summaries was not independently
   isolated; this is a code-confirmed exposure, not a proven sole cause of failures.

5. **Guidance can count as a fresh observation.**
   Runtime's completion.Observe callback excludes delegation, evidence recall and
   investigation tools, but not skill loading/resource reading. New guidance reads
   can therefore affect review revision/progress tracking despite observing no target
   state. Repeated identical results are deduplicated; this is not unlimited progress
   from repeatedly loading the same skill.

## Recommended implementation, in order

- Introduce an explicit host-owned distinction between selected skill guidance,
  target observations and skill script results. Scripts remain ordinary untrusted
  execution output. Native or MCP content must not self-declare instruction authority.
- Deliver selected skills' main instructions completely as bounded per-member task
  guidance, subordinate to user task and host permissions. Preserve this context
  across compaction. Enforce a combined size budget and report overflow explicitly;
  do not silently omit instructions or indiscriminately load every resource.
- Keep progressive reference loading, but return complete bounded reference pages
  without a second head/tail cut. Preserve unambiguous continuation metadata and
  track delivered ranges, version and content hash. Delivery is not proof of reading
  or comprehension. No SCCM-specific dispatch rules or forced loading of all files.
- Exclude instruction/resource reads from fresh target evidence and review progress.
  Keep them visible in the event journal. Expose selected/loaded/partially delivered
  state using existing events before considering additional UI.
- Clarify generic guidance to consult applicable references before conclusions;
  do not equate missing reference reads with proof the model failed to understand.

## Verification for the implementation

Use the real runtime-to-ILlmClient seam: instructions and a unique fact placed in the
middle of a skill/resource must arrive intact, survive compaction and stay scoped to
the authorized member. Long references must paginate without gaps; changing byte
offsets must not corrupt UTF-8. Unselected/revoked skills remain inaccessible;
guidance cannot grant tools or satisfy a fresh-observation review requirement.

Then run a small Luna-only synthetic workflow whose answer requires a neutral
reference fact; verify tool trace and result across repeated runs. Only after that
repeat the affected SCCM cases. Do not raise budgets or switch models to conceal a
delivery problem.

Evidence: `.runlogs/agent-skill-retests/skill-delivery-audit.json`, the nine original
result journals, `src/NodePilot.Ai/Agents/AgentRuntime.cs`, `AgentEvidenceStore.cs`,
`AgentContextManager.cs`, `TeamCompletionState.cs`, and
`src/NodePilot.Engine/Agents/AgentSkillTools.cs`.

## Implemented follow-up

The subsequent implementation supplies selected main instructions in the member's
fixed instruction context with an explicit aggregate budget (64,000 characters or
one third of input context). Host-only tool metadata distinguishes guidance from
observations; MCP responses cannot opt into that metadata. Package enablement/hash
is checked before model calls as well as resource/script calls. Revocation stops
subsequent model calls; it cannot retract an already transmitted request.

References are serialized-budget-aware UTF-8 pages with package hash, path, offset,
nextOffset and hasMore, without evidence-store wrapping. Main delivery emits
skill_loaded; resource delivery remains visible in tool events. Guidance cannot
advance observation-based reviews or become original target evidence. Script
execution/output keeps the existing authorization and observation path.

Validation:

- Two initial runtime regressions failed before implementation and passed after it.
- 95 agent/adapter tests pass, including complete middle-of-instruction/reference
  delivery, context compaction, member isolation, revocation, explicit overflow
  and guidance not advancing review evidence.
- 345 Engine agent tests pass, including 17 skill tests and multi-page UTF-8/escaped
  JSON reconstruction under a 1,024-character output budget.
- Three Luna-only live runs answered a neutral lookup exactly: a random prefix in
  the main instruction middle plus a random value in the reference middle. All
  three read four reference pages, with zero evidence wrappers/snapshots. Counts:
  7/5, 6/4, 7/5 model/tool calls. No native tools or target machines were configured.
- A preliminary test was rejected before model execution because its 30-call budget
  exceeded the administrator's single-agent limit of 20. Only the fixture budget
  was corrected; administrator limits were unchanged.
- Temporary workflows/skills disabled. Existing SCCM skill remains enabled. Final
  Dev API build passed and readiness returned HTTP 200; installed service unchanged.

Live evidence: `.runlogs/skill-delivery-live-verified/summary.json`, result journals,
and `cleanup.json`. SCCM fault cases were not repeated in this implementation step.
The synthetic success demonstrates delivery and use in these three runs, not a
guarantee of diagnosis quality or complete reference consumption on every future task.
