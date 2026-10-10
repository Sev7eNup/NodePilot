# ConfigMgr server content diagnosis — 2026-09-20

Open item 5 previously stopped at an empty DP list observed on CLIENT1. All members
were bound to CLIENT1; there was no agent-side CM1 inspection. The controller's
knowledge that test content had been removed was not an agent finding.

## Implementation and identity

The existing PowerShell tool now also accepts read-only SMS_ProviderLocation in
root/SMS and SMS_BoundaryGroupMembers in a valid site namespace. Two regression
cases failed before the change. All 124 permission-policy tests then passed.
No CIM methods, target overrides, write commands or arbitrary classes were enabled.
The API build passed with existing compiler/analyzer warnings (54 warnings, no errors).

The Windows diagnostic example gains resources/server-content.md, covering provider
discovery, package/DP assignment, installed version/status, boundary membership,
AD-site versus IP boundaries and narrowly scoped repair proposals. Final version 1.0.9
was imported into the dev registry (`b788fbb8-3fbb-4517-9fa5-f6b3e3d08fb5`). Older
immutable selections are unchanged. Live members receive that exact resource as
instructions; these runs test native PowerShell, not remote skill-script execution.

Deployment assumes the selected service account or Windows network computer
identity already has WinRM/SMS Provider permissions. Service identity is explicitly
selected; permissions and target assignment remain host-enforced. The local dev
process runs as a local interactive user, so the live lab uses the existing encrypted
CORP\LabAdmin credential. These tests do not prove domain machine-account SSO.

## Live method

CM1 is reached over real WinRM at its existing isolated host-access address
192.168.240.10. Each team has a supervisor, CM server analyst and required reviewer,
both explicitly assigned to CM1. Only the checked PowerShell tool is enabled.
Fresh workflows/sessions use the same task describing package CHQ00009 and CLIENT1's
address, without disclosing the injected fault. Analyst and reviewer query the SMS
Provider independently. The task expressly requires stating the lack of fresh
client-side observations.

The controller verifies that CHQ00009 is the existing harmless NP-SccmFive-Content
package, removes only its distribution to CM1, and restores it in finally. No new
deployment or client execution is triggered. Initial state: source version 1,
CM1 assignment present, status 0. Fault: package still exists, assignment and status
queries successfully empty. Restoration is checked for assignment and installed
version/status. Temporary exact-address TrustedHosts entries are restored afterward;
no wildcard, new network adapter, execution-policy change or certificate is needed.

## Initial finding and refinement

Execution `6815dc81-b0cd-419f-9774-d4e91ee47bf8` completed in 77.2 seconds,
12 model calls, 23 tool calls and two delegations. The team independently found
the missing distribution and proposed distributing the specific package followed
by status/version and client verification. It did not invent who removed it.

Boundary analysis was incomplete: it searched for an exact IP boundary while the
site uses an AD-site boundary, and projected an absent Role field. Guidance now
requires BoundaryType/Value, SMS_R_System.ADSiteName and the membership join, and
explicitly distinguishes an invented property from a missing role. Existing
distribution records for other packages can corroborate the DP identity without
proving this package's availability. Server discovery inventory is not a fresh
observation of the client's AD-site membership.

## Scope limits

This is real server-side evidence, not synthetic log replay. It does not establish
a new simultaneous CLIENT1 download failure/success: CLIENT1 has no agent member in
these server-focused runs. Cross-machine end-to-end acceptance remains separate.
Missing distribution is a demonstrated immediate blocker, not proof of the actor
or historical action that caused it. Broader boundary/fallback, hash, authentication
and content-transfer error scenarios are not all reproduced here.

Evidence and local controller/runner scripts are under `.runlogs/agent-cause/` with
prefix `server-content-`; scripts and source resource contain no stored passwords.

## Verified server outcomes

| Run | Execution | Result |
|---|---|---|
| Missing content repeat | `f2a9df26-a710-479d-a1d7-8e2306cf0c06` | Found absent package/DP association and status; correlated CLIENT1 AD-site inventory through boundary membership to CM1; proposed distributing CHQ00009 version 1 to CM1 and checking installed state/version. 82.3 seconds, 13 model calls, 29 tools, two delegations. |
| Restored content | `2b65f216-501d-4c44-b2b7-cefb3b43b870` | Found matching source version 1, state 0 and boundary chain; advised no redistribution and retained missing client-download proof. 115.1 seconds, 18 model calls, 42 tools, four delegations. |
| Bounded healthy repeat | `2425e57e-9bae-4e3e-bef5-4b00b9672945` | Same correct healthy conclusion with explicit source-path and inventory limitations. 60.4 seconds, 10 model calls, 21 tools, two delegations; no tool/member failures and no truncated outputs. |

The restored run first exhausted a member's context by serializing whole CIM
objects including nested metadata. The supervisor followed up and obtained a new
review before completion. This was recovered, not a clean first-attempt run.
Final skill guidance specifies bounded projections for each class and distinguishes
a configured source path from physical source-file existence (the earlier missing
run overstated that distinction). The guidance does not increase model budgets or
loosen read permissions. A further healthy repeat checks the smaller output shape.

Controller restoration passed: CHQ00009 has its original CM1 assignment, version 1,
state 0. TrustedHosts returned to its original `localhost` value. All generated
workflows are disabled after execution. MSI event auditing succeeded with zero
MsiInstaller events during each of the first three run windows. This is scoped
event evidence, not a universal claim that no external background changes occurred.
The audit was repeated including the fourth run: zero MSI events in all four windows.
The fourth run's 19 native commands were exclusively Get-CimInstance reads with
field projection/JSON formatting; the remaining two tool calls were delegation.

Final skill 1.0.9 is available in the dev registry. Server test workflows are retained
disabled for inspection; existing CLIENT1-only workflows are not silently retargeted.
The temporary workgroup lab TrustedHosts exception has been removed, so rerunning
these lab-IP workflows requires appropriate connection configuration. Production
domain-hostname/service-identity configuration is intentionally separate.
