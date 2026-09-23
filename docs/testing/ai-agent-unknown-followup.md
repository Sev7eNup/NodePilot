# Unknown compliance and diagnosis-quality follow-up

Date: 2026-09-20. Continues [review/time corrections](ai-agent-review-time-fix.md).
Work stays on feature/ai-agent-activities; installed Windows service is untouched.

## Current acceptance summary

The fixes below are implemented and exercised. The final unchanged 12-case fixture
suite passes its structured-field and source-quotation checks, and all 549 AI tests
pass. The 154 selected Engine permission/read tests passed earlier in this work;
they were not rerun after the final AI-only instruction changes. The API builds.

The blind five-member CLIENT1/CM1 fault run identified the effective scan blocker
and mapped the Unknown assignment correctly in 125.6 seconds / 56 tool calls. A
separate current-scan healthy control passed in 88.7 seconds / 36 tool calls. These
are scoped results, not full acceptance of point 1: the combined scan/compliance
recovery probe still exhausted 80 tools, and final narrative review found an
imprecise alternative firewall remedy (see final fixture results below).

Point 1 therefore remains partially accepted. Remaining work is bounded completion
of the combined investigation and consistently precise remediation advice. The last
two generic evidence-instruction refinements were validated against fixtures and
unit tests after lab cleanup, not by another real WinRM run. No broader diagnostic
reliability or universal absence of side effects is claimed.

Lab cleanup is complete. Final API checks returned readiness HTTP 200 and settings
maxContextCharacters=250000 (characters, not tokens), teamToolCalls=80 and
allowServiceIdentity=false. The following sections preserve intermediate failures
and their corrections chronologically; earlier results are not current totals.

## Independent setup and acceptance

The controller verifies CLIENT1-only collection CHQ00014 and uses a temporary update
assignment, future deadline, host-access adapter and bounded firewall fault. The agent
receives the reported Unknown symptom, assignment name, CI and machine identity, never
the injected fault or expected answer. Five members have read-only client/server tools;
general diagnostic skill resources are supplied as instructions. This is not another
remote skill-runner acceptance test.

Acceptance requires an actual Unknown observation, correct CI/assignment mapping,
current scan failure versus older success, effective blocking configuration, a focused
repair proposal, substantive review, and no unsupported metadata-corruption or general
health claims. Engine Succeeded alone is insufficient. SQL/controller observations are
independent of the team's evidence. Installation event audits cover each run window.

## Findings and corrections

- Initial CI 16778968 already had Required compliance while its new assignment had
  an Unknown asset row. During execution, its server status changed to Installed.
  The team correctly distinguished individual CI and assignment status, but incorrectly
  used Installed as a reason to defer the still-failing scan investigation. Execution
  086405c5-e18c-48d2-9690-6e307f20fe34 is not accepted as root-cause diagnosis.
- A subsequent site observation exposed CI 16779065 with actual Status=0 Unknown
  and scan state 5/error 0x80240438. The controller created assignment 16777222 for
  this CI. No SQL status values or log evidence were forged.
- Repeating the existing diagnosis fixtures exposed malformed reviewer envelopes.
  Cases 02–04 failed; the baseline was stopped during case 06. The host now supplies
  one unambiguous member response contract and permits one format-only correction
  with tools disabled. It retains needs_work/openChecks and does not replay actions.
  Three regression cases failed before the fix and pass afterward.
- A subsequent fixture run exposed over-demanding network evidence for an already
  observed stopped required service. Instructions now require checks relevant to the
  claimed mechanism, without downgrading a narrow proven finding for unrelated gaps.
- files_search now advertises its existing nonempty 1–1024-character query constraint
  and first/last ordering in the tool schema. PowerShell descriptions show valid
  property-list syntax. A repeated real failure with quoted comma lists led to a small
  canonicalization: literal comma-separated property names become the same validated
  array as explicit PowerShell property arguments. Two differential tests failed before
  the fix; expressions, empty items, alternate classes/targets and Win32_Product remain
  rejected. This changes tolerated input syntax, not the available operations.
- The adapter supplies remaining shared budgets before each model call; delegation
  guidance prioritizes unanswered discriminating checks before final review. Budgets
  initially remained at 40 model / 80 tool / 16 delegation limits; the later explicitly
  identified extended probe temporarily changes only the tool limit.
  The first actual-Unknown run exhausted its tool budget on repeated inventory/log
  reads and failed the review gate (5c9e15c2-1160-45b2-9b86-bad84e7fc9cd); it is not
  counted as accepted. It included three malformed property lists and seven empty
  file searches, motivating the clearer tool contracts above.
- Example skill 1.0.13 separates scan health from individual/deployment status and
  distinguishes IsLatest from IsSuperseded. Their different relationships follow
  [Microsoft's CI property definitions](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/compliance/sms_configurationitemlatestbaseclass-server-wmi-class).
  Existing workflow selections keep their immutable skill versions.
- The 12-case reasoning pass reached 10/12: case 03 incorrectly redefined a schema
  field to describe the failed diagnostic query, and case 07 exhausted review after
  requesting an addition to the proposed verification. Members now explicitly retain
  the original result-contract definitions. Review objections distinguish missing
  evidence from a required revision of interpretation/proposal. Revision still needs
  a subsequent approved review and cannot clear an earlier unmet evidence requirement.
  This gate failure has a red/green regression and an end-to-end runtime test.
- Current automated checks: all 545 AI tests and 154 selected Engine permission/read
  tests pass. Both include the added regressions; the dev API builds successfully.
- The actual-Unknown repeat a0087b56-1b66-4351-a127-9b4957e90417 found the firewall
  mechanism and proposed focused correction (24 model / 72 tool / six delegations,
  183.8 seconds), but called supersedence a configuration defect without a target-version
  requirement. It is only partial acceptance. Skill guidance now explicitly separates
  lifecycle observations from a demonstrated violation and qualifies downstream effects.

Two additional checked-in reasoning fixtures cover the metadata flags and Unknown
assignment versus individual compliance. Their expected results remain outside the
agent's input, just like the existing ten fixtures.

The final unchanged 12-case pass reached 11/12. All original ten cases and the new
metadata case passed. New case 12 was ambiguous about whether mechanismProven referred
to the directly proven scan blocker or the ultimate cause of Unknown reporting. The
reviewer correctly distinguished those scopes but then exhausted the evidence gate.
Its question now explicitly classifies the scan and discusses Unknown separately;
source files and expected values are unchanged. This is a fixture-scope correction,
not a claim that the original 12-case pass was green.

The clarified case 12 passed its targeted repeat (execution
be424461-3fca-41e8-aa7f-f23877da6e41, 43.2 seconds, nine model calls, eight tools,
two delegations). Together these runs cover all twelve cases, but are not a single
unchanged 12/12 run.

The 80-call real repeat 6dd4073f-41ae-4e98-89bc-4ffdd5a1c445 failed closed after
293.7 seconds (31 model calls, 80 tools, eight delegations), reaching effective network
checks too late. An additional controlled test uses a temporary administrator team
limit of 120 and an explicit workflow limit of 120; the original limit is restored
afterward. This is a changed acceptance configuration, not proof of reliable completion
within the default 80 calls. Permissions, model budget and context limit are unchanged.

## Earlier extended live result: acceptance remains open

The extended execution 6c354503-0e63-4f54-9b2b-820311e56c40 finished with engine
status Succeeded after 268.9 seconds, 37 model calls, 101 tools and ten delegations.
It completed reviewer follow-ups and correctly avoided treating a failed assignment
query as a negative observation. It also qualified supersedence against the unknown
desired policy. However, it stopped at the scan error and assignment-evaluation chain,
explicitly leaving the cause of 0x80240438 unknown. It did not discover the injected
effective firewall rule or propose the corresponding focused remedy. Increasing the
budget therefore did not satisfy the root-cause acceptance criteria.

At this stage point 1 was **not closed**. Real-run diagnosis remained inconsistent: one run found the
mechanism but overstated a secondary finding; later runs either exhausted their budget
or missed the mechanism. The remaining work is investigation prioritization and review
of whether the requested root cause was actually established, followed by repeat blind
validation. An engine success, green unit tests or one favorable diagnosis is not a
substitute for that evidence. No healthy-agent countercheck was claimed in this round.

The event audits on CLIENT1 and CM1 found zero MsiInstaller and WindowsUpdateClient
installation events (19/20/21) in each completed live execution window. This is scoped
audit evidence, not a universal proof against all possible side effects.

## Cleanup and retained settings

After all agents stopped, the controller removed the SUP block and requested a scan.
WUAHandler reported success at 04:06:33.292+420 on September 20. The controller then
removed the temporary update assignment/group, requested policy, and removed all three
test firewall rules, the restore task and CLIENT1's temporary host-access adapter.
Final checks found zero test assignments on client/server, zero test rules/task/adapter,
TrustedHosts restored to localhost, CurrentUser execution policy Undefined, Defender
running at unchanged platform 4.18.26080.4, and all three lab VMs running. A subsequent
scan also succeeded at 04:07:01.815+420. These controller cleanup actions were outside
the read-only agent execution windows.

Administrator teamToolCalls is restored to 80 and allowServiceIdentity to false.
maxContextCharacters remains 250000 (characters, not tokens). Test workflows are
disabled. The dev API remains available; the installed Windows service was untouched.

## Review handoff follow-up

The next investigation reproduced three concrete issues in regression tests before
changing production code:

- A reviewer `needs_input` response could contain structured openChecks that the parser
  discarded. Checks now precede the narrative in the returned/pending question, and
  malformed arrays trigger the existing format-correction path rather than silent loss.
- Reviewers received the supervisor's task as a direct instruction, including demands
  to approve despite an unknown cause. Review requests now separate originalTask from
  supervisorSubmission; the latter is explicitly a proposal to assess, not authority
  to change the original objective or dictate a verdict.
- The supervisor omitted a decisive client-review finding from its server-review
  handoff. The host now supplies the latest actual reports of other members with their
  IDs/statuses as memberFindings. The recipient still independently assesses these
  untrusted claims. This does not grant tools, targets or delegation rights.

Execution 510e8e76-178e-49a9-ac37-4239f1bf8101 used the first two corrections and
finished in 203.7 seconds, 28 model calls, 75 tools and eight delegations. It found
the effective firewall mechanism and proposed a focused rule correction without
unsupported metadata repair. However, it filtered UnknownAsset by the update CI_ID,
missing the real assignment-level CI_ID=0 row. The controller independently read that
row for assignment 16777223 / GUID {FF9483B8-42F6-40B8-982B-EEDCCDA5CABC}.
The scan diagnosis passed but the complete Unknown-status diagnosis was not yet
accepted. Skill version 1.0.14 explicitly uses MachineID plus assignment identity
before mapping AssignedCIs; existing workflow skill selections remain unchanged.

The parallel reasoning run on that intermediate build exposed an over-demanding
review of a bounded evidence question (case 03). It requested live WMI/elevation
despite having only fixture file tools and then hit the evidence gate. Review guidance
now distinguishes a supported "not established by these sources" conclusion from a
root-cause task with still-available discriminating reads. Expected fixture results
and source evidence were not changed.

All 549 AI tests pass, including four new regressions. Live tests continue to use the
active gpt-5.6-luna model, 40 model / 80 tool calls and 250000 context characters.
On the final shared-report build, both targeted unchanged fixtures passed: case 03
in 46.2 seconds (11 model calls, 12 tools, three delegations), and case 12 in 54.2
seconds (14 model calls, 12 tools, four delegations). In case 03 the reviewer found
and corrected an inaccurate quotation while accepting the bounded evidence conclusion.

The blind five-member WinRM execution 0885abbd-1ce1-4b0a-898f-2c73670e81b7 finished
in 125.6 seconds with 19 model calls, 56 tools and four delegations. Both reviewers
completed their own client/server checks. The result correctly mapped the real
UnknownAsset CI_ID=0 row through assignment identity and AssignedCIs, identified
the effective outbound block to CM1/10.0.0.7:8530, distinguished older scan successes
and later MP communication, and proposed focused rule correction and verification.
It did not prescribe metadata repair merely because the update was superseded.
The improvement in this run is observed, not a statistical performance guarantee.

The run establishes the current scan blocker and the assignment-level missing
assessment; it does not prove that this rule is the sole possible reason for the
individual CI's missing server report. A restored successful scan does not by itself
prove restored end-to-end compliance reporting. The following healthy countercheck
is therefore scoped to current scan health, not universal client/site health.

Both new real execution windows had zero matching MSI/WindowsUpdate installation
events on CLIENT1 and CM1. After the final fault run the controller removed the SUP
block and requested a scan; WUAHandler recorded success at 05:35:53.022+420. A fresh
team receives the reported scan-failure symptom without being told about restoration.

The first recovery probe (096d08f1-eac1-4d91-8c83-19521954495b) correctly observed
that later success superseded the old scan failure, but failed review completion after
221.6 seconds, 31 model calls, 80 tools and nine delegations. Its reused task also
requested investigation of the particular CI; reviewers expanded into unresolved
compliance and historical-cause questions. This is a remaining scope/budget limitation,
not a passed healthy control. Both target installation-event audits were again empty.
The next control keeps the same five members/tools/limits but asks only about current
scan health and a remedy if a present scan failure is established. It supplies no
expected outcome or information about the removed rule. The changed task scope is
explicit and does not erase the failed combined probe.

The scoped healthy control 718e34fd-c810-4c86-8008-1bb89ecfe3a8 passed in 88.7
seconds with 17 model calls, 36 tools and four delegations. All five members completed,
including both reviewers. The report cited the newer successful scan and matching SUP,
treated earlier failures as superseded, limited its health claim to that operation,
and recommended no repair. Installation-event audits on both targets were empty.

The complete unchanged reasoning rerun on that build passed 11/12. Case 12 again
understated a proven current blocker because no historical packet trace or post-repair
experiment existed. The generic evidence guidance now separates a proven effective
current blocker from attribution of a historical failure and from authorization to
change an intentional policy. Three consecutive unchanged case-12 repeats passed
their categories, boolean fields and quotation checks (40.3, 40.7 and 38.5 seconds;
each nine model calls, eight tools and two delegations). Manual narrative review still
found unsupported identifier-mismatch language; those automated passes alone were not
treated as full semantic acceptance. A further generic rule requires establishing
field meanings and key relationships across record types/aggregation levels before
asserting inconsistent or unassignable data. Final results are recorded below.

## Final fixture results and remaining narrative limitation

The final unchanged suite `diagnosis-final-evidence-20260920` passed 12/12 automated
checks. The evidence files and expected classifications were unchanged. Manual
review confirmed the current/historical distinction, bounded conclusions after
access-denied evidence, endpoint matching, unknown historical timezone handling,
and separate metadata relationships.

Case 12 (execution 05de92fa-e6c3-4836-afcd-a12a17a6730f, 55.3 seconds) correctly
identified the effective current scan blocker. It separately described assignment
Unknown and individual Required status without declaring the different CI identifiers
corrupt or unassignable. It did not assert an initiating actor, installation damage
or whole-system health. Its primary remedy was focused correction of the blocking rule.

However, its alternative wording, "eine gleichwertige erlaubende Ausnahme", leaves
rule precedence unspecified. An ordinary conflicting allow rule does not override
an explicit Windows Firewall block rule; see
[Microsoft's rule precedence documentation](https://learn.microsoft.com/en-za/windows/security/operating-system-security/network-security/windows-firewall/rules).
The proposal needs to specify correction of the effective block at its controlling
policy source, rather than imply that adding an ordinary allow rule is sufficient.
This is an unresolved remediation-quality issue, not a passed semantic check or an
action performed by the agents. No additional favorable rerun is substituted for it.

The fixture validator checks classifications, flags and literal source quotations;
it does not automatically establish every free-text claim or remedy's correctness.
Together with the failed combined recovery probe, this prevents full closure of
point 1 despite the green automated suite and scoped live successes.

## Final lab cleanup

At 12:43 UTC the temporary assignment/group, all test firewall rules, restore task and
CLIENT1 adapter were removed. Client/server assignment counts, rule/task/adapter counts
are zero. TrustedHosts is localhost, CurrentUser policy remains Undefined, Defender is
running at unchanged platform 4.18.26080.4, and CLIENT1/CM1/GW1 remain running. The
controller's final post-cleanup scan succeeded at 05:43:04.186+420. Subsequent tests
use only local synthetic files, not further lab faults.

## Other outstanding acceptance items

Follow-up on the next requested work: [content and remote log validation](ai-agent-content-and-remote-logs.md)
records the increased 500-call team ceiling, combined client/server diagnosis and
remote transfer/rotation checks. The list below preserves the scope at this report's
original completion; consult the follow-up for the current status of items 2 and 3.

2. Combined CLIENT1/CM1 content-download fault and healthy-agent countercheck.
3. Real 250 MB WinRM log collection, including rotation, encodings and long lines.
4. Remote CMD/Git Bash skill execution and signed scripts under AllSigned.
5. Restricted-user endpoint authorization/redaction, external MCP authentication and
   disconnects, and workflow dependency portability.
6. Full CI/designer E2E, architecture/catalog/settings/documentation checks and
   consolidated acceptance evidence.

Live evidence, full journals and controller scripts are retained in
.runlogs/agent-update-validation/; reasoning-run evidence is in .runlogs/agent-live/.
These tests evaluate specific cases, not a universal guarantee of diagnostic truth.
