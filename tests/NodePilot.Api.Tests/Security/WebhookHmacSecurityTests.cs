using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class WebhookHmacSecurityTests
{
    [Fact]
    public void ComputeMac_MatchesIndependentV2WireVector()
    {
        // Expected digest was generated independently from the documented byte sequence,
        // not through WebhookHmacSecurity.ComputeMac. This pins every delimiter and field.
        var mac = WebhookHmacSecurity.ComputeMac(
            Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef"),
            "1783960200",
            "delivery-20260713-0001",
            "post",
            "/api/webhooks/Finance%20Ops/payment",
            "a=%2Fslash&a=hello%20world&flag=&z=last",
            Encoding.UTF8.GetBytes("{\"amount\":100,\"currency\":\"EUR\"}"));

        Convert.ToHexString(mac).Should().Be(
            "8623670A5241A8603AA6B07B0C80C663BBE98B443DD00F3A7CF5A3AB02FAE377",
            "the public NodePilot-HMAC-v2 wire format must remain byte-for-byte stable");
    }

    [Fact]
    public void CanonicalizeQuery_SortsKeysPreservesDuplicateOrderAndUsesRfc3986Utf8Encoding()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(
            "?z=last&a=hello%20world&a=%2Fslash&flag&unicode=%C3%A4");

        WebhookHmacSecurity.CanonicalizeQuery(context.Request.Query).Should().Be(
            "a=hello%20world&a=%2Fslash&flag=&unicode=%C3%A4&z=last");
    }

    [Fact]
    public void CanonicalizePath_IncludesPathBaseAndEscapesDecodedCharacters()
    {
        WebhookHmacSecurity.CanonicalizePath(
                new PathString("/base"),
                new PathString("/api/webhooks/Finance Ops/hook"))
            .Should().Be("/base/api/webhooks/Finance%20Ops/hook");
    }

    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public void TryParseFreshnessHeaders_AcceptsExactClockSkewBoundary(int offsetSeconds)
    {
        var now = new DateTime(2026, 7, 13, 18, 0, 0, DateTimeKind.Utc);
        var timestamp = new DateTimeOffset(now.AddSeconds(offsetSeconds)).ToUnixTimeSeconds().ToString();

        WebhookHmacSecurity.TryParseFreshnessHeaders(
                timestamp, "delivery-12345678", now, out var parsedTimestamp, out var deliveryId)
            .Should().BeTrue();
        parsedTimestamp.Should().Be(timestamp);
        deliveryId.Should().Be("delivery-12345678");
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void TryParseFreshnessHeaders_RejectsOutsideClockSkew(int offsetSeconds)
    {
        var now = new DateTime(2026, 7, 13, 18, 0, 0, DateTimeKind.Utc);
        var timestamp = new DateTimeOffset(now.AddSeconds(offsetSeconds)).ToUnixTimeSeconds().ToString();

        WebhookHmacSecurity.TryParseFreshnessHeaders(
                timestamp, "delivery-12345678", now, out _, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789012345")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    [InlineData("delivery-with-space 1")]
    [InlineData("delivery-with-newline\n")]
    [InlineData("delivery-non-ascii-ä")]
    public void TryParseFreshnessHeaders_RejectsNonCanonicalDeliveryIds(string deliveryId)
    {
        var now = new DateTime(2026, 7, 13, 18, 0, 0, DateTimeKind.Utc);
        var timestamp = new DateTimeOffset(now).ToUnixTimeSeconds().ToString();

        WebhookHmacSecurity.TryParseFreshnessHeaders(timestamp, deliveryId, now, out _, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("+1783960200")]
    [InlineData("999999999999999999999999")]
    public void TryParseFreshnessHeaders_RejectsNonCanonicalTimestamp(string timestamp)
    {
        var now = new DateTime(2026, 7, 13, 18, 0, 0, DateTimeKind.Utc);

        WebhookHmacSecurity.TryParseFreshnessHeaders(
                timestamp, "delivery-12345678", now, out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void ValidateDefinition_RejectsLegacyUnversionedHmacMode()
    {
        const string definition = """
        {
          "nodes": [
            { "id": "hook", "data": { "activityType": "webhookTrigger", "config": {
              "signatureMode": "hmac",
              "secret": "0123456789abcdef0123456789abcdef"
            } } }
          ]
        }
        """;

        WebhookHmacSecurity.ValidateDefinition(definition)
            .Should().Contain("legacy signatureMode 'hmac'")
            .And.Contain("nodepilot-hmac-v2");
    }
}
