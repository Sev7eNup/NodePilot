using System.Text.Json;
using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class AgentConclusionTests
{
    [Fact]
    public void CoverageCannotUpgradeAnExplicitBlockedAssessment()
    {
        var result = AgentConclusion.Parse("""{"outcome":"blocked","reason":"Required access unavailable","report":"Cannot establish result","coverage":[{"requirement":"Describe limitation","status":"fulfilled","basis":"No source access"},{"requirement":"Establish actual inventory","status":"unresolved","basis":"No source access"}]}""", 2000);
        Assert.Equal("blocked", result.Outcome);
    }
    [Theory]
    [InlineData("fulfilled", "partial")]
    [InlineData("unresolved", "blocked")]
    public void UnresolvedTaskRequirementCannotBeAssessedCompleted(string firstStatus, string expected)
    {
        var result = AgentConclusion.Parse(JsonSerializer.Serialize(new {
            outcome = "completed", reason = "Work finished", report = "Some information is unavailable",
            coverage = new[] {
                new { requirement = "Read inventory", status = firstStatus, basis = "Available source is incomplete" },
                new { requirement = "Compare complete inventories", status = "unresolved", basis = "Second inventory unavailable" }
            }
        }), 2000);
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void SchemaErrorSuppliesMissingFieldsAndLimitsWithoutArgumentValues()
    {
        var contract = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","required":["owner"],"properties":{"owner":{"type":"string"},"conclusion":{"type":"string","maxLength":5}}}""");
        var error = AgentJsonSchema.ArgumentError(AgentJsonSchema.Compile(contract),
            JsonSerializer.SerializeToElement(new { conclusion = "private-do-not-echo" }), contract);
        Assert.Contains("owner", error);
        Assert.Contains("5", error);
        Assert.DoesNotContain("private", error);
    }
    [Fact]
    public void ConfigurationRejectsBudgetWithoutRoomForInvestigationAndReport()
    {
        var raw = JsonSerializer.SerializeToElement(new { task = "Compare", maxModelCalls = 1 });
        Assert.Throws<ArgumentException>(() => AgentConfiguration.Parse(raw, false));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("partial")]
    [InlineData("blocked")]
    public void TaskAssessmentIsSeparateFromTheUserJsonResult(string outcome)
    {
        var result = AgentConclusion.Parse(JsonSerializer.Serialize(new { outcome, reason = "Evidence limitation", report = new { answer = 42 },
            coverage = new[] { new { requirement = "Answer question", status = "fulfilled", basis = "Supplied facts" } } }), 1000, jsonReport: true);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal("{\"answer\":42}", result.Report);
    }

    [Theory]
    [InlineData("{\"outcome\":\"Succeeded\",\"reason\":\"done\",\"report\":\"text\"}")]
    [InlineData("{\"outcome\":\"completed\",\"reason\":\"\",\"report\":\"text\"}")]
    [InlineData("{\"outcome\":\"partial\",\"reason\":\"no access\",\"report\":\"\"}")]
    public void InvalidAssessmentDoesNotBecomeSuccess(string json) => Assert.Throws<JsonException>(() => AgentConclusion.Parse(json, 1000));

    [Fact]
    public void FinalInputRejectsAContextTooSmallForTheOriginalTask()
    {
        Assert.Throws<AgentBudgetExceededException>(() => AgentConclusion.Prompt(
            new() { Task = new string('x', 30_000) }, "Draft", "Latest", null, null, 20_000));
    }

    [Fact]
    public void BoundedFinalInputRetainsEveryMembersIdentityAndEveryCheckStatus()
    {
        var findings = JsonSerializer.SerializeToElement(Enumerable.Range(0, 12).Select(i => new {
            memberId = "member" + i, status = "completed", content = new string('x', 60000)
        }));
        var checks = JsonSerializer.SerializeToElement(Enumerable.Range(0, 20).Select(i => new {
            id = "check" + i, status = "blocked", limitation = new string('y', 6000)
        }));
        var prompt = AgentConclusion.Prompt(new() { Task = "Compare" }, "Original", "Updated", findings, checks, 40000);
        Assert.True(prompt.Length + AgentConclusion.Instructions.Length + 2000 <= 40000);
        using var doc = JsonDocument.Parse(prompt["Final report synthesis: ".Length..]);
        Assert.Equal(12, doc.RootElement.GetProperty("memberFindings").GetArrayLength());
        Assert.Equal(20, doc.RootElement.GetProperty("investigation").GetArrayLength());
        Assert.All(doc.RootElement.GetProperty("investigation").EnumerateArray(), c => Assert.Equal("blocked", c.GetProperty("status").GetString()));
        Assert.True(doc.RootElement.GetProperty("memberFindings")[0].GetProperty("content").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void SchemaErrorIdentifiesFieldAndConstraintWithoutEchoingItsValue()
    {
        var schema = AgentJsonSchema.Compile(JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"conclusion":{"type":"string","maxLength":5}}}"""));
        var error = AgentJsonSchema.ArgumentError(schema, JsonSerializer.SerializeToElement(new { conclusion = "private-do-not-echo" }));
        Assert.Contains("/conclusion", error);
        Assert.Contains("maxLength", error);
        Assert.DoesNotContain("private", error);
    }

    [Fact]
    public void FinalCallReservationCannotBeSpentByInvestigation()
    {
        var budget = new AgentBudget(3, 20, 4);
        budget.ReserveFinalReport();
        budget.TakeModelCall();
        budget.TakeModelCall();
        Assert.Throws<AgentBudgetExceededException>(() => { budget.TakeModelCall(); });
        budget.BeginFinalReport();
        budget.TakeModelCall();
        Assert.Equal(3, budget.ModelCalls);
        Assert.Throws<AgentBudgetExceededException>(() => { budget.TakeModelCall(); });
    }

    [Fact]
    public async Task SharedBudgetNumbersAreUniqueAndBatchReservationIsAtomic()
    {
        var budget = new AgentBudget(200, 5, 2);
        var numbers = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => budget.TakeModelCall(), TestContext.Current.CancellationToken)));
        Assert.Equal(Enumerable.Range(1, 200), numbers.Order());
        Assert.Throws<AgentBudgetExceededException>(() => budget.TakeDelegationBatch(3, 3));
        Assert.Equal(0, budget.ToolCalls);
        Assert.Equal(0, budget.Delegations);
        budget.TakeDelegationBatch(2, 2);
        Assert.Equal(2, budget.Snapshot().ToolCalls);
        Assert.Equal(2, budget.Snapshot().Delegations);
    }
}
