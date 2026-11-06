using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Configuration;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

public sealed class SettingsConfigurationSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "np-settings-snapshot-" + Guid.NewGuid().ToString("N"));

    public SettingsConfigurationSnapshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Capture_HostEnvironmentProviderBeforeAppsettings_KeepsRuntimeAboveAppsettings()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), "{\"Llm\":{\"Enabled\":false,\"ActiveProfileId\":\"\"}}");
        File.WriteAllText(Path.Combine(_dir, "appsettings.Production.json"), "{\"Llm\":{\"Enabled\":false}}");
        var runtimePath = Path.Combine(_dir, "appsettings.runtime.json");
        File.WriteAllText(runtimePath, "{\"Llm\":{\"Enabled\":true,\"ActiveProfileId\":\"default\"}}");
        var protector = new PlainProtector();
        // WebApplicationBuilder registers its prefixed host provider ahead of the appsettings files.
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables("ASPNETCORE_")
            .SetBasePath(_dir)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Production.json", optional: false, reloadOnChange: false)
            .Add(new EncryptingJsonConfigurationSource(runtimePath, protector, optional: true, reloadOnChange: false))
            .AddEnvironmentVariables("NP_SNAPSHOT_TEST_")
            .Build();
        var writer = new RuntimeOverridesWriter(runtimePath, NullLogger<RuntimeOverridesWriter>.Instance);

        using var snapshot = SettingsConfigurationSnapshot.Capture(config, writer, protector);

        bool.Parse(snapshot.Configuration["Llm:Enabled"]!).Should().BeTrue();
        snapshot.Configuration["Llm:ActiveProfileId"].Should().Be("default");
        EffectiveSourceDetector.DetectMany(snapshot.Configuration, ["Llm:Enabled"])["Llm:Enabled"]
            .Should().Be(EffectiveSourceDetector.SourceRuntime);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string ProviderName => "Test";
        public byte[] Protect(string plaintext) => Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] blob) => Encoding.UTF8.GetString(blob);
    }
}
