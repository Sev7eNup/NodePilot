// Tests covering the activity-hardening pass landed on 2026-05-17.
// Layered alongside the existing per-activity test files; each Fact pinpoints exactly
// one finding from the consolidated security/correctness audit.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Options;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using NodePilot.Engine.Triggers;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Activities;

public sealed class ActivityHardening2026_05_17Tests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;
    private static StepExecutionContext Ctx() => new()
    {
        WorkflowExecutionId = Guid.NewGuid(),
        StepId = "step-1",
        Variables = new(),
    };

    // ----- A1: WaitForCondition rejects {{...}} residue (script-injection guard) -----

    [Fact]
    public async Task A1_WaitForCondition_RejectsTemplateResidueInScript()
    {
        using var db = TestDbContext.Create();
        var session = new Mock<IRemoteSession>();
        var sessionFactory = new Mock<IRemoteSessionFactory>();
        sessionFactory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var credentialStore = new Mock<ICredentialStore>();
        var activity = new WaitForConditionActivity(
            sessionFactory.Object, credentialStore.Object, db,
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            new ConfigurationBuilder().Build());

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"script\": \"Test-Path '{{upstream.output}}'\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("must not contain");
        result.ErrorOutput.Should().Contain("{{");
    }

    // ----- A2: ZipOperation extract embeds a Zip-Slip pre-scan block -----

    [Fact]
    public async Task A2_ZipExtract_ScriptContainsZipSlipGuard()
    {
        using var db = TestDbContext.Create();
        var credentialId = Guid.NewGuid();
        var machineId = Guid.NewGuid();
        db.Credentials.Add(new Credential { Id = credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });
        db.ManagedMachines.Add(new ManagedMachine { Id = machineId, Name = "S", Hostname = "host.local", WinRmPort = 5985, DefaultCredentialId = credentialId, IsReachable = true });
        db.SaveChanges();

        string captured = "";
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((s, _, _) => captured = s)
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "###NODEPILOT_ZIP_RESULT_START###\n{\"operation\":\"extract\",\"destination\":\"C:\\\\out\",\"sizeBytes\":0}\n###NODEPILOT_ZIP_RESULT_END###" });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var sessionFactory = new Mock<IRemoteSessionFactory>();
        sessionFactory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var credentialStore = new Mock<ICredentialStore>();
        credentialStore.Setup(c => c.GetAsync(credentialId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential { Id = credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });

        var activity = new ZipOperationActivity(
            sessionFactory.Object, credentialStore.Object, db,
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            new ConfigurationBuilder().Build());

        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1",
            TargetMachineId = machineId, CredentialId = credentialId,
            Variables = new(),
        };
        await activity.ExecuteAsync(ctx,
            Cfg("{\"operation\":\"extract\",\"source\":\"C:\\\\in.zip\",\"destination\":\"C:\\\\out\"}"),
            CancellationToken.None);

        captured.Should().Contain("Zip-Slip blocked");
        captured.Should().Contain("ZipFile]::OpenRead");
        captured.Should().Contain("StartsWith");
    }

    // ----- A3: RegistryActivity rejects non-registry keyPath -----

    [Theory]
    [InlineData("C:\\Windows")]
    [InlineData("./relative")]
    [InlineData("Env:PATH")]
    [InlineData("not-a-prefix")]
    public async Task A3_Registry_RejectsNonRegistryKeyPath(string keyPath)
    {
        var activity = BuildRegistry(out var db);
        try
        {
            var (machineId, credId) = SeedHost(db);
            var ctx = new StepExecutionContext
            {
                WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1",
                Variables = new(),
                TargetMachineId = machineId,
                CredentialId = credId,
            };
            var cfg = Cfg($"{{\"operation\":\"exists\",\"keyPath\":{JsonSerializer.Serialize(keyPath)}}}");
            Func<Task> act = () => activity.ExecuteAsync(ctx, cfg, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*registry provider path*");
        }
        finally { db.Dispose(); }
    }

    [Theory]
    [InlineData("HKLM:\\SOFTWARE\\Foo")]
    [InlineData("HKCU:\\Console")]
    [InlineData("HKEY_LOCAL_MACHINE\\SYSTEM")]
    [InlineData("Registry::HKEY_CLASSES_ROOT\\CLSID")]
    public async Task A3_Registry_AcceptsRegistryProviderPaths(string keyPath)
    {
        var activity = BuildRegistry(out var db);
        try
        {
            var (machineId, credId) = SeedHost(db);
            var ctx = new StepExecutionContext
            {
                WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1",
                TargetMachineId = machineId, CredentialId = credId,
                Variables = new(),
            };
            var cfg = Cfg($"{{\"operation\":\"exists\",\"keyPath\":{JsonSerializer.Serialize(keyPath)}}}");
            // Should NOT throw the registry-prefix InvalidOperationException; the mock returns
            // a stub result so the call completes normally.
            var result = await activity.ExecuteAsync(ctx, cfg, CancellationToken.None);
            result.Should().NotBeNull();
        }
        finally { db.Dispose(); }
    }

    private static RegistryActivity BuildRegistry(out Data.NodePilotDbContext db)
    {
        db = TestDbContext.Create();
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "###NODEPILOT_REGISTRY_RESULT_START###\n{\"operation\":\"exists\",\"ok\":true,\"exists\":true}\n###NODEPILOT_REGISTRY_RESULT_END###" });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var sessionFactory = new Mock<IRemoteSessionFactory>();
        sessionFactory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var credentialStore = new Mock<ICredentialStore>();
        credentialStore.Setup(c => c.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential { Id = Guid.NewGuid(), Name = "c", Username = "u", EncryptedPassword = new byte[] { 1 } });
        return new RegistryActivity(
            sessionFactory.Object, credentialStore.Object, db,
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            new ConfigurationBuilder().Build());
    }

    private static (Guid machineId, Guid credentialId) SeedHost(Data.NodePilotDbContext db)
    {
        var machineId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        db.Credentials.Add(new Credential { Id = credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });
        db.ManagedMachines.Add(new ManagedMachine { Id = machineId, Name = "S", Hostname = "host.local", WinRmPort = 5985, DefaultCredentialId = credentialId, IsReachable = true });
        db.SaveChanges();
        return (machineId, credentialId);
    }

    // ----- A4: Reserved-prefix detection shared helper -----

    [Theory]
    [InlineData("__callDepth", true)]
    [InlineData("__CALLDEPTH", true)]
    [InlineData("__anything", true)]
    [InlineData("callDepth", false)]
    [InlineData("_single", false)]
    [InlineData("", false)]
    public void A4_WorkflowRecursion_ReservedParameterName(string name, bool expected)
    {
        WorkflowRecursion.IsReservedParameterName(name).Should().Be(expected);
    }

    [Fact]
    public void A4_WorkflowRecursion_FindReservedKey_ReturnsFirstHit()
    {
        var keys = new[] { "userId", "__callDepth", "__other" };
        WorkflowRecursion.FindReservedKey(keys).Should().Be("__callDepth");
    }

    [Fact]
    public void A4_WorkflowRecursion_FindReservedKey_NullOrEmpty()
    {
        WorkflowRecursion.FindReservedKey(null).Should().BeNull();
        WorkflowRecursion.FindReservedKey(new[] { "a", "b" }).Should().BeNull();
    }

    // ----- A5: Junction aggregates only PreviousResults' OutputParameters -----

    [Fact]
    public async Task A5_Junction_DoesNotLeakGlobalsOrManual()
    {
        var prevResult = new ActivityResult
        {
            Success = true,
            Output = "branch-out",
            OutputParameters = new Dictionary<string, string> { ["count"] = "5" },
        };
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "junction-1",
            Variables = new()
            {
                ["globals.STRIPE_KEY"] = "secret_sauce",
                ["manual.userId"] = "u1",
                ["other_step.output"] = "noise",
                ["other_step.param.count"] = "5",
            },
            PreviousResults = new Dictionary<string, ActivityResult> { ["upstream"] = prevResult },
        };

        var activity = new JunctionActivity();
        var result = await activity.ExecuteAsync(ctx, Cfg("{\"mode\":\"waitAll\"}"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("count").WhoseValue.Should().Be("5");
        result.OutputParameters.Should().NotContainKey("STRIPE_KEY");
        result.OutputParameters.Should().NotContainKey("globals.STRIPE_KEY");
        result.OutputParameters.Should().NotContainKey("manual.userId");
        result.OutputParameters.Should().NotContainKey("other_step.output");
        result.Output.Should().NotContain("secret_sauce");
    }

    [Fact]
    public async Task A5_Junction_SkipsReservedPrefixKeysFromUpstream()
    {
        var prevResult = new ActivityResult
        {
            Success = true,
            OutputParameters = new Dictionary<string, string> { ["__callDepth"] = "99", ["userId"] = "u1" },
        };
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(), StepId = "junction-1",
            Variables = new(),
            PreviousResults = new Dictionary<string, ActivityResult> { ["upstream"] = prevResult },
        };
        var activity = new JunctionActivity();
        var result = await activity.ExecuteAsync(ctx, Cfg("{}"), CancellationToken.None);
        result.OutputParameters.Should().ContainKey("userId");
        result.OutputParameters.Should().NotContainKey("__callDepth");
    }

    // ----- B1: ActivityExecution propagates OperationCanceledException -----

    [Fact]
    public async Task B1_ActivityExecution_PropagatesOCE()
    {
        Func<Task> act = () => ActivityExecution.RunAsync(() => throw new OperationCanceledException("user-initiated"));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task B1_ActivityExecution_WrapsOtherExceptions()
    {
        var result = await ActivityExecution.RunAsync(() => throw new InvalidOperationException("boom"));
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Be("boom");
    }

    // ----- B2: ScheduledTask 'get' invokes Get-ScheduledTaskInfo -----

    [Fact]
    public async Task B2_ScheduledTask_GetUsesGetScheduledTaskInfo()
    {
        using var db = TestDbContext.Create();
        var (machineId, credId) = SeedHost(db);
        string captured = "";
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((s, _, _) => captured = s)
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "{}" });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var sessionFactory = new Mock<IRemoteSessionFactory>();
        sessionFactory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var credentialStore = new Mock<ICredentialStore>();
        credentialStore.Setup(c => c.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential { Id = credId, Name = "c", Username = "u", EncryptedPassword = new byte[] { 1 } });
        var activity = new ScheduledTaskActivity(
            sessionFactory.Object, credentialStore.Object, db,
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            new ConfigurationBuilder().Build());

        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "s", TargetMachineId = machineId, CredentialId = credId, Variables = new() };
        await activity.ExecuteAsync(ctx,
            Cfg("{\"action\":\"get\",\"taskName\":\"MyTask\"}"),
            CancellationToken.None);

        captured.Should().Contain("Get-ScheduledTaskInfo");
        captured.Should().Contain("LastRunTime");
        captured.Should().Contain("NextRunTime");
    }

    // ----- B7: ReturnData per-value cap + envelope fail -----

    [Fact]
    public async Task B7_ReturnData_TruncatesPerValueNotEnvelope()
    {
        using var db = TestDbContext.Create();
        var workflowId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf-b7", DefinitionJson = "{}", IsEnabled = true });
        var execId = Guid.NewGuid();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = execId, WorkflowId = workflowId,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = "test",
        });
        db.SaveChanges();
        var activity = new ReturnDataActivity(db);
        var big = new string('x', 16 * 1024);
        var json = JsonSerializer.Serialize(new { data = new { huge = big, small = "ok" } });
        var ctx = new StepExecutionContext { WorkflowExecutionId = execId, StepId = "rd-1", Variables = new() };

        var result = await activity.ExecuteAsync(ctx, Cfg(json), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["huge"].Should().EndWith("…(truncated)");
        result.OutputParameters["huge"].Length.Should().BeLessThan(big.Length);
        result.OutputParameters["small"].Should().Be("ok");
    }

    // ----- C1: SQL default timeout is 60s -----

    [Fact]
    public void C1_Sql_DefaultCommandTimeoutIs60()
    {
        SqlActivity.DefaultCommandTimeoutSeconds.Should().Be(60);
    }

    // ----- C2: StartProgram default timeout is 300s -----

    [Fact]
    public void C2_StartProgram_DefaultTimeoutIs300()
    {
        StartProgramActivity.DefaultTimeoutSeconds.Should().Be(300);
    }

    // ----- C3: StartWorkflow default child timeout is 3600s -----

    [Fact]
    public void C3_StartWorkflow_DefaultChildTimeoutIs3600()
    {
        StartWorkflowActivity.DefaultChildTimeoutSeconds.Should().Be(3600);
    }

    // ----- C4: PowerManagement self-shutdown guard -----

    [Fact]
    public async Task C4_PowerManagement_RejectsLocalSelfShutdownByDefault()
    {
        using var db = TestDbContext.Create();
        var machineId = Guid.NewGuid();
        db.ManagedMachines.Add(new ManagedMachine { Id = machineId, Name = "local", Hostname = "localhost", WinRmPort = 5985, IsReachable = true });
        db.SaveChanges();
        var activity = new PowerManagementActivity(
            new Mock<IRemoteSessionFactory>().Object,
            new Mock<ICredentialStore>().Object,
            db,
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            new ConfigurationBuilder().Build());
        var machine = db.ManagedMachines.Find(machineId)!;
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "s", TargetMachineId = machineId, ResolvedMachine = machine, Variables = new() };

        Func<Task> act = () => activity.ExecuteAsync(ctx, Cfg("{\"action\":\"shutdown\"}"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*local NodePilot host is blocked*");
    }

    [Fact]
    public async Task C4_PowerManagement_AllowsRemoteShutdownEvenWithoutOptIn()
    {
        // Sanity check: the guard ONLY fires for local-self-shutdown. A remote target
        // (non-loopback hostname) with destructive action must NOT be blocked, no matter
        // what the config says. We can't exercise the opt-in branch by actually running
        // shutdown.exe /l against the test agent (that would log out the developer running
        // these tests), so instead we verify the guard does not over-fire on remote
        // targets — the inverse cohort is the negative test above.
        using var db = TestDbContext.Create();
        var (machineId, credId) = SeedHost(db); // hostname "host.local" — non-loopback

        string? captured = null;
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((s, _, _) => captured = s)
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "" });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var sessionFactory = new Mock<IRemoteSessionFactory>();
        sessionFactory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var credentialStore = new Mock<ICredentialStore>();
        credentialStore.Setup(c => c.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential { Id = credId, Name = "c", Username = "u", EncryptedPassword = new byte[] { 1 } });

        var activity = new PowerManagementActivity(
            sessionFactory.Object, credentialStore.Object, db,
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            new ConfigurationBuilder().Build());

        var machine = db.ManagedMachines.Find(machineId)!;
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(), StepId = "s",
            TargetMachineId = machineId, CredentialId = credId,
            ResolvedMachine = machine, Variables = new(),
        };
        var result = await activity.ExecuteAsync(ctx, Cfg("{\"action\":\"shutdown\"}"), CancellationToken.None);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull().And.Subject.Should().Contain("shutdown.exe");
    }

    // ----- D1: Delay clamp -----

    [Theory]
    [InlineData("{\"seconds\":-5}", 0)]
    [InlineData("{\"seconds\":3}", 3)]
    [InlineData("{}", 5)] // default
    [InlineData("{\"seconds\":99999999}", 86400)] // clamped to MaxDelaySeconds
    public void D1_Delay_ReadSecondsClampsAndDefaults(string json, int expected)
    {
        DelayActivity.ReadSeconds(Cfg(json)).Should().Be(expected);
    }

    [Fact]
    public void D1_Delay_NonIntegerSecondsFallsBackToDefault()
    {
        DelayActivity.ReadSeconds(Cfg("{\"seconds\":\"abc\"}")).Should().Be(DelayActivity.DefaultDelaySeconds);
        DelayActivity.ReadSeconds(Cfg("{\"seconds\":null}")).Should().Be(DelayActivity.DefaultDelaySeconds);
    }

    // ----- D5/D6: Email isHtml + timeout -----

    [Fact]
    public async Task D5_Email_isHtmlAcceptsStringTrueWithoutThrowing()
    {
        var options = new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions
        {
            Host = "127.0.0.1", Port = 1, From = "test@local",
        });
        var activity = new EmailActivity(options);
        // Will fail at network connect (no SMTP listener on port 1) but MUST NOT throw
        // InvalidOperationException from GetBoolean on the string "true".
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "s", Variables = new() };
        var result = await activity.ExecuteAsync(ctx,
            Cfg("{\"to\":\"foo@bar.com\",\"subject\":\"x\",\"body\":\"y\",\"isHtml\":\"true\"}"),
            CancellationToken.None);

        // Failure is expected (no SMTP server). We assert the error is NOT the
        // GetBoolean-on-string InvalidOperationException, which would mean the
        // robustness fix is not in place.
        result.Success.Should().BeFalse();
        (result.ErrorOutput ?? string.Empty).Should().NotContain("ValueKind");
    }

    [Fact]
    public void D6_Email_DefaultSmtpTimeoutIs30()
    {
        EmailActivity.DefaultSmtpTimeoutSeconds.Should().Be(30);
    }

    // ----- D3/D4: SQL caps as constants -----

    [Fact]
    public void D3_Sql_MaxRowsReturnedIs1000()
    {
        SqlActivity.MaxRowsReturned.Should().Be(1000);
    }

    [Fact]
    public void D4_Sql_MaxFlatOutputKeysIs200()
    {
        SqlActivity.MaxFlatOutputKeys.Should().Be(200);
    }

    // ----- D7: StartProgram output cap constant -----

    [Fact]
    public void D7_StartProgram_OutputCap1MiB()
    {
        StartProgramActivity.MaxOutputBytesPerStream.Should().Be(1024 * 1024);
    }

    // ----- D9: EventLog scan cap constant -----

    [Fact]
    public void D9_EventLog_MaxEventsScannedConstant()
    {
        EventLogTrigger.MaxEventsToScanPerManualRun.Should().Be(5000);
    }
}
