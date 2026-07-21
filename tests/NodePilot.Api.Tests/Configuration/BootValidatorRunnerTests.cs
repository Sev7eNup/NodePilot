using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

/// <summary>
/// Runner-level behaviour: aggregation, error-vs-warning routing, the auto-discovery
/// pipeline finds the production validators. Individual validator rules are covered
/// by their own test fixtures.
/// </summary>
public class BootValidatorRunnerTests
{
    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    private sealed class FixedValidator : IBootValidator
    {
        public string Name { get; }
        private readonly BootValidationIssue[] _emit;
        public FixedValidator(string name, params BootValidationIssue[] emit)
        {
            Name = name;
            _emit = emit;
        }
        public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
        {
            foreach (var e in _emit) issues.Add(e);
        }
    }

    [Fact]
    public void RunAll_NoIssues_ReturnsEmpty()
    {
        var v = new FixedValidator("OK");
        var result = BootValidatorRunner.RunAll(EmptyConfig(), new[] { v });
        result.Should().BeEmpty();
    }

    [Fact]
    public void RunAll_WarningOnly_LogsButDoesNotThrow()
    {
        var v = new FixedValidator("WarnTest",
            new BootValidationIssue("WarnTest", BootValidationSeverity.Warning, "Some:Key", "heads up"));
        var logged = new List<string>();
        var result = BootValidatorRunner.RunAll(EmptyConfig(), new[] { v }, msg => logged.Add(msg));
        result.Should().HaveCount(1);
        logged.Should().ContainSingle(s => s.Contains("WarnTest") && s.Contains("Some:Key") && s.Contains("heads up"));
    }

    [Fact]
    public void RunAll_AnyError_Throws_AggregatesAll()
    {
        var v1 = new FixedValidator("V1",
            new BootValidationIssue("V1", BootValidationSeverity.Error, "K1", "broken thing 1"));
        var v2 = new FixedValidator("V2",
            new BootValidationIssue("V2", BootValidationSeverity.Error, "K2", "broken thing 2"));

        var act = () => BootValidatorRunner.RunAll(EmptyConfig(), new[] { v1, v2 });

        // The whole point of the runner — operators see every fix in one error rather
        // than fix-restart-fix-restart per individual throw site.
        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("K1") && e.Message.Contains("K2")
                     && e.Message.Contains("broken thing 1") && e.Message.Contains("broken thing 2"),
                "the aggregated error must list every offending key + reason so a single restart cycle fixes the whole misconfiguration");
    }

    [Fact]
    public void RunAll_WarningsAndErrors_ErrorWinsAndWarningsAreStillLogged()
    {
        var v = new FixedValidator("Mixed",
            new BootValidationIssue("Mixed", BootValidationSeverity.Warning, "WK", "soft"),
            new BootValidationIssue("Mixed", BootValidationSeverity.Error, "EK", "hard"));
        var logged = new List<string>();
        var act = () => BootValidatorRunner.RunAll(EmptyConfig(), new[] { v }, msg => logged.Add(msg));
        act.Should().Throw<InvalidOperationException>().WithMessage("*EK*");
        logged.Should().ContainSingle(s => s.Contains("WK"),
            "warnings emitted by the same validator must still surface to the log even when a sibling error aborts the boot");
    }

    [Fact]
    public void RunAll_NullArgs_Throw()
    {
        var act1 = () => BootValidatorRunner.RunAll(null!);
        act1.Should().Throw<ArgumentNullException>();
        var act2 = () => BootValidatorRunner.RunAll(EmptyConfig(), validators: null!);
        act2.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DiscoverValidators_FindsProductionValidators_InApiAssembly()
    {
        var validators = BootValidatorRunner.DiscoverValidators(typeof(NodePilot.Api.Controllers.AdminSettingsController).Assembly);
        validators.Select(v => v.Name).Should().Contain(new[] { "Cluster", "SecretsConsistency", "LoggingFormat", "LlmConfig" },
            "auto-discovery is what guarantees a new validator under Configuration/Validators is run on every boot " +
            "without needing an explicit DI registration");
    }

    [Fact]
    public void RunAll_DefaultOverload_ExercisesAutoDiscovery()
    {
        // Empty config — no cluster, no exotic logging format, no secrets settings — must
        // pass with no errors. Catches regressions where a default validator falsely flags
        // an out-of-the-box configuration as broken.
        var result = BootValidatorRunner.RunAll(EmptyConfig());
        result.Where(i => i.Severity == BootValidationSeverity.Error).Should().BeEmpty(
            "an out-of-the-box config without overrides must boot — error-only findings here would silently brick fresh installs");
    }
}
