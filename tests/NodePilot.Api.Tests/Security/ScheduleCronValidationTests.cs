using FluentAssertions;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Quartz-accurate cron validation at publish/enable. Three surfaces answered "is this cron
/// usable?" and only the scheduler source — which the author never sees — used Quartz. The node
/// executor accepted any non-blank string and the designer preview parses Unix cron, so the
/// natural 6-field expression "0 0 2 * * *" was green everywhere, failed registration inside the
/// orchestrator's silent backoff, and left a workflow that displayed itself as active and never
/// fired.
/// </summary>
public class ScheduleCronValidationTests
{
    private static string Definition(string cron) =>
        "{\"nodes\":[{\"id\":\"t1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},"
        + "\"data\":{\"activityType\":\"scheduleTrigger\",\"config\":{\"cronExpression\":\""
        + cron + "\"}}}],\"edges\":[]}";

    [Theory]
    [InlineData("0 0 2 * * ?")]
    [InlineData("0 */5 * * * ?")]
    [InlineData("0 0 2 ? * MON-FRI")]
    [InlineData("0 0 2 * * ? *")]
    public void ValidateDefinition_QuartzAcceptableExpression_IsAccepted(string cron)
        => ScheduleCronValidation.ValidateDefinition(Definition(cron)).Should().BeNull();

    [Theory]
    // Both day-of-month and day-of-week specified — Quartz requires exactly one to be "?".
    [InlineData("0 0 2 * * *")]
    // Unix 5-field form.
    [InlineData("0 2 * * *")]
    [InlineData("not a cron")]
    public void ValidateDefinition_ExpressionQuartzRejects_IsReported(string cron)
    {
        var error = ScheduleCronValidation.ValidateDefinition(Definition(cron));

        error.Should().NotBeNull();
        error.Should().Contain("t1").And.Contain(cron);
    }

    [Fact]
    public void ValidateDefinition_NoCronConfigured_IsNotAnError()
    {
        const string definition =
            """
            {"nodes":[{"id":"t1","type":"activity","position":{"x":0,"y":0},
              "data":{"activityType":"scheduleTrigger","config":{}}}],"edges":[]}
            """;

        ScheduleCronValidation.ValidateDefinition(definition).Should().BeNull(
            "an empty cron is the structural validator's business, not this one's");
    }

    [Fact]
    public void ValidateDefinition_MalformedDefinition_IsLeftToStructuralValidation()
        => ScheduleCronValidation.ValidateDefinition("not json").Should().BeNull();

    [Fact]
    public void ValidateDefinition_NonScheduleTrigger_IsIgnored()
    {
        const string definition =
            """
            {"nodes":[{"id":"t1","type":"activity","position":{"x":0,"y":0},
              "data":{"activityType":"webhookTrigger","config":{"cronExpression":"nonsense"}}}],"edges":[]}
            """;

        ScheduleCronValidation.ValidateDefinition(definition).Should().BeNull();
    }
}