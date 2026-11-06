# Agent instructions

Follow `CLAUDE.md` for repository conventions.

## Audit duplicate handling

Before a security, architecture, or test audit, read `SECURITY.md`, `docs/roadmap.md`, and the local `.audit-private/later.md` if it exists. Scan the source normally. Match a candidate against the backlog by root cause, affected surface, and impact before calling it new. An unchanged known item belongs in the existing backlog, not a second finding or issue; mention its ID and status separately in the audit summary. Report it again when evidence shows a new variant, greater impact, a regression, or that the recorded fix failed. Never treat a backlog entry as proof that its code is safe or fixed.

`.audit-private/` contains unpublished audit notes. Keep its details out of public issues, pull requests, and documentation. Follow `SECURITY.md` for suspected vulnerabilities. If the local backlog is absent, state that duplicate matching was unavailable.

## Finding open audit points

When asked for "offene Punkte aus Audits", "Audit-Findings", the "Später-Liste", "open audit points", or similar, read `.audit-private/later.md` even though Git ignores it. Also check audit-origin items in `docs/roadmap.md` and any referenced open issues; use `docs/security-findings.md` to identify resolved findings. Deduplicate across these sources, verify status against the current code before calling an item open, and distinguish confirmed findings from conditional findings and investigation candidates. Include the local backlog IDs in the answer. If the local file is missing, say that its entries could not be checked.
