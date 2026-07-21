using System.Text.Json;
using System.Text.Json.Nodes;
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
        // Custom-activity inputs (and cloud/OAuth creds) named with these keys previously leaked.
        var def = Parse($$"""{ "config": { "{{key}}": "s3cr3t-value" } }""");
        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();
        cfg[key]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_RestApiObjectHeaders_MasksCredentialHeaders_PreservesBenignOnes()
    {
        var def = Parse("""
        { "config": { "headers": {
            "Authorization": "Bearer sk-live-abc",
            "X-Api-Key": "key-123",
            "Content-Type": "application/json"
        } } }
        """);
        var headers = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!["headers"]!.AsObject();
        headers["Authorization"]!.GetValue<string>().Should().Be("***");
        headers["X-Api-Key"]!.GetValue<string>().Should().Be("***");
        headers["Content-Type"]!.GetValue<string>().Should().Be("application/json"); // benign preserved
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
    public void Redact_RestApiStringHeaders_ReferencingGlobals_NotMasked()
    {
        // The steered pattern references a secret global — no literal secret lives in the definition.
        var def = Parse("""
        { "config": { "headers": "Content-Type: application/json\nAuthorization: Bearer {{globals.API_TOKEN}}" } }
        """);
        var cfg = WorkflowSecretRedactor.Redact(def).AsObject()["config"]!.AsObject();
        cfg["headers"]!.GetValue<string>().Should()
            .Be("Content-Type: application/json\nAuthorization: Bearer {{globals.API_TOKEN}}");
    }

    [Fact]
    public void Redact_RestApiBody_WithInlineToken_Masked_BenignBodyPreserved()
    {
        var secretBody = Parse("""{ "config": { "body": "{\"key\":\"sk_live_0123456789abcdef\"}" } }""");
        WorkflowSecretRedactor.Redact(secretBody).AsObject()["config"]!["body"]!.GetValue<string>().Should().Be("***");

        var benignBody = Parse("""{ "config": { "body": "{\"name\":\"disk-check\",\"count\":3}" } }""");
        WorkflowSecretRedactor.Redact(benignBody).AsObject()["config"]!["body"]!.GetValue<string>()
            .Should().Be("{\"name\":\"disk-check\",\"count\":3}");
    }

    [Fact]
    public void Redact_RunScript_WithInlineSecretAssignment_Masked_BenignScriptPreserved()
    {
        var secretScript = Parse("""{ "config": { "script": "$apiToken = \"sk-live-9f8e7d6c5b4a\"; Invoke-RestMethod" } }""");
        WorkflowSecretRedactor.Redact(secretScript).AsObject()["config"]!["script"]!.GetValue<string>().Should().Be("***");

        var benignScript = Parse("""{ "config": { "script": "Get-Service | Where-Object Status -eq Running" } }""");
        WorkflowSecretRedactor.Redact(benignScript).AsObject()["config"]!["script"]!.GetValue<string>()
            .Should().Be("Get-Service | Where-Object Status -eq Running");
    }
}
