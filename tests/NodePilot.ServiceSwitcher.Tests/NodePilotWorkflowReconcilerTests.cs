using System.Text.Json;
using FluentAssertions;
using NodePilot.ServiceSwitcher.Configuration;
using NodePilot.ServiceSwitcher.Services;
using Xunit;

namespace NodePilot.ServiceSwitcher.Tests;

public sealed class NodePilotWorkflowReconcilerTests
{
    [Fact]
    public async Task Reconcile_DisablesAndCancelsUnlistedAndPermanentlyEnablesAllowed()
    {
        var allowedId = Guid.NewGuid();
        var unlistedId = Guid.NewGuid();
        var runner = new StatefulNodePilotRunner([
            new NodePilotWorkflow(allowedId, "Allowed", false),
            new NodePilotWorkflow(unlistedId, "Old", true),
        ], [unlistedId]);
        var reconciler = new NodePilotWorkflowReconciler(runner, new RecordingLogger());

        await reconciler.ReconcileAsync(Configuration(), ["Allowed"], null, CancellationToken.None);

        runner.Operations.Should().ContainInOrder(
            $"disable:{unlistedId}",
            $"cancel-all:{unlistedId}",
            $"enable:{allowedId}");
        runner.Workflows.Single(workflow => workflow.Id == allowedId).IsEnabled.Should().BeTrue();
        runner.Workflows.Single(workflow => workflow.Id == unlistedId).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Reconcile_DoesNotCancelInactiveUnlistedWorkflows()
    {
        var allowedId = Guid.NewGuid();
        var inactive = Enumerable.Range(0, 50)
            .Select(index => new NodePilotWorkflow(Guid.NewGuid(), $"Inactive {index}", false))
            .ToArray();
        var runner = new StatefulNodePilotRunner([
            new NodePilotWorkflow(allowedId, "Allowed", true),
            .. inactive,
        ]);

        await new NodePilotWorkflowReconciler(runner, new RecordingLogger())
            .ReconcileAsync(Configuration(), [allowedId.ToString()], null, CancellationToken.None);

        runner.Operations.Should().NotContain(operation => operation.StartsWith("cancel-all:"));
        runner.Operations.Should().Contain("operations:graph");
    }

    [Fact]
    public async Task Reconcile_RejectsUnknownAllowlistEntryBeforeMutation()
    {
        var runner = new StatefulNodePilotRunner([
            new NodePilotWorkflow(Guid.NewGuid(), "Known", true),
        ]);

        var action = () => new NodePilotWorkflowReconciler(runner, new RecordingLogger())
            .ReconcileAsync(Configuration(), ["Missing"], null, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
        runner.Operations.Should().BeEmpty();
    }

    [Fact]
    public void ResolveAllowList_RejectsAmbiguousNamesAndAcceptsGuid()
    {
        var first = new NodePilotWorkflow(Guid.NewGuid(), "Duplicate", false);
        var second = new NodePilotWorkflow(Guid.NewGuid(), "Duplicate", false);

        var byName = () => NodePilotWorkflowReconciler.ResolveAllowList([first, second], ["Duplicate"], "workflow");
        byName.Should().Throw<InvalidOperationException>().WithMessage("*ambiguous*");

        NodePilotWorkflowReconciler.ResolveAllowList([first, second], [first.Id.ToString()], "workflow")
            .Should().ContainSingle().Which.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task Reconcile_ExpiredSessionReauthenticatesAndRetriesTheInterruptedCommand()
    {
        var allowedId = Guid.NewGuid();
        var runner = new ExpiredSessionNodePilotRunner([
            new NodePilotWorkflow(allowedId, "Allowed", true),
        ]);
        var prompt = new RecordingCredentialPrompt(new NodePilotCredentials("switcher-admin", "correct horse"));
        var reconciler = new NodePilotWorkflowReconciler(runner, new RecordingLogger(), prompt);

        await reconciler.ReconcileAsync(Configuration(), [allowedId.ToString()], null, CancellationToken.None);

        prompt.RequestCount.Should().Be(1);
        runner.LoginAttempts.Should().Be(1);
        runner.LoginUsername.Should().Be("switcher-admin");
        runner.LoginPassword.Should().Be("correct horse");
        runner.LoginArguments.Should().NotContain("correct horse",
            "the password must only be passed through standard input");
        runner.WorkflowListAttempts.Should().BeGreaterThan(1,
            "the command interrupted by the expired session must be retried after login");
    }

    [Fact]
    public async Task Reconcile_InvalidCredentialsPromptsAgainAndThenContinues()
    {
        var allowedId = Guid.NewGuid();
        var runner = new ExpiredSessionNodePilotRunner([
            new NodePilotWorkflow(allowedId, "Allowed", true),
        ], rejectedLoginAttempts: 1);
        var prompt = new RecordingCredentialPrompt(
            new NodePilotCredentials("switcher-admin", "wrong"),
            new NodePilotCredentials("switcher-admin", "correct"));

        await new NodePilotWorkflowReconciler(runner, new RecordingLogger(), prompt)
            .ReconcileAsync(Configuration(), [allowedId.ToString()], null, CancellationToken.None);

        prompt.RequestCount.Should().Be(2);
        prompt.PreviousErrors[1].Should().Contain("ungültige Credentials");
        runner.LoginAttempts.Should().Be(2);
        runner.LoginPassword.Should().Be("correct");
    }

    [Fact]
    public async Task Reconcile_CancelledSignInStopsWithClearFailure()
    {
        var allowedId = Guid.NewGuid();
        var runner = new ExpiredSessionNodePilotRunner([
            new NodePilotWorkflow(allowedId, "Allowed", true),
        ]);
        var prompt = new RecordingCredentialPrompt((NodePilotCredentials?)null);

        var action = () => new NodePilotWorkflowReconciler(runner, new RecordingLogger(), prompt)
            .ReconcileAsync(Configuration(), [allowedId.ToString()], null, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cancelled*");
        runner.LoginAttempts.Should().Be(0);
    }

    private static NodePilotWorkloadConfiguration Configuration() =>
        new(@"\\server\share\nodepilot.txt", "np.exe", "switcher");

    private sealed class StatefulNodePilotRunner : ICommandRunner
    {
        public StatefulNodePilotRunner(
            IEnumerable<NodePilotWorkflow> workflows,
            IEnumerable<Guid>? activeWorkflowIds = null)
        {
            Workflows = workflows.ToList();
            ActiveWorkflowIds = activeWorkflowIds?.ToHashSet() ?? [];
        }
        public List<NodePilotWorkflow> Workflows { get; }
        public HashSet<Guid> ActiveWorkflowIds { get; }
        public List<string> Operations { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string? standardInput = null)
        {
            if (arguments[0] == "workflow" && arguments[1] == "list")
                return Task.FromResult(new CommandResult(0, JsonSerializer.Serialize(Workflows), string.Empty));

            if (arguments[0] == "operations" && arguments[1] == "graph")
            {
                Operations.Add("operations:graph");
                var graph = new
                {
                    nodes = Workflows.Select(workflow => new
                    {
                        workflowId = workflow.Id,
                        runningCount = ActiveWorkflowIds.Contains(workflow.Id) ? 1 : 0,
                    }),
                };
                return Task.FromResult(new CommandResult(0, JsonSerializer.Serialize(graph), string.Empty));
            }

            var operation = arguments[1];
            var id = Guid.Parse(arguments[2]);
            Operations.Add($"{operation}:{id}");
            var index = Workflows.FindIndex(workflow => workflow.Id == id);
            if (operation == "enable") Workflows[index] = Workflows[index] with { IsEnabled = true };
            if (operation == "disable") Workflows[index] = Workflows[index] with { IsEnabled = false };
            if (operation == "cancel-all") ActiveWorkflowIds.Remove(id);
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class RecordingCredentialPrompt : INodePilotCredentialPrompt
    {
        private readonly Queue<NodePilotCredentials?> _responses;

        public RecordingCredentialPrompt(params NodePilotCredentials?[] responses) =>
            _responses = new Queue<NodePilotCredentials?>(responses);

        public int RequestCount { get; private set; }
        public List<string?> PreviousErrors { get; } = [];

        public Task<NodePilotCredentials?> PromptAsync(
            string profile,
            string? previousError,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            PreviousErrors.Add(previousError);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ExpiredSessionNodePilotRunner : ICommandRunner
    {
        private readonly IReadOnlyList<NodePilotWorkflow> _workflows;
        private int _remainingRejectedLogins;
        private bool _authenticated;

        public ExpiredSessionNodePilotRunner(
            IReadOnlyList<NodePilotWorkflow> workflows,
            int rejectedLoginAttempts = 0)
        {
            _workflows = workflows;
            _remainingRejectedLogins = rejectedLoginAttempts;
        }

        public int WorkflowListAttempts { get; private set; }
        public int LoginAttempts { get; private set; }
        public string? LoginUsername { get; private set; }
        public string? LoginPassword { get; private set; }
        public IReadOnlyList<string> LoginArguments { get; private set; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string? standardInput = null)
        {
            if (arguments[0] == "auth" && arguments[1] == "login")
            {
                LoginAttempts++;
                LoginArguments = arguments;
                LoginUsername = arguments[Array.IndexOf(arguments.ToArray(), "--username") + 1];
                LoginPassword = standardInput;
                if (_remainingRejectedLogins-- > 0)
                    return Task.FromResult(new CommandResult(
                        3,
                        string.Empty,
                        "Login fehlgeschlagen: ungültige Credentials."));
                _authenticated = true;
                return Task.FromResult(new CommandResult(0, "login ok", string.Empty));
            }

            if (arguments[0] == "workflow" && arguments[1] == "list")
            {
                WorkflowListAttempts++;
                return Task.FromResult(_authenticated
                    ? new CommandResult(0, JsonSerializer.Serialize(_workflows), string.Empty)
                    : new CommandResult(3, string.Empty, "Session abgelaufen. Bitte `np auth login` erneut ausführen."));
            }

            if (arguments[0] == "operations" && arguments[1] == "graph")
            {
                var graph = new
                {
                    nodes = _workflows.Select(workflow => new
                    {
                        workflowId = workflow.Id,
                        runningCount = 0,
                    }),
                };
                return Task.FromResult(new CommandResult(0, JsonSerializer.Serialize(graph), string.Empty));
            }

            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
        }
    }
}
