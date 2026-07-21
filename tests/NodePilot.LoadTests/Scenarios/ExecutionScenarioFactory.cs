using NBomber.Contracts;
using NBomber.CSharp;

namespace NodePilot.LoadTests.Scenarios;

/// <summary>
/// Builds NBomber scenarios that trigger random workflows from a pre-seeded pool and wait
/// for each to reach a terminal status. One scenario iteration = one full workflow execution
/// (trigger + poll terminal), so NBomber's latency stats reflect end-to-end execution time.
/// </summary>
public static class ExecutionScenarioFactory
{
    public static ScenarioProps Build(
        string name,
        NodePilotApiClient client,
        List<Guid> workflowIds,
        TimeSpan terminalTimeout,
        SignalRObserver? observer,
        params LoadSimulation[] loadSimulations)
    {
        if (workflowIds.Count == 0)
            throw new InvalidOperationException("No workflow ids available — seeding must have failed.");

        Func<IScenarioContext, Task<IResponse>> runIteration = async ctx =>
        {
            var wfId = workflowIds[Random.Shared.Next(workflowIds.Count)];
            var triggeredAt = DateTime.UtcNow;
            Guid execId;

            try
            {
                execId = await client.ExecuteAsync(wfId);
            }
            catch (Exception ex)
            {
                return Response.Fail(statusCode: "trigger", message: ex.Message);
            }

            if (observer is not null)
                _ = observer.TrackAsync(execId, triggeredAt, CancellationToken.None);

            var status = await client.PollTerminalAsync(execId, terminalTimeout);
            if (status == "Succeeded")
                return Response.Ok(statusCode: status);
            if (status == "Timeout")
                return Response.Fail(statusCode: status, message: "Terminal poll timeout");
            return Response.Fail(statusCode: status, message: $"Execution ended with status {status}");
        };

        return Scenario.Create(name, runIteration)
            .WithoutWarmUp()
            .WithLoadSimulations(loadSimulations);
    }
}
