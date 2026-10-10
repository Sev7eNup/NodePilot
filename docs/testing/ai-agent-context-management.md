# Agent context management validation

Date: 2026-09-20. Branch: `feature/ai-agent-activities`.

Implemented the five context-management measures: durable selected evidence with
bounded recall, automatic compaction inside the framework tool loop, untrusted notes
separate from host permissions/review state, bounded section analysis with explicit
coverage and continuation, and journaled/budgeted compaction. See `docs/ai-agents.md`.

`Agents:ModelMaxOutputTokens` now defaults to 250,000 and the Dev settings were updated.
The active profile's ceiling is 1,000,000, so the effective agent ceiling is 250,000.
The independent input character guard and final workflow-result limit are separately
documented. Existing chat/query profile settings were not changed.

| Validation | Result |
|---|---|
| Full AI suite | 564 passed; nine new context/evidence regressions |
| Inner Microsoft Agent Framework function loop | 30 native reads, repeated compaction, first evidence recalled without another native read; redaction retained |
| Context safety | Original task, atomic tool pairs, latest observations, untrusted-note placement, repeated compaction and failed-summary history preservation verified |
| Evidence | Lossless paging including escaped Unicode/control characters at 1,024/16,000-character tool limits; persisted pages below journal cap; another run cannot resolve an ID |
| Partial analysis | Four independent model calls, overlapping ranges, durable retrievable notes, explicit incomplete coverage and shared token/call accounting |
| Engine agent tests | 279 passed on repeat |
| API, settings and architecture checks | 185 passed |
| Frontend trace/projection | 8 passed; TypeScript build passed |
| API build | Passed; existing unrelated compiler/documentation warnings remain |
| Actual configured provider | `gpt-5.6-luna`: successful agent execution with effective 250,000 output-token ceiling; 1 model call, 0 tools, 1.7 seconds |

The initial broad Engine run had one failure in the pre-existing one-second overall
deadline test. Its six cases passed separately and the full 279-test agent group then
passed. No deadline behavior or test expectations were changed to obtain that result.
After final source-review adjustments, the 49 relevant AI runtime/context/review/timeout
tests passed again.

The live smoke workflow is `80c498ea-ba3d-44b1-98c5-9bd748baefaf`, execution
`4c7d36e6-1f26-420e-85a2-0cc5bdad682f`. It returned “Kontextverwaltung bereit.”
and was disabled afterwards. Local logs are under `.runlogs/agent-context-management/`.

Long-context behavior and section coverage were tested with synthetic data and
controlled model replies. This proves routing, bounds and evidence retention, not
universal semantic quality of LLM summaries. The live smoke establishes that the active
provider accepts the configured output ceiling; it does not generate 250,000 tokens.
No CM1/CLIENT1 fault was injected during this work. The cache case and remaining
diagnostic cases in `ai-agent-tasksequence-followup.md` are deliberately deferred.
