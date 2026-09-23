using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Agents;
using NodePilot.Engine.PowerShell;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentSkillTests
{
    internal static byte[] Package(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return stream.ToArray();
    }

    private const string Instructions = "---\nname: windows-diagnostics\ndescription: >-\n  Windows update diagnosis\n  using bounded log excerpts.\n---\nRead evidence before making changes.";

    [Theory]
    [InlineData("Restricted", "Write-Output 'must not run'", "execution_policy_blocked")]
    [InlineData("AllSigned", "Write-Output 'must not run'", "skill_signature_required")]
    [InlineData("RemoteSigned", "Get-Item -LiteralPath 'C:\\NodePilot-Nonexistent-Skill-Test-File'", "process_failed")]
    public async Task SkillRunner_ReportsFailure_AndCleansStaging(string policy, string source, string code)
    {
        await using var db = TestDbFactory.Create();
        var bytes = Package(("SKILL.md", Instructions), ("scripts/check.ps1", source));
        var parsed = AgentSkillArchive.Read(bytes);
        var id = Guid.NewGuid();
        db.AgentSkillPackages.Add(new AgentSkillPackage { Id = id, Name = parsed.Name, Version = "1", Description = parsed.Description, Sha256 = parsed.Sha256, Package = bytes });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var monitor = new Mock<IOptionsMonitor<AgentOptions>>(); monitor.SetupGet(m => m.CurrentValue).Returns(new AgentOptions());
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string script, int? _, CancellationToken _) => new RemoteExecutionResult
            {
                Success = true,
                Output = await AgentShellTests.Execute("$env:PSExecutionPolicyPreference='" + policy + "'; " + script)
            });
        var factory = new Mock<IRemoteSessionFactory>();
        factory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        var engine = Mock.Of<IPowerShellExecutionEngine>();
        var target = new AgentTarget(new ManagedMachine { Hostname = "bound-target" }, new Credential(), false, factory.Object,
            new PowerShellEngineFactory(engine, engine, engine), monitor.Object, "skill", NullLogger.Instance);
        string staging;
        try
        {
            var tools = await AgentSkillTools.CreateAsync(db, new AgentDefinition { SkillIds = [id], Tools = [new() { Name = "powershell" }] }, target, Guid.NewGuid(), TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<AgentToolExecutionException>(() => tools.Single(t => t.Name == "run_skill_script").InvokeAsync(
                JsonSerializer.SerializeToElement(new { skillId = id, path = "scripts/check.ps1" }), TestContext.Current.CancellationToken));
            Assert.Contains(code, error.Message);
            Assert.Equal(code, error.Code);
            if (code == "process_failed")
            {
                Assert.Equal(1, error.Details!.Value.GetProperty("exitCode").GetInt32());
                Assert.Contains("NodePilot-Nonexistent", error.Details.Value.GetProperty("stderr").GetString());
            }
            staging = target.WorkingRoot;
            if (policy == "Restricted") Assert.Empty(staging);
        }
        finally { await target.DisposeAsync(); }
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void ShippedDiagnosticScripts_PassTheReadPolicy()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "samples", "agent-skills"))) root = root.Parent;
        Assert.NotNull(root);
        var scripts = Directory.GetFiles(Path.Combine(root.FullName, "samples", "agent-skills"), "*.ps1", SearchOption.AllDirectories);
        Assert.NotEmpty(scripts);
        foreach (var script in scripts) AgentPermissionPolicy.ValidateSkillScript("powershell", File.ReadAllText(script), []);
    }

    [Fact]
    public void Skill_ParsesStandardYamlAndImmutableIntegrityHash()
    {
        var bytes = Package(("SKILL.md", Instructions), ("scripts/check.ps1", "Write-Output 'ok'"));
        var result = AgentSkillArchive.Read(bytes);
        Assert.Equal("windows-diagnostics", result.Name);
        Assert.Equal("Windows update diagnosis using bounded log excerpts.", result.Description);
        Assert.Contains("Read evidence", result.Instructions);
        Assert.Equal(64, result.Sha256.Length);
        Assert.Equal(result.Sha256, AgentSkillArchive.Read(bytes).Sha256);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SkillRunner_TransfersAndVerifiesResources_ExecutesOnBoundSession_ThenCleansUpEvenAfterRevocation(bool hashCmdletAvailable)
    {
        await using var db = TestDbFactory.Create();
        var bytes = Package(("SKILL.md", Instructions), ("resources/value.txt", "bound-target-resource"),
            ("scripts/check.ps1", "param([string]$Value)\nWrite-Output 'bound-target-resource'\nWrite-Output $Value"));
        var parsed = AgentSkillArchive.Read(bytes);
        var id = Guid.NewGuid();
        db.AgentSkillPackages.Add(new AgentSkillPackage { Id = id, Name = parsed.Name, Version = "1", Description = parsed.Description, Sha256 = parsed.Sha256, Package = bytes });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var options = new AgentOptions();
        var monitor = new Mock<IOptionsMonitor<AgentOptions>>(); monitor.SetupGet(m => m.CurrentValue).Returns(options);
        var session = new Mock<IRemoteSession>();
        // The transport is simulated; every uploaded script, hash check and command runs in Windows PowerShell.
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string script, int? _, CancellationToken _) => new RemoteExecutionResult
            {
                Success = true,
                Output = await AgentShellTests.Execute("$env:PSExecutionPolicyPreference='RemoteSigned'; " + (hashCmdletAvailable ? "" : "function Get-FileHash { throw 'Hash cmdlet is unavailable in this host.' }; ") + script)
            });
        var factory = new Mock<IRemoteSessionFactory>();
        factory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        var engine = Mock.Of<IPowerShellExecutionEngine>();
        var target = new AgentTarget(new ManagedMachine { Hostname = "bound-target" }, new Credential(), false, factory.Object,
            new PowerShellEngineFactory(engine, engine, engine), monitor.Object, "skill", NullLogger.Instance);
        string? staging = null;
        try
        {
            var definition = new AgentDefinition { SkillIds = [id], Tools = [new AgentToolSelection { Name = "powershell" }] };
            var tools = await AgentSkillTools.CreateAsync(db, definition, target, Guid.NewGuid(), TestContext.Current.CancellationToken);
            using var result = JsonDocument.Parse(await tools.Single(t => t.Name == "run_skill_script").InvokeAsync(
                JsonSerializer.SerializeToElement(new { skillId = id, path = "scripts/check.ps1", arguments = new[] { "quoted ' & input" } }), TestContext.Current.CancellationToken));
            Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("bound-target-resource", result.RootElement.GetProperty("stdout").GetString());
            Assert.Contains("quoted ' & input", result.RootElement.GetProperty("stdout").GetString());
            staging = target.WorkingRoot;
            Assert.True(Directory.Exists(staging));
            Assert.Equal("bound-target-resource", await File.ReadAllTextAsync(Path.Combine(staging, id.ToString(), "resources", "value.txt"), TestContext.Current.CancellationToken));
            var scriptPath = Path.Combine(staging, id.ToString(), "scripts", "check.ps1");
            Assert.Equal(parsed.Files["scripts/check.ps1"], await File.ReadAllBytesAsync(scriptPath, TestContext.Current.CancellationToken));
            await File.AppendAllTextAsync(scriptPath, "\n# externally changed", TestContext.Current.CancellationToken);
            var changed = await Assert.ThrowsAsync<AgentToolExecutionException>(() => tools.Single(t => t.Name == "run_skill_script").InvokeAsync(
                JsonSerializer.SerializeToElement(new { skillId = id, path = "scripts/check.ps1", arguments = new[] { "value" } }), TestContext.Current.CancellationToken));
            Assert.Equal("skill_integrity_failed", changed.Code);
            options.Enabled = false;
        }
        finally { await target.DisposeAsync(); }
        Assert.False(Directory.Exists(staging));
        factory.Verify(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData("../escape.ps1")]
    [InlineData("scripts/../../escape.ps1")]
    [InlineData("C:/escape.ps1")]
    [InlineData("scripts\\escape.ps1")]
    [InlineData("scripts/CON.ps1")]
    [InlineData("scripts/file.ps1:stream")]
    [InlineData("scripts/file. ")]
    public void Skill_RejectsUnsafePackagePaths(string path)
        => Assert.Throws<ArgumentException>(() => AgentSkillArchive.Read(Package(("SKILL.md", Instructions), (path, "bad"))));

    [Fact]
    public void Skill_RejectsCaseInsensitiveDuplicatePaths()
        => Assert.Throws<ArgumentException>(() => AgentSkillArchive.Read(Package(("SKILL.md", Instructions), ("skill.md", Instructions))));

    [Fact]
    public async Task Skills_OnlySelectedPackagesAreVisible_AndRevocationIsCheckedForEveryCall()
    {
        await using var db = TestDbFactory.Create();
        var bytes = Package(("SKILL.md", Instructions), ("scripts/check.ps1", "Write-Output 'ok'"));
        var parsed = AgentSkillArchive.Read(bytes);
        var id = Guid.NewGuid();
        db.AgentSkillPackages.Add(new AgentSkillPackage { Id = id, Name = parsed.Name, Version = "1", Description = parsed.Description, Sha256 = parsed.Sha256, Package = bytes });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var tools = await AgentSkillTools.CreateAsync(db, new AgentDefinition { SkillIds = [id] }, null, Guid.NewGuid(), TestContext.Current.CancellationToken);
        var load = tools.Single(t => t.Name == "load_skill");
        Assert.Contains("already loaded", await load.InvokeAsync(JsonSerializer.SerializeToElement(new { skillId = id }), TestContext.Current.CancellationToken));
        Assert.Contains("Read evidence", Assert.Single(load.Skills).Instructions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => load.InvokeAsync(JsonSerializer.SerializeToElement(new { skillId = Guid.NewGuid() }), TestContext.Current.CancellationToken));
        var runner = tools.Single(t => t.Name == "run_skill_script");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.InvokeAsync(JsonSerializer.SerializeToElement(new { skillId = id, path = "scripts/check.ps1" }), TestContext.Current.CancellationToken));
        await db.AgentSkillPackages.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => load.InvokeAsync(JsonSerializer.SerializeToElement(new { skillId = id }), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => load.Skills[0].ValidateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResourcePagesRespectSerializedBudgetAndUtf8Boundaries()
    {
        await using var db = TestDbFactory.Create();
        var content = string.Concat(Enumerable.Repeat("a€😀\"\\", 3000));
        var bytes = Package(("SKILL.md", Instructions), ("references/example.md", content));
        var parsed = AgentSkillArchive.Read(bytes);
        var id = Guid.NewGuid();
        db.AgentSkillPackages.Add(new AgentSkillPackage { Id = id, Name = parsed.Name, Version = "1", Description = parsed.Description, Sha256 = parsed.Sha256, Package = bytes });
        var ct = TestContext.Current.CancellationToken;
        await db.SaveChangesAsync(ct);
        var tools = await AgentSkillTools.CreateAsync(db, new AgentDefinition { SkillIds = [id] }, null, Guid.NewGuid(), ct, 1024);
        var read = tools.Single(t => t.Name == "read_skill_resource");
        var assembled = new StringBuilder();
        var offset = 0;
        while (true)
        {
            var text = await read.InvokeAsync(JsonSerializer.SerializeToElement(new { skillId = id, path = "references/example.md", offset }), ct);
            Assert.True(text.Length <= 1024);
            using var page = JsonDocument.Parse(text);
            assembled.Append(page.RootElement.GetProperty("text").GetString());
            var next = page.RootElement.GetProperty("nextOffset").GetInt32();
            Assert.True(next > offset);
            offset = next;
            if (!page.RootElement.GetProperty("hasMore").GetBoolean()) break;
        }
        Assert.Equal(content, assembled.ToString());
        Assert.Equal(Encoding.UTF8.GetByteCount(content), offset);
        await Assert.ThrowsAsync<ArgumentException>(() => read.InvokeAsync(JsonSerializer.SerializeToElement(new { skillId = id, path = "references/example.md", offset = 2 }), ct));
    }
}
