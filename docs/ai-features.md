# NodePilot AI Features

NodePilot supports creating scripts and workflows and analyzing existing flows. AI is
disabled by default and requires an active LLM profile. In the development profile,
`Llm:Enabled` is already switched on.

## Features

| Feature | Usage |
|---|---|
| Script generation | In the script editor, generate PowerShell from a description and insert it at the cursor or replace the script. Sending the existing script requires explicit approval on each call. |
| Workflow generation | On the workflow overview, describe a flow, review the preview, and create a workflow from it. |
| Workflow assistant | In the designer, have the open workflow explained, investigate errors, and get changes proposed. Proposals can be applied, refined, or undone individually. |
| Knowledge assistant | Under `/ai-chat` or in the chat widget, ask questions about documentation, configuration, and operations. In the designer, the workflow assistant is available instead. |
| `llmQuery` | Run a model request inside a workflow. |
| `aiAgent` and `aiAgentTeam` | Have tasks handled with selected tools. Setup and limits are described under [AI Agents](ai-agents.md). |

All roles may use the workflow assistant for explanations. Generation and applying
proposals are reserved for admins and operators. Changes first land in the canvas and
then go through the usual flow of edit lock, save, and publish. Stale proposals are blocked.

## Configuration

Profiles are created and tested under **Settings / System / Integrations / LLM**.
`Llm:Enabled=true` enables the features, `Llm:ActiveProfileId` selects the profile in use.
If this selection is missing or points to no existing profile, AI responds with
`503 LLM_NO_ACTIVE_PROFILE`. The service still starts.

Connections live under `Llm:Profiles:<id>`. Fresh installations contain no profiles.
Profiles created in the UI remain fully manageable, while profiles from the base
configuration can be edited there but not deleted.

| Profile key | Default | Meaning |
|---|---|---|
| `Name` | Empty | Display name. The profile ID is kept when renamed. |
| `BaseUrl` | OpenAI cloud | Address of the OpenAI-compatible endpoint. |
| `ApiKey` | `null` | Access key, if the provider requires one. |
| `Model` | `gpt-5.5` | Model for generation and chats. |
| `MaxTokens` | `128000` | Output limit, valid from `256` to `1000000`. It must fit the model and does not denote its context window. |
| `TimeoutSeconds` | `90` | Time budget for the model response. Connection setup has its own deadlines. |
| `EnableToolCalling` | `false` | Allows the chats to use read-only tools. The model must support function calling. |
| `ToolCallMaxDepth` | `6` | At most 1 to 10 model rounds per chat turn. The last round returns a text answer without further tools. |
| `ShareScriptsWithAssistant` | `true` | Releases scripts, SQL, and program arguments to the workflow assistant. |

API keys can be provided via `Llm__Profiles__<id>__ApiKey`.
Plaintext keys in settings files trigger a hardening warning at startup.
Profile and proxy changes in the admin UI take effect on the next call.

### Endpoint and Connection Format

`BaseUrl` requires HTTPS. HTTP is only allowed for literal loopback targets, which bypass the
outbound proxy. Examples of local endpoints are
`http://localhost:11434/v1` and `http://localhost:1234/v1`.

| End of `BaseUrl` | Usage |
|---|---|
| `/responses` | Responses requests to exactly this URL. |
| `/chat/completions` | Chat Completions requests to exactly this URL. |
| Any other path, such as `/v1` | Chat Completions with `/chat/completions` appended. |

URLs with a query string are not supported. All profile addresses are checked for blocked
metadata targets on save and at startup. For Responses requests,
NodePilot sets `store: false`.

### Proxy

`Llm:Proxy` applies installation-wide to the LLM calls. `Mode` selects `Off` as the
default, `System` for the proxy configuration of the service account, or `Custom`.
`Custom` requires `Address`. `BypassList` takes host names and shell globs,
while with `System` the exceptions of the operating system apply.

For authentication, `Username` and `Password` or `UseDefaultCredentials=true`
with the Windows identity of the service account are available. The password can be set via
`Llm__Proxy__Password`. Changes to the Windows proxy settings
themselves require a service restart. A proxy does not replace a missing network approval.

## Data Sharing and Permissions

The workflow assistant masks named secrets before the model call. With
`ShareScriptsWithAssistant` enabled, however, the provider receives scripts, SQL, and arguments
to the extent that secret detection does not mask them. This detection is heuristic.
Changing the profile, its endpoint or model, or the sharing switch excludes the previous
chat history from the next call. The global AI chat binds its history the same way.

Script generation sends the current script only with the separate approval
`IncludeCurrentScript=true`. It receives upstream variables as a schema without their values.
Generated scripts are shown in the editor before they are run. Proposals of the
workflow assistant keep existing secrets and layouts when merged.
Changes to masked conditions may have to be made manually.

AI endpoints are limited to 20 requests per minute and IP. Audit entries record
the respective action and metadata, but no prompt text. Further trust boundaries
are described in the [Threat Model](threat-model.md).

## Read-only Tools and Knowledge Sources

With `EnableToolCalling=true`, the workflow assistant can analyze the flow,
look up activity types, and read execution details for troubleshooting.
Execution history is only available for saved workflows with the corresponding
folder read permission. Results are masked and limited in size.

The global knowledge assistant additionally requires `AiKnowledge:Enabled`.
The sources are managed in the `AiKnowledge` section:

| Source | Setting | Access |
|---|---|---|
| Documentation | `DocsEnabled`, on by default | Documentation search. |
| Workflows and operations | `OperationalEnabled`, on by default | Within the folder permissions. |
| Source code | `SourceCodeEnabled`, off by default | Admin and Operator. |
| Database / text2sql | `DbEnabled`, off by default | Global admins only, plus `EnableToolCalling=true`. |
| System configuration | Available to authorized roles | Admin and Operator. |

With text2sql, the model translates a question into SQL, which NodePilot checks as read-only and
runs. Protected columns are blocked, results are masked and limited to 200 rows.
Inventory lists of workflows, runs, or machines also use this
DB source. Folder permissions do not extend access to SQL. The knowledge assistant
does not change workflows and does not start repairs.

In read mode, the shared SQL guard for the knowledge assistant and the admin console explicitly rejects
PostgreSQL escape strings (`E'…'`/`e'…'`) before execution.
Standard SQL literals with doubled apostrophes remain supported. The differing
[PostgreSQL escape syntax](https://www.postgresql.org/docs/current/sql-syntax-lexical.html#SQL-SYNTAX-STRINGS-ESCAPE)
is not interpreted by the lexer in use.

## Error Handling

| HTTP | Code | Cause |
|---|---|---|
| 503 | `LLM_DISABLED` | AI disabled. |
| 503 | `LLM_NO_ACTIVE_PROFILE` | No valid active profile. |
| 503 | `LLM_UNREACHABLE` | Endpoint unreachable. |
| 503 | `LLM_TIMEOUT` | Time budget of the model response exceeded. |
| 503 | `LLM_UNAUTHORIZED` | Access key missing or rejected. |
| 503 | `LLM_RATE_LIMITED` | Provider throttles requests. |
| 502 | `LLM_MALFORMED_RESPONSE` | Unusable response format. |
| 502 | `LLM_UPSTREAM_ERROR` | Error of the provider or the transport. |
| 429 | No dedicated code | Local request limit reached. |

Connection errors name the affected stage. DNS and TCP each have 15 seconds,
the TLS handshake 30 seconds. `TimeoutSeconds` applies to the model response
that follows. Certificates are checked against the machine trust store, so
an internal CA must be present on the NodePilot host under `LocalMachine\Root`.
`NodePilot.Ai.LlmConnect` logs details on addresses and connection duration at `Debug`.
