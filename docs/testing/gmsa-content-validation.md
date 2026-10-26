# Validation of the gMSA articles — 2026-10-25

## Scope

Validated `gmsa-scheduled-task` and `gmsa-troubleshooting`, each in German and English. The articles are drafts. This record distinguishes executed examples from diagnostic guidance supported by Microsoft documentation.

## Lab

- Domain-joined Windows Server 2025 Standard Evaluation; Windows PowerShell 5.1.26100.1591.
- Execution policy RemoteSigned; no bypass switch.
- Dedicated temporary gMSA, local script/report directory, SMB test share and denied subdirectory.
- Scheduled principal: gMSA with Password logon type, no password parameter, RunLevel Limited, no administrator membership.
- No interactive user session on the execution host during the task tests.
- Existing KDS configuration was read, not replaced. No managed-password or KDS key material was collected.

## Executed cases

| Case | Account test | Observed result |
| --- | --- | --- |
| Execution computer not authorized to retrieve the managed password | False | Install-ADServiceAccount failed with Access Denied. |
| Dedicated account changed to authorize the execution computer directly | True | Install-ADServiceAccount succeeded. |
| Task writes to the authorized SMB test destination | True | Exit 0, new JSON written and read back; owner and logged identity were the gMSA. |
| Task writes to the separate NTFS-restricted destination | True | Exit 1, local JSON records access denied. |

The host authorization test changed only the newly created account. It used a direct computer principal rather than a group, so it does not reproduce stale group membership or replication delays.

After drafting, the actual fenced PowerShell code was extracted from the articles. All **26 blocks** parsed with the Windows PowerShell 5.1 parser, and corresponding DE/EN blocks were identical. Parsing is syntax validation, not a claim that every diagnostic or cleanup command was run verbatim.

The final probe, task registration and completion-wait code were executed with only example account/path values replaced by lab values. Successful run `b965e730-b8bf-400b-8bce-2d1fbd485f1e` returned 0; denied run `803b937b-19ad-496e-9cde-0c22825c7e1b` returned 1 with a local error report. Both recorded the expected gMSA and executable. The AD object query and KDS metadata enumeration were also exercised.

The KDS example deliberately enumerates the collection returned by Get-KdsRootKey before selecting metadata. A direct pipeline selection had produced empty fields in this lab; the final article code returned CreationTime and EffectiveTime correctly.

Not tested: PowerShell 7, older Windows releases, multiple domain controllers, replication delay, group-token refresh, every effective domain policy combination, or all diagnostics against deliberately damaged infrastructure. The articles state their tested environment and do not claim these cases were reproduced.

## Website checks

- `npx vitest run src/site/blog-build.test.mjs src/site/prerender.test.ts src/site/seo.test.ts`: 34 tests passed across three files.
- `npx tsc -b`: passed.
- `npm run lint:ci`: passed.
- `NP_BLOG_PREVIEW=1` site build: passed.
- Existing Playwright SEO suite, selected prerendering and article-layout cases: four passed. These cover DE/EN pages without JavaScript and article layout at desktop/mobile widths.
- Catalog checks cover title/description constraints, production draft exclusion and preview behavior. Catalog contains 19 articles: eight published, eleven drafts.

Existing Vite warnings about future native config resolution remain; they did not fail the build.

The already-live PowerShell 5.1/7 publication state was imported from the local `publish-powershell-runtime` worktree because the common Git base did not yet include it. Those source changes are baseline synchronization, not a new article created by this task.

## Cleanup

Removed the two dedicated test tasks, uninstalled and deleted the temporary gMSA, removed the dedicated SMB share, and removed the local/remote test directories after validating their absolute paths. Removed only the test account's batch-logon SID if present, preserving existing entries. Verified the account, share and directories were absent.

The execution VM, initially off, was shut down to restore that state. Existing running lab VMs and the NodePilot service were left in their original state. Local raw evidence remains in the ignored `.tmp/seo-2026-10-25/lab.json` and `article-validation.json`; no credentials are included in this document.
