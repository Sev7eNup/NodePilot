using FluentAssertions;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public class SecretComparerTests
{
    [Fact]
    public void FixedTimeEquals_EqualStrings_ReturnsTrue()
    {
        SecretComparer.FixedTimeEquals("hunter2", "hunter2").Should().BeTrue();
    }

    [Fact]
    public void FixedTimeEquals_DifferentSameLength_ReturnsFalse()
    {
        SecretComparer.FixedTimeEquals("hunter2", "Hunter2").Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_DifferentLengths_ReturnsFalseWithoutThrow()
    {
        // Must not throw on length mismatch. FixedTimeEquals on two unequal-length byte
        // arrays throws unless the implementation pads them to equal length first.
        var act = () => SecretComparer.FixedTimeEquals("short", "much-longer-secret");
        act.Should().NotThrow();
        SecretComparer.FixedTimeEquals("short", "much-longer-secret").Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_PresentedNull_ReturnsFalse()
    {
        SecretComparer.FixedTimeEquals(null, "x").Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_ExpectedNull_ReturnsFalse()
    {
        SecretComparer.FixedTimeEquals("x", null).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_BothNull_ReturnsFalse()
    {
        // null != null for secret comparison: "no secret was set" must never match
        // "no secret was sent".
        SecretComparer.FixedTimeEquals(null, null).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_BothEmpty_ReturnsTrue()
    {
        // Empty == empty. CryptographicOperations.FixedTimeEquals on two zero-length
        // spans returns true. Callers that don't want this must null-guard upstream.
        SecretComparer.FixedTimeEquals("", "").Should().BeTrue();
    }

    [Fact]
    public void FixedTimeEquals_PresentedTooLarge_RejectsWithoutEncoding()
    {
        // A large presented value (e.g. a 100 KB header) against a short expected token
        // must be rejected before the UTF-8 GetBytes allocation, without throwing or
        // spending CPU proportional to its size.
        var huge = new string('a', 100_000);
        var expected = new string('b', 44); // realistic CSRF token length

        SecretComparer.FixedTimeEquals(huge, expected).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_PresentedAtCapBoundary_StillCompares()
    {
        // The cap is "presented.Length > expected.Length * 4". Exactly 4x must still go
        // through the compare path, and return false because the contents differ.
        var expected = "abcd"; // 4 chars
        var presented = new string('x', 16); // exactly 4 * expected.Length

        SecretComparer.FixedTimeEquals(presented, expected).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_ExpectedEmpty_AllowsAnyPresented()
    {
        // The cap only applies when expected.Length > 0. With expected="" the only match
        // is presented="", since FixedTimeEquals returns true for two empty arrays.
        SecretComparer.FixedTimeEquals("", "").Should().BeTrue();
        SecretComparer.FixedTimeEquals("anything", "").Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_Utf8MultiByte_HandledCorrectly()
    {
        // Multi-byte UTF-8 input must compare correctly without throwing.
        SecretComparer.FixedTimeEquals("pässwörd", "pässwörd").Should().BeTrue();
        SecretComparer.FixedTimeEquals("pässwörd", "paesswoerd").Should().BeFalse();
    }
}
