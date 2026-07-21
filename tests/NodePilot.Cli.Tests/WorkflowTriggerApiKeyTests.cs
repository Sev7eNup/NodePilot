using FluentAssertions;
using NodePilot.Cli.Commands.Workflow;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// Unit tests for the API-key resolution rules in <see cref="WorkflowTriggerCommand"/>.
/// Precedence: --api-key flag > --api-key-stdin > NODEPILOT_TRIGGER_API_KEY env. The
/// env path is the recommended one for scripts because it never lands in shell history.
/// </summary>
public class WorkflowTriggerApiKeyTests
{
    [Fact]
    public void Flag_TakesPrecedenceOverEnv()
    {
        var prev = Environment.GetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY");
        Environment.SetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY", "from-env");
        try
        {
            var settings = new WorkflowTriggerSettings { ApiKey = "from-flag" };
            WorkflowTriggerCommand.ResolveApiKey(settings).Should().Be("from-flag");
        }
        finally { Environment.SetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY", prev); }
    }

    [Fact]
    public void Env_UsedWhenNoFlagAndNoStdin()
    {
        var prev = Environment.GetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY");
        Environment.SetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY", "from-env");
        try
        {
            var settings = new WorkflowTriggerSettings();
            WorkflowTriggerCommand.ResolveApiKey(settings).Should().Be("from-env");
        }
        finally { Environment.SetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY", prev); }
    }

    [Fact]
    public void Null_WhenNothingProvided()
    {
        var prev = Environment.GetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY");
        Environment.SetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY", null);
        try
        {
            var settings = new WorkflowTriggerSettings();
            WorkflowTriggerCommand.ResolveApiKey(settings).Should().BeNull();
        }
        finally { Environment.SetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY", prev); }
    }

    [Fact]
    public void Stdin_ReadsFirstLine()
    {
        var prev = Environment.GetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY");
        Environment.SetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY", null);

        var originalIn = Console.In;
        using var reader = new StringReader("stdin-key\nignored-second-line");
        Console.SetIn(reader);
        try
        {
            var settings = new WorkflowTriggerSettings { ApiKeyStdin = true };
            WorkflowTriggerCommand.ResolveApiKey(settings).Should().Be("stdin-key");
        }
        finally
        {
            Console.SetIn(originalIn);
            Environment.SetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY", prev);
        }
    }
}
