namespace NodePilot.LoadTests;

/// <summary>
/// Creates a fresh batch of workflows for the load test. Each run uses a unique session suffix
/// so concurrent runs don't step on each other's names. Returns the workflow ids to execute.
/// </summary>
public static class Seeder
{
    public record SeedResult(List<Guid> PlainWorkflowIds, List<Guid> SubWorkflowRootIds);

    public static async Task<SeedResult> SeedAsync(NodePilotApiClient client, LoadTestOptions options, CancellationToken ct)
    {
        var session = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var plain = new List<Guid>();

        for (int copy = 0; copy < options.Seed.CopiesPerTemplate; copy++)
        {
            var suffix = $"{session}-{copy}";

            var deepJson = WorkflowTemplates.BuildDeepSequential(options.Seed.DeepSequentialDepth, suffix);
            plain.Add(await client.CreateWorkflowAsync($"loadtest-deep-{suffix}", deepJson, ct));

            var wideJson = WorkflowTemplates.BuildWideFanout(options.Seed.WideFanoutWidth, suffix);
            plain.Add(await client.CreateWorkflowAsync($"loadtest-wide-{suffix}", wideJson, ct));

            var mixedJson = WorkflowTemplates.BuildMixedHeavy(suffix, options.ApiBaseUrl);
            plain.Add(await client.CreateWorkflowAsync($"loadtest-mixed-{suffix}", mixedJson, ct));
        }

        // Sub-workflow chain: seed children before parents so parent's workflowNameOrId resolves.
        var subRoots = new List<Guid>();
        for (int copy = 0; copy < options.Seed.CopiesPerTemplate; copy++)
        {
            var suffix = $"{session}-{copy}";
            var chain = WorkflowTemplates.BuildSubworkflowNest(options.Seed.SubWorkflowDepth, suffix);
            Guid topId = Guid.Empty;
            foreach (var (name, json) in chain)
            {
                topId = await client.CreateWorkflowAsync(name, json, ct);
            }
            subRoots.Add(topId); // The last one seeded is the outermost parent
        }

        return new SeedResult(plain, subRoots);
    }
}
