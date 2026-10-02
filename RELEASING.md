# Releasing NodePilot

The release build is manual and local on purpose. Signing requires the code-signing certificate, and a signing key on a hosted runner would undermine the very thing the signature is meant to prove. This checklist replaces the automation.

---

## 1. Pick the version

`v<major>.<minor>.<patch>`. Tags carry the `v`, release titles do not (`NodePilot 1.2.10`).

The version lives in **four** files, which must agree:

| File | Field |
|---|---|
| `Directory.Build.props` | `<Version>`, the source of truth for the backend |
| `src/nodepilot-ui/package.json` | `version` |
| `src/nodepilot-desktop/package.json` | `version`, which becomes the .exe's file properties |
| `src/nodepilot-docs-ui/package.json` | `version` |

After the bump, two tests prove that the files agree with each other as well as with the CLI:

```powershell
dotnet test tests/NodePilot.Api.Tests --filter "FullyQualifiedName~PackageVersionParity"
dotnet test tests/NodePilot.Cli.Tests --filter "FullyQualifiedName~CliVersion"
```

## 2. Make sure the tree is releasable

```powershell
dotnet test                                        # full backend suite
cd src\nodepilot-ui;      npm run lint:ci; npm run test:run; npm run test:e2e
cd src\nodepilot-docs-ui; npm run lint:ci; npm run test:run; npm run build
cd src\nodepilot-desktop; npm run test:run
```

A release cut is one of the few occasions where the **full** suite is the right call. CI on `main` has to be green as well, since the local nightly job runs against its own checkout and is not a status check on `origin/main`.

## 3. Update the changelog

The new version is added to `CHANGELOG.md` before building, so that the tag and the changelog cannot drift apart.

## 4. Build the artifacts

One command builds everything, signs it and writes the checksum file:

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

- **Signing happens before the checksums are computed.** Nothing is signed after the fact, because `SHA256SUMS.txt` would then describe bytes nobody downloads.
- **Inno Setup installs per user**, so `ISCC.exe` usually sits under `%LOCALAPPDATA%`. `Resolve-IsccPath.ps1` finds it, and `-IsccPath` is only needed when it does not.
- **The bundled PostgreSQL must be major version 16.** A 17.x payload fails against every existing NodePilot database, which the build asserts.
- `-SkipNpmCi` is not used for a release, since a release installs from the lockfile.

## 5. Check the output before uploading

**Everything the build writes belongs in the release:**

| Artifact | Purpose |
|---|---|
| `NodePilot-<version>.zip` | server payload |
| `NodePilot-<version>.zip.manifest.json` + `.p7s` | detached signed manifest |
| `NodePilot-Deploy-Scripts-<version>.zip` | install scripts, shipped separately so that the verifying script is available before the unverified archive is extracted |
| `NodePilot-Server-Setup-<version>.exe` | GUI installer |
| `NodePilot-Desktop-Setup-<version>.exe` | desktop installer |
| `NodePilot-Switcher-<version>-win-x64.zip` | standalone switcher with its configuration template, for machines without a NodePilot installation |
| `nodepilot-release-signing.cer` | publisher certificate, the comparison anchor named in the deployment guide |
| `NodePilot-<version>.SHA256SUMS.txt` | checksums of **every** file above, the certificate included |

The verification is then carried out from the output folder, the same way a stranger would do it:

```powershell
Get-FileHash .\NodePilot-<version>.zip -Algorithm SHA256      # must match SHA256SUMS
$sig = Get-AuthenticodeSignature .\NodePilot-Server-Setup-<version>.exe
$sig.SignerCertificate.Subject      # CN=NodePilot Release Signing
$sig.SignerCertificate.Thumbprint   # must equal the shipped .cer's thumbprint
(Get-PfxCertificate .\nodepilot-release-signing.cer).Thumbprint    # goes into the release notes
```

`Status` reports **`UnknownError`, which is the pass condition.** The certificate is self-signed, so no chain can be built. What counts are the signer's subject and a thumbprint that equals the shipped certificate.

## 6. Run the release lab

Before anything is tagged, the signed setups from `out\` are installed on the Hyper-V lab. The server setup runs in every identity/database combination, fresh and as an update from the previous release, each followed by an uninstall. The desktop setup goes through install, over-install, uninstall and reinstall. Checks, lab prerequisites and pass criteria are described in [`scripts/release-lab/README.md`](scripts/release-lab/README.md).

```powershell
.\scripts\release-lab\server\Invoke-ServerMatrix.ps1  -ConfigPath <lab config> -ArtifactDir .\out -Version 1.2.11
.\scripts\release-lab\desktop\Invoke-DesktopMatrix.ps1 -ConfigPath <lab config> -ArtifactDir .\out -Version 1.2.11
```

Afterwards the same server setup is installed as an update on the lab's long-running instance, against which the workflow test suite (`scripts/test-suite/`, see [`docs/workflow-tests.md`](docs/workflow-tests.md)) runs. Because that instance runs with production hardening, it needs the following once:

- `Trigger:Database:Connections:np-testsuite-sentinel` = `Data Source=C:\Temp\NP-TestSuite\runtime\db\sentinel.sqlite` in `appsettings.Production.json`, which survives updates
- its own host name in `RestApi:AllowedHosts` and `WaitForCondition:AllowedHosts`, plus the probe host in `WaitForCondition:AllowedHosts`
- a registered machine with a default credential for the remote workflows, named in the global `NP_TESTSUITE_REMOTE_MACHINE`

```powershell
.\scripts\test-suite\Install-TestSuite.ps1 -BaseUrl https://<lab instance>:8443 -Password <admin> `
  -Profiles continuous, integration -ProbeUrl http://<another lab host>/
.\scripts\test-suite\Verify-TestSuite.ps1 -BaseUrl https://<lab instance>:8443 -Password <admin> -Once
```

The probe URL points at another host, since an instance cannot probe itself by its own name. **The suite has to finish with `fail=0`.** Right after the update the database trigger skips its first round by design. Should the trigger driver fail on that alone, the verifier is run once more.

**No release is tagged without a green run of both matrices and the test suite.** A fix found here means a new build and a new run, not a patched artifact, because the setups are signed and listed in `SHA256SUMS.txt` and the tag has to point at the commit they were built from. The `summary.md` of both lab runs and the verifier's result belong in the test section of the release notes.

### Experimental: Computer-Use UI acceptance

`scripts/release-lab/computer-use/` contains an experimental run that operates the installed desktop and server releases through their UI. It is not a release gate. Usage is described in [`scripts/release-lab/computer-use/README.md`](scripts/release-lab/computer-use/README.md).

## 7. Tag and publish

```powershell
git tag -a v1.2.11 -m "NodePilot 1.2.11"
git push origin v1.2.11
gh release create v1.2.11 --title "NodePilot 1.2.11" --notes-file <notes.md> <artifact paths...>
```

The release notes contain:

- the changes, grouped the same way as in `CHANGELOG.md`
- the full **certificate thumbprint** as text, because until NodePilot is signed by a public CA it is the only out-of-band anchor a downloader has
- a note that Windows SmartScreen warns on first run, since the publisher is self-signed and has no reputation

## 8. After publishing

- The artifacts are downloaded **from the release page**, and the checks from step 5 are repeated against those copies. A file that was never uploaded, or uploaded truncated, looks fine locally.
- Website, documentation and demo are published to the webspace with `deploy/Publish-Site.ps1`. The docs copy inside the artifacts is checked after installing: `GET /docs` answers 301 to `/docs/`, and `/docs/` renders the docs without signing in.
- `Directory.Build.props` and the three `package.json` files are bumped to the next patch version, so that `main` never sits on an already published version.
