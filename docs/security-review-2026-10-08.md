# Focused security review — 2026-10-08

Source baseline: `e0e83fe`, with the dependency/reporting changes in this working tree.
Scope: role and folder enforcement, credential exposure, execution identities, WinRM,
agent tool authorization and the frontend dependencies raised by the repository report.
Method: local adversarial source review plus selected existing regression tests. This is
not an independent external penetration test or a complete review of every endpoint.

## Critical

No concrete finding established in the reviewed scope.

## High

### Dependency: source-map-js denial of service — corrected

- **Description:** `npm audit` identified `source-map-js` 1.2.1, a development dependency,
  affected by [GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q).
- **Attack Scenario:** Malicious indexed source-map offsets can block a consuming Node
  process. Reachability from NodePilot's production API was not established; the observed
  dependency belongs to frontend build/test tooling.
- **Affected Files:** [UI lockfile](../src/nodepilot-ui/package-lock.json).
- **Exact Root Cause:** The lockfile retained the affected transitive version.
- **Fix Recommendation:** Resolved to 1.2.2 within the existing dependency ranges. No new
  package or broad dependency upgrade was introduced. Keep the existing npm audit CI gate.

## Medium

No concrete finding established in the reviewed scope.

## Low

### Dependency: DOMPurify affected release — corrected

- **Description:** Monaco's DOMPurify resolution was 3.4.13, affected by
  [GHSA-6688-9rhm-gjv2](https://github.com/cure53/DOMPurify/security/advisories/GHSA-6688-9rhm-gjv2).
- **Attack Scenario:** The upstream issue requires sanitizing a DOM node with `IN_PLACE`
  and then serializing/reparsing a removed raw-text root. A corresponding NodePilot exploit
  path was not established. The inspected Monaco wrapper uses DOM-fragment/trusted-HTML
  output rather than establishing the advisory's `IN_PLACE` precondition.
- **Affected Files:** [UI manifest](../src/nodepilot-ui/package.json),
  [UI lockfile](../src/nodepilot-ui/package-lock.json).
- **Exact Root Cause:** Override minimum and locked resolution predated the patched release.
- **Fix Recommendation:** Override minimum raised to `^3.4.16`, resolved version 3.4.16.
  Verify the built editor and Markdown rendering when updating this sanitizer.

## Summary

- **Total Findings:** Two affected dependencies, both corrected. Severity above is the
  advisory severity, not a claim of demonstrated NodePilot exploitability.
- **Estimated Breach Risk:** Not quantified by this review. No additional concrete
  application authorization bypass was established in the inspected paths.
- **Most Likely Attack Vectors:** Compromised trusted-author/service identities; excessive
  target-account rights; prompt injection affecting permitted reads or external disclosure;
  incorrectly trusted MCP services. These are residual trust/deployment concerns, not newly
  demonstrated vulnerabilities.
- **Top 5 Immediate Actions:** Keep patched dependencies; expose the global credential and
  trusted-Operator contract; retain negative authorization tests; inspect retry/flake evidence;
  validate the documented cases on an isolated Production-mode installation with an
  independent reviewer.

The consolidated [threat model](threat-model.md) records these assumptions and the exact
remaining external validation. `SECURITY.md` now links it and corrects its blanket claim
that every local script runs in-process. Credential API metadata, global role caps and
folder access were traced through `CredentialsController`, `ResourceAuthorizationService`,
`ExecutionsController`, `ExecutionDebugController` and `SubWorkflowAuthorizationResolver`.
Target/credential selection, policy checks and MCP revalidation were inspected in
`AgentTarget`, `AgentToolHost`, `AgentExternalReadPolicy` and `WinRmSessionFactory`.

### Executed checks

| Check | Result |
|---|---|
| `npm audit --audit-level=moderate` in UI workspace | Zero reported vulnerabilities after both updates |
| API role/folder/credential/sub-workflow/external-authorization regression selection | 43 passed, zero skipped |
| Engine agent permission/target/HTTP/workflow, WinRM guard/span and output-redaction selection | 223 passed, zero skipped |
| Production-bundle `e2e/script-editor.spec.ts` | 8 passed, no retries |
| Editor/Markdown Vitest selection (four files) | 37 passed |
| Reliability reporting tests, including a real Playwright retry/failure probe | 8 passed |
| UI lint / workflow checks | No lint errors (10 existing warnings); YAML and CI-gate job coverage valid |

Reproduce the backend selections from the repository root:

```powershell
dotnet test tests/NodePilot.Api.Tests/NodePilot.Api.Tests.csproj -c Release --filter 'FullyQualifiedName~RoleMatrixSmokeTests|FullyQualifiedName~ResourceAuthorizationServiceTests|FullyQualifiedName~CredentialsControllerTests|FullyQualifiedName~SubWorkflowAuthorization|FullyQualifiedName~ExternalAuthorizationEvaluatorTests'
dotnet test tests/NodePilot.Engine.Tests/NodePilot.Engine.Tests.csproj -c Release --filter 'FullyQualifiedName~AgentTargetTests|FullyQualifiedName~AgentPermissionPolicyTests|FullyQualifiedName~AgentReadCallsTests|FullyQualifiedName~AgentReadWorkflowTests|FullyQualifiedName~WinRmSessionFactoryGuardTests|FullyQualifiedName~WinRmSessionSpanRedactionTests|FullyQualifiedName~OutputRedactorTests'
```

The builds emit existing compiler/analyzer warnings. The initial Debug API build could not
replace DLLs held by the running development API; the Release build and selected tests passed.
No production instance, directory account, remote machine or installer was changed.
Real certificate negotiation, domain permissions, provider data handling and the release-lab
matrix were not executed by this review. GitHub artifact history will begin accumulating
after the updated workflow runs; local reporter validation is not historical CI evidence.
