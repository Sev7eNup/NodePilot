using FluentAssertions;
using NodePilot.Switcher.ViewModels;
using Xunit;

namespace NodePilot.Switcher.Tests;

public sealed class AsyncCommandTests
{
    // Execute is async void: without the handler the exception reaches the WPF dispatcher and
    // terminates the process instead of showing a dialog.
    [Fact]
    public void Execute_WhenTheCommandFaults_ReportsInsteadOfEscaping()
    {
        Exception? reported = null;
        var command = new AsyncCommand(
            () => throw new TimeoutException("reconciliation did not settle"),
            onError: exception => reported = exception);

        command.Execute(null);

        reported.Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Be("reconciliation did not settle");
    }

    [Fact]
    public void Execute_WhenTheCommandFaults_BecomesExecutableAgain()
    {
        var command = new AsyncCommand(() => throw new InvalidOperationException("boom"), onError: _ => { });

        command.Execute(null);

        command.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Execute_WithoutAnErrorHandler_DoesNotThrowOutOfTheCommand()
    {
        var completed = new TaskCompletionSource();
        var command = new AsyncCommand(() =>
        {
            completed.SetResult();
            throw new InvalidOperationException("boom");
        });

        var action = () => command.Execute(null);

        action.Should().NotThrow();
        await completed.Task;
    }
}
