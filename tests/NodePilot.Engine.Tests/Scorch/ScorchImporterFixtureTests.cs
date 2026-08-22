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
    private const string QueryXmlId = "22222222-0000-0000-0000-000000000003";
    private const string RobocopyId = "22222222-0000-0000-0000-00000000000b";

    // References resolve through each node's outputVariable, derived from its SCOrch name.
    private const string MonitorFileVar = "Monitor_Intake_Folder";
    private const string QueryXmlVar = "Query_Manifest_Status";

    [Fact]
    public void Parse_RealExportShapedFixture_ProducesBothRunbooks()
    {
        var result = ParseFixture();

        result.Errors.Should().BeEmpty();
        result.Workflows.Select(w => w.Name).Should()
            .BeEquivalentTo(["Sample Package Intake", "Log Error"]);
        result.Workflows.Single(w => w.Name == "Sample Package Intake").ActivityCount.Should().Be(14);
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

        script.Should().Contain("{{" + MonitorFileVar + ".param.fileName}}");
        script.Should().Contain("{{" + MonitorFileVar + ".param.fileDirectory}}");
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

        arguments.Should().Be("{{" + MonitorFileVar + @".param.fileDirectory}} D:\Staging /E");
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
            .Be("{{" + QueryXmlVar + ".param.result}}");
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
            .Be("{{" + MonitorFileVar + ".param.fileNameWithoutExtension}}.XML");
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
        config.GetProperty("path").GetString().Should().Be("{{" + MonitorFileVar + ".param.fileDirectory}}");
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

    // ---------- link conditions ----------

    private static JsonElement EdgeFrom(JsonElement definition, string sourceId, string targetId) =>
        definition.GetProperty("edges").EnumerateArray().Single(e =>
            e.GetProperty("source").GetString() == sourceId && e.GetProperty("target").GetString() == targetId);

    /// <summary>
    /// SCOrch's "on success" link carries a bare {GUID} in Data and the outcome in Value. The parser
    /// required {GUID}.field, so it reported every one as unparseable and dropped it — turning the
    /// most common conditional link in any runbook into an unconditional edge.
    /// </summary>
    [Fact]
    public void Parse_StatusOnlyLink_BecomesTheSuccessShortcut()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        var data = EdgeFrom(def, RunScriptId, QueryXmlId).GetProperty("data");

        data.GetProperty("condition").GetString().Should().Be(RunScriptId + ".success");
        data.GetProperty("conditionExpression").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Parse_SingleFilterLink_BecomesAComparison()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        var expr = EdgeFrom(def, QueryXmlId, "22222222-0000-0000-0000-000000000004")
            .GetProperty("data").GetProperty("conditionExpression");

        expr.GetProperty("type").GetString().Should().Be("comparison");
        expr.GetProperty("op").GetString().Should().Be("==");
        expr.GetProperty("left").GetProperty("paramName").GetString().Should().Be("hasPayload");
    }

    /// <summary>
    /// GroupID is empty in every real export, so the link's &lt;And&gt; is the only thing carrying
    /// ALL-vs-ANY. Inferring AND from the group alone turned every "match any of these" link into
    /// "match all of these".
    /// </summary>
    [Fact]
    public void Parse_TwoFiltersWithAndFalse_AreOrJoined()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        var expr = EdgeFrom(def, "22222222-0000-0000-0000-000000000006", "22222222-0000-0000-0000-000000000007")
            .GetProperty("data").GetProperty("conditionExpression");

        expr.GetProperty("type").GetString().Should().Be("group");
        expr.GetProperty("op").GetString().Should().Be("OR");
        expr.GetProperty("children").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Parse_NegatedOperator_KeepsItsMeaning()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        var expr = EdgeFrom(def, "22222222-0000-0000-0000-000000000007", "22222222-0000-0000-0000-000000000008")
            .GetProperty("data").GetProperty("conditionExpression");

        // 'doesnotequal' — written without spaces in a real export, which the spaced-only table
        // did not recognise, so the filter was dropped and the edge became unconditional.
        expr.GetProperty("op").GetString().Should().Be("!=");
    }

    [Fact]
    public void Parse_EveryLinkThatCarriedFilters_StillCarriesACondition()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");

        var conditional = def.GetProperty("edges").EnumerateArray().Count(e =>
        {
            var data = e.GetProperty("data");
            return data.GetProperty("condition").ValueKind != JsonValueKind.Null
                   || data.GetProperty("conditionExpression").ValueKind != JsonValueKind.Null;
        });

        conditional.Should().Be(6, "the fixture has six links with a TRIGGERS block");
    }

    /// <summary>
    /// SCOrch's Compare Values evaluates one comparison and its outgoing links branch on the result.
    /// Imported as a `log` it kept the node visible but killed every branch behind it, because a log
    /// publishes nothing for the links to read. As a `decision` whose single case is named "true"
    /// with defaultCaseName "false", <c>param.case</c> carries exactly the values the SCOrch filters
    /// already compare against — so the whole remap is the field name.
    /// </summary>
    [Fact]
    public void Parse_CompareValues_BecomesADecision_AndItsLinksReadTheCase()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");
        const string CompareId = "22222222-0000-0000-0000-000000000004";

        var data = NodeById(def, CompareId).GetProperty("data");
        data.GetProperty("activityType").GetString().Should().Be("decision");
        data.GetProperty("disabled").GetBoolean().Should().BeFalse();

        var config = data.GetProperty("config");
        config.GetProperty("defaultCaseName").GetString().Should().Be("false");
        var singleCase = config.GetProperty("cases").EnumerateArray().Single();
        singleCase.GetProperty("name").GetString().Should().Be("true");

        var condition = singleCase.GetProperty("condition");
        condition.GetProperty("type").GetString().Should().Be("comparison");
        condition.GetProperty("op").GetString().Should().Be("==");
        // The left operand is a Published-Data expression, rewritten like any other reference.
        condition.GetProperty("left").GetProperty("value").GetString()
            .Should().Be("{{" + QueryXmlVar + ".param.result}}");
        condition.GetProperty("right").GetProperty("value").GetString().Should().Be("ARCHIVE");

        // Both outgoing links read the decision's case rather than SCOrch's Compare.CompareResult.
        foreach (var target in new[] { "22222222-0000-0000-0000-000000000005", "22222222-0000-0000-0000-000000000006" })
        {
            var operand = EdgeFrom(def, CompareId, target)
                .GetProperty("data").GetProperty("conditionExpression").GetProperty("left");
            operand.GetProperty("stepId").GetString().Should().Be(CompareId);
            operand.GetProperty("paramName").GetString().Should().Be("case");
        }
    }

    // ---------- data bus ----------

    [Fact]
    public void Parse_OutputVariable_IsDerivedFromTheScorchActivityName()
    {
        var def = DefinitionOf(ParseFixture(), "Sample Package Intake");

        NodeById(def, MonitorFileId).GetProperty("data").GetProperty("outputVariable")
            .GetString().Should().Be(MonitorFileVar);
        NodeById(def, QueryXmlId).GetProperty("data").GetProperty("outputVariable")
            .GetString().Should().Be(QueryXmlVar);
    }

    /// <summary>
    /// Rewriting the marker syntax is not the same as translating the data. SCOrch's Monitor File
    /// publishes Path/FileName/FileNameExt; fileWatcherTrigger publishes fileAction/filePath/
    /// fileName. The reference comes out well-formed and still resolves to nothing — and inside a
    /// runScript body an unresolved template is legal script text, so the step would run green with
    /// the literal placeholder in it.
    /// </summary>
    [Fact]
    public void Parse_ReferenceToAFieldTheTargetDoesNotPublish_IsReported()
    {
        var result = ParseFixture();

        result.Warnings.Should().Contain(w =>
            w.Contains("param.FileSize") && w.Contains("fileWatcherTrigger") && w.Contains("fileName"));
    }

    /// <summary>
    /// A runScript's real outputs are the variables its script assigns — the static catalog knows
    /// only <c>exitCode</c>. Checking against the catalog alone flagged six perfectly good
    /// references in the reference runbook; a report that tells an operator to fix working wiring is
    /// worse than no report at all.
    /// </summary>
    [Fact]
    public void Parse_ValueAScriptWritesToTheBus_IsNotReportedAsMissing()
    {
        var result = ParseFixture();

        // 'Check Package Contents' assigns $hasPayload, and the link out of it filters on that value.
        result.Warnings.Should().NotContain(w => w.Contains("param.hasPayload"));
    }

    /// <summary>
    /// The same check applied to link conditions, which read the bus exactly like a node config
    /// does. A filter reading a value its source never publishes makes the edge silently never
    /// match — harder to notice than a broken step, and previously not examined at all.
    /// </summary>
    [Fact]
    public void Parse_LinkConditionReadingAValueTheSourceDoesNotPublish_IsReported()
    {
        var result = ParseFixture();

        // The links out of 'Query Manifest Status' filter on queryResult; xmlQuery publishes
        // result/count, so those branches would never match.
        result.Warnings.Should().Contain(w =>
            w.Contains("param.FileSize") && w.Contains("the link into") && w.Contains("fileWatcherTrigger"));
    }

    [Fact]
    public void Parse_ActivityDescriptionTimeoutAndRunAs_SurviveTheMetadataStrip()
    {
        var result = ParseFixture();
        var def = DefinitionOf(result, "Sample Package Intake");
        var data = NodeById(def, RunScriptId).GetProperty("data");

        data.GetProperty("description").GetString().Should().Contain("payload files");
        // ASW_ObjectTimeout=600 overrides the mapper's default, and runScript documents the key.
        data.GetProperty("config").GetProperty("timeoutSeconds").GetInt32().Should().Be(600);
        result.Warnings.Should().ContainSingle(w =>
            w.Contains(@"CONTOSO\svc-orchestrator") && w.Contains("no credential was created"));
    }

    /// <summary>
    /// The import report reuses the workflow analyzer, so it says the same thing about a workflow
    /// that the canvas and the MCP tools do. The fixture's disabled placeholder cuts a branch loose,
    /// which is precisely the finding an operator needs to see.
    /// </summary>
    [Fact]
    public void Parse_AnalyzerFindings_AreFoldedIntoTheImportReport()
    {
        var result = ParseFixture();

        result.Warnings.Should().Contain(w => w.Contains("[unreachable-node]"));
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
