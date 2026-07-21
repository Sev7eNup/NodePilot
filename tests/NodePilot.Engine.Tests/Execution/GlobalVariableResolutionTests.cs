using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// End-to-end smoke for <c>{{globals.NAME}}</c> resolution: populate the DB via
/// <see cref="GlobalVariableStore"/>, run a workflow whose activity config references
/// a global, and assert the executor saw the resolved value.
/// </summary>
[Collection("SerialEngineTests")]
public class GlobalVariableResolutionTests
{
    [Fact]
    public async Task EngineInjectsGlobalsIntoActivityConfig()
    {
        // Arrange: one-shared-connection test harness with the ActivityRegistry wired up.
        var capturedConfig = (JsonElement?)null;
        var mockExecutor = new Mock<IActivityExecutor>();
        mockExecutor.Setup(e => e.ActivityType).Returns("restApi");
        mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((_, cfg, __) => capturedConfig = cfg.Clone())
            .ReturnsAsync(new ActivityResult { Success = true, Output = "ok" });

        var manualTriggerExecutor = new Mock<IActivityExecutor>();
        manualTriggerExecutor.Setup(e => e.ActivityType).Returns("manualTrigger");
        manualTriggerExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        var registry = new ActivityRegistry(new[] { mockExecutor.Object, manualTriggerExecutor.Object });
        var (db, sp, conn) = TestDbContext.CreateWithScopedServices(registry);
        try
        {
            // Seed a plain (non-secret) global via the real store so the engine's resolve
            // path exercises the same code the controller would.
            var store = new GlobalVariableStore(db, new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser));
            await store.CreateAsync("API_BASE", "https://api.example.com", isSecret: false, description: null,
                folderId: GlobalVariableFolder.RootFolderId, updatedBy: "test", ct: CancellationToken.None);

            // IMPORTANT: GlobalVariableStore also has to be resolvable in the per-execution
            // scope the engine creates. Register it in the shared-SP so the scope's child
            // container picks it up.
            var sp2 = WireGlobalStoreInto(conn, registry);

            var def = """
                {"nodes":[{"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},
                  {"id":"s1","type":"activity","position":{"x":0,"y":0},
                  "data":{"activityType":"restApi","config":{"url":"{{globals.API_BASE}}/v2/status","method":"GET"}}}],
                 "edges":[{"id":"te","source":"trigger-1","target":"s1"}]}
                """;
            var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Test", DefinitionJson = def };
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync();

            var engine = new WorkflowEngine(db, registry, NullLogger<WorkflowEngine>.Instance, sp2, Mock.Of<IExecutionNotifier>());

            // Act
            var execution = await engine.ExecuteAsync(workflow, "test", CancellationToken.None);

            // Assert
            execution.Status.Should().Be(ExecutionStatus.Succeeded);
            capturedConfig.Should().NotBeNull();
            capturedConfig!.Value.GetProperty("url").GetString()
                .Should().Be("https://api.example.com/v2/status",
                    "engine must substitute {{globals.API_BASE}} before handing config to the executor");
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// Regression test for a secret-resolution fix: when a workflow references a global that
    /// exists in the DB but cannot be decrypted on this host (DPAPI scope mismatch / clustered
    /// HA with DPAPI / wrong AES key), the engine must FAIL the run loudly. The previous behaviour left the literal
    /// "{{globals.X}}" in the resolved config, so the activity ran with that literal in
    /// place of the secret — silent corruption of every downstream call.
    /// </summary>
    [Fact]
    public async Task UnresolvableGlobalReference_FailsRun_WithActionableErrorMessage()
    {
        var manualTriggerExecutor = new Mock<IActivityExecutor>();
        manualTriggerExecutor.Setup(e => e.ActivityType).Returns("manualTrigger");
        manualTriggerExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        var registry = new ActivityRegistry(new[] { Mock.Of<IActivityExecutor>(e => e.ActivityType == "restApi"), manualTriggerExecutor.Object });
        var (db, _, conn) = TestDbContext.CreateWithScopedServices(registry);
        try
        {
            // Seed a secret global with valid Base64 wrapping ciphertext that won't
            // decrypt under the active protector — i.e. the row is "exists but broken".
            // We do this by writing the row directly via raw bytes that look like a
            // DPAPI blob to GlobalVariableStore (Base64-decoded), but use a different
            // protector at read time so Unprotect throws CryptographicException.
            var dpapiStore = new GlobalVariableStore(db,
                new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser));
            await dpapiStore.CreateAsync("STRIPE_KEY", "sk_test_real_value", isSecret: true,
                description: null, folderId: GlobalVariableFolder.RootFolderId, updatedBy: "test", ct: CancellationToken.None);

            // Switch to AES-GCM at engine-resolve time → DPAPI rows decrypt-fail and land
            // in the Unresolvable set.
            var aesKey = new byte[32];
            for (var i = 0; i < aesKey.Length; i++) aesKey[i] = (byte)(i + 1);
            var sp2 = BuildScopedSpWithProtector(conn, registry,
                new NodePilot.Data.Security.AesGcmSecretProtector(aesKey));

            var def = """
                {"nodes":[{"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},
                  {"id":"s1","type":"activity","position":{"x":0,"y":0},
                  "data":{"activityType":"restApi","config":{"url":"https://api.com","headers":{"Authorization":"Bearer {{globals.STRIPE_KEY}}"}}}}],
                 "edges":[{"id":"te","source":"trigger-1","target":"s1"}]}
                """;
            var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WithBrokenSecret", DefinitionJson = def };
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync();

            var engine = new WorkflowEngine(db, registry, NullLogger<WorkflowEngine>.Instance, sp2, Mock.Of<IExecutionNotifier>());
            var execution = await engine.ExecuteAsync(workflow, "test", CancellationToken.None);

            execution.Status.Should().Be(ExecutionStatus.Failed,
                "a workflow that references an unresolvable secret must fail loudly, not run with the literal placeholder leaking into HTTP headers");
            execution.ErrorMessage.Should().NotBeNull();
            execution.ErrorMessage!.Should().Contain("STRIPE_KEY",
                "the error message must name which global is broken so the operator knows what to re-enter");
            execution.ErrorMessage!.Should().Contain("AesGcm",
                "and should suggest the portable provider as the fix");
        }
        finally { conn.Dispose(); }
    }

    private static IServiceProvider BuildScopedSpWithProtector(SqliteConnection conn, ActivityRegistry registry, ISecretProtector protector)
    {
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(conn));
        services.AddScoped(_ => registry);
        services.AddSingleton(protector);
        services.AddScoped<IGlobalVariableStore, GlobalVariableStore>();
        return services.BuildServiceProvider();
    }

    // Rebuilds a ServiceProvider on the same SQLite connection but with BOTH ActivityRegistry
    // and IGlobalVariableStore wired up — so the engine's per-run CreateAsyncScope finds
    // the store and doesn't silently skip globals resolution.
    private static IServiceProvider WireGlobalStoreInto(SqliteConnection conn, ActivityRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(conn));
        services.AddScoped(_ => registry);
        // ISecretProtector is required by GlobalVariableStore's only public ctor (added when
        // secret handling was refactored) — register a DPAPI-CurrentUser default for tests.
        services.AddSingleton<ISecretProtector>(_ => new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser));
        services.AddScoped<IGlobalVariableStore, GlobalVariableStore>();
        return services.BuildServiceProvider();
    }
}
