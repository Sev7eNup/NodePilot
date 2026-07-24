using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Principal;
using Microsoft.Extensions.Options;

namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Real <see cref="ILdapConnectionAdapter"/> built on
/// <see cref="System.DirectoryServices.Protocols.LdapConnection"/>.
/// <para>
/// Two-stage flow per call:
/// <list type="number">
/// <item>Simple-bind as <c>upn + password</c> against the configured server. A bind failure
/// with <c>LdapErrorCodes.InvalidCredentials</c> means "wrong password" → return null.
/// Anything else (connect refused, TLS error, server-down) → throw
/// <see cref="LdapInfrastructureException"/> so the caller can trip the circuit breaker.</item>
/// <item>Subtree-search under <c>BaseDn</c> for the user via <c>userPrincipalName=&lt;upn&gt;</c>
/// to recover <c>objectGUID</c>, <c>displayName</c>, <c>distinguishedName</c>; then a
/// base-scope search on the recovered DN for <c>tokenGroups</c> (transitive group SIDs —
/// only available as a constructed-attribute with base scope).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SystemLdapConnectionAdapter : ILdapConnectionAdapter
{
    private readonly IOptionsMonitor<LdapOptions> _options;
    private readonly ILogger<SystemLdapConnectionAdapter> _logger;
    private readonly ActiveDirectoryAuthenticationConfiguration? _activeConfiguration;

    public SystemLdapConnectionAdapter(
        IOptionsMonitor<LdapOptions> options,
        ILogger<SystemLdapConnectionAdapter> logger,
        ActiveDirectoryAuthenticationConfiguration? activeConfiguration = null)
    {
        _options = options;
        _logger = logger;
        _activeConfiguration = activeConfiguration;
    }

    public Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct)
    {
        // System.DirectoryServices.Protocols is fully synchronous; running the work on the
        // thread-pool keeps the calling request thread free. Cancellation token is honoured
        // before the bind starts but the bind itself is not cancellable — bind timeout +
        // circuit-breaker bound the worst-case wait.
        return Task.Run(() => AuthenticateCore(upn, password, ct), ct);
    }

    public Task<LdapDirectorySnapshot?> LookupBySubjectAsync(string subject, CancellationToken ct) =>
        Task.Run(() => LookupBySubjectCore(subject, ct), ct);

    public Task<IReadOnlyDictionary<string, LdapDirectoryLookupResult>> LookupManyBySubjectAsync(
        IReadOnlyCollection<string> subjects,
        int maxConcurrency,
        CancellationToken ct) => Task.Run<IReadOnlyDictionary<string, LdapDirectoryLookupResult>>(
            () => LookupManyBySubjectCore(subjects, maxConcurrency, ct), ct);

    public Task<LdapEndpointHealth> CheckHealthAsync(CancellationToken ct) =>
        Task.Run(() => CheckHealthCore(ct), ct);

    private LdapDirectorySnapshot? LookupBySubjectCore(string subject, CancellationToken ct)
    {
        var opts = _activeConfiguration?.Ldap ?? _options.CurrentValue;
        EnsureServiceLookupConfiguration(opts);
        var endpoints = LdapEndpoint.Resolve(opts);
        var sidFilter = SidToFilterValue(subject);
        var observations = new List<LdapDirectoryLookupResult>(endpoints.Count);

        foreach (var endpoint in endpoints)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                using var connection = OpenServiceConnection(opts, endpoint);
                var snapshot = LookupOnConnection(connection, opts, subject, sidFilter);
                observations.Add(new LdapDirectoryLookupResult(subject, snapshot, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = ex as LdapInfrastructureException
                    ?? new LdapInfrastructureException("LDAP directory lookup failed: " + ex.Message, ex);
                observations.Add(new LdapDirectoryLookupResult(subject, null, failure));
                _logger.LogWarning(ex,
                    "Directory sync lookup failed against {Host}:{Port}; remaining endpoints will still be compared",
                    endpoint.Host, endpoint.Port);
            }
        }

        return ReconcileEndpointResults(subject, observations);
    }

    private IReadOnlyDictionary<string, LdapDirectoryLookupResult> LookupManyBySubjectCore(
        IReadOnlyCollection<string> subjects,
        int maxConcurrency,
        CancellationToken ct)
    {
        var opts = _activeConfiguration?.Ldap ?? _options.CurrentValue;
        EnsureServiceLookupConfiguration(opts);
        var endpoints = LdapEndpoint.Resolve(opts);
        var queue = new System.Collections.Concurrent.ConcurrentQueue<string>(
            subjects.Distinct(StringComparer.OrdinalIgnoreCase));
        var results = new System.Collections.Concurrent.ConcurrentDictionary<
            string, LdapDirectoryLookupResult>(StringComparer.OrdinalIgnoreCase);
        var workerCount = Math.Min(
            Math.Clamp(maxConcurrency, 1, 32),
            Math.Max(1, queue.Count));

        Parallel.For(0, workerCount, new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = ct,
        }, _ =>
        {
            // Each worker owns and reuses at most one service-bind connection per DC.
            // Connections are never shared concurrently between users or workers.
            var connections = new LdapConnection?[endpoints.Count];
            var endpointFailures = new LdapInfrastructureException?[endpoints.Count];
            try
            {
                while (queue.TryDequeue(out var subject))
                {
                    ct.ThrowIfCancellationRequested();
                    var observations = new List<LdapDirectoryLookupResult>(endpoints.Count);
                    string sidFilter;
                    try
                    {
                        sidFilter = SidToFilterValue(subject);
                    }
                    catch (LdapInfrastructureException ex)
                    {
                        results[subject] = new LdapDirectoryLookupResult(subject, null, ex);
                        continue;
                    }

                    for (var index = 0; index < endpoints.Count; index++)
                    {
                        if (endpointFailures[index] is { } priorFailure)
                        {
                            observations.Add(new LdapDirectoryLookupResult(
                                subject, null, priorFailure));
                            continue;
                        }

                        try
                        {
                            connections[index] ??= OpenServiceConnection(opts, endpoints[index]);
                            var snapshot = LookupOnConnection(
                                connections[index]!, opts, subject, sidFilter);
                            observations.Add(new LdapDirectoryLookupResult(
                                subject, snapshot, null));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            connections[index]?.Dispose();
                            connections[index] = null;
                            var failure = ex as LdapInfrastructureException
                                ?? new LdapInfrastructureException(
                                    "LDAP directory lookup failed: " + ex.Message, ex);
                            endpointFailures[index] = failure;
                            observations.Add(new LdapDirectoryLookupResult(
                                subject, null, failure));
                            _logger.LogWarning(ex,
                                "Bulk directory sync worker lost {Host}:{Port}; remaining subjects fail over",
                                endpoints[index].Host, endpoints[index].Port);
                        }
                    }

                    try
                    {
                        results[subject] = new LdapDirectoryLookupResult(
                            subject, ReconcileEndpointResults(subject, observations), null);
                    }
                    catch (LdapInfrastructureException ex)
                    {
                        results[subject] = new LdapDirectoryLookupResult(subject, null, ex);
                    }
                }
            }
            finally
            {
                foreach (var connection in connections)
                    connection?.Dispose();
            }
        });

        return results;
    }

    /// <summary>
    /// Reconciles one subject's observations from every configured DC. A snapshot is
    /// fresh only when all DCs agree on the security-relevant state. Mixed found/not-found,
    /// enabled-state disagreement, group disagreement, or an unavailable DC is ambiguous
    /// and must not refresh authorization freshness. Destructive absence is accepted only
    /// when every DC independently confirms not-found.
    /// </summary>
    internal static LdapDirectorySnapshot? ReconcileEndpointResults(
        string subject,
        IReadOnlyList<LdapDirectoryLookupResult> observations)
    {
        if (observations.Count == 0)
            throw new LdapInfrastructureException(
                "No LDAP endpoint could confirm the directory object state.");
        if (observations.Any(observation => observation.Error is not null))
            throw new LdapInfrastructureException(
                $"Directory state for subject '{subject}' could not be confirmed by every configured LDAP endpoint.",
                observations.First(observation => observation.Error is not null).Error!);

        var snapshots = observations
            .Select(observation => observation.Snapshot)
            .ToArray();
        if (snapshots.All(snapshot => snapshot is null))
            return null;
        if (snapshots.Any(snapshot => snapshot is null))
            throw new LdapInfrastructureException(
                $"Directory state for subject '{subject}' is ambiguous: configured DCs disagree on object existence.");

        var baseline = snapshots[0]!;
        var baselineGroups = baseline.GroupSids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots.Skip(1).Select(snapshot => snapshot!))
        {
            if (snapshot.IsEnabled != baseline.IsEnabled)
                throw new LdapInfrastructureException(
                    $"Directory state for subject '{subject}' is ambiguous: configured DCs disagree on account enabled state.");
            if (!baselineGroups.SetEquals(snapshot.GroupSids))
                throw new LdapInfrastructureException(
                    $"Directory state for subject '{subject}' is ambiguous: configured DCs disagree on group membership.");
        }
        return baseline;
    }

    private LdapDirectorySnapshot? LookupOnConnection(
        LdapConnection connection,
        LdapOptions opts,
        string subject,
        string sidFilter)
    {
        var search = new SearchRequest(
            opts.BaseDn,
            // objectCategory=person excludes computer accounts (which are also objectClass=user)
            // so a machine object can never masquerade as a login principal.
            $"(&(objectCategory=person)(objectClass=user)(objectSid={sidFilter}))",
            SearchScope.Subtree,
            "objectSid", "userPrincipalName", "displayName", "cn", "userAccountControl");
        var response = (SearchResponse)connection.SendRequest(search);
        if (response.Entries.Count == 0)
            return null;

        var entry = response.Entries[0];
        var accountControlText = GetFirstString(entry, "userAccountControl") ?? "0";
        _ = int.TryParse(accountControlText, out var accountControl);
        var groups = ReadTokenGroups(connection, entry.DistinguishedName, subject);
        return new LdapDirectorySnapshot(
            subject,
            IsEnabled: (accountControl & 0x2) == 0,
            Upn: GetFirstString(entry, "userPrincipalName") ?? subject,
            DisplayName: GetFirstString(entry, "displayName")
                ?? GetFirstString(entry, "cn")
                ?? subject,
            GroupSids: groups);
    }

    private LdapEndpointHealth CheckHealthCore(CancellationToken ct)
    {
        var opts = _activeConfiguration?.Ldap ?? _options.CurrentValue;
        try
        {
            EnsureServiceLookupConfiguration(opts);
            var endpoints = LdapEndpoint.Resolve(opts);
            var healthyEndpoints = new List<string>();
            var failedEndpoints = new List<string>();
            foreach (var endpoint in endpoints)
            {
                var label = $"{endpoint.Host}:{endpoint.Port}";
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using var connection = OpenServiceConnection(opts, endpoint);
                    _ = (SearchResponse)connection.SendRequest(new SearchRequest(
                        opts.BaseDn, "(objectClass=*)", SearchScope.Base, "distinguishedName"));
                    healthyEndpoints.Add(label);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedEndpoints.Add(label);
                    _logger.LogWarning(ex, "LDAP health probe failed against {Host}:{Port}", endpoint.Host, endpoint.Port);
                }
            }
            if (healthyEndpoints.Count == endpoints.Count)
                return new LdapEndpointHealth(
                    true, string.Join(",", healthyEndpoints), null,
                    HealthyEndpoints: healthyEndpoints, FailedEndpoints: failedEndpoints);
            if (healthyEndpoints.Count > 0)
                return new LdapEndpointHealth(
                    true, string.Join(",", healthyEndpoints), "partial_endpoint_failure",
                    Degraded: true,
                    HealthyEndpoints: healthyEndpoints,
                    FailedEndpoints: failedEndpoints);
            return new LdapEndpointHealth(false, null, "all_endpoints_unavailable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LDAP health probe configuration failed");
            return new LdapEndpointHealth(false, null, "configuration_error");
        }
    }

    private static void EnsureServiceLookupConfiguration(LdapOptions opts)
    {
        if (!opts.UseSsl)
            throw new LdapInfrastructureException("Directory sync requires LDAPS.");
        if (string.IsNullOrWhiteSpace(opts.BaseDn))
            throw new LdapInfrastructureException("Authentication:Ldap:BaseDn is not configured.");
        if (string.IsNullOrWhiteSpace(opts.ServiceBindDn)
            || string.IsNullOrWhiteSpace(opts.ServicePassword))
            throw new LdapInfrastructureException("Directory sync requires service-bind credentials.");
    }

    private static LdapConnection OpenServiceConnection(LdapOptions opts, LdapEndpoint endpoint)
    {
        var identifier = new LdapDirectoryIdentifier(
            endpoint.Host, endpoint.Port, fullyQualifiedDnsHostName: false, connectionless: false);
        var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(Math.Max(1, opts.BindTimeoutSeconds)),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        // Never chase referrals: a subtree search could otherwise be redirected to a DC
        // outside the configured, TLS-verified endpoint set. All queries must be answered
        // by the DC we deliberately connected to.
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        try
        {
            connection.Bind(new NetworkCredential(opts.ServiceBindDn, opts.ServicePassword));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private IReadOnlyList<string> ReadTokenGroups(LdapConnection connection, string userDn, string subject)
    {
        var response = (SearchResponse)connection.SendRequest(
            new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, "tokenGroups"));
        var groups = new List<string>();
        if (response.Entries.Count == 0 || !response.Entries[0].Attributes.Contains("tokenGroups"))
            return groups;
        foreach (var raw in response.Entries[0].Attributes["tokenGroups"].GetValues(typeof(byte[])))
        {
            if (raw is not byte[] bytes || bytes.Length == 0) continue;
            try { groups.Add(new SecurityIdentifier(bytes, 0).ToString()); }
            catch (ArgumentException)
            {
                _logger.LogWarning("Skipping malformed tokenGroups entry for subject {Subject}", subject);
            }
        }
        return groups;
    }

    internal static string SidToFilterValue(string subject)
    {
        try
        {
            var sid = new SecurityIdentifier(subject);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            return string.Concat(bytes.Select(value => $"\\{value:x2}"));
        }
        catch (Exception ex) when (ex is ArgumentException or SystemException)
        {
            throw new LdapInfrastructureException("External identity subject is not a valid AD SID.", ex);
        }
    }

    private LdapAuthResult? AuthenticateCore(string upn, string password, CancellationToken ct)
    {
        // Security-audit finding H-17 (defense-in-depth): never issue a simple-bind with an empty or whitespace-only
        // password. A populated UPN + zero-length password is an RFC 4513 §5.1.2 *unauthenticated
        // bind* that AD answers with LDAP_SUCCESS, not error 49 — i.e. an auth bypass. The primary
        // guard lives in LdapAuthenticator, but the adapter is the component that actually calls
        // Bind, so it refuses independently (clean invalid-credentials → null) in case it is ever
        // reached by another caller.
        if (string.IsNullOrWhiteSpace(password))
            return null;

        var opts = _activeConfiguration?.Ldap ?? _options.CurrentValue;
        var endpoints = LdapEndpoint.Resolve(opts);
        if (endpoints.Count == 0)
            throw new LdapInfrastructureException("Authentication:Ldap:Endpoints is not configured.");
        if (string.IsNullOrWhiteSpace(opts.BaseDn))
            throw new LdapInfrastructureException("Authentication:Ldap:BaseDn is not configured.");

        // The simple-bind below sends the user's password in cleartext over the connection;
        // without TLS that's a wire-sniff away from full credential takeover. LDAPS is
        // unconditional: the boot validator already rejects UseSsl=false for enabled
        // deployments, and the adapter refuses independently in case it is ever reached
        // with an unvalidated configuration.
        if (!opts.UseSsl)
        {
            throw new LdapInfrastructureException(
                "LDAP simple-bind over a plaintext channel is refused; set Authentication:Ldap:UseSsl=true (LDAPS).");
        }

        LdapInfrastructureException? lastInfrastructureError = null;
        foreach (var endpoint in endpoints)
        {
            try
            {
        ct.ThrowIfCancellationRequested();
        var identifier = new LdapDirectoryIdentifier(endpoint.Host, endpoint.Port, fullyQualifiedDnsHostName: false, connectionless: false);
        using var connection = new LdapConnection(identifier);

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = opts.UseSsl;
        // Never chase referrals (same rationale as OpenServiceConnection): stay on the
        // deliberately configured, TLS-verified endpoint.
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        connection.AuthType = AuthType.Basic;
        connection.Timeout = TimeSpan.FromSeconds(Math.Max(1, opts.BindTimeoutSeconds));

        // Stage 1: bind. We use NetworkCredential + UPN for simple-bind which AD accepts
        // without resolving the user DN client-side.
        try
        {
            connection.Bind(new NetworkCredential(upn, password));
        }
        catch (LdapException ex) when (ex.ErrorCode == 49 /* LDAP_INVALID_CREDENTIALS */)
        {
            // Wrong password / disabled account — clean negative verdict.
            return null;
        }
        catch (LdapException ex)
        {
            // Connect/server/TLS errors arrive here too. Distinguishing on ErrorCode is
            // unreliable across providers, so we treat anything that isn't 49 as infrastructure.
            throw new LdapInfrastructureException(
                $"LDAP bind failed (ErrorCode={ex.ErrorCode}): {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LdapInfrastructureException("LDAP bind failed: " + ex.Message, ex);
        }

        ct.ThrowIfCancellationRequested();

        // Optional service-bind for the search phase. When the operator configured both
        // ServiceBindDn and ServicePassword, we re-bind the SAME connection as the service
        // account before issuing the user/tokenGroups searches. Two reasons to want this:
        //   (a) The user's bind context can't always read tokenGroups under a typical
        //       enterprise OU layout (delegated read on the user object only). A service
        //       account with directory-wide read closes that gap.
        //   (b) The service-bind is the same identity for every login, so search-side
        //       diagnostics (failed lookups, slow searches) attribute cleanly to one
        //       principal in the DC's audit log.
        // If only one of the two is set, treat as misconfiguration and fail loud — half a
        // service-bind silently falling back to user-bind would mask the real intent.
        var hasServiceDn = !string.IsNullOrWhiteSpace(opts.ServiceBindDn);
        var hasServicePw = !string.IsNullOrWhiteSpace(opts.ServicePassword);
        if (hasServiceDn ^ hasServicePw)
        {
            throw new LdapInfrastructureException(
                "Authentication:Ldap:ServiceBindDn and ServicePassword must both be set, or both be empty.");
        }
        if (hasServiceDn && hasServicePw)
        {
            try
            {
                connection.Bind(new NetworkCredential(opts.ServiceBindDn, opts.ServicePassword));
            }
            catch (LdapException ex) when (ex.ErrorCode == 49)
            {
                throw new LdapInfrastructureException(
                    "LDAP service-bind credentials are invalid (Authentication:Ldap:ServiceBindDn / ServicePassword).", ex);
            }
            catch (LdapException ex)
            {
                throw new LdapInfrastructureException(
                    $"LDAP service-bind failed (ErrorCode={ex.ErrorCode}): {ex.Message}", ex);
            }
        }

        // Stage 2: locate the user record. Uses the bound credentials of the connection —
        // either the user we just validated, or (when configured) the service account we
        // rebound to above.
        try
        {
            var userSearch = new SearchRequest(
                opts.BaseDn,
                // objectCategory=person excludes computer accounts (also objectClass=user) that
                // could otherwise share a userPrincipalName with a real login principal.
                $"(&(objectCategory=person)(objectClass=user)(userPrincipalName={EscapeFilter(upn)}))",
                SearchScope.Subtree,
                "objectSid", "objectGUID", "displayName", "cn", "distinguishedName");

            var userResponse = (SearchResponse)connection.SendRequest(userSearch);
            if (userResponse.Entries.Count == 0)
            {
                throw new LdapInfrastructureException(
                    $"LDAP bind succeeded but no user object found for UPN '{upn}' under BaseDn '{opts.BaseDn}'.");
            }
            var entry = userResponse.Entries[0];

            var objectGuidBytes = GetFirstBytes(entry, "objectGUID")
                ?? throw new LdapInfrastructureException(
                    $"LDAP entry for '{upn}' is missing objectGUID — directory schema is non-AD?");
            var legacyExternalId = new Guid(objectGuidBytes).ToString("D");
            var objectSidBytes = GetFirstBytes(entry, "objectSid")
                ?? throw new LdapInfrastructureException(
                    $"LDAP entry for '{upn}' is missing objectSid — cannot establish a canonical AD identity.");
            string subject;
            try
            {
                subject = new SecurityIdentifier(objectSidBytes, 0).ToString();
            }
            catch (ArgumentException ex)
            {
                throw new LdapInfrastructureException(
                    $"LDAP entry for '{upn}' contains an invalid objectSid.", ex);
            }
            // Credential validation may succeed against a replication-stale DC. Never use
            // that DC's tokenGroups as an authorization/freshness source: resolve the SID
            // again through service-bind lookups against every configured DC and require
            // their security-relevant state to agree.
            var authoritativeSnapshot = LookupBySubjectCore(subject, ct);
            return BuildAuthoritativeAuthenticationResult(
                subject, legacyExternalId, authoritativeSnapshot);
        }
        catch (LdapInfrastructureException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LdapInfrastructureException(
                "LDAP bind succeeded but user/group lookup failed: " + ex.Message, ex);
        }
            }
            catch (LdapInfrastructureException ex)
            {
                lastInfrastructureError = ex;
                _logger.LogWarning(ex,
                    "LDAP endpoint {Host}:{Port} failed; trying the next configured endpoint",
                    endpoint.Host, endpoint.Port);
            }
        }

        throw new LdapInfrastructureException(
            $"All {endpoints.Count} configured LDAP endpoints failed.",
            (Exception?)lastInfrastructureError ?? new InvalidOperationException("No LDAP endpoint was attempted."));
    }

    internal static LdapAuthResult? BuildAuthoritativeAuthenticationResult(
        string subject,
        string legacyExternalId,
        LdapDirectorySnapshot? authoritativeSnapshot)
    {
        if (authoritativeSnapshot is null || !authoritativeSnapshot.IsEnabled)
            return null;
        if (!string.Equals(
                authoritativeSnapshot.Subject, subject, StringComparison.OrdinalIgnoreCase))
            throw new LdapInfrastructureException(
                "Authoritative LDAP lookup returned a different identity subject.");

        return new LdapAuthResult(
            ExternalId: subject,
            Upn: authoritativeSnapshot.Upn,
            DisplayName: authoritativeSnapshot.DisplayName,
            GroupSids: authoritativeSnapshot.GroupSids,
            LegacyExternalId: legacyExternalId);
    }

    private static byte[]? GetFirstBytes(SearchResultEntry entry, string attr)
    {
        if (!entry.Attributes.Contains(attr)) return null;
        var values = entry.Attributes[attr].GetValues(typeof(byte[]));
        return values.Length > 0 ? values[0] as byte[] : null;
    }

    private static string? GetFirstString(SearchResultEntry entry, string attr)
    {
        if (!entry.Attributes.Contains(attr)) return null;
        var values = entry.Attributes[attr].GetValues(typeof(string));
        return values.Length > 0 ? values[0] as string : null;
    }

    /// <summary>
    /// Escape characters that have special meaning inside an LDAP search filter (RFC 4515).
    /// Not strictly necessary for UPNs (which are mostly ASCII) but cheap insurance against
    /// a directory that decided <c>(</c> is a valid local-part character.
    /// </summary>
    internal static string EscapeFilter(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var ch in input)
        {
            switch (ch)
            {
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\\': sb.Append(@"\5c"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
