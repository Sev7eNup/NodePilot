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
/// Tests for FolderOperationActivity error cases, security guards, and edge cases.
/// Happy-path script generation (copy/move/delete/exists/list/create/rename) is covered
/// in BuildScriptTests.
/// </summary>
public sealed class FolderOperationActivityTests : IDisposable
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

    public FolderOperationActivityTests()
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

    private FolderOperationActivity CreateActivity(IConfiguration? cfg = null)
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
            Cfg("{\"operation\": \"copy\", \"path\": \"C:\\\\temp\\\\src\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*destination*");
    }

    [Fact]
    public async Task MoveWithoutDestination_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"move\", \"path\": \"C:\\\\temp\\\\src\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*destination*");
    }

    [Fact]
    public async Task RenameWithoutNewName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"rename\", \"path\": \"C:\\\\temp\\\\old\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*newName*");
    }

    [Theory]
    [InlineData("..\\archive")]
    [InlineData("../archive")]
    [InlineData("D:\\archive")]
    [InlineData("bad:name")]
    public async Task RenameWithPathLikeNewName_Throws(string newName)
    {
        var activity = CreateActivity();
        var json = JsonSerializer.Serialize(new
        {
            operation = "rename",
            path = "C:\\temp\\old",
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
            Cfg("{\"path\": \"C:\\\\temp\\\\dir\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*operation*");
    }

    [Fact]
    public async Task UnknownOperation_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"purge\", \"path\": \"C:\\\\temp\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*purge*");
    }

    [Fact]
    public async Task WildcardPath_RejectedBeforeScriptRuns()
    {
        var activity = CreateActivity();

        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\", \"path\": \"C:\\\\temp\\\\*\"}"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*wildcard*");
    }

    [Fact]
    public async Task WildcardDestination_RejectedBeforeScriptRuns()
    {
        var activity = CreateActivity();

        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"copy\", \"path\": \"C:\\\\src\", \"destination\": \"D:\\\\backup\\\\?\"}"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*wildcard*");
    }

    // ---- Security: apostrophe escaping ----

    [Fact]
    public async Task PathWithApostrophe_IsEscapedInScript()
    {
        var activity = CreateActivity();
        _capturedScript = null;
        await activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\", \"path\": \"C:\\\\O'Brian's files\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("'C:\\O''Brian''s files'");
    }

    // ---- Container assertion is emitted for destructive ops ----

    [Theory]
    [InlineData("delete")]
    [InlineData("list")]
    [InlineData("copy")]
    [InlineData("move")]
    [InlineData("rename")]
    public async Task DestructiveOps_EmitContainerAssertion(string op)
    {
        var activity = CreateActivity();
        _capturedScript = null;
        var json = op switch
        {
            "rename" => "{\"operation\": \"rename\", \"path\": \"C:\\\\dir\", \"newName\": \"new\"}",
            "copy" or "move" => $"{{\"operation\": \"{op}\", \"path\": \"C:\\\\src\", \"destination\": \"D:\\\\dst\"}}",
            _ => $"{{\"operation\": \"{op}\", \"path\": \"C:\\\\dir\"}}",
        };
        await activity.ExecuteAsync(Ctx(), Cfg(json), CancellationToken.None);
        _capturedScript.Should().Contain("-PathType Container");
        _capturedScript.Should().Contain("Not a directory:");
    }

    [Fact]
    public async Task Create_DoesNotAssertContainer()
    {
        // create skips the assertion: target must not exist yet, so checking PathType
        // would always fail. New-Item -Force is the no-op when the folder is already there.
        var activity = CreateActivity();
        _capturedScript = null;
        await activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"create\", \"path\": \"C:\\\\temp\\\\NewFolder\"}"),
            CancellationToken.None);
        _capturedScript.Should().NotContain("Not a directory:");
        _capturedScript.Should().Contain("$__path = 'C:\\temp\\NewFolder'");
        _capturedScript.Should().Contain("New-Item -Path $__path -ItemType Directory -Force");
    }

    // ---- Security: path traversal ----

    [Fact]
    public async Task TraversalPath_WhenFlagExplicitlyDisabled_Succeeds()
    {
        // Mirror of the file-op test: dev-mode escape hatch is explicit RejectTraversal=false.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileSystemOperation:RejectTraversal"] = "false" })
            .Build();
        var activity = CreateActivity(cfg);
        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"delete\", \"path\": \"C:\\\\temp\\\\..\\\\windows\"}"),
            CancellationToken.None);
        result.ErrorOutput.Should().NotContain("traversal");
    }

    [Fact]
    public async Task TraversalPath_OnDefault_Rejected()
    {
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
        var permissive = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileSystemOperation:RejectTraversal"] = "false" })
            .Build();
        var activity = CreateActivity(permissive);
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"copy\", \"path\": \"\\\\\\\\attacker.com\\\\share\\\\dir\", \"destination\": \"C:\\\\temp\\\\out\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UNC*");
    }

    // ---- PostProcess: structured JSON envelope ----

    private static string MarkerOutput(string json) => $$"""
        ###NODEPILOT_FOLDEROP_RESULT_START###
        {{json}}
        ###NODEPILOT_FOLDEROP_RESULT_END###
        """;

    [Fact]
    public async Task ExistsResult_IsExposedAsOutputParameter()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"exists\",\"path\":\"C:\\\\dir\",\"ok\":true,\"exists\":false}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"exists\",\"path\":\"C:\\\\dir\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("False");
        result.OutputParameters["exists"].Should().Be("false");
    }

    [Fact]
    public async Task ListResult_ExposesItemsAndCount()
    {
        var itemsJson = "[{\"name\":\"a.txt\",\"length\":42,\"lastWriteTime\":\"2026-05-15T12:00:00\",\"isFolder\":false}," +
                        "{\"name\":\"sub\",\"length\":null,\"lastWriteTime\":\"2026-05-14T08:00:00\",\"isFolder\":true}]";
        _scriptOutput = MarkerOutput($"{{\"operation\":\"list\",\"path\":\"C:\\\\dir\",\"ok\":true,\"items\":{itemsJson},\"count\":2,\"truncated\":false}}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"list\",\"path\":\"C:\\\\dir\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["count"].Should().Be("2");
        result.OutputParameters["items"].Should().Contain("a.txt").And.Contain("sub");
        result.OutputParameters["truncated"].Should().Be("false");
    }

    [Fact]
    public async Task ListResult_TruncatedFlag_IsPropagatedWhenCapHit()
    {
        // When the PowerShell script trims the items list down to the ListMaxItems cap, it
        // signals that via truncated=true while count still holds the real folder size. The
        // activity's post-process step must pass that through so downstream steps can detect
        // the overflow.
        var truncatedJson = "[{\"name\":\"a\",\"length\":1,\"lastWriteTime\":\"2026-05-15T12:00:00\",\"isFolder\":false}]";
        _scriptOutput = MarkerOutput($"{{\"operation\":\"list\",\"path\":\"C:\\\\big\",\"ok\":true,\"items\":{truncatedJson},\"count\":50000,\"truncated\":true}}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"list\",\"path\":\"C:\\\\big\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["count"].Should().Be("50000");
        result.OutputParameters["truncated"].Should().Be("true");
    }

    [Fact]
    public async Task CopyResult_ExposesDestinationParameter()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"copy\",\"path\":\"C:\\\\src\",\"destination\":\"D:\\\\dst\",\"ok\":true}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"copy\",\"path\":\"C:\\\\src\",\"destination\":\"D:\\\\dst\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["destination"].Should().Be("D:\\dst");
    }

    [Fact]
    public async Task RemoteFailureWithStructuredError_IsPropagated()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"delete\",\"path\":\"C:\\\\d\",\"ok\":false,\"error\":\"Not a directory: C:\\\\d\"}");
        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"delete\",\"path\":\"C:\\\\d\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Not a directory:");
    }
}
