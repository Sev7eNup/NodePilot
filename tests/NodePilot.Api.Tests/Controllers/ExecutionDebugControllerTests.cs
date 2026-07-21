using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Coverage for the step-debugger surface (<see cref="ExecutionDebugController"/>): resume
/// validation + mode mapping + RBAC/ownership gating, and the paused-steps read fallback.
/// The engine is mocked (<see cref="IWorkflowEngine"/>); the DB is in-memory SQLite.
/// </summary>
public class ExecutionDebugControllerTests
{
    private readonly Guid _ownerId = Guid.NewGuid();

    private sealed record Harness(
        ExecutionDebugController Ctrl, Mock<IWorkflowEngine> Engine, CapturingAuditWriter Audit, Guid ExecutionId);

    // Authz stub that says yes to Read but no to Run — for the "insufficient run permission" 403 path.
    private sealed class ReadOnlyAuthz : IResourceAuthorizationService
    {
        public Task<bool> CanAccessWorkflowAsync(ClaimsPrincipal u, Guid f, ResourceOp op, CancellationToken ct = default)
            => Task.FromResult(op == ResourceOp.Read);
        public Task<bool> CanAccessFolderAsync(ClaimsPrincipal u, Guid f, ResourceOp op, CancellationToken ct = default)
            => Task.FromResult(op == ResourceOp.Read);
        public Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(ClaimsPrincipal u, CancellationToken ct = default)
            => Task.FromResult(AccessibleFolderSet.Unrestricted);
        public Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(ClaimsPrincipal u, Guid f, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.None);
        public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(ClaimsPrincipal u, Guid f, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.None);
        public Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(ClaimsPrincipal u, Guid f, CancellationToken ct = default)
            => Task.FromResult<SharedFolderRole?>(null);
        public void InvalidateAll() { }
    }

    private async Task<Harness> BuildAsync(
        NodePilotDbContext db, IResourceAuthorizationService? authz = null,
        string role = "Admin", Guid? callerId = null, Guid? startedBy = null)
    {
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Paused,
            StartedByUserId = startedBy ?? _ownerId,
        };
        db.Workflows.Add(wf);
        db.WorkflowExecutions.Add(exec);
        await db.SaveChangesAsync();

        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(e => e.Resume(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DebugResumeCommand>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>())).Returns(true);
        engine.Setup(e => e.GetPausedSteps(It.IsAny<Guid>())).Returns(new[] { "step-1", "step-2" });

        var audit = new CapturingAuditWriter();
        var ctrl = new ExecutionDebugController(db, engine.Object, audit, authz ?? new AlwaysAllowAuthorizationService());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, (callerId ?? _ownerId).ToString())], "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return new Harness(ctrl, engine, audit, exec.Id);
    }

    [Theory]
    [InlineData("continue", "EXECUTION_RESUMED")]
    [InlineData("stepOver", "EXECUTION_STEP_OVER")]
    [InlineData("step-over", "EXECUTION_STEP_OVER")]
    [InlineData("stop", "EXECUTION_DEBUG_STOP")]
    public async Task Resume_ValidMode_SignalsEngine_AuditsAndReturnsNoContent(string mode, string expectedAudit)
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);

        var result = await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("step-1", mode, null), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        h.Engine.Verify(e => e.Resume(h.ExecutionId, "step-1", It.IsAny<DebugResumeCommand>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Once);
        h.Audit.Calls.Should().ContainSingle().Which.Action.Should().Be(expectedAudit);
    }

    [Fact]
    public async Task Resume_NullBody_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);
        (await h.Ctrl.Resume(h.ExecutionId, null!, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Resume_UnknownExecution_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);
        (await h.Ctrl.Resume(Guid.NewGuid(), new ResumeDebugRequest("s", "continue", null), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Resume_MissingStepId_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);
        (await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("  ", "continue", null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Resume_InvalidMode_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);
        (await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("s", "teleport", null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Resume_TooManyOverrides_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);
        var overrides = Enumerable.Range(0, 257).ToDictionary(i => $"k{i}", _ => "v");
        (await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("s", "continue", overrides), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Resume_OverrideValueTooLarge_Returns400()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);
        var overrides = new Dictionary<string, string> { ["big"] = new string('x', 65_537) };
        (await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("s", "continue", overrides), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Resume_EngineHasNoPausedStep_Returns409()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);
        h.Engine.Setup(e => e.Resume(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DebugResumeCommand>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>())).Returns(false);

        (await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("s", "continue", null), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Resume_NonOwnerNonAdmin_Returns403()
    {
        await using var db = TestDbFactory.Create();
        // Run started by _ownerId; caller is a different Operator → ownership check forbids.
        var h = await BuildAsync(db, role: "Operator", callerId: Guid.NewGuid(), startedBy: _ownerId);

        (await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("s", "continue", null), CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Resume_ReadAllowedRunDenied_Returns403()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db, authz: new ReadOnlyAuthz());

        var result = await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("s", "continue", null), CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Resume_ReadDenied_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db, authz: new RestrictedAuthorizationService(AccessibleFolderSet.None));

        (await h.Ctrl.Resume(h.ExecutionId, new ResumeDebugRequest("s", "continue", null), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetPausedSteps_ReturnsEngineList()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);

        var result = await h.Ctrl.GetPausedSteps(h.ExecutionId, CancellationToken.None);
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ((IEnumerable<string>)ok.Value!).Should().BeEquivalentTo(["step-1", "step-2"]);
    }

    [Fact]
    public async Task GetPausedSteps_UnknownExecution_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db);
        (await h.Ctrl.GetPausedSteps(Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetPausedSteps_ReadDenied_Returns404()
    {
        await using var db = TestDbFactory.Create();
        var h = await BuildAsync(db, authz: new RestrictedAuthorizationService(AccessibleFolderSet.None));
        (await h.Ctrl.GetPausedSteps(h.ExecutionId, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }
}
