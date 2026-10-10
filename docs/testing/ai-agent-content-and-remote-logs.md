# Combined content diagnosis and remote log collection

Date: 2026-09-20. Branch: `feature/ai-agent-activities`.
Continues [Unknown-compliance follow-up](ai-agent-unknown-followup.md).

Current result: the 500-tool team ceiling is active; combined fault/current-health
content diagnosis passed. Corrected remote collection passed the full-volume
single-agent run and rotation/truncation/append checks. The three original cancelled
reviewer requests remain part of the evidence. A follow-up adds per-model bounds and
verifies complete team execution; see [team model-response stalls](ai-agent-team-model-timeouts.md)
for the correction, successful repeat and remaining limit on historical root-cause attribution.

## Budget change

The team tool-call default and the development administrator ceiling are now 500,
up from 80. Core options, settings DTO, frontend fallback and the configuration
reference/documentation agree. New test workflows explicitly use 500. Model calls,
delegations, timeouts and the 250000-character context limit remain separate bounds.
Historical workflow definitions retain their explicit lower limits; changing an
administrator ceiling does not overwrite a published workflow's configuration.

## Combined CLIENT1/CM1 content test

The independent controller used the existing lab-only package CHQ00009,
`NP-SccmFive-Content`, whose verified program is `cmd.exe /d /c type payload.txt`.
The target collection CHQ00014 was verified to contain only CLIENT1. A temporary
deployment CHQ20007 initiated a real client download. The controller removed this
package's distribution from CM1 and cleared only this package's cached content.
Other packages, boundaries and distribution points were not changed.

Before the agent ran, real client evidence showed the request waiting for content,
an empty location response and a suspended CTM job. The SMS Provider independently
showed the package/version present but no package-DP assignment or distribution
status. No source logs or status rows were fabricated. The model received the
reported download symptom and package/deployment identities, not the injected cause.

The five members were a supervisor, client/server specialists and independently
reading client/server reviewers. Targets and credentials were host-bound. Tools
were read-checked PowerShell and bounded client log reads. General diagnostic
resources were supplied as instructions; this is not a remote skill-runner test.

| Run | Execution | Result | Seconds | Model / tools / delegations |
|---|---|---|---:|---|
| Blind fault | `6d624ff5-403e-4113-b0e9-eb66ee70fe22` | Cause and focused remedy established | 144.4 | 22 / 61 / 7 |
| First healthy control | `59fe804f-c783-4b37-9b67-089307d5d85c` | Current health correct; historical explanation imprecise | 103.4 | 16 / 39 / 4 |
| Reviewed healthy control | `f07e17da-4299-4783-9793-a5faeb9dda87` | Current success and no repair recognized | 86.9 | 15 / 34 / 4 |

The fault team linked the missing distribution to the empty client location response
and WaitingContent. It proposed distributing the specific package/version to its
eligible DP, waiting for matching successful status and verifying the client download.
It distinguished a successful MP manifest request and older HTTP errors from content
delivery. Review follow-ups obtained the missing CTM-stage evidence before completion.

The controller restored the distribution, waited for State=0/SourceVersion=1, and
requested the documented [location refresh](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/clients/client-classes/triggerschedule-method-in-class-sms_client).
The same CTM job then completed the download, hash verification and harmless program
execution before the healthy agent started. No installation was needed.

Both controls recognized current success and recommended no redistribution or repair.
The first report loosely called a later HTTP failure during successful HTTPS fallback
the cause of the earlier problem. Skill resource `server-content.md` now explicitly
separates location blocking, transport, fallback, hash verification and execution;
later recovery warnings do not establish an earlier blocker's cause. Version 1.0.15
was imported as a new immutable package; existing selections were not overwritten.
The repeated control described the successful fallback sequence and left its HTTP
root cause unproven. It does not independently reconstruct the prior missing-DP state
from current healthy provider data. Acceptance is of fault diagnosis and current
healthy recognition, not a complete historical attribution audit.

Both targets had zero matching MsiInstaller and WindowsUpdateClient installation
events (19/20/21) during each content-agent execution window. These are scoped audits,
not a claim that the test controller or the operating system performed zero writes.

## Remote 250 MB and mutable logs

The controller prepared exactly 250000000 bytes on CLIENT1: a real CAS.log copy,
UTF-8 with BOM, UTF-16 LE/BE with BOM and a large synthetic log. A two-million-character
line and markers spanning search-fragment boundaries exercise bounded searching.
Source hashes and random marker values were retained outside model input.

A three-member team collected the five files over real WinRM and independently
searched the shared artifacts. A host observer hashed completed local raw copies
before deletion and compared them with the independently measured source hashes.
The first full run (`887d3d7c-9993-48fa-bf68-6fbd8d06a17d`) passed in 94.8 seconds:
all five SHA-256 values matched, all six markers and Unicode text were present, the
additional collection was rejected by the shared quota, and the raw run directory
was deleted. Largest recorded native tool result was 14125 characters.

The mutable-source test then exposed a real defect. Atomic replacement during the
32 MB transfer (`1566968f-7c56-45ca-b3d7-42fb98e37422`) produced mixed original and
replacement bytes but returned a successful artifact. A separate rename-gap test
correctly failed on a missing file; that did not cover atomic replacement.

The fix hashes transferred blocks and verifies the same-length source prefix with a
bounded target-side streaming reread before registering the artifact or consuming quota.
Mismatch discards the partial copy. The real script execution regression failed before
the fix and passes afterward. Append-only growth keeps the originally observed prefix;
this is an integrity check, not an atomic snapshot of an arbitrary changing filesystem.

| Corrected real WinRM case | Execution | Observed result |
|---|---|---|
| Atomic replacement | `c7aa1e54-8b1c-407a-b19d-7efcdcb65744` | Hash mismatch; no artifact published; partial copy removed |
| Truncation | `f0880cca-d5af-4c72-9cf8-1a384fcfe302` | Invalid/empty transfer block; no artifact; partial copy removed |
| Append | `251f71bf-cdff-4674-9d58-e0f16eaf7f92` | Exact original 32000000-byte prefix preserved and hash-matched |

The truncation workflow itself ended Failed because its member retained a failed status;
the expected file-integrity rejection passed. Test acceptance does not equate all engine
statuses with success. Each mutation was performed by the controller after observing
at least 1 MB transferred; the agents had only the collection tool and did not mutate files.

The first repeat on the corrected build (`0fdb7cdc-85bd-462c-9a66-0883ce9463c9`)
transferred all 250000000 bytes with five matching hashes, enforced the quota and
completed its source searches. It then remained in a reviewer model request for over
five minutes. The controller cancelled the run after 369.5 seconds; cancellation
removed the raw workspace. The active provider's existing HTTP timeout is 3600 seconds
and was not changed. This is a retained cancelled attempt, not a passed full workflow.

A fresh unchanged repeat (`f9a1f8e0-f93a-4a8e-b0f5-bfa3d577a164`) also remained in
the reviewer request after completing the transfer and searches. It was cancelled
after 223.9 seconds. Both retained proofs show matching hashes for all five files,
quota rejection and workspace cleanup. Neither cancelled attempt is counted as a
passed end-to-end workflow. A subsequent controlled probe changes only the review
instruction to request one CAS citation instead of returning up to 20 matches; the
source files, total bytes, encoding/long-line markers and hash assertions are unchanged.

The bounded-citation team probe (`8ac85e17-f621-4a1e-88d2-d4a299b9bf25`) likewise
remained in the reviewer model request and was cancelled after 225.6 seconds. Reducing
the CAS excerpt did not establish excerpt volume as the sole cause. Its retained
proof again confirms all bytes/hashes, quota rejection and cancellation cleanup.
The cause of these long reviewer requests is unresolved; tool/model count exhaustion
was not the trigger. These attempts prevent a claim of consistently completing this
particular full-volume reviewer workflow on the final build.

A separate single-agent probe isolates the changed native file path from team review.
Execution `465ce938-b788-4ba5-950e-a41ea978416d` completed successfully in **41.9 seconds**,
four model calls and 12 tools. It used the same five remote sources and assertions,
with one requested CAS citation. All five SHA-256 values matched, the exact total was
250000000 bytes, all six random markers and `Grüße aus Köln äöü` appeared in the final
answer, the extra collection was rejected, and the run directory was removed. Largest
native tool result was 567 characters. This is successful end-to-end single-agent
acceptance of collection/search/integrity/quota; it does not erase the team timeouts.

## Cleanup and final state

At 15:36:10 UTC the controller verified no remaining generated source fixture directories,
test firewall rule, temporary CLIENT1 adapter or test client policy. Temporary deployments
were removed; the pre-existing package's original distribution is restored with State=0
and SourceVersion=1. Its harmless cache payload was downloaded again during recovery.
TrustedHosts is restored to `localhost`; CurrentUser execution policy remains Undefined
and Defender platform remains 4.18.26080.4. CLIENT1, CM1 and GW1 remain running.

All audited content, volume, mutation and cancelled-volume execution windows contain zero
matching MSI/WindowsUpdate installation events on both targets. The read-only agents
did not perform the fixture mutations, content-distribution changes or location refresh.
Those were explicitly separate controller setup/restoration actions.

Remaining acceptance outside these scoped results: remote CMD/Git Bash and AllSigned,
restricted-user authorization/redaction and external MCP/disconnect/portability checks,
full CI/designer E2E and consolidation. Long reviewer model-request completion now has
its own reproducible open observation above. The earlier imprecise alternative firewall
remedy and broad combined Unknown-recovery diagnosis are not claimed resolved by this
content/log test; the requested larger tool budget is applied without changing their
historical results.

## Automated verification

263 Engine/agent/catalog/configuration tests and three settings/frontend-documentation
checks pass. The API builds with zero errors and 54 existing warnings outside these
changes. This is not the full repository CI or designer E2E acceptance. The encoded-log
checks cover UTF-8 and BOM-tagged UTF-16, not arbitrary legacy encodings.

Complete run definitions, journals, hash comparisons and controller audits are retained
locally under `.runlogs/agent-update-validation/`. Test workflows remain disabled after
execution. The installed Windows service is untouched.
