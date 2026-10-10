# Fixed items, explained

Working state on `audit/architecture-security-2026-10-09`. This list was extended during the
review. The effects describe possible everyday situations, not incidents claimed on your
installation. Technical evidence, severities and limits are in the
[full report](../architecture-security-review-2026-10-09.md) and the individual round reports.
A German version is in [fix-list-de.md](fix-list-de.md).

### 1. Subworkflows keep the actual caller.

A called workflow
no longer takes on the identity of its publisher. Otherwise an ordinary user
could have gained additional rights through a subworkflow published by the administrator.

### 2. Current user rights also apply within the same folder.

The previous shortcut
skipped a role check. A user who had since been demoted to Viewer
could therefore still have run subworkflows.

### 3. Waiting subworkflows are checked again before they start.

Permissions and
activation are reloaded after waiting periods. A workflow that was blocked in the meantime
or an access that was revoked is therefore taken into account even for calls that are already queued.

### 4. Moving ends live subscriptions that are no longer allowed.

Moving a
workflow and signing off the previous viewers follow a common order.
Otherwise an open browser window could have kept receiving live output after the move into a private folder.

### 5. LLM responses are limited before they are read in full.

The size check applies
while reading instead of afterwards. A faulty or tampered AI endpoint could otherwise
have filled the service's memory with a huge response.

### 6. Knowledge search does not follow hidden directory detours.

Links and junctions
inside the selected directory are excluded in a controlled way; the explicitly
configured entry point remains usable. This way the search does not read files located outside
and does not get stuck in a directory cycle.

### 7. Paused steps are completed cleanly after a restart.

The
restart handling also covers these steps. In the UI, orphaned pauses would otherwise have
remained, even though the original execution no longer exists.

### 8. A rejected start is not left stored as running.

Reaching a
capacity limit also goes through the completion of the execution. Otherwise the overview
would have shown a running job that was never actually started.

### 9. SCOrch follow-up pages stay with the original server.

Pagination checks target host,
protocol and port before the next authenticated call. A manipulated response
could otherwise have passed Windows credentials on to a different target.

### 10. Nested workflows do not block themselves at the execution limit.

Waiting
parents release capacity in a controlled way for their children. With many simultaneous
subworkflow calls, all slots could previously be occupied by waiting parents
while no child could start anymore.

### 11. PowerShell output already received is not lost when an output channel stays open.

The output is collected continuously. A leftover process with an open channel
could otherwise have caused results that had already been written to come back empty.

### 12. The final AI evaluation receives the complete task.

When space is short,
evidence is shortened first; if the task itself does not fit, there is an explicit error.
Previously, with long tasks, requirements at the end could disappear and the result
would still be rated as complete.

### 13. PowerShell and WinRM do not repeat scripts covertly.

Repetitions belong to the
explicitly configured retry rule. A script that first made a change and
then reported a specific error could previously run three times without approval.

### 14. The CLI detects a later connection loss.

If the already established
live connection fails, the status is queried through the existing API. The terminal
could otherwise wait indefinitely even though the job had long since finished.

### 15. Publishing stays bound to the selected workflow.

After a delayed save,
it is checked whether the original editor is still current. When switching quickly from
workflow A to B, the old action could otherwise have accidentally published B.

### 16. Execution data does not reveal protected parent workflows.

Names and IDs of the
parent run are only delivered with the corresponding read permission. A user
with access to the subworkflow would otherwise have seen details of a private parent workflow.

### 17. Invalid maintenance window types are rejected.

Unknown numeric enum values
are also checked. Previously, a seemingly saved maintenance window could be created
whose type the execution could not evaluate at all.

### 18. An import no longer checks unrelated webhooks.

The unnecessary global scan was
removed; imported workflows remain disabled at first, as intended. An error
in another workflow could previously prevent a completely unrelated import.

### 19. Desktop rollback starts the API only after the data was restored successfully.

PostgreSQL is provided separately and the restore success is verified. Otherwise
background jobs could already write into a partially restored database or
a failed restore could appear successful.

### 20. HTTP callers use the same complete target check.

REST activities,
agent tools, MCP connections and webhooks share the existing policy seam.
An approved proxy call could previously bypass protection checks for individual callers
that blocked cloud metadata or non-approved targets elsewhere.

### 21. Cancelled file watchers clean up resources that arrive later.

This also applies
to errors during setup. If a network drive responded late after a server switch,
a watcher that was no longer needed could otherwise remain.

### 22. The archive check visits all archives over time.

A bounded pass remembers
its progress and starts again from the front after a complete cycle.
Previously the same oldest files were always checked; later damage remained undetected.

### 23. After a reconnect, completed jobs are reloaded.

The UI
reconciles known active runs with current individual queries. After a Wi-Fi outage,
a job that had meanwhile finished could otherwise keep appearing as running.

### 24. SCIM removal of folder groups also ends existing session rights.

Changes
take into account not only global roles but also groups with folder access.
A window that was already open could previously keep seeing private live data after the removal.

### 25. The load test uses valid workflows and the current API contract.

Triggers,
branches, publishing and the paginated count of running jobs are corrected.
The documented test could otherwise fail already in its preparation and deliver no
usable statement about the actual system load.

### 26. Invalid extreme time zone values do not cause a numeric overflow.

The check uses
a direct value range. A specially malformed client value could otherwise have aborted
a knowledge query with an internal error.

### 27. Connecting and pasting directly in the designer triggers saving.

Both paths mark
the workflow as changed. Previously, visibly pasted nodes or edges could disappear
when leaving the editor because autosave detected no change.

### 28. A backup restore updates live permissions after the commit.

Restored
folder assignments and group memberships also take effect for existing connections.
Otherwise earlier rights could persist after the restore.

### 29. Newly created backup copies point to the correct subworkflows.

References
are rewritten only once all new IDs are known. A flow restored as a copy
could otherwise have accidentally started the original subworkflow.

### 30. The alert for failed schedules also detects frequent schedules.

It looks for the last
relevant due time outside the tolerance period and avoids a truncated forward scan.
A job running every minute could otherwise have been failing for hours without triggering this alert.

### 31. The development restart ends only processes of this project.

Local listeners and
executable paths are checked before stopping. Previously the helper could also close other
Node projects or an unrelated tool.

### 32. Website publishing stops on a failed file upload.

After
retries are exhausted, no dependent HTML page is published anymore.
Visitors could otherwise have received a page whose JavaScript or styles are missing.

### 33. Database passwords do not end up in provisioning arguments and SQL error texts.

The existing secure handover via standard input and child process environment is used;
errors containing credentials are not printed. An installation error could otherwise
have left the new password in the process call or the installation log.


### 34. Alert editing keeps advanced settings.

Occurrence threshold,
time window and route filters are preserved during simple UI changes. Someone who only
renamed an elaborately configured rule could otherwise suddenly trigger unfiltered
or considerably more frequent notifications.

### 35. A skill import still belongs to the original sign-in.

After the asynchronous
reading of the file, the sign-in session is checked again. With a logout/login in between,
the old import intent could otherwise be executed under the new administrator.

### 36. Dashboard caches respect moved workflows.

Old calculations stay
in their old cache generation after invalidation. Otherwise names and
error outputs of a workflow that had meanwhile become private could still come back from the cache.

### 37. Live events cannot write a stale folder assignment back into the cache.

Resolution, dispatch and moving are coordinated and time-limited. With an
unfavorable concurrent sequence, status messages would otherwise have continued to reach the previous viewers.

### 38. Manual queues also check permissions directly before the start.

This applies equally
to debug and retry. A job queued hours earlier could otherwise still start
even though the requester's execution rights had been revoked in the meantime.

### 39. SCIM errors keep their prescribed response format.

The general REST filter
no longer alters these typed protocol responses. A connected
user management system could otherwise misclassify errors and abort provisioning.

### 40. Removed sign-in groups and roles stay removed.

A higher-priority configuration source
replaces permission lists completely, including emptied lists and individual
mapping fields. A group removed from the settings could previously keep taking effect from the
base file and even retain its old admin mapping.

### 41. Deleted MCP read grants are actually revoked.

The same shared
list logic applies to tool grants. An AI agent could otherwise have continued to
call a tool whose grant the administrator had just removed.

### 42. Removed network exceptions stay blocked.

REST and probe-target lists also use
the complete effective list. An internally reachable service could otherwise still be
contacted despite the exception having been deleted. The display of file system shares
also uses the same strict reader as execution.

### 43. Dropped support events are counted correctly.

The non-blocking
channel now correctly reports a full buffer. Under high load, some of the
support events could previously be missing while the loss indicator wrongly stayed unremarkable.

### 44. Whitespace does not silently change the log format.

Validation and formatter normalize
the setting identically. An accidental space could previously produce plain text
instead of JSON and thereby disturb log analysis.

### 45. Daily logs also include their follow-up files.

Download and tail take
files split by size into account, in the correct order and using the local roll day.
The display reads only as many new segments as necessary. After the file size limit was reached,
the newest messages could otherwise be missing from the display and the daily download.

### 46. The LLM connection test reads only the required response.

Success responses are
not buffered completely; error texts are bounded and subject to a deadline.
A hanging or very large response body could otherwise put unnecessary load on the test.

### 47. The experimental acceptance catalog matches the application again.

Agent activities,
agent settings and the changed editor/reconnect cases are covered.
Otherwise a later acceptance run would have omitted important new functions. The
catalog check is green; it did not replace an actual computer-use acceptance run.

### 48. Restored agents use the newly created machines and credentials.

Nested agent/team configurations are also validated and rewritten via the existing reference seam.
A backup copy could otherwise run on the original
machine or with the original credentials.

### 49. Removed proxy exceptions really no longer apply.

REST and LLM connections
replace the bypass list completely, also when the settings are reloaded.
Otherwise a target could still be contacted directly even though the administrator
had made the route via the corporate proxy mandatory again.

### 50. The load test reads the actual number of running executions.

Its client uses
the `total` field from the shared API contract. Previously the final
evaluation aborted despite successful requests; the regression now serializes the real
response type instead of a hand-built test response.

### 51. Renaming a Custom Activity preserves its resource limits.

Memory and
process limits are retained, even though the form shows no fields of its own for them.
Someone who only renamed an isolated activity could previously lose its restriction unnoticed.

### 52. CLI triggers respect the configured TLS pin.

Profile and command-line option
now also take effect when signing in with a trigger API key. On a desktop installation
with a self-signed certificate, exactly this command previously failed although the other
CLI commands worked. A wrong pin is still rejected.

### 53. Long valid Custom Activity names fit into execution data (Medium)

An allowed key of 64 characters needs 71 characters in total with `custom:`. The database fields for step type and support event were smaller; PostgreSQL/SQL Server therefore could not store valid runs. Shared length limits and an additive migration now preserve the full type names. In practice, even a long, descriptively named Custom Activity runs. Two model regressions were red; native migration scripts, Up/Down/Up and preservation of existing SQLite data were checked.

### 54. Concurrent changes do not overwrite an already released Custom Activity (Medium)

The existing version token is now also checked at the database commit. Delete, rollback and enable require the state seen when reading; stale changes end with the existing conflict notice. In practice, an edit opened in parallel cannot unnoticeably overwrite a release made in the meantime. A real save barrier reproduced the bug; six further store cases and the shared Data focus with 34/34 tests were green. This is a governance/integrity bug, not a claimed general operator sandbox.

### 55. Freshly confirmed sign-in rights stay valid.

The automatic expiry check
re-evaluates the current directory state within its database change.
If the directory confirmed the rights again during a running check, that check could
previously still revoke the valid session and cancel a job that had just started.

### 56. Incomplete AI responses do not trigger tools.

Even if a
complete tool call is already in the response, an abort due to the
output limit remains recognizable. Otherwise an agent could execute part of a still incomplete
action sequence, for example start a job, before the response was complete.

### 57. Renamed SQL columns do not reveal protected content via star queries.

Database administration and AI queries reject the automatic reading of all columns
of protected tables. Required normal columns and count queries remain available.
A renamed result table could previously hide, for example, a password hash from the
column masking and pass it on to the AI context.

### 58. Layout restoration preserves later workflow changes.

It resets only the positions
of nodes that still exist. After "Tidy up", editing and "Original layout", new nodes could
previously disappear, deleted ones return and changed settings be
overwritten; autosave would then have saved this old state.

### 59. Administration, demo and load-test scripts match the API again.

Sign-in,
edit lock and publishing follow the current contract. The shared
Python client prevents further deviations between the clients. More than
500 workflows are also found; faulty or unfinished runs return an error code.
Alert seeds sign in explicitly. Previously the helpers could abort immediately,
leave unpublished workflows behind or report success despite failed work.
PowerShell also sends definitions as clean UTF-8 text instead of laboriously processing
provider metadata.

### 60. Global secrets survive a CLI export followed by an import.

The export
omits their value as `null`. `--upsert` thereby preserves an existing secret, even with
the old `***` mask. New secrets need a real value; otherwise the command ends
with an error. Previously a configuration round trip could unnoticeably replace the real SMTP password
with three asterisks.

### 61. Secret return fields stay hidden in the history too

**Medium, secret protection.** Return data is now sanitized when stored, based on field name and value. Previously, for example, `smtpPassword` or `accessToken` could lose their name-based protection and end up in the execution history. A subsequent step still receives the unchanged data internally; the stored history contains the protected representation.

### 62. Windows service actions produce executable PowerShell

**Medium, function.** The error check of three `sc.exe` actions now uses a correctly delimited exit-code expression that also survives the workflow template substitution. Previously the generated script already failed at parsing. In practice, a service can, for example, be configured again; real Windows errors remain visible as errors. Parser tests and subsequent activity integration tests cover both processing stages.

### 63. A cancelled wait branch does not become a workflow failure

**Medium, function.** `WaitForCondition` passes on the cancellation during a poll pause as a cancellation. Previously it was turned into a timeout or error. When, with "wait for the first completed branch", another branch wins, the losing wait branch now ends its work quietly instead of making the entire successful run fail.

### 64. Difficult search patterns do not block text editing indefinitely

**Medium, availability.** Both regex paths of text file editing now have a bounded evaluation budget. Previously an unfavorable pattern could compute indefinitely on a matching long file. Such inputs now return a structured error without modifying the file; two isolated process regressions demonstrate the behavior.

### 65. Output aliases are validated the way they are resolved later.

`shared` and
`SHARED` now count as the same name during validation, just as during execution.
Otherwise a workflow looked valid although two steps delivered under the same name
and a subsequent step could thereby use the wrong output.

### 66. The dashboard shows the actual database polling interval.

It now uses
the same configuration reader as the trigger. Differing spellings, default values
and minimum intervals are thereby treated identically. A trigger polling every five seconds
could previously be displayed with a different or missing interval.

### 67. "Run" in the script editor executes the text currently visible.

Both the large
dialog window and the properties view pass the current text directly to
the step test. Previously, after a change, the previous script version could still run on
the target machine, because the saved React configuration was only updated afterwards.

### 68. Links created after the fact do not bypass the watched folder

**Medium, file boundary.** The FileWatcher snapshot, reconciliation and delivery use the same reparse check as direct file events. Previously, a link created only after startup could cause the reconciliation to include files outside the permitted directory. A failed scan also keeps the previous snapshot. Real file and directory links created after the fact reproduced the difference.

### 69. Metric queries stay entirely within the permitted scope.

Every single
Prometheus selector must belong to an allowed metric. An allowed metric at the
start of the query is no longer enough to mix in further arbitrary measurements.
With a shared Prometheus, data from other services could otherwise be returned as well.

### 71. Numbers in alert policies are reliably applied

**Medium, function and structure.** The system alert evaluator now uses the shared parameter conversion instead of a second, incomplete JSON evaluation. Previously, numeric values could silently drop out. A configured observation period of 60 minutes therefore takes effect even when it is stored as a JSON number, instead of silently falling back to ten minutes.

### 72. New problem cases are also checked behind many old runs

**Medium, function.** The check for executions that wait or run for too long continues its bounded scan with a cyclic cursor. Previously, the same oldest 200 entries occupied every pass. When many jobs are stuck, jobs that started later and are overdue are now examined as well and reported if necessary. Two tests with 205 runs each secure this.

### 73. Disabled notifications are no longer delivered

**Medium, function.** A pending delivery attempt reloads the rule and takes its enable switch into account. Previously, retries that were already queued could keep sending after the rule was switched off. Anyone who disables a noisy notification now also stops its pending retries.

### 74. A trigger that fails again raises an alert again

**Medium, function.** A complete healthy trigger snapshot ends the previous failure phase. Previously, its identifier persisted; a later real outage could therefore be suppressed as already reported. Partial or unavailable snapshots still do not count as all-clear, in particular on passive cluster nodes.

### 75. Stored host allowances actually take effect on the web server.

The setting
now lands at the place ASP.NET uses when loading; concurrent changes
as well as defaults from the environment and command line are still taken into account. Previously,
the UI could confirm a new company address while, after the restart, the
old list of allowed addresses still applied.

### 76. Concurrent creation does not produce duplicate active Custom keys (Medium)

A filtered unique index protects active keys, including drafts. Import skips the competing loser in a controlled way; restore rolls back the conflict and requires a new preview. In practice, two simultaneous create attempts no longer lead to duplicate definitions, which for example made the workflow chat abort while building its catalog. Existing duplicates are not deleted: the migration halts with a diagnostic. A real create race was first red, then green, including three provider scripts, tombstones and import/restore behavior; migration help documented.

### 77. A password change remains effective even with a concurrent login or refresh.

The new session stays bound to the security version for which the password
or the existing token was verified. If an administrator changes the password at exactly
that moment, the old request does not accidentally take over the new
approval. Normal logins and token renewals continue to work.

### 78. Knowledge search reliably recognizes the exact spelling of a workflow name.

If both "Job" and "job" exist, a search for "Job" finds the right workflow even on
a database that otherwise treats upper and lower case the same. The search now uses
the same name resolution as the other entry points and still respects the
permitted folders. Previously, an existing workflow appeared as ambiguous without need.

### 79. Old directory groups keep no folder rights through another fresh group.

If, for example, the general sign-in group is updated via SCIM, a
stale membership in the finance folder remains invalid anyway. This applies to API calls,
sub-workflows and already opened live views. Direct user grants and
sufficiently fresh groups remain usable; the live connection only loses the
subscriptions that are no longer permitted.

### 80. CLI live output uses the same TLS settings as REST

**Medium, function.** A server reachable via certificate pin or an explicit insecure exception now also works for live output. Previously, the SignalR connection ignored these settings and fell back to polling. The existing decision "valid certificate chain or matching pin" is retained. Two real local TLS hub cases were red; the combined CLI focus passed with 89 tests.

### 81. A key rotation covers all stored secret areas and rollback files.

At startup the server can already read with the temporarily configured old key.
The sweep then also re-encrypts notification routes, waiting start parameters
and runtime settings including rollback files. Previously, after
removing the old key the server could fail to start or a waiting execution could not read its password,
despite a previously reported success. Concurrent changes are not
overwritten; damaged entries are shown as partial failures in the API, CLI and UI.
In HA, each node must switch its own runtime files.

**Medium, function, shared API/client fix.** Rotation also takes notification routes, waiting execution parameters, and active and backed-up runtime settings into account. The CLI shows the additional success and failure counts including exceptions, so partial successes remain recognizable. Two CLI contract cases were red; combined CLI focus 89 green. The API change and its own evidence are documented in the runtime review report.

### 82. The Operations view discards old live status on user switch.

Messages from a closed connection that are still in transit are ignored;
on re-subscribing, the refresh timers work again as well.
Previously, old status data could remain in browser storage or updates
could fail to appear. The existing permission filters already prevented the display
of other users' workflows; what was fixed is the reliability of the local state.

### 83. SCOrch redirects stay with the configured server

**Medium, security.** The Switcher now checks every HTTP redirect step before the next request. A different server receives no credentials or start/stop data. Bounded redirects within the same origin remain possible. Three local redirect cases were red; all 109 Switcher tests are green, without real Orchestrator actions.

### 84. A new step no longer re-enables a disabled connection.

On insertion,
both parts of a previously disabled connection stay disabled. Previously,
the inserted step could be executed on the next run even though the branch
had been switched off, for example a script to restart a service.

### 85. Installation directories require a trusted owner

**High, security.** A foreign owner could change the permissions again despite initially safe access rights and swap out service files. Check and repair now also take the owner into account before new files are copied. An isolated temporary Windows directory reproduced the gap; the ArtifactSecurity checks are green. No installation was changed.

### 86. Server updates preserve the complete Switcher configuration

**Medium, function.** Supported local settings are preserved byte for byte. Previously only the server URL survived, while other options were replaced by the template. Invalid or unreadable configuration stops before the swap; restore errors reach the rollback. The actual script parts were checked against isolated temporary files.

### 87. LLM connection errors keep their specific cause.

If, for example, name
resolution fails, the hint about DNS is retained instead of being replaced by a generic
network error. Port errors, certificate problems and TLS timeouts
remain distinguishable. This way one does not look at the model or response time when the
connection to the model server is already failing.

### 88. The sub-workflow preview uses the shared API integration.

A separate
copy of the sign-in and error handling is dropped. As a result, concrete error codes
and retry periods are preserved, and database problems reach the same
health check as on other pages. Previously, the preview could only show a generic
HTTP error even though the API had already supplied a helpful explanation.

### 89. The read-only workflow view no longer offers insertion.

The
plus button on connections respects the same edit permission as other
Designer actions. Previously, one could seemingly add a step whose
saving then failed because of missing rights or an edit lock.

### 90. Schedule preview shows the dates the server actually computes

**Medium, function and structure.** The preview in the node and in the settings window now jointly uses the server's existing Quartz computation. Previously, a number as a weekday could show a different day, a year restriction could disappear, or the browser time zone could differ from the server. In practice, a job would have been scheduled for the wrong time, or one would have kept counting on executions after its schedule had expired. Fast input does not produce one request per keystroke; stale previews disappear immediately. The offline demo points out unsupported syntax. UI, cache, demo and backend checks including daylight saving changes are green.

### 91. The parameter overview does not reveal hidden default values.

Readers without edit permission now see existing default values of a workflow as
`***`; names, types and required flags are preserved. A
service password stored in a default value could previously be read via the contract query, even though the normal
workflow view already hid it. The actual value remains
stored for execution, and the UI still recognizes that a default exists.

### 92. Database credentials are written only after the registry is secured

**Medium, security.** The installer and LocalDB provisioning first set the secure registry permissions and only then publish the credentials. The previous brief read window is removed. Regressions run the actual script parts against an isolated registry replica; no production registry was changed.

### 93. Full desktop updates replace the entire delivery scope

**Medium, function.** Besides the application, desktop and PostgreSQL, deployment scripts and tools are also checked, updated and reset on errors. Previously, the old installation script could run afterwards. All five components are validated before stopping; on rollback, newly added components that were missing before also disappear. Isolated file system tests are green.

### 94. Safely skip old events when the Eventlog trigger starts

**Medium, function.** After a restart, a Windows event arriving at that moment could start processing before the old events from the downtime had been skipped. As a result, earlier actions could be triggered unexpectedly. The start lock now stays closed until the stored new starting point. New events afterwards are still processed. A controlled parallel test was red beforehand; the combined trigger/alert test run is green with 119 cases.

### 95. Offline runtime packages can be copied from the specified directory

**Low, function.** The build helper uses actual file paths instead of an ineffective asterisk with LiteralPath. The documented offline build path therefore also works with special characters in the directory name. A temporary file structure with brackets in the path demonstrates the error and the fix.

### 96. Alert retries keep filter and urgency

**Medium, function.** If, for example, an alert monitors the last hour and mail delivery briefly fails, the retry attempt suddenly used only five minutes. An existing event was then discarded as vanished; in addition, "Critical" could silently become "Warning". Retries now use the settings of the matching active alert rule again. Two real failed first deliveries with subsequent retry were red beforehand; filter, urgency and cancellation are checked afterwards.

### 97. Backup completely overwrites alert recipients and scopes

**Medium, function.** When restoring an existing alert rule, its old recipients and monitored workflows previously remained. As a result, a mail could still go to a removed recipient; with identical scopes, the restore could fail altogether. The restore now loads the existing routes and targets and replaces them completely on overwrite. "Skip" still keeps the rule unchanged. Two failure cases were red at first; afterwards all 84 backup tests are green.

### 98. Failed SQL pre-checks do not delete an existing database

**High, data loss.** The remote SQL test cleans up its database and login only after an explicitly and successfully captured empty initial state. Previously, a failed pre-check could treat existing resources as newly created. Missing, invalid or faulty remote responses abort safely. Seven isolated script cases check the states; no SQL Server was changed.

### 99. TestSuite installation also finds older existing workflows

**Medium, function.** The installer uses the complete name list and loads required details selectively. The previous overview was limited to 500 entries; repeated runs could therefore create existing workflows again instead of updating them. A real consumer test with 501 workflow entries is green and retains the checkout check.


### 100. Switching folders in the database editor ends old live subscriptions

**Medium, security.** If an administrator moves a workflow into a protected folder via the FolderId cell, previous readers no longer receive any further live output. Before, a window that was already open could keep listening even though a new access to the same run was already denied. The cell path now uses the same lock and subscription cleanup as the normal folder change; cell editing and its audit entry are kept. A real viewer/hub/notifier test was red before; afterwards all 229 checked database editor, move and notifier cases pass.

### 101. Failover check uses valid remote service calls

**Low, function.** Start and stop go through the existing remote execution path instead of invalid ComputerName parameters. HTTP 503 responses keep their diagnostic content. Offline checks cover the Windows PowerShell and the simulated PowerShell 7 error shape; no services were stopped.

### 102. Service and task scheduler test fixtures no longer clean up after each other

**Low, function.** The janitor and teardown limit their cleanup to their own fixture family. Before, a test running at the same time could remove resources of the other family. Generator and isolated cleanup checks are green; the existing parallelism limit within the same family is kept.

### 103. The try/catch template can log the actual error

**Low, function.** The template's script step now exposes its results under the name its error log refers to. Before, inserting it changed the internal step ID while the error text still pointed to the old name. In practice, the template could therefore not pass on a usable error text exactly in the error case. A regression on the graph that is actually inserted was red; 40 template/insert tests are green.

### 104. A visible debug pause can be resumed immediately

**Low, function.** The resume function is now ready before the pause is saved and shown. Before, a fast click on "Resume" could still be rejected even though the step already appeared as paused. Cancellation and failed notifications also reliably clean up the waiting entry. Tests trigger the resume directly during saving and during notification; both cases were red before.

### 105. HA configuration template starts with the real .NET JSON provider

**Medium, function.** Comment-like JSON properties have unique names. Duplicate `//` keys previously prevented the generated configuration from being read. A check of the actually rendered template with the real provider was red and is now green.

### 106. Settings names have a common source

**Low, structure and usability.** The tabs and the navigation path now read their section names from the same mapping. Before, "AI knowledge" was missing from the separate navigation table and the path showed "Integrations" instead. New sections therefore no longer have to be added to two different name tables.

### 107. Grafana receives the announced dashboard catalog

**Medium, function.** The existing provider file is now actually shipped through a targeted gitignore exception. Mounting only dashboard JSON files was not enough for the auto-import. Offline checks confirm the mount, the provider and ten dashboard identifiers against the official provisioning contract; no container run is claimed.

### 108. Failed power actions report an error

**Medium, function.** Shutdown, restart, logoff and hibernate now check the return code from Windows. If Windows rejects a restart because of missing rights, for example, the step counts as failed and keeps the error message. Before, the workflow could still report success and run further steps. Tests replace the Windows program with a harmless function and check success and failure for all four actions; no machine is shut down.

### 109. Workflow selection also covers older workflows in large installations

**Medium, function.** Maintenance windows, both alert rule editors and the sub-workflow check now use the existing full name list with a shared cache. The overview used before returns at most 500 workflows. In large installations, an older workflow could therefore not be selected for maintenance or alerts, or wrongly appeared as unknown in the designer. The name list still respects the server-side read permissions. A real selection check was red before; 151 tests of the affected pages are green.

### 110. Scheduled task names stay plain text in error messages too

**Medium, security.** The error path of the local task scheduler replacement now inserts the task name into the message as text. Before, special characters in a dynamic name could change the generated PowerShell code or run an embedded expression. Two tests with quotation marks and a harmless marker expression reproduced the problem. The combined debug, power action and task scheduler test run is green with 118 cases.

### 111. Audit history stays coherent when filtering and loading more

**Medium, function and structure.** All loaded pages now belong together to the respective filter in the query cache. Late responses of an old filter can no longer append foreign entries; fast double clicks load the same page only once. On refresh, the page sequence is built from the new continuation tokens. Before, entries could appear twice or be missing from the display while investigating an incident. Three real UI regressions were red; afterwards all 23 audit page tests as well as TypeScript and focused ESLint pass.

### 112. Table of contents follows the language switch of the documentation

**Low, function.** When switching between German and English, the table of contents reads the new headings and jump targets. Before, it could stay in the old language on the same documentation page and point to sections that no longer exist. The small fix is checked by source cross-checking, TypeScript and the existing documentation suite with 221 tests; a targeted browser check is still pending.

### 113. A lost database acknowledgement does not undo a finished restore

**Medium, data consistency and permissions.** If the database saves a backup successfully but the connection acknowledgement is lost, the restore now checks an identifier in the same transaction. Before, the data could already be changed while the settings were rolled back and old live subscriptions remained. A confirmed commit keeps its settings and revokes outdated subscriptions; a commit that actually failed rolls the settings back. If the check is also unreachable, nothing is blindly re-imported or written back: existing live access is revoked as a precaution and the operator receives a concrete check message. Three local failure cases cover these states; all 87 backup tests are green.

### 114. LoadHarness dashboard uses existing metrics and is wired in

**Low, function.** Histogram names and millisecond units match the producer; the queue length remains a point-in-time value instead of a rate. Provider, datasource and mount complete the documented auto-import. Three configuration checks are green; no Grafana run was executed.

### 115. A failed ZIP backup keeps the previous archive

**Medium, function.** The compression first creates a temporary file and replaces the old archive only after full success. Before, the old backup was deleted immediately. In particular, a backup in its own source folder failed on the second run and lost the previous backup in the process. The target is now excluded from the input files; an identical single source and target file is rejected. Tests check the repeated run, locked inputs and unchanged old files on errors.

### 116. A text replacement keeps the final line break

**Low, function.** The replacement now works with the complete original text including its final line break. Before, even a search without a match could remove this line break although the step reported zero changes. Deliberately added or removed line breaks remain possible; tests check LF and CRLF on real temporary files.

### 117. Guided demo does not treat disabled steps as executed

**Low, function.** The mission plan checks the active step set and reports the existing graph change error when required steps are missing. Three mission cases were red; all 139 demo tests are green.

### 118. Further pages of a log file stay readable

**Low, function.** The agent file reading tool detects the character encoding at the start of the file and ends a page only after a complete character. Before, the second page of a UTF-16 file was decoded incorrectly; UTF-8 characters could be split at the page boundary. For example, umlauts and other multi-part characters are thus preserved when continuing to read a longer log. The raw data transfer for collected log files remains unchanged.

### 119. An unsuccessful execution search can be corrected directly

**Low, function.** The search field and filters remain visible even without hits. Before, they disappeared exactly when a typo or status filter found no runs; to reset, the page had to be reopened. A real zero-hit case followed by a return to the list was red and is now green.

### 120. Execution search considers names and shortened IDs before paging (Low)

- **Problem:** The UI additionally searched for the triggering user and ID fragments after the server had already selected a page. Older matching executions thus remained invisible.
- **Fix:** These search fields now belong to the server-side search before counting and page selection. The redundant text search on the single browser page is removed; permission filters are kept.
- **In practice:** Anyone searching for a user or a copied short execution ID also finds matching older runs and gets a matching hit count.
- **Evidence:** Two original regressions red, then 79 execution/permission tests and 42 combined UI cases green. PostgreSQL/SQL Server translation additionally checked against the official provider sources; no live database test is claimed.

### 121. The quick editor keeps return data as an object

**Medium, function.** A double click on a return step now shows its data as readable JSON and saves an object again. Before, even saving without changes turned the existing fields into text; the next workflow run could then no longer deliver return data. Invalid JSON, arrays and single text values stay in the editor with an understandable error message. Five previously red cases are green, including saving unchanged and a real change.

### 122. Multi-selection no longer bypasses the write lock in the UI

**Low, function.** Readers and users without their own edit lock can still select several steps, but the bulk change stays locked. Before, the local view could be changed that way even though the server refused the later save. The real right designer panel now checks the same write capability as individual properties; the page handler checks it as well. The reader case was red, the editor case remains functional.

The same required write permission applies to replace as well as tidy/restore in the compact header. Read-only search remains available. There is no permissive default value for canWrite; the productive owner must pass it explicitly.

### 123. Running jobs open their live view

**Low, function.** A click on a running job in the dashboard now opens its workflow with the existing live display. Before, the link led to the history, which loads only completed runs and therefore did not show this job. Completed jobs still open their history. A navigation check was red before.

### 124. Custom activity warnings can be corrected without a version conflict

**Low, function.** If the editor stays open after saving because of notices, it now takes over the saved ID and the new version identifier. Before, the next correction failed with a conflict or tried again to create the same activity. The warnings and entered fields are kept. A test demonstrated the stale identifier; the shared page run for #119/#123/#124 passes with 90 tests.

### 125. Closed quick editors leave no outside-click listener behind

**Low, lifecycle.** If the quick editor is closed again immediately, the scheduled registration of its outside-click listener is removed as well. Before, the listener could only be created after closing and stay attached until the page was reloaded. A regression closes before the timer expires; together with the return data cases, all 17 quick editor tests pass.

### 126. Machine bulk test has a fixed parallelism limit (Medium)

- **Problem:** "Test all" started a remote connection attempt for every machine at the same time. The connection pool limits only attempts per target, not the number of different targets.
- **Fix:** The existing bulk operation mechanism distributes the work to at most four concurrent attempts. Double clicks do not start a second batch; after leaving the page or an identity change, no further tests begin.
- **In practice:** Even large machine lists do not cause a sudden surge of hundreds of remote connections. A single unreachable machine does not prevent the other results.
- **Evidence:** Twelve concurrent attempts reproduced before the fix; afterwards a maximum of four and all twelve unique targets including one failure case checked. Combined page focus 42/42 green; independent cross-check of the WinRM pool.

### 127. Shared lifecycle for the column widths of five tables (Low)

- **Problem:** Five pages contained almost the same mouse handler. When leaving the page during dragging, its global event listeners remained.
- **Fix:** A shared hook manages widths and the active drag movement; it removes listeners on release, on change of the column and on removal of the table. Individual minimum widths are kept.
- **In practice:** Tables no longer react to an old drag movement after page changes. Future corrections only have to be made in one place.
- **Evidence:** Real machine page change during dragging initially red, then 74 page tests green. Minimum widths, change of the active column and cleanup additionally checked in the shared hook; together with the editor/coordinate library 129/129 green.

### 128. Escape closes dialogs from their input field too

**Low, function.** ModalShell, the confirmation dialog and the cell editor handle Escape before deliberately stopping event propagation. Nested dialogs close only on the inside; editors can still claim Escape themselves, and a running save stays protected. Three regressions were red; 41 modal/DbViewer tests are green.

### 129. Large workflow inventories stay fully reachable (Medium)

- **Problem:** The list returned at most the 500 most recently changed workflows. Folder filter and search then ran in the browser; older workflows could remain invisible in their own folder.
- **Fix:** The shared paging endpoint filters permissions, folders and search text and sorts before the page selection. Statistics/capabilities projection remain shared; at most 200 entries per page. The workflow page, designer browser, folder popovers and quick switch use pages. The last ten visited workflows are loaded in a bounded batch. The older array contract actually used by CLI/MCP remains available in bounded form.
- **In practice:** Even the month-end close that has been unchanged for years can be found and opened. Sorting by runtime or success rate applies to the entire permitted inventory.
- **Evidence:** Eleven original API cases red, then 86/86 workflow/RBAC cases green. Additionally 17/17 paging cases including offline translation with real PostgreSQL/SQL Server providers. Two designer paging cases and one quick switch case first red; final 52/52 UI/FindReplace cases, 93/93 workflow page and 62/62 demo cases green. TSC and focused ESLint without errors/warnings. No live database/browser check is claimed.

### 130. Settings drafts and their version identifier survive a reconnect

**Medium, function and structure.** A background reload no longer overwrites unsaved settings. A shared form seam owns the draft, the associated ETag, the save operation and the exact payload for conflict retries. Only a confirmed save or an explicitly adopted server state replaces the draft. This also applies to new secrets and repeated 412 conflicts. A real reconnect reproduced the loss; 97 distinct settings tests and TypeScript are green.

### 131. Search jumps take moved groups into account (Low)

- **Problem:** The designer used the position of a group child relative to the group as an absolute canvas position. With moved groups, the view therefore jumped to the wrong place.
- **Fix:** Jumps to nodes and connections use the existing conversion to absolute coordinates.
- **In practice:** A found work step or connection is actually brought to the center of the screen even inside a moved group.
- **Evidence:** Independent cross-check of the coordinate contract and the existing conversion; shared editor/coordinate/resize focus 129/129 green. No separate browser check is claimed.

### 132. Open operations details reload the final error diagnosis

**Low, function.** When an observed run changes from Running to Failed, the error text, failed steps and final count are reloaded. Before, only the status display changed. A mounted component reproduced the missing diagnosis; afterwards all 39 operations detail/page tests pass.
