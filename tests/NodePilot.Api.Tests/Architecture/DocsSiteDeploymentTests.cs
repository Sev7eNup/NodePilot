using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// The documentation bundle only reaches an installation through the deploy scripts, and no CI
/// job ever assembles a wwwroot to check. These are text guards over exactly the steps that
/// would fail silently: a missing staging step ships an artifact whose /docs 404s, and a
/// mirroring sync deletes the bundle without saying so.
/// </summary>
public sealed class DocsSiteDeploymentTests
{
    private static string ReadDeployScript(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { ProductionSources.RepoRoot(), "deploy" }.Concat(relativeParts).ToArray()));

    [Fact]
    public void ServerArtifact_StagesTheDocsBundleIntoWwwroot()
    {
        var script = ReadDeployScript("Build-Artifact.ps1");

        script.Should().Contain("$DocsUiDir = Join-Path $RepoRoot 'src\\nodepilot-docs-ui'");
        script.Should().Contain("Invoke-NodePilotWebBuild -ProjectDir $DocsUiDir -Label 'docs site'");
        script.Should().Contain("$DocsWwwRoot = Join-Path $WwwRoot 'docs'");
        script.Should().Contain("Copy-Item (Join-Path $DocsDistDir '*') $DocsWwwRoot -Recurse -Force");
    }

    [Fact]
    public void ServerArtifact_VerifiesTheDocsIndexBeforeTheFileManifestIsGenerated()
    {
        var script = ReadDeployScript("Build-Artifact.ps1");

        var gate = script.IndexOf("wwwroot\\docs\\index.html in staging", StringComparison.Ordinal);
        var manifest = script.IndexOf("New-NodePilotExtractedFileManifest", StringComparison.Ordinal);

        gate.Should().BeGreaterThan(0, "the staging gate must exist");
        manifest.Should().BeGreaterThan(0);
        gate.Should().BeLessThan(
            manifest,
            "an artifact missing the bundle has to fail before it is hashed, signed and shipped");
    }

    [Fact]
    public void Installer_RefusesAnArtifactWithoutTheDocsBundle()
    {
        var script = ReadDeployScript("Install-NodePilot.ps1");

        script.Should().Contain("Artifact did not contain wwwroot\\docs\\index.html");
    }

    [Fact]
    public void DesktopInstaller_StagesTheDocsBundleAlongsideTheSpa()
    {
        var script = ReadDeployScript("desktop", "Build-DesktopInstaller.ps1");

        script.Should().Contain("$DocsUiDir    = Join-Path $RepoRoot 'src\\nodepilot-docs-ui'");
        script.Should().Contain("$docsWwwroot = Join-Path $wwwroot 'docs'");
        script.Should().Contain("Copy-Item -Path (Join-Path $docsDist '*') -Destination $docsWwwroot -Recurse -Force");
    }

    [Fact]
    public void DesktopSync_DoesNotMirrorTheDocsBundleAway()
    {
        // robocopy /MIR deletes whatever the source does not contain. The SPA mirror runs
        // against wwwroot, which now also holds the independently built docs bundle, so without
        // the exclusion every dev sync would remove it — visible only as a 404 much later.
        var script = ReadDeployScript("desktop", "Sync-DesktopApp.ps1");

        script.Should().Contain("/MIR /XD (Join-Path $AppDir 'wwwroot\\docs')");
        script.Should().Contain("robocopy.exe $docsDist (Join-Path $AppDir 'wwwroot\\docs') /MIR");
    }

    [Fact]
    public void DocsIndexHtml_CarriesNoInlineScript()
    {
        // Second line of defence for the docs-ui vitest guard: that suite is skipped for a
        // backend-only change, while this project always runs. Inline script is blocked by the
        // API's `script-src 'self'` CSP once the bundle is served at /docs.
        var indexHtml = File.ReadAllText(Path.Combine(
            ProductionSources.RepoRoot(), "src", "nodepilot-docs-ui", "index.html"));

        System.Text.RegularExpressions.Regex
            .Matches(indexHtml, "<script(?![^>]*\\bsrc=)[^>]*>")
            .Should().BeEmpty("the API serves this file under a CSP that blocks inline script");
    }
}
