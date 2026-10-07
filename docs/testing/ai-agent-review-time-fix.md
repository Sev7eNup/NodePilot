# Review follow-up and scheduling-time corrections

Date: 2026-09-20. Follow-up to [update validation](ai-agent-update-validation.md).

## Reproduced faults

The earlier real scan diagnosis returned a final answer after a reviewer identified
missing checks. Asking that reviewer to close with limitations removed the host's
open question without obtaining the missing evidence. A deterministic regression
reproduced this and failed before the correction.

For SCCM scheduling, raw WMI `20260922022900.000000+***` with UseGMTTimes=false was
rendered by CIM as the previous day's 19:29. The raw value describes an unspecified
offset, with local scheduling semantics supplied separately. It is not a UTC instant.

## Changes

For checked agent reads of CCM_UpdateCIAssignment, CCM_SoftwareUpdate and
SMS_UpdatesAssignment, the host emits a local, module-qualified Get-WmiObject read
instead of Get-CimInstance. This preserves raw DMTF properties through selection and
JSON formatting. Namespace/class authorization and literal parameter validation still
run first; this does not expose arbitrary WMI classes or methods. Other CIM queries
are unchanged. Agent instructions explain wildcard offsets and UseGMTTimes.

Teams with tools retain reviewer objections until a new tool observation is obtained
by that reviewer, a specialist or the supervisor. Another reviewer's unrelated review
does not satisfy it. Repeated identical observations and repeated delegation alone
do not count. A renewed objection starts a new evidence requirement. Rejected reviewer
completion is returned as needs_input; unresolved host state prevents final success
and remains bounded by the existing model/tool/delegation/time limits. Evidence
fingerprints retain hashes rather than copies of tool results.

Reviewers additionally return verdict (approved/needs_work) and openChecks. A completed
review that still needs work or has open checks is converted to needs_input. Missing
verdict fields are an invalid completion. The supervisor must obtain approval of the
requested result, not just completion of the review process.

Review scope distinguishes currently necessary diagnostic reads from verification
after a proposed repair. A read-only diagnosis does not require performing that
repair or proving subsequent recovery. Those steps belong in the recommendation.

The host enforces a minimum evidence transition, not semantic truth: it cannot prove
that an arbitrary new observation establishes the claimed cause. Instructions also
require discriminating follow-up, effective configuration checks and matching the
repair to the demonstrated mechanism. Tool-less editorial teams retain their existing
context-only review contract. No new SCCM-specific technical agent role is introduced.

## Regression checks

- 135 permission tests pass, including scheduling-query routing, projection identifier
  validation (before raw WMI query rendering), and existing unsafe
  namespace/class/method/target rejection cases.
- 26 runtime/review tests pass. The runtime replay refuses a supervisor's unsupported
  reviewer closure and accepts continuation after an actual read. Renewed objections,
  duplicate evidence and another reviewer's observations have separate tests.
- At the user's request, defaults and the dev setting use 250,000 context characters
  per model call. Two independent 200,000-character requests pass; an over-limit call
  is rejected before transport. Token/output limits were not changed.
- Example skill windows-diagnostics 1.0.11 is available in dev; immutable earlier
  workflow selections are unchanged.

Live evidence is stored under .runlogs/agent-update-validation/, using labels
supervisor-fix and subsequent repeats. The installed Windows service is untouched.

## Live iterations

- `supervisor-fix`: found the blocking firewall rule after follow-up, but exceeded
  the former 128,000-character context limit before a final answer.
- `supervisor-final`: execution succeeded but diagnostic acceptance failed: a
  reviewer marked its review completed despite unresolved cause checks. This drove
  the explicit verdict/openChecks contract.
- `reviewer-verdict`: the client reviewer found the effective blocking rule, but
  requested post-repair verification; the server review lacked that combined finding.
  The host refused final success with unresolved reviews. Review instructions now
  distinguish diagnosis from post-change verification and assess combined evidence.
- MSI installer and Windows Update installation event checks on both CLIENT1 and
  CM1 returned zero events in each of those three execution windows. This is scoped
  evidence, not a general proof that every possible read has no side effects.
- All 539 AI tests pass on the final review-scope build.
- `scope-fix` (execution `7343f488-a345-4d2b-a33f-dfdf64a7c4a2`) succeeded:
  27 model calls, 77 tool calls, six delegations, 187.5 seconds. The five-member
  team independently found the active outbound rule and matched address, port,
  profile, application and service scope to the failing SUP scan. The supervisor
  continued through server-review objections and returned the concrete cause,
  focused corrective proposal, counterevidence and post-change verification.
  The final answer qualified central management as conditional rather than proven.
  Both targets again had zero matching installation events in the execution window.
  This validates this scenario, not reliable diagnosis of every failure class.
- `time-fix` (execution `b2ba0fad-cbce-4065-85de-d2ad7cb4117e`) succeeded:
  15 model calls, 18 tool calls, four delegations, 65.8 seconds. Independent client
  and server reads plus both reviews preserved start `20260920025800.000000+***`
  and deadline `20260922030300.000000+***`. The final answer correctly reported
  20 September 02:58 and 22 September 03:03 local time, UseGMTTimes=false,
  Pacific Standard Time, without inventing a UTC instant. Both targets again had
  zero matching installation events in the execution window.

## Cleanup

The controller removed the temporary deployment/group, all three test firewall
rules, restore task and CLIENT1 host-access adapter. Client and server assignment
counts are zero; TrustedHosts is restored to localhost, CurrentUser execution policy
remains Undefined, Defender is running at 4.18.26080.4, and the lab VMs remain running.
After removing the block, the controller requested policy and a scan; WUAHandler
reported `Successfully completed scan` at 20 September 03:27:19.056+420. These cleanup
actions occurred after the read-only agent runs. The dev API remains healthy on the
feature branch; the installed service was not restarted.

Follow-up: [Unknown-compliance diagnosis](ai-agent-unknown-followup.md) records the
subsequent response-contract and review-gate corrections, repeated blind client/server
tests and their remaining diagnosis-reliability gap. The earlier successful scan test
does not establish acceptance of this broader Unknown-compliance case.
