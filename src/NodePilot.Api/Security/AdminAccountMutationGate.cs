using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Serializes mutations that can reduce the active Admin set. The database cannot express
/// "at least one active Admin must exist" as a portable EF constraint, so controller and
/// DB-admin paths share this gate around guard + save. SQL Server and PostgreSQL also
/// acquire a transaction-level database advisory lock inside every execution-strategy
/// attempt, so the invariant survives HA leader handoff, reconnects, and retries.
/// </summary>
internal static class AdminAccountMutationGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IAsyncDisposable> EnterLocalAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        return new LocalReleaser();
    }

    /// <summary>
    /// Acquires the cross-node lock with transaction ownership. Call this inside every
    /// execution-strategy attempt after BeginTransaction so a reconnect/retry always
    /// reloads state and reacquires the cross-node invariant lock.
    /// </summary>
    public static async Task AcquireTransactionLockAsync(
        NodePilotDbContext db,
        CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("The admin invariant transaction lock requires an active transaction.");
        var provider = db.Database.ProviderName;
        if (provider is not ("Microsoft.EntityFrameworkCore.SqlServer" or "Npgsql.EntityFrameworkCore.PostgreSQL"))
            return;

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = N'nodepilot-admin-account-mutation',
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 30000;
                SELECT @result;
                """;
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            if (result < 0)
                throw new TimeoutException(
                    $"Could not acquire the transaction-scoped admin invariant lock (sp_getapplock={result}).");
        }
        else
        {
            command.CommandText = "SELECT pg_advisory_xact_lock(512960745230417001);";
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class LocalReleaser : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
