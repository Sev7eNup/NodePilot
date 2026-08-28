using System.Security.Claims;
using FluentAssertions;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Tests for the role-membership helpers that gate every privileged action in the API.
/// They pin the canonical role-name strings ("Admin"/"Operator"/"Viewer") so a typo
/// cannot silently disable an authorization check.
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
        // A user can have both roles claimed if the JWT was minted with multiple
        // role claims. Unusual but legitimate.
        Principal("Admin", "Operator").IsPrivileged().Should().BeTrue();
    }

    [Fact]
    public void RoleName_CaseSensitive_DocumentsAspNetBehavior()
    {
        // ClaimsPrincipal.IsInRole is case-sensitive by default. This test catches any
        // change to case-insensitive matching, intentional or not.
        Principal("admin").IsAdmin().Should().BeFalse(
            "ASP.NET role membership is case-sensitive — lower-case 'admin' must not match");
    }
}
