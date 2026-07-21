using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Shared instantiation helper for the three controllers in the workflow family
/// (<see cref="WorkflowsController"/>, <see cref="WorkflowEditingController"/>,
/// <see cref="WorkflowImportExportController"/>). All three share the same
/// <see cref="ControllerContext"/> + <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// so tests don't need to duplicate the auth-claims wiring per call site.
/// </summary>
internal sealed record WorkflowControllerHarness(
    WorkflowsController Workflows,
    WorkflowEditingController Editing,
    WorkflowImportExportController ImportExport);

internal static class WorkflowControllerHarnessFactory
{
    public static WorkflowControllerHarness Build(
        NodePilotDbContext db,
        IAuditWriter? audit = null,
        string role = "Admin",
        Guid? userId = null,
        IResourceAuthorizationService? authz = null)
    {
        audit ??= new CapturingAuditWriter();
        var effectiveUserId = userId ?? Guid.Parse("00000000-0000-0000-0000-000000000001");

        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[]
                {
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, effectiveUserId.ToString()),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, $"test-{role.ToLowerInvariant()}"),
                },
                "TestAuth"));

        ControllerContext NewCtx() => new()
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // RBAC harness: in test mode the harness wires a permissive AlwaysAllowAuthz so
        // existing controller tests keep passing without each test having to seed folder
        // permissions for its principal. Tests that specifically exercise RBAC denial use
        // the dedicated RBAC test fixtures instead of this harness.
        authz ??= new AlwaysAllowAuthorizationService();
        var workflows = new WorkflowsController(
            db, NullLogger<WorkflowsController>.Instance, audit, authz,
            new NodePilot.Api.Services.WorkflowContractDeriver())
        {
            ControllerContext = NewCtx()
        };
        var editing = new WorkflowEditingController(
            db, NullLogger<WorkflowEditingController>.Instance, audit, authz,
            Mock.Of<IStepTester>(), Mock.Of<IStepTestContextProvider>())
        {
            ControllerContext = NewCtx()
        };
        var importExport = new WorkflowImportExportController(
            db, NullLogger<WorkflowImportExportController>.Instance, audit, authz,
            new NodePilot.Data.GlobalVariableStore(db, new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser)))
        {
            ControllerContext = NewCtx()
        };

        return new WorkflowControllerHarness(workflows, editing, importExport);
    }
}
