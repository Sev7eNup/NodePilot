using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.Scheduler.SystemAlerts;
using NodePilot.Scheduler.SystemAlerts.Sources;
using NodePilot.TestCommons;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Request validation of <see cref="SystemAlertingController"/>: scope/target matching and the
/// source-parameter contract. Both reject before anything is persisted — a policy that names a
/// bogus parameter or a target of the wrong kind would otherwise only fail later inside the
/// evaluator, where the operator no longer sees it.
/// <see cref="SystemAlertingControllerTests"/> covers the happy paths and the lifecycle.
/// </summary>
public sealed class SystemAlertingValidationTests : IDisposable
{
    private readonly NodePilotDbContext _db = TestDbFactory.Create();

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------- scope + targets

    [Fact]
    public async Task Create_WorkflowScopedWithoutTargets_IsRejected()
    {
        var result = await Controller().Create(
            WorkflowScoped(targets: []), TestContext.Current.CancellationToken);

        BadRequestMessage(result).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Create_WorkflowScopedWithNullTargets_IsRejected()
    {
        var result = await Controller().Create(
            WorkflowScoped(targets: null), TestContext.Current.CancellationToken);

        BadRequestMessage(result).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Create_UnparsableTargetKind_IsRejected()
    {
        var result = await Controller().Create(
            WorkflowScoped([new NotificationRuleTargetDto("NotAKind", Guid.NewGuid())]),
            TestContext.Current.CancellationToken);

        BadRequestMessage(result).Should().Contain("NotAKind");
    }

    [Fact]
    public async Task Create_TargetKindThatDoesNotMatchTheScope_IsRejected()
    {
        // execution-result is workflow-scoped, so a Folder target is a category error.
        var result = await Controller().Create(
            WorkflowScoped([new NotificationRuleTargetDto("Folder", Guid.NewGuid())]),
            TestContext.Current.CancellationToken);

        BadRequestMessage(result).Should().Contain("Workflow");
    }

    [Fact]
    public async Task Create_EmptyTargetId_IsRejected()
    {
        var result = await Controller().Create(
            WorkflowScoped([new NotificationRuleTargetDto("Workflow", Guid.Empty)]),
            TestContext.Current.CancellationToken);

        BadRequestMessage(result).Should().Contain("Target id");
    }

    [Fact]
    public async Task Create_ValidWorkflowTarget_IsAccepted()
    {
        var result = await Controller().Create(
            WorkflowScoped([new NotificationRuleTargetDto("Workflow", Guid.NewGuid())]),
            TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    // ---------------------------------------------------------------- source parameters

    [Fact]
    public async Task Create_UnknownSourceParameter_IsRejected()
    {
        var result = await Controller().Create(
            WorkflowScoped(Target(), parameters: new Dictionary<string, object?> { ["nope"] = 5 }),
            TestContext.Current.CancellationToken);

        BadRequestMessage(result).Should().Contain("nope");
    }

    [Fact]
    public async Task Create_NumericParameterBelowItsMinimum_IsRejected()
    {
        var result = await Controller().Create(
            WorkflowScoped(Target(), parameters: new Dictionary<string, object?> { ["lookbackSeconds"] = 0 }),
            TestContext.Current.CancellationToken);

        BadRequestMessage(result).Should().Contain("lookbackSeconds");
    }

    [Fact]
    public async Task Create_NonNumericValueForANumericParameter_IsRejected()
    {
        var result = await Controller().Create(
            WorkflowScoped(Target(), parameters: new Dictionary<string, object?> { ["lookbackSeconds"] = "soon" }),
            TestContext.Current.CancellationToken);

        BadRequestMessage(result).Should().Contain("number");
    }

    [Fact]
    public async Task Create_NumericParameterWithinRange_IsAccepted()
    {
        var result = await Controller().Create(
            WorkflowScoped(Target(), parameters: new Dictionary<string, object?> { ["lookbackSeconds"] = 600 }),
            TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_NumericParameterSentAsAJsonString_IsCoerced()
    {
        // The UI posts form values as strings; the coercion path must accept them.
        var result = await Controller().Create(
            WorkflowScoped(Target(), parameters: new Dictionary<string, object?>
            {
                ["lookbackSeconds"] = JsonDocument.Parse("\"600\"").RootElement.Clone(),
            }),
            TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_NullParameterValue_IsSkippedRatherThanValidated()
    {
        var result = await Controller().Create(
            WorkflowScoped(Target(), parameters: new Dictionary<string, object?> { ["lookbackSeconds"] = null }),
            TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<CreatedAtActionResult>(
            "an explicit null means 'use the default', not 'validate null as a number'");
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<NotificationRuleTargetDto> Target() =>
        [new NotificationRuleTargetDto("Workflow", Guid.NewGuid())];

    private static SaveSystemAlertPolicyRequest WorkflowScoped(
        IReadOnlyList<NotificationRuleTargetDto>? targets,
        IReadOnlyDictionary<string, object?>? parameters = null) => new(
        "exec-failures", null, true,
        "execution-result", null, parameters,
        SystemAlertConditions.Compare("status", "==", "Failed"), 0, null, "Workflows", targets,
        [new NotificationRouteDto(null, "Email", "ops@example.test", null, 0)], 0, 1, 0);

    /// <summary>Bad() returns an anonymous { message } payload — serialize so tests can read
    /// it.</summary>
    private static string BadRequestMessage(ActionResult<SystemAlertPolicyResponse> result)
    {
        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        return JsonSerializer.Serialize(bad.Value);
    }

    private SystemAlertingController Controller()
    {
        var catalog = new SystemAlertCatalog([new BacklogSource(), new ExecutionResultSource()]);
        var store = new NotificationRuleStore(_db, new AesGcmSecretProtector(Key()));
        return new SystemAlertingController(
            catalog, _db, store, NoopAuditWriter.Instance,
            [new NoopSink()], NullLogger<SystemAlertingController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Role, "Admin"), new Claim(ClaimTypes.Name, "admin")], "test")),
                },
            },
        };
    }

    private static byte[] Key()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 5);
        return key;
    }

    private sealed class NoopSink : INotificationSink
    {
        public NotificationChannel Channel => NotificationChannel.Email;

        public Task<NotificationSendResult> SendAsync(
            NotificationContext ctx, string target, string? secret, CancellationToken ct)
            => Task.FromResult(NotificationSendResult.Ok);
    }
}
