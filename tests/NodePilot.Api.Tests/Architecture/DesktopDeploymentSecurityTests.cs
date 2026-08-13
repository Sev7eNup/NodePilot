using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

public sealed class DesktopDeploymentSecurityTests
{
    [Fact]
    public void DesktopRuntimeOverridesPath_UsesRestrictedSecretsDirectory()
    {
        var template = File.ReadAllText(Path.Combine(
            ProductionSources.RepoRoot(),
            "deploy",
            "desktop",
            "appsettings.Desktop.json.template"));

        Regex.Match(template, "\"RuntimeOverridesPath\"\\s*:\\s*\"(?<path>[^\"]+)\"")
            .Groups["path"].Value
            .Should().Be("{{DATA_PATH_ESCAPED}}\\\\secrets\\\\appsettings.runtime.json");
    }

    [Fact]
    public void DesktopProvisioning_LegacyRuntimeOverrides_MigratesAndLocksFiles()
    {
        var script = File.ReadAllText(Path.Combine(
            ProductionSources.RepoRoot(),
            "deploy",
            "desktop",
            "Provision-LocalDb.ps1"));

        script.Should().Contain("Get-ChildItem -LiteralPath $DataPath -Filter 'appsettings.runtime.json*' -File");
        script.Should().Contain("Move-Item -LiteralPath $legacy.FullName -Destination $destination");
        script.Should().MatchRegex(
            @"(?s)Move-Item -LiteralPath \$legacy\.FullName -Destination \$destination.{0,500}?Set-RestrictedAcl -path \$destination -NoCurrentUser",
            "each legacy file must be locked immediately, even if a later move aborts the upgrade");
        script.Should().Contain("Get-ChildItem -LiteralPath $SecretsDir -Filter 'appsettings.runtime.json*' -File");
        script.Should().Contain("Set-RestrictedAcl -path $protectedFile.FullName -NoCurrentUser");
        Regex.Matches(script, @"Set-RestrictedAcl\s+-path\s+\$SecretsDir(?![^\r\n]*-NoCurrentUser)")
            .Should().BeEmpty("the secrets directory must grant only SYSTEM and Administrators");

        var serviceStop = script.IndexOf("Write-Step 'Removing any prior NodePilot services'", StringComparison.Ordinal);
        var migrationCall = script.IndexOf("Move-DesktopRuntimeOverridesToSecrets", serviceStop, StringComparison.Ordinal);
        var databaseProvisioning = script.IndexOf("# --- 1. ports", StringComparison.Ordinal);

        serviceStop.Should().BeGreaterThan(-1);
        migrationCall.Should().BeGreaterThan(serviceStop, "migration must run only after the API service is stopped");
        migrationCall.Should().BeLessThan(databaseProvisioning, "legacy plaintext-readable copies must be secured before provisioning continues");
    }

    [Fact]
    public void DesktopInstallerBuild_SelfContainedRuntime_IsPinnedAndArtifactGated()
    {
        var desktopDeploy = Path.Combine(ProductionSources.RepoRoot(), "deploy", "desktop");
        var build = File.ReadAllText(Path.Combine(desktopDeploy, "Build-DesktopInstaller.ps1"));
        var gatePath = Path.Combine(desktopDeploy, "Assert-DesktopRuntimePayload.ps1");

        build.Should().Contain("$DesktopRuntimeVersion = '10.0.11'");
        build.Should().Contain("\"-p:RuntimeFrameworkVersion=$DesktopRuntimeVersion\"");
        build.Should().Contain(". (Join-Path $PSScriptRoot 'Assert-DesktopRuntimePayload.ps1')");
        build.Should().Contain("Assert-DesktopRuntimePayload -AppPath $appStage -MinimumVersion ([version]$DesktopRuntimeVersion)");
        File.Exists(gatePath).Should().BeTrue("the build must verify the staged payload, not only request a version from dotnet publish");
    }

    [Fact]
    public void DesktopRuntimeArtifactGate_ManifestAndSecuritySensitiveBinaries_MustMeetFloor()
    {
        var gate = File.ReadAllText(Path.Combine(
            ProductionSources.RepoRoot(),
            "deploy",
            "desktop",
            "Assert-DesktopRuntimePayload.ps1"));

        foreach (var requiredEvidence in new[]
        {
            "NodePilot.Api.runtimeconfig.json",
            "Microsoft.NETCore.App",
            "Microsoft.AspNetCore.App",
            "hostfxr.dll",
            "System.Private.CoreLib.dll",
            "System.Net.WebSockets.dll",
            "System.Net.WebSockets.Client.dll",
            "Microsoft.AspNetCore.Server.Kestrel.Core.dll"
        })
        {
            gate.Should().Contain(requiredEvidence);
        }

        Regex.Matches(gate, @"\$actualVersion\s+-lt\s+\$MinimumVersion")
            .Count.Should().BeGreaterThanOrEqualTo(2, "both manifest declarations and shipped binaries must be checked");
    }
}
