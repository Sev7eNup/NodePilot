# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Layout

This is a single-context repo.

Read these when present:

- `CONTEXT.md` at the repo root
- `docs/adr/` for architecture decision records

If these files do not exist, proceed silently. Do not suggest creating them upfront. The producer skill (`grill-with-docs`) creates them lazily when terms or decisions are resolved.

## Use the glossary's vocabulary

When output names a domain concept, use the term as defined in `CONTEXT.md`. Avoid drifting to synonyms the glossary explicitly avoids.

If the concept is missing from the glossary, either reconsider the term or note the gap for `grill-with-docs`.

## Flag ADR conflicts

If output contradicts an existing ADR, surface it explicitly rather than silently overriding.
