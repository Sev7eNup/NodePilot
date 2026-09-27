# What a SCOrch runbook must retain when it moves

On paper, the runbook contains four activities: find servers, query a service, restart it if necessary, and report the result. Rebuilding it appears straightforward. Looking at the data reveals that “find servers” publishes several results and that subsequent steps execute for each match. Converting those results into a single list has already changed the process.

The migration example below is fictional. It describes an assessment that should precede production adoption, rather than a customer deployment that actually took place.

## The contract between activities

In this example, the search returns a computer name and service name for each result. The service query adds the current state. A conditional link leads to a restart only when the service is stopped. Another path records query failures without turning them into restart requests.

SCOrch makes information available to subsequent activities through Published Data. The conditions available on a link also depend on the data type of the published values. Comparing visible labels alone is therefore insufficient during migration. [Microsoft: Control runbook activities](https://learn.microsoft.com/en-us/system-center/orchestrator/control-runbook-activities).

Before transferring the runbook, its operational contract can be recorded briefly:

| Handoff | Expectation in this example | Check after migration |
|---|---|---|
| Search result | One record per selected server and service | Preserve the count and association |
| Service state | An unambiguous value for running or stopped | The condition uses the actual destination value |
| Query failure | A separate failure path | Missing output does not trigger a restart |
| Completion report | A result per target, including the action taken | No blanket success for partial results |

Cardinality deserves particular attention. No match, one match, and several matches are different input situations. A runbook may also aggregate data before passing it on. That aggregation must either be deliberately retained or replaced through an explicitly approved change.

## Assess the import with counterexamples

A successful trial against one reachable server exercises only the simplest branch. This example also needs an already running service, a stopped service, and a denied query. The expected behavior is established before testing: access denial must not trigger a restart, and an already running service should remain unchanged.

Original outputs help replace assumptions with evidence. The SCOrch Runbook Tester allows inspection of the data published by activities. Testing should use approved test targets because the activities being executed can make changes. [Microsoft: Build and test runbooks](https://learn.microsoft.com/en-us/system-center/orchestrator/design-and-build-runbooks).

For each case, compare the input, selected path, and observed result. A different internal structure is acceptable if the agreed properties remain intact. A change in the number of restarts or in failure handling, however, requires an operational decision.

## What the diagram can leave out

A runbook also depends on its execution conditions. Its schedule, existing service identity, and access to a file share may not appear beside the activity, yet they determine its behavior. Operating instructions may also require a manual check before restarting after a partial failure.

That follow-up work belongs in the handover. If it remains known only to one administrator, the new workflow appears more complete than it actually is. Open items should therefore have an owner before approval, and existing start mechanisms need to be controlled during the transition. Two active environments could otherwise execute the same job twice.

NodePilot can import supported SCOrch activities and their connections. Unsupported elements and limitations then need attention using the import report. The import saves transfer work. The comparison cases described above provide the basis for operational approval. [NodePilot: SCOrch import](https://github.com/Sev7eNup/NodePilot#coming-from-system-center-orchestrator).
