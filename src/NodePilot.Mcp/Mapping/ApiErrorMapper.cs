using System.Text;
using ModelContextProtocol;
using NodePilot.Core.Clients;
using NodePilot.Mcp.Api;

namespace NodePilot.Mcp.Mapping;

/// <summary>
/// Turns NodePilot API failures into actionable MCP tool errors. Centralises the
/// status-code -> guidance mapping so every tool reports failures consistently.
/// </summary>
public static class ApiErrorMapper
{
    /// <summary>Run an API call, translating <see cref="ApiException"/>/<see
    /// cref="NotConfiguredException"/>
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
            throw new McpException(DescribeTransport(ex));
        }
        catch (TaskCanceledException)
        {
            throw new McpException("The NodePilot API request timed out.");
        }
    }

    public static async Task Guard(Func<Task> call)
        => await Guard(async () => { await call(); return true; });

    /// <summary>
    /// Unwraps the exception chain — the outer message of a handshake failure says nothing — and
    /// names the certificate the server presented, when this request observed one.
    /// </summary>
    private static string DescribeTransport(HttpRequestException ex)
    {
        var info = NetworkFailureAnalyzer.Analyze(ex);
        var text = new StringBuilder("Cannot reach the NodePilot API: ");
        text.Append(string.Join(" -> ", info.CauseChain));

        if (info.Certificate is { HasCertificate: true } certificate)
        {
            text.Append($" Presented certificate: {certificate.Subject}, issuer {certificate.Issuer}");
            if (certificate.DnsNames.Count > 0) text.Append($", DNS {string.Join("/", certificate.DnsNames)}");
            text.Append($", SHA-256 {certificate.Sha256}.");
            text.Append(info.Tls == TlsFailureKind.PinMismatch
                ? " The configured pin does not match; check why the certificate changed before updating it."
                : @" Trust it by importing it into LocalMachine\Root, or pin it with"
                  + " NODEPILOT_MCP_TLS_THUMBPRINT=<SHA-256> in the .mcp.json env block.");
        }

        return text.ToString();
    }

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
