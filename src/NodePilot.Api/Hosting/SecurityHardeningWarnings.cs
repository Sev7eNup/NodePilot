using Serilog;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Surfaces deliberately-permissive security defaults as loud Warning lines on every
/// startup (audit H-11 / H-12 / M-16 / M-17). The defaults stay permissive so upgrades
/// don't break in-place deployments, but a production instance that ships with them
/// untouched is exposed to known threat models — a Warning at boot is louder than a
/// silent config and cheap to pay.
/// </summary>
public static class SecurityHardeningWarnings
{
    public static void LogSecurityHardeningWarnings(IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;

        if (!configuration.GetValue<bool>("Remote:RequireWinRmSsl"))
            Log.Warning("SECURITY: Remote:RequireWinRmSsl is false. WinRM without SSL lets an on-path attacker " +
                        "capture Negotiate/NTLM credentials. Set Remote:RequireWinRmSsl=true and use UseSsl=true on each machine.");

        if (!configuration.GetValue<bool>("RestApi:BlockPrivateNetworks"))
            Log.Warning("SECURITY: RestApi:BlockPrivateNetworks is false. Workflow authors can hit internal services via restApi " +
                        "(10.x, 192.168.x, 127.x). Link-local/cloud-metadata is always blocked. Consider flipping and using RestApi:AllowedHosts for exceptions.");

        if (!configuration.GetValue<bool>("FileSystemOperation:RejectTraversal"))
            Log.Warning("SECURITY: FileSystemOperation:RejectTraversal is false. File-system activities accept '..' components — " +
                        "set to true and configure FileSystemOperation:AllowedRoots to scope file writes.");

        if (!DefaultTrueUnlessExplicitFalse(configuration, "SqlActivity:RequireConnectionRef"))
            Log.Warning("SECURITY: SqlActivity:RequireConnectionRef is false. Workflow authors can embed raw connection strings " +
                        "(including Password=) in the workflow JSON. Configure named SqlActivity:ConnectionStrings and flip this to true.");

        if (!DefaultTrueUnlessExplicitFalse(configuration, "Trigger:Database:RequireConnectionRef"))
            Log.Warning("SECURITY: Trigger:Database:RequireConnectionRef is false. DatabaseTrigger sources can use raw connection " +
                        "strings. Same risk as SqlActivity (plaintext password at rest in Workflows.DefinitionJson).");

        if (!configuration.GetValue<bool>("StartProgram:DisallowShellExecute"))
            Log.Warning("SECURITY: StartProgram:DisallowShellExecute is false. Workflows can set useShellExecute=true and bypass " +
                        "stdout/stderr capture (audit-trail loss). Consider flipping to true.");

        // H-4: In Production, AllowedHosts must be neither "*" nor an unsubstituted installer
        // template placeholder (a typical operator mistake when the template was rendered by
        // hand). Logged at Error level so it doesn't get lost in the flood of Information-level
        // boot lines. The hard-fail (aborting startup instead of just logging) only kicks in
        // when Security:StrictAllowedHosts=true (enabled in the Production template, opt-out
        // for in-place upgrades).
        var allowedHosts = configuration["AllowedHosts"];
        var isUnsubstitutedPlaceholder = !string.IsNullOrEmpty(allowedHosts)
            && allowedHosts.StartsWith("{{", StringComparison.Ordinal)
            && allowedHosts.EndsWith("}}", StringComparison.Ordinal);
        var allowedHostsUnsafe = string.IsNullOrWhiteSpace(allowedHosts)
            || allowedHosts == "*"
            || isUnsubstitutedPlaceholder;
        if (allowedHostsUnsafe)
        {
            var detail = isUnsubstitutedPlaceholder
                ? $"AllowedHosts is the unsubstituted template placeholder '{allowedHosts}'. " +
                  "Render the Production template via the installer or set AllowedHosts to a concrete FQDN."
                : "AllowedHosts is '*'. Host-header injection / cache-poisoning risk. " +
                  "Set AllowedHosts=\"nodepilot.example.com\" (semicolon-separated for multiple).";
            Log.Error("SECURITY: {Detail}", detail);
        }

        var jwtIssuer  = configuration["Jwt:Issuer"];
        var jwtAudience = configuration["Jwt:Audience"];
        if (jwtIssuer is null or "NodePilot" || jwtAudience is null or "NodePilot")
            Log.Warning("SECURITY: Jwt:Issuer/Audience use the default 'NodePilot'. Deployments sharing the same Jwt:Key across " +
                        "staging and production would accept cross-instance tokens. Set unique values (e.g. 'nodepilot:prod').");

        if (!string.IsNullOrWhiteSpace(configuration["Smtp:Password"]))
            Log.Warning("SECURITY: Smtp:Password appears to be set in configuration. Prefer environment variable (Smtp__Password) " +
                        "or a secrets manager to keep the password out of appsettings.json and backups.");

        // H-2 (security audit 2026-05-15): plaintext SMTP transport with credentials.
        // Username configured but EnableSsl explicitly false → LOGIN/PLAIN auth and the
        // whole message body travel in the clear. Default is EnableSsl=true (SmtpOptions),
        // so this only fires when an operator deliberately turned it off.
        if (!configuration.GetValue("Smtp:EnableSsl", true)
            && !string.IsNullOrWhiteSpace(configuration["Smtp:Username"]))
            Log.Warning("SECURITY: Smtp:EnableSsl=false and Smtp:Username is set. SMTP credentials and message bodies will travel " +
                        "in plaintext. Set Smtp:EnableSsl=true unless this is a localhost-only relay.");

        if (!string.IsNullOrWhiteSpace(configuration["Llm:ApiKey"])
            && !string.Equals(configuration["Llm:ApiKey"], "EMPTY", StringComparison.OrdinalIgnoreCase))
            Log.Warning("SECURITY: Llm:ApiKey appears to be set in configuration. Prefer environment variable (Llm__ApiKey) " +
                        "or a secrets manager. Plaintext API keys end up in appsettings.json backups and Git history.");

        // LDAP plaintext bind is operator error: the boot validator rejects UseSsl=false for
        // enabled deployments and the adapter refuses the bind unconditionally. Warn anyway so
        // the mismatch is visible even in unvalidated configurations.
        if (configuration.GetValue<bool>("Authentication:Ldap:Enabled")
            && !configuration.GetValue("Authentication:Ldap:UseSsl", true))
            Log.Warning("SECURITY: Authentication:Ldap:Enabled=true with UseSsl=false. The simple-bind would send the user's " +
                        "password in cleartext — the adapter refuses to bind without LDAPS. Set Authentication:Ldap:UseSsl=true (port 636).");

        // DPAPI CurrentUser scope binds Credential blob ciphertext to the *Windows account*
        // the API process runs as. A service-account swap (or a managed-identity rotation that
        // changes the underlying SID) makes every existing Credential row undecryptable —
        // workflows that need stored passwords break silently until each credential is re-entered.
        // LocalMachine survives service-account rotation as long as the host stays the same;
        // the deploy template defaults to LocalMachine for production for exactly this reason.
        if (string.Equals(configuration["Credentials:DpapiScope"], "CurrentUser", StringComparison.OrdinalIgnoreCase))
            Log.Warning("SECURITY: Credentials:DpapiScope=CurrentUser in a non-Development environment. Stored Credentials " +
                        "won't survive a service-account rotation. Set Credentials:DpapiScope=LocalMachine in production.");

        // Inline Password=... in a connection string is functionally a plaintext secret in
        // appsettings — the same risk class as Smtp:Password above, but easier to miss because
        // the JSON key is ConnectionStrings:* not *:Password. Warn for both providers.
        foreach (var key in new[] { "ConnectionStrings:Postgres", "ConnectionStrings:DefaultConnection" })
        {
            var cs = configuration[key];
            if (!string.IsNullOrWhiteSpace(cs)
                && cs.Contains("Password=", StringComparison.OrdinalIgnoreCase)
                && !cs.Contains("Trusted_Connection=True", StringComparison.OrdinalIgnoreCase)
                && !cs.Contains("Integrated Security=True", StringComparison.OrdinalIgnoreCase)
                && DbContextSetup.IsJsonBacked(configuration, key))
            {
                Log.Warning("SECURITY: {Key} contains a plaintext Password=. Prefer Integrated/Trusted auth " +
                            "(gMSA) or move the password to an environment variable (e.g. ConnectionStrings__Postgres) " +
                            "or a secrets manager. Plaintext passwords in appsettings end up in backups and Git history.", key);
            }
        }

        // Vault provider hardening: AES-GCM master key in plaintext config is unavoidable
        // for the AES-GCM provider, but operators should know it ends up in DB backups,
        // appsettings, and Git if not careful. The DPAPI default doesn't have this issue.
        if (string.Equals(configuration["Secrets:Provider"], "AesGcm", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(configuration["Secrets:MasterKey"]))
            Log.Warning("SECURITY: Secrets:MasterKey is in plaintext configuration. Protect appsettings with file ACLs " +
                        "(icacls / chmod 600) and exclude it from any backup/git pipeline. Prefer an env var " +
                        "(Secrets__MasterKey), ACL-restricted Secrets:MasterKeyFile, or a secret manager to keep the key out of JSON files.");

        if (!string.IsNullOrWhiteSpace(configuration["OpenTelemetry:Prometheus:Password"])
            || !string.IsNullOrWhiteSpace(configuration["OpenTelemetry:Prometheus:BearerToken"]))
            Log.Warning("SECURITY: OpenTelemetry:Prometheus credentials appear to be set in configuration. Prefer environment " +
                        "variables (OpenTelemetry__Prometheus__Password / ...__BearerToken) over appsettings.");

        if (configuration.GetValue<bool>("OpenTelemetry:Enabled")
            && configuration.GetValue<bool>("OpenTelemetry:Exporters:PrometheusScrape")
            && configuration.GetValue<bool>("OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous"))
            Log.Warning("SECURITY: /metrics is configured for anonymous scrape. Metrics leak workflow names, host identifiers, " +
                        "and execution counts. Restrict at the reverse proxy or flip PrometheusScrapeAllowAnonymous=false.");

        // H-4 strict mode: when the strict switch is enabled, an unsafe AllowedHosts value
        // aborts startup instead of just being logged. Defaults to false (for in-place
        // upgrades); the Production template sets it to true. An operator can flip it back via
        // a settings override if they deliberately want to stay lenient.
        if (configuration.GetValue<bool>("Security:StrictAllowedHosts") && allowedHostsUnsafe)
        {
            throw new InvalidOperationException(
                "SECURITY: Security:StrictAllowedHosts=true and AllowedHosts is unsafe " +
                $"('{allowedHosts}'). Set AllowedHosts to a concrete FQDN (semicolon-separated for multiple).");
        }
    }

    private static bool DefaultTrueUnlessExplicitFalse(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loud warning for explicitly-disabled retention sweepers. The services log their
    /// disabled state at Info, but those messages get lost in a busy boot log — Warning
    /// at the config-parse stage is loud enough that an operator skimming the first 10
    /// log lines sees it.
    /// </summary>
    public static void LogRetentionDisabledWarnings(IConfiguration configuration)
    {
        if (!configuration.GetValue("Retention:Executions:Enabled", true))
            Log.Warning("Retention:Executions:Enabled is false — WorkflowExecutions + StepExecutions will grow unbounded. " +
                        "Set to true (default MaxAgeDays=30) unless you have external archival.");
        if (!configuration.GetValue("Retention:AuditLog:Enabled", true))
            Log.Warning("Retention:AuditLog:Enabled is false — AuditLog will grow unbounded (one row per mutation + one per CREDENTIAL_DECRYPTED). " +
                        "Set to true (default MaxAgeDays=365) to meet long-run stability.");
        if (!configuration.GetValue("Retention:WorkflowVersions:Enabled", true))
            Log.Warning("Retention:WorkflowVersions:Enabled is false — workflow history snapshots will accumulate forever. " +
                        "Set to true (default MaxVersionsPerWorkflow=50) unless versioning policy requires full history.");
    }
}
