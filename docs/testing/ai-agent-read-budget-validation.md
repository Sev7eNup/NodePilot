# Read access and completion checks — 21 September 2026

Branch: `feature/ai-agent-activities`. This follows the all-Terra cases in
[the server fault comparison](ai-agent-terra-server-validation.md).

## Changes

The existing PowerShell tool now reads local share definitions/access lists,
Win32_Share, deployment/task-sequence classes and client cache/scheduled policy
state. Inline UNC paths are resolved to a local disk-share path on the bound
machine; they do not create another SMB connection or delegate credentials.
The helper rejects foreign hosts, traversal, alternate streams, device/WebDAV
paths and reparse points. This tests local existence/content, not another
identity's effective SMB access. Packaged scripts retain their original bytes
and therefore cannot bypass the helper with newly accepted UNC syntax.

An evidence-backed team's candidate final answer receives one additional host
completion check when the shared budget can accommodate a focused follow-up and
required reviews. This can reopen investigation and invalidate stale approvals.
Supported answers do not require more tool reads. Corrective completion rounds
can now exceed two while new distinct evidence arrives or pending obligations
decrease; two consecutive rounds without either form of progress still stop.
Existing model/tool/delegation/time ceilings remain unchanged. No error-code,
filename or test-case-specific diagnosis rule was introduced.

## Automated verification

- The new regression tests failed on the old implementation: ten read cases and
  three completion cases reproduced the missing behaviors before their fixes.
- All **579 AI tests** pass, including useful follow-up, no redundant reads,
  budget reserve, renewed review and progress across multiple review rounds.
- All **301 Engine agent tests** pass. One deadline variant failed in an initial
  parallel run; all variants passed individually and the complete 301-test group
  passed on repetition. No timing thresholds were weakened.
- API build: zero errors, 54 existing warnings. Dev API was restarted; readiness
  returned HTTP 200. The installed Windows service was not restarted.

## Live read probes

Nine deterministic probes executed the actual canonicalizer and process wrapper
over WinRM on CM1. They verified a present and an absent source directory, the
original source payload hash, share metadata, share ACLs, SMS_Advertisement,
SMS_TaskSequencePackage and SMS_DeploymentSummary. A foreign UNC host was rejected
before any SMB read. All nine passed. These are explicitly scoped capability
probes, separate from the blind model evaluation.

Artifacts are in `.runlogs/agent-read-budget-validation/`. Prior traces are retained
unchanged. The new harmless deployment is CHQ2000E, limited to CLIENT1. A healthy
task-sequence reference succeeded at 08:12:06 UTC before fault injection.

## Blind comparison

Case 9 repeats the reversible physical DP-payload corruption with all five members
on Terra, unchanged neutral task, skill 1.0.21 and budgets 100/500/20. The model
receives no injected-fault description or expected answer. Results and restoration
are recorded below.

**Result: the current physical DP-content cause and a scoped remedy are identified.**
The team maps CHQ0000A version 4 through PkgLib, per-file DataLib metadata and
FileLib ownership, hashes the physical payload and compares it with the source.
The expected/source digest is `3AAD819EAC7DD3DD91C9C904A5E1D6280CC08A8036B02233A0B88B8A1BDBA220`;
the actual physical object is `E23878D9D9F675ABF5544836648ECD619987594B620CD45BA6E1AD064809EF9C`
at the same length. The final report proposes supported correction/redistribution
of only the affected package on CM1. It correctly keeps the historical aggregate
download hash distinct from the currently measured single-file hash and does not
claim a captured historical HTTP response body or identify an actor.

| Metric | Previous Terra run | New run |
|---|---:|---:|
| Model calls | 70 | 70 |
| Tool calls (including delegation/run memory) | 111 | 171 |
| Delegations | 6 | 7 |
| Elapsed seconds | 471.5 | 578.4 |
| Actual physical DP payload comparison | Missing | Completed |

The initial candidate answer arrived after 54 model calls. Exactly one host
completion check then produced 16 additional model calls, 26 tool calls and three
delegations: a targeted server follow-up and fresh reviews by both reviewers.
The additional original IIS evidence links the client's TokenAuth DP application
to C:\SCCMContentLib; a repeated payload hash confirms the observed difference.
The cause had already been found before the check. This single comparison is
evidence of the implemented behavior, not proof that an individual instruction
change deterministically caused the better diagnosis. It is also not an attempt
to exhaust all 100 calls.

Four calls failed: two absent client paths and two invalid Select-Object -Tail
invocations. Select-Object uses -Last; these syntax errors do not require broader
permissions. No newly needed legitimate read was denied in this run.

[New Terra workflow](http://localhost:5173/workflows/dc626263-1c48-48ce-a783-2f5f8bc03cdf),
execution `dca7e748-753f-47c7-9dbb-d4ba3ec48320`,
agent run `8f550a71-c0af-46eb-9bae-aab9fa10cc87`.

## Audit and restoration

All 54 recorded PowerShell commands are read/query/format operations, without
dynamic command invocation, member methods or parser errors. Neither CLIENT1
nor CM1 has MsiInstaller events or WindowsUpdateClient installation events 19/20/21
within this diagnostic execution's time window. The controller's fault setup,
tasksequence triggering and restoration are separate authorized operations.

Original payload bytes were restored and the real tasksequence succeeded after a
fresh download at 08:22:47 UTC. Cleanup verifies original source/hash, DP state 0,
WSUS/MP HTTP 200 and original service/cache/update settings. The temporary access
adapter/firewall and recovery task are removed, TrustedHosts restored to localhost,
deployment CHQ2000E removed and the test tasksequence disabled. The diagnostic
workflow is terminal and disabled; the global model remains Luna. Dev API and UI
remain running on the feature branch. No commit, push or installed-service restart.

Cases 7 and 10 were not repeated blind in this change; their prior partial grades
remain. The nine deterministic CM1 probes verify the access gaps, not their full
diagnostic success. Arbitrary PowerShell remains outside the checked read language,
and semantic completeness is still evaluated by the model and reviewers.

## Reference contracts

The share mapping accepts the local disk/admin-disk types documented by
[Microsoft for Win32_Share](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-share).
Share ACL reads use [Get-SmbShareAccess](https://learn.microsoft.com/en-us/powershell/module/smbshare/get-smbshareaccess).
Deployment inspection reads the properties of
[SMS_Advertisement](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/servers/configure/sms_advertisement-server-wmi-class);
its mutating methods are not exposed.
