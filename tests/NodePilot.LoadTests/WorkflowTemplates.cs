using System.Text;
using System.Text.Json;

namespace NodePilot.LoadTests;

/// <summary>
/// Builds large/complex workflow definitions as React-Flow-schema JSON.
/// Every template accepts a uniqueSuffix so multiple seeded copies don't collide on node ids.
/// All templates target the "loadtest-target" machine (localhost, no credential) — the
/// NoOpSessionFactory in the API host short-circuits the Remote path without doing any WinRM work.
/// </summary>
public static class WorkflowTemplates
{
    public const string TargetMachineName = "loadtest-target";

    public static string BuildDeepSequential(int depth, string uniqueSuffix)
    {
        var nodes = new List<object>();
        var edges = new List<object>();
        for (int i = 0; i < depth; i++)
        {
            var id = $"step-{uniqueSuffix}-{i}";
            nodes.Add(new
            {
                id,
                type = "activity",
                position = new { x = 100, y = 80 * i },
                data = new
                {
                    label = $"Step {i}",
                    activityType = "runScript",
                    targetMachineId = TargetMachineName,
                    config = new { script = $"Write-Output 'step-{i}'", timeoutSeconds = 30 }
                }
            });
            if (i > 0)
            {
                edges.Add(new
                {
                    id = $"edge-{uniqueSuffix}-{i}",
                    source = $"step-{uniqueSuffix}-{i - 1}",
                    target = id,
                    type = "labeled"
                });
            }
        }
        return Serialize(nodes, edges);
    }

    public static string BuildWideFanout(int width, string uniqueSuffix)
    {
        var nodes = new List<object>();
        var edges = new List<object>();

        var rootId = $"root-{uniqueSuffix}";
        var joinId = $"join-{uniqueSuffix}";

        nodes.Add(new
        {
            id = rootId,
            type = "activity",
            position = new { x = 50, y = 50 },
            data = new
            {
                label = "Root",
                activityType = "delay",
                config = new { seconds = 0 }
            }
        });

        for (int i = 0; i < width; i++)
        {
            var branchId = $"branch-{uniqueSuffix}-{i}";
            nodes.Add(new
            {
                id = branchId,
                type = "activity",
                position = new { x = 200 + 180 * i, y = 200 },
                data = new
                {
                    label = $"Branch {i}",
                    activityType = "runScript",
                    targetMachineId = TargetMachineName,
                    config = new { script = $"Write-Output 'branch-{i}'", timeoutSeconds = 30 }
                }
            });
            edges.Add(new
            {
                id = $"e-root-{uniqueSuffix}-{i}",
                source = rootId,
                target = branchId,
                type = "labeled"
            });
            edges.Add(new
            {
                id = $"e-join-{uniqueSuffix}-{i}",
                source = branchId,
                target = joinId,
                type = "labeled"
            });
        }

        nodes.Add(new
        {
            id = joinId,
            type = "activity",
            position = new { x = 400, y = 400 },
            data = new
            {
                label = "Join",
                activityType = "junction",
                config = new { mode = "waitAll" }
            }
        });

        return Serialize(nodes, edges);
    }

    public static string BuildMixedHeavy(string uniqueSuffix, string apiBaseUrl)
    {
        var nodes = new List<object>();
        var edges = new List<object>();

        void AddNode(string id, int x, int y, string label, string activityType, object? config = null, bool remote = false)
        {
            nodes.Add(new
            {
                id,
                type = "activity",
                position = new { x, y },
                data = remote
                    ? new { label, activityType, targetMachineId = TargetMachineName, config = config ?? new { } }
                    : (object)new { label, activityType, config = config ?? new { } }
            });
        }

        void AddEdge(string source, string target)
        {
            edges.Add(new { id = $"e-{source}-{target}", source, target, type = "labeled" });
        }

        string N(string name) => $"{name}-{uniqueSuffix}";

        // Root → parallel branches of mixed activities → junction → returnData
        AddNode(N("root"), 50, 50, "Root", "delay", new { seconds = 0 });

        AddNode(N("script1"), 250, 50, "Script A", "runScript",
            new { script = "$x = 'a'; Write-Output $x", timeoutSeconds = 30 }, remote: true);
        AddNode(N("script2"), 250, 150, "Script B", "runScript",
            new { script = "$y = 'b'; Write-Output $y", timeoutSeconds = 30 }, remote: true);
        AddNode(N("rest"), 250, 250, "REST", "restApi",
            new { url = $"{apiBaseUrl}/healthz", method = "GET", timeoutSeconds = 10 });
        AddNode(N("log1"), 250, 350, "Log", "log",
            new { level = "info", message = "branch reached" });
        AddNode(N("delay1"), 250, 450, "Delay", "delay", new { seconds = 0 });

        AddEdge(N("root"), N("script1"));
        AddEdge(N("root"), N("script2"));
        AddEdge(N("root"), N("rest"));
        AddEdge(N("root"), N("log1"));
        AddEdge(N("root"), N("delay1"));

        AddNode(N("junction"), 500, 250, "Join", "junction", new { mode = "waitAll" });
        AddEdge(N("script1"), N("junction"));
        AddEdge(N("script2"), N("junction"));
        AddEdge(N("rest"), N("junction"));
        AddEdge(N("log1"), N("junction"));
        AddEdge(N("delay1"), N("junction"));

        // After junction: another fan-out of 5 script steps to exercise DB write concurrency
        for (int i = 0; i < 5; i++)
        {
            var id = N($"post{i}");
            AddNode(id, 700, 100 + 80 * i, $"Post {i}", "runScript",
                new { script = $"Write-Output 'post-{i}'", timeoutSeconds = 30 }, remote: true);
            AddEdge(N("junction"), id);
        }

        AddNode(N("return"), 900, 250, "Return", "returnData",
            new { data = new { status = "done", suffix = uniqueSuffix } });
        for (int i = 0; i < 5; i++)
            AddEdge(N($"post{i}"), N("return"));

        return Serialize(nodes, edges);
    }

    /// <summary>
    /// Builds a nested sub-workflow chain: each parent invokes the next child via startWorkflow.
    /// Returns the list of definitions in child-first order — caller must seed in that order
    /// so parents reference an already-created child name.
    /// </summary>
    public static List<(string Name, string Json)> BuildSubworkflowNest(int depth, string uniqueSuffix)
    {
        var result = new List<(string, string)>();
        for (int level = 0; level < depth; level++)
        {
            var name = $"loadtest-sub-{uniqueSuffix}-L{level}";
            var nodes = new List<object>();
            var edges = new List<object>();

            var rootId = $"root-{uniqueSuffix}-L{level}";
            nodes.Add(new
            {
                id = rootId,
                type = "activity",
                position = new { x = 50, y = 50 },
                data = new
                {
                    label = $"L{level} root",
                    activityType = "runScript",
                    targetMachineId = TargetMachineName,
                    config = new { script = $"Write-Output 'L{level}'", timeoutSeconds = 30 }
                }
            });

            if (level > 0)
            {
                var childId = $"child-{uniqueSuffix}-L{level}";
                var childName = $"loadtest-sub-{uniqueSuffix}-L{level - 1}";
                nodes.Add(new
                {
                    id = childId,
                    type = "activity",
                    position = new { x = 50, y = 200 },
                    data = new
                    {
                        label = $"Call L{level - 1}",
                        activityType = "startWorkflow",
                        config = new
                        {
                            workflowNameOrId = childName,
                            waitForCompletion = true,
                            timeoutSeconds = 120
                        }
                    }
                });
                edges.Add(new { id = $"e-{rootId}-{childId}", source = rootId, target = childId, type = "labeled" });

                var returnId = $"return-{uniqueSuffix}-L{level}";
                nodes.Add(new
                {
                    id = returnId,
                    type = "activity",
                    position = new { x = 50, y = 350 },
                    data = new
                    {
                        label = "Return",
                        activityType = "returnData",
                        config = new { data = new { level } }
                    }
                });
                edges.Add(new { id = $"e-{childId}-{returnId}", source = childId, target = returnId, type = "labeled" });
            }
            else
            {
                var returnId = $"return-{uniqueSuffix}-L0";
                nodes.Add(new
                {
                    id = returnId,
                    type = "activity",
                    position = new { x = 50, y = 200 },
                    data = new
                    {
                        label = "Return",
                        activityType = "returnData",
                        config = new { data = new { level = 0 } }
                    }
                });
                edges.Add(new { id = $"e-{rootId}-{returnId}", source = rootId, target = returnId, type = "labeled" });
            }

            result.Add((name, Serialize(nodes, edges)));
        }
        return result;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string Serialize(object nodes, object edges)
    {
        var sb = new StringBuilder();
        sb.Append("{\"nodes\":");
        sb.Append(JsonSerializer.Serialize(nodes, JsonOpts));
        sb.Append(",\"edges\":");
        sb.Append(JsonSerializer.Serialize(edges, JsonOpts));
        sb.Append('}');
        return sb.ToString();
    }
}
