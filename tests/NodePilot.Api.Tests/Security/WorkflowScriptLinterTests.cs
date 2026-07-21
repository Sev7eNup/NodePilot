using FluentAssertions;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public class WorkflowScriptLinterTests
{
    private static string Def(string script, string activityType = "runScript", string stepId = "step-1")
        => $$"""
             {
               "nodes": [
                 {
                   "id": "{{stepId}}",
                   "type": "activity",
                   "data": {
                     "activityType": "{{activityType}}",
                     "config": { "script": "{{script.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")}}" }
                   }
                 }
               ]
             }
             """;

    private static string MultiNodeDef(params (string Id, string Script)[] nodes)
    {
        var nodeJsons = nodes.Select(n => $$"""
            {
              "id": "{{n.Id}}",
              "type": "activity",
              "data": {
                "activityType": "runScript",
                "config": { "script": "{{n.Script.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}}" }
              }
            }
            """);
        return $"{{\"nodes\":[{string.Join(",", nodeJsons)}]}}";
    }

    [Fact]
    public void Lint_CleanScript_ReturnsNoWarnings()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("Get-Process | Select-Object Name, CPU"));
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Lint_InvokeExpression_FlagsRule()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("Invoke-Expression $userInput"));
        warnings.Should().ContainSingle(w => w.Rule == "invoke-expression");
    }

    [Fact]
    public void Lint_IexAlias_FlagsRule()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("iex $userInput"));
        warnings.Should().ContainSingle(w => w.Rule == "invoke-expression");
    }

    [Fact]
    public void Lint_IexAlias_CaseInsensitive()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("IEX $x"));
        warnings.Should().ContainSingle(w => w.Rule == "invoke-expression");
    }

    [Fact]
    public void Lint_InvokeExpression_Uppercase_FlagsRule()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("INVOKE-EXPRESSION $x"));
        warnings.Should().ContainSingle(w => w.Rule == "invoke-expression");
    }

    [Fact]
    public void Lint_CallOperatorOnVariable_FlagsRule()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("& $cmd"));
        warnings.Should().ContainSingle(w => w.Rule == "call-operator-on-variable");
    }

    [Fact]
    public void Lint_CallOperatorOnVariableWithParens_FlagsRule()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("& ($cmd)"));
        warnings.Should().ContainSingle(w => w.Rule == "call-operator-on-variable");
    }

    [Fact]
    public void Lint_CallOperatorOnLiteral_NotFlagged()
    {
        // Safe usage: literal path with & is a normal call, not dynamic
        var warnings = WorkflowScriptLinter.Lint(Def(@"& 'C:\Tools\tool.exe' --arg value"));
        warnings.Should().NotContain(w => w.Rule == "call-operator-on-variable");
    }

    [Fact]
    public void Lint_ExecutionContextInvoke_FlagsRule()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("$ExecutionContext.InvokeCommand.InvokeScript $x"));
        warnings.Should().ContainSingle(w => w.Rule == "execution-context-invoke");
    }

    [Fact]
    public void Lint_ExecutionContextInvoke_CaseInsensitive()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("$executioncontext.invokecommand.invokescript $x"));
        warnings.Should().ContainSingle(w => w.Rule == "execution-context-invoke");
    }

    [Fact]
    public void Lint_MultipleRules_ReturnsAllWarnings()
    {
        var script = "Invoke-Expression $x\n& $cmd\n$ExecutionContext.InvokeCommand.InvokeScript $y";
        var warnings = WorkflowScriptLinter.Lint(Def(script));
        warnings.Should().HaveCount(3);
        warnings.Select(w => w.Rule).Should().Contain("invoke-expression")
            .And.Contain("call-operator-on-variable")
            .And.Contain("execution-context-invoke");
    }

    [Fact]
    public void Lint_MultipleNodes_ScansAll_CorrectStepId()
    {
        var def = MultiNodeDef(
            ("safe-step", "Get-Process"),
            ("danger-step", "iex $x"),
            ("also-safe", "Write-Output done"));

        var warnings = WorkflowScriptLinter.Lint(def);
        warnings.Should().ContainSingle();
        warnings[0].StepId.Should().Be("danger-step");
        warnings[0].Rule.Should().Be("invoke-expression");
    }

    [Fact]
    public void Lint_NonScriptNode_Ignored()
    {
        var def = """
                  {
                    "nodes": [
                      {
                        "id": "delay-1",
                        "type": "activity",
                        "data": {
                          "activityType": "delay",
                          "config": { "seconds": 5 }
                        }
                      }
                    ]
                  }
                  """;
        var warnings = WorkflowScriptLinter.Lint(def);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Lint_NodeWithoutScript_Skipped()
    {
        var def = """
                  {
                    "nodes": [
                      {
                        "id": "step-1",
                        "data": {
                          "activityType": "runScript",
                          "config": { "timeoutSeconds": 30 }
                        }
                      }
                    ]
                  }
                  """;
        var warnings = WorkflowScriptLinter.Lint(def);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Lint_MalformedJson_ReturnsEmpty()
    {
        var warnings = WorkflowScriptLinter.Lint("this is not json {{{");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Lint_EmptyDefinition_ReturnsEmpty()
    {
        var warnings = WorkflowScriptLinter.Lint("{}");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Lint_MultilineScript_FlagsCorrectRule()
    {
        // Pattern is on line 3 — Regex must still match across multiline
        const string script = "Get-Date\nWrite-Output hello\nInvoke-Expression $x\nGet-Process";
        var warnings = WorkflowScriptLinter.Lint(Def(script));
        warnings.Should().ContainSingle(w => w.Rule == "invoke-expression");
    }

    [Fact]
    public void Lint_WarningContainsStepId()
    {
        var warnings = WorkflowScriptLinter.Lint(Def("iex $x", stepId: "my-custom-step"));
        warnings.Should().ContainSingle();
        warnings[0].StepId.Should().Be("my-custom-step");
    }

    [Fact]
    public void Lint_NodeWithoutId_UsesQuestionMark()
    {
        var def = """
                  {
                    "nodes": [
                      {
                        "type": "activity",
                        "data": {
                          "config": { "script": "iex $x" }
                        }
                      }
                    ]
                  }
                  """;
        var warnings = WorkflowScriptLinter.Lint(def);
        warnings.Should().ContainSingle();
        warnings[0].StepId.Should().Be("?");
    }
}
