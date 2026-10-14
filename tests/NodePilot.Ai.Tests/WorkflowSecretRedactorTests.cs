using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Tests the shared Core redaction walk (used by the API export path, the MCP definition redactor
/// and the AI chat assistant). Lives here because NodePilot.Ai.Tests already references Core and
/// there is no dedicated Core test project.
/// </summary>
public sealed class WorkflowSecretRedactorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Redact_MasksSecretConfigKeys()
    {
        var def = Parse("""
        {
          "nodes": [
            { "id": "n1", "data": { "config": {
              "apiKey": "sk-live-123",
              "password": "hunter2",
              "secret": "whsec_abc",
              "authToken": "tok",
              "bearer": "b",
              "connectionString": "Server=x;Password=p;",
              "prompt": "hello",
              "url": "https://example.com"
            } } }
          ],
          "edges": []
        }
        """);

        var node = WorkflowSecretRedactor.Redact(def);
        var cfg = node["nodes"]![0]!["data"]!["config"]!.AsObject();

        cfg["apiKey"]!.GetValue<string>().Should().Be("***");
        cfg["password"]!.GetValue<string>().Should().Be("***");
        cfg["secret"]!.GetValue<string>().Should().Be("***");
        cfg["authToken"]!.GetValue<string>().Should().Be("***");
        cfg["bearer"]!.GetValue<string>().Should().Be("***");
        cfg["connectionString"]!.GetValue<string>().Should().Be("***");
        // Non-secret keys are preserved verbatim.
        cfg["prompt"]!.GetValue<string>().Should().Be("hello");
        cfg["url"]!.GetValue<string>().Should().Be("https://example.com");
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("true")]
    [InlineData("""{ "value": "sk-live-123" }""")]
    [InlineData("""[ 4711, "sk-live-123" ]""")]
    public void Redact_SecretKeyWithNonStringValue_MasksWholeValue(string rawValue)
    {
        var def = Parse($$"""
        { "nodes": [ { "id": "n1", "data": { "config": { "password": {{rawValue}}, "port": 5432 } } } ], "edges": [] }
        """);

        var cfg = WorkflowSecretRedactor.Redact(def)["nodes"]![0]!["data"]!["config"]!.AsObject();

        cfg["password"]!.GetValue<string>().Should().Be("***");
        cfg["port"]!.GetValue<int>().Should().Be(5432, "non-secret numbers stay visible");
    }

    [Fact]
    public void Redact_SecretKeyWithNull_StaysNull()
    {
        var def = Parse("""{ "config": { "apiKey": null } }""");
        WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject()["apiKey"].Should().BeNull();
    }

    [Fact]
    public void Redact_EmptySecret_LeftEmptyNotMasked()
    {
        var def = Parse("""{ "config": { "apiKey": "" } }""");
        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();
        cfg["apiKey"]!.GetValue<string>().Should().Be("");
    }

    [Fact]
    public void Redact_IsCaseInsensitiveOnKeys()
    {
        var def = Parse("""{ "config": { "ApiKey": "sk-1", "PASSWORD": "p" } }""");
        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();
        cfg["ApiKey"]!.GetValue<string>().Should().Be("***");
        cfg["PASSWORD"]!.GetValue<string>().Should().Be("***");
    }

    [Theory]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("refreshToken")]
    [InlineData("clientSecret")]
    [InlineData("privateKey")]
    [InlineData("accessKey")]
    [InlineData("secretKey")]
    [InlineData("apiSecret")]
    [InlineData("webhookSecret")]
    public void Redact_MasksExtendedSecretKeys(string key)
    {
        // Custom-activity inputs (and cloud/OAuth creds) can use these key names; must be masked.
        var def = Parse($$"""{ "config": { "{{key}}": "s3cr3t-value" } }""");
        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();
        cfg[key]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiObjectHeaders_MasksCompleteOpaqueValue()
    {
        var def = Parse("""
        { "config": { "headers": {
            "Authorization": "Bearer sk-live-abc",
            "X-Api-Key": "key-123",
            "Content-Type": "application/json"
        } } }
        """);
        WorkflowSecretRedactor.Redact(def).AsObject()["config"]!["headers"]!.GetValue<string>()
            .Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiObjectHeaders_MasksUnknownLiteralAndPublicHeadersTogether()
    {
        var def = Parse("""
        { "config": { "headers": {
            "X-Tenant-Token": "opaque-tenant-credential",
            "Accept": "application/json",
            "Content-Type": "application/json"
        } } }
        """);

        WorkflowSecretRedactor.Redact(def).AsObject()["config"]!["headers"]!.GetValue<string>()
            .Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiObjectHeaders_MasksTemplateOnlyCustomHeaderBecauseFieldIsOpaque()
    {
        var def = Parse("""
        { "config": { "headers": {
            "X-Tenant-Token": "{{globals.TENANT_TOKEN}}"
        } } }
        """);

        WorkflowSecretRedactor.Redact(def).AsObject()["config"]!["headers"]!.GetValue<string>()
            .Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiObjectHeaders_MasksTemplateOnlyAuthorizationHeaderBecauseFieldIsOpaque()
    {
        var def = Parse("""
        { "config": { "headers": {
            "Authorization": "Bearer {{globals.API_TOKEN}}"
        } } }
        """);

        WorkflowSecretRedactor.Redact(def).AsObject()["config"]!["headers"]!.GetValue<string>()
            .Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiStringHeaders_WithInlineSecret_MasksWholeValue()
    {
        // The UI persists headers as a newline "Key: Value" string; the secret lives under key
        // `headers`, which is not itself a secret name — content detection must catch it.
        var def = Parse("""
        { "config": { "headers": "Content-Type: application/json\nAuthorization: Bearer sk-live-abc123" } }
        """);
        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();
        cfg["headers"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiStringHeaders_UnknownLiteralHeader_MasksWholeValue()
    {
        var def = Parse("""
        { "config": { "headers": "Accept: application/json\nX_Tenant.Token: opaque-tenant-credential" } }
        """);

        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();

        cfg["headers"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiStringHeaders_ReferencingGlobals_StillMaskedAsOpaque()
    {
        // The steered pattern references a secret global — no literal secret lives in the
        // definition.
        var def = Parse("""
        { "config": { "headers": "Content-Type: application/json\nAuthorization: Bearer {{globals.API_TOKEN}}" } }
        """);
        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();
        cfg["headers"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiBody_AlwaysMaskedWithoutClassifyingContents()
    {
        var secretBody = Parse("""{ "config": { "body": "{\"key\":\"sk_live_0123456789abcdef\"}" } }""");
        WorkflowSecretRedactor.Redact(secretBody).AsObject()["config"]!["body"]!.GetValue<string>().Should().Be("***");

        var benignBody = Parse("""{ "config": { "body": "{\"name\":\"disk-check\",\"count\":3}" } }""");
        WorkflowSecretRedactor.Redact(benignBody).AsObject()["config"]!["body"]!.GetValue<string>()
            .Should().Be("***");
    }

    [Fact]
    public void Redact_RunScript_AlwaysMaskedWithoutClassifyingContents()
    {
        var secretScript = Parse("""{ "config": { "script": "$apiToken = \"sk-live-9f8e7d6c5b4a\"; Invoke-RestMethod" } }""");
        WorkflowSecretRedactor.Redact(secretScript).AsObject()["config"]!["script"]!.GetValue<string>().Should().Be("***");

        var benignScript = Parse("""{ "config": { "script": "Get-Service | Where-Object Status -eq Running" } }""");
        WorkflowSecretRedactor.Redact(benignScript).AsObject()["config"]!["script"]!.GetValue<string>()
            .Should().Be("***");
    }

    [Fact]
    public void Redact_ScorchRaw_AlwaysMasksCompletePayload()
    {
        var def = Parse("""{ "config": { "scorchRaw": { "payload": "unclassified-legacy-secret" }, "url": "https://example.test" } }""");
        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();

        cfg["scorchRaw"]!.GetValue<string>().Should().Be("***");
        cfg["url"]!.GetValue<string>().Should().Be("https://example.test");
    }

    [Theory]
    [InlineData("startProgram", "arguments")]
    [InlineData("scheduledTask", "arguments")]
    [InlineData("wmiQuery", "filter")]
    [InlineData("sql", "query")]
    [InlineData("databaseTrigger", "query")]
    [InlineData("restApi", "url")]
    [InlineData("restApi", "proxyAddress")]
    [InlineData("waitForCondition", "url")]
    [InlineData("emailNotification", "subject")]
    [InlineData("log", "message")]
    [InlineData("jsonQuery", "jsonPath")]
    [InlineData("xmlQuery", "xpath")]
    [InlineData("textFileEdit", "replace")]
    [InlineData("textFileEdit", "matchPattern")]
    [InlineData("forEach", "items")]
    [InlineData("registryOperation", "value")]
    [InlineData("llmQuery", "prompt")]
    [InlineData("llmQuery", "systemPrompt")]
    [InlineData("llmQuery", "baseUrl")]
    [InlineData("serviceManagement", "binaryPath")]
    [InlineData("powerManagement", "message")]
    [InlineData("eventLogTrigger", "messagePattern")]
    public void Redact_MasksRuntimeConsumedOpaqueFieldForItsActivity(string activityType, string key)
    {
        var def = Parse($$"""
            { "nodes": [{ "data": { "activityType": "{{activityType}}", "config": {
              "{{key}}": "plain-looking-secret"
            } } }] }
            """);

        var config = WorkflowSecretRedactor.Redact(def)["nodes"]![0]!["data"]!["config"]!;
        config[key]!.GetValue<string>().Should().Be("***");
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("databaseTrigger")]
    [InlineData("startWorkflow")]
    [InlineData("forEach")]
    [InlineData("manualTrigger")]
    public void Redact_MasksCompleteParameterPayloadForActivitiesThatForwardValues(string activityType)
    {
        var def = Parse($$"""
            { "nodes": [{ "data": { "activityType": "{{activityType}}", "config": {
              "parameters": { "innocentName": "plain-looking-secret" }
            } } }] }
            """);

        var parameters = WorkflowSecretRedactor.Redact(def)["nodes"]![0]!["data"]!["config"]!["parameters"]!;
        parameters.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_MasksReturnDataObjectAndLiteralEdgeOperand_WithoutMaskingVariableOperand()
    {
        var def = Parse("""
            {
              "nodes": [{ "data": { "activityType": "returnData", "config": {
                "data": { "ordinaryName": "plain-looking-secret" }
              } } }],
              "edges": [{ "data": { "conditionExpression": {
                "type": "comparison",
                "left": { "kind": "variable", "value": "visible-structural-value" },
                "op": "==",
                "right": { "kind": "literal", "value": "plain-looking-secret" }
              } } }]
            }
            """);

        var redacted = WorkflowSecretRedactor.Redact(def);
        redacted["nodes"]![0]!["data"]!["config"]!["data"]!.GetValue<string>()
            .Should().Be("***");
        var expression = redacted["edges"]![0]!["data"]!["conditionExpression"]!;
        expression["left"]!["value"]!.GetValue<string>().Should().Be("visible-structural-value");
        expression["right"]!["value"]!.GetValue<string>().Should().Be("***");
    }

    [Theory]
    [InlineData("startProgram", "arguments")]
    [InlineData("sql", "query")]
    public void Redact_UsesConcreteNodeTypeWhenActivityTypeIsOmitted(string nodeType, string key)
    {
        var def = Parse($$"""
            { "nodes": [{ "type": "{{nodeType}}", "data": { "config": {
              "{{key}}": "plain-looking-secret"
            } } }] }
            """);

        WorkflowSecretRedactor.Redact(def)["nodes"]![0]!["data"]!["config"]![key]!
            .GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_NestedActivityTypeCannotOverrideOwningNodePolicy()
    {
        var def = Parse("""
            { "nodes": [{ "type": "activity", "data": {
              "activityType": "startProgram", "config": {
                "activityType": "log", "arguments": "--password plain-looking-secret"
              }
            }}] }
            """);

        var config = WorkflowSecretRedactor.Redact(def)["nodes"]![0]!["data"]!["config"]!;
        config["arguments"]!.GetValue<string>().Should().Be("***");
        config["activityType"]!.GetValue<string>().Should().Be("log");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Redact_MasksFreeFormCustomActivityStringInputsButKeepsStructuralIdentity(bool explicitActivityType)
    {
        var nodePrefix = explicitActivityType
            ? "\"type\":\"activity\",\"data\":{\"activityType\":\"custom:licensed_task\","
            : "\"type\":\"custom:licensed_task\",\"data\":{";
        var def = Parse("{\"nodes\":[{" + nodePrefix + "\"config\":{" +
                        "\"__customDefinitionId\":\"11111111-1111-1111-1111-111111111111\"," +
                        "\"__customKey\":\"licensed_task\",\"license\":\"plain-looking-key\"," +
                        "\"retries\":3}}}]}" );

        var config = WorkflowSecretRedactor.Redact(def)["nodes"]![0]!["data"]!["config"]!;
        config["license"]!.GetValue<string>().Should().Be("***");
        config["__customKey"]!.GetValue<string>().Should().Be("licensed_task");
        config["__customDefinitionId"]!.GetValue<string>().Should().Be(
            "11111111-1111-1111-1111-111111111111");
        config["retries"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void Redact_MasksLiteralOperandOnEitherSideAndInsideGroups()
    {
        var def = Parse("""
            { "edges": [{ "data": { "conditionExpression": {
              "type": "group", "op": "AND", "children": [
                { "type": "comparison",
                  "left": { "kind": "literal", "value": "left-secret" },
                  "op": "==", "right": { "kind": "literal", "value": "right-secret" } }
              ]
            } } }] }
            """);

        var comparison = WorkflowSecretRedactor.Redact(def)
            ["edges"]![0]!["data"]!["conditionExpression"]!["children"]![0]!;
        comparison["left"]!["value"]!.GetValue<string>().Should().Be("***");
        comparison["right"]!["value"]!.GetValue<string>().Should().Be("***");
    }

    // ---- RedactForAssistant ----------------------------------------------------------

    private static JsonElement SingleNode(string activityType, string configJson) => Parse($$"""
        { "nodes": [ { "id": "n1", "type": "activity",
            "data": { "activityType": "{{activityType}}", "config": {{configJson}} } } ], "edges": [] }
        """);

    private static System.Text.Json.Nodes.JsonObject AssistantConfig(string activityType, string configJson)
        => WorkflowSecretRedactor.RedactForAssistant(SingleNode(activityType, configJson))["nodes"]![0]!["data"]!["config"]!.AsObject();

    [Theory]
    [InlineData("runScript", "script", "Get-PSDrive C")]
    [InlineData("waitForCondition", "script", "Test-Path C:\\ready.flag")]
    [InlineData("sql", "query", "SELECT * FROM dbo.Jobs")]
    [InlineData("sql", "parameters", "@id=5")]
    [InlineData("databaseTrigger", "query", "SELECT MAX(Id) FROM dbo.Queue")]
    [InlineData("startProgram", "arguments", "/c dir C:\\temp")]
    [InlineData("scheduledTask", "arguments", "-File C:\\scripts\\run.ps1")]
    [InlineData("wmiQuery", "query", "SELECT * FROM Win32_Service")]
    [InlineData("wmiQuery", "filter", "Name='Spooler'")]
    [InlineData("wmiQuery", "arguments", "Name")]
    public void RedactForAssistant_ReadableFields_StayInClear(string activityType, string key, string value)
    {
        var cfg = AssistantConfig(activityType, JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value }));
        cfg[key]!.GetValue<string>().Should().Be(value);
    }

    [Theory]
    [InlineData("runScript", "script", "$pw = 'abc'; Connect-Thing -Credential $pw")]
    [InlineData("runScript", "script", "$s = ConvertTo-SecureString 'P@ss' -AsPlainText -Force")]
    [InlineData("sql", "query", "CREATE LOGIN app WITH PASSWORD = 'abc'")]
    [InlineData("startProgram", "arguments", "-Password 'x' -User svc")]
    public void RedactForAssistant_RecognisedSecret_MasksWholeValue(string activityType, string key, string value)
    {
        var cfg = AssistantConfig(activityType, JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value }));
        cfg[key]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void RedactForAssistant_OtherOpaqueFieldsAndNamedSecrets_StayMasked()
    {
        var def = Parse("""
        { "nodes": [
            { "id": "r", "type": "activity", "data": { "activityType": "restApi",
              "config": { "url": "https://api.example/x", "body": "{\"a\":1}", "headers": "Accept: text/plain", "apiKey": "k" } } },
            { "id": "l", "type": "activity", "data": { "activityType": "llmQuery",
              "config": { "prompt": "Summarise this" } } },
            { "id": "s", "type": "activity", "data": { "activityType": "runScript",
              "config": { "script": "Get-Date", "password": "hunter2" } } }
          ],
          "edges": [ { "id": "e", "source": "r", "target": "l", "data": { "conditionExpression":
            { "type": "comparison", "operator": "==", "left": { "kind": "variable", "value": "r.output" },
              "right": { "kind": "literal", "value": "ok" } } } } ] }
        """);

        var node = WorkflowSecretRedactor.RedactForAssistant(def);
        var rest = node["nodes"]![0]!["data"]!["config"]!;
        rest["url"]!.GetValue<string>().Should().Be("***");
        rest["body"]!.GetValue<string>().Should().Be("***");
        rest["headers"]!.GetValue<string>().Should().Be("***");
        rest["apiKey"]!.GetValue<string>().Should().Be("***");
        node["nodes"]![1]!["data"]!["config"]!["prompt"]!.GetValue<string>().Should().Be("***");
        var script = node["nodes"]![2]!["data"]!["config"]!;
        script["script"]!.GetValue<string>().Should().Be("Get-Date");
        script["password"]!.GetValue<string>().Should().Be("***");
        node["edges"]![0]!["data"]!["conditionExpression"]!["right"]!["value"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void RedactForAssistant_CustomActivityInputNamedScript_StaysMasked()
    {
        var cfg = AssistantConfig("custom:deploy", """{ "__customKey": "deploy", "script": "Get-Date", "target": "web01" }""");

        cfg["script"]!.GetValue<string>().Should().Be("***");
        cfg["target"]!.GetValue<string>().Should().Be("***");
        cfg["__customKey"]!.GetValue<string>().Should().Be("deploy");
    }

    [Fact]
    public void RedactForAssistant_ScriptOutsideABuiltInConfig_StaysMasked()
    {
        // No activity context (and a nested "script" inside another field) never qualifies.
        var cfg = WorkflowSecretRedactor.RedactForAssistant(Parse("""{ "config": { "script": "Get-Date" } }"""))["config"]!;
        cfg["script"]!.GetValue<string>().Should().Be("***");

        var nested = AssistantConfig("runScript", """{ "script": "Get-Date", "extra": { "script": "Get-Date" } }""");
        nested["extra"]!["script"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void RedactForAssistant_StructuredSqlParameters_StayMasked()
    {
        var cfg = AssistantConfig("sql", """{ "query": "SELECT 1", "parameters": [ { "name": "@id", "value": "5" } ] }""");
        cfg["query"]!.GetValue<string>().Should().Be("SELECT 1");
        cfg["parameters"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_DefaultMode_StillMasksReadableFields()
    {
        var cfg = WorkflowSecretRedactor.Redact(SingleNode("runScript", """{ "script": "Get-Date" }"""))["nodes"]![0]!["data"]!["config"]!;
        cfg["script"]!.GetValue<string>().Should().Be("***");
    }
}
