using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Audit;

public class AuditWriterTests
{
    private static (AuditWriter writer, Data.NodePilotDbContext db) Create(
        string? username = null, string? remoteIp = null, Guid? userId = null)
    {
        var db = TestDbFactory.Create();

        var httpContext = new DefaultHttpContext();
        if (username is not null || userId is not null)
        {
            var claims = new List<Claim>();
            if (username is not null) claims.Add(new Claim(ClaimTypes.Name, username));
            if (userId is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString()!));
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }
        if (remoteIp is not null)
            httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var writer = new AuditWriter(db, accessor, NullLogger<AuditWriter>.Instance);
        return (writer, db);
    }

    [Fact]
    public async Task LogAsync_PersistsEntryToDb()
    {
        var (writer, db) = Create("testuser");
        await writer.LogAsync("WORKFLOW_CREATED", "Workflow", Guid.NewGuid(), null, CancellationToken.None);
        db.AuditLog.Should().ContainSingle(e => e.Action == "WORKFLOW_CREATED");
    }

    [Fact]
    public async Task LogAsync_SetsAction_ResourceType_ResourceId()
    {
        var (writer, db) = Create();
        var resourceId = Guid.NewGuid();
        await writer.LogAsync("MACHINE_DELETED", "Machine", resourceId, null, CancellationToken.None);
        var entry = db.AuditLog.Single();
        entry.Action.Should().Be("MACHINE_DELETED");
        entry.ResourceType.Should().Be("Machine");
        entry.ResourceId.Should().Be(resourceId);
    }

    [Fact]
    public async Task LogAsync_PersistsUsername_AsColumn()
    {
        var (writer, db) = Create(username: "alice");
        await writer.LogAsync("LOGIN_SUCCESS", null, null, null, CancellationToken.None);
        var entry = db.AuditLog.Single();
        entry.Username.Should().Be("alice");
    }

    [Fact]
    public async Task LogAsync_PersistsRemoteIp_AsColumn()
    {
        var (writer, db) = Create(remoteIp: "192.168.1.42");
        await writer.LogAsync("LOGIN_FAILED", null, null, null, CancellationToken.None);
        var entry = db.AuditLog.Single();
        entry.IpAddress.Should().Be("192.168.1.42");
    }

    [Fact]
    public async Task LogAsync_DoesNotEmbedUsername_OrIp_InDetailsJson()
    {
        // The whole point of promoting Username/IpAddress to columns is so the Details blob
        // doesn't have to be parsed for the common UI/SIEM filters. Re-embedding them would
        // double the storage cost and re-introduce the "rename user → audit row is wrong"
        // failure mode the columns are meant to prevent.
        var (writer, db) = Create(username: "alice", remoteIp: "10.0.0.1");
        await writer.LogAsync("LOGIN_SUCCESS", null, null,
            "{\"viaSso\":true}", CancellationToken.None);
        var entry = db.AuditLog.Single();
        entry.Details.Should().NotContain("alice");
        entry.Details.Should().NotContain("10.0.0.1");
        entry.Details.Should().Contain("viaSso");
    }

    [Fact]
    public async Task LogAsync_WithCallerDetails_PassesThroughAsIs()
    {
        var (writer, db) = Create(username: "bob");
        await writer.LogAsync("CREDENTIAL_UPDATED", "Credential", null,
            "{\"passwordChanged\":true}", CancellationToken.None);
        var entry = db.AuditLog.Single();
        entry.Details.Should().Contain("passwordChanged");
        entry.Username.Should().Be("bob");
    }

    [Fact]
    public async Task LogAsync_NullDetails_NoContext_StoresNull()
    {
        var (writer, db) = Create();
        await writer.LogAsync("WORKFLOW_DELETED", "Workflow", null, null, CancellationToken.None);
        var entry = db.AuditLog.Single();
        entry.Details.Should().BeNull();
        entry.Username.Should().BeNull();
        entry.IpAddress.Should().BeNull();
    }

    [Fact]
    public async Task LogAsync_DbException_DoesNotThrow()
    {
        // Create the writer with a disposed db to simulate a write failure
        var db = TestDbFactory.Create();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var writer = new AuditWriter(db, accessor, NullLogger<AuditWriter>.Instance);
        db.Dispose();

        var act = async () => await writer.LogAsync("TEST_ACTION", null, null, null, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LogAsync_UserId_PersistsAsColumn()
    {
        var id = Guid.NewGuid();
        var (writer, db) = Create(username: "carol", userId: id);
        await writer.LogAsync("USER_CREATED", "User", null, null, CancellationToken.None);
        var entry = db.AuditLog.Single();
        entry.UserId.Should().Be(id);
    }

    [Fact]
    public async Task LogAsync_MultipleEntries_EachPersisted()
    {
        var (writer, db) = Create(username: "admin");
        await writer.LogAsync("WORKFLOW_CREATED", "Workflow", Guid.NewGuid(), null, CancellationToken.None);
        await writer.LogAsync("WORKFLOW_UPDATED", "Workflow", Guid.NewGuid(), null, CancellationToken.None);
        db.AuditLog.Should().HaveCount(2);
    }

    /// <summary>
    /// S1 (a SIEM-integration finding) — the SIEM forward must carry the rich audit context
    /// as structured properties, not just a 5-field message-template. The properties land in
    /// a BeginScope so EcsJsonFormatter projects them into ECS root fields (event.action,
    /// user.id, …).
    /// </summary>
    [Fact]
    public async Task LogAsync_SiemForward_IncludesEcsRootFields()
    {
        var captor = new ScopeCapturingLogger();
        var db = TestDbFactory.Create();
        var httpCtx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000abc"),
                new Claim(ClaimTypes.Name, "alice"),
            }, "test")),
        };
        httpCtx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");
        var writer = new AuditWriter(db, new HttpContextAccessor { HttpContext = httpCtx }, captor);

        await writer.LogAsync("WORKFLOW_PUBLISHED", "Workflow", Guid.NewGuid(),
            "{\"name\":\"Daily-Report\",\"version\":4}", CancellationToken.None);

        captor.LastScope.Should().NotBeNull("AuditWriter must wrap the SIEM-forward log call in BeginScope so structured fields land");
        var scope = captor.LastScope!;
        scope.Should().Contain(p => p.Key == "event.action" && (p.Value as string) == "WORKFLOW_PUBLISHED");
        scope.Should().Contain(p => p.Key == "event.category" && (p.Value as string) == "configuration");
        scope.Should().Contain(p => p.Key == "event.kind" && (p.Value as string) == "event");
        scope.Should().Contain(p => p.Key == "event.outcome" && (p.Value as string) == "success");
        scope.Should().Contain(p => p.Key == "user.id" && (p.Value as string) == "00000000-0000-0000-0000-000000000abc");
        scope.Should().Contain(p => p.Key == "user.name" && (p.Value as string) == "alice");
        scope.Should().Contain(p => p.Key == "source.ip" && (p.Value as string) == "10.1.2.3");
        scope.Should().Contain(p => p.Key == "event.id");
        scope.Should().Contain(p => p.Key == "event.original" && (p.Value as string)!.Contains("Daily-Report"),
            "the redacted details JSON must be forwarded so investigators can pivot from event.action to the workflow name without joining the AuditLog table");
    }

    [Fact]
    public void AuditEventForwarder_StagedBackgroundEntry_IncludesEcsFields()
    {
        var captor = new ScopeCapturingLogger();
        var entry = new AuditStager().Build(
            AuditActions.CredentialDecrypted,
            AuditActor.System,
            "Credential",
            Guid.NewGuid(),
            "{\"provider\":\"test\"}");

        AuditEventForwarder.ForwardCommitted(captor, entry);

        captor.LastScope.Should().Contain(p =>
            p.Key == "event.action" && (string?)p.Value == AuditActions.CredentialDecrypted);
        captor.LastScope.Should().Contain(p =>
            p.Key == "event.dataset" && (string?)p.Value == "nodepilot.audit");
        captor.LastScope.Should().Contain(p => p.Key == "event.id");
    }

    [Fact]
    public async Task LogAsync_SiemForward_IamCategoryForLoginEvents()
    {
        var captor = new ScopeCapturingLogger();
        var db = TestDbFactory.Create();
        var writer = new AuditWriter(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, captor);

        await writer.LogAsync("LOGIN_FAILED", null, null, null, CancellationToken.None);
        captor.LastScope.Should().Contain(p => p.Key == "event.category" && (p.Value as string) == "iam");

        await writer.LogAsync("CREDENTIAL_UPDATED", "Credential", Guid.NewGuid(), null, CancellationToken.None);
        captor.LastScope.Should().Contain(p => p.Key == "event.category" && (p.Value as string) == "iam");

        await writer.LogAsync("TOKEN_REFRESHED", "User", Guid.NewGuid(), null, CancellationToken.None);
        captor.LastScope.Should().Contain(p => p.Key == "event.category" && (p.Value as string) == "iam",
            "JWT rotation belongs in the iam category alongside LOGIN_SUCCESS/LOGIN_FAILED");

        await writer.LogAsync("EXECUTION_STARTED", "Execution", Guid.NewGuid(), null, CancellationToken.None);
        captor.LastScope.Should().Contain(p => p.Key == "event.category" && (p.Value as string) == "process");

        await writer.LogAsync("WORKFLOW_PUBLISHED", "Workflow", Guid.NewGuid(), null, CancellationToken.None);
        captor.LastScope.Should().Contain(p => p.Key == "event.category" && (p.Value as string) == "configuration");
    }

    /// <summary>
    /// The previous hardcoded <c>event.outcome=success</c> meant a SIEM rule
    /// <c>event.action=LOGIN_FAILED AND event.outcome=failure</c> (the Sigma / Sentinel
    /// standard for brute-force detection) never matched NodePilot. This test pins the
    /// suffix-driven outcome mapping so a future refactor that goes back to "success"
    /// for everything breaks loudly.
    /// </summary>
    [Theory]
    [InlineData("LOGIN_FAILED", "failure")]
    [InlineData("LOGIN_LOCKED", "failure")]
    [InlineData("LOGIN_SUCCESS", "success")]
    [InlineData("TRIGGER_FIRE_SUPPRESSED", "failure")]
    [InlineData("USER_LDAP_REFUSED_COLLISION", "failure")]
    [InlineData("WORKFLOW_LOCKED", "success")]     // edit-lock checkout, NOT account lockout
    [InlineData("WORKFLOW_UNLOCKED", "success")]
    [InlineData("WORKFLOW_PUBLISHED", "success")]
    [InlineData("USER_CREATED", "success")]
    [InlineData("USER_ROLE_CHANGED", "success")]
    [InlineData("CREDENTIAL_DECRYPTED", "success")]
    [InlineData("TOKEN_REFRESHED", "success")]
    public async Task LogAsync_SiemForward_OutcomeDerivedFromActionSuffix(string action, string expectedOutcome)
    {
        var captor = new ScopeCapturingLogger();
        var db = TestDbFactory.Create();
        var writer = new AuditWriter(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, captor);

        await writer.LogAsync(action, null, null, null, CancellationToken.None);

        captor.LastScope.Should().Contain(p => p.Key == "event.outcome" && (p.Value as string) == expectedOutcome,
            $"action '{action}' must map to event.outcome={expectedOutcome}");
    }

    /// <summary>
    /// Pin: AuditWriter sets the discriminator <c>support.event_type=AUDIT</c> in the scope
    /// for every audit event — the SupportEventDbSink projects that into the EventType
    /// column so it can be filtered efficiently.
    /// </summary>
    [Fact]
    public async Task LogAsync_SetsSupportEventTypeAudit_ForAnyAction()
    {
        var captor = new ScopeCapturingLogger();
        var db = TestDbFactory.Create();
        var writer = new AuditWriter(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, captor);

        await writer.LogAsync("WORKFLOW_CREATED", null, null, null, CancellationToken.None);

        captor.LastScope.Should().Contain(p => p.Key == "support.event_type" && (p.Value as string) == "AUDIT");
    }

    /// <summary>
    /// Support-log whitelist: every action in the hardcoded list inside
    /// <see cref="AuditWriter"/> must write <c>SupportLog=true</c> into the scope. The
    /// second Serilog sink filters on exactly that property, so this test pins the behavior
    /// against an action accidentally being dropped from the list.
    /// </summary>
    [Theory]
    [InlineData("LOGIN_SUCCESS")]
    [InlineData("LOGIN_FAILED")]
    [InlineData("LOGIN_LOCKED")]
    [InlineData("LOGOUT")]
    [InlineData("USER_CREATED")]
    [InlineData("USER_DELETED")]
    [InlineData("USER_ROLE_CHANGED")]
    [InlineData("USER_PASSWORD_RESET")]
    [InlineData("WORKFLOW_PUBLISHED")]
    [InlineData("WORKFLOW_DELETED")]
    [InlineData("WORKFLOW_FORCE_UNLOCKED")]
    [InlineData("EXTERNAL_TRIGGER_FIRED")]
    [InlineData("WEBHOOK_TRIGGERED")]
    [InlineData("TRIGGER_FIRE_SUPPRESSED")]
    [InlineData("SECRETS_REENCRYPTED")]
    public async Task LogAsync_SupportLog_WhitelistedActions_SetsFlag(string action)
    {
        var captor = new ScopeCapturingLogger();
        var db = TestDbFactory.Create();
        var writer = new AuditWriter(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, captor);

        await writer.LogAsync(action, null, null, null, CancellationToken.None);

        captor.LastScope.Should().Contain(p => p.Key == "SupportLog" && (bool)(p.Value ?? false),
            $"action '{action}' is on the support-log whitelist and must carry SupportLog=true");
    }

    /// <summary>
    /// Outcome fallthrough: actions that are NOT on the whitelist but are classified with
    /// <c>event.outcome=failure</c> must still be mirrored into the support log — otherwise
    /// things like brute-force decryption attempts or trigger rejections would go missing
    /// from the file.
    /// </summary>
    [Theory]
    [InlineData("CREDENTIAL_DECRYPT_FAILED")]
    [InlineData("WHATEVER_REJECTED")]
    [InlineData("SOMETHING_SUPPRESSED")]
    public async Task LogAsync_SupportLog_NonWhitelistedFailure_StillSetsFlag(string action)
    {
        var captor = new ScopeCapturingLogger();
        var db = TestDbFactory.Create();
        var writer = new AuditWriter(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, captor);

        await writer.LogAsync(action, null, null, null, CancellationToken.None);

        captor.LastScope.Should().Contain(p => p.Key == "SupportLog" && (bool)(p.Value ?? false),
            $"failure-outcome action '{action}' must mirror to support-log even outside the whitelist");
    }

    /// <summary>
    /// Negative pin: high-frequency actions that are deliberately excluded from the support
    /// log (token refresh every 12h per user, editor churn, every single credential
    /// decryption).
    /// </summary>
    [Theory]
    [InlineData("TOKEN_REFRESHED")]
    [InlineData("CREDENTIAL_DECRYPTED")]
    [InlineData("WORKFLOW_CREATED")]
    [InlineData("WORKFLOW_UPDATED")]
    [InlineData("WORKFLOW_LOCKED")]
    [InlineData("WORKFLOW_UNLOCKED")]
    [InlineData("EXECUTION_STARTED")]
    public async Task LogAsync_SupportLog_NonWhitelistedSuccess_FlagIsFalse(string action)
    {
        var captor = new ScopeCapturingLogger();
        var db = TestDbFactory.Create();
        var writer = new AuditWriter(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, captor);

        await writer.LogAsync(action, null, null, null, CancellationToken.None);

        captor.LastScope.Should().Contain(p => p.Key == "SupportLog" && !(bool)(p.Value ?? true),
            $"action '{action}' is intentionally excluded from the support-log; flag must be false");
    }

    /// <summary>Tiny ILogger that snapshots the most recent BeginScope state.</summary>
    private sealed class ScopeCapturingLogger : Microsoft.Extensions.Logging.ILogger<AuditWriter>
    {
        public IReadOnlyDictionary<string, object?>? LastScope { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvs)
                LastScope = kvs.ToDictionary(k => k.Key, k => k.Value);
            return Disposable.Instance;
        }
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }

        private sealed class Disposable : IDisposable
        {
            public static readonly Disposable Instance = new();
            public void Dispose() { }
        }
    }
}
