using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Agents;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentTimeEvidenceTests
{
    [Theory]
    [InlineData("de-DE", "2026-09-19T08:56:00.7399345Z")]
    [InlineData("en-US", "2026-11-01T08:30:00.1234567Z")]
    [InlineData("en-US", "2026-11-01T09:30:00.1234567Z")]
    public async Task FileMetadata_UsesExactIsoUtcInsteadOfLegacyEpoch(string culture, string timestamp)
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "CBS.log");
        try
        {
            await File.WriteAllTextAsync(path, "2026-09-19 01:53:40 ERROR original source time without zone", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, DateTime.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            var session = new Mock<IRemoteSession>();
            session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .Returns(async (string script, int? _, CancellationToken _) => new RemoteExecutionResult
                {
                    Success = true,
                    Output = await AgentShellTests.Execute("[Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo('" + culture + "'); " + script)
                });
            var factory = new Mock<IRemoteSessionFactory>();
            factory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
            var engine = Mock.Of<IPowerShellExecutionEngine>();
            var monitor = new Mock<IOptionsMonitor<AgentOptions>>(); monitor.SetupGet(x => x.CurrentValue).Returns(new AgentOptions());
            await using var target = new AgentTarget(new ManagedMachine { Hostname = "source-target" }, new Credential(), false, factory.Object,
                new PowerShellEngineFactory(engine, engine, engine), monitor.Object, "time", NullLogger.Instance);
            using var result = JsonDocument.Parse(await AgentFileTools.ListAsync(target, root, "CBS.log", TestContext.Current.CancellationToken));
            var entry = result.RootElement.ValueKind == JsonValueKind.Array ? result.RootElement[0] : result.RootElement;
            Assert.Equal(timestamp, entry.GetProperty("LastWriteTimeUtc").GetString());
            Assert.DoesNotContain("/Date(", result.RootElement.GetRawText());
            Assert.Contains("2026-09-19 01:53:40 ERROR", await AgentFileTools.ReadAsync(target, path, 0, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ShellTimeContext_DescribesTheTargetClockWithoutRewritingSourceText()
    {
        var before = DateTimeOffset.UtcNow;
        const string raw = "2026-09-19 01:53:40 ERROR source time has no zone";
        using var result = JsonDocument.Parse(await AgentShellTests.Execute(AgentProcessScript.Build("powershell", "Write-Output '" + raw + "'", null, null)));
        Assert.Equal(raw, result.RootElement.GetProperty("stdout").GetString()!.Trim());
        var context = result.RootElement.GetProperty("timeContext");
        var observed = DateTimeOffset.Parse(context.GetProperty("observedAtUtc").GetString()!, CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, observed.Offset);
        Assert.InRange(observed, before, DateTimeOffset.UtcNow);
        Assert.Equal(TimeZoneInfo.Local.Id, context.GetProperty("targetTimeZoneId").GetString());
        var offset = DateTimeOffset.Parse("2000-01-01T00:00:00" + context.GetProperty("observedUtcOffset").GetString(), CultureInfo.InvariantCulture).Offset;
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(observed), offset);
    }
}
