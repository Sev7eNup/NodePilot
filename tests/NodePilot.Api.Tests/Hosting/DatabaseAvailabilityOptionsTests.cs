using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Configuration;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class DatabaseAvailabilityOptionsTests
{
    [Fact]
    public void FromConfiguration_UnsetValues_UseSafePositiveDefaults()
    {
        var options = DatabaseAvailabilityOptions.FromConfiguration(
            new ConfigurationBuilder().Build());

        options.ConnectTimeoutSeconds.Should().Be(5);
        options.ProbeConnectTimeoutSeconds.Should().Be(2);
        options.ProbeCommandTimeoutSeconds.Should().Be(2);
        options.CleanupTimeoutSeconds.Should().Be(2);
        options.IdleIntervalSeconds.Should().Be(5);
        options.OutageIntervalSeconds.Should().Be(5);
        options.SuccessesToRecover.Should().Be(2);
        options.FailureThreshold.Should().Be(2);
        options.ReadinessTimeoutSeconds.Should().Be(5);
        options.AuthReadTimeoutSeconds.Should().Be(3);
    }

    [Theory]
    [InlineData("Database:ConnectTimeoutSeconds", "0")]
    [InlineData("Database:Probe:ConnectTimeoutSeconds", "-1")]
    [InlineData("Database:Probe:CommandTimeoutSeconds", "0")]
    [InlineData("Database:Probe:CleanupTimeoutSeconds", "0")]
    [InlineData("Database:Probe:IdleIntervalSeconds", "0")]
    [InlineData("Database:Probe:OutageIntervalSeconds", "0")]
    [InlineData("Database:Probe:SuccessesToRecover", "0")]
    [InlineData("Database:Probe:FailureThreshold", "not-a-number")]
    [InlineData("Database:ReadinessProbeTimeoutSeconds", "-2")]
    [InlineData("Database:AuthReadTimeoutSeconds", "0")]
    public void Validator_NonPositiveOrInvalidAvailabilityValue_IsRejected(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = value,
            })
            .Build();
        var issues = new List<BootValidationIssue>();

        new DatabaseAvailabilityOptionsBootValidator().Validate(configuration, issues);

        issues.Should().ContainSingle(issue =>
            issue.Severity == BootValidationSeverity.Error
            && issue.ConfigKey == key);
    }

    [Fact]
    public void FromConfiguration_PositiveAvailabilityValues_AreAcceptedAndMapped()
    {
        var values = new Dictionary<string, string?>
        {
            [DatabaseAvailabilityOptions.ConnectTimeoutKey] = "1",
            [DatabaseAvailabilityOptions.ProbeConnectTimeoutKey] = "2",
            [DatabaseAvailabilityOptions.ProbeCommandTimeoutKey] = "3",
            [DatabaseAvailabilityOptions.CleanupTimeoutKey] = "4",
            [DatabaseAvailabilityOptions.IdleIntervalKey] = "5",
            [DatabaseAvailabilityOptions.OutageIntervalKey] = "6",
            [DatabaseAvailabilityOptions.SuccessesToRecoverKey] = "7",
            [DatabaseAvailabilityOptions.FailureThresholdKey] = "8",
            [DatabaseAvailabilityOptions.ReadinessTimeoutKey] = "9",
            [DatabaseAvailabilityOptions.AuthReadTimeoutKey] = "10",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var issues = new List<BootValidationIssue>();

        new DatabaseAvailabilityOptionsBootValidator().Validate(configuration, issues);
        var options = DatabaseAvailabilityOptions.FromConfiguration(configuration);

        issues.Should().BeEmpty();
        options.ConnectTimeoutSeconds.Should().Be(1);
        options.ProbeConnectTimeoutSeconds.Should().Be(2);
        options.ProbeCommandTimeoutSeconds.Should().Be(3);
        options.CleanupTimeoutSeconds.Should().Be(4);
        options.IdleIntervalSeconds.Should().Be(5);
        options.OutageIntervalSeconds.Should().Be(6);
        options.SuccessesToRecover.Should().Be(7);
        options.FailureThreshold.Should().Be(8);
        options.ReadinessTimeoutSeconds.Should().Be(9);
        options.AuthReadTimeoutSeconds.Should().Be(10);
    }

    [Fact]
    public void BootValidation_MultipleInvalidValues_AreReportedTogetherBeforeMaterialization()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DatabaseAvailabilityOptions.ProbeCommandTimeoutKey] = "not-a-number",
                [DatabaseAvailabilityOptions.ReadinessTimeoutKey] = "0",
                [DatabaseAvailabilityOptions.AuthReadTimeoutKey] = "-3",
            })
            .Build();

        var act = () => BootValidatorRunner.RunAll(
            configuration,
            [new DatabaseAvailabilityOptionsBootValidator()]);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain(DatabaseAvailabilityOptions.ProbeCommandTimeoutKey);
        exception.Message.Should().Contain(DatabaseAvailabilityOptions.ReadinessTimeoutKey);
        exception.Message.Should().Contain(DatabaseAvailabilityOptions.AuthReadTimeoutKey);
    }

    [Fact]
    public void RegisteredOptions_AreOneImmutableBootSnapshotAcrossConfigurationReload()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DatabaseAvailabilityOptions.ConnectTimeoutKey] = "7",
                [DatabaseAvailabilityOptions.ReadinessTimeoutKey] = "9",
                [DatabaseAvailabilityOptions.AuthReadTimeoutKey] = "11",
            })
            .Build();
        BootValidatorRunner.RunAll(
            configuration,
            [new DatabaseAvailabilityOptionsBootValidator()]);
        var materialized = DatabaseAvailabilityOptions.FromConfiguration(configuration);
        var services = new ServiceCollection();
        services.AddSingleton(materialized);
        using var provider = services.BuildServiceProvider();

        configuration[DatabaseAvailabilityOptions.ConnectTimeoutKey] = "17";
        configuration[DatabaseAvailabilityOptions.ReadinessTimeoutKey] = "19";
        configuration[DatabaseAvailabilityOptions.AuthReadTimeoutKey] = "21";
        configuration.Reload();

        var resolved = provider.GetRequiredService<DatabaseAvailabilityOptions>();
        resolved.Should().BeSameAs(materialized);
        resolved.ConnectTimeoutSeconds.Should().Be(7);
        resolved.ReadinessTimeoutSeconds.Should().Be(9);
        resolved.AuthReadTimeoutSeconds.Should().Be(11);
    }
}
