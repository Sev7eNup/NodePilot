using System.Text.Json;
using FluentAssertions;
using NodePilot.Engine.Scorch;
using Xunit;

namespace NodePilot.Engine.Tests.Scorch;

/// <summary>
/// Importer tests driven by an on-disk <c>.ois_export</c> fixture shaped like a real SCOrch 2016
/// export, rather than by XML assembled inline.
///
/// <para>The distinction matters for exactly one reason: in a real export every Published-Data
/// marker is written as BACKSLASH-backtick (<c>\`d.T.~Ed/{GUID}.Field\`d.T.~Ed/</c>, bytes
/// <c>5c 60</c>). The inline fixtures in <see cref="ScorchImporterTests"/> used a bare backtick, so
/// they matched a pattern that no real file produces — the importer resolved 0 of 147 references in
/// the reference export while its unit tests were green. Keeping the fixture as a file preserves
/// those bytes; a C# string literal invites "cleaning up" the escaping.</para>
///
/// <para>The fixture is synthetic. It reproduces the structures verified against a real export —
/// type names, property names, marker syntax, the three TRIGGERS shapes, the 75 px position grid —
/// with invented values, because this repository is public.</para>
/// </summary>
public class ScorchImporterFixtureTests
{
    private static readonly ScorchImporter Importer = new();

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Scorch", "Fixtures", "realistic-runbook.ois_export");

    private static ScorchImportResult ParseFixture()
    {
        using var stream = File.OpenRead(FixturePath);
        return Importer.Parse(stream);
    }

    private static JsonElement DefinitionOf(ScorchImportResult result, string workflowName) =>
        JsonSerializer.Deserialize<JsonElement>(
            result.Workflows.Single(w => w.Name == workflowName).DefinitionJson);

    private static JsonElement NodeById(JsonElement definition, string id) =>
        definition.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == id);

    private const string MonitorFileId = "22222222-0000-0000-0000-000000000001";
    private const string RunScriptId = "22222222-0000-0000-0000-000000000002";
    private const string RobocopyId = "22222222-0000-0000-0000-00000000000b";

    [Fact]
    public void Parse_RealExportShapedFixture_ProducesBothRunbooks()
    {
        var result = ParseFixture();

        result.Errors.Should().BeEmpty();
        result.Workflows.Select(w => w.Name).Should()
            .BeEquivalentTo(["Sample Package Intake", "Log Error"]);
        result.Workflows.Single(w => w.Name == "Sample Package Intake").ActivityCount.Should().Be(12);
    }

    // The regression that motivated this file: backslash-backtick markers must resolve.
    [Fact]
    public void Parse_BackslashBacktickVariableReference_ResolvesToGlobal()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");

        var directory = NodeById(def, MonitorFileId)
            .GetProperty("data").GetProperty("config").GetProperty("directory").GetString();

        directory.Should().Be(@"{{globals.ShareRoot}}\Intake");
    }

    [Fact]
    public void Parse_BackslashBacktickStepReference_ResolvesToStepParam()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");

        var script = NodeById(def, RunScriptId)
            .GetProperty("data").GetProperty("config").GetProperty("script").GetString()!;

        script.Should().Contain("{{" + MonitorFileId + ".param.FileNameExt}}");
        script.Should().Contain("{{" + MonitorFileId + ".param.Path}}");
        // Global referenced from inside a script body, under its sanitized name.
        script.Should().Contain("{{globals.Tools_Dir__x86}}");
    }

    [Fact]
    public void Parse_ReferenceInsideNonScriptConfig_IsAlsoRewritten()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");

        var arguments = NodeById(def, RobocopyId)
            .GetProperty("data").GetProperty("config").GetProperty("arguments").GetString();

        arguments.Should().Be("{{" + MonitorFileId + @".param.Path}} D:\Staging /E");
    }

    // SCOrch exposes runbook metadata as a DOTTED published-data name ({GUID}.Policy.Name).
    // NodePilot's {{step.param.X}} tail has no nested dots, so the only honest outcomes are
    // "leave it visible" and "say so" — never a template that silently never resolves.
    [Fact]
    public void Parse_DottedPublishedDataName_IsLeftLiteralAndReported()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");

        var script = NodeById(def, RunScriptId)
            .GetProperty("data").GetProperty("config").GetProperty("script").GetString()!;

        script.Should().Contain("Policy.Name");
        script.Should().NotContain("param.Policy.Name");

        result.Warnings.Should().ContainSingle(w =>
            w.Contains("Check Package Contents")
            && w.Contains("'script'")
            && w.Contains("Policy.Name"));
    }

    [Fact]
    public void Parse_VariableWithNameOutsideGrammar_IsSanitizedAndReported()
    {
        var result = ParseFixture();

        result.Variables.Should().Contain(v => v.Name == "Tools_Dir__x86");
        result.Warnings.Should().ContainSingle(w =>
            w.Contains("Tools Dir (x86)") && w.Contains("renamed"));
    }

    [Fact]
    public void Parse_EncryptedVariable_IsFlaggedAsSecretWithPlaceholder()
    {
        var result = ParseFixture();

        var secret = result.Variables.Single(v => v.Name == "ServiceAccountPassword");
        secret.IsSecret.Should().BeTrue();
        secret.Value.Should().Be("[ENCRYPTED - set actual value after import]");
    }

    [Fact]
    public void Parse_StringAndStreamOverloads_AgreeOnTheSameDocument()
    {
        var fromStream = ParseFixture();
        var fromString = Importer.Parse(File.ReadAllText(FixturePath));

        fromString.Workflows.Select(w => w.DefinitionJson).Should()
            .BeEquivalentTo(fromStream.Workflows.Select(w => w.DefinitionJson));
        fromString.Warnings.Should().BeEquivalentTo(fromStream.Warnings);
    }

    [Fact]
    public void Parse_LinkPointingAtAnObjectOutsideTheRunbook_DoesNotProduceAnEdge()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");

        var nodeIds = def.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()).ToHashSet();

        foreach (var edge in def.GetProperty("edges").EnumerateArray())
        {
            nodeIds.Should().Contain(edge.GetProperty("source").GetString());
            nodeIds.Should().Contain(edge.GetProperty("target").GetString());
        }
    }
}
