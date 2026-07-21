# ADR 0007 - API Validation And Error Contract

**Status:** Accepted - 2026-06-29 · Amended - 2026-07-07 (error-shape guidance revised, see Amendment)
**Scope:** HTTP API validation, error responses, and audit guardrails.

## Kontext

The API used a mix of ad-hoc error payloads (`{ message }`, `{ error }`, `{ code, message }`) and
controller-local validation. This made clients parse several shapes and left the boundary between
request-shape validation, HTTP composition checks, and domain invariants implicit.

## Entscheidung

Client-visible 4xx responses from API controllers use RFC 7807 `ProblemDetails`.

- `ApiProblemDetailsResultFilter` (registered globally in `Program.cs`) is the **authoritative
  normalization point**: every `ObjectResult` with status >= 400 is converted to
  `application/problem+json` at the HTTP boundary, preserving extra payload fields as extensions.
- Controllers keep emitting the concise in-controller payloads (`{ message }`,
  `{ code, message }`, or ad-hoc objects with extra context fields) — the filter owns the wire
  shape. `ApiProblems` remains available for call sites that need full control over
  `title`/`type`/extensions, but its use is optional, not required.
- `ProblemDetails.Extensions["code"]` carries the stable machine-readable error code.
- `Detail` is the human-readable explanation. Extra request context may be added as extensions when
  it is safe for clients and logs.
- The end-to-end guarantee is guarded by `ApiProblemDetailsPipelineTests` (Api.Tests/Hosting),
  which boots the real `Program` pipeline and asserts a legacy payload arrives as ProblemDetails.

Validation belongs at the narrowest layer that has enough information:

- **DTO/DataAnnotations / model binding:** syntactic request shape, required body fields, enum and
  primitive parsing, and local single-property format or length rules.
- **Controller:** route/body/user composition, cheap authorization-adjacent checks, translating
  service validation into HTTP status codes, and checks that only make sense for one endpoint.
- **Service/domain:** business invariants, cross-entity consistency, transaction-scoped rules, and
  reusable validation that must hold outside HTTP.
- **Persistence:** database constraints such as unique indexes and foreign keys. Map expected
  constraint failures only when the API needs a stable user-facing error.
- **Client, designer, CLI, and MCP:** preflight UX only. These checks improve feedback but are not
  authoritative.

Mutating API actions should emit an audit event or carry an explicit exemption. The architecture
test `MutatingActionAuditCoverageTests` guards this at the controller layer.

## Konsequenzen

- API clients can prefer `ProblemDetails.detail`, `ProblemDetails.title`, and the `code` extension
  instead of branching over legacy payload shapes.
- Validation remains close to the authority for the rule and avoids duplicate business logic in
  controllers.
- The pipeline test above must stay green; removing or reordering the result filter breaks the
  external error contract for every controller at once.

## Amendment (2026-07-07)

The original decision asked new/touched controller code to call `ApiProblems` directly, with the
result filter as a transitional shim. A repository coherence audit (2026-07-07) found the opposite
reality: 1 of 31 controllers adopted `ApiProblems` (`MachinesController`), while all others —
including controllers written *after* this ADR (`AlertingController`, `CustomActivitiesController`)
— kept the concise legacy payloads. The wire contract stayed uniform the whole time because the
filter normalizes everything.

Decision revised: the filter is promoted from transitional shim to the permanent, authoritative
mechanism. The in-controller payload shape is a controller-internal detail; no migration of
existing call sites is planned, and new code is not required to use `ApiProblems`. This makes the
documented convention match the dominant practice instead of prescribing a migration nobody
executes.

