using Microsoft.Extensions.Options;

namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Possible outcomes of an LDAP login attempt, surfaced to the caller (PR4 will be the
/// AuthController) so the local-fallback logic stays in one place.
/// </summary>
public enum LdapAuthOutcome
{
    /// <summary>The directory rejected the credentials cleanly. The caller MUST NOT attempt
    /// the local-password path — falling back here would let an attacker pivot from a
    /// failed AD login to a local account with the same username.</summary>
    InvalidCredentials,
    /// <summary>Bind + lookup both succeeded; <see cref="LdapAuthOutcomeResult.Result"/>
    /// is populated.</summary>
    Success,
    /// <summary>LDAP is disabled by config, or the circuit breaker is open, or an
    /// infrastructure failure occurred. The caller MAY fall back to the local-password
    /// path so a DC outage doesn't lock out local admins.</summary>
    Unavailable,
}

/// <summary>Outcome envelope. <see cref="Result"/> is non-null iff <see cref="Outcome"/>
/// is <see cref="LdapAuthOutcome.Success"/>; <see cref="UnavailableReason"/> is non-null
/// iff <see cref="Outcome"/> is <see cref="LdapAuthOutcome.Unavailable"/>.</summary>
public sealed record LdapAuthOutcomeResult(
    LdapAuthOutcome Outcome,
    LdapAuthResult? Result,
    string? UnavailableReason);

/// <summary>
/// Coordinates the LDAP-bind path: enabled-check → username normalization → circuit
/// breaker → adapter call → translate exceptions into the narrow
/// <see cref="LdapAuthOutcome"/> vocabulary the caller needs.
/// </summary>
public sealed class LdapAuthenticator
{
    private readonly IOptionsMonitor<LdapOptions> _options;
    private readonly ILdapConnectionAdapter _adapter;
    private readonly LdapCircuitBreaker _breaker;
    private readonly ILogger<LdapAuthenticator> _logger;
    private readonly ActiveDirectoryAuthenticationConfiguration? _activeConfiguration;

    public LdapAuthenticator(
        IOptionsMonitor<LdapOptions> options,
        ILdapConnectionAdapter adapter,
        LdapCircuitBreaker breaker,
        ILogger<LdapAuthenticator> logger,
        ActiveDirectoryAuthenticationConfiguration? activeConfiguration = null)
    {
        _options = options;
        _adapter = adapter;
        _breaker = breaker;
        _logger = logger;
        _activeConfiguration = activeConfiguration;
    }

    /// <summary>
    /// Attempt to authenticate <paramref name="rawUsername"/> against LDAP. Returns the
    /// outcome envelope; never throws for credential / availability reasons.
    /// </summary>
    public async Task<LdapAuthOutcomeResult> AuthenticateAsync(string rawUsername, string password, CancellationToken ct)
    {
        var opts = _activeConfiguration?.Ldap ?? _options.CurrentValue;
        if (!opts.Enabled)
            return new LdapAuthOutcomeResult(LdapAuthOutcome.Unavailable, null, "ldap_disabled");

        // Security-audit finding H-17: reject an empty (or whitespace-only) password before any bind is attempted.
        // Per RFC 4513 §5.1.2 a simple-bind that carries a populated name but a zero-length
        // password is an *unauthenticated bind* — Active Directory (and most directories)
        // answer it with LDAP_SUCCESS instead of error 49. Forwarding it to the adapter would
        // turn "attacker knows a valid username" into a full authentication bypass (worse with
        // a service account configured, since the post-bind search then succeeds too). We also
        // fold in whitespace-only passwords as belt-and-suspenders — they are never a valid
        // directory credential. Treat it as a clean invalid-credentials verdict: never bind,
        // never touch the breaker/network, and (because the outcome is InvalidCredentials, not
        // Unavailable) never fall through to the local-password path either.
        if (string.IsNullOrWhiteSpace(password))
            return new LdapAuthOutcomeResult(LdapAuthOutcome.InvalidCredentials, null, null);

        if (!_breaker.TryAcquire())
            return new LdapAuthOutcomeResult(LdapAuthOutcome.Unavailable, null, "circuit_open");

        string upn;
        try
        {
            upn = UsernameNormalizer.ToUpn(rawUsername, opts.UpnSuffix);
        }
        catch (ArgumentException ex)
        {
            // A malformed username isn't an LDAP availability problem; don't trip the breaker.
            _breaker.RecordSuccess();
            _logger.LogDebug(ex, "Username could not be normalized for LDAP bind");
            return new LdapAuthOutcomeResult(LdapAuthOutcome.InvalidCredentials, null, null);
        }

        try
        {
            var result = await _adapter.AuthenticateAsync(upn, password, ct).ConfigureAwait(false);
            _breaker.RecordSuccess();
            return result is null
                ? new LdapAuthOutcomeResult(LdapAuthOutcome.InvalidCredentials, null, null)
                : new LdapAuthOutcomeResult(LdapAuthOutcome.Success, result, null);
        }
        catch (LdapInfrastructureException ex)
        {
            _breaker.RecordFailure();
            _logger.LogWarning(ex, "LDAP infrastructure failure for user '{Upn}' — circuit state {State}",
                upn, _breaker.CurrentState);
            return new LdapAuthOutcomeResult(LdapAuthOutcome.Unavailable, null, "infrastructure_failure");
        }
        catch (OperationCanceledException)
        {
            // Don't trip the breaker — request was cancelled by the caller, not by LDAP.
            _breaker.RecordSuccess();
            throw;
        }
    }
}
