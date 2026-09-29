"""Versioned, human-operated release scenarios. No UI automation is executed here."""
SCHEMA_VERSION = 1
ROUTES = ['/', '/login', '/workflows', '/workflows/:id', '/executions', '/operations',
          '/ai-chat', '/machines', '/global-variables', '/custom-activities',
          '/maintenance-windows', '/alerts', '/metrics', '/metrics/:section',
          '/support-log', '/users', '/audit', '/database', '/backup', '/settings']
SETTINGS = ['Smtp', 'Llm', 'AiKnowledge', 'Retention', 'Authentication', 'Logging',
            'OpenTelemetry', 'Stats', 'DbAdmin', 'RestApi', 'FileSystemOperation',
            'WaitForCondition', 'SqlActivity', 'StartProgram', 'Webhook', 'ExternalTrigger',
            'Security', 'Performance', 'Engine', 'ExecutionDispatch', 'Threading', 'Remote']
THEMES = ['light', 'light-minimal', 'light-grey', 'light-bank', 'dark', 'dark-minimal',
          'dark-lila', 'dark-bank', 'dark-ion', 'dark-nebula', 'system']
INTEGRATIONS = ['winrm', 'sql', 'smtp', 'http', 'proxy', 'llm', 'ldap', 'windows-sso',
                'oidc', 'scim', 'eventlog']

# id, area, route, actions, observable acceptance; semicolons separate checkpoints.
JOURNEYS = [
 ('AUTH-01', 'authentication', '/login', 'Create the bootstrap admin using the installed setup token if needed; sign out; submit a wrong password; sign in with the correct password', 'Wrong login is refused; correct identity and role appear; no token or password is exposed'),
 ('AUTH-02', 'authentication', '/users', 'Create run-prefixed Operator and Viewer accounts; edit a display name; reset a disposable account password; disable and re-enable that account; delete the disposable account', 'Changes survive reload; disabled and deleted accounts cannot sign in; other accounts remain intact'),
 ('AUTH-03', 'authentication', '/workflows', 'As Admin grant Operator edit and Viewer read on the run folder; as each user try edit, execute, export, sharing and deletion; remove the grants and repeat', 'Allowed actions succeed; denied actions cannot be completed, including after reload; removed access disappears'),
 ('WF-01', 'workflows', '/workflows', 'Create a folder and a workflow; rename both; add a description; search and sort; duplicate; move the copy between folders; delete the copy and empty folder', 'Each mutation persists after reload; counts and search results update; canceling deletion preserves the object'),
 ('WF-02', 'workflows', '/workflows', 'Import the prepared workflow JSON through the native file picker and drag/drop; import the prepared SCOrch archive; try malformed and over-limit input', 'Valid workflows appear with correct folders and disabled state; invalid files show actionable errors without partial misleading success'),
 ('WF-03', 'workflows', '/workflows', 'Export one workflow and then all run workflows; choose a path in Save As; cancel a second save; export the workflow diagram as PNG from the designer; import the saved single JSON export', 'Saved JSON parses and matches the authored graph with secrets redacted; canceled save creates no file; PNG Save As creates a readable image of the graph; reimported graph is usable'),
 ('WF-04', 'workflows', '/workflows', 'Exercise bulk selection, bulk move and recursive folder deletion using disposable copies; inspect sharing controls', 'Bulk scope is correct; confirmation identifies the affected folder; Viewer cannot delete and Operator cannot bypass recursive-delete restrictions'),
 ('DES-01', 'designer', '/workflows/:id', 'Build manual trigger -> log -> return data from the palette; connect and move nodes; edit properties; save, reopen, publish and run', 'Graph geometry and values persist; execution displays the exact Unicode marker and return value'),
 ('DES-02', 'designer', '/workflows/:id', 'Copy/paste and duplicate nodes; undo/redo edits; select multiple nodes; delete a selection; use node context menu and snippets; zoom, fit and auto-layout', 'Selection and graph remain consistent; undo restores the prior graph; pasted IDs and edges are valid; saved layout survives reopen'),
 ('DES-03', 'designer', '/workflows/:id', 'Add parallel branches, decision conditions and a wait-all junction; edit an edge label and condition operands; deliberately leave an invalid graph and then correct it', 'Lint identifies the actual invalid nodes; corrected graph publishes and takes the expected branch; manual labels persist'),
 ('DES-04', 'designer', '/workflows/:id', 'Open the script editor; type and complete global, input and upstream references; use search, multiline edit and script generation; save and reopen', 'No extra closing braces or lost text; validation is visible; generated script can be reviewed before accepting; Unicode and syntax highlighting are readable'),
 ('DES-05', 'designer', '/workflows/:id', 'Checkout a workflow; try editing from a second identity; force-unlock as Admin; publish a revision; compare history and roll back', 'Lock owner is shown; second editor is refused; rollback restores the selected graph without erasing history'),
 ('DES-06', 'designer', '/workflows/:id', 'Toggle toolbar layout, minimap, panels, live console, node styles and grid; follow a sub-workflow breadcrumb and return', 'Controls do not overlap; layout preferences persist; navigation opens the correct workflow without losing saved edits'),
 ('EXE-01', 'executions', '/executions', 'Run with declared inputs; inspect live steps, return data, history and trace; search, filter and paginate executions', 'Inputs and outputs match the entered markers; live state converges to persisted history; filters and paging retain their meaning'),
 ('EXE-02', 'executions', '/executions', 'Start a long delay; cancel it; run a deliberate failure and retry; inspect failed step details and retry origin', 'Cancellation finishes; error identifies the failing step; retry is a distinct execution linked to the original'),
 ('EXE-03', 'executions', '/workflows/:id', 'Set concurrency to one; start two runs; observe Pending; cancel the first; use cancel-all on remaining runs', 'Second run waits and then starts; no execution remains running or pending after cancel-all'),
 ('EXE-04', 'executions', '/workflows/:id', 'Set a breakpoint; debug; inspect variables; Step Over; change a permitted override; Continue; repeat and Stop', 'Pause and step boundaries are correct; override affects the intended value; stop terminates the debug run'),
 ('INF-01', 'infrastructure', '/machines', 'Create a credential and a run-scoped WinRM machine; test connection with valid and invalid credentials; edit and run a remote marker command; delete disposable copies', 'Connection results distinguish success and failure; hostname/output prove execution on the target; credentials are masked'),
 ('INF-02', 'infrastructure', '/global-variables', 'Create nested folders, a plain Unicode variable and a secret; move and rename them; use the picker in a workflow; delete a disposable copy', 'Values persist and resolve; secrets stay masked in UI, execution logs and exports; folder counts update'),
 ('INF-03', 'infrastructure', '/custom-activities', 'Create and enable a Custom Node with a global only in its script template and another in an input default; run it; edit/version/rollback; export/import a copy; disable the original and try running it', 'Both globals resolve exactly; disabled node fails clearly; imported copy is disabled; outputs and definition provenance match; missing globals fail before script execution'),
 ('OPS-01', 'operations', '/', 'Run successful, failed and queued workflows; inspect dashboard cards, trends, drill-downs and time ranges', 'Displayed counts and links correspond to the seeded executions; empty and populated states are usable'),
 ('OPS-02', 'operations', '/operations', 'Filter running, pending and failing workflows; open incident details; cancel a run; inspect polling/reconnect status', 'Live-Ops actions target the selected execution; state refreshes without stale incidents'),
 ('OPS-03', 'operations', '/metrics/:section', 'Visit every metrics tab; change period and workflow filters; inspect chart tooltips, empty states and detail navigation', 'Axes, units, legend and counts are consistent; filters are preserved where shown; no chart crashes'),
 ('OPS-04', 'operations', '/maintenance-windows', 'Create, edit and enable blackout and allow-only windows; attempt a new run inside and outside the interval; disable and delete test windows', 'New dispatch obeys the window; running execution is not canceled; time zone and next occurrence are explicit'),
 ('OPS-05', 'operations', '/alerts', 'Create notification rule; preview its filter; test-fire into the lab mailbox/webhook; trigger a real matching failure; edit system policy; disable and delete the rule', 'Preview and delivery agree; one expected message reaches the sink; disabled rule sends none; secret fields stay masked'),
 ('OPS-06', 'operations', '/support-log', 'Filter level, dates and text; inspect details; pause/resume the live console; download log and diagnostics bundle with native Save As', 'Filters select expected markers; pause does not lose history; downloaded files contain the intended sanitized data'),
 ('OPS-07', 'operations', '/audit', 'Filter by user, action, resource and time; paginate; export; inspect records of create, edit, publish and delete from this run', 'Each action is attributable to the correct user/resource; export respects the selection and contains no secrets'),
 ('ADM-01', 'administration', '/database', 'Browse tables; filter and paginate; execute SELECT 42 AS answer; edit a disposable sandbox row with write confirmation; cancel a second edit', 'Read returns 42; write requires the documented confirmation and audit; canceled edit changes nothing'),
 ('ADM-02', 'administration', '/settings', 'Create and test SMTP, SQL, WinRM, HTTP/proxy and LLM settings using lab endpoints; save, reload and exercise each consumer', 'Connection tests and real consumers succeed; invalid endpoints produce useful errors; secret masks survive unrelated saves'),
 ('ADM-03', 'administration', '/settings', 'Open the same settings section in two sessions; save in one and attempt a stale save in the other; resolve the conflict', 'Conflict is visible; no silent overwrite; chosen final value is persisted'),
 ('AI-01', 'ai', '/ai-chat', 'Ask about the seeded workflow and lab documentation; stop streaming; retry; start a new chat; test Admin and Viewer knowledge access', 'Streaming/cancel are usable; responses refer to seeded content; Viewer cannot retrieve admin-only database knowledge'),
 ('AI-02', 'ai', '/workflows/:id', 'Generate a workflow and script from a simple lab prompt; inspect and apply the proposal; ask for an edit; reject another proposal; publish and run', 'Only accepted changes enter the graph; secrets are not exposed; resulting workflow passes ordinary validation and runs'),
 ('BAK-01', 'backup', '/backup', 'Select sections; try short and mismatched passphrases; create a backup using native Save As; cancel another Save As', 'Validation blocks invalid submissions; saved nonempty encrypted archive can be previewed; UI does not claim completed storage on cancellation'),
 ('BAK-02', 'backup', '/backup', 'Choose the saved archive; try wrong passphrase and corrupt archive; preview valid archive; test skip, rename and overwrite on disposable conflicts; restore; sign in again', 'Invalid archives change nothing; preview counts match results; workflows, Globals, Custom Nodes, settings and permissions match the chosen policies'),
 ('RES-01', 'resilience', '/operations', 'With an unsaved editor open, interrupt and restore the lab API connection; navigate and recover; inspect connection status', 'Disconnected state is visible; no false save success; application recovers without duplicate writes'),
 ('RES-02', 'resilience', '/operations', 'Have the fixture helper stop only the isolated NodePilot database; observe banner and attempted action; restore it and retry', 'Database-outage banner and useful refusal appear; service heals; successful retry executes once'),
 ('SHELL-01', 'shell', '/workflows', 'Close the window to tray; open from tray; launch a second instance; open bundled docs and an external documentation link', 'One main instance; correct window restored; docs open separately; external link goes to system browser'),
 ('SHELL-02', 'shell', '/workflows', 'Open tray menu; inspect Quit NodePilot; use Restart backend; wait for recovery; Quit NodePilot; relaunch from Start menu', 'Tray text is correct; restart recovers; Quit exits the shell; relaunch preserves login and saved workflows'),
 ('BROWSER-01', 'browser', '/workflows', 'In Edge open server workflow by deep link; reload; use back/forward; open a second tab; edit and sign out in one tab', 'Routing is correct; server-backed writes persist; logout invalidates the other tab; no old-user data remains'),
 ('BROWSER-02', 'browser', '/backup', 'In Edge save single/all workflow exports and a backup; cancel a download; import the saved workflow through the file chooser', 'Real files are saved with expected bytes/structure; no navigation into raw API data; import succeeds'),
 ('SSO-01', 'authentication', '/login', 'Use a fresh Edge profile for Windows SSO; test allowed Admin/Viewer and denied lab directory accounts; sign out and reauthenticate', 'SSO succeeds without a credentials prompt; role/group mapping is correct; denied account cannot enter; Kerberos evidence supplements the visible login'),
 ('SSO-02', 'authentication', '/login', 'Sign in through LDAP and then Windows SSO with the same lab user; disable that disposable directory identity; retry login', 'Identity is not duplicated; disabled identity is refused; local break-glass login still works'),
 ('SSO-03', 'authentication', '/login', 'Use the lab OIDC provider; complete and cancel sign-in; exercise logout and denied identity; provision/update/deactivate a test identity through the SCIM fixture and inspect Users', 'Callback and session handling work; mapped role is correct; deactivation removes access; no token is exposed'),
]

# Each recipe names a safe positive result and a concrete negative input.
ACTIVITIES = {
 'runScript': ('Write-Output "CU-marker"; expose a declared return value', 'Use an unresolved global'),
 'fileOperation': ('Copy sandbox/source.txt to sandbox/copy.txt then read it', 'Leave the required path empty'),
 'folderOperation': ('Create and list a run-scoped subfolder', 'Use a path outside the allowed sandbox'),
 'fileHash': ('Hash the fixed source.txt with SHA256 and compare the fixture digest', 'Enter an invalid expected hash'),
 'zipOperation': ('Compress source.txt and extract into a new sandbox folder', 'Select a missing archive'),
 'serviceManagement': ('Read, stop and start only the disposable fixture service', 'Use a nonexistent fixture service name'),
 'scheduledTask': ('Create, inspect and delete only the run-prefixed fixture task', 'Leave task name empty'),
 'registryOperation': ('Write/read/delete a string below HKCU\\SOFTWARE\\NP-ComputerUse\\<run>', 'Leave required value name empty'),
 'wmiQuery': ('Query Win32_OperatingSystem.Caption on the fixture target', 'Enter invalid WQL'),
 'startProgram': ('Run powershell.exe -NoProfile -Command "Write-Output CU-marker"', 'Use a nonexistent executable'),
 'powerManagement': ('Query uptime of the fixture target; do not shut down a shared host', 'Leave the required target/action field invalid'),
 'waitForCondition': ('Wait for the fixture HTTP endpoint to return 200', 'Use a closed fixture port with a short timeout'),
 'restApi': ('GET the fixture JSON endpoint and check the run marker', 'Use a host not on the allow-list'),
 'sql': ('Use the lab connection reference and SELECT 42 AS answer', 'Use a nonexistent connection reference'),
 'xmlQuery': ('Read <root><value>42</value></root> with /root/value', 'Enter malformed XML'),
 'jsonQuery': ('Read {"value":42} with $.value', 'Enter malformed JSON'),
 'emailNotification': ('Send a run-tagged message only to the lab SMTP sink', 'Use an invalid recipient address'),
 'textFileEdit': ('Replace alpha with beta in sandbox/source.txt and read back', 'Use a missing input file'),
 'generateText': ('Generate a UUID and verify its displayed format', 'Use invalid input for a mode with required fields'),
 'llmQuery': ('Ask the lab model to return CU-marker and inspect provider/usage/output', 'Choose an unavailable profile'),
 'log': ('Log the exact marker Grüße äöü 日本語', 'Reference a nonexistent global'),
 'delay': ('Wait one second and then return CU-marker', 'Enter a negative duration'),
 'junction': ('Join two branches in wait-all mode and inspect both outputs', 'Publish a multi-input graph without a valid junction'),
 'startWorkflow': ('Call the run-scoped echo child with an input and inspect the returned marker', 'Choose a deleted child workflow'),
 'forEach': ('Iterate [1,2,3] through the echo child and inspect all results', 'Use invalid JSON instead of an array'),
 'decision': ('Compare 42 with 42 and inspect true/false branch routing', 'Leave a comparison operand invalid'),
 'returnData': ('Return a named Unicode field and verify its exact execution value', 'Reference a nonexistent upstream output'),
 'manualTrigger': ('Declare and supply a runMarker input using the Run dialog', 'Omit a required input'),
 'scheduleTrigger': ('Set a permitted one-minute Quartz schedule and wait for a genuine scheduled run', 'Enter an invalid cron expression'),
 'webhookTrigger': ('Configure a secret and field mapping; fixture sends a run-tagged request', 'Send a request with the wrong secret'),
 'fileWatcherTrigger': ('Watch the sandbox watch folder; fixture creates one marked file', 'Configure a missing or inaccessible watched folder'),
 'databaseTrigger': ('Watch the named sentinel connection; baseline then change the sentinel value', 'Use an unknown connection reference'),
 'eventLogTrigger': ('Watch the dedicated lab Application source; fixture writes a marked event', 'Use an invalid event filter or unavailable log'),
}

def cases():
    result = []
    def add(id, area, route, steps, expected, target='desktop', role='Admin', covers=None, prerequisites=None):
        result.append(dict(id=id, area=area, target=target, role=role, route=route,
            prerequisites=['installed-release', 'isolated-fixtures', 'native-session'] + (prerequisites or []),
            steps=steps, expected=expected, covers=covers or [],
            evidence=['screenshot', 'observation'],
            cleanup='Remove only run-prefixed disposable copies; restore changed values from the fixture journal. Keep shared run fixtures until dependent cases finish.'))
    for id, area, route, steps, expected in JOURNEYS:
        add(id, area, route, steps.split('; '), expected.split('; '),
            target='server' if id.startswith(('BROWSER-', 'SSO-')) else 'desktop',
            role='Admin/Operator/Viewer' if id in ('AUTH-03', 'AI-01') else 'Admin',
            covers=['route:' + route])
    for name, (positive, negative) in ACTIVITIES.items():
        add('ACT-' + name, 'activities', '/workflows/:id', [
            'Create a run-prefixed workflow and add ' + name + ' from the palette using Computer Use.',
            'Configure: ' + positive + '. Exercise each visible configuration mode; save/reopen its controls before reverting to this safe execution mode.',
            'Publish and execute through the UI, or have the fixture helper stimulate the configured external trigger. Inspect the real execution.',
            'Negative case: ' + negative + '. Observe validation or the expected failing step; correct and rerun.',
            'Disable automatic triggers after observing the correlated result.'],
            ['Configured values survive reopen.', positive + '.', 'Invalid input does not silently succeed; corrected case succeeds.'],
            covers=['activity:' + name], prerequisites={
                'llmQuery': ['llm'], 'emailNotification': ['smtp'], 'sql': ['sql'],
                'databaseTrigger': ['sql'], 'eventLogTrigger': ['eventlog'], 'restApi': ['http'],
                'waitForCondition': ['http'], 'webhookTrigger': ['http'],
                'serviceManagement': ['winrm'], 'scheduledTask': ['winrm'], 'wmiQuery': ['winrm'],
            }.get(name, []))
    for section in SETTINGS:
        add('SET-' + section, 'settings', '/settings', [
            'Open the system settings tab containing ' + section + '; capture all visible controls and their initial values in the protected fixture journal.',
            'Using the isolated lab values, exercise each visible editable control, connection test and secret mask; submit one invalid value and observe validation.',
            'Save a reversible valid change; reload and reopen the section; check live effect or restart-required indicator as specified by SettingsSchema.',
            'For restart-required settings, group changes for one controlled restart per compatible group, verify their effects, then restore the baseline.'],
            ['No lost controls or untranslated keys in DE/EN.', 'Invalid configuration is refused; valid configuration persists.', 'Secret masks and conflict handling preserve stored credentials.'],
            covers=['setting:' + section])
    for role in ('Operator', 'Viewer'):
        add('ROLE-' + role, 'permissions', '/settings', [
            'Sign in as ' + role + ' and visit every catalog route, including direct links to Admin-only pages.',
            'Attempt create/edit/run/export/share/delete in the permitted and unshared fixture folders.',
            'Compare controls and observed refusals with the controller authorization and folder capability contracts; sign out.'],
            ['Each allowed action completes.', 'Each denied action is hidden or refused, including direct navigation.', 'No previous-user data remains.'], role=role)
    for route in ROUTES:
        key = route.strip('/').replace('/', '-').replace(':', '') or 'dashboard'
        for lang in ('de', 'en'):
            add('VIS-' + key + '-' + lang, 'visual', route, [
                'Select ' + lang.upper() + ' through the UI and open this route (substitute a run workflow ID or visit every metrics section).',
                'Open every dialog, menu and tab reachable on this page; inspect populated, empty and validation-error states.',
                'Record the names of the inspected dialogs in the observation; cancel destructive dialogs here.'],
                ['No raw translation keys, wrong language, clipped actions or unreadable values.', 'Keyboard focus, scrolling and error messages are usable.'],
                covers=['route:' + route, 'locale:' + lang])
    for theme in THEMES:
        add('THEME-' + theme, 'visual', '/settings', [
            'Select ' + theme + ' in Appearance; inspect dashboard, populated workflow designer, script editor, execution details and one modal.',
            'Reload and inspect the stored appearance; for System exercise OS light/dark switching on the disposable session.'],
            ['Text, charts, focus and selection are readable; icons match the selected skin.', 'No overlapping or invisible primary controls; preference survives reload.'],
            covers=['theme:' + theme])
    for theme in ('light', 'dark'):
        add('WINDOW-' + theme, 'visual', '/workflows/:id', [
            'Use the ' + theme + ' theme and resize the window to 1024x768.',
            'Complete DES-01 and EXE-04, use a properties form and native Save As, then restore 1440x900.'],
            ['All primary actions and dialogs remain reachable with scrolling.', 'Graph editing and debugging produce the same result as at the standard size.'])
    for dependency in INTEGRATIONS:
        add('INT-' + dependency, 'integrations', '/settings', [
            'Use the real lab ' + dependency + ' fixture described in environment.json; confirm its run marker and endpoint.',
            'Configure its NodePilot consumer through the UI; perform the matching journey or Activity case; inspect delivery or remote output.',
            'Make the fixture refuse one request or use an invalid disposable credential; observe the UI error; restore and retry.'],
            ['A genuine end-to-end interaction succeeds and is correlated to this run.', 'The negative case is visible and recovery succeeds.'],
            target='server' if dependency in ('ldap', 'windows-sso', 'oidc', 'scim') else 'desktop',
            covers=['integration:' + dependency], prerequisites=[dependency])
    return result

PILOT = ['AUTH-01', 'AUTH-03', 'DES-01', 'INF-03', 'WF-03', 'BAK-01', 'BAK-02', 'SHELL-02', 'BROWSER-02']
