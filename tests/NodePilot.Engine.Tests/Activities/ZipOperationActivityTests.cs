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

public sealed class ZipOperationActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly Mock<IRemoteSession> _mockSession;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private string? _capturedScript;
    private string _scriptOutput = ZipOutput("compress", "C:\\out.zip", "12345");

    public ZipOperationActivityTests()
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

    private ZipOperationActivity CreateActivity(IConfiguration? cfg = null)
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
               cfg ?? new ConfigurationBuilder().Build());

    private StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", TargetMachineId = _machineId, CredentialId = _credentialId };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    private static string ZipOutput(string operation, string destination, string sizeBytes) => $$"""
        ###NODEPILOT_ZIP_RESULT_START###
        {"operation":"{{operation}}","destination":"{{destination.Replace("\\", "\\\\")}}","sizeBytes":{{sizeBytes}}}
        ###NODEPILOT_ZIP_RESULT_END###
        """;

    // The UI's operation dropdown shows "Compress (zip)" as visual default but won't
    // persist 'compress' to config until the user actually changes the dropdown. Workflows
    // authored without touching the dropdown used to fail with "'operation' is required".
    // The activity now defaults to "compress" so those workflows run.
    [Fact]
    public async Task MissingOperation_DefaultsToCompress()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"source\":\"C:\\\\src\",\"destination\":\"C:\\\\out.zip\"}"),
            CancellationToken.None);
        // BuildCompressScript writes to the destination, so capturing the script proves the
        // compress branch was taken (extract would read from source).
        _capturedScript.Should().Contain("Compress-Archive");
    }

    [Fact]
    public async Task EmptyOperation_DefaultsToCompress()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"\",\"source\":\"C:\\\\src\",\"destination\":\"C:\\\\out.zip\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Compress-Archive");
    }

    [Fact]
    public async Task UnknownOperation_Throws()
    {
        var act = CreateActivity();
        Func<Task> call = () => act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"tarball\",\"source\":\"C:\\\\s\",\"destination\":\"C:\\\\d.zip\"}"),
            CancellationToken.None);
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tarball*");
    }

    [Fact]
    public async Task CompressWithoutSource_Throws()
    {
        var act = CreateActivity();
        Func<Task> call = () => act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"compress\",\"destination\":\"C:\\\\out.zip\"}"),
            CancellationToken.None);
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*source*");
    }

    [Fact]
    public async Task UnsupportedCompressionLevel_Throws()
    {
        var act = CreateActivity();
        Func<Task> call = () => act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"compress\",\"source\":\"C:\\\\s\",\"destination\":\"C:\\\\d.zip\",\"compressionLevel\":\"Ultra\"}"),
            CancellationToken.None);
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ultra*");
    }

    [Fact]
    public async Task CompressBuildsExpectedScript()
    {
        var act = CreateActivity();
        await act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"compress\",\"source\":\"C:\\\\logs\\\\*.log\",\"destination\":\"C:\\\\out.zip\",\"force\":true,\"compressionLevel\":\"Fastest\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Compress-Archive");
        _capturedScript.Should().Contain("-CompressionLevel Fastest");
        _capturedScript.Should().Contain("-Force");
        _capturedScript.Should().Contain("###NODEPILOT_ZIP_RESULT_START###");
        // Source for compress uses -Path (glob expansion); destination uses literal.
        _capturedScript.Should().Contain("-Path 'C:\\logs\\*.log'");
        _capturedScript.Should().Contain("$__npDestination = 'C:\\out.zip'");
        _capturedScript.Should().Contain("-DestinationPath $__npDestination");
    }

    [Fact]
    public async Task ExtractBuildsExpectedScript()
    {
        var act = CreateActivity();
        await act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"extract\",\"source\":\"C:\\\\in.zip\",\"destination\":\"C:\\\\out\"}"),
            CancellationToken.None);
        // A2 hardening: source is bound to a variable so the Zip-Slip pre-scan can
        // re-use it. We assert on the assignment + the Expand-Archive invocation that
        // uses it, instead of the old literal-inline form.
        _capturedScript.Should().Contain("Expand-Archive");
        _capturedScript.Should().Contain("$__npSource = 'C:\\in.zip'");
        _capturedScript.Should().Contain("-LiteralPath $__npSource");
        _capturedScript.Should().Contain("$__npDestination = 'C:\\out'");
        _capturedScript.Should().Contain("-DestinationPath $__npDestination");
        // A2: Zip-Slip pre-scan is now baked into the extract path.
        _capturedScript.Should().Contain("Zip-Slip blocked");
        // -Force on Expand-Archive itself is gated by config.force (default false here),
        // so the cmdlet invocation should NOT carry the flag — even though the pre-scan
        // helper uses `-Force` on a New-Item call to materialise the destination dir.
        _capturedScript.Should().NotContain("Expand-Archive -LiteralPath $__npSource -DestinationPath $__npDestination -Force");
    }

    [Fact]
    public async Task ExtractWildcardSource_RejectedBeforeScriptRuns()
    {
        var act = CreateActivity();
        Func<Task> call = () => act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"extract\",\"source\":\"C:\\\\in*.zip\",\"destination\":\"C:\\\\out\"}"),
            CancellationToken.None);

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*wildcard*");
        _capturedScript.Should().BeNull();
    }

    [Fact]
    public async Task CompressWildcardDestination_RejectedBeforeScriptRuns()
    {
        var act = CreateActivity();
        Func<Task> call = () => act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"compress\",\"source\":\"C:\\\\logs\\\\*.log\",\"destination\":\"C:\\\\out*.zip\"}"),
            CancellationToken.None);

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*wildcard*");
        _capturedScript.Should().BeNull();
    }

    [Fact]
    public async Task CompressOutput_ParsesStructuredResultBlock()
    {
        _scriptOutput = ZipOutput("compress", "C:\\d.zip", "98765");
        var act = CreateActivity();
        var result = await act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"compress\",\"source\":\"C:\\\\s\",\"destination\":\"C:\\\\d.zip\"}"),
            CancellationToken.None);
        result.OutputParameters["sizeBytes"].Should().Be("98765");
        result.OutputParameters["destination"].Should().Be("C:\\d.zip");
    }
}
