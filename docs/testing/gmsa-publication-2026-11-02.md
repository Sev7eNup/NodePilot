# gMSA scheduled-task article publication: 2026-11-02

Published on the user's explicit request, using the actual publication date in Europe/Berlin rather than backdating to the planned 1 November release.

- German: https://www.nodepilot.run/blog/gmsa-scheduled-task/
- English: https://www.nodepilot.run/en/blog/gmsa-scheduled-task/

## Scope

Only `gmsa-scheduled-task` changed from draft to published and became the featured article. Both prepared article bodies and their examples were preserved. A contextual link was added to each published gMSA foundation article, with `modifiedAt` set to 2026-11-02. The production count is nine; the preview count remains 21. The gMSA diagnosis, try/catch, AI workflows and Execution Policy remain drafts.

The older four-article release patch is not applicable unchanged.

## Verification

- All 227 tests in 22 docs-ui test files passed, including production/preview boundaries and internal article links.
- TypeScript build and the production site build passed with `NP_BLOG_PREVIEW=0` and canonical origin `https://www.nodepilot.run`.
- All six PowerShell blocks match between languages and parse with Windows PowerShell 5.1.26100.9818. The earlier [Windows Server laboratory evidence](gmsa-content-validation.md) was reviewed; the domain/SMB tests were not rerun for this publication.
- Local and live browser checks passed for both languages at 1440px and 390px: article text, six code blocks, publication date, canonical URLs, reciprocal language links, no noindex directive, no document overflow, no JavaScript page errors, nine-entry blog listings and the contextual foundation links.
- Production output and sitemap exclude all twelve drafts. Desktop and mobile screenshots were inspected.
- Every existing remote file selected for replacement matched a fresh build of the unchanged source baseline before upload. Copies were saved locally.
- The scoped upload completed successfully: 37 HTML files, one content-hashed JavaScript file, `sitemap.xml` and `llms.txt`, 40 files total. Shared navigation, catalog references and related links account for changes beyond the two new article pages. Documentation, browser demo and media files were not uploaded; all 54 training videos were already present.
- After upload, all 40 files were fetched from the public origin and their SHA-256 hashes matched the verified build. Both article URLs returned HTTP 200.

## Local evidence

Ignored artifacts are under `out/gmsa-publication/`: build/test logs, `upload-plan.json`, the `remote-before/` backup, local/live browser screenshots and `verify-publication.mjs`. `out/Publish-GmsaSite.ps1` is a task-local copy of the established publisher, restricted to the verified manifest and guarded by path and hash checks; its credential handling, host-key pinning, retry behavior and asset-before-HTML order are unchanged.

No Git commit, push, automatic publication schedule or Search Console indexing request was performed.

## Same-day editorial update

At the user's request, removed the introductory paragraph describing the 25 October lab environment and its test limitations from both article bodies. The other article markup and all six code blocks are unchanged. The seven publication/build tests passed, the production build succeeded, and the output diff contained only the two article HTML files. Both were uploaded and verified live against their build hashes. The internal laboratory evidence remains in the validation report.

The first upload manifest is retained as `out/gmsa-publication/initial-upload-plan.json`; `upload-plan.json` now describes the two-file editorial update. Evidence: `before-paragraph-removal/`, `verify-paragraph-removal.mjs`, `removal-tests.log`, `removal-build.log` and `removal-publish.log` in the same ignored directory.
