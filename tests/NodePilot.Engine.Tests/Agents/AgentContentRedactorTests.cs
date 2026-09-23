using System.Text.Json;
using NodePilot.Engine.Agents;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public class AgentContentRedactorTests
{
    [Fact]
    public void NestedSerializedToolResponse_RedactsBeforeEvidenceAndModelConsumption()
    {
        var body = JsonSerializer.Serialize(new { status = "ok", password = "password-canary", apiKey = "key-canary" });
        var tool = JsonSerializer.Serialize(new { statusCode = 200, body });
        var snapshot = JsonSerializer.Serialize(new { evidenceId = "ev-00001", text = tool });
        var result = AgentContentRedactor.Redact(snapshot, new OutputRedactor());
        Assert.DoesNotContain("password-canary", result);
        Assert.DoesNotContain("key-canary", result);
        using var evidence = JsonDocument.Parse(result);
        Assert.Equal("ev-00001", evidence.RootElement.GetProperty("evidenceId").GetString());
        using var response = JsonDocument.Parse(evidence.RootElement.GetProperty("text").GetString()!);
        Assert.Equal(200, response.RootElement.GetProperty("statusCode").GetInt32());
        using var content = JsonDocument.Parse(response.RootElement.GetProperty("body").GetString()!);
        Assert.Equal("ok", content.RootElement.GetProperty("status").GetString());
        Assert.Equal("***", content.RootElement.GetProperty("password").GetString());
        Assert.Equal("***", content.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal(result, AgentContentRedactor.Redact(result, new OutputRedactor()));
    }

    [Fact]
    public void PlainTextAndArrays_KeepEvidenceAndMaskNamedSecrets()
    {
        var result = AgentContentRedactor.Redact("[{\"status\":\"ok\",\"clientSecret\":{\"value\":\"opaque\"},\"text\":\"password=plain\"}]", new OutputRedactor());
        Assert.DoesNotContain("opaque", result);
        Assert.DoesNotContain("plain", result);
        using var parsed = JsonDocument.Parse(result);
        Assert.Equal("ok", parsed.RootElement[0].GetProperty("status").GetString());
        Assert.Equal("***", parsed.RootElement[0].GetProperty("clientSecret").GetString());
    }
}
