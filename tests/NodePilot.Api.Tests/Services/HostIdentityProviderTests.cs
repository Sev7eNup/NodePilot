using FluentAssertions;
using NodePilot.Api.Services;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// Coverage for <see cref="HostIdentityProvider"/> — the process-cached host identity used by
/// <c>GET /api/system/host-info</c>. The <c>Resolve()</c> path reads the real OS network stack;
/// the <see cref="HostIdentityProvider.BuildIdentity"/> FQDN-assembly logic is exercised in
/// isolation so every workgroup / domain / already-qualified branch is pinned.
/// </summary>
public class HostIdentityProviderTests
{
    [Fact]
    public void Current_ReturnsNonEmptyIdentity()
    {
        var provider = new HostIdentityProvider();
        var id = provider.Current;

        id.MachineName.Should().NotBeNullOrEmpty();
        id.Fqdn.Should().NotBeNullOrEmpty();
        // MachineName is the process's real NetBIOS name.
        id.MachineName.Should().Be(Environment.MachineName);
    }

    [Fact]
    public void Current_IsProcessCached_ReturnsSameInstance()
    {
        var provider = new HostIdentityProvider();
        var first = provider.Current;
        var second = provider.Current;

        // Lazy<T> means both reads yield the identical cached record instance.
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void BuildIdentity_Workgroup_NoDomain_UsesHostLabelAsFqdn()
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "npsrv01", domainName: null);

        id.MachineName.Should().Be("NPSRV01");
        id.Domain.Should().BeNull();
        id.Fqdn.Should().Be("npsrv01");
    }

    [Fact]
    public void BuildIdentity_WithDomain_AppendsDomainToHost()
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "npsrv01", "corp.example.local");

        id.Domain.Should().Be("corp.example.local");
        id.Fqdn.Should().Be("npsrv01.corp.example.local");
    }

    [Fact]
    public void BuildIdentity_HostAlreadyQualifiedWithDomain_DoesNotDoubleAppend()
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "npsrv01.corp.example.local", "corp.example.local");

        id.Fqdn.Should().Be("npsrv01.corp.example.local");
    }

    [Fact]
    public void BuildIdentity_HostContainsDot_TreatedAsAlreadyQualified()
    {
        // Host label already carries a dot (a different sub-domain) — don't append the domain again.
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "npsrv01.other", "corp.example.local");

        id.Fqdn.Should().Be("npsrv01.other");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildIdentity_EmptyHostName_FallsBackToMachineName(string? hostName)
    {
        var id = HostIdentityProvider.BuildIdentity("FALLBACK01", hostName, domainName: null);
        id.Fqdn.Should().Be("FALLBACK01");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildIdentity_BlankDomain_TreatedAsWorkgroup(string domainName)
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "npsrv01", domainName);
        id.Domain.Should().BeNull();
        id.Fqdn.Should().Be("npsrv01");
    }

    [Fact]
    public void BuildIdentity_TrimsHostAndDomain()
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "  npsrv01  ", "  corp.local  ");
        id.Domain.Should().Be("corp.local");
        id.Fqdn.Should().Be("npsrv01.corp.local");
    }
}
