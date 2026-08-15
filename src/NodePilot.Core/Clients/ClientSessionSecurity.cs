using System.Text.Json;

namespace NodePilot.Core.Clients;

/// <summary>
/// Shared facts for the two HTTP-only clients (the <c>np</c> CLI and the
/// <c>nodepilot-mcp</c> server). Both deliberately copy their HTTP plumbing
/// (ADR 0005), but they MUST agree on the DPAPI session-blob format: the MCP
/// server reads the same <c>%APPDATA%\NodePilot\session-&lt;profile&gt;.dat</c>
/// file that <c>np auth login</c> writes. Before this constant existed, the
/// entropy literal was hard-coded in both projects — a silent-breakage coupling
/// (coherence audit 2026-08).
/// </summary>
public static class ClientSessionSecurity
{
    /// <summary>
    /// DPAPI additional entropy for the session blob. The value predates the MCP
    /// server and is part of the on-disk format — changing it would orphan every
    /// existing logged-in session, so it stays "NodePilot.Cli/v1" even though the
    /// blob is now shared by two executables.
    /// </summary>
    public const string DpapiSessionEntropy = "NodePilot.Cli/v1";

    /// <summary>
    /// Bearer clients rotate a still-valid token shortly before the server-side absolute
    /// session deadline. Refresh never extends that deadline; the lead time only guarantees
    /// that rotation is attempted while the presented JWT can still authenticate the refresh
    /// endpoint.
    /// </summary>
    public static readonly TimeSpan ProactiveRefreshLeadTime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A transient proactive-refresh failure must not turn a burst of already queued client
    /// calls into an equivalent burst against the refresh endpoint. The cooldown is deliberately
    /// short and token-bound: normal API calls continue with the still-valid credential, then a
    /// later request retries rotation.
    /// </summary>
    public static readonly TimeSpan TransientRefreshFailureCooldown = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Suppresses repeated proactive rotations of the same freshly minted JWT across short-lived
    /// CLI processes. One minute still leaves multiple retry opportunities inside the five-minute
    /// lead window, while keeping a command burst well below the server's refresh rate limit.
    /// </summary>
    public static readonly TimeSpan SuccessfulRefreshDeduplicationWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Resolves the absolute session deadline advertised by a new server, or (for rolling upgrades
    /// against an older server) from the returned JWT's signed-on-use <c>exp</c> claim. This method
    /// only parses the payload; it never treats JWT claims as authorization or origin evidence.
    /// The API validates the token when it is subsequently used.
    /// </summary>
    public static bool TryResolveExpiration(
        string jwt,
        DateTimeOffset? advertisedExpiration,
        out DateTimeOffset expiration)
    {
        if (advertisedExpiration.HasValue)
        {
            expiration = advertisedExpiration.Value;
            return true;
        }

        return TryReadUnixTimestamp(jwt, "exp", milliseconds: false, out expiration);
    }

    /// <summary>
    /// Returns true when the current token generation was minted within <paramref name="window"/>.
    /// Used only to deduplicate proactive refresh attempts between CLI/MCP processes. A small future
    /// tolerance accommodates host clock skew; a far-future or malformed claim is ignored.
    /// </summary>
    public static bool WasIssuedRecently(
        string jwt,
        DateTimeOffset now,
        TimeSpan window)
    {
        if (!TryReadUnixTimestamp(jwt, "np_iat_ms", milliseconds: true, out var issuedAt)
            && !TryReadUnixTimestamp(jwt, "iat", milliseconds: false, out issuedAt))
        {
            return false;
        }

        var age = now - issuedAt;
        return age >= TimeSpan.FromMinutes(-1) && age < window;
    }

    private static bool TryReadUnixTimestamp(
        string jwt,
        string claim,
        bool milliseconds,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(jwt)) return false;

        var firstDot = jwt.IndexOf('.');
        if (firstDot < 0) return false;
        var secondDot = jwt.IndexOf('.', firstDot + 1);
        if (secondDot < 0) return false;
        var encodedPayload = jwt[(firstDot + 1)..secondDot];
        if (encodedPayload.Length == 0) return false;

        try
        {
            var base64 = encodedPayload.Replace('-', '+').Replace('_', '/');
            base64 = (base64.Length % 4) switch
            {
                0 => base64,
                2 => base64 + "==",
                3 => base64 + "=",
                _ => throw new FormatException("Invalid base64url payload length."),
            };

            using var payload = JsonDocument.Parse(Convert.FromBase64String(base64));
            if (!payload.RootElement.TryGetProperty(claim, out var value)) return false;
            long unixValue;
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (!value.TryGetInt64(out unixValue)) return false;
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                if (!long.TryParse(value.GetString(), out unixValue)) return false;
            }
            else
            {
                return false;
            }

            timestamp = milliseconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue)
                : DateTimeOffset.FromUnixTimeSeconds(unixValue);
            return true;
        }
        catch (Exception ex) when (ex is FormatException
                                   or JsonException
                                   or ArgumentOutOfRangeException
                                   or OverflowException)
        {
            return false;
        }
    }
}
