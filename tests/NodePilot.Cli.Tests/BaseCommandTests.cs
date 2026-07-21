using System.Net;
using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Commands;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// BaseCommand owns the catch-block that turns API exceptions into the right exit
/// code. We exercise that mapping with a stub command that throws what we want
/// rather than spinning up a WireMock server per case.
/// </summary>
public sealed class BaseCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly SessionResolver _sessions;
    private readonly ApiClientFactory _factory;

    public BaseCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "np-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigStore(_dir);
        var cfg = new CliConfig();
        cfg.Profiles["default"] = new ProfileEntry { Server = "https://np.local" };
        config.Save(cfg);
        _sessions = new SessionResolver(config, new TokenStore(_dir));
        _factory = new ApiClientFactory(new TokenStore(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<int> Run(Func<Task<int>> body)
    {
        var cmd = new ThrowingCommand(_sessions, _factory, body);
        var ctx = new CommandContext(Array.Empty<string>(), DummyRemainingArgs.Instance, "test", null);
        return await cmd.ExecuteAsync(ctx, new GlobalSettings { Server = "https://np.local" });
    }

    [Fact]
    public async Task Unauthorized_MapsToAuthRequired()
    {
        var rc = await Run(() => throw new ApiException(HttpStatusCode.Unauthorized, "Unauthorized", null, null));
        rc.Should().Be(ExitCodes.AuthRequired);
    }

    [Fact]
    public async Task Forbidden_MapsToPermissionDenied()
    {
        var rc = await Run(() => throw new ApiException(HttpStatusCode.Forbidden, "Forbidden", "Admin only", null));
        rc.Should().Be(ExitCodes.PermissionDenied);
    }

    [Fact]
    public async Task Locked_MapsToError()
    {
        var rc = await Run(() => throw new ApiException((HttpStatusCode)423, "Locked", "checked out by alice", null));
        rc.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public async Task GenericApiException_MapsToError()
    {
        var rc = await Run(() => throw new ApiException(HttpStatusCode.InternalServerError, "Server", "boom", null));
        rc.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public async Task HttpRequestException_MapsToError()
    {
        var rc = await Run(() => throw new HttpRequestException("connection refused"));
        rc.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public async Task NotAuthenticatedException_MapsToAuthRequired()
    {
        var rc = await Run(() => throw new NotAuthenticatedException("no session for profile 'default'"));
        rc.Should().Be(ExitCodes.AuthRequired);
    }

    [Fact]
    public async Task InvalidOperationException_MapsToError()
    {
        var rc = await Run(() => throw new InvalidOperationException("workflow not found"));
        rc.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public async Task SuccessReturn_PassesThrough()
    {
        var rc = await Run(() => Task.FromResult(ExitCodes.Success));
        rc.Should().Be(ExitCodes.Success);
    }

    private sealed class ThrowingCommand : BaseCommand<GlobalSettings>
    {
        private readonly Func<Task<int>> _body;
        public ThrowingCommand(SessionResolver s, ApiClientFactory f, Func<Task<int>> body) : base(s, f) => _body = body;
        protected override Task<int> RunAsync(CommandContext context, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
            => _body();
    }

    private sealed class DummyRemainingArgs : IRemainingArguments
    {
        public static readonly DummyRemainingArgs Instance = new();
        public ILookup<string, string?> Parsed => Array.Empty<KeyValuePair<string, string?>>().ToLookup(k => k.Key, k => k.Value);
        public IReadOnlyList<string> Raw => Array.Empty<string>();
    }
}
