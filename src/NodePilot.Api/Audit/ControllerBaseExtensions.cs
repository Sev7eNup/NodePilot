using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace NodePilot.Api.Audit;

/// <summary>
/// Shared claim extractors for controllers, so a change to the claim names is made in one place.
/// </summary>
public static class ControllerBaseExtensions
{
    public static Guid? GetCurrentUserId(this ControllerBase c)
    {
        var raw = c.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static string? GetCurrentUsername(this ControllerBase c)
        => c.User?.FindFirstValue(ClaimTypes.Name);
}
