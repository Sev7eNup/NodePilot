using System.Text.Json;
using FluentAssertions;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests.Commands;

[Collection(CommandTestCollection.Name)]
public class GlobalsSecretRoundTripTests
{
    [Fact]
    public void ExportThenUpsert_PreservesStoredSecretAndDoesNotExportItsMask()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        StubExisting(h, id, true);
        var file = Path.GetTempFileName();
        try
        {
            h.Run("globals", "export", "--file", file).ExitCode.Should().Be(ExitCodes.Success);
            using var exported = JsonDocument.Parse(File.ReadAllText(file));
            exported.RootElement[0].GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);

            h.Run("globals", "import", "--file", file, "--upsert").ExitCode.Should().Be(ExitCodes.Success);
            var update = h.Server.LogEntries.Single(e => e.RequestMessage.Method == "PUT");
            using var body = JsonDocument.Parse(update.RequestMessage.Body!);
            body.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null,
                "the API interprets null as preserving the stored encrypted secret");
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("***")]
    [InlineData("replacement-password")]
    public void UpsertExistingSecret_PreservesOmittedOrLegacyMaskButAcceptsExplicitValue(string? value)
    {
        using var h = new CommandTestHarness();
        StubExisting(h, Guid.NewGuid(), true);
        WithImportFile(value, file =>
        {
            h.Run("globals", "import", "--file", file, "--upsert").ExitCode.Should().Be(ExitCodes.Success);
            var update = h.Server.LogEntries.Single(e => e.RequestMessage.Method == "PUT");
            using var body = JsonDocument.Parse(update.RequestMessage.Body!);
            body.RootElement.GetProperty("value").GetString().Should().Be(value == "***" ? null : value);
        });
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "***")]
    [InlineData(true, null)]
    [InlineData(true, "***")]
    public void MissingSecretValue_CannotCreateOrPromotePlainVariable(bool existingPlain, string? value)
    {
        using var h = new CommandTestHarness();
        if (existingPlain) StubExisting(h, Guid.NewGuid(), false);
        else h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));
        WithImportFile(value, file =>
        {
            h.Run("globals", "import", "--file", file, "--upsert").ExitCode.Should().Be(ExitCodes.Error);
            h.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Method != "GET");
        });
    }

    private static void StubExisting(CommandTestHarness h, Guid id, bool secret)
    {
        h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id, name = "SMTP_PASSWORD", value = secret ? "***" : "plain", isSecret = secret,
                    description = (string?)null, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow }
            }));
        h.Server.Given(Request.Create().WithPath($"/api/global-variables/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));
    }

    private static void WithImportFile(string? value, Action<string> action)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new[]
                { new { name = "SMTP_PASSWORD", value, isSecret = true, description = "updated description" } }));
            action(file);
        }
        finally { File.Delete(file); }
    }
}
