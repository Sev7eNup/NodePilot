# Agent workflow portability acceptance

Date: 2026-09-23. Branch: `feature/ai-agent-activities`.

## Implemented behavior

Native workflow export v1 now adds optional `sourceId` and `dependencies` fields
to each workflow item. Keeping the manifest with its item preserves it when the
designer combines single exports into a bulk download. API, CLI and MCP DTOs
retain both fields. No database migration is required.

The reference walker visits node machine/credential bindings, individual agent
skills/tools, nested team member bindings/skills/tools, and GUID references in
`startWorkflow` and `forEach`. It does not rewrite arbitrary JSON, prompts or tool
arguments. Portable descriptors contain only resource metadata; passwords, MCP
secrets/arguments/endpoints, skill archives and MCP read approvals are not bundled.
Existing definition secret redaction remains unchanged.

| Resource | Automatic match on destination |
|---|---|
| Machine | Name plus hostname, WinRM port and TLS mode |
| Credential | Name plus domain/account |
| Skill | Name, exact version and SHA-256 |
| MCP server | Name and transport; destination administrator owns its endpoint and credentials || Workflow | Newly imported copy when in the same package; otherwise one accessible workflow with that name |

Only one exact metadata match is accepted. An exported source GUID alone never
authorizes retaining a destination resource, even if that GUID happens to exist.
For intentional differences, an operator can set `dependencies[].targetId` before
import, or select resources in the designer afterward. Explicit mappings still
require a destination resource of the correct kind; disabled skills/MCP servers
and inaccessible workflows cannot be selected by the importer.

Missing, ambiguous, duplicate or absent descriptors produce import diagnostics
including resource name, kind and definition path. The reference becomes
`Guid.Empty`, rather than null: this avoids accidentally falling back to localhost
or inherited credentials. Publish and Enable reject these placeholders. Every
import remains disabled, including a fully mapped import. Unresolved skill IDs
are visible/removable in the editor rather than becoming hidden selections.

Bulk import allocates workflow IDs before remapping. It preserves links to new
copies despite name collisions, duplicate source workflow IDs are rejected, and
a child skipped for invalid input cannot silently fall back to an existing
namesake. Workflow metadata queries respect folder read permissions.

## Validation

| Check | Result |
|---|---|
| API portability/import/export/edit/lifecycle tests | 105 passed |
| CLI roundtrip, DTO parity and agent integration tests | 11 passed |
| MCP roundtrip, workflow tools and agent tools | 13 passed |
| Workflow-list UI tests, including bulk manifest preservation | 83 passed |
| Chromium agent designer/settings E2E, including unresolved-skill removal and saved state | 3 passed |

The controller roundtrip uses two independently initialized databases, with
different resource GUIDs and matching metadata. It checks every nested reference
kind and verifies the export does not contain seeded secret material. Negative
cases include missing resources, ambiguity, legacy metadata absence, duplicate
declarations, matching GUID with a different identity, changed skill version/hash,
explicit mapping, inaccessible workflow targets, and a skipped child. Both
publishing and enabling unresolved imports are rejected.

The parity check also exposed pre-existing missing `Outcome`/`OutcomeReason`
fields in the CLI/MCP agent-run DTOs; those fields now survive client responses.

Frontend production build and targeted whitespace checks passed. The development
API was restarted with the change; API readiness and UI returned HTTP 200.
No LLM or remote-machine execution was needed for these portability tests.

## Deliberate limits

This is dependency discovery and safe reassignment, not deployment of external
resources. Install skill packages and register MCP servers/credentials/machines
on the destination first. Read approvals remain destination-owned. Review the
destination machine's default credential and any explicitly selected service
identity before enabling.

Legacy files still import, but their static GUID references require reassignment
because they lack portable identity metadata. Paths, working directories, model
names, template expressions, globals and workflow calls expressed as names are
not converted. Existing redaction of opaque fields, including agent instructions,
still requires author review/replacement after export. A new graphical mapping
wizard is not included; current designer selectors and optional `targetId`
provide the correction paths. This is focused feature acceptance, not a full CI
or release certification.

Local test logs: `.runlogs/portability-tests.log`, `portability-cli.log`,
`portability-mcp.log`, `portability-ui-tests.log`, `portability-e2e.log`.
