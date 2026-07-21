using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NodePilot.Api.Configuration;
using NodePilot.Api.Configuration.Validators;
using Xunit;

namespace NodePilot.Api.Tests.Configuration.Validators;

public sealed class DatabaseTlsBootValidatorTests
{
    [Fact]
    public void SqlServer_StrictCertificateValidation_Passes()
        => Validate(new()
        {
            ["Database:Provider"] = "sqlserver",
            ["ConnectionStrings:DefaultConnection"] = "Server=db.example;Database=np;Encrypt=Strict;TrustServerCertificate=False",
        }).Should().BeEmpty();

    [Theory]
    [InlineData("Server=db;Database=np;Encrypt=True;TrustServerCertificate=True")]
    [InlineData("Server=db;Database=np;Encrypt=True;TrustServerCertificate=False")]
    [InlineData("Server=db;Database=np;Encrypt=Strict;TrustServerCertificate=True")]
    public void SqlServer_UnverifiedOrNonStrictTls_Fails(string connectionString)
        => Validate(new()
        {
            ["Database:Provider"] = "sqlserver",
            ["ConnectionStrings:DefaultConnection"] = connectionString,
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Error);

    [Fact]
    public void Postgres_VerifyFullWithRevocation_Passes()
        => Validate(new()
        {
            ["Database:Provider"] = "postgres",
            ["ConnectionStrings:Postgres"] = "Host=db.example;Database=np;SSL Mode=VerifyFull;Check Certificate Revocation=true",
        }).Should().BeEmpty();

    [Theory]
    [InlineData("Host=db;Database=np")]
    [InlineData("Host=db;Database=np;SSL Mode=Require;Check Certificate Revocation=true")]
    [InlineData("Host=db;Database=np;SSL Mode=VerifyFull;Check Certificate Revocation=false")]
    public void Postgres_WithoutFullIdentityAndRevocationValidation_Fails(string connectionString)
        => Validate(new()
        {
            ["Database:Provider"] = "postgres",
            ["ConnectionStrings:Postgres"] = connectionString,
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Error);

    [Fact]
    public void ExplicitDevelopmentOverride_ProducesVisibleWarning()
        => Validate(new()
        {
            ["Database:Provider"] = "postgres",
            ["Database:AllowInsecureTls"] = "true",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=np",
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Warning);

    [Fact]
    public void ExplicitDevelopmentOverride_UsesCanonicalHostEnvironment()
        => Validate(new()
        {
            ["Database:Provider"] = "postgres",
            ["Database:AllowInsecureTls"] = "true",
            [HostDefaults.EnvironmentKey] = Environments.Development,
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=np",
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Warning);

    [Fact]
    public void CanonicalProductionHostEnvironment_CannotBeRelaxedByDevelopmentFallback()
        => Validate(new()
        {
            ["Database:Provider"] = "postgres",
            ["Database:AllowInsecureTls"] = "true",
            [HostDefaults.EnvironmentKey] = Environments.Production,
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Development,
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=np",
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Error);

    [Theory]
    [InlineData("Production", "127.0.0.1")]
    [InlineData("Staging", "localhost")]
    [InlineData("Development", "db.internal")]
    public void InsecureOverride_OutsideLoopbackDevelopment_Fails(
        string environment,
        string host)
        => Validate(new()
        {
            ["Database:Provider"] = "postgres",
            ["Database:AllowInsecureTls"] = "true",
            ["ASPNETCORE_ENVIRONMENT"] = environment,
            ["ConnectionStrings:Postgres"] = $"Host={host};Database=np",
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Error);

    [Fact]
    public void InsecureDevelopmentOverride_RejectsRemotePostgresFailoverHost()
        => Validate(new()
        {
            ["Database:Provider"] = "postgres",
            ["Database:AllowInsecureTls"] = "true",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1,db.remote.example;Database=np",
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Error);

    [Fact]
    public void InsecureDevelopmentOverride_AllowsSqlLoopbackPort()
        => Validate(new()
        {
            ["Database:Provider"] = "sqlserver",
            ["Database:AllowInsecureTls"] = "true",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings:DefaultConnection"] = "Server=tcp:localhost,1433;Database=np",
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Warning);

    [Fact]
    public void InsecureDevelopmentOverride_UsesEffectiveSqlAliasValue()
        => Validate(new()
        {
            ["Database:Provider"] = "sqlserver",
            ["Database:AllowInsecureTls"] = "true",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Data Source=db.remote.example;Database=np",
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Error);

    [Fact]
    public void SqlServer_ConflictingTrustCertificateAlias_Fails()
        => Validate(new()
        {
            ["Database:Provider"] = "sqlserver",
            ["ConnectionStrings:DefaultConnection"] = "Server=db;Encrypt=Strict;TrustServerCertificate=False;Trust Server Certificate=True",
        }).Should().ContainSingle(i => i.Severity == BootValidationSeverity.Error);

    private static List<BootValidationIssue> Validate(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var issues = new List<BootValidationIssue>();
        new DatabaseTlsBootValidator().Validate(configuration, issues);
        return issues;
    }
}
