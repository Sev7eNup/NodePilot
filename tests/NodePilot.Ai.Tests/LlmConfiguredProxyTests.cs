using System.Net;
using FluentAssertions;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// The dynamic <see cref="IWebProxy"/> behind the LLM HttpClient. The single most important
/// assertion here is the Off case: it must behave exactly like the hard-coded
/// <c>UseProxy = false</c> the handler carried before proxy support existed, because that is what
/// makes "no proxy configured" a genuine no-op for every existing installation.
/// </summary>
public sealed class LlmConfiguredProxyTests
{
    private static readonly Uri CloudEndpoint = new("https://api.openai.com/v1/chat/completions");
    private static readonly Uri LocalEndpoint = new("http://localhost:11434/v1/chat/completions");

    private static (LlmConfiguredProxy Proxy, MutableOptionsMonitor<LlmOptions> Monitor) Build(LlmProxyOptions proxy)
    {
        var options = LlmTestOptions.WithProfile();
        options.Proxy = proxy;
        var monitor = new MutableOptionsMonitor<LlmOptions>(options);
        return (new LlmConfiguredProxy(monitor), monitor);
    }

    [Fact]
    public void Off_BypassesEveryDestination_AndOffersNoProxy()
    {
        var (proxy, _) = Build(new LlmProxyOptions { Mode = LlmProxyMode.Off });

        proxy.IsBypassed(CloudEndpoint).Should().BeTrue();
        proxy.IsBypassed(LocalEndpoint).Should().BeTrue();
        proxy.GetProxy(CloudEndpoint).Should().BeNull();
        proxy.Credentials.Should().BeNull();
    }

    [Fact]
    public void Off_IsTheDefault_WhenNothingIsConfigured()
    {
        // A fresh LlmOptions must not route anything through a proxy — the upgrade path for every
        // existing installation depends on this.
        var monitor = new MutableOptionsMonitor<LlmOptions>(LlmTestOptions.WithProfile());
        var proxy = new LlmConfiguredProxy(monitor);

        proxy.IsBypassed(CloudEndpoint).Should().BeTrue();
        proxy.GetProxy(CloudEndpoint).Should().BeNull();
    }

    [Fact]
    public void Custom_RoutesThroughTheConfiguredAddress()
    {
        var (proxy, _) = Build(new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://proxy.corp.local:8080",
        });

        proxy.IsBypassed(CloudEndpoint).Should().BeFalse();
        proxy.GetProxy(CloudEndpoint).Should().Be(new Uri("http://proxy.corp.local:8080"));
    }

    [Fact]
    public void Custom_BypassGlob_KeepsALocalEndpointDirect()
    {
        // The mixed case the global (rather than per-profile) design relies on: cloud through the
        // proxy, local Ollama straight out.
        var (proxy, _) = Build(new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://proxy.corp.local:8080",
            BypassList = ["localhost", "*.intern"],
        });

        proxy.IsBypassed(LocalEndpoint).Should().BeTrue();
        proxy.IsBypassed(new Uri("https://llm.intern/v1/chat/completions")).Should().BeTrue();
        proxy.IsBypassed(CloudEndpoint).Should().BeFalse();
    }

    [Fact]
    public void Custom_PlaintextLoopback_IsAlwaysDirect_EvenWithoutBypassConfiguration()
    {
        var (proxy, _) = Build(new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://proxy.corp.local:8080",
        });

        proxy.IsBypassed(LocalEndpoint).Should().BeTrue();
        proxy.GetProxy(LocalEndpoint).Should().BeNull();
    }

    [Fact]
    public void Custom_WithUsername_PresentsNetworkCredential()
    {
        var (proxy, _) = Build(new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://proxy.corp.local:8080",
            Username = "svc-nodepilot",
            Password = "s3cret",
        });

        var credential = proxy.Credentials.Should().BeOfType<NetworkCredential>().Subject;
        credential.UserName.Should().Be("svc-nodepilot");
        credential.Password.Should().Be("s3cret");
    }

    [Fact]
    public void Custom_UseDefaultCredentials_WinsOverAnExplicitUsername()
    {
        var (proxy, _) = Build(new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://proxy.corp.local:8080",
            Username = "svc-nodepilot",
            Password = "s3cret",
            UseDefaultCredentials = true,
        });

        proxy.Credentials.Should().BeSameAs(CredentialCache.DefaultCredentials);
    }

    [Fact]
    public void System_DelegatesToTheProcessDefaultProxy()
    {
        var (proxy, _) = Build(new LlmProxyOptions { Mode = LlmProxyMode.System });

        // No assumption about what the host is configured with — only that the answer is the
        // OS-derived one rather than NodePilot's own.
        proxy.IsBypassed(CloudEndpoint).Should().Be(HttpClient.DefaultProxy.IsBypassed(CloudEndpoint));
        proxy.GetProxy(CloudEndpoint).Should().Be(HttpClient.DefaultProxy.GetProxy(CloudEndpoint));
    }

    [Fact]
    public void System_UseDefaultCredentials_PresentsTheServiceAccount()
    {
        var (proxy, _) = Build(new LlmProxyOptions { Mode = LlmProxyMode.System, UseDefaultCredentials = true });

        proxy.Credentials.Should().BeSameAs(CredentialCache.DefaultCredentials);
    }

    [Fact]
    public void ModeChange_TakesEffectWithoutRebuildingTheProxy()
    {
        // This is the whole reason the proxy is resolved per request instead of at handler
        // construction — it is what keeps the Llm settings section hot-reloadable.
        var (proxy, monitor) = Build(new LlmProxyOptions { Mode = LlmProxyMode.Off });
        proxy.IsBypassed(CloudEndpoint).Should().BeTrue();

        var updated = LlmTestOptions.WithProfile();
        updated.Proxy = new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://proxy.corp.local:8080",
        };
        monitor.Set(updated);

        proxy.IsBypassed(CloudEndpoint).Should().BeFalse();
        proxy.GetProxy(CloudEndpoint).Should().Be(new Uri("http://proxy.corp.local:8080"));
    }

    [Fact]
    public void AddressChange_TakesEffectOnTheNextRequest()
    {
        // The WebProxy is rebuilt per call, so a hot-reloaded address needs no invalidation step.
        var options = LlmTestOptions.WithProfile();
        options.Proxy = new LlmProxyOptions { Mode = LlmProxyMode.Custom, Address = "http://p1:8080" };
        var monitor = new MutableOptionsMonitor<LlmOptions>(options);
        var proxy = new LlmConfiguredProxy(monitor);

        proxy.GetProxy(CloudEndpoint).Should().Be(new Uri("http://p1:8080"));

        var updated = LlmTestOptions.WithProfile();
        updated.Proxy = new LlmProxyOptions { Mode = LlmProxyMode.Custom, Address = "http://p2:8080" };
        monitor.Set(updated);

        proxy.GetProxy(CloudEndpoint).Should().Be(new Uri("http://p2:8080"));
    }

    [Fact]
    public void BypassListChange_TakesEffectOnTheNextRequest()
    {
        // The address stays the same — only the bypass globs change. Nothing may carry the old
        // bypass regexes over into the next request.
        var options = LlmTestOptions.WithProfile();
        options.Proxy = new LlmProxyOptions { Mode = LlmProxyMode.Custom, Address = "http://p1:8080" };
        var monitor = new MutableOptionsMonitor<LlmOptions>(options);
        var proxy = new LlmConfiguredProxy(monitor);

        var internalEndpoint = new Uri("https://llm.intern/v1/chat/completions");
        proxy.IsBypassed(internalEndpoint).Should().BeFalse();

        var updated = LlmTestOptions.WithProfile();
        updated.Proxy = new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://p1:8080",
            BypassList = ["*.intern"],
        };
        monitor.Set(updated);

        proxy.IsBypassed(internalEndpoint).Should().BeTrue();
    }

    [Fact]
    public void CredentialChange_TakesEffectOnTheNextRequest()
    {
        // Same address, different credentials — the proxy must not serve a previously built one.
        var options = LlmTestOptions.WithProfile();
        options.Proxy = new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://p1:8080",
            Username = "old",
            Password = "old-secret",
        };
        var monitor = new MutableOptionsMonitor<LlmOptions>(options);
        var proxy = new LlmConfiguredProxy(monitor);

        proxy.GetProxy(CloudEndpoint).Should().Be(new Uri("http://p1:8080"));
        proxy.Credentials.Should().BeOfType<NetworkCredential>().Which.UserName.Should().Be("old");

        var updated = LlmTestOptions.WithProfile();
        updated.Proxy = new LlmProxyOptions
        {
            Mode = LlmProxyMode.Custom,
            Address = "http://p1:8080",
            Username = "new",
            Password = "new-secret",
        };
        monitor.Set(updated);

        var credential = proxy.Credentials.Should().BeOfType<NetworkCredential>().Subject;
        credential.UserName.Should().Be("new");
        credential.Password.Should().Be("new-secret");
    }

    [Fact]
    public void Custom_WithoutAddress_ThrowsWithAnActionableMessage()
    {
        // Rejected by LlmProfileValidation on save and at boot, so this only happens for a
        // hand-edited config picked up by hot-reload. Failing loudly beats silently going direct.
        var (proxy, _) = Build(new LlmProxyOptions { Mode = LlmProxyMode.Custom, Address = "" });

        proxy.Invoking(p => p.GetProxy(CloudEndpoint))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Llm:Proxy:Address is empty*");
    }

    [Fact]
    public void Custom_WithNonHttpAddress_Throws()
    {
        var (proxy, _) = Build(new LlmProxyOptions { Mode = LlmProxyMode.Custom, Address = "ftp://proxy:21" });

        proxy.Invoking(p => p.GetProxy(CloudEndpoint))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*not a valid http(s) URL*");
    }

    [Fact]
    public void CredentialsSetter_Throws_RatherThanSilentlyIgnoringTheAssignment()
    {
        var (proxy, _) = Build(new LlmProxyOptions { Mode = LlmProxyMode.Off });

        proxy.Invoking(p => p.Credentials = new NetworkCredential("a", "b"))
            .Should().Throw<NotSupportedException>();
    }
}
