# Alerting

Alerting sends notifications about workflow events and system states. Without an active policy or an active rule, nothing is delivered.

The **Alerting** page has two areas:

| Area | Use |
|---|---|
| **System alerts** | Monitoring known system values such as backlog, reachability or credential expiry |
| **Custom rules** | Notification on selected workflow and operational events |

For new monitoring, system alerts are the simpler choice. Custom rules suit your own combinations of event type, scope and filters.

## Notification channels

A policy or rule can contain several channels.

| Channel | Target | Prerequisite |
|---|---|---|
| **Email** | A single recipient address | A configured SMTP connection |
| **Webhook** | An HTTP(S) endpoint | A reachable and permitted target address; HTTPS is recommended |

The SMTP connection is configured under **Settings → Integrations** with host, port, sender, TLS and optional credentials. A connection test is available there too.

A webhook receives a JSON message with the event type, severity, workflow, status, error and timestamp. An optional secret signs the message with HMAC-SHA256 in the `X-NodePilot-Signature` header.

## Quick setup

1. Open the appropriate area under **Alerting**.
2. Choose a system source or create a custom rule.
3. Set the condition, the scope and at least one channel.
4. Check your selection with **Preview** or **Check current values**.
5. Save and send a **test notification**.
6. Enable the policy or rule.

The preview sends no message. A test notification really does exercise the saved channels.

## System alerts

System alerts monitor measurements NodePilot provides. Several policies with different thresholds, recipients or scopes can be created for one source.

### Available sources

| Category | Source | Purpose |
|---|---|---|
| Execution | Execution result | Successful, failed or cancelled runs |
| Execution | Stuck execution | Executions running unusually long |
| Execution | Workflow health | The error rate and runtime trend of a workflow |
| Queue | Execution backlog | The sum of waiting and running executions |
| Queue | Queue depth | The number of waiting executions only |
| Queue | Cancellation rate | The number of cancelled executions in a time window |
| System state | Machine unreachable | A failed stored connection test |
| System state | Stale service heartbeat | A missing status from a background service |
| System state | Alert delivery failed | Repeated errors when sending email or webhooks |
| System state | Trigger not registered | A trigger that cannot become active, for example because a directory is unreachable |
| Schedule | Missed schedule | An expected scheduled start with no matching execution |
| Schedule | No recent workflow success | A scheduled workflow without a recent successful run |
| Credentials | Credential expiring | An upcoming or already reached expiry date |
| Security | Audit event | Audit-log entries such as failed logins, lockouts, break-glass sign-ins, role changes or credential deletions — filterable by code, outcome, user, IP and the details JSON |

A source can appear as **unavailable** if the required data is missing. Examples:

- Machines with no connection test yet are not evaluated as unreachable.
- Credentials without a maintained expiry date are not monitored.
- Workflow-related sources need existing execution or schedule data.
- "Trigger not registered" is only available while a trigger is actually affected. In high-availability operation, only the active node knows that state; on the passive node the source appears as unavailable even though the active node alerts correctly.

### Configuring a policy

| Setting | Meaning |
|---|---|
| **Template** | Fills in a sensible starting configuration |
| **Condition** | Determines the value at which an alert fires |
| **Source parameters** | Determine, for example, the time window considered |
| **Time until alert** | The condition has to hold continuously for this long |
| **Severity** | `Info`, `Warning` or `Critical` |
| **Scope** | Global, folders or individual workflows; depends on the source |
| **Cooldown** | The minimum gap between repeated messages |
| **Routes** | Email and webhook destinations |

**Check current values** shows which existing values currently satisfy the policy. A template only fills in the editor; it does not enable the policy automatically.

## Custom rules

Custom rules react to events. A rule consists of event types, optional filters, a scope and at least one channel.

### Event types

| Group | Events |
|---|---|
| Executions | Failed, succeeded, cancelled, running long, waiting long |
| Credentials | Credential error, credential expiring |
| Operations | Stale service, machine unreachable, high backlog, high pending backlog, high cancellation rate |
| Schedules | Missed schedule, no recent workflow success |
| System | System alert |

For a manual cancellation, the **Cancelled by** field can be filtered. The value `user` limits the rule to executions cancelled individually by a person.

### Configuring a rule

| Setting | Meaning |
|---|---|
| **Event types** | The events the rule reacts to |
| **Scope** | All workflows, selected folders or selected workflows |
| **Filters** | Additional conditions, for example status, workflow name, duration or target machine |
| **Group by** | Groups similar events together for repetition control |
| **Channels** | Email or webhook destinations |
| **Channel condition** | Sends a particular channel only when an additional condition matches |
| **Cooldown** | Prevents the same message repeating too often |
| **Minimum occurrences and time window** | Alerts only once an event occurs several times within the window |

An empty filter admits every selected event within the defined scope. An empty grouping uses the event's default grouping.

Example: a rule for **execution failed** can apply globally, send email only on `Critical`, and fire a webhook exclusively for one folder.

## Preview and testing

The three checks serve different purposes:

| Check | Result |
|---|---|
| **Preview** | Checks the rule, filters, grouping and channel conditions against a sample event |
| **Check current values** | Evaluates a system policy against currently available measurements |
| **Test notification** | Sends a real message to all saved channels |

A new configuration should first be saved disabled, tested, and then enabled.

## Delivery history

The **Deliveries** action opens the history of delivery attempts. It shows:

- The time and the rule
- The channel and the destination
- The status `Pending`, `Sent` or `Failed`
- The attempt number
- The error message

Failed deliveries are retried and marked as failed after five unsuccessful attempts. The retention period follows the notification retention; by default, completed entries are kept for 90 days.

## Permissions and security

- Admins and operators may read rules and deliveries.
- Only admins may create, change, delete, test or enable policies and rules.
- Webhook secrets are stored encrypted and are not shown again.
- Webhook destinations are subject to the configured rules for outbound connections.
- Changes and test firings are recorded in the audit log.
- The preview ("check current values") of the **Audit event** source is admin-only because it returns audit rows — the same boundary as the audit log itself.

Alerting currently sends no automatic all-clear when a state returns to normal.
