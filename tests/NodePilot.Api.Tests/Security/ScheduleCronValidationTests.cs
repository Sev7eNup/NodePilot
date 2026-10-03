using FluentAssertions;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Publish/enable validation must agree with the Quartz parser the scheduler uses: an expression
/// the designer's Unix-cron preview accepts but Quartz rejects would otherwise look valid and
/// never fire.
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
    // Quartz 4 accepts both day fields as '*', which Quartz 3 rejected.
    [InlineData("0 0 2 * * *")]
    public void ValidateDefinition_QuartzAcceptableExpression_IsAccepted(string cron)
        => ScheduleCronValidation.ValidateDefinition(Definition(cron)).Should().BeNull();

    [Theory]
    // Unix 5-field form.
    [InlineData("0 2 * * *")]
    // Zero step width: accepted by Quartz 3, rejected by Quartz 4.
    [InlineData("0 0/0 * * * ?")]
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