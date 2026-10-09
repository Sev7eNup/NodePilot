using System.Text;
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

public sealed class AgentFileReadTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("ascii")]
    public async Task ReadPages_RetainEncodingAndWholeCharacters(string format)
    {
        Encoding encoding = format switch
        {
            "utf16le" => new UnicodeEncoding(false, true),
            "utf16be" => new UnicodeEncoding(true, true),
            _ => new UTF8Encoding(false),
        };
        var content = format == "ascii" ? new string('a', 18000)
            : new string('a', format == "utf8" ? 8191 : 4094) + "😀€中文" + new string('b', 9000);
        var path = Path.Combine(Path.GetTempPath(), "np-agent-text-" + Guid.NewGuid().ToString("N") + ".log");
        await File.WriteAllTextAsync(path, content, encoding, TestContext.Current.CancellationToken);
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns((string script, int? _, CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                using var shell = System.Management.Automation.PowerShell.Create();
                shell.AddScript(script);
                var result = string.Join("\n", shell.Invoke().Select(item => item.ToString()));
                Assert.False(shell.HadErrors);
                return Task.FromResult(new RemoteExecutionResult { Success = true, Output = result });
            });
        var sessions = new Mock<IRemoteSessionFactory>();
        sessions.Setup(s => s.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var options = new Mock<IOptionsMonitor<AgentOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new AgentOptions());
        var engine = Mock.Of<IPowerShellExecutionEngine>();
        await using var target = new AgentTarget(new ManagedMachine { Hostname = "test-target" }, new Credential(), false,
            sessions.Object, new PowerShellEngineFactory(engine, engine, engine), options.Object, "step", NullLogger.Instance);
        try
        {
            long offset = 0;
            var text = new StringBuilder();
            var size = new FileInfo(path).Length;
            while (offset < size)
            {
                using var page = JsonDocument.Parse(await AgentFileTools.ReadAsync(target, path, offset, TestContext.Current.CancellationToken));
                var next = page.RootElement.GetProperty("nextOffset").GetInt64();
                Assert.InRange(next - offset, 1, 8192);
                text.Append(page.RootElement.GetProperty("text").GetString());
                offset = next;
            }
            Assert.Equal(size, offset);
            Assert.Equal(content, text.ToString().TrimStart('\uFEFF'));
        }
        finally { File.Delete(path); }
    }
}
