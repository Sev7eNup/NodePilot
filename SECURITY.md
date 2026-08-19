# Security Policy

NodePilot executes PowerShell across a Windows estate and holds the credentials to do it. Reports
about it are welcome and taken seriously.

## Reporting a vulnerability

**Use GitHub's private vulnerability reporting:**
[Report a vulnerability](https://github.com/Sev7eNup/NodePilot/security/advisories/new).

That channel is private until an advisory is published. Please do **not** open a public issue, a
pull request, or a discussion for a suspected vulnerability — a public issue is a disclosure.

What helps a report land:

- affected version (`np --version`, the installer file name, or the commit)
- install shape — desktop app, Windows service, or from source
- what an attacker gains, not only what misbehaves
- the smallest reproduction you have: a request, a workflow JSON, a config snippet

NodePilot is a single-maintainer project. Expect a first reply within **7 days** and an assessment
within **30 days**. If a report is confirmed, the fix ships in the next release and the finding is
recorded in [`docs/security-findings.md`](docs/security-findings.md) with its fix and test. Credit
in the advisory if you want it, none if you prefer.

## Supported versions

| Version | Supported |
|---|---|
| Latest release | ✅ |
| Anything older | ❌ — upgrade first, then report if it persists |

Only the newest release receives fixes. There are no maintenance branches; NodePilot upgrades in
place and rolls back on failure, so "upgrade first" is a realistic ask rather than a deflection.

## Scope — what is a vulnerability here, and what is not

Some behaviour that looks like a hole is a documented product decision. Reporting these is fine,
but they will be closed as intended rather than fixed:

- **Operator can run code as the service identity.** `Operator` is deliberately a *trusted
  automation author*. A local activity without a target machine runs in-process under the NodePilot
  service identity — that is what agentless local automation means. **Folder RBAC scopes which
  workflows a user sees; it is not a sandbox around the code an Operator writes.** Escalation from
  Viewer, or across a folder boundary the RBAC model claims to hold, *is* in scope.
- **`localhost` / `127.0.0.1` / `::1` without credentials skips WinRM** and runs in-process. Same
  reasoning, same conclusion.
- **The release installers are signed with a self-signed publisher certificate**, so Windows
  reports an untrusted root and SmartScreen warns on a downloaded file. This is a cost decision,
  not a defect. The thumbprint is published in the release notes and the artifact is verified
  against a pinned thumbprint at install time. See
  [the deployment guide](docs/deployment-guide.md#first-run-the-smartscreen-prompt).
- **Development configuration is deliberately relaxed.** `appsettings.Development.json` turns the
  hardening flags off so local iteration works without certificates. A finding that only reproduces
  under `ASPNETCORE_ENVIRONMENT=Development` is not a product vulnerability — a finding that a
  hardening flag *fails to hold* in production is.

In scope, and worth reporting: authentication and session handling, privilege escalation between
roles, cross-tenant or cross-folder data exposure, SSRF and injection paths, secret leakage into
logs, audit or output, credential handling at rest, and anything that lets an unauthenticated
caller reach an authenticated surface.

## What NodePilot does on its own behalf

For context, not as a promise that it is sufficient — every PR runs CodeQL (C# and TypeScript),
Gitleaks over the full reachable history, a transitive NuGet vulnerability gate, and
`npm audit` across three workspaces. Resolved findings are registered in
[`docs/security-findings.md`](docs/security-findings.md); the security roadmap lives in
[`docs/roadmap.md`](docs/roadmap.md).
