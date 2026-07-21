namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Single source of truth for the workflow-definition config keys whose string values are
/// secrets (webhook secrets, inline API keys, passwords, bearer tokens, connection strings).
/// Lives in Core so every layer agrees: the API's redaction/export/backup paths
/// (<c>WorkflowDefinitionSecretRewriter</c>) AND the MCP server's definition-redaction layer
/// both consume this exact set. Keep it here, not duplicated.
/// </summary>
public static class WorkflowSecretKeys
{
    /// <summary>
    /// Config keys (case-insensitive) whose string values must be masked before a workflow
    /// definition leaves the system or is surfaced to an agent. Aligned with the runtime
    /// <c>OutputRedactor</c> secret-name vocabulary and the restApi object-form credential headers
    /// (<see cref="WorkflowSecretContent.CredentialHeaderNames"/>) so the definition layer no longer
    /// lags behind the two curated lists the codebase already maintained.
    /// </summary>
    public static readonly IReadOnlySet<string> SecretConfigKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "secret",           // webhookTrigger
            "apiKey",
            "password",
            "authToken",
            "bearer",
            "connectionString", // sqlActivity / databaseTrigger — often carry Password=
            // Additional secret-bearing value keys (custom-activity inputs, OAuth / cloud creds).
            "token",
            "accessToken",
            "refreshToken",
            "sessionToken",
            "clientSecret",
            "privateKey",
            "accessKey",
            "secretKey",
            "apiSecret",
            "webhookSecret",
            // restApi object-form header names — `{ "Authorization": "Bearer …" }`. The newline
            // string form (`config.headers = "Authorization: Bearer …"`) is caught by
            // WorkflowSecretContent instead, since its parent key is only `headers`.
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "Set-Cookie",
            "X-Api-Key",
            "X-Auth-Token",
            "X-Webhook-Secret",
        };

    /// <summary>
    /// The single predicate every redaction walk uses: a string value is a secret when its config
    /// key is in <see cref="SecretConfigKeys"/> <b>or</b> its content looks like an inline secret
    /// (<see cref="WorkflowSecretContent.LooksSecret"/>). Content detection covers secrets that hide
    /// in non-secret-named keys — the restApi <c>headers</c> string, <c>body</c>, and runScript
    /// <c>script</c>.
    /// </summary>
    public static bool IsSecretValue(string? key, string? value)
        => !string.IsNullOrEmpty(value)
           && ((key is not null && SecretConfigKeys.Contains(key)) || WorkflowSecretContent.LooksSecret(value));
}
