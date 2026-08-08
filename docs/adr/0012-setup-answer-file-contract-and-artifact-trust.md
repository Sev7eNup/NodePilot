# ADR 0012 - Setup Answer-File Contract and Pinned-Signer Artifact Trust

**Status:** Implemented — 2026-08-08
**Scope:** Deployment — the contract between the GUI setup wizard and the PowerShell install
scripts, and the trust model for signed release artifacts.

## Kontext

The server installer grew a second front end: the Inno Setup wizard (`deploy/server/`) and
unattended installs (SCCM answer files) both drive the same PowerShell scripts that operators
previously called by hand. That created two problems with lasting consequences.

**1. The wizard-to-scripts hand-off had no contract.** The wizard collects values, writes an
answer file, and an adapter splats them onto `Install-NodePilot.ps1`. PowerShell's own
parameter binding is too forgiving for this seam: a misspelled key in an unattended answer
file is silently ignored and the defect surfaces mid-installation on a production host —
after services, ACLs and firewall rules have already been touched — instead of before it
starts. Static text checks cannot catch a mis-splat; only executing the seam can.

**2. Installing a signed artifact required a machine-wide trust decision that bought
nothing.** Release artifacts carry a detached CMS signature (`.p7s`) over a manifest, made
with a self-signed publisher certificate whose thumbprint is pinned (compiled into the
setup, printed in release notes). Verification originally also built and validated the X.509
chain — which, for a self-signed certificate, can only succeed if that same certificate has
been imported into `LocalMachine\Root` first. A field install failed exactly this way: all
nine preflight rows green, then abort-and-rollback at `CheckSignature` with an
untrusted-root error. The chain validation only re-confirmed what the thumbprint pin already
established, at the price of a permanent, machine-wide trust change on every target — and
the certificate is not a CA (no basic constraints, KeyUsage `DigitalSignature`, EKU
code-signing only), so trusting it could never mean more than trusting it.

## Entscheidung

**Answer-file contract as a single dot-sourceable unit.** `deploy/SetupContract.ps1` owns
the schema (dotted-path `Required`/`Optional` key tables per mode, `install` and `update`),
the splat mapping onto `Install-NodePilot.ps1`, SecureString construction for secret-bearing
values, and the INI result file the wizard reads back. The parser is deliberately stricter
than PowerShell binding: an unknown or misspelled key fails the run **before** installation
starts, naming the offending dotted path. `Get-NodePilotAnswerFileKeys` exposes the table so
the behavioural test and the documentation read the same source of truth. The contract is
guarded by `Test-SetupAdapter.ps1` — a behavioural test that runs non-admin, offline and
without a database, executed in CI under **both** Windows PowerShell 5.1 and PowerShell 7
(the wizard ships 5.1 hosts; developers run 7).

**Artifact trust = pin + explicit checks, no chain, no store mutation.**
`Assert-NodePilotSignedArtifact` verifies the detached CMS (`CheckSignature($true)`),
requires the signer to be exactly the pinned thumbprint, and enforces explicitly what the
chain build used to imply: the certificate's validity window, the code-signing EKU, and —
newly, because the EKU alone never answered it — a KeyUsage that permits
`DigitalSignature`/`NonRepudiation`. Deliberately given up, and recorded at the function so
it stays a decision: trust-anchor validation, time nesting across a chain, basic/name/policy
constraints, and revocation — for a self-signed end-entity certificate with no CRL
distribution point, only the anchor was ever real. The preflight readiness row runs the same
checks the installer will run and blocks on them; only a chain failure that is exclusively
`UntrustedRoot`/`PartialChain` remains an optional yellow row whose root-import fix is
offered but never pre-ticked — the import changes how Windows treats these installers from
then on, it does not authenticate the setup that is already running.

Alternative weighed and rejected: having the setup import the publisher certificate into
`LocalMachine\Root` itself. That automates the machine-wide change instead of eliminating
it, and validates nothing the pin does not already establish.

## Konsequenzen

- Installs no longer mutate machine trust. The optional import remains an operator choice
  with its actual meaning stated in the wizard.
- **Signing with a CA-issued certificate would now lose chain validation entirely** — if the
  publisher model ever changes away from self-signed, this decision must be revisited.
- The pin is a SHA-1 thumbprint: the published, fixed value — "the expected publisher"
  rather than "provably this exact certificate". A SHA-256 pin over `RawData` is the named
  stronger follow-up; it is a change of its own because the value is compiled into the setup
  and printed in every release note.
- Every contract change (new wizard field, new install parameter) updates the
  `SetupContract.ps1` key table and `Test-SetupAdapter.ps1` in the same change — unknown
  keys must keep failing fast, or unattended installs regress to mid-install failures.
- Signature verification is covered by behavioural tests using in-memory certificates (no
  certificate store), including the two cases the chain used to catch (expired, untrusted)
  and the KeyUsage case nothing caught before; the suite was mutation-checked.

## Referenzen

- Contract: [`deploy/SetupContract.ps1`](../../deploy/SetupContract.ps1),
  adapter [`deploy/Invoke-NodePilotSetup.ps1`](../../deploy/Invoke-NodePilotSetup.ps1),
  guard [`deploy/Test-SetupAdapter.ps1`](../../deploy/Test-SetupAdapter.ps1)
- Artifact trust: [`deploy/ArtifactSecurity.ps1`](../../deploy/ArtifactSecurity.ps1)
  (`Assert-NodePilotSignedArtifact`), readiness in [`deploy/Preflight.ps1`](../../deploy/Preflight.ps1),
  guard [`deploy/Test-ArtifactSecurity.ps1`](../../deploy/Test-ArtifactSecurity.ps1)
- CI runs all three deploy self-tests under PowerShell 5.1 **and** 7
  (`.github/workflows/ci.yml`, backend job)
- Wizard documentation: [`deploy/server/README.md`](../../deploy/server/README.md)
- Key commits: `da9f49f5` (pinned trust without chain), `a2d75442` (preflight learns the
  distinction), `a816f485` (setup log truthfulness)
