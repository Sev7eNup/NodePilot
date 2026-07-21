using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Unit tests for the polling loop in <see cref="WaitForConditionActivity"/>. We mock the
/// remote session and have it return deterministic marker output so the test runs without
/// a real PowerShell engine. Goal: pin down the loop logic end-to-end (first match → success,
/// timeout → failure with an "attempts" count) without needing to restart the API process.
/// </summary>
public sealed class WaitForConditionActivityTests : IDisposable
{
    private readonly NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly Mock<IRemoteSession> _session;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RestApi:AllowedHosts:0"] = "db.internal",
            ["RestApi:AllowedHosts:1"] = "api",
            ["RestApi:AllowedHosts:2"] = "x",
        })
        .Build();
    private readonly ManagedMachine _machine;
    private int _invocationCount;
    private readonly List<string> _capturedScripts = new();

    public WaitForConditionActivityTests()
    {
        _db = TestDbContext.Create();
        _credentialStore = new Mock<ICredentialStore>();

        _session = new Mock<IRemoteSession>();
        _session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => { _invocationCount++; _capturedScripts.Add(script); })
            .ReturnsAsync(() => new RemoteExecutionResult { Success = true, Output = "noop" });
        _session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory = new Mock<IRemoteSessionFactory>();
        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_session.Object);

        // A real, non-loopback machine → the activity picks the remote path (session) instead
        // of the localhost bypass. The hostname is deliberately "example.net" so that
        // IsLoopbackHostname doesn't trigger the in-process fallback.
        _machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "TestHost",
            Hostname = "host.example.net",
            WinRmPort = 5985,
            UseSsl = false,
            IsReachable = true,
        };
        _db.ManagedMachines.Add(_machine);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private WaitForConditionActivity CreateActivity()
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);

    private StepExecutionContext CreateContext() => new()
    {
        WorkflowExecutionId = Guid.NewGuid(),
        StepId = "wait-step",
        TargetMachineId = _machine.Id,
        ResolvedMachine = _machine,
    };

    private static JsonElement ParseConfig(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ExecuteAsync_FirstPollReturnsTrue_SucceedsAfterOneAttempt()
    {
        _session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => { _invocationCount++; _capturedScripts.Add(script); })
            .ReturnsAsync(() => new RemoteExecutionResult
            {
                Success = true,
                Output = "###NODEPILOT_COND:True###",
            });

        var activity = CreateActivity();
        var config = ParseConfig("{\"script\":\"$true\",\"intervalSeconds\":1,\"timeoutSeconds\":10}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Condition met after 1 attempt");
        result.OutputParameters["attempts"].Should().Be("1");
        result.OutputParameters["lastResult"].Should().Be("true");
        _invocationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ConditionFlipsAfterThreePolls_SucceedsAtAttemptThree()
    {
        int call = 0;
        _session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => { _invocationCount++; _capturedScripts.Add(script); call++; })
            .ReturnsAsync(() => new RemoteExecutionResult
            {
                Success = true,
                // Three false polls, then true → success on attempt 4.
                Output = call >= 4 ? "###NODEPILOT_COND:True###" : "###NODEPILOT_COND:False###",
            });

        var activity = CreateActivity();
        var config = ParseConfig("{\"script\":\"$x -eq 1\",\"intervalSeconds\":1,\"timeoutSeconds\":20}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["attempts"].Should().Be("4");
    }

    [Fact]
    public async Task ExecuteAsync_AlwaysFalse_TimesOutWithAttemptCount()
    {
        _session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => { _invocationCount++; _capturedScripts.Add(script); })
            .ReturnsAsync(() => new RemoteExecutionResult
            {
                Success = true,
                Output = "###NODEPILOT_COND:False###",
            });

        var activity = CreateActivity();
        // Interval 1s, timeout 3s → expect ~3 attempts (initial poll plus two after sleeping)
        // within the budget, then bail out with a failure.
        var config = ParseConfig("{\"script\":\"$false\",\"intervalSeconds\":1,\"timeoutSeconds\":3}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Timeout after 3s");
        _invocationCount.Should().BeGreaterThanOrEqualTo(2, "at least the initial poll plus one retry within 3s");
        result.OutputParameters["lastResult"].Should().Be("false");
    }

    [Fact]
    public async Task ExecuteAsync_MissingScript_FailsImmediately()
    {
        var activity = CreateActivity();
        var config = ParseConfig("{\"intervalSeconds\":1,\"timeoutSeconds\":5}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("'script' is required");
        _invocationCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoTargetMachine_RunsLocally()
    {
        // Hybrid behavior (matches RunScriptActivity): without a target machine the polling
        // runs locally in the API process instead of failing with "No target machine". We
        // verify that the remote-session mock was NEVER called — the local PowerShell engine
        // evaluates the trivial `$true` expression and reports back via the marker.
        var activity = CreateActivity();
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "wait-step",
            // Deliberately no ResolvedMachine
        };
        var config = ParseConfig("{\"script\":\"$true\",\"intervalSeconds\":1,\"timeoutSeconds\":10}");

        var result = await activity.ExecuteAsync(ctx, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("attempts");
        _invocationCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WrapsUserScriptWithMarkerEmission()
    {
        // The script-wrapping logic matters: the wrapper must emit the ###NODEPILOT_COND:<bool>###
        // output marker, and the user's expression must be wrapped in try/catch — otherwise a
        // syntax error in the user's script would break the whole step.
        var activity = CreateActivity();
        var config = ParseConfig("{\"script\":\"Test-Path 'C:/foo'\",\"intervalSeconds\":1,\"timeoutSeconds\":2}");

        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        _capturedScripts.Should().NotBeEmpty();
        var wrapped = _capturedScripts[0];
        wrapped.Should().Contain("###NODEPILOT_COND:");
        wrapped.Should().Contain("Test-Path 'C:/foo'");
        wrapped.Should().Contain("try {");
        wrapped.Should().Contain("[bool]");
    }

    [Fact]
    public async Task ExecuteAsync_RemotePolls_LeasesAndDisposesSessionPerPoll()
    {
        // Quota hardening (closes an enterprise-scale gap): each poll iteration leases its own
        // session and disposes it before the next poll starts. If we went back to holding a
        // single session for the entire wait duration (the old code), every waitForCondition
        // step would tie up a WinRM shell slot on the target machine for the full timeout (up
        // to 300s) — with MaxShellsPerUser=10, just 10 parallel waits would be enough to lock
        // out all other steps against that host.
        int call = 0;
        _session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((_, _, _) => { _invocationCount++; call++; })
            .ReturnsAsync(() => new RemoteExecutionResult
            {
                Success = true,
                Output = call >= 3 ? "###NODEPILOT_COND:True###" : "###NODEPILOT_COND:False###",
            });

        var activity = CreateActivity();
        var config = ParseConfig("{\"script\":\"$true\",\"intervalSeconds\":1,\"timeoutSeconds\":10}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        _invocationCount.Should().Be(3);
        _sessionFactory.Verify(
            f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "Per-Poll-Session: jede Iteration leased eine eigene Session statt eine über die ganze Dauer zu halten");
        _session.Verify(s => s.DisposeAsync(), Times.Exactly(3),
            "Per-Poll-Session: jede Iteration disposed die Session vor dem Sleep, sonst bleibt der Shell-Slot belegt");
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMidPoll_ReturnsTimeoutShape()
    {
        // When the passed-in CancellationToken fires during the Task.Delay phase, the loop
        // exits cleanly — no exception leaks to the caller.
        _session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RemoteExecutionResult { Success = true, Output = "###NODEPILOT_COND:False###" });

        var activity = CreateActivity();
        var config = ParseConfig("{\"script\":\"$false\",\"intervalSeconds\":2,\"timeoutSeconds\":30}");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var result = await activity.ExecuteAsync(CreateContext(), config, cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Timeout after 30s", "we bail out on cancel with the same failure shape");
    }

    // ---- Typed sub-modes (added 2026-05-17 — closes the "dynamic path doesn't work in the
    //      script field" gap). The script-field stays protected from {{...}} resolution, but the
    //      sub-mode fields (path/serviceName/host/port/url) are normal config and can be
    //      driven by upstream outputs safely because we PowerShell-quote the values
    //      ourselves. The unit tests pin the expression shape so an accidental refactor
    //      can't silently break the contract these fixtures rely on.

    [Fact]
    public void BuildConditionExpression_Default_TreatsAsScriptMode()
    {
        var expr = WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"script\":\"$true\"}"));
        expr.Should().Be("$true");
    }

    [Fact]
    public void BuildConditionExpression_ScriptMode_RejectsTemplateResidue()
    {
        // The original guard moves into the builder. Authors who try to slip a
        // {{...}} into `script` must be pointed at the typed sub-modes instead of
        // getting silent injection.
        Action act = () => WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"script\":\"Test-Path {{prep.param.flagPath}}\"}"));
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*must not contain {{...}} templates*pathExists*");
    }

    [Fact]
    public void BuildConditionExpression_PathExists_QuotesPathLiteral()
    {
        // Real fixture scenario: an upstream runScript step emits a flag path, the
        // engine resolves {{prep.param.flagPath}} to "C:\\Temp\\flag.txt", and we
        // build a safe Test-Path call. -LiteralPath so wildcard chars in the value
        // don't trigger glob behaviour.
        var expr = WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"pathExists\",\"path\":\"C:\\\\Temp\\\\flag.txt\"}"));
        expr.Should().Be("Test-Path -LiteralPath 'C:\\Temp\\flag.txt'");
    }

    [Fact]
    public void BuildConditionExpression_PathExists_ApostropheInPath_Escaped()
    {
        // Defence-in-depth: even if the upstream output had an apostrophe in it,
        // PowerShellQuoter doubles it so the single-quoted literal stays intact.
        var expr = WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"pathExists\",\"path\":\"C:\\\\Users\\\\o'brian\\\\flag\"}"));
        expr.Should().Be("Test-Path -LiteralPath 'C:\\Users\\o''brian\\flag'");
    }

    [Fact]
    public void BuildConditionExpression_PathExists_MissingPath_Throws()
    {
        Action act = () => WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"pathExists\"}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*'path' is required*");
    }

    [Fact]
    public void BuildConditionExpression_ServiceRunning_QuotesServiceName()
    {
        var expr = WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"serviceRunning\",\"serviceName\":\"Spooler\"}"));
        expr.Should().Contain("Get-Service -Name 'Spooler'");
        expr.Should().Contain(".Status -eq 'Running'");
    }

    [Fact]
    public void BuildConditionExpression_ServiceRunning_MissingServiceName_Throws()
    {
        Action act = () => WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"serviceRunning\"}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*'serviceName' is required*");
    }

    [Fact]
    public void BuildConditionExpression_PortOpen_BuildsBoundedTcpClientProbe()
    {
        var expr = WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"portOpen\",\"host\":\"db.internal\",\"port\":5432}"));
        expr.Should().Contain("System.Net.Sockets.TcpClient");
        expr.Should().Contain("ConnectAsync('db.internal', 5432)");
        expr.Should().Contain(".Wait(1500)");
        expr.Should().Contain("$__c.Close()");
    }

    [Fact]
    public void BuildConditionExpression_PortOpen_MissingHost_Throws()
    {
        Action act = () => WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"portOpen\",\"port\":443}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*'host' is required*");
    }

    [Fact]
    public void BuildConditionExpression_PortOpen_MissingPort_Throws()
    {
        Action act = () => WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"portOpen\",\"host\":\"db.internal\"}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*'port'*required*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(99999)]
    public void BuildConditionExpression_PortOpen_OutOfRange_Throws(int port)
    {
        Action act = () => WaitForConditionActivity.BuildConditionExpression(
            ParseConfig($"{{\"conditionType\":\"portOpen\",\"host\":\"x\",\"port\":{port}}}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*'port' must be 1..65535*");
    }

    [Fact]
    public void BuildConditionExpression_HttpOk_BuildsTryCatchAroundInvokeWebRequest()
    {
        // The try/catch is important: a connection refused / TLS error must surface as
        // $false (keep polling) rather than bubble up and crash the [bool] cast.
        var expr = WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"httpOk\",\"url\":\"https://api/health\"}"));
        expr.Should().Contain("Invoke-WebRequest -Uri 'https://api/health'");
        expr.Should().Contain("StatusCode -ge 200");
        expr.Should().Contain("StatusCode -lt 300");
        expr.Should().Contain("try {");
        expr.Should().Contain("catch {");
        expr.Should().Contain("$false");
        expr.Should().Contain("-MaximumRedirection 0");
    }

    [Theory]
    [InlineData("{\"conditionType\":\"portOpen\",\"host\":\"127.0.0.1\",\"port\":80}")]
    [InlineData("{\"conditionType\":\"httpOk\",\"url\":\"https://8.8.8.8/health\"}")]
    public async Task ExecuteAsync_NetworkTargetOutsideStaticAllowlist_FailsClosed(string json)
    {
        var result = await CreateActivity().ExecuteAsync(CreateContext(), ParseConfig(json), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("explicitly allowed");
        _invocationCount.Should().Be(0);
    }

    [Fact]
    public void BuildConditionExpression_HttpOk_WrapsTryCatchInScriptBlockCall()
    {
        // PS regression guard: try/catch is a STATEMENT, not an expression — embedding
        // it bare into `[bool](...)` is a parser error. The fix wraps in `& { ... }`,
        // turning the script-block call into an expression whose last-evaluated value
        // is the bool we want. This test pins that wrap so a refactor can't silently
        // re-introduce the broken `(try { ... } catch { ... })` form.
        var expr = WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"httpOk\",\"url\":\"https://x\"}"));
        expr.Should().Contain("& {",
            "try/catch must be invoked via the call operator so the outer [bool](...) cast sees an expression");
        expr.Should().NotMatchRegex(@"\(\s*try\s*\{",
            "the bare `(try { ... }` form is a PS parser error and must never reappear");
    }

    [Fact]
    public void BuildConditionExpression_AllTypedModes_ParseAsValidPowerShellExpressions()
    {
        // End-to-end syntax guard: build every typed sub-mode's expression, wrap it in
        // the same `[bool](...)` shell the activity uses at runtime, and ask the real
        // PS parser if the result is syntactically valid. This catches BOTH the httpOk
        // try/catch regression AND any future variant where someone embeds a statement
        // in an expression context. Pure parser check — no execution — so it runs fast
        // and doesn't need WinRM / a remote target.
        var configs = new[]
        {
            "{\"conditionType\":\"script\",\"script\":\"$true\"}",
            "{\"conditionType\":\"pathExists\",\"path\":\"C:\\\\flag\"}",
            "{\"conditionType\":\"serviceRunning\",\"serviceName\":\"Spooler\"}",
            "{\"conditionType\":\"portOpen\",\"host\":\"db\",\"port\":5432}",
            "{\"conditionType\":\"httpOk\",\"url\":\"https://api/h\"}",
        };

        foreach (var raw in configs)
        {
            var expr = WaitForConditionActivity.BuildConditionExpression(ParseConfig(raw));
            // Mirror the runtime wrap: outer wrapper casts to bool.
            var wrapped = $"[bool]({expr})";

            System.Management.Automation.Language.Token[] _;
            System.Management.Automation.Language.ParseError[] errors;
            System.Management.Automation.Language.Parser.ParseInput(wrapped, out _, out errors);

            errors.Should().BeEmpty(
                $"sub-mode `{raw}` produced expression `{expr}` which the PS parser rejected: " +
                string.Join("; ", errors.Select(e => e.Message)));
        }
    }

    [Fact]
    public void BuildConditionExpression_HttpOk_MissingUrl_Throws()
    {
        Action act = () => WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"httpOk\"}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*'url' is required*");
    }

    [Fact]
    public void BuildConditionExpression_UnknownConditionType_Throws()
    {
        // Typo guard — a `conditionType: "patHexists"` (one character off) shouldn't
        // silently fall back to script mode and then complain about a missing `script`.
        Action act = () => WaitForConditionActivity.BuildConditionExpression(
            ParseConfig("{\"conditionType\":\"bogusMode\",\"path\":\"x\"}"));
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*unknown conditionType 'bogusMode'*");
    }

    [Fact]
    public async Task ExecuteAsync_PathExistsMode_RemotePollUsesTestPathScript()
    {
        // End-to-end smoke: the pathExists sub-mode flows through ExecuteAsync, gets
        // wrapped in the [bool] cast + marker emission, and emits Test-Path on the
        // remote session — proving the new mode is wired all the way through.
        _session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => { _invocationCount++; _capturedScripts.Add(script); })
            .ReturnsAsync(() => new RemoteExecutionResult { Success = true, Output = "###NODEPILOT_COND:True###" });

        var activity = CreateActivity();
        var config = ParseConfig("{\"conditionType\":\"pathExists\",\"path\":\"C:\\\\flag.txt\",\"intervalSeconds\":1,\"timeoutSeconds\":5}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        _capturedScripts.Should().NotBeEmpty();
        _capturedScripts[0].Should().Contain("Test-Path -LiteralPath 'C:\\flag.txt'");
        _capturedScripts[0].Should().Contain("###NODEPILOT_COND:");
    }
}
