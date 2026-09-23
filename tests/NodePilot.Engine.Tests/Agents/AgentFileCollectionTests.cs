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

public sealed class AgentFileCollectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSourceCannotPublishMixedArtifact_AppendPreservesObservedPrefix(bool append)
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-collection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.log");
        var original = Enumerable.Repeat((byte)'A', AgentArtifactStore.TransferBlockBytes * 3).ToArray();
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(source, original, ct);
        var changed = false;
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string script, int? _, CancellationToken token) =>
            {
                var output = await AgentShellTests.Execute(script);
                if (!changed && script.Contains("ToBase64String", StringComparison.Ordinal))
                {
                    changed = true;
                    if (append) await File.AppendAllTextAsync(source, "appended after observation", token);
                    else
                    {
                        var replacement = Path.Combine(root, "replacement.log");
                        await File.WriteAllBytesAsync(replacement, Enumerable.Repeat((byte)'B', original.Length).ToArray(), token);
                        File.Move(replacement, source, overwrite: true);
                    }
                }
                return new RemoteExecutionResult { Success = true, Output = output };
            });
        var sessions = new Mock<IRemoteSessionFactory>();
        sessions.Setup(s => s.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var engine = Mock.Of<IPowerShellExecutionEngine>();
        var options = new Mock<IOptionsMonitor<AgentOptions>>();
        options.SetupGet(m => m.CurrentValue).Returns(new AgentOptions());
        await using var target = new AgentTarget(new ManagedMachine { Hostname = "target" }, new Credential(), false,
            sessions.Object, new PowerShellEngineFactory(engine, engine, engine), options.Object, "step", NullLogger.Instance);
        await using var store = new AgentArtifactStore(Guid.NewGuid());
        try
        {
            if (append)
            {
                var artifact = await AgentFileTools.CollectAsync(target, store, source, ct);
                Assert.Equal(original, await File.ReadAllBytesAsync(artifact.LocalPath, ct));
                Assert.Equal(original.Length, artifact.Length);
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => AgentFileTools.CollectAsync(target, store, source, ct));
                Assert.Empty(store.Artifacts);
                Assert.Empty(Directory.GetFiles(store.Root));
                // A stable subsequent read can still be collected.
                var artifact = await AgentFileTools.CollectAsync(target, store, source, ct);
                Assert.Equal(original.Length, artifact.Length);
                Assert.All(await File.ReadAllBytesAsync(artifact.LocalPath, ct), b => Assert.Equal((byte)'B', b));
            }
            Assert.True(changed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
