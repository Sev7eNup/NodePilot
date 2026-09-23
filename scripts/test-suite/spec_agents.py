"""Opt-in live-model integration; no OS, HTTP or external MCP tools are selected."""

from suitelib import Step, Workflow
from spec_core import assert_step, ok_return


def workflows():
    schema = {"type": "object", "properties": {"answer": {"const": "OK"}},
              "required": ["answer"], "additionalProperties": False}
    steps = [
        Step("v0", "Agent: structured answer", "aiAgent", {
            "task": 'Return exactly {"answer":"OK"}.', "resultFormat": "json",
            "resultSchema": schema, "maxModelCalls": 3, "maxToolCalls": 1,
            "timeoutSeconds": 90, "agent": {"id": "agent", "role": "Responder", "tools": []},
        }, cases=[{"id": "aiAgent.json", "dimension": "aiAgent.resultFormat", "value": "json"}]),
        Step("v1", "Team: delegate and answer", "aiAgentTeam", {
            "task": 'First delegate to researcher to confirm the word OK. Then return {"answer":"OK"}.',
            "resultFormat": "json", "resultSchema": schema,
            "maxModelCalls": 6, "maxToolCalls": 3, "maxDelegations": 2, "timeoutSeconds": 120,
            "members": [
                {"id": "supervisor", "role": "Supervisor", "isSupervisor": True,
                 "instructions": "Delegate once before returning the answer.", "tools": []},
                {"id": "researcher", "role": "Researcher", "instructions": "Confirm OK in your delegation response.", "tools": []},
            ],
        }, cases=[{"id": "aiAgentTeam.delegate", "dimension": "aiAgentTeam.members", "value": "sequential delegation"}]),
        assert_step('''
$single = {{v0.output}} | ConvertFrom-Json
$team = {{v1.output}} | ConvertFrom-Json
$run = {{v1.param.agentRunId}}
$delegations = [int]{{v1.param.delegations}}
if ($single.answer -ne 'OK' -or $team.answer -ne 'OK') { throw 'Unexpected agent result' }
if ([string]::IsNullOrWhiteSpace($run)) { throw 'Agent run ID missing' }
if ($delegations -lt 1) { throw 'The model did not delegate' }
$assertOk = 'aiAgents'
'''),
        ok_return("aiAgents"),
    ]
    return [Workflow(99, "aiAgents", "[TestSuite] AI agents", "Opt-in live model and sequential delegation check. No host tools.",
                     "positive", "integration", "C", steps, max_runtime=270,
                     requires=["config:Llm:Enabled=true", "config:Llm:ActiveProfileId", "globals:NP_TESTSUITE_AI_AGENTS"])]
