using System.Text.Json;
using FluentAssertions;
using NodePilot.Engine.Scorch;
using Xunit;

namespace NodePilot.Engine.Tests.Scorch;

/// <summary>
/// Covers the SCOrch .ois_export importer against the real export format:
///   - Links are <c>Object</c> elements with <c>ObjectTypeName="Link"</c>, not separate <c>&lt;Link&gt;</c>.
///   - Activity properties are direct children of <c>Object</c> (e.g. <c>&lt;ScriptBody&gt;</c>).
///   - Positions live in <c>PositionX</c> / <c>PositionY</c>.
///   - Variable references use the <c>`d.T.~Vb/{GUID}`d.T.~Vb/</c> pattern.
///   - Link filters live in a nested <c>&lt;TRIGGERS&gt;&lt;Entry&gt;</c> block.
///
/// Raw XML strings use <c>$$"""..."""</c> syntax throughout: <c>{</c>/<c>}</c> stay literal
/// (important for GUIDs wrapped in <c>{...}</c>), and <c>{{expr}}</c> is interpolation.
/// </summary>
public class ScorchImporterTests
{
    private static readonly ScorchImporter Importer = new();

    [Fact]
    public void Parse_NonExportRoot_Errors()
    {
        var result = Importer.Parse("<?xml version=\"1.0\"?><Nope />");
        result.Errors.Should().ContainMatch("*ExportData*");
        result.Workflows.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MalformedXml_Errors()
    {
        var result = Importer.Parse("<ExportData><broken");
        result.Errors.Should().ContainMatch("Failed to parse XML*");
    }

    [Fact]
    public void Parse_RunScriptObject_MapsScriptBody()
    {
        var id = Guid.NewGuid();
        var xml = BuildExport(PolicyWith("RB",
            objects: new[]
            {
                ActivityXml(id, name: "Do stuff", typeName: "Run .Net Script",
                    props: new[] { ("ScriptType", "PowerShell"), ("ScriptBody", "Get-Service Spooler") }),
            }));

        var result = Importer.Parse(xml);

        result.Errors.Should().BeEmpty();
        result.Workflows.Should().HaveCount(1);
        var def = JsonSerializer.Deserialize<JsonElement>(result.Workflows[0].DefinitionJson);
        var node = def.GetProperty("nodes")[0];
        node.GetProperty("data").GetProperty("activityType").GetString().Should().Be("runScript");
        node.GetProperty("data").GetProperty("config").GetProperty("script").GetString()
            .Should().Be("Get-Service Spooler");
    }

    [Fact]
    public void Parse_UnknownActivityTypeName_FallsBackToLogPlaceholder()
    {
        var id = Guid.NewGuid();
        var xml = BuildExport(PolicyWith("RB",
            objects: new[] { ActivityXml(id, "Mystery", typeName: "Extra Custom IP Activity",
                                          props: new[] { ("Foo", "bar") }) }));

        var result = Importer.Parse(xml);
        result.Workflows[0].FallbackCount.Should().Be(1);
        var def = JsonSerializer.Deserialize<JsonElement>(result.Workflows[0].DefinitionJson);
        def.GetProperty("nodes")[0].GetProperty("data").GetProperty("activityType").GetString()
            .Should().Be("log");
    }

    [Fact]
    public void Parse_LinkObject_ProducesEdge_NotNode()
    {
        var srcId = Guid.NewGuid();
        var dstId = Guid.NewGuid();
        var xml = BuildExport(PolicyWith("RB",
            objects: new[]
            {
                ActivityXml(srcId, "Src", "Run .Net Script", props: new[] { ("ScriptBody", "x") }),
                ActivityXml(dstId, "Dst", "Run .Net Script", props: new[] { ("ScriptBody", "y") }),
                LinkXml(Guid.NewGuid(), srcId, dstId),
            }));

        var result = Importer.Parse(xml);
        var def = JsonSerializer.Deserialize<JsonElement>(result.Workflows[0].DefinitionJson);

        def.GetProperty("nodes").GetArrayLength().Should().Be(2);
        def.GetProperty("edges").GetArrayLength().Should().Be(1);
        var edge = def.GetProperty("edges")[0];
        edge.GetProperty("source").GetString().Should().Be(srcId.ToString());
        edge.GetProperty("target").GetString().Should().Be(dstId.ToString());
    }

    [Fact]
    public void Parse_LinkWithTrigger_EmitsConditionExpression()
    {
        var srcId = Guid.NewGuid();
        var dstId = Guid.NewGuid();
        var triggerData = "{" + srcId + "}.FileExists";
        var xml = BuildExport(PolicyWith("RB",
            objects: new[]
            {
                ActivityXml(srcId, "Src", "Get File Status", props: new[] { ("SourcePath", @"C:\tmp\x.log") }),
                ActivityXml(dstId, "Dst", "Run .Net Script", props: new[] { ("ScriptBody", "y") }),
                LinkXml(Guid.NewGuid(), srcId, dstId, triggerData: triggerData,
                        triggerCondition: "equals", triggerValue: "True"),
            }));

        var result = Importer.Parse(xml);
        var def = JsonSerializer.Deserialize<JsonElement>(result.Workflows[0].DefinitionJson);
        var edgeData = def.GetProperty("edges")[0].GetProperty("data");
        var expr = edgeData.GetProperty("conditionExpression");
        expr.GetProperty("type").GetString().Should().Be("comparison");
        expr.GetProperty("op").GetString().Should().Be("==");
        var left = expr.GetProperty("left");
        left.GetProperty("kind").GetString().Should().Be("variable");
        left.GetProperty("stepId").GetString().Should().Be(srcId.ToString());
        left.GetProperty("field").GetString().Should().Be("param");
        left.GetProperty("paramName").GetString().Should().Be("FileExists");
        expr.GetProperty("right").GetProperty("value").GetString().Should().Be("True");
    }

    [Fact]
    public void Parse_PositionXY_PreservedOnNode()
    {
        var id = Guid.NewGuid();
        var xml = BuildExport(PolicyWith("RB",
            objects: new[] { ActivityXml(id, "A", "Run .Net Script", x: -879, y: 152,
                                          props: new[] { ("ScriptBody", "x") }) }));

        var result = Importer.Parse(xml);
        var pos = JsonSerializer.Deserialize<JsonElement>(result.Workflows[0].DefinitionJson)
            .GetProperty("nodes")[0].GetProperty("position");
        pos.GetProperty("x").GetDouble().Should().Be(-879);
        pos.GetProperty("y").GetDouble().Should().Be(152);
    }

    [Fact]
    public void Parse_GlobalVariables_ExtractedAndMappedInReferences()
    {
        var varId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var scriptBody = "$host = '`d.T.~Vb/{" + varId + "}`d.T.~Vb/'";
        var policyXml = PolicyWith("RB", new[]
        {
            ActivityXml(stepId, "Script using variable", "Run .Net Script",
                props: new[] { ("ScriptBody", scriptBody) }),
        });
        var varObject = $$"""
            <Object>
              <UniqueID datatype="string">{{{varId}}}</UniqueID>
              <Name datatype="string">MyHost</Name>
              <Description datatype="string">Target hostname</Description>
              <ObjectType datatype="string">{2E88BB5A-62F9-482E-84B0-4D963C987231}</ObjectType>
              <ObjectTypeName datatype="string">Variable</ObjectTypeName>
              <Value datatype="string">my-server.example.com</Value>
            </Object>
            """;
        // Note the `{{{varId}}}` above: with $$"""...""", `{` is literal and `{{expr}}` is
        // interpolation, so `{{{varId}}}` = `{` (literal) + `{{varId}}` (interp) + `}` (literal).
        var xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <ExportData>
              <Policies>
                <Folder>
                  <UniqueID>{00000000-0000-0000-0000-000000000000}</UniqueID>
                  <Name>Policies</Name>
                  {{policyXml}}
                </Folder>
              </Policies>
              <GlobalSettings>
                <Variables>
                  <Folder>
                    <UniqueID>{00000000-0000-0000-0000-000000000005}</UniqueID>
                    <Objects>
                      {{varObject}}
                    </Objects>
                  </Folder>
                </Variables>
              </GlobalSettings>
            </ExportData>
            """;

        var result = Importer.Parse(xml);

        result.Variables.Should().HaveCount(1);
        result.Variables[0].Name.Should().Be("MyHost");
        result.Variables[0].Value.Should().Be("my-server.example.com");
        result.Variables[0].Description.Should().Be("Target hostname");

        var def = JsonSerializer.Deserialize<JsonElement>(result.Workflows[0].DefinitionJson);
        var script = def.GetProperty("nodes")[0].GetProperty("data").GetProperty("config").GetProperty("script").GetString();
        script.Should().Contain("{{globals.MyHost}}");
    }

    [Fact]
    public void Parse_ExecutionDataReference_RewrittenToStepParam()
    {
        var srcId = Guid.NewGuid();
        var dstId = Guid.NewGuid();
        var script = "$t = '`d.T.~Ed/{" + srcId + "}.LastModificationDateTime`d.T.~Ed/'";
        var xml = BuildExport(PolicyWith("RB",
            objects: new[]
            {
                ActivityXml(srcId, "Get", "Get File Status", props: new[] { ("SourcePath", @"C:\x.log") }),
                ActivityXml(dstId, "Use", "Run .Net Script", props: new[] { ("ScriptBody", script) }),
            }));

        var result = Importer.Parse(xml);
        var def = JsonSerializer.Deserialize<JsonElement>(result.Workflows[0].DefinitionJson);
        var useNode = def.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("id").GetString() == dstId.ToString());
        var scriptOut = useNode.GetProperty("data").GetProperty("config").GetProperty("script").GetString();
        scriptOut.Should().Contain("{{" + srcId + ".param.LastModificationDateTime}}");
    }

    [Fact]
    public void Parse_DisabledLink_SurfacesDisabledFlagOnEdge()
    {
        var srcId = Guid.NewGuid();
        var dstId = Guid.NewGuid();
        var xml = BuildExport(PolicyWith("RB",
            objects: new[]
            {
                ActivityXml(srcId, "Src", "Run .Net Script", props: new[] { ("ScriptBody", "x") }),
                ActivityXml(dstId, "Dst", "Run .Net Script", props: new[] { ("ScriptBody", "y") }),
                LinkXml(Guid.NewGuid(), srcId, dstId, enabled: false),
            }));

        var result = Importer.Parse(xml);
        var def = JsonSerializer.Deserialize<JsonElement>(result.Workflows[0].DefinitionJson);
        def.GetProperty("edges")[0].GetProperty("data").GetProperty("disabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Parse_RealisticPolicy_ProducesExpectedShape()
    {
        // Mini-reproduction of the user's real export: a schedule → get-file → compare → email
        // chain with real link-trigger conditions + variable substitution.
        var varGuid = Guid.NewGuid();
        var schedule = Guid.NewGuid();
        var getFile = Guid.NewGuid();
        var compare = Guid.NewGuid();
        var sendEmail = Guid.NewGuid();

        var getFilePath = "\\\\`d.T.~Vb/{" + varGuid + "}`d.T.~Vb/\\c$\\App.log";
        var compareScript = "$t = '`d.T.~Ed/{" + getFile + "}.LastModificationDateTime`d.T.~Ed/'";
        var scheduleXml = ActivityXml(schedule, "Every hour", "Monitor Date/Time", x: -879, y: 152, props: new[]
        {
            ("Type", "interval"), ("EveryDayValue", "0"), ("EveryHourValue", "0"),
            ("EveryMinuteValue", "60"), ("TriggerImmediately", "TRUE"),
        });
        var getFileXml = ActivityXml(getFile, "check if logfile exists", "Get File Status", x: -729, y: 152,
            props: new[] { ("SourcePath", getFilePath) });
        var compareXml = ActivityXml(compare, "Script check", "Run .Net Script", x: -579, y: 152, props: new[]
        {
            ("ScriptType", "PowerShell"),
            ("ScriptBody", compareScript),
        });
        var emailXml = ActivityXml(sendEmail, "send alert", "Send Email", x: -354, y: 152, props: new[]
        {
            ("Subject", "Alert"), ("MessageContent", "Log entry detected"),
            ("SenderAddress", "alerts@example.com"), ("OutgoingServer", "smtp.example.com"),
        });
        var link1Xml = LinkXml(Guid.NewGuid(), schedule, getFile);
        var link2Xml = LinkXml(Guid.NewGuid(), getFile, compare,
            triggerData: "{" + getFile + "}.FileExists", triggerCondition: "equals", triggerValue: "True");
        var link3Xml = LinkXml(Guid.NewGuid(), compare, sendEmail,
            triggerData: "{" + compare + "}.recentLogEntryFound", triggerCondition: "equals", triggerValue: "True");

        var varObj = $$"""
            <Object>
              <UniqueID datatype="string">{{{varGuid}}}</UniqueID>
              <Name datatype="string">TargetServer</Name>
              <ObjectTypeName datatype="string">Variable</ObjectTypeName>
              <Value datatype="string">server01</Value>
            </Object>
            """;

        var xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <ExportData>
              <Policies>
                <Folder>
                  <UniqueID>{00000000-0000-0000-0000-000000000000}</UniqueID>
                  <Name>Policies</Name>
                  <Policy>
                    <UniqueID datatype="string">{33EEBB54-ACF6-479E-946A-2DA74C7B5ED0}</UniqueID>
                    <Name datatype="string">Monitor log file</Name>
                    <Description datatype="null"></Description>
                    {{scheduleXml}}
                    {{getFileXml}}
                    {{compareXml}}
                    {{emailXml}}
                    {{link1Xml}}
                    {{link2Xml}}
                    {{link3Xml}}
                  </Policy>
                </Folder>
              </Policies>
              <GlobalSettings>
                <Variables>
                  <Folder>
                    <UniqueID>{00000000-0000-0000-0000-000000000005}</UniqueID>
                    <Objects>
                      {{varObj}}
                    </Objects>
                  </Folder>
                </Variables>
              </GlobalSettings>
            </ExportData>
            """;

        var result = Importer.Parse(xml);

        result.Errors.Should().BeEmpty();
        result.Workflows.Should().HaveCount(1);
        var wf = result.Workflows[0];
        wf.Name.Should().Be("Monitor log file");
        wf.ActivityCount.Should().Be(4);

        var def = JsonSerializer.Deserialize<JsonElement>(wf.DefinitionJson);
        def.GetProperty("nodes").GetArrayLength().Should().Be(4);
        def.GetProperty("edges").GetArrayLength().Should().Be(3);

        // Variable reference on getFile node's path was rewritten to {{globals.TargetServer}}
        var getFileNode = def.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("id").GetString() == getFile.ToString());
        getFileNode.GetProperty("data").GetProperty("config").GetProperty("path").GetString()
            .Should().Contain("{{globals.TargetServer}}");

        // The second edge (getFile → compare) has a comparison expression against
        // the getFile step's FileExists param.
        var edges = def.GetProperty("edges").EnumerateArray().ToList();
        var condEdge = edges.First(e =>
            e.GetProperty("source").GetString() == getFile.ToString()
            && e.GetProperty("target").GetString() == compare.ToString());
        var expr = condEdge.GetProperty("data").GetProperty("conditionExpression");
        expr.GetProperty("type").GetString().Should().Be("comparison");
        expr.GetProperty("left").GetProperty("paramName").GetString().Should().Be("FileExists");

        result.Variables.Should().ContainSingle(v => v.Name == "TargetServer" && v.Value == "server01");
    }

    [Fact]
    public void Parse_EncryptedVariable_MarkedAsSecretWithPlaceholder()
    {
        var varId = Guid.NewGuid();
        var encryptedValue = "`d.T.~Ec/`d.T.~De/opSCGB7iz1kNL/YMbVAAb8y2j23PnGLY5KMrlytVmTE`d.T.~De/";
        var xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <ExportData>
              <Policies>
                <Folder>
                  <UniqueID>{{{Guid.Empty}}}</UniqueID>
                  <Name>Policies</Name>
                </Folder>
              </Policies>
              <GlobalSettings>
                <Variables>
                  <Folder>
                    <UniqueID>{00000000-0000-0000-0000-000000000005}</UniqueID>
                    <Objects>
                      <Object>
                        <UniqueID datatype="string">{{{varId}}}</UniqueID>
                        <Name datatype="string">SmtpPassword</Name>
                        <ObjectTypeName datatype="string">Variable</ObjectTypeName>
                        <Value datatype="string">{{encryptedValue}}</Value>
                      </Object>
                    </Objects>
                  </Folder>
                </Variables>
              </GlobalSettings>
            </ExportData>
            """;

        var result = Importer.Parse(xml);

        result.Variables.Should().HaveCount(1);
        var v = result.Variables[0];
        v.Name.Should().Be("SmtpPassword");
        v.IsSecret.Should().BeTrue();
        v.Value.Should().Be("[ENCRYPTED - set actual value after import]");
        result.Warnings.Should().ContainSingle(w => w.Contains("SmtpPassword") && w.Contains("encrypted"));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static string BuildExport(string policyXml) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <ExportData>
          <Policies>
            <Folder>
              <UniqueID>{{{Guid.Empty}}}</UniqueID>
              <Name>Policies</Name>
              {{policyXml}}
            </Folder>
          </Policies>
        </ExportData>
        """;

    private static string PolicyWith(string name, IEnumerable<string> objects)
    {
        var joined = string.Join("\n", objects);
        var escaped = System.Security.SecurityElement.Escape(name);
        return $$"""
            <Policy>
              <UniqueID datatype="string">{{{Guid.NewGuid()}}}</UniqueID>
              <Name datatype="string">{{escaped}}</Name>
              <Description datatype="null"></Description>
              {{joined}}
            </Policy>
            """;
    }

    private static string ActivityXml(Guid id, string name, string typeName,
        double x = 0, double y = 0, IEnumerable<(string Key, string Value)>? props = null)
    {
        var extra = string.Join("\n",
            (props ?? Array.Empty<(string, string)>()).Select(p =>
                "<" + p.Key + " datatype=\"string\">" + System.Security.SecurityElement.Escape(p.Value) + "</" + p.Key + ">"));
        var escapedName = System.Security.SecurityElement.Escape(name);
        return $$"""
            <Object>
              <UniqueID datatype="string">{{{id}}}</UniqueID>
              <Name datatype="string">{{escapedName}}</Name>
              <PositionX datatype="int">{{x}}</PositionX>
              <PositionY datatype="int">{{y}}</PositionY>
              <ObjectType datatype="string">{{{Guid.NewGuid()}}}</ObjectType>
              <ObjectTypeName datatype="string">{{typeName}}</ObjectTypeName>
              <Enabled datatype="bool">TRUE</Enabled>
              {{extra}}
            </Object>
            """;
    }

    private static string LinkXml(Guid id, Guid src, Guid dst,
        bool enabled = true, string? triggerData = null, string? triggerCondition = null, string? triggerValue = null)
    {
        var triggers = triggerData is null ? "" : $$"""
            <TRIGGERS>
              <Entry>
                <UniqueID datatype="string">{{{Guid.NewGuid()}}}</UniqueID>
                <Type datatype="string">trigger</Type>
                <Condition datatype="string">{{triggerCondition}}</Condition>
                <Data datatype="string">{{triggerData}}</Data>
                <Value datatype="string">{{triggerValue}}</Value>
                <GroupID datatype="int">0</GroupID>
              </Entry>
            </TRIGGERS>
            """;
        var enabledStr = enabled ? "TRUE" : "FALSE";
        return $$"""
            <Object>
              <UniqueID datatype="string">{{{id}}}</UniqueID>
              <Name datatype="string">Link</Name>
              <ObjectType datatype="string">{7A65BD17-9532-4D07-A6DA-E0F89FA0203E}</ObjectType>
              <ObjectTypeName datatype="string">Link</ObjectTypeName>
              <Enabled datatype="bool">{{enabledStr}}</Enabled>
              <SourceObject datatype="string">{{{src}}}</SourceObject>
              <TargetObject datatype="string">{{{dst}}}</TargetObject>
              <And datatype="bool">FALSE</And>
              {{triggers}}
            </Object>
            """;
    }
}
