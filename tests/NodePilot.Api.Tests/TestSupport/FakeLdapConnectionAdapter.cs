using NodePilot.Api.Security.Ldap;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// Programmable <see cref="ILdapConnectionAdapter"/> so LDAP tests can script directory verdicts
/// deterministically without a real domain controller. Union of the four private
/// <c>FakeAdapter</c> copies the auth/LDAP test files used to carry: the authenticate side
/// serves <see cref="Result"/> or throws per the <c>Throw*</c> switches, the lookup side
/// (background directory sync) serves <see cref="Snapshot"/>/<see cref="Snapshots"/>.
/// </summary>
public sealed class FakeLdapConnectionAdapter : ILdapConnectionAdapter
{
    // --- AuthenticateAsync (interactive login) ---------------------------------------

    /// <summary>Verdict returned on bind; null models a clean credential rejection.</summary>
    public LdapAuthResult? Result { get; set; }

    /// <summary>Throw <see cref="LdapInfrastructureException"/> on authenticate (DC offline).</summary>
    public bool ThrowInfra { get; set; }

    /// <summary>Throw <see cref="LdapUserObjectNotFoundException"/> on authenticate.</summary>
    public bool ThrowUserObjectMissing { get; set; }

    /// <summary>Throw <see cref="OperationCanceledException"/> on authenticate.</summary>
    public bool ThrowCancellation { get; set; }

    /// <summary>Arbitrary exception thrown on authenticate; takes precedence over the switches.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>Number of authenticate attempts observed (lockout/breaker assertions).</summary>
    public int Calls { get; private set; }

    /// <summary>The UPN of the most recent authenticate attempt.</summary>
    public string? LastUpn { get; private set; }

    // --- LookupBySubjectAsync (background directory synchronization) -----------------

    /// <summary>Snapshot served for any subject without a <see cref="Snapshots"/> entry.</summary>
    public LdapDirectorySnapshot? Snapshot { get; set; }

    /// <summary>Per-subject snapshots; falls back to <see cref="Snapshot"/> on a miss.</summary>
    public IReadOnlyDictionary<string, LdapDirectorySnapshot?>? Snapshots { get; set; }

    /// <summary>Throw <see cref="LdapInfrastructureException"/> on lookup (sync-side outage).</summary>
    public bool ThrowInfrastructureOnLookup { get; set; }

    /// <summary>Hook invoked on every lookup — used to inject leadership changes mid-sync.</summary>
    public Action? OnLookup { get; set; }

    public Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct)
    {
        Calls++;
        LastUpn = upn;
        if (ExceptionToThrow is not null) throw ExceptionToThrow;
        if (ThrowCancellation) throw new OperationCanceledException(ct);
        if (ThrowUserObjectMissing)
            throw new LdapUserObjectNotFoundException("simulated missing userPrincipalName");
        if (ThrowInfra) throw new LdapInfrastructureException("simulated DC offline");
        return Task.FromResult(Result);
    }

    public Task<LdapDirectorySnapshot?> LookupBySubjectAsync(string subject, CancellationToken ct)
    {
        OnLookup?.Invoke();
        if (ThrowInfrastructureOnLookup) throw new LdapInfrastructureException("offline");
        return Task.FromResult(
            Snapshots is not null && Snapshots.TryGetValue(subject, out var snapshot)
                ? snapshot
                : Snapshot);
    }
}
