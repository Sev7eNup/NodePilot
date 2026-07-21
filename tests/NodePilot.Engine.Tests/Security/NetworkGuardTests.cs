using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

public class NetworkGuardTests
{
    private static IConfiguration Cfg(string? blockPrivate)
    {
        var dict = new Dictionary<string, string?>();
        if (blockPrivate is not null) dict["RestApi:BlockPrivateNetworks"] = blockPrivate;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.1/")]
    public void Default_BlocksLoopbackAndPrivate(string url)
    {
        // Secure-by-default: a missing RestApi:BlockPrivateNetworks key now reads as "true"
        // so a stripped-down deployment falls on the safe side. Dev/test setups that need
        // 127.0.0.1 / RFC1918 reachability set the flag to "false" explicitly (mirrors
        // appsettings.Development.json).
        Action act = () => NetworkGuard.ValidateUrl(Cfg(null), url);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.1/")]
    public void WhenExplicitlyDisabled_AllowsLoopbackAndPrivate(string url)
    {
        // Dev-mode escape hatch — explicit BlockPrivateNetworks=false lets internal CMDB /
        // ticketing / monitoring calls flow through. The link-local range stays blocked
        // regardless (covered by Default_AlwaysBlocksLinkLocal).
        Action act = () => NetworkGuard.ValidateUrl(Cfg("false"), url);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // AWS metadata endpoint
    [InlineData("http://169.254.170.2/")]                    // ECS task metadata
    public void Default_AlwaysBlocksLinkLocal(string url)
    {
        // Link-local (incl. the cloud metadata range) is blocked unconditionally — there
        // is no legitimate reason for a workflow to hit the host's metadata endpoint via
        // the REST API activity.
        Action act = () => NetworkGuard.ValidateUrl(Cfg(null), url);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // AWS metadata
    public void WhenEnabled_BlocksPrivateAndLoopback(string url)
    {
        Action act = () => NetworkGuard.ValidateUrl(Cfg("true"), url);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("tru")]
    [InlineData("0")]
    [InlineData(" true ")]
    [InlineData("false ")]
    public void MalformedPrivateNetworkFlag_FailsClosed(string value)
    {
        Action act = () => NetworkGuard.ValidateUrl(Cfg(value), "http://127.0.0.1/");

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("169.254.169.254", "http://169.254.169.254/")]
    [InlineData("fe80::1", "http://[fe80::1]/")]
    public void AllowedHost_DoesNotBypassLinkLocalMetadataBlock(string host, string url)
    {
        // The allow-list only relaxes private/loopback blocking. Link-local remains an
        // absolute deny because an allowed DNS name can later be rebound to cloud metadata.
        var dict = new Dictionary<string, string?>
        {
            ["RestApi:AllowedHosts:0"] = host,
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        Action act = () => NetworkGuard.ValidateUrl(cfg, url);
        act.Should().Throw<InvalidOperationException>().WithMessage("*cannot be enabled*");
    }

    [Fact]
    public void AllowedHost_StillPermitsExplicitPrivateNetworkException()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["RestApi:AllowedHosts:0"] = "10.20.30.40" }).Build();

        Action act = () => NetworkGuard.ValidateUrl(config, "http://10.20.30.40/");

        act.Should().NotThrow();
    }

    [Fact]
    public void AllowedHost_NormalizesEquivalentIpv6Forms()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["RestApi:AllowedHosts:0"] = "::1" }).Build();

        Action act = () => NetworkGuard.ValidateUrl(config, "http://[0:0:0:0:0:0:0:1]/");

        act.Should().NotThrow();
    }

    [Fact]
    public void WhenEnabled_PublicIpLiteralAccepted()
    {
        Action act = () => NetworkGuard.ValidateUrl(Cfg("true"), "https://8.8.8.8/");
        act.Should().NotThrow();
    }

    [Fact]
    public void WhenEnabled_InvalidUrlRejected()
    {
        Action act = () => NetworkGuard.ValidateUrl(Cfg("true"), "not-a-url");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WhenEnabled_NonHttpSchemeRejected()
    {
        Action act = () => NetworkGuard.ValidateUrl(Cfg("true"), "file:///etc/passwd");
        act.Should().Throw<InvalidOperationException>();
    }

    // ---- EnforceConnect: TCP-connect-time policy (DNS rebinding closure) ----

    [Fact]
    public void EnforceConnect_LinkLocal_AlwaysRejected()
    {
        // ConnectCallback path: same policy as ValidateUrl, but expressed as IOException
        // because that's the transport-failure shape HttpClient surfaces. Link-local stays
        // blocked even when the operator has loosened private-network policy.
        var act = () => NetworkGuard.EnforceConnect(
            Cfg("false"),
            "rebind.example.com",
            new[] { System.Net.IPAddress.Parse("169.254.169.254") });

        act.Should().Throw<IOException>().WithMessage("*link-local*");
    }

    [Fact]
    public void EnforceConnect_PrivateNetwork_RejectedByDefault()
    {
        var act = () => NetworkGuard.EnforceConnect(
            Cfg(null),
            "internal.corp",
            new[] { System.Net.IPAddress.Parse("10.0.0.5") });

        act.Should().Throw<IOException>().WithMessage("*loopback/private*");
    }

    [Fact]
    public void EnforceConnect_PrivateNetwork_AllowedWhenExplicitlyDisabled()
    {
        var allowed = NetworkGuard.EnforceConnect(
            Cfg("false"),
            "internal.corp",
            new[] { System.Net.IPAddress.Parse("10.0.0.5") });

        allowed.Should().HaveCount(1);
        allowed[0].ToString().Should().Be("10.0.0.5");
    }

    [Fact]
    public void EnforceConnect_AllowedHost_DoesNotBypassLinkLocalMetadataBlock()
    {
        // The connect-time check must preserve the same absolute link-local deny as the
        // URL-time path, including for an explicitly allow-listed host name.
        var dict = new Dictionary<string, string?>
        {
            ["RestApi:AllowedHosts:0"] = "metadata.example",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var act = () => NetworkGuard.EnforceConnect(
            cfg, "metadata.example", [System.Net.IPAddress.Parse("169.254.169.254")]);

        act.Should().Throw<IOException>().WithMessage("*cannot be enabled*");
    }

    [Fact]
    public void EnforceConnect_AllowedHost_StillPermitsPrivateNetworkException()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["RestApi:AllowedHosts:0"] = "db.internal" }).Build();

        var allowed = NetworkGuard.EnforceConnect(
            config, "db.internal", [System.Net.IPAddress.Parse("10.20.30.40")]);

        allowed.Should().ContainSingle().Which.ToString().Should().Be("10.20.30.40");
    }

    [Fact]
    public void EnforceConnect_DnsRebinding_PartiallyBlocked_FiltersToAllowed()
    {
        // Realistic DNS-rebinding shape: the resolver returns one safe public IP and one
        // attacker-injected metadata IP. The policy strips the bad one and lets the request
        // continue on the safe survivor — mimics Happy-Eyeballs + filter semantics.
        var allowed = NetworkGuard.EnforceConnect(
            Cfg("false"),
            "split.example.com",
            new[]
            {
                System.Net.IPAddress.Parse("169.254.169.254"), // attacker rebind
                System.Net.IPAddress.Parse("8.8.8.8"),         // legitimate
            });

        allowed.Should().HaveCount(1);
        allowed[0].ToString().Should().Be("8.8.8.8");
    }

    [Fact]
    public void EnforceConnect_AllAddressesBlocked_ThrowsIO()
    {
        var act = () => NetworkGuard.EnforceConnect(
            Cfg("true"),
            "rebind.example.com",
            new[]
            {
                System.Net.IPAddress.Parse("169.254.169.254"),
                System.Net.IPAddress.Parse("10.0.0.5"),
            });

        act.Should().Throw<IOException>().WithMessage("*rejected every resolved address*");
    }

    [Theory]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:127.42.10.9]/")]
    public void Default_BlocksIpv4MappedLoopback(string url)
    {
        Action act = () => NetworkGuard.ValidateUrl(Cfg(null), url);

        act.Should().Throw<InvalidOperationException>().WithMessage("*loopback/private*");
    }
}
