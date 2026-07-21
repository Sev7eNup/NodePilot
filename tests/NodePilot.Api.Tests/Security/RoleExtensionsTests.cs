using System.Security.Claims;
using FluentAssertions;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Centralised role-membership helpers gate every privileged action in the API —
/// regression tests here keep the canonical role-name strings ("Admin"/"Operator"/
/// "Viewer") pinned so a typo can't silently strip authorization checks.
/// </summary>
public class RoleExtensionsTests
{
    private static ClaimsPrincipal Principal(params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToArray();
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void IsAdmin_AdminRole_True()
    {
        Principal("Admin").IsAdmin().Should().BeTrue();
    }

    [Fact]
    public void IsAdmin_OperatorRole_False()
    {
        Principal("Operator").IsAdmin().Should().BeFalse();
    }

    [Fact]
    public void IsAdmin_ViewerRole_False()
    {
        Principal("Viewer").IsAdmin().Should().BeFalse();
    }

    [Fact]
    public void IsAdmin_Anonymous_False()
    {
        new ClaimsPrincipal(new ClaimsIdentity()).IsAdmin().Should().BeFalse();
    }

    [Fact]
    public void IsOperator_OperatorRole_True()
    {
        Principal("Operator").IsOperator().Should().BeTrue();
    }

    [Fact]
    public void IsOperator_AdminRole_False()
    {
        // IsOperator is strictly Operator — not "is privileged". Admin-only logic
        // and "Operator and above" logic are different things; this test pins the
        // strict semantic so a refactor doesn't relax it.
        Principal("Admin").IsOperator().Should().BeFalse();
    }

    [Fact]
    public void IsPrivileged_Admin_True() => Principal("Admin").IsPrivileged().Should().BeTrue();

    [Fact]
    public void IsPrivileged_Operator_True() => Principal("Operator").IsPrivileged().Should().BeTrue();

    [Fact]
    public void IsPrivileged_Viewer_False() => Principal("Viewer").IsPrivileged().Should().BeFalse();

    [Fact]
    public void IsPrivileged_Anonymous_False()
    {
        new ClaimsPrincipal(new ClaimsIdentity()).IsPrivileged().Should().BeFalse();
    }

    [Fact]
    public void IsPrivileged_AdminAndOperator_True()
    {
        // Defensive: a user could have both roles claimed if the JWT was minted with
        // multiple role-claims (unusual but legitimate).
        Principal("Admin", "Operator").IsPrivileged().Should().BeTrue();
    }

    [Fact]
    public void RoleName_CaseSensitive_DocumentsAspNetBehavior()
    {
        // ClaimsPrincipal.IsInRole is case-sensitive by default. If somebody flips this
        // to a case-insensitive match (intentional or not), this test will catch it.
        Principal("admin").IsAdmin().Should().BeFalse(
            "ASP.NET role membership is case-sensitive — lower-case 'admin' must not match");
    }
}
