# Domain permissions and redaction acceptance

Date: 2026-09-22. Branch: `feature/ai-agent-activities`.

## Scope and environment

The user selected LDAPS and permissions first, explicitly leaving CM1's NTLM
policy unchanged. This is **not a Kerberos/browser SSO acceptance**.

A separate current-branch API on CM1 used the existing `CORP\q-sdvorch2$`
service identity, a dedicated `NodePilotAgentDomain20260922` database, and
LDAPS to DC1 on port 636 with certificate validation enabled. Existing AD test
accounts and groups were reused; no directory memberships were changed.
The temporary API used HTTP port 18766 on the lab adapter, restricted by firewall
to the development host. The installed CM1 service and `NodePilotFinal` database
were left intact. Actual model calls used `gpt-5.6-luna`.

## Results

106 final live checks passed. Evidence is in the ignored local directory
`.runlogs/agent-domain-acceptance/`; private session/configuration files there
must not be published.

| Area | Observed result |
|---|---|
| LDAPS authentication | Valid domain credentials accepted; wrong password and missing access-group membership rejected |
| Group mapping | Nested access membership and administrator mapping worked; ordinary users remained Viewers |
| Identity | Repeat login retained the same application user ID; tokens did not expose AD group lists |
| Folder permissions | User-specific and AD-group folder grants worked; unrelated users could not read workflow/run data |
| Execution and administration | Viewer execution and administrative settings/registries were forbidden |
| Run surfaces | Execution, steps, agent runs and paged event journal respected workflow access and redaction |
| Export/support surfaces | Authorized exports and support data were redacted; unauthorized access was rejected |
| Permission revocation | Old session became invalid (401); fresh login retained identity but could no longer access the folder |
| Live events | Authorized SignalR subscriber received two batches; unauthorized subscription failed and received no events |
| Settings | LLM API key returned only its mask |

An administrator with workflow-edit permission can retrieve the editable raw
workflow definition; this existing capability was distinguished from redacted
Viewer, execution, export and support views. Initial harness expectations for
that capability, authorization ordering and revoked sessions were corrected.

## Defect discovered and corrected

A harmless HTTP fixture returned synthetic password/API-key canaries inside a
JSON body. Further JSON serialization escaped those fields, bypassing the old
plain-text redactor. Canaries appeared in `tool_completed` and
`evidence_snapshot` events and could consequently enter model context.

`AgentContentRedactor` now traverses JSON values, recursively decodes embedded
JSON strings, masks sensitive keys through the existing central key classifier,
and applies the existing text redactor to leaves. It preserves ordinary JSON
types and envelopes and bounds recursion. Agent runtime sanitization and the
persistent journal both use it, including error/final-result handling.

Two regression tests failed before the fix and passed afterward. The focused
Engine suite passed 379 tests after the fix. The focused LDAP, Windows-auth,
agent-controller and workflow/folder authorization API suite passed 225 tests
earlier in this session (API code was unchanged). The API build succeeded.
Two real post-fix Luna runs passed, including actual SignalR delivery without
the synthetic secrets.

- Fixed execution: `82d88cb8-5eb8-42b4-98ca-acf43eb42b61`.
- SignalR execution: `31901a85-6342-4dea-951e-fe53af5401ea`.
- SignalR agent run: `50356a83-3cd7-40ec-a6d0-97a911dbeff8`.

## Cleanup and remaining acceptance

All four test workflows were disabled. The temporary LLM profile was disabled
and its stored API key cleared; the runtime LDAP service password was cleared.
The isolated scheduled API task was stopped; port 18766 has no listener.
The installed NodePilot service remains running, the incoming-NTLM policy value
remains absent, and host TrustedHosts was restored to `localhost`.

Automatic execution review rejected the combined full cleanup command with
`blocked by policy`, without a more specific reason. Consequently these test
resources remain and require cleanup:

- CM1 directory `C:\np-agent-domain-20260922`, including the static test LDAP
  configuration and temporary bootstrap credentials; its ACL is restricted to
  administrators, SYSTEM and the service identity.
- Database `NodePilotAgentDomain20260922` (not `NodePilotFinal`).
- Scheduled tasks `NP-AgentDomainAcceptance` and `NP-AgentDomainBootstrap`.
- CM1 firewall rule `NP-AgentDomainAcceptance` and host rule `NP-AgentDomainFixture`.

The runtime-secret clearing does not erase the static configuration in that
directory. The pending SQL cleanup script is retained locally in the evidence
directory. The denied cleanup was not retried through an alternative execution
mechanism.

Still unaccepted: Kerberos/browser SSO, actual AD membership-removal/offboarding
propagation, multi-controller/proxy deployments, and exhaustive browser/CLI/MCP
coverage of every surface. This test verifies structured secret fields and the
existing text patterns; it does not establish arbitrary-encoding DLP. Point 6
therefore has a validated LDAPS/permission subset, not complete production signoff.

## Follow-up: Kerberos and NTLM rejection (2026-09-22)

The user subsequently authorized the SSO/NTLM test. NTLM was tested exclusively
as a rejected protocol; no fallback support was added or enabled.

The same isolated CM1 API ran under its gMSA with the existing
`HTTP/cm1.corp.contoso.com` SPN. CLIENT1 connected directly over HTTPS on 18768
using an already trusted CM1 certificate, without skipping certificate checks.
For the test window, incoming NTLM was denied with
`RestrictReceivingNTLMTraffic=2`. A scheduled fallback restored the absent value
if the controlling test failed; each run also restored it in `finally`.

| Live case | Result |
|---|---|
| Kerberos, domain administrator test user | 200, Admin role, session cookie |
| Kerberos, ordinary admitted user | 200, Viewer role, session cookie |
| Kerberos, user outside admitted groups | 401, no session cookie |
| NTLM attempt via internal IP | 401, `WINDOWS_AUTHENTICATION_FAILED`, no session cookie |
| Ambient Windows credentials on CLIENT1 | LabAdmin process used `UseDefaultCredentials`; login and subsequent cookie-authenticated `/auth/me` both 200; HTTP Kerberos ticket recorded |

Security events 4624 explicitly identified Kerberos for the three test users.
NTLM Operational event **4002** identified the isolated NodePilot executable and
gMSA as the blocked recipient. This is evidence of actual NTLM rejection, not
merely an unanswered HTTP authentication challenge. The original request also
contained an NTLM type-1 token transported under Negotiate.

The initial test exposed a real error: SSPI rejection escaped the Negotiate
handler as `AuthenticationFailureException: Unsupported`, resulting in HTTP 500
and development exception details. The registered Negotiate failure event now
handles the response as a generic 401, issues no session, and logs only the
exception type. A regression test exercises the registered event and verifies
the response is handled without exception detail or cookies. All **42 focused
authentication tests passed**, followed by the five successful live outcomes.
The dev API was restarted with this fix and its readiness endpoint returned 200.

The first ambient-credential attempt using `nptest.alice` could not start because
of Windows logon rights (Task Scheduler error 1385); a separate process launch
was also denied. No account rights were expanded. Following the user's LabAdmin
credential instruction, a temporary LabAdmin task succeeded. Existing
`NodePilot-Users` and `NodePilot-Admins` groups were admitted/mapped **only in the
isolated test configuration**, then restored; AD memberships were unchanged.
RemoteSigned applied only to the temporary client PowerShell process.

Final restoration verified: incoming-NTLM value absent as before, test API
stopped, no test listeners, new SSO firewall rule and rollback task removed,
CLIENT1 task/directory removed, installed CM1 service running, and host
TrustedHosts restored to `localhost`. The previously documented isolated
database/directory cleanup remains pending; this follow-up did not remove it.

Remaining limits: this verifies direct-Kestrel Kerberos and ambient Windows
credentials, **not a browser login without prompts or the HAProxy route**.
Those end-to-end deployment checks and AD offboarding propagation remain open.
It also does not certify NTLM disabled permanently on CM1: the temporary policy
was deliberately restored. Production Windows SSO still requires an effective
permanent host/domain NTLM block.

Protocol/policy references: [Microsoft incoming NTLM policy](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-security-restrict-ntlm-incoming-ntlm-traffic),
[ASP.NET Core Negotiate failure event](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.negotiate.negotiateevents.onauthenticationfailed?view=aspnetcore-10.0).
Local evidence: `.runlogs/agent-sso-acceptance/results.json`, `restored.json`,
`tests.log`, and the reproducible `run.ps1` harness.
