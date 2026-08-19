# AI features

NodePilot integrates an OpenAI-compatible language model. Cloud services and local endpoints such as Ollama, LM Studio, vLLM, LocalAI or llama.cpp are supported.

The AI features are disabled by default. The corresponding buttons (script editor, workflow designer, AI chat) only appear once an LLM profile has been configured and enabled in the admin settings. Generated content is never published or executed automatically.

## Where they are used

| Area | Purpose | Can it produce changes? |
|---|---|---|
| **Script editor** | Create or revise PowerShell for a `runScript` activity | Yes, after you accept it into the editor manually |
| **Workflow designer** | Create new workflows, and explain, review and change the open one | Yes, after review and confirmation |
| **AI chat** | Answer questions about NodePilot, the documentation and permitted operational data | No, read-only |

## Script editor

**Where:** open a `runScript` activity, maximize the script editor and start the AI feature through the sparkles icon. The button is only available to the Admin and Operator roles.

**Suitable for:**

- A new PowerShell script from a short task description
- Extending an existing script
- Fixing errors or simplifying
- Adapting to the available workflow variables

An existing script and the available variables are taken into account as context. The result can be inserted at the cursor or accepted as a complete replacement.

Accepting it only changes the content in the editor. Before saving and running, a review of the commands, paths, permissions and variables used is required.

## Workflow designer

Two features are available in the workflow area.

### Generating a new workflow

**Where:** the workflow overview, the **Generate with AI** action.

From a description, NodePilot creates a complete workflow draft with triggers, activities and connections. Before it is created, a preview, the generated definition and the number of nodes and edges are shown. Only your confirmation creates the workflow.

Suitable for:

- A first executable draft
- Typical linear or branching processes
- A starting point for further editing in the designer

Machines, credentials, target paths and business conditions have to be reviewed and completed afterwards.

Generation and the assistant know every activity type — including the `llmQuery` activity, loops and branches. They also know the custom nodes enabled on this installation and suggest them, instead of rebuilding their function out of individual script steps.

### Editing the open workflow

**Where:** the workflow designer, the **AI assistant** button.

The assistant knows the currently open workflow. Possible tasks are:

- Explaining the structure and flow
- Describing possible failure points
- Analysing the execution history and failed steps
- Suggesting error handling or additional steps
- Including selected nodes deliberately through `@` or the current canvas selection
- Tidying up the layout

Changes appear as a proposal first. Individual nodes and edges can be selected, accepted or discarded. An accepted change can be undone immediately.

If the canvas changes after a proposal was created, the stale proposal is no longer applied. That protects edits made in the meantime.

The workflow-scoped chat supports several named threads, regeneration, Markdown export and a view of the previous AI activity.

The chat and the properties panel share the right-hand area of the designer: the open assistant overlays the properties. As soon as a node or a connection is clicked on the canvas, the chat steps back and the matching properties reappear. A multiple selection leaves the chat in place — it is the chat's selection context.

## The global AI chat

**Where:** the navigation, the **AI Chat** page.

The global AI chat is not tied to an open workflow. It serves as a read-only assistant for questions such as:

- Setting up a trigger or a deployment
- Explaining existing workflows
- Finding failed or scheduled executions
- Information about machines and operations
- Questions about the source code, if that source is enabled

The chat cannot propose or apply workflow changes. Answers depend on the knowledge sources enabled administratively:

| Knowledge source | Content | Access |
|---|---|---|
| **Documentation** | The contents of the NodePilot documentation | All authenticated roles |
| **Workflows and operations** | The workflow definition and static analysis (Admin and Operator, additionally folder RBAC); scheduled execution times for all roles | See the content column |
| **Source code** | The bundled NodePilot source code | Admin and Operator |
| **Database** | Read-only questions about operational data | Global admins only |

The database source runs read-only queries exclusively. Write operations are blocked. Protected columns and detected secrets are not passed to the model.

The starter suggestions on the empty chat page depend on the available knowledge sources. If the database source is available, operational analyses are suggested — the most recent failed runs, stuck executions or unreachable machines, for example. Otherwise, questions about the documentation and the schedule appear, which can be answered without that source.

## Choosing between them

| Task | The right feature |
|---|---|
| Create PowerShell for a single step | The script editor |
| Create a new workflow from a description | AI generation in the workflow overview |
| Explain or change the current workflow | The AI assistant in the workflow designer |
| Ask general questions about NodePilot or operations | The global AI chat |
| Call a model during a workflow run | The `llmQuery` activity |

## The `llmQuery` activity

`llmQuery` is not an authoring aid but an activity inside a workflow. During execution the activity sends a prompt to the configured model and passes the answer text on to subsequent steps.

Configurable, among other things:

- The prompt and an optional system prompt
- The model and the endpoint
- The maximum answer length and the temperature
- Text or JSON output
- The timeout

By default the activity uses the active LLM profile. Different settings can be set on the node. `Llm:Enabled=false` disables this activity too, as does a missing active profile.

Further fields and outputs are in the [`llmQuery` reference](activities-reference).

## Permissions and security

- Script and workflow generation require the Admin or Operator role.
- Read-only questions in the workflow-scoped and global chat are possible for authenticated roles.
- Workflow proposals may only be applied with edit permission and an active edit lock.
- Source-code knowledge in the global chat is limited to Admin and Operator; database knowledge with raw SQL is available to global admins only. Folder permissions do not elevate an operator into that capability. Workflow definitions and static workflow analyses remain reserved for Admin and Operator with folder RBAC; a viewer receives only the scheduled execution times from the operational source.
- Folder RBAC limits access to workflow data.
- Secrets are redacted before model requests.
- Generated scripts and workflow changes always require a domain review.
- AI actions and applied proposals are recorded in the audit log.

## Configuring the LLM

NodePilot stores any number of **LLM profiles**. A profile describes exactly one connection — endpoint, model, key and limits. Exactly one profile is active and is used by all AI features; switching between profiles is a save operation in the settings and does not require re-entering the connection details.

```json
{
  "Llm": {
    "Enabled": false,
    "ActiveProfileId": "openai",
    "Profiles": {
      "openai": {
        "Name": "OpenAI Cloud",
        "BaseUrl": "https://api.openai.com/v1",
        "ApiKey": null,
        "Model": "gpt-4o-mini",
        "MaxTokens": 4096,
        "TimeoutSeconds": 90,
        "EnableToolCalling": false,
        "ToolCallMaxDepth": 6
      }
    }
  }
}
```

| Setting | Meaning |
|---|---|
| `Enabled` | Enables or disables every AI feature |
| `ActiveProfileId` | The ID of the profile all AI features use; it has to point at an existing profile |
| `Profiles` | The stored connections, keyed by an immutable profile ID |

Per profile:

| Setting | Meaning |
|---|---|
| `Name` | The display name; freely changeable, the ID stays |
| `BaseUrl` | The HTTPS address of an OpenAI-compatible endpoint; HTTP is only permitted for exact loopback targets (`localhost`, `127.0.0.0/8`, `::1`). The path determines the request format (see below). |
| `ApiKey` | The API key; often not required for local models |
| `Model` | The model name to use |
| `MaxTokens` | The maximum length of a model answer (256 to 1,000,000) |
| `TimeoutSeconds` | How long the model may take for its answer — not the wait for the connection, which has its own short limits |
| `EnableToolCalling` | Allows the chats to use the enabled read-only analysis and knowledge sources |
| `ToolCallMaxDepth` | The maximum number of consecutive tool calls per question |

### Outbound proxy

In corporate networks, outbound traffic is often only permitted through a proxy. The settings for
that are under `Llm:Proxy` and apply to **all** AI calls — both chats, script and workflow
generation, the `llmQuery` activity and the connection test in the settings. It is deliberately one
block for the whole installation rather than one per profile: the mixed case — a cloud model through
the proxy, a local model directly — is expressed through the bypass list.

A proxy is the answer to "outbound traffic **may** only leave this network through the proxy" — not
to "the endpoint is unreachable". If the endpoint is in a different network segment and the firewall
has not been opened, a proxy changes nothing. So check first which stage fails: the section
[When the endpoint is unreachable](#when-the-endpoint-is-unreachable) answers that in seconds.

| Setting | Meaning |
|---|---|
| `Mode` | `Off` connects directly (the default), `System` adopts the service account's proxy including its bypass rules, `Custom` uses the address below |
| `Address` | The proxy's address, e.g. `http://proxy.corp.local:8080`; required with `Custom` |
| `BypassList` | Hosts reached directly; wildcards are allowed, such as `localhost` or `*.corp.local` |
| `Username` | A user name for proxies with basic authentication |
| `Password` | The matching password; better set through the environment variable `Llm__Proxy__Password` |
| `UseDefaultCredentials` | Authenticates to the proxy with the service account's Windows credentials — the normal case with domain-integrated proxies |

Loopback targets (`localhost`, `127.0.0.0/8`, `::1`) are always connected directly regardless of
`Mode` and `BypassList`, so that the prompt and API key never leave the host unencrypted.

Note: if the traffic goes through a proxy, the proxy resolves the target address. The additional
check NodePilot otherwise performs immediately before the connection is established then only applies
to the proxy itself; the base URL is still checked when saving and at startup.

Proxy changes take effect without a service restart. The only exception is `System`: changes to the
Windows proxy settings themselves are only picked up after the service is restarted.

### When the endpoint is unreachable

Reaching the endpoint and waiting for an answer are two separate things with separate limits.
`TimeoutSeconds` applies only to the model's answer; establishing the connection fails independently
of it after a few seconds. A generous time limit for a slow model therefore does not mean waiting
minutes for an unreachable endpoint.

The error message names the stage at which it failed:

| The message starts with | Meaning |
|---|---|
| `LLM endpoint DNS:` | The name could not be resolved — a wrong name, a wrong suffix, or the name service is not answering. |
| `LLM endpoint TCP:` | The machine was not reachable. *Refused* means: the host answers, but nothing is listening on that port. *No answer* almost always means a firewall or a network segment. |
| `LLM endpoint TLS:` | The connection was established but encryption did not come about — often a required client certificate, or a certificate the server does not trust. |
| `accepted the request but sent no answer` | Everything is fine, the model simply took too long. Here a higher `TimeoutSeconds` is the right answer. |

One certificate note that often costs time: NodePilot validates against the **machine's** certificate
store, not the signed-in user's. An internal certificate that the browser on a workstation accepts
has to be in the computer's trusted root certification authorities on the NodePilot server.

### The request format (derived from the base URL)

OpenAI operates two request formats side by side: the classic **chat completions** and the newer
**Responses API**. Some models are reachable only through the Responses API. NodePilot supports both
and recognizes from the base URL's path which one is meant — a separate switch for it is deliberately
unnecessary:

| The base URL ends in | Format used | Address called |
|---|---|---|
| `/responses` | Responses API | Exactly that address |
| `/chat/completions` | Chat completions | Exactly that address |
| Anything else (e.g. `…/v1`) | Chat completions | The base URL + `/chat/completions` |

The detection ignores case and trailing slashes. Local runtimes such as Ollama, LM Studio, vLLM, LocalAI or llama.cpp understand chat completions only.

With the Responses API, NodePilot always sends the instruction **not** to store the request at the
provider. Without that instruction OpenAI would retain every request there for 30 days by default,
whereas chat completions stores nothing.

Profiles are best created under **Settings → System → Integrations → LLM**. Profiles created there can be managed completely. A profile that is additionally defined in a base configuration file or in environment variables can be edited in the interface but not deleted — it would reappear the next time the configuration is reloaded. Such profiles are marked accordingly in the interface.

The API key should be set through the environment variable `Llm__Profiles__<id>__ApiKey` or a secret provider. A plaintext value in the configuration file produces a security warning.

Tool calling is a property of the model and is therefore configured per profile. It requires a model with reliable function-calling support and is necessary for the global AI chat to query the enabled knowledge sources.

The LLM connection can be tested per profile in the administrative settings. Changes to the `Llm` section take effect without a service restart. If AI is enabled but no profile is selected, all AI endpoints answer with `503 LLM_NO_ACTIVE_PROFILE`; the service still starts normally.

## Configuring the global AI chat

The global AI chat has its own switch and its own knowledge sources:

```json
{
  "AiKnowledge": {
    "Enabled": false,
    "DocsEnabled": true,
    "OperationalEnabled": true,
    "SourceCodeEnabled": false,
    "DbEnabled": false
  }
}
```

For a working global AI chat, the following settings have to be active:

```text
Llm:Enabled = true
Llm:ActiveProfileId = <the ID of an existing profile>
Llm:Profiles:<id>:EnableToolCalling = true
AiKnowledge:Enabled = true
```

The individual sources can be enabled independently under **Settings → AI knowledge**. Documentation as well as workflows and operations are intended as sources by default. Source code and database are disabled by default for security reasons.

Optionally, your own root directories for documentation and source code can be set, as well as limits for file size and the number of hits. Without your own paths, NodePilot uses the knowledge directories shipped with the installation.
