# gMSA diagnosis publication: 2026-11-08

Published on the user's explicit request with the actual date in Europe/Berlin:

- German: https://www.nodepilot.run/blog/gmsa-troubleshooting/
- English: https://www.nodepilot.run/en/blog/gmsa-troubleshooting/

## Source and scope

Source is in `E:/NodePilot-worktrees/publish-gmsa-diagnosis-2026-11-08`, branch `docs/publish-gmsa-diagnosis-2026-11-08`, based on `b56d666`. No commit or push was requested or performed. The primary worktree remains unchanged.

The primary checkout still contained the pre-2-November publication state, and the earlier publication worktree was absent. Reconstructed the previously authorized scheduled-task publication, original publication date, foundation links and the user's paragraph removal. A fresh build of that restored baseline matched the live site before any upload, including the previously published article bodies and catalog asset.

Only the diagnosis is newly published. It is now featured, and the foundation and scheduled-task articles link to it with `modifiedAt: 2026-11-08`. The scheduled-task publication date remains 2026-11-02. The catalog now has ten published articles and eleven drafts. The other three November-package articles remain drafts.

Kept the diagnosis introduction concise by retaining its setup-guide link and example-name note while removing the detailed lab-date/environment sentences, consistent with the user's earlier editorial correction. Existing diagnostic examples and their specific lab results remain intact; the internal laboratory record is preserved.

## Verification

- All 227 docs-ui tests in 22 files passed, including publication boundaries and article links. TypeScript and the production site build passed with `NP_BLOG_PREVIEW=0` and canonical origin `https://www.nodepilot.run`.
- All seven PowerShell examples match across languages and parse with Windows PowerShell 5.1.26100.9818. The earlier [lab evidence](gmsa-content-validation.md) was reviewed; domain mutations and SMB tests were not repeated for this release.
- Microsoft references for `Test-ADServiceAccount`, KDS readiness and `Test-ComputerSecureChannel` were reviewed. The article retains the distinction between local account readiness and application/resource access.
- Local and live browser checks passed for both languages at 1440px and 390px: seven code blocks, one heading, complete static content, publication date, self canonical, reciprocal language links, no noindex directive, no document overflow and no JavaScript page errors. Navigation from the blog list and links from both preceding gMSA articles work.
- The listing contains ten articles with the diagnosis first. The production output and sitemap exclude all eleven drafts. The new article appears in the sitemap and `llms.txt`. The removed paragraph in the scheduled-task guide remains absent.
- Screenshots were inspected. German and English style checks found no forbidden typography or AI-slop markers; the English profile remains derived from the German profile.
- Every replaced remote file matched the restored baseline before upload and was backed up. The scoped publisher successfully transferred 42 files: 39 HTML pages, one content-hashed JavaScript file, `sitemap.xml` and `llms.txt`. Shared catalog/navigation changes account for HTML changes beyond the new article. Documentation, demo and media were not transferred; 54 training videos were already present.
- Both new URLs returned HTTP 200 after publication. All 42 files were fetched from the public origin and their SHA-256 hashes matched the verified build.

## Recovery and evidence

Ignored evidence is under `out/gmsa-diagnosis/`: baseline build, remote backups, upload manifest, logs, local/live screenshots and `verify-publication.mjs`. `out/Publish-GmsaDiagnosis.ps1` is a task-local copy of the existing publisher restricted to that manifest, with path and hash guards; credential handling, host-key pinning, retries and assets-before-HTML ordering are unchanged.

The source recovery patch and this record are also stored under `E:/NodePilot/out/website-publications/2026-11-08/`, outside the temporary worktree. The patch includes the 2 November baseline restoration and 8 November publication. Check it against the target checkout before applying; it is not a deployment artifact. Preserve or integrate the source before any future website deployment to avoid reverting the catalog to eight articles.

No automatic schedule or Search Console indexing request was created.
# Recovery on 2026-11-13

The live blog indexes contained nine posts instead of ten. Of the 42 files in the
verified November 8 publication, three remained correct and 39 had been replaced
on November 12. Of those 39, 37 matched the older November 2 build exactly; the
two first-workflow pages also contained changed UI labels. Cache-bypassed HTTP
requests reproduced the mismatch. The German and English diagnosis article
pages themselves remained available.

At the user's request, all 39 overwritten files were restored from the verified
November 8 build. The previous live bytes and response timestamps were backed up
before upload. The recovery changed no documentation, demo, or training videos.

Verification: all 42 publication hashes match the saved build. Both languages
show ten articles with the diagnosis article first; direct access, client
navigation, seven code blocks, inbound links, publication date, canonical and
alternate links, and desktop/mobile overflow checks passed. The restored local
publication sources passed all seven blog build tests. Future planned articles
remain drafts.

The publication source changes were also applied to the primary checkout on
`fix/restore-website-2026-11-13`. They remain uncommitted. Recovery artifacts are
stored under `out/website-publications/2026-11-13/` in the primary checkout.

