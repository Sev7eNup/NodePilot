<!-- Keep this concise. Delete sections that don't apply. -->

## What & why

<!-- What does this change do, and what problem or need does it address? -->

## How

<!-- Notable implementation choices, trade-offs, or anything a reviewer should look at closely. -->

## Testing

<!-- Which suites did you run, and what did you add? Every behavioral change needs matching tests. -->

- [ ] `dotnet test` (affected backend suites) green
- [ ] `npm run test:run` (affected specs) / `npm run lint:ci` green (if frontend touched)
- [ ] Guard/parity tests run for whatever this change touches (catalog, DTO, migration, audit,
      trigger, settings) — see `CLAUDE.md`, *Build & Test*
- [ ] New/updated tests cover the change
- [ ] i18n strings added to **both** `de` and `en` (if UI strings touched)

## Checklist

- [ ] Branched off `main`; no direct commits to `main`
- [ ] Architecture guard tests still pass (catalog/DTO/RBAC/audit/admin-settings parity)
- [ ] Added an ADR under `docs/adr/` if this is a lasting architectural decision
      (see [`docs/adr/README.md`](../docs/adr/README.md))
- [ ] Docs updated (README / CLAUDE.md / docs/ / docs-ui `content/de/` **and** `content/en/`) if behavior or config changed
