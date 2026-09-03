# Releasing NodePilot

The release build is manual and local, on purpose: signing requires the code-signing certificate,
and putting a signing key on a hosted runner buys convenience at the cost of the one thing the
signature is supposed to prove. This checklist is what replaces the automation.

It exists because two things went wrong before it did. The publisher certificate stopped being
attached after 1.2.8 — nobody had written down that it was a manual upload — which broke the exact
verification step the deployment guide calls "the trust decision". And the npm manifests sat at
1.2.5 while the product was on 1.2.10, so the installer said one version and the executable's file
properties said another. Both are now guarded by tests, and both are steps below.

---

## 1. Pick the version

`v<major>.<minor>.<patch>`. Tags carry the `v`, release titles do not (`NodePilot 1.2.10`).

The version lives in **four** files and they must agree:

| File | Field |
|---|---|
| `Directory.Build.props` | `<Version>` — the source of truth for the whole backend |
| `src/nodepilot-ui/package.json` | `version` |
| `src/nodepilot-desktop/package.json` | `version` — this one becomes the .exe's file properties |
| `src/nodepilot-docs-ui/package.json` | `version` |

Bump all four, then prove it:

```powershell
dotnet test tests/NodePilot.Api.Tests --filter "FullyQualifiedName~PackageVersionParity"
dotnet test tests/NodePilot.Cli.Tests --filter "FullyQualifiedName~CliVersion"
```

Those two tests exist solely to stop a partial bump from shipping. If they pass, the four files
agree with each other and with the CLI.

## 2. Make sure the tree is releasable

```powershell
dotnet test                                        # full backend suite
cd src\nodepilot-ui;      npm run lint:ci; npm run test:run; npm run test:e2e
cd src\nodepilot-docs-ui; npm run lint:ci; npm run test:run; npm run build
cd src\nodepilot-desktop; npm run test:run
```

A release cut is one of the few times the **full** suite is the right call rather than a scoped
run — see the testing section in `CONTRIBUTING.md`. Confirm CI is green on `main` as well; the
local nightly job runs against its checked-out tree and is not a status check on `origin/main`.

## 3. Update the changelog

Add the new version to `CHANGELOG.md` before building, so the tag and the changelog cannot drift.

## 4. Build the artifacts

One command produces everything, signs it, and writes the checksum file:

```powershell
.\deploy\Build-Artifact.ps1 `
  -Version 1.2.11 `
  -SigningCertificateThumbprint <thumbprint> `
  -InstallerSigningCertificateThumbprint <thumbprint> `
  -IncludeServerInstaller `
  -IncludeDesktopInstaller `
  -PgBinariesPath <path-to-postgresql-16-binaries> `
  -IsccPath <path-to-ISCC.exe>
```

Notes that have cost time before:

- **Signing must happen before checksums are computed.** The script already orders it that way —
  do not reorder, and do not sign an artifact after the fact, or `SHA256SUMS.txt` describes bytes
  nobody will download.
- **Inno Setup installs per-user**, so `ISCC.exe` is usually under `%LOCALAPPDATA%`, not
  `C:\Program Files`. `Resolve-IsccPath.ps1` finds it; pass `-IsccPath` only if it cannot.
- **The bundled PostgreSQL must be major version 16.** A 17.x payload produces an installer that
  fails against every existing NodePilot database. The build asserts this.
- `-SkipNpmCi` reuses the existing `node_modules`. Fine for a rebuild, wrong for a release —
  a release should install from the lockfile.

## 5. Check the output before uploading

The build writes these, and **all of them belong in the release**:

| Artifact | Why it must be there |
|---|---|
| `NodePilot-<version>.zip` | the server payload |
| `NodePilot-<version>.zip.manifest.json` + `.p7s` | detached signed manifest |
| `NodePilot-Deploy-Scripts-<version>.zip` | the install scripts, **separately** — a user must be able to get the verifying script without first extracting the archive they have not verified yet |
| `NodePilot-Server-Setup-<version>.exe` | GUI installer |
| `NodePilot-Desktop-Setup-<version>.exe` | desktop installer |
| `NodePilot-Switcher-<version>-win-x64.zip` | the switcher as a standalone, self-contained executable plus its configuration template — the server artifact carries the same bytes under `tools\switcher`, this copy is for a machine that has no NodePilot installation to take it from, which is also why the template travels with it |
| `nodepilot-release-signing.cer` | the publisher certificate the deployment guide tells people to compare against |
| `NodePilot-<version>.SHA256SUMS.txt` | covers **every** file above, the certificate included |

Then verify as a stranger would, from the output folder:

```powershell
Get-FileHash .\NodePilot-<version>.zip -Algorithm SHA256      # must match SHA256SUMS
$sig = Get-AuthenticodeSignature .\NodePilot-Server-Setup-<version>.exe
$sig.SignerCertificate.Subject      # CN=NodePilot Release Signing
$sig.SignerCertificate.Thumbprint   # must equal the shipped .cer's thumbprint
(Get-PfxCertificate .\nodepilot-release-signing.cer).Thumbprint    # goes into the release notes
```

`Status` is **`UnknownError`, and that is the pass condition**, not a failure — the release
certificate is self-signed and its root is in nobody's trust store, so `Get-AuthenticodeSignature`
cannot build a chain. Every published release reports the same; check one if it looks wrong. What
carries the meaning is the pair above: the signer's subject and a thumbprint equal to the
certificate shipped alongside. Waiting for `Valid` means waiting for a public CA.

## 6. Tag and publish

```powershell
git tag -a v1.2.11 -m "NodePilot 1.2.11"
git push origin v1.2.11
gh release create v1.2.11 --title "NodePilot 1.2.11" --notes-file <notes.md> <artifact paths...>
```

The release notes must contain:

- what changed, grouped the way `CHANGELOG.md` groups it;
- the **certificate thumbprint** in full, in text. Until NodePilot is signed by a public CA, this
  is the only out-of-band anchor a downloader has — the checksum file proves the download is
  intact, the thumbprint is what proves who built it;
- a note that Windows SmartScreen will warn on first run, and why (self-signed publisher, no
  reputation yet). The deployment guide covers this; the release notes should not let it be a
  surprise.

## 7. After publishing

- Download the artifacts **from the release page** and re-run the checks in step 5 against those
  copies. A file that was never uploaded, or was uploaded truncated, looks fine locally.
- The docs site redeploys itself on push to `main` (`.github/workflows/docs-pages.yml`); it does
  not need anything here. Its **second** copy does ride along in the artifacts, though: after
  installing, check `GET /docs` answers 301 to `/docs/` and that `/docs/` renders the site
  without signing in. A missing bundle fails the install, but a broken one does not.
- Bump `Directory.Build.props` and the three `package.json` files to the next patch version so
  `main` is never sitting on a version that is already published.
