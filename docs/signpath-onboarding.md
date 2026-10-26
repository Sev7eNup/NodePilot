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

The first request completed successfully in SignPath on 26 October 2026:
`a4361446-00ae-4e7c-a212-7faf6a2ecb86`. Its source is commit `119e6f6`, built by
[GitHub run 40314013517, attempt 2](https://github.com/Sev7eNup/NodePilot/actions/runs/40314013517/attempts/2).
The subsequent Windows verification step stalled; this is not yet a successful
end-to-end workflow result. The workflow correction uses the machine certificate
store and adds progress messages so a stall can be located precisely.

After the run succeeds, inspect the request's GitHub origin in SignPath. Send the
Foundation the workflow/run and signing-request links for its review. Repository
publication and the support reply require the maintainer's approval.

The full release pipeline still needs an integration for the server and desktop
installers, scripts, and detached CMS manifests. Preserve this dependency order:
sign executable/script payloads, hash the resulting payloads, create and sign the
manifests, package the installers, sign the installers, then generate final release
checksums. Confirm that detached CMS signing is available under the OSS subscription
before replacing `ArtifactSecurity.ps1`'s local certificate signing. Do not weaken
the installer's signature or pinned-publisher checks.

The existing release acceptance gates in [RELEASING.md](../RELEASING.md) still apply.
The local release process has not yet been migrated by this onboarding workflow.

References: [GitHub integration](https://docs.signpath.io/trusted-build-systems/github),
[artifact configuration](https://docs.signpath.io/artifact-configuration/).
