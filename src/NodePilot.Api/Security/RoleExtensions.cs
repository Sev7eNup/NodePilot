using System.Security.Claims;

namespace NodePilot.Api.Security;

/// <summary>
/// Role-membership helpers for controllers. Role strings are the canonical "Admin" /
/// "Operator" / "Viewer" names written into the JWT by <see cref="Controllers.AuthController"/>;
/// keeping the checks here centralises the role-name source of truth.
/// </summary>
internal static class RoleExtensions
{
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole("Admin");

    public static bool IsOperator(this ClaimsPrincipal user) => user.IsInRole("Operator");

    /// <summary>Admin or Operator — i.e. can mutate / see raw secrets.</summary>
    public static bool IsPrivileged(this ClaimsPrincipal user)
        => user.IsInRole("Admin") || user.IsInRole("Operator");
}
