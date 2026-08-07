using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NodePilot.Data.Availability;

/// <summary>Maps the classifier's verdict onto the coarser reason the breaker publishes.</summary>
internal static class DatabaseOutageReasonMap
{
    public static DatabaseOutageReason From(DbFailureKind kind) => kind switch
    {
        DbFailureKind.ConnectionRejected => DatabaseOutageReason.RejectedByServer,
        DbFailureKind.ConnectionFailure => DatabaseOutageReason.Unreachable,
        DbFailureKind.CommandTimeout => DatabaseOutageReason.Wedged,
        _ => DatabaseOutageReason.Unknown,
    };
}

/// <summary>
/// Opens the breaker when EF reports that a physical connection could not be established.
///
/// <para>Verified on 2026-08-06 against EF Core 10.0.10 + Npgsql 10.0.3: this hook fires <b>once per
/// attempt inside</b> the retrying execution strategy — <c>EnableRetryOnFailure(2)</c> produced three
/// calls, not one. The breaker therefore trips on the first failed attempt rather than after the whole
/// retry budget has burned, which is what makes sub-second detection possible at all.</para>
///
/// <para><b>Deliberately not overridden:</b>
/// <list type="bullet">
/// <item><c>ConnectionOpened*</c> — a successful checkout is not evidence of anything. With
/// <c>Min Pool Size=40</c> it hands back an already-open socket without contacting the server, so
/// wiring it to recovery would clear the breaker on every operation and make it impossible to trip.
/// Only the probe may publish Available.</item>
/// <item><c>ConnectionCanceled*</c> — a caller's token firing says nothing about the database.</item>
/// </list></para>
///
/// <para>The probe is not exempted here by a flag because it does not need to be: it works on a raw
/// provider connection outside EF entirely, so it never reaches an interceptor.</para>
/// </summary>
public sealed class DatabaseConnectionAvailabilityInterceptor(IDatabaseAvailability availability)
    : DbConnectionInterceptor
{
    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
        => Report(eventData.Exception);

    public override Task ConnectionFailedAsync(
        DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Report(eventData.Exception);
        return Task.CompletedTask;
    }

    internal void Report(Exception? exception)
    {
        // ClassifyConnectionFailure, not Classify: a connect timeout and a command timeout arrive in
        // the identical exception shape (NpgsqlException wrapping TimeoutException, measured), so the
        // hook the failure arrived on is the only thing that can tell them apart. On this hook a
        // timeout means no handshake completed, which is a dead server.
        var kind = DbErrorClassifier.ClassifyConnectionFailure(exception);

        switch (kind)
        {
            // The server is alive and out of connection slots. Opening the breaker here would turn a
            // busy moment into a self-inflicted outage.
            case DbFailureKind.CapacityBackpressure:
                return;

            case DbFailureKind.ConnectionRejected:
            case DbFailureKind.ConnectionFailure:
                availability.ReportUnreachable(DatabaseOutageReasonMap.From(kind));
                return;

            // An unfamiliar provider-side open exception is not enough evidence to seal the whole
            // installation. Arm the dedicated SELECT-1 probe; its positive round trip adjudicates the
            // unknown shape without pretending the failed open succeeded.
            case DbFailureKind.None:
            default:
                availability.Arm();
                return;
        }
    }
}

/// <summary>
/// Arms the probe when a command times out, and opens the breaker when a command fails for a
/// connection-class reason.
///
/// <para>A command timeout deliberately does <b>not</b> open the breaker. It cannot: one slow query is
/// indistinguishable from a wedged server at the exception level, and the difference matters enormously
/// to the user. Arming hands the question to the probe, which answers it with a positive test —
/// <c>SELECT 1</c> either comes back or it does not.</para>
///
/// <para><b>Deliberately not overridden:</b> <c>CommandExecuted*</c> (same reason as
/// <c>ConnectionOpened*</c>) and <c>CommandCanceled*</c> — a caller-token abort must never feed the
/// breaker, which is what keeps a cancelled HTTP request from being read as a database failure.</para>
/// </summary>
public sealed class DatabaseCommandAvailabilityInterceptor(IDatabaseAvailability availability)
    : DbCommandInterceptor
{
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => Report(eventData.Exception);

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Report(eventData.Exception);
        return Task.CompletedTask;
    }

    private void Report(Exception? exception)
    {
        switch (DbErrorClassifier.Classify(exception))
        {
            case DbFailureKind.CommandTimeout:
                availability.Arm();
                break;

            case DbFailureKind.ConnectionFailure:
                availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
                break;

            case DbFailureKind.ConnectionRejected:
                availability.ReportUnreachable(DatabaseOutageReason.RejectedByServer);
                break;

            // CapacityBackpressure: the server is fine and busy. Arming would issue another query that
            // either adds load or cannot get a connection at all.
            // None: unique violations and ordinary bugs. This codebase *expects* PK violations - the
            // retry/idempotency pairing in WorkflowDbWriteMetrics depends on them - so an unqualified
            // call here would turn every one of them into probe traffic.
            case DbFailureKind.CapacityBackpressure:
            case DbFailureKind.None:
            default:
                break;
        }
    }
}
