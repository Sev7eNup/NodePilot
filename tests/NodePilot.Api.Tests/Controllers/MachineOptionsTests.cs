using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class MachineOptionsTests
{
    [Fact]
    public async Task Options_ReturnOrderedConfiguration_WithoutReadingStatisticsOrDefinitions()
    {
        var (connection, seed) = NodePilot.TestCommons.TestDbFactory.CreateWithConnection();
        await using var ownedConnection = connection;
        await using var seedDb = seed;
        seedDb.ManagedMachines.AddRange(
            new ManagedMachine { Id = Guid.NewGuid(), Name = "Zulu", Hostname = "zulu.local" },
            new ManagedMachine { Id = Guid.NewGuid(), Name = "Alpha", Hostname = "alpha.local", IsReachable = true });
        await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var recorder = new ReadRecorder();
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);
        var stepStats = new MachineStepStatsCache(Mock.Of<IServiceScopeFactory>(), Mock.Of<IHostApplicationLifetime>());
        var controller = new MachinesController(db, Mock.Of<IRemoteSessionFactory>(),
            Mock.Of<ICredentialStore>(), NoopAuditWriter.Instance, new WorkflowDefinitionFactsCache(), stepStats);

        var result = await controller.GetOptions(TestContext.Current.CancellationToken);
        var rows = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<List<MachineOptionResponse>>().Subject;
        rows.Select(m => m.Name).Should().Equal("Alpha", "Zulu");
        rows[0].Hostname.Should().Be("alpha.local");
        rows[0].IsReachable.Should().BeTrue();
        rows[0].DefaultCredentialId.Should().BeNull();

        recorder.Commands.Should().ContainSingle().Which.Should().Contain("ManagedMachines")
            .And.NotContain("StepExecutions").And.NotContain("Workflows");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(rows[0], new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "name", "hostname", "winRmPort", "useSsl", "defaultCredentialId",
            "tags", "lastConnectivityCheck", "isReachable");
    }

    private sealed class ReadRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
