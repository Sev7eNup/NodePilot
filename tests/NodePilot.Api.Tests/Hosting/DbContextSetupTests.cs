using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NodePilot.Api.Hosting;
using NodePilot.Data;
using System.Text;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Pins the contract of <see cref="DbContextSetup"/> — the helper that <c>Program.cs</c>
/// delegates DB-Provider-Branching to. We verify the three observable outputs of the
/// registration:
/// <list type="bullet">
///   <item>NodePilotDbContext is registered (no matter which provider).</item>
///   <item>Unknown providers throw with a clear message at <i>service-resolution</i> time
///   (DbContextPool defers connection-string parsing until the context is asked for).</item>
///   <item>Pool size override is honoured.</item>
/// </list>
/// </summary>
public class DbContextSetupTests
{
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "NodePilot.Api.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values!).Build();

    private static IConfiguration BuildJsonConfig(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    [Fact]
    public void AddNodePilotDbContext_RegistersDbContext_ForPostgresProvider()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "postgres",
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=test;Username=u;Password=p",
        });

        var services = new ServiceCollection();
        services.AddNodePilotDbContext(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(NodePilotDbContext));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddNodePilotDbContext_RegistersDbContext_ForSqlServerProvider()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "sqlserver",
            ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Database=NP;Trusted_Connection=True",
        });

        var services = new ServiceCollection();
        services.AddNodePilotDbContext(config);

        services.Any(d => d.ServiceType == typeof(NodePilotDbContext)).Should().BeTrue();
    }

    [Fact]
    public void AddNodePilotDbContext_AcceptsPostgresAliases()
    {
        // The dispatcher accepts "postgres", "postgresql" and "npgsql" — all map to
        // the same Npgsql provider. A regression in the alias list once silently fell
        // through to the "unknown" branch and crashed startup.
        foreach (var alias in new[] { "postgresql", "npgsql" })
        {
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Database:Provider"] = alias,
                ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=t;Username=u;Password=p",
            });

            var services = new ServiceCollection();
            services.AddNodePilotDbContext(config);

            // Resolving the context forces options-builder execution. If the alias
            // didn't dispatch, this would throw InvalidOperationException.
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            FluentActions.Invoking(() => scope.ServiceProvider.GetRequiredService<NodePilotDbContext>())
                .Should().NotThrow($"alias '{alias}' must dispatch to the Npgsql branch");
        }
    }

    [Fact]
    public void AddNodePilotDbContext_RejectsUnknownProvider()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "mongodb",
        });

        var services = new ServiceCollection();
        services.AddNodePilotDbContext(config);

        // The throw happens inside the DbContextOptions builder, which is invoked
        // when the DbContext is first resolved. Before that point, registration is
        // symbolic and doesn't surface the bad provider.
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        FluentActions.Invoking(() => scope.ServiceProvider.GetRequiredService<NodePilotDbContext>())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Database:Provider 'mongodb'*nicht unterstützt*");
    }

    [Fact]
    public void AddNodePilotDbContext_DefaultsToPostgres_WhenProviderUnset()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            // Provider intentionally absent — must default to "postgres"
            // (matches CLAUDE.md "Datenbank" default).
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=t;Username=u;Password=p",
        });

        var services = new ServiceCollection();
        services.AddNodePilotDbContext(config);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        FluentActions.Invoking(() => scope.ServiceProvider.GetRequiredService<NodePilotDbContext>())
            .Should().NotThrow();
    }

    [Fact]
    public void WarnAboutInlinePasswords_ProductionJsonPassword_Throws()
    {
        var config = BuildJsonConfig("""
        {
          "Database": { "Provider": "postgres" },
          "ConnectionStrings": {
            "Postgres": "Host=127.0.0.1;Database=t;Username=u;Password=p"
          }
        }
        """);

        var act = () => DbContextSetup.WarnAboutInlinePasswords(config, new StubHostEnvironment());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:Postgres*Password=*ConnectionStrings__Postgres*");
    }

    [Fact]
    public void WarnAboutInlinePasswords_ProductionEnvironmentStylePassword_DoesNotThrow()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "postgres",
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=t;Username=u;Password=p",
        });

        var act = () => DbContextSetup.WarnAboutInlinePasswords(config, new StubHostEnvironment());

        act.Should().NotThrow();
    }

    [Fact]
    public void WarnAboutInlinePasswords_ProductionEnvironmentVariablePassword_DoesNotThrow()
    {
        const string prefix = "NODEPILOT_TEST_DBCTX_";
        var key = $"{prefix}ConnectionStrings__Postgres";
        Environment.SetEnvironmentVariable(key, "Host=127.0.0.1;Database=t;Username=u;Password=p");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "postgres",
                })
                .AddEnvironmentVariables(prefix)
                .Build();

            var act = () => DbContextSetup.WarnAboutInlinePasswords(config, new StubHostEnvironment());

            act.Should().NotThrow();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
