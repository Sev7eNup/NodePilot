using System.Net;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Prevents production database connections from silently accepting an unverified peer.
/// An explicit development-only escape hatch exists because local SQL/Postgres instances
/// commonly have no PKI, but the default is fail closed.
/// </summary>
public sealed class DatabaseTlsBootValidator : IBootValidator
{
    public string Name => "DatabaseTls";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        var provider = (configuration["Database:Provider"] ?? "postgres").Trim().ToLowerInvariant();
        var allowInsecure = bool.TryParse(configuration["Database:AllowInsecureTls"], out var allowed) && allowed;
        var key = provider == "sqlserver"
            ? "ConnectionStrings:DefaultConnection"
            : "ConnectionStrings:Postgres";
        var connectionString = configuration[key];

        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        if (provider == "sqlserver")
        {
            SqlConnectionStringBuilder builder;
            try { builder = new SqlConnectionStringBuilder(connectionString); }
            catch (ArgumentException ex)
            {
                AddParseError(issues, key, ex);
                return;
            }

            if (allowInsecure)
            {
                ValidateInsecureTlsOverride(
                    configuration,
                    IsLoopbackSqlServerHost(builder.DataSource)
                    && (string.IsNullOrWhiteSpace(builder.FailoverPartner)
                        || IsLoopbackSqlServerHost(builder.FailoverPartner)),
                    issues);
                return;
            }

            if (builder.Encrypt != SqlConnectionEncryptOption.Strict
                || builder.TrustServerCertificate)
            {
                issues.Add(new BootValidationIssue(
                    Name, BootValidationSeverity.Error, key,
                    "SQL Server must use Encrypt=Strict and TrustServerCertificate=False so the server certificate and hostname are verified. " +
                    "Set Database:AllowInsecureTls=true only for an isolated local development instance."));
            }
            return;
        }

        if (provider is "postgres" or "postgresql" or "npgsql")
        {
            NpgsqlConnectionStringBuilder builder;
            try { builder = new NpgsqlConnectionStringBuilder(connectionString); }
            catch (ArgumentException ex)
            {
                AddParseError(issues, key, ex);
                return;
            }

            if (allowInsecure)
            {
                ValidateInsecureTlsOverride(
                    configuration,
                    builder.Host.Split(',', StringSplitOptions.TrimEntries)
                        .All(IsLoopbackDatabaseHost),
                    issues);
                return;
            }

            if (builder.SslMode != SslMode.VerifyFull
                || !builder.CheckCertificateRevocation)
            {
                issues.Add(new BootValidationIssue(
                    Name, BootValidationSeverity.Error, key,
                    "PostgreSQL must use SSL Mode=VerifyFull and Check Certificate Revocation=true. " +
                    "Configure Root Certificate when the issuing CA is not in the operating-system trust store."));
            }
            return;
        }

        issues.Add(new BootValidationIssue(
            Name, BootValidationSeverity.Error, "Database:Provider",
            $"Unsupported database provider '{provider}'. Allowed values are sqlserver, postgres, postgresql, and npgsql."));
    }

    private static void AddParseError(
        IList<BootValidationIssue> issues,
        string key,
        ArgumentException exception)
    {
        issues.Add(new BootValidationIssue("DatabaseTls", BootValidationSeverity.Error, key,
            $"Database connection string cannot be parsed: {exception.Message}"));
    }

    private static void ValidateInsecureTlsOverride(
        IConfiguration configuration,
        bool loopbackOnly,
        IList<BootValidationIssue> issues)
    {
        // WebApplicationFactory.UseEnvironment and the generic host expose the
        // authoritative host environment through HostDefaults.EnvironmentKey.
        // Keep the environment-variable keys as fallbacks for direct validator use.
        var environment = configuration[HostDefaults.EnvironmentKey]
                          ?? configuration["ASPNETCORE_ENVIRONMENT"]
                          ?? configuration["DOTNET_ENVIRONMENT"]
                          ?? "Production";
        var isDevelopment = string.Equals(environment, Environments.Development, StringComparison.OrdinalIgnoreCase);
        // Deployment:Mode=Desktop is the single-machine package: a bundled Postgres on 127.0.0.1
        // with no PKI. A loopback connection that never leaves the host has no meaningful TLS
        // identity to verify, so we accept the same escape hatch the Development environment gets
        // — but ONLY for loopback hosts, and the rest of the production hardening stays on.
        var isDesktop = DeploymentModeReader.IsDesktop(configuration);

        if (!loopbackOnly || (!isDevelopment && !isDesktop))
        {
            issues.Add(new BootValidationIssue(
                "DatabaseTls", BootValidationSeverity.Error, "Database:AllowInsecureTls",
                "Database:AllowInsecureTls=true is accepted only for loopback database hosts, and only in the Development environment or under Deployment:Mode=Desktop. Production/server and remote database connections must verify TLS identity."));
            return;
        }

        var posture = isDesktop ? "Deployment:Mode=Desktop" : "Development";
        issues.Add(new BootValidationIssue(
            "DatabaseTls", BootValidationSeverity.Warning, "Database:AllowInsecureTls",
            $"Database TLS identity verification is disabled for a loopback-only database ({posture})."));
    }

    private static bool IsLoopbackSqlServerHost(string configuredHost)
    {
        var host = configuredHost.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        // SQL Server uses comma for a port and backslash for an instance name.
        var separator = host.IndexOfAny([',', '\\']);
        if (separator >= 0) host = host[..separator];
        return IsLoopbackDatabaseHost(host);
    }

    private static bool IsLoopbackDatabaseHost(string configuredHost)
    {
        if (string.IsNullOrWhiteSpace(configuredHost)) return false;

        var host = configuredHost.Trim();
        if (host.StartsWith("[", StringComparison.Ordinal) && host.Contains(']'))
            host = host[1..host.IndexOf(']')];

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, ".", StringComparison.Ordinal)
            || string.Equals(host, "(local)", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
