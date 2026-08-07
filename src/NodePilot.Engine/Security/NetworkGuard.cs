using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace NodePilot.Engine.Security;

/// <summary>
/// SSRF guard for <c>RestApiActivity</c>. Always rejects non-http(s) schemes and always
/// blocks link-local IPv4 (169.254.0.0/16 — including the cloud metadata endpoint at
/// 169.254.169.254) and IPv6 link-local. Loopback and private-network blocking (RFC1918,
/// CGNAT, ULA) is on by default since the Phase-3 hardening; flip
/// <c>RestApi:BlockPrivateNetworks=false</c> in dev/test to call internal/CMDB APIs on
/// loopback or RFC1918 addresses. An explicit allow-list of hosts can be configured via
/// <c>RestApi:AllowedHosts</c> to permit specific private/loopback services. Link-local
/// addresses (including cloud metadata) remain blocked even for allow-listed hosts.
///
/// Beyond URL-time validation, <see cref="EnforceConnect"/> applies the same rules at TCP-
/// connect time. RestApiHttpClientProvider wires this into the SocketsHttpHandler's
/// <c>ConnectCallback</c> so DNS rebinding cannot defeat the guard between
/// <see cref="ValidateUrl"/> and the actual outbound connect.
/// </summary>
public static class NetworkGuard
{
    public static void ValidateUrl(IConfiguration config, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"REST API: url '{url}' is not a valid absolute URI");

        // Scheme allow-list is unconditional: file://, gopher://, ftp:// etc. are never
        // safe to pipe to HttpClient regardless of network policy.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"REST API: url scheme '{uri.Scheme}' is not allowed");

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var direct))
        {
            addresses = [direct];
        }
        else
        {
            try { addresses = Dns.GetHostAddresses(uri.Host); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"REST API: host '{uri.Host}' could not be resolved: {ex.Message}");
            }
        }

        // The allow-list is a narrow exception for private/loopback services. It must never
        // become an exception for link-local/cloud-metadata addresses: an otherwise trusted
        // DNS name can be compromised or rebound to 169.254.169.254.
        var blockPrivate = ShouldBlockPrivateNetworks(config)
                           && !IsHostAllowlisted(config, uri.Host);
        foreach (var ip in addresses)
            AssertAddressAllowed(ip, uri.Host, blockPrivate);
    }

    /// <summary>
    /// Re-applies the SSRF policy at TCP-connect time. Wired into
    /// <see cref="System.Net.Http.SocketsHttpHandler.ConnectCallback"/> so a DNS-rebinding
    /// attack — where the validated URL resolves to a safe IP at <see cref="ValidateUrl"/>
    /// time but rebinds to <c>169.254.169.254</c> (cloud metadata) or RFC1918 between
    /// validation and connect — is rejected before the socket is opened.
    ///
    /// Returns the subset of <paramref name="addresses"/> that the caller may legitimately
    /// connect to. Throws <see cref="IOException"/> when the entire set is blocked, since
    /// this runs inside an HttpClient connect callback where IOException is the expected
    /// transport failure type. The exception message names the rule that fired so operators
    /// can correlate the connect failure with the SSRF policy.
    /// </summary>
    public static IPAddress[] EnforceConnect(IConfiguration config, string host, IPAddress[] addresses)
    {
        var hostAllowlisted = IsHostAllowlisted(config, host);
        var blockPrivate = ShouldBlockPrivateNetworks(config) && !hostAllowlisted;
        var allowed = new List<IPAddress>(addresses.Length);
        var lastReason = "";
        foreach (var original in addresses)
        {
            var ip = NormalizeAddress(original);
            if (IsLinkLocal(ip))
            {
                lastReason = $"link-local {ip} (169.254/16 incl. cloud metadata is always blocked)";
                continue;
            }
            if (blockPrivate && (IPAddress.IsLoopback(ip) || IsPrivateNetwork(ip)))
            {
                lastReason = $"loopback/private {ip} (RestApi:BlockPrivateNetworks)";
                continue;
            }
            allowed.Add(ip);
        }

        if (allowed.Count == 0)
        {
            var remediation = hostAllowlisted || lastReason.StartsWith("link-local", StringComparison.Ordinal)
                ? "Link-local/cloud-metadata destinations cannot be enabled through RestApi:AllowedHosts."
                : "Add the host to RestApi:AllowedHosts if this is genuinely an internal service.";
            throw new IOException(
                $"SSRF guard rejected every resolved address for host '{host}': {lastReason}. " +
                remediation);
        }

        return allowed.ToArray();
    }

    /// <summary>
    /// Outbound allow-list for <c>restApi</c>. Listing a host here is a narrow exception to
    /// <c>RestApi:BlockPrivateNetworks</c> — it lets an HTTP call reach a loopback/RFC1918
    /// service. Deliberately separate from the probe list below: widening one must never
    /// silently widen the other.
    /// </summary>
    internal static bool IsHostAllowlisted(IConfiguration config, string host)
        => IsHostListed(config, "RestApi:AllowedHosts", host);

    /// <summary>
    /// Allow-list for the PowerShell-backed network probes (<c>waitForCondition</c>
    /// <c>portOpen</c>/<c>httpOk</c>). Kept in its own configuration section so an operator can
    /// permit "probe my own host" without also punching a loopback hole into the
    /// <c>restApi</c> SSRF guard, whose URLs can be assembled from trigger payloads.
    /// </summary>
    internal static bool IsProbeHostAllowlisted(IConfiguration config, string host)
        => IsHostListed(config, "WaitForCondition:AllowedHosts", host);

    private static bool IsHostListed(IConfiguration config, string sectionPath, string host)
    {
        var requested = NormalizeHostForComparison(host);
        var allowedHosts = config.GetSection(sectionPath).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => NormalizeHostForComparison(v!));
        return allowedHosts.Any(h => string.Equals(h, requested, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Full policy check for the <c>httpOk</c> probe: URL shape, scheme, and probe admission.
    ///
    /// <para>Deliberately does <b>not</b> consult <c>RestApi:BlockPrivateNetworks</c> or
    /// <c>RestApi:AllowedHosts</c>. Routing the probe through <see cref="ValidateUrl"/> — as it
    /// did until 2026-08-07 — meant "check whether my own service is up" additionally required
    /// the restApi loopback exception, the exact coupling the two lists exist to prevent: the
    /// shipped default <c>WaitForCondition:AllowedHosts=["localhost"]</c> was inert on every
    /// non-Development instance, and the remediation the operator was shown named the wrong key.
    /// The mismatch also crossed a reload boundary — the probe list is hot-reloadable, the
    /// <c>RestApi</c> section is restart-required.</para>
    /// </summary>
    internal static void ValidateProbeUrl(IConfiguration config, string url, string operation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"{operation}: url '{url}' is not a valid absolute URI");

        // Same unconditional scheme rule as ValidateUrl: file://, gopher://, ftp:// are never
        // safe to hand to Invoke-WebRequest regardless of which allow-list admitted the host.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{operation}: url scheme '{uri.Scheme}' is not allowed");

        RequireExplicitlyAllowlistedHost(config, uri.Host, operation);
    }

    /// <summary>
    /// Network probes executed by PowerShell cannot use HttpClient's guarded ConnectCallback.
    /// They therefore require an exact, administrator-controlled host allow-list entry rather
    /// than accepting an externally supplied host that merely resolves safely once.
    ///
    /// <para>Link-local/cloud-metadata stays unreachable through <i>any</i> allow-list, matching
    /// <see cref="AssertAddressAllowed"/> and <see cref="EnforceConnect"/>: an allow-listed DNS
    /// name can be rebound to 169.254.169.254. The lookup is best-effort — see
    /// <see cref="TryResolve"/>.</para>
    /// </summary>
    internal static void RequireExplicitlyAllowlistedHost(IConfiguration config, string host, string operation)
    {
        if (!IsProbeHostAllowlisted(config, host))
            throw new InvalidOperationException(
                $"{operation}: host '{host}' is not explicitly allowed. Add the exact host to WaitForCondition:AllowedHosts; " +
                "PowerShell-backed network probes reject all dynamic destinations by default to prevent SSRF and DNS rebinding. " +
                "Note this is a separate list from RestApi:AllowedHosts, which only governs restApi's loopback/private-network exception.");

        foreach (var ip in TryResolve(host))
        {
            if (IsLinkLocal(ip))
                throw new InvalidOperationException(
                    $"{operation}: host '{host}' resolves to a link-local address ({NormalizeAddress(ip)}); this range " +
                    "(including the cloud metadata endpoint 169.254.169.254) is always blocked and cannot be enabled " +
                    "through WaitForCondition:AllowedHosts.");
        }
    }

    /// <summary>
    /// Best-effort address lookup for the probe guard. An unresolvable name yields an empty set
    /// instead of an error: <c>waitForCondition</c> is a <i>wait</i> activity, and a host that
    /// does not resolve yet is the normal case for "poll until the service comes up". The
    /// allow-list — not this lookup — is the authorization; the link-local check on top of it is
    /// defense in depth, so failing open on DNS cannot widen who may be probed.
    /// </summary>
    private static IPAddress[] TryResolve(string host)
    {
        var candidate = host.Trim();
        if (candidate.Length > 2 && candidate[0] == '[' && candidate[^1] == ']')
            candidate = candidate[1..^1];
        if (IPAddress.TryParse(candidate, out var direct)) return [direct];
        try { return Dns.GetHostAddresses(candidate); }
        catch (Exception) { return []; }
    }

    private static bool ShouldBlockPrivateNetworks(IConfiguration config)
    {
        // Default-on since Phase 3: a missing key behaves like "true" so a stripped-down
        // appsettings or a forgotten override falls on the safe side. Dev/test deployments
        // that need loopback or RFC1918 calls set the flag to "false" explicitly.
        var raw = config["RestApi:BlockPrivateNetworks"];
        if (string.IsNullOrWhiteSpace(raw)) return true;
        // Only the exact, explicitly configured boolean false relaxes this control. A typo
        // such as "flase", unexpected whitespace, or any other malformed value must not
        // silently turn SSRF protection off.
        return !string.Equals(raw, bool.FalseString, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertAddressAllowed(IPAddress ip, string host, bool blockPrivate)
    {
        ip = NormalizeAddress(ip);
        if (IsLinkLocal(ip))
            throw new InvalidOperationException($"REST API: host '{host}' resolves to a link-local address ({ip}); this range (including the cloud metadata endpoint 169.254.169.254) is always blocked by the SSRF guard and cannot be enabled through RestApi:AllowedHosts.");
        if (blockPrivate && (IPAddress.IsLoopback(ip) || IsPrivateNetwork(ip)))
            throw new InvalidOperationException($"REST API: host '{host}' resolves to loopback/private address {ip} (blocked by RestApi:BlockPrivateNetworks)");
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        ip = NormalizeAddress(ip);
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254; // 169.254/16 incl. cloud metadata
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv4MappedToIPv6)
                return IsLinkLocal(ip.MapToIPv4());
            return false;
        }
        return false;
    }

    private static bool IsPrivateNetwork(IPAddress ip)
    {
        ip = NormalizeAddress(ip);
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 127) return true;
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true; // CGNAT
            return false;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6SiteLocal) return true;
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true; // fc00::/7 ULA
            if (ip.IsIPv4MappedToIPv6)
                return IsPrivateNetwork(ip.MapToIPv4());
            return false;
        }
        return false;
    }

    private static IPAddress NormalizeAddress(IPAddress ip)
        => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static string NormalizeHostForComparison(string host)
    {
        var candidate = host.Trim();
        if (candidate.Length > 2 && candidate[0] == '[' && candidate[^1] == ']')
            candidate = candidate[1..^1];
        return IPAddress.TryParse(candidate, out var address)
            ? NormalizeAddress(address).ToString()
            : candidate;
    }

}
