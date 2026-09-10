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
            AppendRemedy(text, info, certificate);
        }

        return text.ToString();
    }

    /// <summary>
    /// Names only what addresses the diagnosed cause. A root import fixes neither a name mismatch
    /// nor an expired certificate, and pointing the agent at either wastes a round trip.
    /// </summary>
    private static void AppendRemedy(
        StringBuilder text, NetworkFailureInfo info, PresentedCertificateInfo certificate)
    {
        if (info.Tls == TlsFailureKind.PinMismatch)
        {
            text.Append(
                " The configured pin does not match; check why the certificate changed before updating it.");
            return;
        }

        if (info.HasNameMismatch)
        {
            text.Append(certificate.SuggestedServerUrl is { } url
                ? $" The request host is not among the certificate's names. Point the server at one it"
                  + $" carries: NODEPILOT_MCP_SERVER={url} in the .mcp.json env block."
                : " The request host is not among the certificate's names, and the certificate names no"
                  + " host a client could dial; reissue it for the name the server is reached under.");
            text.Append(@" A LocalMachine\Root import does not fix a name mismatch.");
        }

        switch (info.Tls)
        {
            case TlsFailureKind.UntrustedChain or TlsFailureKind.Unknown:
                text.Append(@" Trust it by importing it into LocalMachine\Root, or pin it with"
                    + " NODEPILOT_MCP_TLS_THUMBPRINT=<SHA-256> in the .mcp.json env block.");
                break;
            case TlsFailureKind.Expired:
                text.Append(" The certificate is outside its validity window; it has to be renewed.");
                break;
        }
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
