using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public sealed class FileHashActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly Mock<IRemoteSession> _mockSession;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private string? _capturedScript;
    private string _scriptOutput = HashOutput("ABCDEF1234567890", "SHA256");

    public FileHashActivityTests()
    {
        _db = TestDbContext.Create();
        _credentialStore = new Mock<ICredentialStore>();

        _mockSession = new Mock<IRemoteSession>();
        _mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => _capturedScript = script)
            .ReturnsAsync(() => new RemoteExecutionResult { Success = true, Output = _scriptOutput });
        _mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory = new Mock<IRemoteSessionFactory>();
        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockSession.Object);

        _db.Credentials.Add(new Credential { Id = _credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });
        _db.ManagedMachines.Add(new ManagedMachine
        {
            Id = _machineId, Name = "S", Hostname = "host.local",
            WinRmPort = 5985, DefaultCredentialId = _credentialId, IsReachable = true
        });
        _db.SaveChanges();

        _credentialStore
            .Setup(cs => cs.GetAsync(_credentialId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential { Id = _credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });
    }

    public void Dispose() => _db.Dispose();

    private FileHashActivity CreateActivity(IConfiguration? cfg = null)
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
               cfg ?? new ConfigurationBuilder().Build());

    private StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", TargetMachineId = _machineId, CredentialId = _credentialId };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    private static string HashOutput(string hash, string algorithm) => $$"""
        ###NODEPILOT_FILEHASH_RESULT_START###
        {"hash":"{{hash}}","algorithm":"{{algorithm}}"}
        ###NODEPILOT_FILEHASH_RESULT_END###
        """;

    [Fact]
    public async Task MissingPath_Throws()
    {
        var act = CreateActivity();
        Func<Task> call = () => act.ExecuteAsync(Ctx(), Cfg("{\"algorithm\":\"SHA256\"}"), CancellationToken.None);
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*path*");
    }

    [Fact]
    public async Task UnsupportedAlgorithm_Throws()
    {
        var act = CreateActivity();
        Func<Task> call = () => act.ExecuteAsync(Ctx(),
            Cfg("{\"path\":\"C:\\\\file.txt\",\"algorithm\":\"CRC32\"}"),
            CancellationToken.None);
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*CRC32*");
    }

    [Fact]
    public async Task DefaultAlgorithm_IsSha256()
    {
        var act = CreateActivity();
        await act.ExecuteAsync(Ctx(), Cfg("{\"path\":\"C:\\\\file.txt\"}"), CancellationToken.None);
        _capturedScript.Should().Contain("$__npAlgorithm = 'SHA256'");
        _capturedScript.Should().Contain("HashAlgorithm]::Create($__npAlgorithm)");
        _capturedScript.Should().Contain("###NODEPILOT_FILEHASH_RESULT_START###");
    }

    [Fact]
    public async Task HappyPath_ReturnsHashAsOutputParameter()
    {
        _scriptOutput = HashOutput("DEADBEEF", "SHA1");
        var act = CreateActivity();
        var result = await act.ExecuteAsync(Ctx(),
            Cfg("{\"path\":\"C:\\\\file.txt\",\"algorithm\":\"SHA1\"}"),
            CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputParameters["hash"].Should().Be("DEADBEEF");
        result.OutputParameters["algorithm"].Should().Be("SHA1");
        result.OutputParameters["match"].Should().BeEmpty();
    }

    [Fact]
    public async Task ExpectedHashMatches_StepSucceeds()
    {
        _scriptOutput = HashOutput("ABCDEF", "SHA256");
        var act = CreateActivity();
        var result = await act.ExecuteAsync(Ctx(),
            Cfg("{\"path\":\"C:\\\\file.txt\",\"expected\":\"abcdef\"}"),
            CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputParameters["match"].Should().Be("true");
    }

    [Fact]
    public async Task ExpectedHashMismatches_StepFails()
    {
        _scriptOutput = HashOutput("AAAA", "SHA256");
        var act = CreateActivity();
        var result = await act.ExecuteAsync(Ctx(),
            Cfg("{\"path\":\"C:\\\\file.txt\",\"expected\":\"BBBB\"}"),
            CancellationToken.None);
        result.Success.Should().BeFalse();
        result.OutputParameters["match"].Should().Be("false");
        result.ErrorOutput.Should().Contain("mismatch");
    }

    [Fact]
    public async Task PathWithApostrophe_IsEscaped()
    {
        var act = CreateActivity();
        await act.ExecuteAsync(Ctx(),
            Cfg("{\"path\":\"C:\\\\Tom's Files\\\\f.txt\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__npPath = 'C:\\Tom''s Files\\f.txt'");
    }
}
