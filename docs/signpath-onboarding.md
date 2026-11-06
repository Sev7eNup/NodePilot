# SignPath Foundation onboarding

The Foundation accepted NodePilot on 26 October 2026. The OSS organization is
`NodePilot.run [OSS]` (`e64c0f9b-346e-4e60-bce6-8791970f5567`), not the separately
created `NodePilot.run` organization. Membership and the `CI builds` notification
address are confirmed. The SignPath GitHub App is installed with access restricted
to `Sev7eNup/NodePilot`, and the CI token is stored in GitHub Actions secrets.

## First signing run

The existing project slug is `NodePilot`. The Foundation supplied `test-signing`,
the self-signed **Test certificate 2026**, and the linked `GitHub.com` trusted build
system. The `release-signing` policy is currently invalid because **Release
certificate 2026** is still **CSR PENDING**. The Foundation orders and imports that
certificate after reviewing the technical setup; creating another CSR is unnecessary.

The `switcher-zip` artifact configuration is installed and valid. Its XML is kept
in [deploy/signpath/switcher-zip.xml](../deploy/signpath/switcher-zip.xml). It signs
only `NodePilot.Switcher.exe` in the root of a ZIP.

[SignPath test signing](../.github/workflows/signpath-test.yml) builds and tests the
actual Switcher on a GitHub-hosted Windows runner, uploads the unsigned executable
as a GitHub artifact, and submits that artifact through the official connector.
It uses the test policy only. It runs on a push of the onboarding workflow/config
to `build/signpath-onboarding`, or manually on that branch or `main` once the
workflow is available on the default branch. Pull requests do not trigger signing.

The CI user's token belongs in the repository Actions secret `SIGNPATH_API_TOKEN`.
Never put it in source, a report, a screenshot, or a chat message. Regenerating it
invalidates the previous token. The CI user already has submitter access to the
test and release policies; the latter remains subject to SignPath's release controls.

The workflow pins the test certificate thumbprint
`1E3876C87EF8D2B73F940F80674D5B81C2AA3649`. It temporarily trusts that exact public
certificate in the disposable runner's local-machine store, verifies Authenticode,
then removes it in `finally`. It never installs a certificate on an end user's
machine. The machine store avoids the interactive confirmation required by the
current-user root store. The verification step has a five-minute timeout.
Checksums are calculated after signing. The result includes the signing
request URL, source commit, certificate thumbprint, and executable checksum.
Neither the workflow nor its test artifact is a production release.

## Review and production integration

The Switcher integration passed end to end on 26 October 2026, including all 167
Switcher tests, signing, Authenticode verification and evidence upload:

- [Successful GitHub run 40316031047](https://github.com/Sev7eNup/NodePilot/actions/runs/40316031047)
- [Tested workflow at commit 495a4657e33adb928aefb6298253f0e658a146c3](https://github.com/Sev7eNup/NodePilot/blob/495a4657e33adb928aefb6298253f0e658a146c3/.github/workflows/signpath-test.yml)
- [Completed SignPath request 73903cab-95aa-4d7a-a13a-6ac11b98ae62](https://app.signpath.io/Web/e64c0f9b-346e-4e60-bce6-8791970f5567/SigningRequests/73903cab-95aa-4d7a-a13a-6ac11b98ae62)

The maintainer sent these links to the Foundation for technical review and asked
whether the OSS subscription supports detached CMS/PKCS#7 signatures with DER
`.p7s` output. As of 6 November 2026, the reply is outstanding and the portal still
shows the release certificate as **CSR PENDING**. Further integration uses only
`test-signing` until the production certificate and review are complete.

## Installer artifact configurations

The following configurations were added in the OSS portal on 6 November 2026.
Both have status **VALID**; neither has completed a signing run yet.

| Slug | XML | Signed file at the ZIP root |
| --- | --- | --- |
| `server-setup-zip` | [server-setup-zip.xml](../deploy/signpath/server-setup-zip.xml) | `NodePilot-Server-Setup-*.exe` |
| `desktop-setup-zip` | [desktop-setup-zip.xml](../deploy/signpath/desktop-setup-zip.xml) | `NodePilot-Desktop-Setup-*.exe` |

These configurations apply Authenticode to the outer setup executable only. They
do not sign embedded application binaries, scripts, third-party components or CMS
manifests. The existing default configuration, policies and certificate settings
were not changed.

## Prepared installer test workflow

[SignPath installer test signing](../.github/workflows/signpath-installers-test.yml)
builds both real installers on a GitHub-hosted Windows runner using the existing
`Build-Artifact.ps1` path. It runs when its workflow, helpers or installer XML
configurations are pushed to `build/signpath-onboarding`. Manual dispatch is also
supported on that branch or `main` once the workflow exists on the default branch.
Forks, pull requests and release tags cannot trigger this signing job.

The workflow uses only `test-signing` and the existing `SIGNPATH_API_TOKEN` secret.
It has no release-publishing step or repository write permission. This workflow
is prepared locally; an end-to-end GitHub run has not yet been performed.

1. Check the signing-test contract, artifact integrity and deployment templates.
2. Download pinned PostgreSQL 16.15 binaries and Inno Setup 6.7.4 from their official
   sources, checking SHA-256 before extraction or execution. Inno Setup must also
   have a valid Authenticode signature.
3. Create a non-exportable, seven-day code-signing certificate on the disposable
   runner. Use it for the existing CMS manifest path, build both installers, then
   verify the server ZIP through `Assert-NodePilotSignedArtifact`. Remove the
   private certificate in `finally`. No production private key is uploaded.
4. Submit each outer installer separately using `server-setup-zip` or
   `desktop-setup-zip`. The inputs come directly from this GitHub build.
5. Verify the returned installer against the pinned SignPath test certificate,
   calculate its final SHA-256, and upload the result plus signing evidence as
   `nodepilot-server-setup-signed-test` and `nodepilot-desktop-setup-signed-test`.
   Test artifacts expire after seven days.

The runner-local CMS signer is deliberately distinct from the SignPath
Authenticode signer, and both thumbprints are recorded in the evidence. This is
an installer integration test, not a replacement for the production CMS signer
or the release-lab acceptance matrix. Embedded executables and scripts are not
re-signed by this workflow. Never distribute these test builds as releases.

The targeted contract check is runnable locally without trusting a test root:

```powershell
powershell.exe -NoProfile -File deploy/signpath/Test-InstallerTest.ps1
```

## Confirmed CMS subscription limitation

On 6 November 2026, the portal rejected a ZIP configuration containing
`create-cms-signature` with `output-encoding="der"`, SHA-256 and PKCS#1 padding:

> The element 'create-cms-signature' cannot be used because the corresponding feature is not available with your current subscription type.

No CMS configuration was created. The full migration cannot use SignPath for the
existing detached `.p7s` manifests under this subscription. Ask the Foundation
whether it can enable this feature or provide a supported equivalent. Do not
replace the manifest signature with an unsigned checksum, remove publisher
pinning or assume that signing the outer setup signs its embedded payloads.

## Remaining production integration

The full release pipeline still needs an integration for the server and desktop
installers, scripts, and detached CMS manifests. Preserve this dependency order:
sign executable/script payloads, hash the resulting payloads, create and sign the
manifests, package the installers, sign the installers, then generate final release
checksums. Resolve the CMS subscription limitation before replacing
`ArtifactSecurity.ps1`'s local certificate signing. Do not weaken
the installer's signature or pinned-publisher checks.

The existing release acceptance gates in [RELEASING.md](../RELEASING.md) still apply.
The local release process has not yet been migrated by this onboarding workflow.

References: [GitHub integration](https://docs.signpath.io/trusted-build-systems/github),
[artifact configuration](https://docs.signpath.io/artifact-configuration/).
