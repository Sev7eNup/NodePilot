using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NodePilot.Api.Hosting;
using NodePilot.Data;
using NodePilot.Data.Availability;

namespace NodePilot.Api.HealthChecks;

/// <summary>
/// Readiness, answered fast.
///
/// <para>Replaces <c>AddDbContextCheck</c>, which had two problems that only show up in the
/// situation
/// the probe is meant to cover. It calls <c>CanConnectAsync</c>, which merely creates a connection
/// and
/// therefore passes against a hung-but-listening server; and it carries no timeout of its own, so
/// it
/// inherits the 120 s command timeout and the retry strategy — readiness then takes minutes and the
/// load balancer hits its own timeout instead of receiving a clean 503.</para>
///
/// <para>Answering from memory while the breaker is open is the point: readiness must be the
/// fastest
/// question in the process, not the slowest.</para>
/// </summary>
public sealed class DatabaseReadyHealthCheck(
    IDatabaseAvailability availability,
    NodePilotDbContext db,
    DatabaseAvailabilityOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = availability.Snapshot;
        var outage = snapshot.CurrentOutage;
        if (outage is not null)
        {
            return HealthCheckResult.Unhealthy(
                $"Database unavailable since {outage.SinceUtc:O} ({outage.Reason}).");
        }

        // Armed means a command timeout has already been observed and the dedicated probe is
        // adjudicating it. Readiness is a traffic gate, not a second competing probe: answer from
        // memory immediately so an LB stops admitting fresh work while /api remains deliberately
        // servable until the probe has enough evidence to open the breaker.
        if (snapshot.State is DatabaseAvailabilityState.Armed)
        {
            return HealthCheckResult.Unhealthy(
                "Database availability probe is adjudicating a command timeout.");
        }

        try
        {
            // A real round trip, bounded. CanConnectAsync would not do: a wedged server accepts
            // connections and answers nothing, which is precisely the state readiness must catch.
            using (DatabaseCommandBudget.Apply(db, options.ReadinessTimeoutSeconds))
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database did not answer a readiness query.", ex);
        }
    }
}
