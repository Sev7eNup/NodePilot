using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

public class StepTesterTests
{
    private static readonly StepTestAuthorizationSnapshot DefaultAuthorizationSnapshot = new(
        SharedWorkflowFolder.RootFolderId, Version: 1, CheckedOutByUserId: null, CheckedOutAt: null);

    /// <summary>
    /// ConfigOverride must replace the persisted node config when present, so the user can
    /// test their unsaved editor draft without going through Save first.
    /// </summary>
    [Fact]
    public async Task TestStepAsync_ConfigOverride_TakesPrecedenceOverPersistedConfig()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        // Persisted: delay 99 seconds; override: delay 0 seconds. The test must finish fast,
        // proving the override won.
        var def = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new
                {
                    id = "step-a",
                    type = "activity",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        activityType = "delay",
                        config = new { seconds = 99 }
                    }
                }
            },
            edges = Array.Empty<object>()
        });
        var workflow = new Workflow
        {
            Id = workflowId,
            Name = "wf",
            DefinitionJson = def,
            CheckedOutByUserId = Guid.NewGuid(),
            CheckedOutAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var registry = new ActivityRegistry(new IActivityExecutor[] { new DelayActivity() });
        var sp = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, registry, new StubGlobalVariableStore(), sp, new OutputRedactor());

        var override_ = JsonDocument.Parse("""{"seconds":0}""").RootElement;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await tester.TestStepAsync(
            workflowId, "step-a", StepTestAuthorizationSnapshot.Capture(workflow), null, override_, default);
        sw.Stop();

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("0 seconds");
        // 99-second persisted config would have been > 30s wall clock; override pushed to 0.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TestStepAsync_NoOverride_UsesPersistedConfig()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var def = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new
                {
                    id = "step-a",
                    type = "activity",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        activityType = "delay",
                        config = new { seconds = 0 }
                    }
                }
            },
            edges = Array.Empty<object>()
        });
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        await db.SaveChangesAsync();

        var registry = new ActivityRegistry(new IActivityExecutor[] { new DelayActivity() });
        var sp = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, registry, new StubGlobalVariableStore(), sp, new OutputRedactor());

        var result = await tester.TestStepAsync(
            workflowId, "step-a", DefaultAuthorizationSnapshot, null, null, default);
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("0 seconds");
    }

    [Fact]
    public async Task TestStepAsync_WhenWorkflowMovedAfterAuthorization_FailsBeforeActivityExecution()
    {
        using var db = TestDbFactory.Create();
        var movedFolder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(),
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Moved",
            Path = "/Moved",
            Depth = 1,
        };
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "wf",
            DefinitionJson = JsonSerializer.Serialize(new
            {
                nodes = new[]
                {
                    new
                    {
                        id = "step-a",
                        type = "activity",
                        data = new { activityType = "delay", config = new { seconds = 0 } },
                    },
                },
                edges = Array.Empty<object>(),
            }),
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var authorized = StepTestAuthorizationSnapshot.Capture(workflow);

        db.SharedWorkflowFolders.Add(movedFolder);
        workflow.FolderId = movedFolder.Id;
        await db.SaveChangesAsync();

        var tester = new StepTester(
            db,
            new ActivityRegistry(new IActivityExecutor[] { new DelayActivity() }),
            new StubGlobalVariableStore(),
            new ServiceCollection().BuildServiceProvider(),
            new OutputRedactor());

        var result = await tester.TestStepAsync(
            workflow.Id, "step-a", authorized, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("changed after authorization");
    }

    [Fact]
    public async Task TestStepAsync_ConfigOverrideWhenLockEpochChanged_FailsBeforeActivityExecution()
    {
        using var db = TestDbFactory.Create();
        var lockOwnerId = Guid.NewGuid();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "wf",
            CheckedOutByUserId = lockOwnerId,
            CheckedOutAt = DateTime.UtcNow.AddMinutes(-1),
            DefinitionJson = JsonSerializer.Serialize(new
            {
                nodes = new[]
                {
                    new
                    {
                        id = "step-a",
                        type = "activity",
                        data = new { activityType = "delay", config = new { seconds = 0 } },
                    },
                },
                edges = Array.Empty<object>(),
            }),
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var authorized = StepTestAuthorizationSnapshot.Capture(workflow);

        // Same user, new lock epoch: a stale override request must not execute under the new lock.
        workflow.CheckedOutAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var tester = new StepTester(
            db,
            new ActivityRegistry(new IActivityExecutor[] { new DelayActivity() }),
            new StubGlobalVariableStore(),
            new ServiceCollection().BuildServiceProvider(),
            new OutputRedactor());
        var configOverride = JsonDocument.Parse("""{"seconds":0}""").RootElement.Clone();

        var result = await tester.TestStepAsync(
            workflow.Id, "step-a", authorized, null, configOverride, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("changed after authorization");
    }

    [Fact]
    public async Task TestStepAsync_SqlQueryTemplate_IsNotResolvedBeforeSqlGuard()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var def = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new
                {
                    id = "step-a",
                    type = "activity",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        activityType = "sql",
                        config = new
                        {
                            connectionString = "Data Source=:memory:",
                            provider = "sqlite",
                            query = "SELECT {{prev.output}} AS id",
                        },
                    },
                },
            },
            edges = Array.Empty<object>(),
        });
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlActivity:RequireConnectionRef"] = "false",
            })
            .Build();
        var registry = new ActivityRegistry(new IActivityExecutor[] { new SqlActivity(config) });
        var sp = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, registry, new StubGlobalVariableStore(), sp, new OutputRedactor());

        var result = await tester.TestStepAsync(
            workflowId,
            "step-a",
            DefaultAuthorizationSnapshot,
            new Dictionary<string, string> { ["prev.output"] = "7" },
            null,
            default);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("{{...}} templates");
        result.ErrorOutput.Should().Contain("parameters");
    }

    /// <summary>
    /// Security regression: step-test must refuse startWorkflow steps. The runtime sub-workflow
    /// authorization gate in StartWorkflowActivity only fires when a persisted parent
    /// WorkflowExecution row exists, and step-test runs synthesise a transient
    /// WorkflowExecutionId that is never persisted — without this guard an Operator with Edit
    /// on the parent workflow could trigger a child workflow they have no folder-permission on
    /// by clicking "Test Step" instead of "Run".
    /// </summary>
    [Fact]
    public async Task TestStepAsync_StartWorkflow_IsRefused()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var def = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new
                {
                    id = "step-a",
                    type = "activity",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        activityType = "startWorkflow",
                        config = new { workflowNameOrId = Guid.NewGuid().ToString() }
                    }
                }
            },
            edges = Array.Empty<object>()
        });
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        await db.SaveChangesAsync();

        var registry = new ActivityRegistry(Array.Empty<IActivityExecutor>());
        var sp = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, registry, new StubGlobalVariableStore(), sp, new OutputRedactor());

        var result = await tester.TestStepAsync(
            workflowId, "step-a", DefaultAuthorizationSnapshot, null, null, default);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("startWorkflow", "sub-workflow auth gate would be bypassed in step-test");
    }

    /// <summary>
    /// Security regression: forEach launches child workflows through the same runtime RBAC
    /// resolver as startWorkflow. A step-test has no persisted parent execution, so allowing it
    /// would skip that resolver and permit a cross-folder child invocation.
    /// </summary>
    [Fact]
    public async Task TestStepAsync_ForEach_IsRefused()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var def = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new
                {
                    id = "step-a",
                    type = "activity",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        activityType = "forEach",
                        config = new
                        {
                            workflowNameOrId = Guid.NewGuid().ToString(),
                            items = "[]",
                        },
                    },
                },
            },
            edges = Array.Empty<object>(),
        });
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        await db.SaveChangesAsync();

        var registry = new ActivityRegistry(Array.Empty<IActivityExecutor>());
        var sp = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, registry, new StubGlobalVariableStore(), sp, new OutputRedactor());

        var result = await tester.TestStepAsync(
            workflowId, "step-a", DefaultAuthorizationSnapshot, null, null, default);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("forEach", "sub-workflow auth gate would be bypassed in step-test");
        result.ErrorOutput.Should().Contain("cannot be step-tested");
    }

    [Fact]
    public async Task TestStepAsync_DisabledStep_FailsFast()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var def = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new
                {
                    id = "step-a",
                    type = "activity",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        activityType = "delay",
                        disabled = true,
                        config = new { seconds = 0 }
                    }
                }
            },
            edges = Array.Empty<object>()
        });
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        await db.SaveChangesAsync();

        var registry = new ActivityRegistry(new IActivityExecutor[] { new DelayActivity() });
        var sp = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, registry, new StubGlobalVariableStore(), sp, new OutputRedactor());

        var result = await tester.TestStepAsync(
            workflowId, "step-a", DefaultAuthorizationSnapshot, null, null, default);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("disabled");
    }

    /// <summary>
    /// Step-test results must run through OutputRedactor exactly like the production
    /// StepRunner path — otherwise an Operator can craft a runScript that interpolates a
    /// secret global / credential into the response body and read the plaintext back via
    /// POST /api/workflows/{id}/steps/{stepId}/test, bypassing the always-on redaction
    /// contract documented in CLAUDE.md.
    /// </summary>
    [Fact]
    public async Task TestStepAsync_ResultIsRedacted_BeforeReturn()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var def = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new
                {
                    id = "step-a",
                    type = "activity",
                    position = new { x = 0, y = 0 },
                    data = new { activityType = "leakySecret", config = new { } }
                }
            },
            edges = Array.Empty<object>()
        });
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        await db.SaveChangesAsync();

        var registry = new ActivityRegistry(new IActivityExecutor[] { new LeakySecretActivity() });
        var sp = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, registry, new StubGlobalVariableStore(), sp, new OutputRedactor());

        var result = await tester.TestStepAsync(
            workflowId, "step-a", DefaultAuthorizationSnapshot, null, null, default);

        result.Success.Should().BeTrue();
        // OutputRedactor catches the key=value shape (`password=`) and the JWT shape; the
        // raw secret values must be replaced with "***" before the response leaves StepTester.
        result.Output.Should().NotContain("hunter2");
        result.Output.Should().Contain("***");
        // JWT shape has no capture group → fully replaced with the placeholder.
        result.OutputParameters["leakedToken"].Should().Be(OutputRedactor.Placeholder);
    }

    /// <summary>
    /// Activity that emits known token-shaped strings on stdout and as an output parameter.
    /// Used to verify <see cref="OutputRedactor"/> is wired into the step-test return path.
    /// </summary>
    private sealed class LeakySecretActivity : IActivityExecutor
    {
        public string ActivityType => "leakySecret";
        public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
            => Task.FromResult(new ActivityResult
            {
                Success = true,
                Output = "password=hunter2",
                OutputParameters = { ["leakedToken"] = "eyJabcdef0123456789.eyJpayloadabcdefghij.signaturevaluexyz0" },
            });
    }

    private sealed class StubGlobalVariableStore : IGlobalVariableStore
    {
        public Task<IReadOnlyDictionary<string, string>> GetAllResolvedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<GlobalVariableResolutionResult> GetAllResolvedDetailedAsync(CancellationToken ct)
            => Task.FromResult(new GlobalVariableResolutionResult(
                new Dictionary<string, string>(), new HashSet<string>()));
        public Task<IReadOnlyList<GlobalVariable>> GetAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GlobalVariable>>(Array.Empty<GlobalVariable>());
        public Task<string?> GetValueAsync(string name, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<GlobalVariable> CreateAsync(string name, string value, bool isSecret, string? description, Guid folderId, string? updatedBy, CancellationToken ct)
            => throw new NotSupportedException();
        public Task UpdateAsync(Guid id, string name, string? value, bool isSecret, string? description, Guid? folderId, string? updatedBy, CancellationToken ct)
            => throw new NotSupportedException();
        public Task MoveToFolderAsync(Guid id, Guid folderId, string? updatedBy, CancellationToken ct)
            => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReencryptionSummary> ReencryptAllSecretsAsync(CancellationToken ct)
            => Task.FromResult(new ReencryptionSummary(0, 0, Array.Empty<ReencryptionSkip>()));
    }
}
