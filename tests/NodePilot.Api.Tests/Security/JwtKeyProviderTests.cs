using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NodePilot.Api.Security;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// JwtKeyProvider resolves the signing key once on construction and caches it.
/// AddJwtKeyProvider registers it as a singleton — these tests guard the audit M-2
/// guarantee that misconfiguration fails fast at startup, not later under load.
/// </summary>
public class JwtKeyProviderTests
{
    private static IConfiguration ConfigWithKey(string key)
    {
        var dict = new Dictionary<string, string?> { ["Jwt:Key"] = key };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IHostEnvironment Env(string contentRoot)
    {
        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.ContentRootPath).Returns(contentRoot);
        return mock.Object;
    }

    [Fact]
    public void Constructor_ValidKey_CachesResolvedKey()
    {
        var key = new string('a', 48);
        var provider = new JwtKeyProvider(ConfigWithKey(key), Env(Path.GetTempPath()));

        provider.Key.Should().Be(key);
    }

    [Fact]
    public void Constructor_InvalidKey_ThrowsImmediately()
    {
        Action act = () => new JwtKeyProvider(ConfigWithKey("too-short"), Env(Path.GetTempPath()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void AddJwtKeyProvider_RegistersAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(ConfigWithKey(new string('z', 48)));
        services.AddSingleton(Env(Path.GetTempPath()));

        services.AddJwtKeyProvider();

        var sp = services.BuildServiceProvider();
        var first = sp.GetRequiredService<IJwtKeyProvider>();
        var second = sp.GetRequiredService<IJwtKeyProvider>();

        first.Should().BeSameAs(second);
        first.Key.Should().Be(new string('z', 48));
    }

    [Fact]
    public void AddNodePilotAuthentication_RegistersAlreadyResolvedProviderInstance()
    {
        var key = new string('q', 48);
        var configuration = ConfigWithKey(key);
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        var services = new ServiceCollection();

        services.AddNodePilotAuthentication(configuration, environment.Object);

        var descriptor = services.Last(d => d.ServiceType == typeof(IJwtKeyProvider));
        var provider = descriptor.ImplementationInstance
            .Should().BeOfType<JwtKeyProvider>().Subject;
        provider.Key.Should().Be(key);
    }
}
