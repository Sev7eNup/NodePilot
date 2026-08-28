using System.Data.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodePilot.Api.Hosting;
using NodePilot.Data;
using NodePilot.Data.Availability;

namespace NodePilot.Api.Hubs;

/// <summary>
/// Fails hub method invocations fast while the database breaker is open.
///
/// <para>The availability middleware gates the whole hub HTTP surface while the breaker is open.
/// An already-upgraded WebSocket does not re-enter that HTTP pipeline, though, and can still
/// <i>invoke</i> methods — <c>JoinExecution</c> and <c>JoinWorkflow</c> read the database — against
/// a
/// wedged server. This filter answers immediately instead, with the same stable code the HTTP
/// surface
/// uses, so the client can tell "database outage" from "bug".</para>
///
/// <para>Server-to-client pushes are unaffected — filters only run for client-to-server
/// invocations.</para>
/// </summary>
public sealed class DatabaseAvailabilityHubFilter(IDatabaseAvailability availability) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (!availability.IsServable)
        {
            throw new HubException(DatabaseUnavailableResponse.UnavailableCode);
        }

        try
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failure = DbErrorClassifier.Classify(ex);
            var breakerState = availability.Snapshot.State;

            // Connection interceptors open the breaker before the provider exception reaches us.
            // Let
            // that stronger process-wide fact win even when the provider wrapped the original
            // failure
            // ambiguously (notably Npgsql connect-vs-command timeouts).
            if (breakerState is DatabaseAvailabilityState.Unavailable)
            {
                throw new HubException(DatabaseUnavailableResponse.UnavailableCode);
            }

            // IOException, TimeoutException and pool-shaped InvalidOperationException are not
            // database
            // types. Hub methods may throw any of them for unrelated work, so classification alone
            // is
            // not enough to replace the original exception with a database protocol code. Armed
            // state,
            // an EF wrapper or a provider DbException supplies the missing database provenance.
            if (breakerState is not DatabaseAvailabilityState.Armed && !HasDatabaseEvidence(ex))
            {
                throw;
            }

            if (failure is DbFailureKind.ConnectionFailure or DbFailureKind.ConnectionRejected)
            {
                throw new HubException(DatabaseUnavailableResponse.UnavailableCode);
            }

            if (failure is DbFailureKind.CommandTimeout or DbFailureKind.CapacityBackpressure)
            {
                throw new HubException(DatabaseUnavailableResponse.TimeoutCode);
            }

            throw;
        }
    }

    private static bool HasDatabaseEvidence(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException or DbUpdateException or RetryLimitExceededException) return true;
        }

        return false;
    }
}
