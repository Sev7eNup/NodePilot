using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Activities;
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

    // ---------- mappings verified against the real export's type and property names ----------

    /// <summary>
    /// SCOrch writes "Invoke Runbook" as <c>Trigger Policy</c>. The old table matched the designer's
    /// name, so the single most common activity in a real estate — a runbook calling another one —
    /// imported as a placeholder, and its arguments were dropped with it.
    /// </summary>
    [Fact]
    public void Parse_TriggerPolicy_BecomesStartWorkflowWithItsArguments()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        var config = NodeById(def, "22222222-0000-0000-0000-000000000005")
            .GetProperty("data").GetProperty("config");

        config.GetProperty("workflowNameOrId").GetString().Should().Be("Log Error");
        config.GetProperty("waitForCompletion").GetBoolean().Should().BeTrue();

        var parameters = config.GetProperty("parameters");
        parameters.GetProperty("Identifier").GetString().Should()
            .Be("{{22222222-0000-0000-0000-000000000003.param.queryResult}}");
        parameters.GetProperty("ShareRoot").GetString().Should().Be("{{globals.ShareRoot}}");
    }

    [Fact]
    public void Parse_QueryXml_BecomesXmlQuery()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        var config = NodeById(def, "22222222-0000-0000-0000-000000000003")
            .GetProperty("data").GetProperty("config");

        config.GetProperty("xpath").GetString().Should().Be("//Manifest/Status");
        config.GetProperty("source").GetString().Should().Be("file");
        config.GetProperty("path").GetString().Should()
            .Be("{{" + MonitorFileId + ".param.FileName}}.XML");
    }

    [Fact]
    public void Parse_DeleteFileWithoutAgeFilter_BecomesFileOperationDelete()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        var data = NodeById(def, "22222222-0000-0000-0000-000000000006").GetProperty("data");

        data.GetProperty("activityType").GetString().Should().Be("fileOperation");
        data.GetProperty("config").GetProperty("operation").GetString().Should().Be("delete");
        data.GetProperty("disabled").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// SCOrch's age filter has no fileOperation counterpart. Importing it as an unconditional delete
    /// would delete more than the runbook ever did, so it degrades to a placeholder instead.
    /// </summary>
    [Fact]
    public void Parse_DeleteFileWithAgeFilter_DegradesToDisabledPlaceholder()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");
        var data = NodeById(def, "22222222-0000-0000-0000-000000000007").GetProperty("data");

        data.GetProperty("activityType").GetString().Should().Be("log");
        data.GetProperty("disabled").GetBoolean().Should().BeTrue();
        result.Warnings.Should().ContainSingle(w => w.Contains("Purge Aged Drops") && w.Contains("30 day"));
    }

    [Fact]
    public void Parse_DeleteFolder_BecomesFolderOperationDelete()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        var config = NodeById(def, "22222222-0000-0000-0000-000000000008")
            .GetProperty("data").GetProperty("config");

        config.GetProperty("operation").GetString().Should().Be("delete");
        config.GetProperty("path").GetString().Should().Be("{{" + MonitorFileId + ".param.Path}}");
    }

    [Fact]
    public void Parse_GenerateRandomText_BecomesGenerateText()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");
        var config = NodeById(def, "22222222-0000-0000-0000-000000000009")
            .GetProperty("data").GetProperty("config");

        config.GetProperty("mode").GetString().Should().Be("alphanumeric");
        config.GetProperty("length").GetInt32().Should().Be(5);
        // SCOrch restricted the charset to upper case; generateText has no such mode, and the
        // widening is reported rather than passed off as equivalent.
        result.Warnings.Should().ContainSingle(w => w.Contains("Generate Batch Suffix") && w.Contains("upper"));
    }

    /// <summary>
    /// startProgram.filePath only accepts an executable path, so a SCOrch Program that holds a whole
    /// command line has to become a script — the alternative is a node that always fails.
    /// </summary>
    [Fact]
    public void Parse_RunProgram_SplitsByWhetherProgramIsAPathOrACommandLine()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");

        var executable = NodeById(def, RobocopyId).GetProperty("data");
        executable.GetProperty("activityType").GetString().Should().Be("startProgram");
        executable.GetProperty("config").GetProperty("filePath").GetString()
            .Should().Be(@"C:\Windows\System32\robocopy.exe");

        var commandLine = NodeById(def, "22222222-0000-0000-0000-00000000000c").GetProperty("data");
        commandLine.GetProperty("activityType").GetString().Should().Be("runScript");
        commandLine.GetProperty("config").GetProperty("script").GetString().Should().StartWith("cmd /C attrib");
    }

    /// <summary>
    /// A remote activity with no target machine does not fall back to the engine host — it fails the
    /// step. The SCOrch computer name is copied verbatim because MachineResolver accepts a name or
    /// hostname and synthesizes an ad-hoc WinRM target when it is not registered.
    /// </summary>
    [Fact]
    public void Parse_ComputerName_IsCarriedToTargetMachineId()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");

        NodeById(def, RobocopyId).GetProperty("data").GetProperty("targetMachineId")
            .GetString().Should().Be("FILESRV01");

        // The runScript nodes carry no computer name, and those silently run on the NodePilot host.
        result.Warnings.Should().Contain(w =>
            w.Contains("Check Package Contents") && w.Contains("NodePilot host"));
    }

    [Fact]
    public void Parse_MonitorFile_MapsTheNestedFilterAndTheEventFlags()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");
        var config = NodeById(def, MonitorFileId).GetProperty("data").GetProperty("config");

        config.GetProperty("filter").GetString().Should().Be("*.csv");
        // NotifyIfCreated and NotifyIfChanged are both set; watchType takes a single value.
        config.GetProperty("watchType").GetString().Should().Be("any");
        config.GetProperty("includeSubdirectories").GetBoolean().Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("Monitor Intake Folder") && w.Contains("single watchType"));
    }

    [Fact]
    public void Parse_ReadLine_IsReportedAsRecognisedButUnsupported()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");
        var data = NodeById(def, "22222222-0000-0000-0000-00000000000a").GetProperty("data");

        data.GetProperty("activityType").GetString().Should().Be("log");
        data.GetProperty("disabled").GetBoolean().Should().BeTrue();
        result.Warnings.Should().ContainSingle(w =>
            w.Contains("Read First Line") && w.Contains("Get-Content"));
    }

    // ---------- runnability ----------

    /// <summary>
    /// The "Log Error" runbook is invoked by another runbook, so in SCOrch it needs no trigger at
    /// all. Translated literally it would have zero roots, and NodePilot fails such an execution
    /// immediately — Initialize Data becomes the manual trigger that makes it startable.
    /// </summary>
    [Fact]
    public void Parse_InitializeData_BecomesTheManualTriggerWithItsInputs()
    {
        var def = DefinitionOf(ParseFixture(), "Log Error");
        var data = NodeById(def, "88888888-0000-0000-0000-000000000001").GetProperty("data");

        data.GetProperty("activityType").GetString().Should().Be("manualTrigger");
        data.GetProperty("config").GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["Identifier", "ShareRoot"]);
    }

    [Fact]
    public void Parse_ReturnData_BecomesReturnDataWithTheDeclaredOutputs()
    {
        var def = DefinitionOf(ParseFixture(), "Log Error");
        var data = NodeById(def, "88888888-0000-0000-0000-000000000002").GetProperty("data");

        data.GetProperty("activityType").GetString().Should().Be("returnData");
        data.GetProperty("config").GetProperty("data").EnumerateObject()
            .Select(p => p.Name).Should().BeEquivalentTo(["Logged"]);
    }

    [Fact]
    public void Parse_EveryImportedRunbook_HasAtLeastOneEnabledTrigger()
    {
        var result = ParseFixture();

        foreach (var workflow in result.Workflows)
        {
            var def = JsonSerializer.Deserialize<JsonElement>(workflow.DefinitionJson);
            var triggers = def.GetProperty("nodes").EnumerateArray()
                .Select(n => n.GetProperty("data"))
                .Where(d => !d.GetProperty("disabled").GetBoolean())
                .Count(d => ActivityCatalog.TriggerTypes.Contains(d.GetProperty("activityType").GetString()!));

            triggers.Should().BeGreaterThan(0,
                "'{0}' would otherwise have zero roots and fail on every run", workflow.Name);
        }
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
