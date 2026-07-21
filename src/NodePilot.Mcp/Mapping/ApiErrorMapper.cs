using ModelContextProtocol;
using NodePilot.Mcp.Api;

namespace NodePilot.Mcp.Mapping;

/// <summary>
/// Turns NodePilot API failures into actionable MCP tool errors. Centralises the
/// status-code → guidance mapping so every tool reports failures consistently.
/// </summary>
public static class ApiErrorMapper
{
    /// <summary>Run an API call, translating <see cref="ApiException"/>/<see cref="NotConfiguredException"/>
    /// into an <see cref="McpException"/> with a clear, role-aware message.</summary>
    public static async Task<T> Guard<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (NotConfiguredException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (ApiException ex)
        {
            throw new McpException(Describe(ex));
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Cannot reach the NodePilot API: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            throw new McpException("The NodePilot API request timed out.");
        }
    }

    public static async Task Guard(Func<Task> call)
        => await Guard(async () => { await call(); return true; });

    private static string Describe(ApiException ex)
    {
        if (ex.IsUnauthorized)
            return "Not authenticated (401). Run `np auth login` (the MCP server reuses the CLI session), or set NODEPILOT_MCP_TOKEN.";
        if (ex.IsForbidden)
            return $"Permission denied (403): your role lacks rights for this action. {ex.Detail ?? ex.Title}".TrimEnd();
        if (ex.IsLocked)
            return $"Workflow is checked out by another user (423). {ex.Detail ?? "Use force_unlock_workflow (Admin) if you must break the lock."}";
        if (ex.IsConflict)
            return $"Conflict (409): {ex.Detail ?? ex.Title ?? "the resource is already in the target state or was modified concurrently."}";
        if (ex.IsNotFound)
            return $"Not found (404): {ex.Detail ?? ex.Title ?? "no such resource."}";
        return ex.Message;
    }
}
