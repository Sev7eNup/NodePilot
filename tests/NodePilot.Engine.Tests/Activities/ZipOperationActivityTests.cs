using System.IO.Compression;
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
            StepId = "local-extract",
            TargetMachineId = machineId,
        };
    }

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    private static string ZipOutput(string operation, string destination, string sizeBytes) => $$"""
        ###NODEPILOT_ZIP_RESULT_START###
        {"operation":"{{operation}}","destination":"{{destination.Replace("\\", "\\\\")}}","sizeBytes":{{sizeBytes}}}
        ###NODEPILOT_ZIP_RESULT_END###
        """;

    // The UI can omit its visual "compress" default, so the activity applies the same default.
    [Fact]
    public async Task MissingOperation_DefaultsToCompress()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"source\":\"C:\\\\src\",\"destination\":\"C:\\\\out.zip\"}"),
            CancellationToken.None);
        // The compress branch builds an explicit, validated manifest and writes ZipArchive
        // entries itself; the extract branch opens ZipArchiveMode.Read.
        _capturedScript.Should().Contain("ZipArchiveMode]::Create");
    }

    [Fact]
    public async Task EmptyOperation_DefaultsToCompress()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"\",\"source\":\"C:\\\\src\",\"destination\":\"C:\\\\out.zip\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("ZipArchiveMode]::Create");
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
        _capturedScript.Should().NotContain("Compress-Archive");
        _capturedScript.Should().Contain("CompressionLevel]::Fastest");
        _capturedScript.Should().Contain("$__npForce = $true");
        _capturedScript.Should().Contain("###NODEPILOT_ZIP_RESULT_START###");
        // Wildcards are expanded only by a top-level .NET directory enumeration. No
        // PowerShell-provider wildcard expansion or second recursive archive walk remains.
        _capturedScript.Should().Contain("$__npSource = 'C:\\logs\\*.log'");
        _capturedScript.Should().Contain("[System.IO.Directory]::EnumerateFileSystemEntries(");
        _capturedScript.Should().Contain("$__npManifest");
        _capturedScript.Should().Contain("FileMode]::CreateNew");
        _capturedScript.Should().Contain("$__npDestination = 'C:\\out.zip'");
    }

    [Fact]
    public async Task Compress_WithAllowedRoots_InjectsTargetSideGuardForBothPaths()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["FileSystemOperation:AllowedRoots:0"] = "C:\\data",
            }).Build();

        await CreateActivity(config).ExecuteAsync(
            Ctx(),
            Cfg("{\"operation\":\"compress\",\"source\":\"C:\\\\data\\\\*.log\",\"destination\":\"C:\\\\data\\\\out.zip\"}"),
            CancellationToken.None);

        _capturedScript.Should().Contain("function Assert-NodePilotAllowedPath");
        _capturedScript.Should().Contain("Assert-NodePilotAllowedPath -Candidate ($__npSource)");
        _capturedScript.Should().Contain("Assert-NodePilotAllowedPath -Candidate ($__npDestination)");
        _capturedScript.Should().Contain("FileAttributes]::ReparsePoint");
    }

    [WindowsFact]
    public async Task Compress_ExpandsSafeWildcardAndWritesArchive()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-zip-compress-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(stage, "source");
        var destination = Path.Combine(stage, "out.zip");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "payload.txt"), "safe-payload");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FileSystemOperation:AllowedRoots:0"] = stage,
                }).Build();
            var result = await CreateActivity(config).ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new
                {
                    operation = "compress",
                    source = Path.Combine(sourceDir, "*.txt"),
                    destination,
                })),
                CancellationToken.None);

            result.Success.Should().BeTrue(result.ErrorOutput);
            using var archive = ZipFile.OpenRead(destination);
            archive.Entries.Select(entry => entry.FullName).Should().Contain("payload.txt");
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Compress_RejectsWildcardSelectedJunction()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-zip-wildcard-link-" + Guid.NewGuid().ToString("N"));
        var allowed = Path.Combine(stage, "allowed");
        var outside = Path.Combine(stage, "outside");
        var link = Path.Combine(allowed, "link");
        var destination = Path.Combine(allowed, "out.zip");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FileSystemOperation:AllowedRoots:0"] = allowed,
                }).Build();
            var result = await CreateActivity(config).ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new
                {
                    operation = "compress",
                    source = Path.Combine(allowed, "*"),
                    destination,
                })),
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("reparse point");
            File.Exists(destination).Should().BeFalse();
        }
        finally
        {
            DeleteDirectoryLink(link);
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Compress_RejectsNestedJunctionBeforeArchiveCreation()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-zip-nested-link-" + Guid.NewGuid().ToString("N"));
        var allowed = Path.Combine(stage, "allowed");
        var source = Path.Combine(allowed, "source");
        var outside = Path.Combine(stage, "outside");
        var link = Path.Combine(source, "nested-link");
        var destination = Path.Combine(allowed, "out.zip");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FileSystemOperation:AllowedRoots:0"] = allowed,
                }).Build();
            var result = await CreateActivity(config).ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new { operation = "compress", source, destination })),
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("reparse point");
            File.Exists(destination).Should().BeFalse();
        }
        finally
        {
            DeleteDirectoryLink(link);
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Compress_RejectsWildcardInDirectorySegmentBeforeScriptRuns()
    {
        var act = CreateActivity();
        Func<Task> call = () => act.ExecuteAsync(
            Ctx(),
            Cfg("{\"operation\":\"compress\",\"source\":\"C:\\\\data\\\\*\\\\payload.txt\",\"destination\":\"C:\\\\out.zip\"}"),
            CancellationToken.None);

        await call.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*only in the final path segment*");
        _capturedScript.Should().BeNull();
    }

    [WindowsFact]
    public async Task Compress_TreatsBracketsLiterallyDuringLeafWildcardExpansion()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-zip-brackets-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(stage, "source");
        var destination = Path.Combine(stage, "out.zip");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "payload[1].txt"), "bracket-literal");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "payload1.txt"), "must-not-match");
        try
        {
            var result = await CreateActivity().ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new
                {
                    operation = "compress",
                    source = Path.Combine(sourceDir, "payload[1]*"),
                    destination,
                })),
                CancellationToken.None);

            result.Success.Should().BeTrue(result.ErrorOutput);
            using var archive = ZipFile.OpenRead(destination);
            archive.Entries.Select(entry => entry.FullName).Should().Equal("payload[1].txt");
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Compress_WildcardScriptRunsUnderWindowsPowerShell51WithoutAllowedRoots()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-zip-winps51-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(stage, "source");
        var destination = Path.Combine(stage, "out.zip");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "payload.txt"), "winps51");
        try
        {
            _capturedScript = null;
            await CreateActivity().ExecuteAsync(
                Ctx(),
                Cfg(JsonSerializer.Serialize(new
                {
                    operation = "compress",
                    source = Path.Combine(sourceDir, "*.txt"),
                    destination,
                })),
                CancellationToken.None);

            _capturedScript.Should().NotBeNull();
            _capturedScript.Should().Contain("$__npEnforceAllowedRoots = $false");
            var scriptPath = Path.Combine(stage, "compress-winps51.ps1");
            await File.WriteAllTextAsync(scriptPath, _capturedScript!, Encoding.Unicode);
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
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            process.ExitCode.Should().Be(0, $"stdout: {stdout}{Environment.NewLine}stderr: {stderr}");
            using var archive = ZipFile.OpenRead(destination);
            archive.Entries.Select(entry => entry.FullName).Should().Equal("payload.txt");
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExtractBuildsExpectedScript()
    {
        var act = CreateActivity();
        await act.ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"extract\",\"source\":\"C:\\\\in.zip\",\"destination\":\"C:\\\\out\"}"),
            CancellationToken.None);
        // Extract entry-by-entry. A separate pre-scan followed by Expand-Archive would
        // re-open every output path and leave a junction-swap window between the two walks.
        _capturedScript.Should().NotContain("Expand-Archive");
        _capturedScript.Should().Contain("$__npSource = 'C:\\in.zip'");
        _capturedScript.Should().Contain("$__npDestination = 'C:\\out'");
        _capturedScript.Should().Contain("Zip-Slip blocked");
        _capturedScript.Should().Contain("FileMode]::CreateNew");
        _capturedScript.Should().Contain("FileAttributes]::ReparsePoint");
        _capturedScript.Should().Contain("$__npForce = $false");
    }

    [WindowsFact]
    public async Task Extract_WritesRegularEntryWithHardenedExtractor()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-zip-safe-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(stage, "input.zip");
        var destination = Path.Combine(stage, "out");
        Directory.CreateDirectory(stage);
        try
        {
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("nested/payload.txt").Open()))
                await writer.WriteAsync("safe-payload");

            var result = await CreateActivity().ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new { operation = "extract", source, destination })),
                CancellationToken.None);

            result.Success.Should().BeTrue(result.ErrorOutput);
            (await File.ReadAllTextAsync(Path.Combine(destination, "nested", "payload.txt")))
                .Should().Be("safe-payload");
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Extract_RejectsZipSlipEntryWithoutWritingOutsideDestination()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-zip-slip-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(stage, "input.zip");
        var destination = Path.Combine(stage, "out");
        var escaped = Path.Combine(stage, "escaped.txt");
        Directory.CreateDirectory(stage);
        try
        {
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()))
                await writer.WriteAsync("must-not-escape");

            var result = await CreateActivity().ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new { operation = "extract", source, destination })),
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("Zip-Slip blocked");
            File.Exists(escaped).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task Extract_RejectsEntryWhoseExistingParentIsJunction()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-zip-junction-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(stage, "input.zip");
        var destination = Path.Combine(stage, "out");
        var outside = Path.Combine(stage, "outside");
        var link = Path.Combine(destination, "link");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // Windows host without the symlink-development privilege.
            }

            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("link/new/escaped.txt").Open()))
                await writer.WriteAsync("must-not-escape");

            var result = await CreateActivity().ExecuteAsync(
                LocalCtx(),
                Cfg(JsonSerializer.Serialize(new { operation = "extract", source, destination })),
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("reparse point");
            Directory.Exists(Path.Combine(outside, "new")).Should().BeFalse(
                "validation must happen before Directory.CreateDirectory can follow the junction");
        }
        finally
        {
            try
            {
                DeleteDirectoryLink(link);
            }
            catch { }
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    private static void DeleteDirectoryLink(string link)
    {
        try
        {
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link);
        }
        catch { }
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
