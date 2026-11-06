using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Security;
using NodePilot.Engine.Tests.Helpers;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

public class StepTesterTargetTests
{
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task PersistedDynamicCredential_RejectsInvalidResolutionBeforeDispatch(string value)
    {
        using var db = TestDbFactory.Create();
        var workflow = CreateWorkflow(null, null);
        workflow.DefinitionJson = workflow.DefinitionJson.Replace("\"credentialId\":null", "\"credentialId\":\"{{manual.credential}}\"");
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var executor = new CapturingRunScriptExecutor();
        using var services = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, new ActivityRegistry([executor]), new StubGlobalVariableStore(), services, new OutputRedactor());
        var result = await tester.TestStepAsync(workflow.Id, "script", StepTestAuthorizationSnapshot.Capture(workflow),
            new Dictionary<string, string> { ["manual.credential"] = value }, null, CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("explicit credential");
        executor.Context.Should().BeNull();
    }

    [Theory]
    [InlineData("[]", "requires an activityType")]
    [InlineData("{}", "requires an activityType")]
    [InlineData("{\"activityType\":\"\",\"config\":{}}", "requires an activityType")]
    [InlineData("{\"activityType\":\"runScript\",\"config\":null}", "requires an activityType")]
    [InlineData("{\"activityType\":\"runScript\",\"config\":{},\"targetMachineId\":42}", "valid node fields")]
    [InlineData("{\"activityType\":\"runScript\",\"config\":{},\"credentialId\":\"not-a-guid\"}", "valid node fields")]
    [InlineData("{\"activityType\":\"runScript\",\"config\":{},\"disabled\":\"false\"}", "valid node fields")]
    [InlineData("{\"activityType\":\"unknown\",\"config\":{}}", "No executor")]
    [InlineData("{\"activityType\":\"runScript\",\"config\":{},\"disabled\":true}", "disabled")]
    [InlineData("{\"activityType\":\"startWorkflow\",\"config\":{}}", "cannot be step-tested")]
    [InlineData("{\"activityType\":\"forEach\",\"config\":{}}", "cannot be step-tested")]
    public async Task TestStepAsync_InvalidOrBlockedStepOverride_FailsBeforeCallingExecutor(string json, string error)
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = TestDbFactory.Create();
        var workflow = CreateWorkflow(null, null);
        workflow.CheckedOutByUserId = Guid.NewGuid();
        workflow.CheckedOutAt = DateTime.UtcNow;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        using var document = JsonDocument.Parse(json);
        var executor = new CapturingRunScriptExecutor();
        using var services = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, new ActivityRegistry([executor]), new StubGlobalVariableStore(),
            services, new OutputRedactor());

        var result = await tester.TestStepAsync(workflow.Id, "script",
            StepTestAuthorizationSnapshot.Capture(workflow), null, null, ct, document.RootElement);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(error);
        executor.Context.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestStepAsync_StepOverrideWithoutCurrentLock_FailsBeforeCallingExecutor(bool replaceLock)
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = TestDbFactory.Create();
        var workflow = CreateWorkflow(null, null);
        workflow.CheckedOutByUserId = replaceLock ? Guid.NewGuid() : null;
        workflow.CheckedOutAt = replaceLock ? DateTime.UtcNow.AddMinutes(-1) : null;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        var snapshot = StepTestAuthorizationSnapshot.Capture(workflow);
        if (replaceLock)
        {
            workflow.CheckedOutAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        var stepOverride = JsonSerializer.SerializeToElement(new { activityType = "runScript", config = new { script = "live" } });
        var executor = new CapturingRunScriptExecutor();
        using var services = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, new ActivityRegistry([executor]), new StubGlobalVariableStore(),
            services, new OutputRedactor());

        var result = await tester.TestStepAsync(workflow.Id, "script", snapshot, null, null, ct, stepOverride);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(replaceLock ? "changed after authorization" : "active edit-lock");
        executor.Context.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestStepAsync_StepOverride_UsesCoherentLiveTargetCredentialAndConfig(bool runLocally)
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = TestDbFactory.Create();
        var savedMachine = new ManagedMachine { Id = Guid.NewGuid(), Name = "Production", Hostname = "production.example.test" };
        var liveMachine = new ManagedMachine { Id = Guid.NewGuid(), Name = "Test", Hostname = "test.example.test" };
        var workflow = CreateWorkflow(savedMachine.Id.ToString(), Guid.NewGuid());
        workflow.CheckedOutByUserId = Guid.NewGuid();
        workflow.CheckedOutAt = DateTime.UtcNow;
        db.ManagedMachines.AddRange(savedMachine, liveMachine);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        Guid? liveCredential = runLocally ? null : Guid.NewGuid();
        var stepOverride = JsonSerializer.SerializeToElement(new
        {
            activityType = "runScript",
            label = "Live script",
            targetMachineId = runLocally ? null : liveMachine.Id.ToString(),
            credentialId = liveCredential,
            config = new { script = "Write-Output 'live'" },
        });
        var executor = new CapturingRunScriptExecutor();
        using var services = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, new ActivityRegistry([executor]), new StubGlobalVariableStore(),
            services, new OutputRedactor());

        var result = await tester.TestStepAsync(workflow.Id, "script",
            StepTestAuthorizationSnapshot.Capture(workflow), null, null, ct, stepOverride);

        result.Success.Should().BeTrue();
        var context = Assert.IsType<StepExecutionContext>(executor.Context);
        context.TargetMachineId.Should().Be(runLocally ? null : liveMachine.Id);
        context.CredentialId.Should().Be(liveCredential);
        context.StepLabel.Should().Be("Live script");
        executor.Config.GetProperty("script").GetString().Should().Be("Write-Output 'live'");
        if (runLocally)
            context.ResolvedMachine.Should().BeNull();
        else
            Assert.IsType<ManagedMachine>(context.ResolvedMachine).Hostname.Should().Be(liveMachine.Hostname);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestStepAsync_SelectedMachineDeleted_FailsBeforeCallingExecutor(bool useStepOverride)
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = TestDbFactory.Create();
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "Remote", Hostname = "remote.example.test" };
        var workflow = CreateWorkflow(machine.Id.ToString(), Guid.NewGuid());
        workflow.CheckedOutByUserId = Guid.NewGuid();
        workflow.CheckedOutAt = DateTime.UtcNow;
        db.ManagedMachines.Add(machine);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        db.ManagedMachines.Remove(machine);
        await db.SaveChangesAsync(ct);

        var executor = new CapturingRunScriptExecutor();
        using var services = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, new ActivityRegistry([executor]), new StubGlobalVariableStore(),
            services, new OutputRedactor());

        JsonElement? stepOverride = useStepOverride
            ? JsonSerializer.SerializeToElement(new { activityType = "runScript", targetMachineId = machine.Id, config = new { script = "live" } })
            : null;
        var result = await tester.TestStepAsync(workflow.Id, "script",
            StepTestAuthorizationSnapshot.Capture(workflow), null, null, ct, stepOverride);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(machine.Id.ToString());
        executor.Context.Should().BeNull("a missing selected target must never reach an activity as a local target");
    }

    [Fact]
    public async Task TestStepAsync_StepOverride_UsesLiveActivityTypeAndClearsOmittedTargetBindings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = TestDbFactory.Create();
        var workflow = CreateWorkflow(Guid.NewGuid().ToString(), Guid.NewGuid(), "startWorkflow");
        workflow.CheckedOutByUserId = Guid.NewGuid();
        workflow.CheckedOutAt = DateTime.UtcNow;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        var stepOverride = JsonSerializer.SerializeToElement(new { activityType = "runScript", config = new { script = "live" } });
        var executor = new CapturingRunScriptExecutor();
        using var services = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, new ActivityRegistry([executor]), new StubGlobalVariableStore(),
            services, new OutputRedactor());

        var result = await tester.TestStepAsync(workflow.Id, "script",
            StepTestAuthorizationSnapshot.Capture(workflow), null, null, ct, stepOverride);

        result.Success.Should().BeTrue();
        var context = Assert.IsType<StepExecutionContext>(executor.Context);
        context.StepId.Should().Be("script");
        context.TargetMachineId.Should().BeNull();
        context.ResolvedMachine.Should().BeNull();
        context.CredentialId.Should().BeNull();
    }

    [Theory]
    [InlineData("local")]
    [InlineData("registered")]
    [InlineData("hostname")]
    public async Task TestStepAsync_ValidTarget_PreservesTargetAndCredential(string targetKind)
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = TestDbFactory.Create();
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "Remote", Hostname = "remote.example.test" };
        string? target = targetKind switch
        {
            "registered" => machine.Id.ToString(),
            "hostname" => machine.Hostname,
            _ => null,
        };
        Guid? credentialId = target is null ? null : Guid.NewGuid();
        var workflow = CreateWorkflow(target, credentialId);
        if (targetKind == "registered")
            db.ManagedMachines.Add(machine);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);

        var executor = new CapturingRunScriptExecutor();
        using var services = new ServiceCollection().BuildServiceProvider();
        var tester = new StepTester(db, new ActivityRegistry([executor]), new StubGlobalVariableStore(),
            services, new OutputRedactor());

        var result = await tester.TestStepAsync(workflow.Id, "script",
            StepTestAuthorizationSnapshot.Capture(workflow), null, null, ct);

        result.Success.Should().BeTrue();
        var context = Assert.IsType<StepExecutionContext>(executor.Context);
        context.CredentialId.Should().Be(credentialId);
        context.TargetMachineId.Should().Be(targetKind == "registered" ? machine.Id : null);
        if (target is null)
            context.ResolvedMachine.Should().BeNull("an explicitly local step remains local");
        else
            Assert.IsType<ManagedMachine>(context.ResolvedMachine).Hostname.Should().Be(machine.Hostname);
    }

    private static Workflow CreateWorkflow(string? target, Guid? credentialId, string activityType = "runScript") => new()
    {
        Id = Guid.NewGuid(),
        Name = "Step target",
        DefinitionJson = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new
                {
                    id = "script",
                    type = "activity",
                    data = new
                    {
                        activityType,
                        targetMachineId = target,
                        credentialId,
                        config = new { script = "Write-Output 'target test'" },
                    },
                },
            },
            edges = Array.Empty<object>(),
        }),
    };

    private sealed class CapturingRunScriptExecutor : IActivityExecutor
    {
        public string ActivityType => "runScript";
        public StepExecutionContext? Context { get; private set; }
        public JsonElement Config { get; private set; }

        public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        {
            Context = context;
            Config = config.Clone();
            return Task.FromResult(new ActivityResult { Success = true });
        }
    }
}
