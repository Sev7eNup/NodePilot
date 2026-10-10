# Skill execution follow-up — 2026-09-19

Branch: `feature/ai-agent-activities`. This report covers the existing skill runner's
policy preflight, error reporting and updated read-only examples.

## Automated verification

Three regression cases first failed against the previous runner: Restricted was not
rejected before staging, a process error was returned as normal tool output, and the
shipped diagnostic script failed the current read-language validator.

After correction, 171 selected Engine tests and 19 AI configuration/runtime tests pass.
The API builds with zero errors. Tests execute actual Windows PowerShell processes via
a simulated remote transport, using process-only policy fixtures; they do not modify
the host's persistent policy or certificate stores. They cover:

- Restricted rejection before creating a target working directory.
- AllSigned rejection of unsigned scripts.
- Real script success, literal argument binding, byte-preserving transfer and SHA-256.
- Detection of a staged script changed between calls, even with cached uploads.
- Failure and cleanup for a missing input file, plus cleanup after permission revocation.
- Signature/publisher decision cases (metadata fixtures, not a signed remote execution).
- Missing/invalid exit status and timeout classification.
- Runtime `tool_failed`, no automatic replay, redaction and bounded valid JSON details.
- Compatibility of shipped diagnostic scripts with the read-language validator.

## Actual model / dev API

The dev API at localhost:5000 was rebuilt and restarted. The installed service was
untouched. The local test temporarily enabled the already-supported service identity
option, explicitly selected it on each test agent, and restored its original value.

| Case | Execution | Result |
|---|---|---|
| Hash a harmless existing fixture through a packaged script | `a519a21d-9e7e-4b16-877b-4161f9ddb9e2` | 9.7 s, 3 model calls, 2 tools; hash independently matched |
| Read an intentionally absent fixture | `8cbb1db2-0d03-40fc-abf0-ae5e0235bd17` | 8.2 s, 3 model calls, 2 tools; one `tool_failed`, code `process_failed`, no script retry |

The second agent completed its report of the failure; workflow success does not mean
the script succeeded. Its journal contains no `tool_completed` for the failed script.
Test workflows and imported fixture skills were disabled. The input hash is unchanged.
Evidence: `.runlogs/agent-live/skill-policy-{hash,missing}-result.json` and
`skill-policy-summary.json`. Two initial harness attempts stopped before any model/tool
call because service identity was disabled or its setting had not propagated yet.

## Actual CLIENT1 / WinRM

CLIENT1 and DC1 were started from their original off state. A temporary NIC, narrowly
scoped TCP 5986 firewall rule and matching HTTPS certificate provided host access to
192.168.240.101. The dev API's existing machine/credential binding was reused and its
connection test confirmed CLIENT1. Agent tools used real HTTPS WinRM as CORP\labadmin;
PowerShell Direct was used only by the independent test controller for setup/verification.

| Case | Execution | Result |
|---|---|---|
| Original effective Restricted | `ff2f91ec-e635-43d4-87df-038f2172fdec` | 5.0 s, 3 model calls, 2 tools; script rejected with `execution_policy_blocked` |
| Temporarily authorized RemoteSigned | `ca913a6c-3a87-4e45-94fe-c3a0dc45cd12` | 6.0 s, 3 model calls, 2 tools; same package ran once with exit code 0 |

Independent inspection confirmed no target staging for the blocked case, matching
Windows Update service states for the successful case, and no staging remaining after
execution. No MsiInstaller 1035 reconfiguration events occurred in the positive test
window. No SCCM fault or repair was introduced for this follow-up. The agent did not
retry or substitute an inline command. Evidence is in `.runlogs/skill-winrm/`.

The user explicitly authorized the controller to set CurrentUser policy temporarily to
RemoteSigned. It was restored to Undefined (effective Restricted). The original HTTPS
listener was restored and the guest test certificate, firewall rule, IP, staging and
temporary NIC were removed. Test workflows and fixture skills are disabled.

Host certificate removal was blocked by the tool approval policy and requires manual
removal of fingerprint `A3845E4553CE137AD871333265C5F6FB97A1C1B7` from CurrentUser Root.
Both VMs are restored to Off, as verified in `cleanup.json`. Certificate removal is the
remaining manual cleanup item until its absence is verified. An initial draft workflow
with an incorrect nested target binding was rejected before execution and disabled;
the final single-agent fixtures use the normal node-level machine/credential fields.

Real signed execution under AllSigned still requires a trusted signing setup and has
not been demonstrated by metadata tests or by the RemoteSigned acceptance above.
