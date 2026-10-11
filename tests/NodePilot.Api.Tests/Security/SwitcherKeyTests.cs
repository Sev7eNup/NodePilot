using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

[Trait("Platform", "Windows")]
public sealed class SwitcherKeyTests : IDisposable
{
    private const string Key = "test-switcher-key";

    private readonly string _tempDir;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config = new ConfigurationBuilder().Build();

    public SwitcherKeyTests()
    {
        _tempDir = Path.Combine(
            Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath(),
            "SwitcherKeyTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User!;
            var acl = new DirectorySecurity();
            acl.SetOwner(owner);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(_tempDir).SetAccessControl(acl);
        }

        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.ContentRootPath).Returns(_tempDir);
        mock.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        _env = mock.Object;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string KeyPath => Path.Combine(_tempDir, SwitcherKeyStore.FileName);
    private string HashPath => KeyPath + SwitcherKeyStore.HashSuffix;

    private void WriteKeyPair(string key)
    {
        File.WriteAllText(KeyPath, key);
        RestrictedFileWriter.WriteText(HashPath, SwitcherKeyStore.Hash(key), failClosed: true);
    }

    private static DefaultHttpContext Request(
        string method, string path, string? key = Key,
        string remote = "127.0.0.1", string local = "127.0.0.1")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (key is not null) ctx.Request.Headers[SwitcherKeyStore.Header] = key;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        ctx.Connection.LocalIpAddress = IPAddress.Parse(local);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private async Task<(DefaultHttpContext Context, bool Reached)> RunAsync(DefaultHttpContext ctx)
    {
        var reached = false;
        var middleware = new SwitcherKeyMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(ctx, new SwitcherKeyStore(_env, _config));
        return (ctx, reached);
    }

    [Fact]
    public void EnsureProvisioned_CreatesKeyAndHashWithoutStoringTheKeyInTheHash()
    {
        SwitcherKeyStore.EnsureProvisioned(_env, _config, NullLogger.Instance);

        File.Exists(KeyPath).Should().BeTrue();
        File.Exists(HashPath).Should().BeTrue();
        File.ReadAllText(HashPath).Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public void EnsureProvisioned_ExistingPair_KeepsIt()
    {
        SwitcherKeyStore.EnsureProvisioned(_env, _config, NullLogger.Instance);
        var hash = File.ReadAllText(HashPath);

        SwitcherKeyStore.EnsureProvisioned(_env, _config, NullLogger.Instance);

        File.ReadAllText(HashPath).Should().Be(hash);
    }

    [Fact]
    public void EnsureProvisioned_MissingKeyFile_RotatesThePair()
    {
        SwitcherKeyStore.EnsureProvisioned(_env, _config, NullLogger.Instance);
        var hash = File.ReadAllText(HashPath);
        File.Delete(KeyPath);

        SwitcherKeyStore.EnsureProvisioned(_env, _config, NullLogger.Instance);

        File.Exists(KeyPath).Should().BeTrue();
        File.ReadAllText(HashPath).Should().NotBe(hash);
    }

    [Fact]
    public void Validate_MatchingKey_ReturnsTrue()
    {
        WriteKeyPair(Key);
        var store = new SwitcherKeyStore(_env, _config);

        store.Validate(Key).Should().BeTrue();
        store.Validate("other").Should().BeFalse();
        store.Validate(null).Should().BeFalse();
    }

    [Fact]
    public void Validate_NoFiles_ReturnsFalse() =>
        new SwitcherKeyStore(_env, _config).Validate(Key).Should().BeFalse();

    [Fact]
    public void Validate_KeyFileDeleted_ReturnsFalse()
    {
        WriteKeyPair(Key);
        File.Delete(KeyPath);

        new SwitcherKeyStore(_env, _config).Validate(Key).Should().BeFalse();
    }

    [Theory]
    [InlineData("GET", "/api/workflows")]
    [InlineData("GET", "/api/operations/graph")]
    [InlineData("POST", "/api/workflows/3f2504e0-4f89-11d3-9a0c-0305e82c3301/enable")]
    [InlineData("POST", "/api/workflows/3f2504e0-4f89-11d3-9a0c-0305e82c3301/disable")]
    [InlineData("POST", "/api/workflows/3f2504e0-4f89-11d3-9a0c-0305e82c3301/cancel-all")]
    public async Task Invoke_ValidKeyOnAllowedRequest_SetsAdminPrincipalAndContinues(string method, string path)
    {
        WriteKeyPair(Key);

        var (ctx, reached) = await RunAsync(Request(method, path));

        reached.Should().BeTrue();
        ctx.User.IsInRole("Admin").Should().BeTrue();
        ctx.Items.Should().ContainKey(SwitcherKeyMiddleware.PrincipalItem);
    }

    [Theory]
    [InlineData("GET", "/api/credentials")]
    [InlineData("DELETE", "/api/workflows/3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("POST", "/api/workflows")]
    [InlineData("PUT", "/api/workflows/3f2504e0-4f89-11d3-9a0c-0305e82c3301/enable")]
    [InlineData("POST", "/api/workflows/not-a-guid/enable")]
    [InlineData("POST", "/api/workflows/3f2504e0-4f89-11d3-9a0c-0305e82c3301/execute")]
    public async Task Invoke_ValidKeyOutsideScope_Returns403(string method, string path)
    {
        WriteKeyPair(Key);

        var (ctx, reached) = await RunAsync(Request(method, path));

        reached.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Invoke_WrongKey_Returns401WithoutContinuing()
    {
        WriteKeyPair(Key);

        var (ctx, reached) = await RunAsync(Request("GET", "/api/workflows", key: "wrong"));

        reached.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_RemoteAddress_Returns403()
    {
        WriteKeyPair(Key);

        var (ctx, reached) = await RunAsync(Request("GET", "/api/workflows", remote: "10.0.0.5"));

        reached.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Invoke_ClientUsingTheMachinesOwnAddress_IsAccepted()
    {
        WriteKeyPair(Key);

        var (_, reached) = await RunAsync(
            Request("GET", "/api/workflows", remote: "10.0.0.5", local: "10.0.0.5"));

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_LoopbackClientToNonLoopbackLocalAddress_Returns403()
    {
        WriteKeyPair(Key);

        var (ctx, reached) = await RunAsync(Request("GET", "/api/workflows", local: "10.0.0.5"));

        reached.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(403);
    }

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("Forwarded")]
    [InlineData("X-Original-For")]
    public async Task Invoke_ForwardingHeader_Returns403(string header)
    {
        WriteKeyPair(Key);
        var ctx = Request("GET", "/api/workflows");
        ctx.Request.Headers[header] = "203.0.113.9";

        var (result, reached) = await RunAsync(ctx);

        reached.Should().BeFalse();
        result.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Invoke_AlreadyAuthenticated_Returns401()
    {
        WriteKeyPair(Key);
        var ctx = Request("GET", "/api/workflows");
        ctx.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity("Bearer"));

        var (result, reached) = await RunAsync(ctx);

        reached.Should().BeFalse();
        result.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Invoke_NoKeyHeader_PassesThroughUntouched()
    {
        var (ctx, reached) = await RunAsync(Request("GET", "/api/credentials", key: null));

        reached.Should().BeTrue();
        ctx.Items.Should().NotContainKey(SwitcherKeyMiddleware.PrincipalItem);
    }
}
