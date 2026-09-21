# Releasing NodePilot

The release build is manual and local on purpose, because signing requires the code-signing certificate, and putting a signing key on a hosted runner buys convenience at the cost of the one thing the signature is supposed to prove. This checklist is what replaces the automation.

It exists because two things went wrong before it did. After 1.2.8 the publisher certificate was no longer attached to the release, because the upload is a manual step and was documented nowhere. That broke the verification step the deployment guide calls "the trust decision". Furthermore the npm manifests sat at 1.2.5 while the product was on 1.2.10, so the installer reported one version and the executable's file properties another. Both are now guarded by tests, and both are steps below.

---

## 1. Pick the version

`v<major>.<minor>.<patch>`. Tags carry the `v`, release titles do not (`NodePilot 1.2.10`).

The version lives in **four** files and they must agree.

| File | Field |
|---|---|
| `Directory.Build.props` | `<Version>`, the source of truth for the whole backend |
| `src/nodepilot-ui/package.json` | `version` |
| `src/nodepilot-desktop/package.json` | `version`, this one becomes the .exe's file properties |
| `src/nodepilot-docs-ui/package.json` | `version` |

All four are bumped, and then proven.

```powershell
dotnet test tests/NodePilot.Api.Tests --filter "FullyQualifiedName~PackageVersionParity"
dotnet test tests/NodePilot.Cli.Tests --filter "FullyQualifiedName~CliVersion"
```

Those two tests exist solely to stop a partial bump from shipping. Should they pass, the four files agree with each other as well as with the CLI.

## 2. Make sure the tree is releasable

```powershell
dotnet test                                        # full backend suite
cd src\nodepilot-ui;      npm run lint:ci; npm run test:run; npm run test:e2e
cd src\nodepilot-docs-ui; npm run lint:ci; npm run test:run; npm run build
cd src\nodepilot-desktop; npm run test:run
```

A release cut is one of the few times the **full** suite is the right call rather than a scoped run. The testing section in `CONTRIBUTING.md` explains the scoping rules. CI on `main` is confirmed as green as well, because the local nightly job runs against its checked-out tree and is not a status check on `origin/main`.

## 3. Update the changelog

The new version is added to `CHANGELOG.md` before building, so that the tag and the changelog cannot drift.

## 4. Build the artifacts

One command produces everything, signs it and writes the checksum file.

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

Four points have cost time before.

- **Signing must happen before checksums are computed.** The script already orders it that way. It is not to be reordered, and an artifact is not signed after the fact, because `SHA256SUMS.txt` would then describe bytes nobody will download.
- **Inno Setup installs per-user**, so `ISCC.exe` is usually under `%LOCALAPPDATA%` rather than `C:\Program Files`. `Resolve-IsccPath.ps1` finds it. `-IsccPath` is passed only if it cannot.
- **The bundled PostgreSQL must be major version 16.** A 17.x payload produces an installer that fails against every existing NodePilot database. The build asserts this.
- `-SkipNpmCi` reuses the existing `node_modules`. That is fine for a rebuild and wrong for a release, since a release should install from the lockfile.

## 5. Check the output before uploading

The build writes the following, and **all of it belongs in the release**.

| Artifact | Why it must be there |
|---|---|
| `NodePilot-<version>.zip` | the server payload |
| `NodePilot-<version>.zip.manifest.json` + `.p7s` | detached signed manifest |
| `NodePilot-Deploy-Scripts-<version>.zip` | the install scripts, shipped **separately**. The verifying script must be obtainable without first extracting the archive that has not been verified yet |
| `NodePilot-Server-Setup-<version>.exe` | GUI installer |
| `NodePilot-Desktop-Setup-<version>.exe` | desktop installer |
| `NodePilot-Switcher-<version>-win-x64.zip` | the switcher as a standalone, self-contained executable plus its configuration template. The server artifact carries the same bytes under `tools\switcher`. This copy is for a machine that has no NodePilot installation to take it from, which is also why the template travels with it |
| `nodepilot-release-signing.cer` | the publisher certificate the deployment guide names as the comparison anchor |
| `NodePilot-<version>.SHA256SUMS.txt` | covers **every** file above, the certificate included |

Verification then follows as a stranger would perform it, from the output folder.

```powershell
Get-FileHash .\NodePilot-<version>.zip -Algorithm SHA256      # must match SHA256SUMS
$sig = Get-AuthenticodeSignature .\NodePilot-Server-Setup-<version>.exe
$sig.SignerCertificate.Subject      # CN=NodePilot Release Signing
$sig.SignerCertificate.Thumbprint   # must equal the shipped .cer's thumbprint
(Get-PfxCertificate .\nodepilot-release-signing.cer).Thumbprint    # goes into the release notes
```

`Status` is **`UnknownError`, and that is the pass condition**, not a failure. The release certificate is self-signed and its root is in nobody's trust store, so `Get-AuthenticodeSignature` cannot build a chain. Every published release reports the same, and one of them can be checked should this look wrong. What carries the meaning is the pair above, namely the signer's subject and a thumbprint equal to the certificate shipped alongside. Waiting for `Valid` means waiting for a public CA.

## 6. Tag and publish

```powershell
git tag -a v1.2.11 -m "NodePilot 1.2.11"
git push origin v1.2.11
gh release create v1.2.11 --title "NodePilot 1.2.11" --notes-file <notes.md> <artifact paths...>
```

The release notes must contain three things.

- What changed, grouped the way `CHANGELOG.md` groups it.
- The **certificate thumbprint** in full, in text. Until NodePilot is signed by a public CA, this is the only out-of-band anchor a downloader has. The checksum file proves the download is intact, the thumbprint proves who built it.
- A note that Windows SmartScreen will warn on first run, as well as why, namely a self-signed publisher without reputation. The deployment guide covers this. The release notes should not let it be a surprise.

## 7. After publishing

- The artifacts are downloaded **from the release page** and the checks in step 5 are re-run against those copies. A file that was never uploaded, or was uploaded truncated, looks fine locally.
- The project website and the docs site redeploy themselves on push to `main` (`.github/workflows/docs-pages.yml`) and need nothing here. The docs' **second** copy does ride along in the artifacts. After installing, `GET /docs` is checked to answer 301 to `/docs/`, and `/docs/` is checked to render the docs without signing in. A missing bundle fails the install, but a broken one does not.
- `Directory.Build.props` and the three `package.json` files are bumped to the next patch version, so that `main` is never sitting on a version that is already published.
