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

/// <summary>
/// Tests for FileOperationActivity error cases, security guards, and edge cases.
/// Happy-path script generation (copy/move/delete/exists/rename) is covered in BuildScriptTests.
/// </summary>
public sealed class FileOperationActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly Mock<IRemoteSession> _mockSession;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private string? _capturedScript;
    private string _scriptOutput = "OK";

    public FileOperationActivityTests()
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

    private FileOperationActivity CreateActivity(IConfiguration? cfg = null)
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
               cfg ?? new ConfigurationBuilder().Build());

    private StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", TargetMachineId = _machineId, CredentialId = _credentialId };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Error cases ----

    [Fact]
    public async Task CopyWithoutDestination_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"copy\", \"path\": \"C:\\\\temp\\\\file.txt\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*destination*");
    }

    [Fact]
    public async Task MoveWithoutDestination_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"move\", \"path\": \"C:\\\\temp\\\\file.txt\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*destination*");
    }

    [Fact]
    public async Task RenameWithoutNewName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"rename\", \"path\": \"C:\\\\temp\\\\old.txt\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*newName*");
    }

    [Theory]
    [InlineData("..\\secrets.txt")]
    [InlineData("../secrets.txt")]
    [InlineData("C:\\temp\\secrets.txt")]
    [InlineData("bad:name.txt")]
    public async Task RenameWithPathLikeNewName_Throws(string newName)
    {
        var activity = CreateActivity();
        var json = JsonSerializer.Serialize(new
        {
            operation = "rename",
            path = "C:\\temp\\old.txt",
            newName,
        });

        Func<Task> act = () => activity.ExecuteAsync(Ctx(), Cfg(json), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*newName*");
    }

    [Fact]
    public async Task RenameOfAllowedRoot_ToSiblingOutsideRoot_Throws()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileSystemOperation:AllowedRoots:0"] = "C:\\data",
            })
            .Build();
        var activity = CreateActivity(cfg);

        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"rename\", \"path\": \"C:\\\\data\", \"newName\": \"data-renamed\"}"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AllowedRoots*");
    }

    [Fact]
    public async Task MissingPath_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*path*");
    }

    [Fact]
    public async Task MissingOperation_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"path\": \"C:\\\\temp\\\\file.txt\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*operation*");
    }

    [Fact]
    public async Task UnknownOperation_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"purge\", \"path\": \"C:\\\\temp\\\\f.txt\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*purge*");
    }

    [Fact]
    public async Task WildcardPath_RejectedBeforeScriptRuns()
    {
        var activity = CreateActivity();

        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\", \"path\": \"C:\\\\temp\\\\*.txt\"}"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*wildcard*");
    }

    [Fact]
    public async Task WildcardDestination_RejectedBeforeScriptRuns()
    {
        var activity = CreateActivity();

        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"copy\", \"path\": \"C:\\\\temp\\\\file.txt\", \"destination\": \"D:\\\\backup\\\\*.txt\"}"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*wildcard*");
    }

    // ---- Folder-specific operations are not allowed in fileOperation ----

    [Theory]
    [InlineData("list")]
    [InlineData("createDirectory")]
    [InlineData("deleteDirectory")]
    public async Task FolderOnlyOperations_Throw(string op)
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg($"{{\"operation\": \"{op}\", \"path\": \"C:\\\\temp\\\\x\"}}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- create: empty-file creation, with directory-collision protection ----

    [Fact]
    public async Task Create_EmitsNewItemWithFileType_AndContainerGuard()
    {
        var activity = CreateActivity();
        _capturedScript = null;
        await activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"create\", \"path\": \"C:\\\\temp\\\\new.txt\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__path = 'C:\\temp\\new.txt'");
        _capturedScript.Should().Contain("New-Item -Path $__path -ItemType File -Force");
        _capturedScript.Should().Contain("-PathType Container");
        _capturedScript.Should().Contain("Cannot create file: path exists as directory:");
        // No leaf-assertion: the target need NOT exist yet — that's the whole point of create.
        _capturedScript.Should().NotContain("Not a file:");
    }

    [Fact]
    public async Task Create_PathWithApostrophe_IsEscaped()
    {
        var activity = CreateActivity();
        _capturedScript = null;
        await activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"create\", \"path\": \"C:\\\\temp\\\\O'Brian's notes.txt\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("'C:\\temp\\O''Brian''s notes.txt'");
    }

    // ---- Security: apostrophe escaping ----

    [Fact]
    public async Task PathWithApostrophe_IsEscapedInScript()
    {
        var activity = CreateActivity();
        _capturedScript = null;
        await activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\", \"path\": \"C:\\\\O'Brian's files.txt\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("'C:\\O''Brian''s files.txt'");
    }

    // ---- Leaf assertion is emitted for destructive ops ----

    [Theory]
    [InlineData("delete")]
    [InlineData("copy")]
    [InlineData("move")]
    [InlineData("rename")]
    public async Task DestructiveOps_EmitLeafAssertion(string op)
    {
        var activity = CreateActivity();
        _capturedScript = null;
        var json = op switch
        {
            "rename" => "{\"operation\": \"rename\", \"path\": \"C:\\\\f.txt\", \"newName\": \"g.txt\"}",
            "copy" or "move" => $"{{\"operation\": \"{op}\", \"path\": \"C:\\\\f.txt\", \"destination\": \"D:\\\\g.txt\"}}",
            _ => $"{{\"operation\": \"{op}\", \"path\": \"C:\\\\f.txt\"}}",
        };
        await activity.ExecuteAsync(Ctx(), Cfg(json), CancellationToken.None);
        _capturedScript.Should().Contain("-PathType Leaf");
        _capturedScript.Should().Contain("Not a file:");
    }

    // ---- Security: path traversal ----

    [Fact]
    public async Task TraversalPath_WhenFlagExplicitlyDisabled_Succeeds()
    {
        // Phase-3 hardening flipped the default to "reject" — the dev-mode escape hatch
        // is to set RejectTraversal=false explicitly (mirrors appsettings.Development.json).
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileSystemOperation:RejectTraversal"] = "false" })
            .Build();
        var activity = CreateActivity(cfg);
        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\", \"path\": \"C:\\\\temp\\\\..\\\\windows\\\\f.txt\"}"),
            CancellationToken.None);
        result.ErrorOutput.Should().NotContain("traversal");
    }

    [Fact]
    public async Task TraversalPath_OnDefault_Rejected()
    {
        // No config → PathGuard now treats the missing key as "reject". Empty IConfiguration
        // is the worst-case "stripped down deployment" scenario; we want it on the safe side.
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\", \"path\": \"../../etc/passwd\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*traversal*");
    }

    [Fact]
    public async Task TraversalPath_WhenFlagExplicitlyEnabled_Throws()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileSystemOperation:RejectTraversal"] = "true" })
            .Build();
        var activity = CreateActivity(cfg);
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\", \"path\": \"../../etc/passwd\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*traversal*");
    }

    [Fact]
    public async Task UncPath_RejectedRegardlessOfFlag()
    {
        // UNC reject is unconditional — the rule fires even when the operator opts out of
        // traversal rejection. NTLM-relay risk doesn't go away just because somebody flipped
        // RejectTraversal=false for legacy admin scripts.
        var permissive = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileSystemOperation:RejectTraversal"] = "false" })
            .Build();
        var activity = CreateActivity(permissive);
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"copy\", \"path\": \"\\\\\\\\attacker.com\\\\share\\\\evil.exe\", \"destination\": \"C:\\\\temp\\\\out.exe\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UNC*");
    }

    // ---- PostProcess: structured JSON envelope ----

    private static string MarkerOutput(string json) => $$"""
        ###NODEPILOT_FILEOP_RESULT_START###
        {{json}}
        ###NODEPILOT_FILEOP_RESULT_END###
        """;

    [Fact]
    public async Task ExistsResult_IsExposedAsOutputParameter()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"exists\",\"path\":\"C:\\\\f.txt\",\"ok\":true,\"exists\":true}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"exists\",\"path\":\"C:\\\\f.txt\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("True");
        result.OutputParameters["operation"].Should().Be("exists");
        result.OutputParameters["exists"].Should().Be("true");
        result.OutputParameters["path"].Should().Be("C:\\f.txt");
    }

    [Fact]
    public async Task CopyResult_ExposesDestinationParameter()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"copy\",\"path\":\"C:\\\\a.txt\",\"destination\":\"D:\\\\b.txt\",\"ok\":true}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"copy\",\"path\":\"C:\\\\a.txt\",\"destination\":\"D:\\\\b.txt\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["destination"].Should().Be("D:\\b.txt");
        result.Output.Should().Contain("copy: C:\\a.txt -> D:\\b.txt");
    }

    [Fact]
    public async Task CreateResult_ExposesFullNameParameter()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"create\",\"path\":\"C:\\\\new.txt\",\"ok\":true,\"fullName\":\"C:\\\\new.txt\",\"creationTime\":\"2026-05-15T12:00:00.0000000\"}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"create\",\"path\":\"C:\\\\new.txt\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["fullName"].Should().Be("C:\\new.txt");
        result.OutputParameters["creationTime"].Should().Be("2026-05-15T12:00:00.0000000");
    }

    [Fact]
    public async Task RenameResult_ExposesNewPathParameter()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"rename\",\"path\":\"C:\\\\old.txt\",\"ok\":true,\"newPath\":\"C:\\\\new.txt\",\"newName\":\"new.txt\"}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"rename\",\"path\":\"C:\\\\old.txt\",\"newName\":\"new.txt\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["newPath"].Should().Be("C:\\new.txt");
        result.OutputParameters["newName"].Should().Be("new.txt");
    }

    [Fact]
    public async Task RemoteFailureWithStructuredError_IsPropagated()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"delete\",\"path\":\"C:\\\\f.txt\",\"ok\":false,\"error\":\"Not a file: C:\\\\f.txt\"}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"delete\",\"path\":\"C:\\\\f.txt\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Not a file:");
    }
}
