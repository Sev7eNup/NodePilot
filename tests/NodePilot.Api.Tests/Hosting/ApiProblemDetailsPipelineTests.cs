using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class ApiProblemDetailsPipelineTests
{
    [Fact]
    public async Task ProgramPipeline_NormalizesLegacyControllerErrorPayloadsToProblemDetails()
    {
        using var factory = new ProblemDetailsApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/trigger/missing-workflow",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status401Unauthorized);
        problem.Title.Should().Be("Unauthorized");
        problem.Detail.Should().Be("Invalid or missing X-Api-Key header");
        problem.Extensions["code"].Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("UNAUTHORIZED");
    }

    private sealed class ProblemDetailsApiFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nodepilot-api-pipeline-tests", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                Directory.CreateDirectory(_tempDir);
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "NodePilot-Test-Jwt-Key-For-Pipeline-Smoke-32-Bytes",
                    ["Security:AdminSetupTokenPath"] = Path.Combine(_tempDir, "admin-setup.token"),
                    ["Logging:SupportLog:Enabled"] = "false",
                    ["Logging:SupportLog:DbProjectionEnabled"] = "false",
                    ["Retention:Executions:Enabled"] = "false",
                    ["Retention:AuditLog:Enabled"] = "false",
                    ["Retention:SupportEvents:Enabled"] = "false",
                    ["OpenTelemetry:Enabled"] = "false",
                });
            });

            builder.ConfigureServices(services =>
            {
                RemoveDbContextServices(services);
                services.RemoveAll<IHostedService>();

                _connection = new SqliteConnection("DataSource=:memory:");
                _connection.Open();
                services.AddDbContext<NodePilotDbContext>(options =>
                {
                    options.UseSqlite(_connection);
                    options.ConfigureWarnings(w =>
                        w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                _connection?.Dispose();
                try { Directory.Delete(_tempDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        private static void RemoveDbContextServices(IServiceCollection services)
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (ReferencesNodePilotDbContext(services[i].ServiceType))
                    services.RemoveAt(i);
            }
        }

        private static bool ReferencesNodePilotDbContext(Type serviceType)
        {
            if (serviceType == typeof(NodePilotDbContext)
                || serviceType == typeof(DbContextOptions<NodePilotDbContext>))
                return true;

            return serviceType.IsGenericType
                   && serviceType.GenericTypeArguments.Any(a => a == typeof(NodePilotDbContext));
        }
    }
}
