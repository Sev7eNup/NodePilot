using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Activities;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class CustomActivityWrapperAllowlistTests
{
    [Fact]
    public void Wrap_WithAllowlist_CapturesOnlyDeclaredOutputs()
    {
        var wrapped = PowerShellScriptWrapper.Wrap(
            "$result = 1; $helper = 2",
            new Dictionary<string, string> { ["ComputerName"] = "srv01" },
            NullLogger.Instance,
            outputCaptureAllowlist: ["result"]);

        wrapped.Should().Contain("$__npOutAllow");
        wrapped.Should().Contain("$__npOutAllow.Add('result')");
        wrapped.Should().Contain("$__npOutAllow.Contains($_.Name)");
        // The injected input + the undeclared helper local must NOT be added to the capture allow-list.
        wrapped.Should().NotContain("$__npOutAllow.Add('ComputerName')");
        wrapped.Should().NotContain("$__npOutAllow.Add('helper')");
    }

    [Fact]
    public void Wrap_WithoutAllowlist_KeepsLegacyCaptureAllBehaviour()
    {
        var wrapped = PowerShellScriptWrapper.Wrap("$x = 1", new Dictionary<string, string>(), NullLogger.Instance);

        wrapped.Should().NotContain("$__npOutAllow");
        wrapped.Should().Contain("-not $__npBuiltinVars.Contains($_.Name)");
    }
}

public class CustomActivityTypeAndValidationTests
{
    [Theory]
    [InlineData("custom:disk_check", true)]
    [InlineData("custom:a", true)]
    [InlineData("runScript", false)]
    [InlineData("custom", false)]
    public void IsCustomType_RecognizesPrefix(string type, bool expected)
        => CustomActivityType.IsCustomType(type).Should().Be(expected);

    [Fact]
    public void KeyOf_ExtractsSlug() => CustomActivityType.KeyOf("custom:disk_check").Should().Be("disk_check");

    [Theory]
    [InlineData("custom:disk_check", true)]
    [InlineData("custom:bad space", false)]
    [InlineData("custom:", false)]
    public void IsValidCustomType_EnforcesGrammar(string type, bool expected)
        => CustomActivityType.IsValidCustomType(type).Should().Be(expected);

    [Fact]
    public void Validate_RejectsOutputNamedExitCode()
    {
        var error = CustomActivityValidation.Validate("k", "K", "extension", "auto",
            [], [new CustomActivityOutputParameter("exitCode", "number")], requireKey: true);
        error.Should().NotBeNull();
    }

    [Fact]
    public void Validate_RejectsOverlappingInputOutputNames()
    {
        var error = CustomActivityValidation.Validate("k", "K", "extension", "auto",
            [new CustomActivityInputParameter("dup", "Dup", "string")],
            [new CustomActivityOutputParameter("dup", "string")], requireKey: true);
        error.Should().NotBeNull();
    }

    [Fact]
    public void Validate_RejectsBadParamName()
    {
        var error = CustomActivityValidation.Validate("k", "K", "extension", "auto",
            [new CustomActivityInputParameter("bad-name", "Bad", "string")], [], requireKey: true);
        error.Should().NotBeNull();
    }

    [Fact]
    public void Validate_AcceptsValidDefinition()
    {
        var error = CustomActivityValidation.Validate("disk_check", "Disk Check", "extension", "auto",
            [new CustomActivityInputParameter("path", "Path", "string", Required: true)],
            [new CustomActivityOutputParameter("status", "string")], requireKey: true);
        error.Should().BeNull();
    }
}

public class CustomActivityRegistryDispatchTests
{
    private sealed class StubExecutor(string type) : IActivityExecutor
    {
        public string ActivityType => type;
        public Task<ActivityResult> ExecuteAsync(StepExecutionContext c, JsonElement cfg, CancellationToken ct)
            => Task.FromResult(new ActivityResult { Success = true });
    }

    [Fact]
    public void GetExecutor_CustomPrefix_RoutesToSentinelExecutor()
    {
        var custom = new StubExecutor(CustomActivityType.ExecutorSentinel);
        var run = new StubExecutor("runScript");
        var registry = new ActivityRegistry(new IActivityExecutor[] { custom, run });

        registry.GetExecutor("custom:disk_check").Should().BeSameAs(custom);
        registry.GetExecutor("custom:anything_else").Should().BeSameAs(custom);
        registry.GetExecutor("runScript").Should().BeSameAs(run);
    }

    [Fact]
    public void GetExecutor_UnknownNonCustom_Throws()
    {
        var registry = new ActivityRegistry(new IActivityExecutor[] { new StubExecutor("runScript") });
        var act = () => registry.GetExecutor("totallyUnknown");
        act.Should().Throw<InvalidOperationException>();
    }
}

public class CustomActivityExecutorBranchTests
{
    private static CustomActivityExecutor NewExecutor(NodePilotDbContext db) =>
        new(new CustomActivityDefinitionStore(db),
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            NullLogger<CustomActivityExecutor>.Instance);

    private static JsonElement Config(object o) => JsonSerializer.SerializeToElement(o);

    private static StepExecutionContext Ctx() => new()
    {
        StepId = "step-1",
        Variables = new Dictionary<string, string>(),
    };

    [Fact]
    public async Task MissingDefinitionReference_FailsCleanly()
    {
        await using var db = TestDbFactory.Create();
        var result = await NewExecutor(db).ExecuteAsync(Ctx(), Config(new { }), CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("definition reference");
    }

    [Fact]
    public async Task UnknownDefinition_FailsCleanly()
    {
        await using var db = TestDbFactory.Create();
        var result = await NewExecutor(db).ExecuteAsync(
            Ctx(), Config(new { __customDefinitionId = Guid.NewGuid().ToString() }), CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task DisabledDefinition_FailsCleanly()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput
        { Key = "k", Name = "K", ScriptTemplate = "x" }, "u", CancellationToken.None); // created disabled

        var result = await NewExecutor(db).ExecuteAsync(
            Ctx(), Config(new { __customDefinitionId = def.Id.ToString(), __customKey = "k" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("disabled");
    }

    [Fact]
    public async Task KeyDrift_FailsCleanly()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput
        { Key = "real_key", Name = "K", ScriptTemplate = "x" }, "u", CancellationToken.None);
        await store.SetEnabledAsync(def.Id, true, "admin", CancellationToken.None);

        var result = await NewExecutor(db).ExecuteAsync(
            Ctx(), Config(new { __customDefinitionId = def.Id.ToString(), __customKey = "stale_key" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("drift");
    }

    [Fact]
    public async Task RunsRemoteWithoutTarget_FailsCleanly()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput
        { Key = "remote_op", Name = "R", ScriptTemplate = "x", RunsRemote = true }, "u", CancellationToken.None);
        await store.SetEnabledAsync(def.Id, true, "admin", CancellationToken.None);

        var result = await NewExecutor(db).ExecuteAsync(
            Ctx(), Config(new { __customDefinitionId = def.Id.ToString(), __customKey = "remote_op" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("requires a target machine");
    }

    [Fact]
    public async Task EnabledDefinition_PublishesOnlyDeclaredOutputsWithDeclaredCasing()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput
        {
            Key = "disk_free_check",
            Name = "Disk Free Check",
            ScriptTemplate = "$FreeGB = 42; $Status = 'ok'; $helper = 'leak'",
            OutputParametersJson = CustomActivityParameters.Serialize([
                new CustomActivityOutputParameter("freeGb", "number"),
                new CustomActivityOutputParameter("status", "string")
            ]),
        }, "u", CancellationToken.None);
        await store.SetEnabledAsync(def.Id, true, "admin", CancellationToken.None);

        var result = await NewExecutor(db).ExecuteAsync(
            Ctx(),
            Config(new { __customDefinitionId = def.Id.ToString(), __customKey = "disk_free_check" }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("freeGb").WhoseValue.Should().Be("42");
        result.OutputParameters.Should().ContainKey("status").WhoseValue.Should().Be("ok");
        result.OutputParameters.Should().ContainKey("exitCode").WhoseValue.Should().Be("0");
        result.OutputParameters.Should().NotContainKey("FreeGB");
        result.OutputParameters.Should().NotContainKey("Status");
        result.OutputParameters.Should().NotContainKey("helper");
    }
}
