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
        // Regression: must NOT throw on length mismatch. The constant-time dummy compare
        // inside the function would throw if it tried to FixedTimeEquals two unequal-length
        // byte arrays without the zero-buffer trick.
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
        // null != null for secret comparison — the API never compares "no secret was set"
        // to "no secret was sent" as a positive match.
        SecretComparer.FixedTimeEquals(null, null).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_BothEmpty_ReturnsTrue()
    {
        // Edge-case: empty == empty. CryptographicOperations.FixedTimeEquals on two
        // zero-length spans returns true. Callers that don't want this should null-guard
        // upstream.
        SecretComparer.FixedTimeEquals("", "").Should().BeTrue();
    }

    [Fact]
    public void FixedTimeEquals_PresentedTooLarge_RejectsWithoutEncoding()
    {
        // L-1 hardening: an attacker posting a 100 KB header against a 44-char expected
        // token must short-circuit BEFORE the UTF-8 GetBytes allocation. The function must
        // return false without throwing or consuming proportional CPU.
        var huge = new string('a', 100_000);
        var expected = new string('b', 44); // realistic CSRF token length

        SecretComparer.FixedTimeEquals(huge, expected).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_PresentedAtCapBoundary_StillCompares()
    {
        // The cap is "presented.Length > expected.Length * 4". Exactly 4× must still go
        // through the compare path (and return false because the contents differ).
        var expected = "abcd"; // 4 chars
        var presented = new string('x', 16); // exactly 4 * expected.Length

        SecretComparer.FixedTimeEquals(presented, expected).Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_ExpectedEmpty_AllowsAnyPresented()
    {
        // The cap only kicks in when expected.Length > 0. With expected="" the only thing
        // that can match is presented="" (same UTF-8 bytes after encoding), and FixedTimeEquals
        // returns true for two empty arrays.
        SecretComparer.FixedTimeEquals("", "").Should().BeTrue();
        SecretComparer.FixedTimeEquals("anything", "").Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_Utf8MultiByte_HandledCorrectly()
    {
        // Multi-byte chars: "ä" is 2 bytes in UTF-8. Comparing two strings where one has
        // an "ä" and the other has "ae" (3 bytes) must return false (different byte lengths
        // after encoding) and must not throw.
        SecretComparer.FixedTimeEquals("pässwörd", "pässwörd").Should().BeTrue();
        SecretComparer.FixedTimeEquals("pässwörd", "paesswoerd").Should().BeFalse();
    }
}
