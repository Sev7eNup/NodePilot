using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace NodePilot.Api.Tests.Rbac;

/// <summary>
/// Reflection guard against silent permission-bypasses. The integration test suite covers
/// the documented endpoints, but a future endpoint added later might forget to call
/// <c>RequireWorkflowAccessAsync</c> / <c>_authz.*</c> and slip through review. This guard
/// scans every public action method on every <c>*Controller</c> in the API assembly and
/// asserts that any method whose body touches <c>_db.Workflows</c> or
/// <c>_db.WorkflowExecutions</c> also references the authorization service somewhere in
/// the same method body.
/// <para>
/// The check is conservative: it cannot prove correctness (a method might call the authz
/// service for a different reason), but it does fail loudly when a method bypasses the
/// service entirely. Combined with the integration tests this is enough to catch the
/// "added a new endpoint, forgot the gate" class of regressions.
/// </para>
/// </summary>
public class RbacControllerCoverageGuardTests
{
    /// <summary>
    /// Controllers that are intentionally exempt from the guard. Each entry needs an
    /// explanation — the guard is here to be loud, not silent.
    /// </summary>
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.Ordinal)
    {
        // Auth + bootstrap endpoints — operate on Users, not Workflows.
        "AuthController",
        // Admin-only DB tooling — gated by [Authorize(Roles="Admin")] which bypasses folder ACLs.
        "DbAdminController",
        // Audit log — Admin-only.
        "AuditController",
        // Webhook + external trigger surfaces — auth via shared secret / API key, not user RBAC.
        // Persistence path goes through ExecutionDispatchService which inherits the workflow
        // permissions from the run record, so the trigger entry-point itself doesn't gate.
        "WebhooksController",
        "ExternalTriggerController",
        // CRUD for global resources (machines, credentials, globals, users) — V1 leaves these
        // global-flat (not foldered). Roadmap stage A.2 may fold them later.
        "MachinesController",
        "CredentialsController",
        "GlobalVariablesController",
        "UsersController",
        // Per-user UI state (bookmarks/folders) — not workflow RBAC.
        "WorkflowFoldersController",
        // Observability / metadata endpoints that don't read Workflows table directly.
        "ObservabilityController",
        // AI helper endpoints — guarded by their own [Authorize(Roles="Admin,Operator")] +
        // explicit workflow-fetch with permission check inline.
        "AiController",
        // Cluster / ScOrch / Health — no workflow-row reads.
        "ScorchController",
    };

    [Fact]
    public void EveryControllerActionTouchingWorkflowsOrExecutions_ReferencesAuthorizationService()
    {
        var apiAssembly = typeof(NodePilot.Api.Controllers.WorkflowsController).Assembly;
        var controllerTypes = apiAssembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .Where(t => !t.IsAbstract)
            .Where(t => !ExemptControllers.Contains(t.Name))
            .ToList();

        var violations = new List<string>();

        foreach (var controllerType in controllerTypes)
        {
            // Public action methods only — internal helpers don't matter for the guard.
            var actions = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().Any(a =>
                    a.GetType().Name.StartsWith("Http", StringComparison.Ordinal) ||
                    a.GetType().Name == "RouteAttribute"));

            foreach (var action in actions)
            {
                var body = ReadMethodBodyAsString(action);
                if (body is null) continue;
                bool touchesWorkflowTables =
                    body.Contains("Workflows") || body.Contains("WorkflowExecutions");
                bool referencesAuthz =
                    body.Contains("_authz") ||
                    body.Contains("RequireWorkflowAccess") ||
                    body.Contains("RequireFolderAccess") ||
                    body.Contains("CanAccessWorkflow") ||
                    body.Contains("CanAccessFolder") ||
                    body.Contains("ApplyExecutionAccessFilter") ||
                    body.Contains("GetAccessibleFolderIds");

                if (touchesWorkflowTables && !referencesAuthz)
                {
                    violations.Add($"{controllerType.Name}.{action.Name}");
                }
            }
        }

        violations.Should().BeEmpty(
            "every action that reads Workflows or WorkflowExecutions must consult the authorization service. " +
            "If an exception is genuinely needed, add the controller name to ExemptControllers with a comment.");
    }

    /// <summary>
    /// Decompile-free body inspection: read the method's IL byte array and keyword-search
    /// the constant strings. This is fragile vs. real static analysis but cheap and good
    /// enough for the "did somebody forget the gate" question.
    /// </summary>
    private static string? ReadMethodBodyAsString(MethodInfo method)
    {
        // Async action bodies live in the compiler-generated state machine, while the
        // public method only constructs it. Inspect MoveNext or the guard silently sees
        // neither DbSet access nor authorization calls for nearly every real controller.
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } asyncStateMachine)
        {
            method = asyncStateMachine.StateMachineType.GetMethod(
                "MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? method;
        }

        var module = method.Module;
        var body = method.GetMethodBody();
        if (body is null) return null;
        var ilBytes = body.GetILAsByteArray();
        if (ilBytes is null) return null;

        // Look at every ldstr instruction (opcode 0x72) and collect the constant strings
        // it references — those are the literal field-access paths and method names that
        // appear in the source code.
        var collected = new System.Text.StringBuilder();
        for (var i = 0; i < ilBytes.Length;)
        {
            if (ilBytes[i] == 0x72 && i + 4 < ilBytes.Length)  // ldstr <token>
            {
                var token = BitConverter.ToInt32(ilBytes, i + 1);
                try
                {
                    collected.Append(module.ResolveString(token));
                    collected.Append(' ');
                }
                catch { /* malformed token — skip */ }
                i += 5;
            }
            else
            {
                i++;
            }
        }

        // Also harvest field/method names referenced by the IL (call/callvirt/ldfld/...).
        // Quick scan over common opcodes that take a metadata token.
        for (var i = 0; i < ilBytes.Length;)
        {
            byte op = ilBytes[i];
            int tokenOffset = -1;
            if (op == 0x28 || op == 0x6F || op == 0x73 || op == 0x7A) tokenOffset = i + 1;  // call/callvirt/newobj/throw
            else if (op == 0x7B || op == 0x7E || op == 0x7C) tokenOffset = i + 1;  // ldfld/ldsfld/ldflda
            if (tokenOffset > 0 && tokenOffset + 4 <= ilBytes.Length)
            {
                var token = BitConverter.ToInt32(ilBytes, tokenOffset);
                try
                {
                    var member = module.ResolveMember(token);
                    if (member is not null)
                    {
                        collected.Append(member.Name);
                        collected.Append(' ');
                        if (member.DeclaringType is not null)
                        {
                            collected.Append(member.DeclaringType.Name);
                            collected.Append(' ');
                        }
                    }
                }
                catch { /* tokens for generic-instantiations or non-resolvable refs — skip */ }
                i += 5;
            }
            else
            {
                i++;
            }
        }

        return collected.ToString();
    }
}
