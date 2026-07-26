using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodePilot.Api.Controllers;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Request validation and the read/observability side of
/// <see cref="ExternalIdentityResolutionController"/>. The happy-path merge semantics are in
/// <see cref="ExternalIdentityResolutionControllerTests"/>; this covers the guards that keep a
/// malformed or hostile resolve request from ever reaching the transaction, the identity
/// listing, and the audit forwarding into the support log.
/// </summary>
public sealed class ExternalIdentityResolutionValidationTests
{
    // ---------------------------------------------------------------- List

    [Fact]
    public async Task List_ProjectsIdentitiesTogetherWithTheirUserState()
    {
        using var db = TestDbFactory.Create();
        var user = ExternalUser(AuthProvider.Ldap, "S-1-5-21-1", UserRole.Operator);
        db.Add(user);
        db.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority,
            Subject = "S-1-5-21-1",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Controller(db).List(TestContext.Current.CancellationToken);

        var payload = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        json.Should().Contain("S-1-5-21-1");
        json.Should().Contain(user.Username, "the projection joins the user row for the admin UI");
    }

    [Fact]
    public async Task List_WithoutIdentities_ReturnsAnEmptyCollection()
    {
        using var db = TestDbFactory.Create();

        var result = await Controller(db).List(TestContext.Current.CancellationToken);

        var payload = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        System.Text.Json.JsonSerializer.Serialize(payload).Should().Be("[]");
    }

    // ---------------------------------------------------------------- request guards

    [Fact]
    public async Task ResolveUpgradeConflict_ProviderOtherThanLdapOrWindows_IsRejected()
    {
        using var db = TestDbFactory.Create();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Oidc, "subject", Guid.NewGuid(), [Guid.NewGuid()]),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>(
            "only the two pre-upgrade directory providers can produce this conflict");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveUpgradeConflict_BlankConflictExternalId_IsRejected(string conflictId)
    {
        using var db = TestDbFactory.Create();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Ldap, conflictId, Guid.NewGuid(), [Guid.NewGuid()]),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ResolveUpgradeConflict_OverlongConflictExternalId_IsRejected()
    {
        using var db = TestDbFactory.Create();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Ldap, new string('x', 257), Guid.NewGuid(), [Guid.NewGuid()]),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ResolveUpgradeConflict_EmptyLoserList_IsRejected()
    {
        using var db = TestDbFactory.Create();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Ldap, "subject", Guid.NewGuid(), []),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ResolveUpgradeConflict_WinnerListedAsItsOwnLoser_IsRejected()
    {
        using var db = TestDbFactory.Create();
        var winner = Guid.NewGuid();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Ldap, "subject", winner, [winner]),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>(
            "retiring the winner would delete the very account the merge preserves");
    }

    [Fact]
    public async Task ResolveUpgradeConflict_DuplicateLoserIds_AreRejected()
    {
        using var db = TestDbFactory.Create();
        var loser = Guid.NewGuid();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Ldap, "subject", Guid.NewGuid(), [loser, loser]),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ResolveUpgradeConflict_MoreThanOneHundredLosers_IsRejected()
    {
        using var db = TestDbFactory.Create();
        var losers = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();

        var result = await Controller(db).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(
                AuthProvider.Ldap, "subject", Guid.NewGuid(), losers),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---------------------------------------------------------------- audit forwarding + signalling

    [Fact]
    public async Task ResolveUpgradeConflict_ForwardsTheAuditEntryIntoTheSupportLog()
    {
        using var db = TestDbFactory.Create();
        const string sid = "S-1-5-21-4242";
        var winner = ExternalUser(AuthProvider.Windows, sid, UserRole.Operator);
        var loser = ExternalUser(AuthProvider.Windows, sid, UserRole.Viewer);
        db.AddRange(winner, loser, Admin());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var logger = new CapturingLogger();

        var result = await Controller(db, logger: logger).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(AuthProvider.Windows, sid, winner.Id, [loser.Id]),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
        logger.Messages.Should().Contain(message => message.Contains("AUDIT", StringComparison.Ordinal),
            "identity mutations are mirrored into the support log for SIEM pickup");
    }

    [Fact]
    public async Task ResolveUpgradeConflict_SignalsCancelledExecutionsToTheEngine()
    {
        using var db = TestDbFactory.Create();
        const string sid = "S-1-5-21-777";
        var winner = ExternalUser(AuthProvider.Windows, sid, UserRole.Operator);
        var loser = ExternalUser(AuthProvider.Windows, sid, UserRole.Viewer);
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        db.AddRange(winner, loser, Admin(), workflow, new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            StartedByUserId = loser.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var engine = new RecordingWorkflowEngine();

        var result = await Controller(db, engine: engine).ResolveUpgradeConflict(
            new ResolveUpgradeIdentityConflictRequest(AuthProvider.Windows, sid, winner.Id, [loser.Id]),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
        engine.Cancelled.Should().NotBeEmpty(
            "the engine must learn about the cancellation after the transaction commits");
    }

    // ---------------------------------------------------------------- helpers

    private static User ExternalUser(AuthProvider provider, string externalId, UserRole role) => new()
    {
        Id = Guid.NewGuid(),
        Username = $"{provider}-{Guid.NewGuid():N}@example.test",
        Provider = provider,
        ExternalId = externalId,
        PasswordHash = null,
        Role = role,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        PasswordChangedAt = DateTime.UtcNow,
    };

    /// <summary>Surviving local break-glass admin so the last-admin guards never trip.</summary>
    private static User Admin() => new()
    {
        Id = Guid.NewGuid(),
        Username = "recovery-admin",
        Provider = AuthProvider.Local,
        PasswordHash = "hash",
        Role = UserRole.Admin,
        IsActive = true,
        IsBreakGlass = true,
        CreatedAt = DateTime.UtcNow,
    };

    private static ExternalIdentityResolutionController Controller(
        NodePilotDbContext db,
        ILogger<ExternalIdentityResolutionController>? logger = null,
        IWorkflowEngine? engine = null)
    {
        var controller = new ExternalIdentityResolutionController(
            db,
            new AuditStager(),
            new MemoryCache(new MemoryCacheOptions()),
            engine,
            logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                            new Claim(ClaimTypes.Name, "sso-admin"),
                            new Claim(ClaimTypes.Role, "Admin"),
                        ],
                        "test")),
                },
            },
        };
        return controller;
    }

    private sealed class CapturingLogger : ILogger<ExternalIdentityResolutionController>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RecordingWorkflowEngine : IWorkflowEngine
    {
        public List<Guid> Cancelled { get; } = [];

        public Task<WorkflowExecution> ExecuteAsync(
            Workflow workflow, string triggeredBy, CancellationToken ct,
            Dictionary<string, string>? inputParameters = null,
            int? timeoutSeconds = null,
            bool debugEnabled = false,
            Guid? startedByUserId = null,
            Guid? parentExecutionId = null,
            int callDepth = 0,
            Guid? executionIdOverride = null,
            bool interactiveRun = false)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<bool> CancelAsync(
            Guid executionId, string? cancelledBy = null, CancellationToken ct = default)
        {
            Cancelled.Add(executionId);
            return Task.FromResult(true);
        }

        public bool Resume(
            Guid executionId, string stepId, DebugResumeCommand command,
            IReadOnlyDictionary<string, string>? overrides) => false;

        public IReadOnlyCollection<string> GetPausedSteps(Guid executionId) => [];
    }
}
