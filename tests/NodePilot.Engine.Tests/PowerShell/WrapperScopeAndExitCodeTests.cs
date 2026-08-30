using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Behavioural cover for the two wrapper defects that only show up on a real, reused runspace:
/// a stale <c>$LASTEXITCODE</c> bleeding across invocations, and injected upstream parameters
/// being re-exported as the step's own output.
///
/// <para>The pool is pinned to a single runspace so "the next script gets the same runspace" is
/// guaranteed rather than likely — with the default pool the leak reproduces only sometimes.</para>
/// </summary>
public sealed class WrapperScopeAndExitCodeTests : IDisposable
{
    private readonly RunspaceExecutionEngine _engine =
        new(NullLogger.Instance, minRunspaces: 1, maxRunspaces: 1);

    public void Dispose() => _engine.Dispose();

    private async Task<(Dictionary<string, string> Params, string Output, bool Success)> RunAsync(
        string script, Dictionary<string, string>? parameters = null)
    {
        var result = await _engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = script,
                Parameters = parameters ?? new Dictionary<string, string>(),
            },
            TestContext.Current.CancellationToken);

        var (clean, _, captured) = PowerShellActivitySupport.ExtractMarkers(
            result.Output, "step-1", NullLogger.Instance);
        return (captured, clean, result.Success);
    }

    // --- Defect 1: $LASTEXITCODE across invocations ---------------------------------

    [Fact]
    public async Task ExitCode_FromAnEarlierScript_DoesNotBleedIntoTheNextOne()
    {
        var first = await RunAsync("cmd /c exit 3");
        first.Params["exitCode"].Should().Be("3", "the native command in this script set it");

        var second = await RunAsync("$marker = 'no native command here'");
        second.Params["exitCode"].Should().Be(
            "0", "a script that runs no native command must not inherit the previous one's exit code");
    }

    [Fact]
    public async Task ExitCode_OfThisScriptsOwnNativeCommand_IsStillReported()
    {
        await RunAsync("cmd /c exit 3");

        var result = await RunAsync("cmd /c exit 7");
        result.Params["exitCode"].Should().Be("7");
    }

    // --- Defect 2: injected parameters are not this step's output -------------------

    [Fact]
    public async Task InjectedParameter_ReadButNotAssigned_IsNotCaptured()
    {
        var result = await RunAsync(
            "$mine = $hostName + '-derived'",
            new Dictionary<string, string> { ["prev.param.hostName"] = "web01", ["hostName"] = "web01" });

        result.Params.Should().ContainKey("mine").WhoseValue.Should().Be("web01-derived",
            "the injected value must still be readable");
        result.Params.Should().NotContainKey("hostName",
            "an upstream parameter this script only read is not its own output");
    }

    [Fact]
    public async Task InjectedParameter_Reassigned_IsCapturedWithTheNewValue()
    {
        var result = await RunAsync(
            "$hostName = 'CHANGED'",
            new Dictionary<string, string> { ["hostName"] = "web01" });

        result.Params.Should().ContainKey("hostName").WhoseValue.Should().Be("CHANGED");
    }

    [Fact]
    public async Task InjectedParameter_ReassignedWithADifferentCaseOnly_IsCaptured()
    {
        // A value comparison would have dropped this: PowerShell's -eq is case-insensitive.
        var result = await RunAsync(
            "$mode = 'PROD'",
            new Dictionary<string, string> { ["mode"] = "prod" });

        result.Params.Should().ContainKey("mode").WhoseValue.Should().Be("PROD");
    }

    [Fact]
    public async Task InjectedParameter_ReassignedWithTheIdenticalValue_IsStillCaptured()
    {
        // The scope split reports what the script assigned, not how the value differs — so
        // `$item = {{prev.param.item}}`, a real authoring pattern, keeps publishing its output.
        var result = await RunAsync(
            "$item = 'same'",
            new Dictionary<string, string> { ["item"] = "same" });

        result.Params.Should().ContainKey("item").WhoseValue.Should().Be("same");
    }

    [Fact]
    public async Task InjectedParameters_DoNotAccumulateAcrossAChainOfSteps()
    {
        // What a step captures is what the next step receives, so an over-wide sweep compounds.
        var upstream = new Dictionary<string, string>
        {
            ["janitorSweep"] = "ok",
            ["engineUsed"] = "runspace",
            ["hostName"] = "web01",
            ["exitCode"] = "0",
        };

        var result = await RunAsync("$onlyMine = 'yes'", upstream);

        result.Params.Keys.Should().BeEquivalentTo(["onlyMine", "exitCode"],
            "only this script's own variable plus the always-present exit code");
    }

    // --- Ambiguous published names --------------------------------------------------

    [Fact]
    public async Task TwoActivitiesPublishingTheSameName_BindNoShortVariable()
    {
        // VariableResolver withholds the unqualified entry when more than one activity publishes
        // the name; the wrapper must not reinvent it by flattening the qualified keys, or the
        // winner would be dictionary order again.
        var result = await RunAsync(
            "$seen = if ($null -eq $hostName) { 'unbound' } else { $hostName }",
            new Dictionary<string, string>
            {
                ["stepA.param.hostName"] = "web01",
                ["stepB.param.hostName"] = "web02",
            });

        result.Params["seen"].Should().Be("unbound",
            "an ambiguous name has no owner, so it is not bound at all");
    }

    [Fact]
    public async Task AnAmbiguousValue_StaysReachableUnderItsOwnersQualifiedKey()
    {
        var result = await RunAsync(
            "$fromA = $Params['stepA.param.hostName']\n$fromB = $Params['stepB.param.hostName']",
            new Dictionary<string, string>
            {
                ["stepA.param.hostName"] = "web01",
                ["stepB.param.hostName"] = "web02",
            });

        result.Params["fromA"].Should().Be("web01");
        result.Params["fromB"].Should().Be("web02", "both publishers keep their own entry");
    }

    [Fact]
    public async Task AnUnambiguousNameKeepsItsShortVariable()
    {
        var result = await RunAsync(
            "$seen = $hostName",
            new Dictionary<string, string>
            {
                ["stepA.param.hostName"] = "web01",
                ["hostName"] = "web01",
            });

        result.Params["seen"].Should().Be("web01");
    }

    [Fact]
    public async Task AGlobalAndATriggerInputSharingAName_BindNeither()
    {
        var result = await RunAsync(
            "$seen = if ($null -eq $FOO) { 'unbound' } else { $FOO }",
            new Dictionary<string, string> { ["globals.FOO"] = "g", ["manual.FOO"] = "m" });

        result.Params["seen"].Should().Be("unbound",
            "the collision crosses namespaces, but it is still a collision");
    }

    // --- Automatic and preference variables ----------------------------------------

    [Fact]
    public async Task ForeachStatement_DoesNotPublishTheAutomaticForeachVariable()
    {
        // A `foreach` statement is what creates $foreach; ForEach-Object does not.
        var result = await RunAsync("foreach ($x in 1..2) { $sum = $x }");

        result.Params.Should().ContainKey("sum");
        result.Params.Should().NotContainKey("foreach");
        result.Params.Should().ContainKey("x", "the loop variable is the script's own");
    }

    [Fact]
    public async Task MatchOperator_DoesNotPublishTheAutomaticMatchesVariable()
    {
        var result = await RunAsync("$hit = 'abc' -match 'a(b)c'");

        result.Params.Should().ContainKey("hit");
        result.Params.Should().NotContainKey("Matches");
    }

    [Fact]
    public async Task UpstreamParameterNamedError_DoesNotShadowThePowerShellErrorCollection()
    {
        var result = await RunAsync(
            "$errorType = $Error.GetType().Name\n$fromParams = $Params['prev.param.error']",
            new Dictionary<string, string> { ["prev.param.error"] = "boom" });

        result.Params["errorType"].Should().Be("ArrayList",
            "$Error must still be PowerShell's error collection, not an injected string");
        result.Params["fromParams"].Should().Be("boom",
            "a step-scoped value stays reachable under its owner's qualified key");
        result.Params.Should().NotContainKey("error");
    }

    [Fact]
    public async Task UpstreamParameterNamedLikeAPreferenceVariable_DoesNotChangeScriptBehaviour()
    {
        var result = await RunAsync(
            "$effective = $VerbosePreference.ToString()",
            new Dictionary<string, string> { ["VerbosePreference"] = "Continue" });

        result.Params["effective"].Should().Be("SilentlyContinue",
            "an upstream value must not rewrite how every cmdlet in the script behaves");
        result.Params.Should().NotContainKey("VerbosePreference");
    }

    [Theory]
    [InlineData("__npReserved")]
    [InlineData("__npBuiltinVars")]
    [InlineData("__npOut")]
    public async Task UpstreamParameterNamedLikeAWrapperInternal_DoesNotBreakTheScript(string name)
    {
        // The key grammar allows these, and binding one would overwrite a HashSet with a string.
        var result = await RunAsync(
            $"$viaParams = $Params['prev.param.{name}']",
            new Dictionary<string, string> { [$"prev.param.{name}"] = "hijack" });

        result.Success.Should().BeTrue();
        result.Params["viaParams"].Should().Be("hijack");
        result.Params.Should().NotContainKey(name);
    }

    [Fact]
    public async Task ErrorCollection_IsEmptyAtTheStartOfTheScript()
    {
        await RunAsync("try { Get-Item 'C:\\nope-does-not-exist' -ErrorAction Stop } catch { }");

        var result = await RunAsync("$errorCount = $Error.Count.ToString()");
        result.Params["errorCount"].Should().Be("0",
            "errors from an earlier script in the same pooled runspace must not be visible");
    }
}
