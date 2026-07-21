using System.Net.NetworkInformation;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Api.Services;

/// <summary>
/// Resolves the host identity (machine name, FQDN, DNS domain) from the local OS network
/// configuration via <see cref="IPGlobalProperties"/>. Deliberately avoids a DNS round-trip
/// (<c>Dns.GetHostEntry</c>): that call blocks and can hang or fail on a misconfigured
/// resolver, whereas <see cref="IPGlobalProperties"/> reads the values the OS already knows.
/// <para>
/// The result is computed once and cached for the process lifetime via <see cref="Lazy{T}"/>.
/// Registered as a singleton.
/// </para>
/// </summary>
public sealed class HostIdentityProvider : IHostIdentityProvider
{
    private readonly Lazy<HostIdentity> _identity = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public HostIdentity Current => _identity.Value;

    private static HostIdentity Resolve()
    {
        var machineName = Environment.MachineName;
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            return BuildIdentity(machineName, props.HostName, props.DomainName);
        }
        catch
        {
            // Network stack unavailable / restricted — fall back to the machine name so the
            // UI still shows *something* identifying. Never let host-info gathering throw.
            return new HostIdentity(machineName, machineName, null);
        }
    }

    /// <summary>
    /// Pure combination of the raw OS values into a <see cref="HostIdentity"/>. Split out so the
    /// FQDN-assembly logic is unit-testable without touching the real network stack.
    /// </summary>
    internal static HostIdentity BuildIdentity(string machineName, string? hostName, string? domainName)
    {
        // Prefer the DNS host label; fall back to the NetBIOS machine name when empty.
        var host = string.IsNullOrWhiteSpace(hostName) ? machineName : hostName.Trim();
        var domain = string.IsNullOrWhiteSpace(domainName) ? null : domainName.Trim();

        string fqdn;
        if (domain is null)
        {
            // Workgroup (no DNS domain) — the host label is the best we have.
            fqdn = host;
        }
        else if (host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
            || host.Contains('.'))
        {
            // Host label is already qualified — don't double-append the domain.
            fqdn = host;
        }
        else
        {
            fqdn = host + "." + domain;
        }

        return new HostIdentity(machineName, fqdn, domain);
    }
}
