using FluentAssertions;
using NodePilot.Core.Time;
using Xunit;

namespace NodePilot.Data.Tests;

public class TimeZoneResolverTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Berlin")]              // IANA — what the browser sends
    [InlineData("W. Europe Standard Time")]    // Windows — what a Windows host knows natively
    public void TryResolve_AcceptsIanaAndWindowsIds(string id)
    {
        TimeZoneResolver.TryResolve(id, out var tz).Should().BeTrue($"'{id}' should resolve on this host");
        tz.Should().NotBeNull();
    }

    [Fact]
    public void TryResolve_IanaAndWindowsForm_ResolveToSameZone()
    {
        TimeZoneResolver.TryResolve("Europe/Berlin", out var iana).Should().BeTrue();
        TimeZoneResolver.TryResolve("W. Europe Standard Time", out var windows).Should().BeTrue();
        iana.BaseUtcOffset.Should().Be(windows.BaseUtcOffset);
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_UnknownOrEmpty_ReturnsFalse(string? id)
    {
        TimeZoneResolver.TryResolve(id, out _).Should().BeFalse();
    }
}
