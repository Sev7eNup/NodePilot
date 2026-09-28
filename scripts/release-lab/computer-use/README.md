# Computer-Use release acceptance

This is the human-facing UI gate for a built release. It operates the installed desktop shell on
the disposable desktop VM and the installed server through a real Edge session. Computer Use is
required for every UI assertion: screenshots, visible controls, mouse, keyboard, native file
pickers, tray menus and observable results. API, PowerShell and fixture scripts may prepare data or
corroborate an observation, but they cannot mark a UI case passed.

## Prepare and run

Copy `environment.example.json` outside the repository and fill in the full release commit,
installer paths, checksum file, disposable checkpoints, protected credential paths and fixture
probes. Development builds may set `allowUnsignedDevelopmentArtifact: true`; this is accepted
only for versions containing `-dev` and does not waive artifact checksums, environment isolation
or any UI case. Production configurations must leave it false and provide the publisher
certificate. The installer files must be the exact artifacts under test. Review
`coverage-review.json` whenever visible sources change; `catalog.py` fails closed on drift.

After the existing server and desktop install matrices finish from clean checkpoints, initialize a
run and inspect both targets. The server target is the configured `HYD-GW1` instance by default;
use `HYD-CM1` only when that machine is explicitly provisioned as the disposable NodePilot server.
The external CM1 fixture remains available for SQL and other integration cases.

The agent records a native session proof for both targets, starts one case at a time, drives its
steps through Computer Use and records an observation, every expected checkpoint, timings and
screenshots under `run\evidence`. The run can be interrupted after any case and resumed with the
same artifact, environment and catalog hashes. Failed and blocked cases require an explicit retry.

## Coverage and performance

The catalog is generated from the current route, Activity, settings and theme inventories and
contains 164 human-operated cases. It groups setup by target, role and page, reuses disposable
fixtures, runs independent integration probes in parallel, waits for visible state changes with
bounded timeouts and records navigation/wait/preparation seconds per case. Only one GUI case runs
at a time. Evidence is captured at checkpoints and on errors; continuous video is not required.
The final report lists the slowest cases and all blockers.

The `PILOT` list is for a quick dry run. A pilot is never a release pass: `verify` requires every
catalog case on both configured targets, an attested Computer-Use session, preflight, cleanup and
immutable evidence. Exceptions can only be recorded with direct user approval and evidence.

## Cleanup

After the last case, restore both disposable VMs to their named baselines, remove run-prefixed
workflows, accounts, credentials, variables, custom nodes, alert rules and fixture resources, remove
temporary access, and record evidence with `gate.py cleanup`. Cleanup itself is required for green.
