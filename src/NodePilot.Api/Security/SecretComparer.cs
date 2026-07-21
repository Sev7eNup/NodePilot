using System.Security.Cryptography;
using System.Text;

namespace NodePilot.Api.Security;

internal static class SecretComparer
{
    /// <summary>
    /// Constant-time string compare. Hardening beyond the stock FixedTimeEquals (Audit L-1):
    ///
    ///  * Caller-controlled string is capped at 4× the expected length. An attacker who
    ///    posts a many-megabyte header should not be able to burn CPU in UTF-8 encoding
    ///    plus the subsequent dummy compare — we short-circuit before allocating.
    ///  * Length mismatch still runs a fixed-time dummy compare against a zeroed buffer
    ///    of the correct length so the observable timing does not leak "wrong length"
    ///    vs. "wrong value".
    /// </summary>
    public static bool FixedTimeEquals(string? presented, string? expected)
    {
        if (presented is null || expected is null) return false;
        // Bound the work an attacker can trigger. expected.Length is bounded by our own
        // token generation (base64 of 32 bytes = 44 chars), so the cap is comfortably
        // above legitimate input while killing DoS-by-giant-header.
        if (expected.Length > 0 && presented.Length > expected.Length * 4) return false;

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length)
        {
            // Constant-time dummy compare so an attacker timing the response cannot
            // distinguish "different length" (would be O(1) with an early return) from
            // "same length but different bytes" (would be O(n)).
            var dummy = new byte[b.Length];
            CryptographicOperations.FixedTimeEquals(dummy, b);
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
