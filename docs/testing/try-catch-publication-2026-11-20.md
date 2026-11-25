# PowerShell try/catch publication: 2026-11-20

Published at the user's request on November 20, 2026 (Europe/Berlin), replacing
the planned November 15 date with the actual publication date.

- German: <https://www.nodepilot.run/blog/powershell-try-catch/>
- English: <https://www.nodepilot.run/en/blog/powershell-try-catch/>
- Eleven published articles and ten drafts; try/catch is featured.
- Execution Policy and AI workflows remain drafts, planned for November 22 and 29.

The prepared article includes four PowerShell examples. Prose received two small
style adjustments, one in each language. The scheduled-task guide now links to
try/catch in both languages and carries `modifiedAt: 2026-11-20`. Existing article
publication dates and the removal of the gMSA introduction remain intact.

## Validation

- Four code blocks match between DE and EN and parse under Windows PowerShell
  5.1.26100.10068. Eight checks passed: syntax/language parity, Continue, Stop with
  finally, valid JSON, invalid JSON, blocked input, missing input, and native exit 7.
- PowerShell semantics were checked against the Microsoft references linked in
  the article. NodePilot's optional `successExitCodes` behavior was checked against
  `PowerShellActivitySupport.ApplyExitCodeSemantics`.
- Both language style checks report clean typography and no AI-slop markers.
  The English style profile is derived from the German profile, not independently
  calibrated. The prose contains about 740 German and 760 English words.
- All 227 documentation/website tests passed in 22 files. Production build passed
  with `NP_BLOG_PREVIEW=0` and `NP_SITE_ORIGIN=https://www.nodepilot.run`.
- Local and live browser checks passed at 1440 and 390 pixels in both languages:
  direct article access, four code blocks, one H1, canonical and alternate links,
  publication date, no `noindex`, no page overflow, eleven index entries, featured
  position, client navigation, and the inbound scheduled-task link.
- All ten previously published articles remain in both indexes. The gMSA
  diagnosis still has seven code blocks. All six internal article/documentation
  targets linked from the new article returned HTTP 200.

## Deployment and retained evidence

Before editing, all 42 files from the November 8 publication matched the live
site and November 13 recovery manifest. The candidate was built from that source.
Every overwritten file matched the saved baseline immediately before publication
and was backed up. No documentation, demo, or media files were included.

The scoped publisher transferred 44 files: 41 HTML pages, one shared JavaScript
bundle, `sitemap.xml`, and `llms.txt`. Three were new and 41 replaced existing files.
All 54 training videos were already on the server and none were uploaded.
After deployment, all 44 remote SHA-256 hashes matched the verified build.

Working source: `E:/NodePilot-worktrees/publish-gmsa-diagnosis-2026-11-08`, branch
`docs/publish-try-catch-2026-11-20`. Publication changes are also synchronized to
the primary checkout without altering its other work. They remain uncommitted
and unpushed.

Durable local backup: `E:/NodePilot/out/website-publications/2026-11-20/`, including
the verified publication, previous live files, source patch, manifest, validation
and deployment logs. Task scripts and browser screenshots remain in the worktree
under `out/try-catch-publication/`.
