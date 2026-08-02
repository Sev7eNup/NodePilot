using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

/// <summary>
/// The two host allow-lists must stay independent.
///
/// <para><c>RestApi:AllowedHosts</c> is a narrow exception to
/// <c>RestApi:BlockPrivateNetworks</c>: it lets an outbound HTTP call reach a
/// loopback/RFC1918 service, and restApi URLs can be assembled from trigger payloads.
/// <c>WaitForCondition:AllowedHosts</c> admits the PowerShell-backed portOpen/httpOk probes,
/// which cannot re-validate the destination at connect time.</para>
///
/// <para>They were a single key until 2026-08-02. Merging them meant an operator who wanted
/// "let a workflow check whether my own service is up" also opened restApi to loopback —
/// which is why shipping <c>localhost</c> as a production default was only defensible once
/// the two were split.</para>
/// </summary>
public class ProbeAllowListSeparationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public void Probe_AdmitsHostFromItsOwnList()
    {
        var config = Config(("WaitForCondition:AllowedHosts:0", "localhost"));

        Action act = () => NetworkGuard.RequireExplicitlyAllowlistedHost(config, "localhost", "WaitForCondition portOpen");

        act.Should().NotThrow();
    }

    [Fact]
    public void Probe_IsNotAdmittedByTheRestApiList()
    {
        // The whole point of the split: a restApi exception must not become probe permission.
        var config = Config(("RestApi:AllowedHosts:0", "localhost"));

        Action act = () => NetworkGuard.RequireExplicitlyAllowlistedHost(config, "localhost", "WaitForCondition portOpen");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WaitForCondition:AllowedHosts*");
    }

    [Fact]
    public void RestApi_LoopbackExceptionIsNotGrantedByTheProbeList()
    {
        // And the reverse: permitting a probe must not relax the SSRF guard for restApi.
        var config = Config(
            ("WaitForCondition:AllowedHosts:0", "localhost"),
            ("RestApi:BlockPrivateNetworks", "true"));

        Action act = () => NetworkGuard.ValidateUrl(config, "http://localhost:8080/");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*loopback*");
    }

    [Fact]
    public void Probe_EmptyListRejectsEverything()
    {
        Action act = () => NetworkGuard.RequireExplicitlyAllowlistedHost(
            Config(), "anything.example", "WaitForCondition httpOk");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*is not explicitly allowed*");
    }

    [Fact]
    public void Probe_ErrorNamesTheCorrectKeyAndDistinguishesItFromRestApi()
    {
        // The old message pointed operators at RestApi:AllowedHosts, which no longer has any
        // effect here — following it would have looked like the product ignoring the setting.
        Action act = () => NetworkGuard.RequireExplicitlyAllowlistedHost(
            Config(), "localhost", "WaitForCondition portOpen");

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("WaitForCondition:AllowedHosts");
        message.Should().Contain("separate list from RestApi:AllowedHosts");
    }

    [Theory]
    [InlineData("LOCALHOST")]
    [InlineData("  localhost  ")]
    public void Probe_MatchesCaseInsensitivelyAndTrimsConfiguredEntries(string configured)
    {
        var config = Config(("WaitForCondition:AllowedHosts:0", configured));

        Action act = () => NetworkGuard.RequireExplicitlyAllowlistedHost(config, "localhost", "WaitForCondition portOpen");

        act.Should().NotThrow();
    }

    [Fact]
    public void Probe_LoopbackSpellingsAreDistinctEntries()
    {
        // Matching is on the host string, not the resolved address: "localhost" and
        // "127.0.0.1" are different entries. Documented so nobody reads a rejected probe as
        // a bug after allow-listing the other spelling.
        var config = Config(("WaitForCondition:AllowedHosts:0", "localhost"));

        Action act = () => NetworkGuard.RequireExplicitlyAllowlistedHost(config, "127.0.0.1", "WaitForCondition portOpen");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Probe_NormalizesEquivalentIpv6Forms()
    {
        var config = Config(("WaitForCondition:AllowedHosts:0", "::1"));

        Action act = () => NetworkGuard.RequireExplicitlyAllowlistedHost(config, "[0:0:0:0:0:0:0:1]", "WaitForCondition httpOk");

        act.Should().NotThrow();
    }
}
