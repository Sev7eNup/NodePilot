using System.Data.Common;
using FluentAssertions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NodePilot.Api.Security;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Verifies the env-aware Secure-flag policy on the auth cookie pair (<c>np_auth</c> +
/// <c>np_csrf</c>). The legacy behaviour ("Secure follows <c>Request.IsHttps</c>") still
/// kicks in when the issuer was constructed without an <see cref="IHostEnvironment"/>
/// (test fixtures and dev-time direct construction). When the host environment is
/// non-Development, the cookies must be Secure regardless of <c>Request.IsHttps</c> —
/// a Reverse-Proxy that strips <c>X-Forwarded-Proto</c> would otherwise hand out cookies
/// that a passive on-path attacker could replay over plain HTTP.
/// </summary>
public class AuthSessionIssuerSecureCookieTests
{
    private sealed record RefreshAttempt(
        IssuedSession? Session,
        Exception? Error,
        DefaultHttpContext Context);

    private sealed class CommitAcknowledgementLostException : Exception { }

    private sealed class CommitAmbiguityExecutionStrategy(
        ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception)
            => exception is CommitAcknowledgementLostException;
    }

    public sealed class CommitAmbiguityExecutionStrategyFactory(
        ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new CommitAmbiguityExecutionStrategy(dependencies);
    }

    private sealed class LoseFirstCommitAcknowledgementInterceptor : DbTransactionInterceptor
    {
        private int _commitCount;
        public int CommitCount => Volatile.Read(ref _commitCount);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _commitCount) == 1)
                throw new CommitAcknowledgementLostException();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NodePilot.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static IConfiguration NewConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "NodePilot-Test-Secret-Key-Minimum-32-Characters!",
            ["Jwt:Issuer"] = "NodePilot",
            ["Jwt:Audience"] = "NodePilot",
        }).Build();

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "ops-user",
        Role = UserRole.Operator,
        PasswordHash = "irrelevant-for-issuer",
    };

    private static DefaultHttpContext NewHttpContext(bool isHttps)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = isHttps ? "https" : "http";
        ctx.Request.Host = new HostString("nodepilot.example.com");
        return ctx;
    }

    private static (string? Auth, string? Csrf) ExtractSetCookieHeader(DefaultHttpContext ctx)
    {
        var values = ctx.Response.Headers["Set-Cookie"].ToArray();
        string? auth = values.FirstOrDefault(v => v is not null && v.StartsWith("np_auth=", StringComparison.Ordinal));
        string? csrf = values.FirstOrDefault(v => v is not null && v.StartsWith("np_csrf=", StringComparison.Ordinal));
        return (auth, csrf);
    }

    [Fact]
    public async Task NoEnvironment_HttpRequest_OmitsSecure()
    {
        // Legacy behaviour for the 10 test fixtures that construct the issuer with the 3-arg
        // ctor: the missing env falls back to "Secure follows Request.IsHttps" — same shape
        // those fixtures were written against.
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance);
        var ctx = NewHttpContext(isHttps: false);

        await issuer.IssueAsync(NewUser(), AuthSource.Local, ctx, CancellationToken.None);

        var (auth, csrf) = ExtractSetCookieHeader(ctx);
        auth.Should().NotBeNull().And.NotContain("secure", "isHttps=false and no env hint defaults to non-Secure");
        csrf.Should().NotBeNull().And.NotContain("secure");
    }

    [Fact]
    public async Task DevelopmentEnvironment_HttpRequest_OmitsSecure()
    {
        // `dotnet run --urls http://localhost:5000` must still produce a working auth cookie
        // in dev — otherwise the SPA cannot store the session at all on plain-HTTP localhost.
        var env = new FakeEnvironment { EnvironmentName = Environments.Development };
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, env);
        var ctx = NewHttpContext(isHttps: false);

        await issuer.IssueAsync(NewUser(), AuthSource.Local, ctx, CancellationToken.None);

        var (auth, csrf) = ExtractSetCookieHeader(ctx);
        auth.Should().NotContain("secure");
        csrf.Should().NotContain("secure");
    }

    [Fact]
    public async Task ProductionEnvironment_HttpRequest_SetsSecure()
    {
        // Defense-in-Depth: production must never hand out cookies without the Secure-flag,
        // even when Request.IsHttps comes back false (proxy without ForwardedHeaders, etc.).
        var env = new FakeEnvironment { EnvironmentName = Environments.Production };
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, env);
        var ctx = NewHttpContext(isHttps: false);

        await issuer.IssueAsync(NewUser(), AuthSource.Local, ctx, CancellationToken.None);

        var (auth, csrf) = ExtractSetCookieHeader(ctx);
        auth.Should().NotBeNull();
        auth!.Should().Contain("secure", "non-Development env enforces Secure regardless of request scheme");
        csrf.Should().NotBeNull();
        csrf!.Should().Contain("secure");
    }

    [Fact]
    public async Task StagingEnvironment_HttpsRequest_SetsSecure()
    {
        // Staging is also non-Development → same hardening as Production.
        var env = new FakeEnvironment { EnvironmentName = Environments.Staging };
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, env);
        var ctx = NewHttpContext(isHttps: true);

        await issuer.IssueAsync(NewUser(), AuthSource.Local, ctx, CancellationToken.None);

        var (auth, csrf) = ExtractSetCookieHeader(ctx);
        auth!.Should().Contain("secure");
        csrf!.Should().Contain("secure");
    }

    [Fact]
    public async Task EnterpriseUser_WithFiveHundredGroups_StillGetsCompactCookie()
    {
        var user = NewUser();
        user.Provider = AuthProvider.Ldap;
        user.KnownGroupSidsJson = JsonSerializer.Serialize(
            Enumerable.Range(1, 500).Select(i => $"S-1-5-21-111111111-222222222-333333333-{1000 + i}"));
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance);
        var ctx = NewHttpContext(isHttps: true);

        var session = await issuer.IssueAsync(user, AuthSource.Ldap, ctx, CancellationToken.None);

        var (auth, _) = ExtractSetCookieHeader(ctx);
        auth.Should().NotBeNull();
        auth!.Length.Should().BeLessThan(3800,
            "directory memberships are server-side and must not inflate the browser cookie");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(session.Token);
        jwt.Claims.Should().NotContain(c => c.Type == ClaimTypes.GroupSid);
        jwt.Claims.Should().ContainSingle(c => c.Type == AuthSessionIssuer.SessionIdClaim);
    }

    [Fact]
    public async Task BreakGlassLogin_EmitsDedicatedEmergencyAccessAuditSignal()
    {
        var user = NewUser();
        user.Role = UserRole.Admin;
        user.IsBreakGlass = true;
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), audit);

        await issuer.IssueAsync(user, AuthSource.Local, NewHttpContext(isHttps: true), default);

        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("BREAK_GLASS_LOGIN_SUCCESS");
        using var details = JsonDocument.Parse(call.Details!);
        details.RootElement.GetProperty("breakGlass").GetBoolean().Should().BeTrue();
        details.RootElement.GetProperty("source").GetString().Should().Be("Local");
    }

    [Fact]
    public async Task RefreshToken_IsSingleUseWithinServerSideSessionFamily()
    {
        using var db = TestDbFactory.Create();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var issuer = new AuthSessionIssuer(
            NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, db: db);
        var loginContext = NewHttpContext(isHttps: true);
        var original = await issuer.IssueAsync(user, AuthSource.Local, loginContext, default);
        var originalJwt = new JwtSecurityTokenHandler().ReadJwtToken(original.Token);
        var originalPrincipal = new ClaimsPrincipal(new ClaimsIdentity(originalJwt.Claims, "jwt"));

        var firstRefreshContext = NewHttpContext(isHttps: true);
        firstRefreshContext.User = originalPrincipal;
        var refreshed = await issuer.RefreshAsync(user, firstRefreshContext, default);

        var replayContext = NewHttpContext(isHttps: true);
        replayContext.User = originalPrincipal;
        var replay = async () => await issuer.RefreshAsync(user, replayContext, default);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>();

        var refreshedJwt = new JwtSecurityTokenHandler().ReadJwtToken(refreshed.Token);
        var persisted = db.AuthSessions.Single();
        persisted.CurrentJti.Should().Be(refreshedJwt.Id);
        persisted.RefreshGeneration.Should().Be(1);
        refreshed.TokenRotationCommitted.Should().BeTrue();
        db.RevokedTokens.Should().ContainSingle(r => r.Jti == originalJwt.Id && r.Reason == "rotated");
    }

    [Fact]
    public async Task Refresh_WhenRevocationWriteFails_RollsBackSessionAndEmitsNoCookies()
    {
        using var db = TestDbFactory.Create();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var issuer = new AuthSessionIssuer(
            NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, db: db);
        var original = await issuer.IssueAsync(
            user, AuthSource.Local, NewHttpContext(isHttps: true), default);
        var originalJwt = new JwtSecurityTokenHandler().ReadJwtToken(original.Token);

        // Abort exactly the second half of the rotation. SaveChanges has already staged the
        // AuthSession update, so this catches regressions that split or fail to transact the
        // two writes rather than merely testing a failure before any database work starts.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER fail_refresh_revocation
            BEFORE INSERT ON RevokedTokens
            WHEN NEW.Reason = 'rotated'
            BEGIN
                SELECT nodepilot_injected_refresh_failure();
            END;
            """);

        var refreshContext = NewHttpContext(isHttps: true);
        refreshContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(originalJwt.Claims, "jwt"));

        Func<Task> refresh = async () =>
            await issuer.RefreshAsync(user, refreshContext, default);

        await refresh.Should().ThrowAsync<DbUpdateException>();
        ExtractSetCookieHeader(refreshContext).Should().Be((null, null),
            "cookies must only be emitted after the complete rotation commits");

        db.ChangeTracker.Clear();
        var persisted = await db.AuthSessions.AsNoTracking().SingleAsync();
        persisted.CurrentJti.Should().Be(originalJwt.Id,
            "a failed revocation insert must roll back the staged CurrentJti change");
        persisted.RefreshGeneration.Should().Be(0);
        (await db.RevokedTokens.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_WhenCommitAcknowledgementIsLost_VerifiesCommitAndReturnsSameToken()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var setupOptions = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection)
            .Options;
        User user;
        IssuedSession original;
        await using (var setup = new NodePilotDbContext(setupOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            user = NewUser();
            setup.Users.Add(user);
            await setup.SaveChangesAsync();
            var setupIssuer = new AuthSessionIssuer(
                NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, db: setup);
            original = await setupIssuer.IssueAsync(
                user, AuthSource.Local, NewHttpContext(isHttps: true), default);
        }

        var lostCommit = new LoseFirstCommitAcknowledgementInterceptor();
        var refreshOptions = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(lostCommit)
            .ReplaceService<IExecutionStrategyFactory, CommitAmbiguityExecutionStrategyFactory>()
            .Options;
        await using var refreshDb = new NodePilotDbContext(refreshOptions);
        var issuer = new AuthSessionIssuer(
            NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, db: refreshDb);
        var originalJwt = new JwtSecurityTokenHandler().ReadJwtToken(original.Token);
        var refreshContext = NewHttpContext(isHttps: true);
        refreshContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(originalJwt.Claims, "jwt"));

        var refreshed = await issuer.RefreshAsync(user, refreshContext, default);

        lostCommit.CommitCount.Should().Be(1,
            "verifySucceeded must recognize the committed stable JTI instead of replaying the write");
        refreshed.TokenRotationCommitted.Should().BeTrue();
        ExtractSetCookieHeader(refreshContext).Auth.Should().NotBeNull();
        refreshDb.ChangeTracker.Clear();
        var persisted = await refreshDb.AuthSessions.AsNoTracking().SingleAsync();
        var refreshedJwt = new JwtSecurityTokenHandler().ReadJwtToken(refreshed.Token);
        persisted.CurrentJti.Should().Be(refreshedJwt.Id);
        persisted.RefreshGeneration.Should().Be(1);
        (await refreshDb.RevokedTokens.AsNoTracking().SingleAsync()).Jti
            .Should().Be(originalJwt.Id);
    }

    [Fact]
    public async Task ParallelRefreshes_AcrossDbContexts_CommitExactlyOneRotation()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), $"nodepilot-refresh-race-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        var baseOptions = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            IssuedSession original;
            await using (var setup = new NodePilotDbContext(baseOptions))
            {
                await setup.Database.EnsureCreatedAsync();
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                var user = NewUser();
                setup.Users.Add(user);
                await setup.SaveChangesAsync();
                var issuer = new AuthSessionIssuer(
                    NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, db: setup);
                original = await issuer.IssueAsync(
                    user, AuthSource.Local, NewHttpContext(isHttps: true), default);
            }

            var originalJwt = new JwtSecurityTokenHandler().ReadJwtToken(original.Token);
            var racingOptions = new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using var firstDb = new NodePilotDbContext(racingOptions);
            await using var secondDb = new NodePilotDbContext(racingOptions);

            async Task<RefreshAttempt> AttemptAsync(NodePilotDbContext db)
            {
                var user = await db.Users.AsNoTracking().SingleAsync();
                var context = NewHttpContext(isHttps: true);
                context.User = new ClaimsPrincipal(
                    new ClaimsIdentity(originalJwt.Claims, "jwt"));
                var issuer = new AuthSessionIssuer(
                    NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, db: db);
                try
                {
                    return new RefreshAttempt(
                        await issuer.RefreshAsync(user, context, default), null, context);
                }
                catch (Exception ex)
                {
                    return new RefreshAttempt(null, ex, context);
                }
            }

            var attempts = await Task.WhenAll(
                AttemptAsync(firstDb),
                AttemptAsync(secondDb));

            var winner = attempts.Should().ContainSingle(a => a.Session != null).Subject;
            var loser = attempts.Should().ContainSingle(a => a.Error != null).Subject;
            loser.Error.Should().BeOfType<UnauthorizedAccessException>(
                "the stale parallel refresh is a replay, not a second valid rotation");
            winner.Session!.TokenRotationCommitted.Should().BeTrue();
            ExtractSetCookieHeader(winner.Context).Auth.Should().NotBeNull();
            ExtractSetCookieHeader(loser.Context).Should().Be((null, null));

            await using var verify = new NodePilotDbContext(baseOptions);
            var persisted = await verify.AuthSessions.AsNoTracking().SingleAsync();
            var winningJwt = new JwtSecurityTokenHandler().ReadJwtToken(winner.Session.Token);
            persisted.CurrentJti.Should().Be(winningJwt.Id);
            persisted.RefreshGeneration.Should().Be(1);
            var revocation = await verify.RevokedTokens.AsNoTracking().SingleAsync();
            revocation.Jti.Should().Be(originalJwt.Id);
            revocation.Reason.Should().Be("rotated");
        }
        finally
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try { File.Delete(path); } catch { /* best-effort test cleanup */ }
            }
        }
    }
}
