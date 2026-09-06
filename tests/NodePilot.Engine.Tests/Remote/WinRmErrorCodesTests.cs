using FluentAssertions;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

/// <summary>
/// The logon-denied predicate the WinRM factory uses to decide whether a connect failure may be
/// retried. Pure integer classification, so it is testable without a WSMan endpoint.
/// </summary>
public sealed class WinRmErrorCodesTests
{
    [Fact]
    public void IsLogonDenied_LogonFailureCode_ReturnsTrue()
        => WinRmErrorCodes.IsLogonDenied(1326).Should().BeTrue();

    [Fact]
    public void IsLogonDenied_SecELogonDeniedCode_ReturnsTrue()
        => WinRmErrorCodes.IsLogonDenied(unchecked((int)0x8009030C)).Should().BeTrue();

    [Fact]
    public void IsLogonDenied_AccountLockedOutCode_ReturnsTrue()
        => WinRmErrorCodes.IsLogonDenied(1909).Should().BeTrue(
            "retrying a locked account only extends the lockout window");

    [Fact]
    public void IsLogonDenied_NoAuthenticatingAuthorityCode_ReturnsFalse()
        => WinRmErrorCodes.IsLogonDenied(unchecked((int)0x80090311)).Should().BeFalse(
            "an unreachable DC is transient and must keep retrying");

    [Fact]
    public void IsLogonDenied_AccessDeniedCode_ReturnsFalse()
        => WinRmErrorCodes.IsLogonDenied(5).Should().BeFalse(
            "WinRM rejects shell-quota overruns with Error 5, which the pool expects to be retried");

    [Fact]
    public void IsLogonDenied_UnknownTransportCode_ReturnsFalse()
        => WinRmErrorCodes.IsLogonDenied(995).Should().BeFalse();
}
