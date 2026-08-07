using System.Data.Common;
using System.IO;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using NodePilot.Api.Hubs;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Api.Tests.Hubs;

public sealed class DatabaseAvailabilityHubFilterTests
{
    private static DatabaseAvailabilityTracker Available()
    {
        var tracker = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance);
        tracker.MarkBootComplete();
        return tracker;
    }

    [Fact]
    public async Task Invoke_BreakerAlreadyOpen_ReturnsDatabaseUnavailableWithoutCallingHubMethod()
    {
        var tracker = Available();
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
        var called = false;
        var filter = new DatabaseAvailabilityHubFilter(tracker);

        Func<Task> act = async () => await filter.InvokeMethodAsync(null!, _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>(null);
        });

        var error = await act.Should().ThrowAsync<HubException>();
        error.Which.Message.Should().Be(DatabaseUnavailableResponse.UnavailableCode);
        called.Should().BeFalse();
    }

    [Fact]
    public async Task Invoke_GenericTimeoutWhileBreakerRemainsServable_PreservesOriginalException()
    {
        var filter = new DatabaseAvailabilityHubFilter(Available());
        var expected = new TimeoutException("non-database hub timeout");

        Func<Task> act = async () => await filter.InvokeMethodAsync(
            null!, _ => ValueTask.FromException<object?>(expected));

        var error = await act.Should().ThrowAsync<TimeoutException>();
        error.Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Invoke_GenericIoFailureWhileBreakerRemainsServable_PreservesOriginalException()
    {
        var filter = new DatabaseAvailabilityHubFilter(Available());
        var expected = new IOException("connection closed");

        Func<Task> act = async () => await filter.InvokeMethodAsync(
            null!, _ => ValueTask.FromException<object?>(expected));

        var error = await act.Should().ThrowAsync<IOException>();
        error.Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Invoke_GenericPoolShapedFailureWhileBreakerRemainsAvailable_PreservesOriginalException()
    {
        var filter = new DatabaseAvailabilityHubFilter(Available());
        var expected = new InvalidOperationException(
            "Timeout expired while obtaining a connection from the pool.");

        Func<Task> act = async () => await filter.InvokeMethodAsync(
            null!, _ => ValueTask.FromException<object?>(expected));

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Invoke_ProviderTransportFailure_ReturnsDatabaseUnavailable()
    {
        var filter = new DatabaseAvailabilityHubFilter(Available());
        var providerFailure = new TestDbException(
            "provider transport failed", new IOException("connection closed"));

        Func<Task> act = async () => await filter.InvokeMethodAsync(
            null!, _ => ValueTask.FromException<object?>(providerFailure));

        var error = await act.Should().ThrowAsync<HubException>();
        error.Which.Message.Should().Be(DatabaseUnavailableResponse.UnavailableCode);
    }

    [Fact]
    public async Task Invoke_ProviderCommandTimeout_ReturnsDatabaseTimeout()
    {
        var filter = new DatabaseAvailabilityHubFilter(Available());
        var providerFailure = new TestDbException(
            "provider command timed out", new TimeoutException("command timeout"));

        Func<Task> act = async () => await filter.InvokeMethodAsync(
            null!, _ => ValueTask.FromException<object?>(providerFailure));

        var error = await act.Should().ThrowAsync<HubException>();
        error.Which.Message.Should().Be(DatabaseUnavailableResponse.TimeoutCode);
    }

    [Fact]
    public async Task Invoke_EfWrappedCapacityFailure_ReturnsDatabaseTimeout()
    {
        var filter = new DatabaseAvailabilityHubFilter(Available());
        var capacityFailure = new DbUpdateException(
            "database write failed",
            new InvalidOperationException("Timeout expired while obtaining a connection from the pool."));

        Func<Task> act = async () => await filter.InvokeMethodAsync(
            null!, _ => ValueTask.FromException<object?>(capacityFailure));

        var error = await act.Should().ThrowAsync<HubException>();
        error.Which.Message.Should().Be(DatabaseUnavailableResponse.TimeoutCode);
    }

    [Fact]
    public async Task Invoke_GenericTimeoutWhileBreakerIsArmed_ReturnsDatabaseTimeout()
    {
        var tracker = Available();
        tracker.Arm();
        var filter = new DatabaseAvailabilityHubFilter(tracker);

        Func<Task> act = async () => await filter.InvokeMethodAsync(
            null!, _ => ValueTask.FromException<object?>(new TimeoutException("command timeout")));

        var error = await act.Should().ThrowAsync<HubException>();
        error.Which.Message.Should().Be(DatabaseUnavailableResponse.TimeoutCode);
    }

    [Fact]
    public async Task Invoke_GenericIoFailureAfterBreakerOpens_ReturnsDatabaseUnavailable()
    {
        var tracker = Available();
        var filter = new DatabaseAvailabilityHubFilter(tracker);

        Func<Task> act = async () => await filter.InvokeMethodAsync(null!, _ =>
        {
            tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
            return ValueTask.FromException<object?>(new IOException("connection closed"));
        });

        var error = await act.Should().ThrowAsync<HubException>();
        error.Which.Message.Should().Be(DatabaseUnavailableResponse.UnavailableCode);
    }

    [Fact]
    public async Task Invoke_NonDatabaseFailure_PreservesOriginalException()
    {
        var filter = new DatabaseAvailabilityHubFilter(Available());
        var expected = new InvalidOperationException("hub bug");

        Func<Task> act = async () => await filter.InvokeMethodAsync(
            null!, _ => ValueTask.FromException<object?>(expected));

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(expected);
    }

    private sealed class TestDbException(string message, Exception innerException)
        : DbException(message, innerException);
}
