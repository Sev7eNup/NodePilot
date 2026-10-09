# Round 2 — AI, delivery and repository support

Status: completed findings round. This is a new source review, not a restatement of the earlier fix checks.

## Scope

The working-tree inventory assigns this pass all 55 files under `src/NodePilot.Ai`,
all 46 under `deploy`, all 226 under `scripts`, the seven `.github` files, root build/configuration
files, `grafana`, `samples`, supporting documentation, shared test infrastructure and load tests.
Generated/ignored outputs (`bin`, `obj`, `node_modules`, build artifacts and local logs) are not
first-party source. Additional shared Core modules: `Net`, `Time`, `Agents/AgentConfiguration`
and `Agents/AgentTool` (budget).

Inventory is not evidence of a completed code review. The following entries record the actual
module/entry-point review; remaining areas stay pending until their pass is complete.

## Architecture and security checks

| Module / entry points | Checks performed | Result so far |
|---|---|---|
| LLM factory, configuration, two wire dialects, transport, proxy/connect guard | DI lifetime and reload; URL-to-dialect ownership; auth headers; connection vs body deadlines; aggregate body caps; streaming framing and tool accumulation; redirect/proxy trust | Earlier byte-limit correction remains valid; no additional confirmed C/H/M |
| Workflow/script generation, workflow merge, both chat registries and shared loop | Untrusted prompt inputs; secret-mask merge; read-only tool dispatch; role/source/SQL gates; round/output limits; caller cancellation | No additional confirmed C/H/M; malformed model output remains non-executable proposal |
| Knowledge corpus, docs/source readers and time context | Root selection; eligibility; traversal; descendant reparse rejection; source denylist; file/result budgets; role wiring | Earlier confinement correction remains valid; concurrent malicious filesystem replacement by a trusted host actor is outside the threat model |
| Agent final assessment, runtime finalization and context compaction | Original task retention across the new final session; fixed instructions vs compacted history; schema and assessment validation | New Medium candidate confirmed independently: final prompt truncates original requirements even when the configured context has room |
| Agent evidence/register/team state and runtime/tool adapter | Shared budgets; progress/review transitions; local-only JSON Schema refs; observation vs summary distinction; transport retries vs tool replay; complete guarded dispatch and delegation envelope | No additional confirmed C/H/M |
| Artifact trust and staging | Signature/pin/validity/EKU/hash checks; held archive handle; protected staging; ZIP traversal and exact extracted manifest | No additional confirmed C/H/M; self-signed pin model is explicitly ADR 0012 |
| Desktop staged update / rollback | Documented advanced-use path; service ordering; database restore exit handling | New integrity candidate independently confirmed: API starts before restore; failed `pg_restore` can be reported as a completed rollback |
| Other installer/server-update/provision/uninstall paths | Install root/ACL and staging; signature/manifest chain; service identity changes; rollback state; source snapshot opt-out; uninstall ownership; PostgreSQL child process and secret handoff | New confirmed credential exposure in desktop PostgreSQL provisioning; correction delegated for independent review |
| CI jobs | Triggers, changed-file gating, artifact/dependency/secret checks, permissions, PR input interpolation and pinned actions | Reviewed workflows; no additional confirmed C/H/M so far |
| Development/nightly scripts | Process ownership, listener selection, test exit propagation and retained reports | New Medium: dev reset kills unrelated Node processes; corrected and offline tested |
| Site publishing | SFTP host-key pin, FTPS requirement, credential handoff, asset/HTML ordering and failed transfer handling | New Medium: failed asset upload still publishes dependent HTML; corrected and offline tested |
| Release laboratory | Named VM/checkpoint lifecycle, disposable computer-use targets, evidence binding/coverage gates, integration probe credentials, explicit external DB ownership checks | No additional confirmed C/H/M; not executed against VMs |
| Test-suite generators/installers, stress/demo samples | Generated topology/manifest ownership, opt-in invasive profiles, sandbox names, publication lifecycle, explicit caller-supplied credentials | No additional confirmed C/H/M; generated fixtures reviewed through emitters and representative supported entry points |
| Grafana and shared test infrastructure | Binding/authentication in compose and metrics configuration; SQLite connection ownership; provider-test database names/cleanup; loopback fake servers | No additional confirmed C/H/M |
| Load-test harness | API response envelopes, create/publish/execute lifecycle, trigger topology, no-op remote contract | Confirmed supported-path contract drift; correction delegated to client reviewer |
| Additional API Hosting and Configuration, Settings DTOs | Database readiness/probe/degradation; cluster fencing; TLS/data protection; runtime provider ordering, encrypted writes and ETags; validators and complete settings read/write adapters | Confirmed authorization-list provider tails; correction and tests below |

## Candidate counterchecks

- Two AI orchestrations are deliberate under ADR 0016; merging them solely to remove similar
  loop syntax would conflate chat proposal semantics with autonomous agent budgets and permissions.
- A proxy changes where DNS policy can be enforced. Explicit administrator-managed proxy trust
  is documented for LLM endpoints; this is not by itself an arbitrary unprivileged SSRF finding.
- The final-report truncation is not the ordinary bounded-evidence trade-off: it removes the
  authoritative task itself. Runtime creates a fresh final session, with no second complete task
  channel. The configuration accepts up to 64,000 characters; default finalization caps its task
  section at roughly 20,166. The correction should retain the complete task and shrink evidence
  excerpts first, failing explicitly if the task itself cannot fit.
- Desktop connection-string splitting is questionable for hand-written quoted passwords, but
  the provisioner generates separator-safe credentials. No independent supported-path failure
  has yet been established for that candidate.

## Evidence and limits

The review traces all owned module families and trust-sensitive entry points. It does not claim
that every generated fixture, documentation translation or test assertion received an individual
line-by-line reading. No installer, service mutation, real restore, deployment or live external
action is executed by this review.

## Confirmed corrections

- Agent finalization preserves the complete original task; evidence excerpts shrink first.
  Two failing regressions demonstrated a missing late task requirement and a missing explicit
  budget failure. Agent runtime/conclusion/context checks: **110 passed**.
  Everyday consequence: a long investigation could appear complete while its final constraints
  had silently disappeared from the final assessor's input.
- Desktop rollback starts only PostgreSQL for restore, waits for readiness, checks a transactional
  `pg_restore`, and restarts the API only after success. Failure leaves an explicit recovery error.
  Five offline scenarios passed under Windows PowerShell 5.1; independent client review agreed.
  Everyday consequence: background jobs could write into a partially restored database, or a
  failed restore could be announced as successful. Real restore/service behavior remains untested.
- Development reset selects only this checkout's API executable and normalized Vite script path;
  it checks all local listeners before stopping anything. Six offline selection scenarios passed.
  Everyday consequence: restarting NodePilot could stop another project or a Node-based tool.
- Site publication aborts after an asset exhausts its existing retries, before dependent HTML is
  published. Baseline AST execution reproduced HTML upload after failed JavaScript; three offline
  corrected-loop scenarios passed. Everyday consequence: visitors could get a blank/broken page
  referencing files which never arrived. This does not claim transactional whole-site publication.
- Low-severity input robustness: implausible timezone offsets use a direct range check instead
  of `Math.Abs(int.MinValue)`, preventing an overflow for malformed chat timezone input.
  Existing invalid-offset coverage now includes both integer extremes. The complete AI suite
  passed **719/719**, including this correction and the complete-task finalization regressions.

- Desktop/server PostgreSQL provisioning keeps credential-bearing SQL out of process arguments
  and redacts SQL error echoes. The existing stdin/child-environment process helper is reused.
  Desktop, server and packaging offline contracts passed under PowerShell 5.1; CI now invokes the
  two added credential tests in both PowerShell jobs. PowerShell 7 was not run locally.
- Load-test templates, publication lifecycle and paged running-count response were corrected;
  eight harness regressions and Release compilation passed (client report).
- High: layered configuration reintroduced removed admission groups and administrative role
  mappings. Eight real JSON-provider/startup-binding regressions failed before correction and
  passed afterwards. List selection now preserves only the highest declaring provider, including
  empty lists and complete mapping rows; scalar precedence and restart semantics remain intact.
  Six additional failing regressions demonstrated the same issue for MCP read grants and REST/
  network-probe destinations. A shared `ProviderAtomicList` under Engine/Security now serves both
  API and Engine without changing the dependency graph. The independent architecture review
  confirmed its scope and identified proxy bypass lists as a related remaining consistency check.
  The existing stricter filesystem-root reader is also used by its settings display, retaining
  its scalar-JSON support. Engine configuration/network/path/agent checks passed **116/116**.
  The proxy follow-up passed eight new regressions after their failing baseline, then the
  Engine proxy selection **30/30** and API LLM override/validator/probe selection **37/37**.

Every owned source family has received its round-2 pass and the confirmed corrections have
focused executable evidence. Combined full-project regression suites are recorded separately.
This round found new issues and therefore is not a zero-finding round. See `round-3.md` for
the next complete source-review pass.
