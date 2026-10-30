# Dependencies

Under **Monitoring → Dependencies** you can examine stored relationships between
workflows, machines, credentials, global variables, and custom activities.
The object pages also offer the entry point **Show dependencies**.

1. Search for and select an object by name, folder, hostname, or ID. The list filters as you
   type. Each row states by how many objects the object is used directly and indirectly,
   and the number of its problems. **Usage** sorts by direct usage.
   **With problems** shows objects with unresolvable, ambiguous, or unevaluable
   references. **No detected use** shows machines, credentials, globals, and custom
   activities that nobody statically references in the visible scope. Workflows are
   excluded there because they start via triggers. An entry is no proof that deleting is safe.
2. **Used by** shows consumers and callers; **Uses** shows the resources used.
3. Open a result: **Why affected?** explains up to three paths. **Reference locations** names
   the configured step and, where needed, the technical field.
4. **Open step in designer** centers and highlights the stored step. The entry point
   does not request an edit lock. If the step has been removed in the meantime, a notice appears.
   **Back to Dependencies** and browser back return to the original entry:
   search term, type filter, direction, selection, and details are preserved. Scroll position as well as
   map section and expanded map objects are remembered for the current session.
5. The **Map** shows both directions at once: on the left, who uses the object, on the right, what it
   uses. Initially the direct relationships appear; **+N** on an object expands the
   next level through that object, the path to the current selection is already open.
   A click on an object on the other side switches the direction of the overview. At most 40
   map objects; on opening, it zooms out only as far as needed to keep the labels readable.

The overview distinguishes direct relationships, indirect usage, and possible agent calls.
Distinct objects are counted, not the number of steps. An object
reachable via several paths is counted once. Agent `workflow_run` grants mean **may call**;
they are no proof of an actual call. Disabled definitions and steps remain included
with a marker, because the view describes the stored state.

## Detection Scope

- `startWorkflow` and `forEach`: static workflow ID or name. Exact case wins,
  otherwise the name must be unique when case is ignored.
- Remote activities: registered target machine, explicit credentials, and effective
  machine default. An explicit credential replaces the default; agent service identity
  does not produce credential usage.
- Global variables: `{{globals.NAME}}` in known configuration fields, edge conditions,
  custom activity templates, and their input default values. Overridden default values are
  not counted as indirect usage by the calling workflows.
- Dynamic targets, free hostnames, local execution, invalid or unresolvable
  references appear as notes. They do not produce invented resources.

Arbitrary script accesses, dynamically composed targets, external systems, agent skills, and
MCP servers are not fully captured as resources. A relationship does not predict an outage.
The analysis is limited to 12 levels and 20,000 examined relationships per request;
when the limit is reached, counters are explicitly lower bounds. Reference locations belong to the
up to three explanatory paths, not to an unbounded listing of all possible paths.

## Permissions and Refresh

Workflow folder permissions are applied before the analysis. Names, counters, paths, and reference locations
contain visible objects only. Credentials and global variables are, as on
their object pages, available to Admin and Operator only. No secret values,
passwords, or script texts are returned to the client.

**Refresh** reads the current stored state. Cursors are bound to state, search scope,
and direction; changes while paging lead to `409 DEPENDENCIES_CHANGED`.
Reference locations with a differing analysis revision are not mixed with the old report.
The analysis needs no migration, background indexing, or additional graph library.
The browser demo mode uses the same UI with relationships from its mutable
in-memory world; it performs no real remote accesses.

## API, CLI, and MCP

All three API endpoints are authenticated and read-only. `kind` is `workflow`, `machine`,
`credential`, `variable`, or `customActivity`; `direction` is `uses` or `usedBy` (default).
`limit` defaults to 50, maximum 100; follow-up pages use `nextCursor`.

| REST | CLI | MCP |
|---|---|---|
| `GET /api/dependencies/objects?q=&kind=&state=&sort=&cursor=&limit=` | `np dependencies list --query TEXT --kind machine --state unused --sort usage` | `list_dependency_objects` |
| `GET /api/dependencies/{kind}/{id}?direction=&cursor=&limit=` | `np dependencies show machine ID --direction usedBy` | `get_dependencies` |
| `GET /api/dependencies/{kind}/{id}/evidence?relatedKind=&relatedId=&direction=&cursor=&limit=` | `np dependencies evidence machine ID workflow WORKFLOW_ID` | `get_dependency_evidence` |

The object list returns `usedByDirect`, `usedByIndirect`, and `problems` per object. `state` is
`problems` or `unused`, `sort` is `name` (default) or `usage`. Indirect usage
is computed only for the delivered page; sorting by usage uses the direct count.
CLI commands support `--cursor`, `--limit`, and the usual global output options.
MCP returns the same report and the same RBAC-filtered data as the API.

Draft and technical decisions: [Feature plan](dependencies-plan.md).
