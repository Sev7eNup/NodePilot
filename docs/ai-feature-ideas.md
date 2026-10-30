# AI Feature Ideas

> **Status:** Depth of description for the AI ideas — user problem, proposed feature, suitable
> place, security boundaries. This document is not a binding implementation specification and
> does not fix any concrete interfaces.
>
> **Prioritization and status are stated exclusively in [`roadmap.md`](roadmap.md)** — ideas 1, 2 and the
> AI chat run card are committed there, the rest is trigger-gated. If this document and the roadmap
> contradict each other, the roadmap prevails.

NodePilot already has script and workflow generation, the workflow assistant, the global
knowledge chat, and the `llmQuery` activity. The greatest additional value therefore comes from
contextual AI actions in existing workflows rather than from yet another general
chat.

## 1. Execution Incident Copilot

- **User problem:** When a workflow execution fails, users currently have to combine ErrorOutput,
  activity history, and workflow definition themselves.
- **Proposed feature:** An action "Analyze error with AI" explains the observed
  errors, separates evidence from suspected causes, and names concrete next checks. From there
  the user can open the workflow in the designer and prepare a repair.
- **Suitable place:** Failed entries in the execution list, the Operations view, and
  the History tab of the designer.
- **Security boundaries:** Read-only diagnosis for users with folder read permission; only
  redacted execution and activity data; no automatic change, no retry, and no
  publish. Suspicions must be marked as such and traced back to visible evidence.

## 2. AI Repair for Lint and Publish

- **User problem:** Lint and pre-publish findings show a problem and its location, but do not
  help with the concrete repair.
- **Proposed feature:** "Fix with AI" hands selected deterministic findings to
  the workflow assistant. It produces a structured change proposal, which the user
  reviews as a diff and applies in full or selectively.
- **Suitable place:** Lint panel and pre-publish checklist in the workflow designer.
- **Security boundaries:** Findings are validated server-side and deterministically and are not taken
  on the model's word alone. Applying requires admin/operator permissions and the user's own
  edit lock; stale-hash protection, secret merge, and the normal save/publish flow remain binding.

## 3. Activity Configuration Copilot

- **User problem:** Complex activity configurations such as decision trees, cron expressions, REST,
  SQL, JSONPath/XPath, mappings, and retry rules are error-prone and require detailed knowledge.
- **Proposed feature:** "Configure with AI" translates a natural-language intent
  into a typed configuration draft and allows applying individual fields.
- **Suitable place:** Properties panel of the respective activity, starting with the most complex
  configurations.
- **Security boundaries:** The draft must pass through the existing deterministic validation and
  preview. Secret values are never transmitted; the activity is not run automatically,
  and a step test remains a separate, deliberate action.

## 4. Workflow Generator v2

- **User problem:** The existing workflow generation is a one-shot flow with statistics and
  JSON preview; ambiguities can only be resolved before creation by starting over.
- **Proposed feature:** A guided drafting process asks follow-up questions, shows a real
  graph preview, runs lint, marks unresolved references, and allows iterative
  refinements.
- **Suitable place:** Existing "Generate with AI" dialog on the workflow overview.
- **Security boundaries:** The generated workflow stays disabled. Folder write permissions are checked
  before creation; secret values are excluded, and lint errors must be
  fixed before publish.

## 5. Reliability and Optimization Review

- **User problem:** Coverage, critical path, runtimes, retries, and failure heatmap are each
  visible on their own, but have to be combined manually for an optimization.
- **Proposed feature:** "Optimize workflow" identifies evidenced bottlenecks and proposes
  changes to parallelization, timeouts, retry behavior, and error paths.
- **Suitable place:** Preset in the workflow assistant and a contextual action on conspicuous
  workflows in the dashboard.
- **Security boundaries:** Every recommendation shows the data window, sample size, and underlying
  telemetry. Small samples are flagged; changes use only the existing
  proposal flow and are never published automatically.

## 6. Custom Activity Copilot

- **User problem:** Creating a custom activity requires PowerShell, contract, and
  NodePilot knowledge at the same time.
- **Proposed feature:** A description yields a draft with name, key,
  inputs/outputs, PowerShell template, timeout, success codes, and suggested test scenarios.
- **Suitable place:** Creation dialog of the custom activities.
- **Security boundaries:** New definitions stay disabled drafts. Only admins can
  enable them; secrets are used as references instead of values, and neither test nor
  activation is started automatically.

## 7. AI Test Case and Mock Generator

- **User problem:** Creating meaningful positive, negative, and edge-case inputs for a workflow
  or a single activity is time-consuming.
- **Proposed feature:** From the workflow contract and activity configuration, test case drafts
  are produced with inputs, mock values, expected paths, and expected outputs. A draft
  can be carried over into the step test or the manual execute dialog.
- **Suitable place:** Step test panel, manual execute dialog, and optionally a test preset in the
  workflow assistant.
- **Security boundaries:** Generating and running remain strictly separate, because activities may have real
  side effects. Historical values are used only in redacted form; destructive
  activities get a clear warning and are never started automatically.

## 8. Contextual Operator Copilot and Shift Briefing

- **User problem:** The global knowledge chat can answer operational questions, but when switching
  from a specialist view it does not automatically know the currently selected workflows, executions,
  machines, or alerts.
- **Proposed feature:** "Ask about this view" opens a contextual assistant.
  In addition, an on-demand shift briefing summarizes new failures, shared target systems,
  endangered schedules, and current alerts with links to the evidence.
- **Suitable place:** Dashboard, Operations, Machines, and Alerts.
- **Security boundaries:** Read-only, folder-RBAC-scoped, and only with explicitly selected IDs and
  filters instead of complete page dumps. Correlations are not presented as proven
  causes; mutations stay outside this feature.

## Shared Guardrails

- AI stays opt-in and disabled by default.
- Existing role, folder-RBAC, rate-limit, SSRF, audit, and secret-redaction rules apply
  unchanged.
- Deterministic analysis and validation supply facts; the model explains, prioritizes, and
  words proposals.
- Write effects occur exclusively through visible, confirmed proposals and the
  existing edit-lock, save, and publish flows.
- Prompt and response contents are not carried over unchecked into audit logs or other long-lived
  storage.
