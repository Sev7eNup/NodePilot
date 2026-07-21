using System.Text.Json;
using FluentAssertions;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

public class RetryPolicyTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void Parse_MissingRetryBlock_ReturnsDisabled()
    {
        var p = RetryPolicy.Parse(Json("{}"));
        p.Should().Be(RetryPolicy.Disabled);
        p.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Parse_ValidExponentialConfig_RoundTripsAllFields()
    {
        var p = RetryPolicy.Parse(Json("""{"retry":{"maxAttempts":4,"backoff":"exponential","initialDelayMs":200,"maxDelayMs":5000}}"""));
        p.MaxAttempts.Should().Be(4);
        p.Backoff.Should().Be(RetryBackoff.Exponential);
        p.InitialDelayMs.Should().Be(200);
        p.MaxDelayMs.Should().Be(5000);
        p.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Parse_MaxAttemptsClampedToSafeRange()
    {
        RetryPolicy.Parse(Json("""{"retry":{"maxAttempts":9999}}""")).MaxAttempts.Should().Be(20);
        RetryPolicy.Parse(Json("""{"retry":{"maxAttempts":-5}}""")).MaxAttempts.Should().Be(1);
    }

    [Fact]
    public void Parse_UnknownBackoff_FallsBackToFixed()
    {
        RetryPolicy.Parse(Json("""{"retry":{"maxAttempts":2,"backoff":"random-walk"}}""")).Backoff
            .Should().Be(RetryBackoff.Fixed);
    }

    [Fact]
    public void DelayFor_FirstAttempt_IsZero()
    {
        var p = new RetryPolicy(3, RetryBackoff.Exponential, 1000, 60_000);
        p.DelayFor(1).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void DelayFor_FixedBackoff_IsConstant()
    {
        var p = new RetryPolicy(5, RetryBackoff.Fixed, 500, 0);
        p.DelayFor(2).TotalMilliseconds.Should().Be(500);
        p.DelayFor(4).TotalMilliseconds.Should().Be(500);
    }

    [Fact]
    public void DelayFor_LinearBackoff_GrowsByInitialEachStep()
    {
        var p = new RetryPolicy(5, RetryBackoff.Linear, 100, 0);
        p.DelayFor(2).TotalMilliseconds.Should().Be(100);
        p.DelayFor(3).TotalMilliseconds.Should().Be(200);
        p.DelayFor(4).TotalMilliseconds.Should().Be(300);
    }

    [Fact]
    public void DelayFor_ExponentialBackoff_Doubles_AndCapsAtMaxDelay()
    {
        var p = new RetryPolicy(10, RetryBackoff.Exponential, 100, 500);
        p.DelayFor(2).TotalMilliseconds.Should().Be(100);   // 100 * 2^0
        p.DelayFor(3).TotalMilliseconds.Should().Be(200);   // 100 * 2^1
        p.DelayFor(4).TotalMilliseconds.Should().Be(400);   // 100 * 2^2
        p.DelayFor(5).TotalMilliseconds.Should().Be(500);   // would be 800, capped to 500
        p.DelayFor(9).TotalMilliseconds.Should().Be(500);   // still capped
    }

    [Fact]
    public void Disabled_DelayFor_AnyAttempt_IsZero()
    {
        RetryPolicy.Disabled.DelayFor(2).Should().Be(TimeSpan.Zero);
        RetryPolicy.Disabled.DelayFor(10).Should().Be(TimeSpan.Zero);
    }
}
