using System.Diagnostics;
using System.Text;
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

    private StepExecutionContext LocalCtx()
    {
        var machineId = Guid.NewGuid();
        _db.ManagedMachines.Add(new ManagedMachine
        {
            Id = machineId,
            Name = "Local " + machineId.ToString("N"),
            Hostname = "localhost",
            WinRmPort = 5985,
            IsReachable = true,
        });
        _db.SaveChanges();
        return new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "local-folder-operation",
            TargetMachineId = machineId,
        };
    }

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

    // ---- Link-local container assertion is emitted for destructive ops ----

    [Theory]
    [InlineData("delete")]
    [InlineData("list")]
    [InlineData("copy")]
    [InlineData("move")]
    [InlineData("rename")]
    public async Task DestructiveOps_EmitLinkLocalContainerAssertion(string op)
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
        _capturedScript.Should().Contain("Get-NodePilotPathAttributes -Path $__path");
        _capturedScript.Should().Contain("FileAttributes]::ReparsePoint");
        _capturedScript.Should().Contain("Not a directory:");
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task TransferOps_ValidateEffectiveDestinationRoot(string operation)
    {
        await CreateActivity().ExecuteAsync(
            Ctx(),
            Cfg($"{{\"operation\":\"{operation}\",\"path\":\"C:\\\\source\",\"destination\":\"C:\\\\destination\"}}"),
            CancellationToken.None);

        _capturedScript.Should().Contain("Get-NodePilotEffectiveDestination");
        _capturedScript.Should().Contain(
            "Assert-NodePilotAllowedPath -Candidate $__effectiveDestination");
    }

    [Fact]
    public async Task Rename_ValidatesTargetBeforeLinkLocalExistenceProbe()
    {
        await CreateActivity().ExecuteAsync(
            Ctx(),
            Cfg("{\"operation\":\"rename\",\"path\":\"C:\\\\source\",\"newName\":\"renamed\"}"),
            CancellationToken.None);

        var targetGuard = _capturedScript!.IndexOf(
            "Assert-NodePilotAllowedPath -Candidate $__target -Label 'rename target'",
            StringComparison.Ordinal);
        var existenceProbe = _capturedScript.IndexOf(
            "Get-NodePilotPathAttributes -Path $__target",
            StringComparison.Ordinal);
        targetGuard.Should().BeGreaterThan(-1);
        existenceProbe.Should().BeGreaterThan(targetGuard);
    }

    [Fact]
    public async Task Copy_EmitsControlledNoFollowTreeWalk()
    {
        await CreateActivity().ExecuteAsync(
            Ctx(),
            Cfg("{\"operation\":\"copy\",\"path\":\"C:\\\\source\",\"destination\":\"C:\\\\destination\"}"),
            CancellationToken.None);

        _capturedScript.Should().Contain("[System.IO.Directory]::EnumerateFileSystemEntries");
        _capturedScript.Should().Contain("[System.IO.File]::Copy");
        _capturedScript.Should().NotContain("Copy-Item -LiteralPath $__path");
    }

    [WindowsFact]
    public async Task Copy_RejectsNestedSourceReparsePointWithoutReadingOutsideTree()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-folder-copy-link-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(stage, "source");
        var destination = Path.Combine(stage, "destination");
        var outside = Path.Combine(stage, "outside");
        var sourceLink = Path.Combine(source, "nested-link");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(source, "safe.txt"), "safe");
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "must-not-copy");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(sourceLink, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FileSystemOperation:AllowedRoots:0"] = stage,
                }).Build();
            var result = await CreateActivity(config).ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new { operation = "copy", path = source, destination })),
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("reparse point");
            File.Exists(Path.Combine(destination, "nested-link", "secret.txt")).Should().BeFalse();
        }
        finally
        {
            DeleteReparsePointOnly(sourceLink);
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Copy_RejectsReparseEffectiveDestinationRoot()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-folder-copy-destination-link-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(stage, "source");
        var destinationParent = Path.Combine(stage, "destination-parent");
        var effectiveDestination = Path.Combine(destinationParent, Path.GetFileName(source));
        var outside = Path.Combine(stage, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationParent);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(source, "payload.txt"), "must-not-copy");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(effectiveDestination, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FileSystemOperation:AllowedRoots:0"] = stage,
                }).Build();
            var result = await CreateActivity(config).ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new
                {
                    operation = "copy",
                    path = source,
                    destination = destinationParent,
                })),
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("reparse point");
            File.Exists(Path.Combine(outside, "payload.txt")).Should().BeFalse();
        }
        finally
        {
            DeleteReparsePointOnly(effectiveDestination);
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Copy_RecursivelyCopiesReparseFreeTree()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-folder-copy-safe-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(stage, "source");
        var destination = Path.Combine(stage, "destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "payload.txt"), "safe-copy");

        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FileSystemOperation:AllowedRoots:0"] = stage,
                }).Build();
            var result = await CreateActivity(config).ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new { operation = "copy", path = source, destination })),
                CancellationToken.None);

            result.Success.Should().BeTrue(result.ErrorOutput);
            (await File.ReadAllTextAsync(Path.Combine(destination, "nested", "payload.txt")))
                .Should().Be("safe-copy");
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Copy_WindowsPowerShell51CopiesSafeTree()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-folder-copy-winps51-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(stage, "source");
        var destinationParent = Path.Combine(stage, "destination-parent");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(destinationParent);
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "payload.txt"), "winps51-safe");

        try
        {
            _capturedScript = null;
            await CreateActivity().ExecuteAsync(
                Ctx(),
                Cfg(JsonSerializer.Serialize(new
                {
                    operation = "copy",
                    path = source,
                    destination = destinationParent,
                })),
                CancellationToken.None);

            var (exitCode, stdout, stderr) = await RunWithWindowsPowerShell51(
                stage,
                "folder-copy-safe.ps1",
                _capturedScript!);

            exitCode.Should().Be(0, $"stdout: {stdout}{Environment.NewLine}stderr: {stderr}");
            stdout.Should().Contain("\"ok\":true");
            (await File.ReadAllTextAsync(Path.Combine(
                    destinationParent,
                    "source",
                    "nested",
                    "payload.txt")))
                .Should().Be("winps51-safe");
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Copy_WindowsPowerShell51RejectsNestedReparsePoint()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-folder-copy-winps51-link-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(stage, "source");
        var destination = Path.Combine(stage, "destination");
        var outside = Path.Combine(stage, "outside");
        var sourceLink = Path.Combine(source, "nested-link");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "must-not-copy");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(sourceLink, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            _capturedScript = null;
            await CreateActivity().ExecuteAsync(
                Ctx(),
                Cfg(JsonSerializer.Serialize(new { operation = "copy", path = source, destination })),
                CancellationToken.None);

            var (exitCode, stdout, stderr) = await RunWithWindowsPowerShell51(
                stage,
                "folder-copy-link.ps1",
                _capturedScript!);

            exitCode.Should().Be(0, $"stdout: {stdout}{Environment.NewLine}stderr: {stderr}");
            stdout.Should().Contain("\"ok\":false");
            stdout.Should().Contain("reparse point");
            File.Exists(Path.Combine(destination, "nested-link", "secret.txt")).Should().BeFalse();
        }
        finally
        {
            DeleteReparsePointOnly(sourceLink);
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
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

    private static void DeleteReparsePointOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                return;
            if ((attributes & FileAttributes.Directory) != 0)
                Directory.Delete(path);
            else
                File.Delete(path);
        }
        catch { }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithWindowsPowerShell51(
        string stage,
        string scriptName,
        string script)
    {
        var scriptPath = Path.Combine(stage, scriptName);
        await File.WriteAllTextAsync(scriptPath, script, Encoding.Unicode);
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", scriptPath,
            },
        })!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
