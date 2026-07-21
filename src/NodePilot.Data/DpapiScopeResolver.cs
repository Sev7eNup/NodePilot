using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace NodePilot.Data;

/// <summary>
/// Single source of truth for the DPAPI scope used by every encrypted-at-rest store
/// (credentials, secret global variables). Honors the <c>Credentials:DpapiScope</c> key —
/// deliberately named after the older CredentialStore setting so operators only tune one knob.
/// </summary>
internal static class DpapiScopeResolver
{
    public static DataProtectionScope FromConfig(IConfiguration? config)
        => Parse(config?["Credentials:DpapiScope"], "Credentials:DpapiScope");

    /// <summary>
    /// Parse a DPAPI scope string from any config key. Used for both the canonical
    /// <c>Credentials:DpapiScope</c> setting and the migration-window
    /// <c>Secrets:LegacyDpapiScope</c> setting; the validation rules are identical.
    /// </summary>
    public static DataProtectionScope Parse(string? raw, string configKeyForErrorMessage)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return DataProtectionScope.CurrentUser;

        // Security-audit finding L-5: previous behavior was `== "LocalMachine" ? LocalMachine : CurrentUser`, which
        // silently folded every typo ("Local_Machine", "Machine", "current") into CurrentUser.
        // That meant an operator who intended LocalMachine but mis-typed the key got CurrentUser
        // without any diagnostic — a legitimate multi-user deployment would then fail to decrypt
        // under a different service account with no clear hint why. Fail fast instead.
        return trimmed.ToLowerInvariant() switch
        {
            "currentuser" => DataProtectionScope.CurrentUser,
            "localmachine" => DataProtectionScope.LocalMachine,
            _ => throw new InvalidOperationException(
                $"{configKeyForErrorMessage} value '{trimmed}' is not recognized. Use 'CurrentUser' or 'LocalMachine'.")
        };
    }
}
