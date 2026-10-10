using System.Text.Json;
using NodePilot.Core.Agents;
using NodePilot.Engine.Agents;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentSkillExecutionTests
{
    [Theory]
    [InlineData("echo hello")]
    [InlineData("echo \"hello world\"")]
    [InlineData("echo hello   world")]
    public async Task PreparedCmdSkill_PreservesLiteralEchoOutput(string source)
    {
        using var original = JsonDocument.Parse(await AgentShellTests.Execute(AgentProcessScript.Build("cmd", source, null, null)));
        using var prepared = JsonDocument.Parse(await AgentShellTests.Execute(AgentProcessScript.Build("cmd",
            AgentPermissionPolicy.PrepareSkillScript("cmd", source, []), null, null)));
        Assert.Equal(original.RootElement.GetProperty("stdout").GetString(), prepared.RootElement.GetProperty("stdout").GetString());
        Assert.Equal(0, prepared.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData(0, false, null)]
    [InlineData(1, false, "process_failed")]
    [InlineData(null, false, "process_failed")]
    [InlineData(0, true, "process_timeout")]
    [InlineData(null, true, "process_timeout")]
    public void ProcessResult_DoesNotConfuseReturnedOutputWithSuccess(int? exitCode, bool timedOut, string? expected)
    {
        var json = JsonSerializer.Serialize(new { exitCode, timedOut, stdout = "partial evidence", stderr = "failure details" });
        if (expected is null) Assert.Equal(json, AgentSkillExecution.RequireSuccess(json));
        else
        {
            var error = Assert.Throws<AgentToolExecutionException>(() => AgentSkillExecution.RequireSuccess(json));
            Assert.Equal(expected, error.Code);
            Assert.Equal("partial evidence", error.Details!.Value.GetProperty("stdout").GetString());
            Assert.Equal("failure details", error.Details.Value.GetProperty("stderr").GetString());
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"exitCode\":0,\"timedOut\":\"false\"}")]
    public void MissingOrMalformedProcessStatusFails(string output)
        => Assert.Equal("invalid_process_result", Assert.Throws<AgentToolExecutionException>(() => AgentSkillExecution.RequireSuccess(output)).Code);

    [Theory]
    [InlineData("AllSigned", "Valid", true, null)]
    [InlineData("AllSigned", "Valid", false, "skill_publisher_untrusted")]
    [InlineData("AllSigned", "NotSigned", true, "skill_signature_required")]
    [InlineData("AllSigned", "HashMismatch", true, "skill_signature_required")]
    [InlineData("Restricted", "Valid", true, "execution_policy_blocked")]
    [InlineData("Undefined", "Valid", true, "execution_policy_unknown")]
    [InlineData("RemoteSigned", "NotSigned", false, null)]
    public void TargetPolicyAndPublisherAreIndependentChecks(string policy, string signature, bool trustedPublisher, string? expected)
    {
        var metadata = JsonSerializer.SerializeToElement(new { policy, signature, trustedPublisher, hash = "ABC" });
        if (expected is null) AgentSkillExecution.ValidateFileMetadata(metadata, "abc");
        else Assert.Equal(expected, Assert.Throws<AgentToolExecutionException>(() => AgentSkillExecution.ValidateFileMetadata(metadata, "abc")).Code);
    }
}
