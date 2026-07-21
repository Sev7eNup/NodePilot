using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace NodePilot.Api.Audit;

/// <summary>
/// Shared claim extractors. Controllers previously re-implemented
/// <c>User.FindFirstValue(ClaimTypes.NameIdentifier)</c> in 4 places — consolidated here so
/// a single upstream change to claim names doesn't require a grep-and-replace campaign.
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
