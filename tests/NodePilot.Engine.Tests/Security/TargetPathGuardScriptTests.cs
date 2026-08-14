using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

public sealed class TargetPathGuardScriptTests
{
    private static readonly RunspaceExecutionEngine Engine =
        new(NullLogger<RunspaceExecutionEngine>.Instance, 1, 2);

    [Fact]
    public void GeneratedGuard_WithNoAllowedRootsStillEmitsLinkLocalReparseCheck()
    {
        var config = new ConfigurationBuilder().Build();

        var guard = TargetPathGuardScript.Build(config, ("$candidate", "path"));

        guard.Should().Contain("function Assert-NodePilotAllowedPath");
        guard.Should().Contain("[System.IO.File]::GetAttributes");
        guard.Should().Contain("$__npEnforceAllowedRoots = $false");
        guard.Should().NotContain("Test-Path -LiteralPath");
        guard.Should().NotContain("Get-Item -LiteralPath");
    }

    [WindowsFact]
    public async Task GeneratedGuard_RejectsCandidateTraversingTargetSideReparsePoint()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-target-guard-" + Guid.NewGuid().ToString("N"));
        var allowed = Path.Combine(stage, "allowed");
        var outside = Path.Combine(stage, "outside");
        var link = Path.Combine(allowed, "link");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "payload.txt"), "outside");

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

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FileSystemOperation:AllowedRoots:0"] = allowed,
                }).Build();
            var guard = TargetPathGuardScript.Build(config, ("$candidate", "path"));
            var result = await Engine.ExecuteAsync(
                new PowerShellExecutionRequest
                {
                    ScriptText = $$"""
                        $candidate = {{PowerShellOperation.Literal(Path.Combine(link, "payload.txt"))}}
                        {{guard}}
                        Write-Output 'guard-bypassed'
                        """,
                    Timeout = TimeSpan.FromSeconds(30),
                },
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("traverses reparse point");
            result.Output.Should().NotContain("guard-bypassed");
        }
        finally
        {
            DeleteLinkOnly(link);
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task GeneratedGuard_WithNoAllowedRootsRejectsDanglingTargetSideReparsePoint()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-target-dangling-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(stage, "link");
        var missingTarget = Path.Combine(stage, "missing-target");
        Directory.CreateDirectory(stage);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, missingTarget);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var guard = TargetPathGuardScript.Build(
                new ConfigurationBuilder().Build(),
                ("$candidate", "path"));
            var result = await Engine.ExecuteAsync(
                new PowerShellExecutionRequest
                {
                    ScriptText = $$"""
                        $candidate = {{PowerShellOperation.Literal(Path.Combine(link, "payload.txt"))}}
                        {{guard}}
                        Write-Output 'guard-bypassed'
                        """,
                    Timeout = TimeSpan.FromSeconds(30),
                },
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("traverses reparse point");
            result.Output.Should().NotContain("guard-bypassed");
        }
        finally
        {
            DeleteLinkOnly(link);
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task GeneratedGuard_AllowsReparseFreeCandidateInsideExistingRoot()
    {
        var allowed = Path.Combine(Path.GetTempPath(), "nodepilot-target-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(allowed);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FileSystemOperation:AllowedRoots:0"] = allowed,
                }).Build();
            var guard = TargetPathGuardScript.Build(config, ("$candidate", "path"));
            var result = await Engine.ExecuteAsync(
                new PowerShellExecutionRequest
                {
                    ScriptText = $$"""
                        $candidate = {{PowerShellOperation.Literal(Path.Combine(allowed, "future", "payload.txt"))}}
                        {{guard}}
                        Write-Output 'allowed'
                        """,
                    Timeout = TimeSpan.FromSeconds(30),
                },
                CancellationToken.None);

            result.Success.Should().BeTrue(result.Error);
            result.Output.Should().Contain("allowed");
        }
        finally
        {
            try { Directory.Delete(allowed, recursive: true); } catch { }
        }
    }

    [WindowsFact]
    public async Task GeneratedGuard_TreatsVolumeRootAsContainingItsChildren()
    {
        var candidate = Path.Combine(Path.GetTempPath(), "nodepilot-root-guard-" + Guid.NewGuid().ToString("N"));
        var volumeRoot = Path.GetPathRoot(candidate)!;
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["FileSystemOperation:AllowedRoots:0"] = volumeRoot,
            }).Build();
        var guard = TargetPathGuardScript.Build(config, ("$candidate", "path"));
        var result = await Engine.ExecuteAsync(
            new PowerShellExecutionRequest
            {
                ScriptText = $$"""
                    $candidate = {{PowerShellOperation.Literal(candidate)}}
                    {{guard}}
                    Write-Output 'allowed'
                    """,
                Timeout = TimeSpan.FromSeconds(30),
            },
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("allowed");
    }

    private static void DeleteLinkOnly(string link)
    {
        try
        {
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link);
        }
        catch { }
    }
}
