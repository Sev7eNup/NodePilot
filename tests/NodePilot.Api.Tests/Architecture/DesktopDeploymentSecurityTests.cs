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

        script.Should().Contain("foreach ($legacyRoot in @($DataPath, $AppPath))");
        script.Should().Contain("Get-ChildItem -LiteralPath $legacyRoot -Filter 'appsettings.runtime.json*' -File");
        script.Should().Contain("Move-Item -LiteralPath $legacy.FullName -Destination $destination");
        script.Should().MatchRegex(
            @"(?s)Move-Item -LiteralPath \$legacy\.FullName -Destination \$destination.{0,500}?Set-RestrictedAcl -path \$destination -NoCurrentUser",
            "each legacy file must be locked immediately, even if a later move aborts the upgrade");
        script.Should().Contain("Get-ChildItem -LiteralPath $SecretsDir -Filter 'appsettings.runtime.json*' -File");
        script.Should().Contain("Set-RestrictedAcl -path $protectedFile.FullName -NoCurrentUser");
        script.Should().Contain("Reset-CompromisedDataProtectionKeyRing");
        script.Should().Contain("data-protection-keys.compromised.");
        script.Should().Contain("$KeyRingDir, $LogsDir, $ArchiveDir");
        script.Should().Contain("-ExtraReadNoInheritance");
        script.Should().Contain("appsettings.Development.json");
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
    public void DesktopProvisioning_Upgrade_RemovesObsoleteDevelopmentSettingsAfterServiceStop()
    {
        var script = File.ReadAllText(Path.Combine(
            ProductionSources.RepoRoot(),
            "deploy",
            "desktop",
            "Provision-LocalDb.ps1"));

        var serviceStop = script.IndexOf("Write-Step 'Removing any prior NodePilot services'", StringComparison.Ordinal);
        var removal = script.IndexOf("Remove-Item -LiteralPath $legacyDevelopmentSettings -Force", StringComparison.Ordinal);
        var databaseProvisioning = script.IndexOf("# --- 1. ports", StringComparison.Ordinal);

        script.Should().Contain("$legacyDevelopmentSettings = Join-Path $AppPath 'appsettings.Development.json'");
        removal.Should().BeGreaterThan(serviceStop, "the old API must be stopped before its install-root files are changed");
        removal.Should().BeLessThan(databaseProvisioning, "Development overrides must be gone before the replacement service can start");
    }

    [Fact]
    public void DesktopProvisioning_DataRoot_DoesNotPropagateUsersReadAccessToSensitiveTrees()
    {
        var script = File.ReadAllText(Path.Combine(
            ProductionSources.RepoRoot(),
            "deploy",
            "desktop",
            "Provision-LocalDb.ps1"));

        script.Should().Contain("[switch] $ExtraReadNoInheritance");
        script.Should().Contain(
            "Set-RestrictedAcl -path $DataPath -extraReadPrincipals @('S-1-5-32-545') -NoCurrentUser -ExtraReadNoInheritance");
        script.Should().Contain("Set-RestrictedAcl -path $DesktopJson -extraReadPrincipals @('S-1-5-32-545')");
        script.Should().Contain("foreach ($protectedDir in @($KeyRingDir, $LogsDir, $ArchiveDir))");
        script.Should().Contain("Set-RestrictedAcl -path $protectedFile.FullName -NoCurrentUser");

        Regex.Matches(script, @"Set-RestrictedAcl\s+-path\s+\$DataPath[^\r\n]*S-1-5-32-545[^\r\n]*-ExtraReadNoInheritance")
            .Count.Should().BeGreaterThan(0, "BUILTIN\\Users access on the root must be non-inheriting");
    }

    [Fact]
    public void DesktopProvisioning_CompromisedDataProtectionRing_IsRotatedButSecureRingIsPreserved()
    {
        var script = File.ReadAllText(Path.Combine(
            ProductionSources.RepoRoot(),
            "deploy",
            "desktop",
            "Provision-LocalDb.ps1"));

        foreach (var broadSid in new[] { "S-1-5-32-545", "S-1-5-11", "S-1-1-0" })
            script.Should().Contain(broadSid);

        script.Should().Contain("$readableRights =");
        script.Should().Contain("($_.FileSystemRights -band $readableRights) -eq 0");
        script.Should().Contain("Translate([System.Security.Principal.SecurityIdentifier])");
        script.Should().Contain("if ($untrusted.Count -eq 0)");
        script.Should().Contain("Move-Item -LiteralPath $KeyRingDir -Destination $quarantine");
        script.Should().Contain("Protect-RestrictedTree -path $quarantine");
        script.Should().MatchRegex(
            @"(?s)Move-Item -LiteralPath \$KeyRingDir -Destination \$quarantine.{0,300}?Protect-RestrictedTree -path \$quarantine.{0,300}?New-Item -ItemType Directory -Force -Path \$KeyRingDir",
            "the exposed ring must be quarantined and locked before a fresh key directory is created");
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
