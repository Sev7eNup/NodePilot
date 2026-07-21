namespace NodePilot.Api.Security.Ldap;

public sealed record LdapDirectoryLookupResult(
    string Subject,
    LdapDirectorySnapshot? Snapshot,
    LdapInfrastructureException? Error);

/// <summary>
/// Thin abstraction over <see cref="System.DirectoryServices.Protocols.LdapConnection"/>
/// so the authenticator can be unit-tested without a real domain controller. The real
/// implementation lives in <c>SystemLdapConnectionAdapter</c>; tests provide a fake that
/// returns canned <see cref="LdapAuthResult"/>s + simulated infrastructure failures.
/// <para>
/// Login binds are isolated per request. Bulk lifecycle synchronization may reuse
/// service-bind connections within one bounded reconciliation pass.
/// </para>
/// </summary>
public interface ILdapConnectionAdapter
{
    /// <summary>
    /// Bind as <paramref name="upn"/> with <paramref name="password"/>; on success, query
    /// <c>objectSid</c>, legacy <c>objectGUID</c>, <c>displayName</c> and <c>tokenGroups</c>.
    /// </summary>
    /// <returns>
    /// <see cref="LdapAuthResult"/> when bind + lookup succeed. <c>null</c> when the
    /// directory cleanly rejected the credentials. Throws
    /// <see cref="LdapInfrastructureException"/> for everything else (connect/TLS/server
    /// errors).
    /// </returns>
    Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct);

    /// <summary>Service-bind lookup used by background lifecycle synchronization.</summary>
    Task<LdapDirectorySnapshot?> LookupBySubjectAsync(string subject, CancellationToken ct) =>
        Task.FromResult<LdapDirectorySnapshot?>(null);

    /// <summary>
    /// Bounded-concurrency directory refresh. Implementations may override this to reuse
    /// service-bind connections; the default still prevents an O(N) serial sync pass.
    /// </summary>
    async Task<IReadOnlyDictionary<string, LdapDirectoryLookupResult>> LookupManyBySubjectAsync(
        IReadOnlyCollection<string> subjects,
        int maxConcurrency,
        CancellationToken ct)
    {
        var results = new System.Collections.Concurrent.ConcurrentDictionary<
            string, LdapDirectoryLookupResult>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            subjects.Distinct(StringComparer.OrdinalIgnoreCase),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 32),
                CancellationToken = ct,
            },
            async (subject, token) =>
            {
                try
                {
                    var snapshot = await LookupBySubjectAsync(subject, token);
                    results[subject] = new LdapDirectoryLookupResult(subject, snapshot, null);
                }
                catch (LdapInfrastructureException ex)
                {
                    results[subject] = new LdapDirectoryLookupResult(subject, null, ex);
                }
            });
        return results;
    }

    /// <summary>Checks LDAPS connectivity, service bind, and BaseDn readability.</summary>
    Task<LdapEndpointHealth> CheckHealthAsync(CancellationToken ct) =>
        Task.FromResult(new LdapEndpointHealth(false, null, "not_supported"));
}
