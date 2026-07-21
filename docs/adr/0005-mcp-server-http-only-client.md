# ADR 0005 - MCP Server as HTTP-Only Client

**Status:** Implemented - 2026-06-22
**Scope:** `nodepilot-mcp` architecture and safety model.

## Kontext

AI agents need a controlled way to inspect, edit, validate, and run NodePilot workflows. Adding a
second backend execution path would duplicate authorization, validation, audit, redaction, and
workflow semantics. The existing CLI already proves the intended client model: authenticate once and
call the REST API.

## Entscheidung

`NodePilot.Mcp` is a separate dotnet tool and **HTTP-only client** against the existing NodePilot
REST API, like the CLI. It does not reference `NodePilot.Api` and does not create a privileged
backend path. Local graph analysis can run in-process against shared `NodePilot.Core` definitions,
but persistence and execution always go through REST endpoints.

Safety guardrails are part of the architecture:

- stdio is the default transport.
- The server reuses the CLI session or explicit headless token config.
- Destructive/admin tools are not registered unless `NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true`.
- Workflow definitions are redacted before they reach the agent, and masked secrets are restored
  from the stored definition during update/publish.

## Konsequenzen

- API authorization, audit, and validation remain the single enforcement point.
- DTOs are intentionally copied into the MCP project for independent packaging; parity tests guard
  drift.
- MCP-specific analysis mirrors some frontend lint/databus rules; drift tests guard the mirror.
- Adding an endpoint-driven capability means adding an API client method, a tool method, and tests.

## Referenzen

- [../mcp-server.md](../mcp-server.md)
- [../enterprise-features.md](../enterprise-features.md)
