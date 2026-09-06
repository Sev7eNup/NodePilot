using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Activities;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Values an activity publishes are read back by the invariant condition parser, so producers
/// must not render them with the host culture. Each test runs the producer on a thread pinned to
/// de-DE, where a comma decimal separator would otherwise be re-read as a group separator and
/// silently multiply the value.
/// </summary>
public sealed class InvariantScalarFormattingTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connStr;

    public InvariantScalarFormattingTests()
    {
        _connStr = $"Data Source=InvariantScalarTests_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connStr);
        _keepAlive.Open();

        using var seed = _keepAlive.CreateCommand();
        seed.CommandText = """
            CREATE TABLE Disks (Id INTEGER PRIMARY KEY, Host TEXT, FreeGb REAL);
            INSERT INTO Disks (Host, FreeGb) VALUES ('srv1', 1.5);
        """;
        seed.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive.Dispose();

    /// <summary>
    /// Runs the work on a dedicated thread pinned to <paramref name="culture"/>. A dedicated
    /// thread rather than assigning CurrentCulture in-place, so a parallel test never observes
    /// the switch.
    /// </summary>
    private static T RunInCulture<T>(string culture, Func<Task<T>> work)
    {
        T value = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var ci = new CultureInfo(culture);
            CultureInfo.CurrentCulture = ci;
            CultureInfo.CurrentUICulture = ci;
            try { value = work().GetAwaiter().GetResult(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return value;
    }

    private static JsonElement Cfg(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    private static StepExecutionContext Ctx() =>
        new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "scalar-1" };

    [Fact]
    public void ToInvariantString_UnderGermanCulture_UsesInvariantForms()
    {
        var rendered = RunInCulture("de-DE", () => Task.FromResult(new[]
        {
            DataBusScalar.ToInvariantString(1.5m),
            DataBusScalar.ToInvariantString(9.99d),
            DataBusScalar.ToInvariantString(true),
            DataBusScalar.ToInvariantString(false),
            DataBusScalar.ToInvariantString(new DateTime(2026, 9, 2, 14, 30, 0, DateTimeKind.Utc)),
            DataBusScalar.ToInvariantString(new byte[] { 0x00, 0x2A }),
            DataBusScalar.ToInvariantString(null),
        }));

        rendered[0].Should().Be("1.5");
        rendered[1].Should().Be("9.99");
        rendered[2].Should().Be("true");
        rendered[3].Should().Be("false");
        rendered[4].Should().Be("2026-09-02T14:30:00.0000000Z");
        rendered[5].Should().Be("002A");
        rendered[6].Should().BeEmpty();
    }

    [Fact]
    public void JsonQuery_SingleModeFloat_UsesInvariantDecimalSeparator()
    {
        var activity = new JsonQueryActivity();
        var cfg = Cfg(new
        {
            source = "inline",
            content = "{\"items\":[{\"price\":9.99}]}",
            jsonPath = "$.items[0].price",
            resultMode = "single",
        });

        var result = RunInCulture("de-DE", () => activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None));

        result.Success.Should().BeTrue();
        result.OutputParameters["result"].Should().Be("9.99");
    }

    [Fact]
    public void JsonQuery_SingleModeBoolean_UsesLowercaseLiteral()
    {
        var activity = new JsonQueryActivity();
        var cfg = Cfg(new
        {
            source = "inline",
            content = "{\"active\":true}",
            jsonPath = "$.active",
            resultMode = "single",
        });

        var result = RunInCulture("de-DE", () => activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None));

        result.Success.Should().BeTrue();
        result.OutputParameters["result"].Should().Be("true");
    }

    [Fact]
    public void JsonQuery_SingleModeIsoTimestamp_KeepsTheOriginalText()
    {
        var activity = new JsonQueryActivity();
        var cfg = Cfg(new
        {
            source = "inline",
            content = "{\"finishedAt\":\"2026-09-02T14:30:00Z\"}",
            jsonPath = "$.finishedAt",
            resultMode = "single",
        });

        var result = RunInCulture("de-DE", () => activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None));

        result.Success.Should().BeTrue();
        result.OutputParameters["result"].Should().Be("2026-09-02T14:30:00Z");
    }

    [Fact]
    public void XmlQuery_NumericXPath_UsesInvariantDecimalSeparator()
    {
        var activity = new XmlQueryActivity();
        var cfg = Cfg(new
        {
            source = "inline",
            content = "<orders><order amount=\"1000.5\"/><order amount=\"234\"/></orders>",
            xpath = "sum(//order/@amount)",
            resultMode = "single",
        });

        var result = RunInCulture("de-DE", () => activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None));

        result.Success.Should().BeTrue();
        result.OutputParameters["result"].Should().Be("1234.5");
    }

    [Fact]
    public void SqlActivity_RealColumn_UsesInvariantDecimalSeparator()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlActivity:RequireConnectionRef"] = "false",
            })
            .Build();
        var activity = new SqlActivity(config);
        var cfg = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            connectionString = _connStr,
            provider = "sqlite",
            query = "SELECT FreeGb FROM Disks WHERE Host = 'srv1'",
        })).RootElement;

        var result = RunInCulture("de-DE", () => activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None));

        result.Success.Should().BeTrue();
        // The JSON projection was always invariant; the flat param must agree with it.
        result.OutputParameters["FreeGb"].Should().Be("1.5");
        result.Output.Should().Contain("1.5");
    }
}