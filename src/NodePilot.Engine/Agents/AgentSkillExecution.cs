using System.Text.Json;
using NodePilot.Core.Agents;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Agents;

internal static class AgentSkillExecution
{
    internal static string RequireSuccess(string output)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(output); }
        catch (JsonException) { throw new AgentToolExecutionException("invalid_process_result", "The target did not return valid process JSON."); }
        using var ownedDocument = document;
        var result = document.RootElement;
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("timedOut", out var timeout) || timeout.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !result.TryGetProperty("exitCode", out var exit))
            throw new AgentToolExecutionException("invalid_process_result", "The target did not return a process status.");
        if (timeout.GetBoolean())
            throw new AgentToolExecutionException("process_timeout", "The skill process exceeded its time limit. Do not assume its task completed.", result);
        if (exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var code) || code != 0)
            throw new AgentToolExecutionException("process_failed", "The skill process failed. Inspect stderr and exitCode; output may be incomplete.", result);
        return output;
    }

    internal static async Task CheckPowerShellPolicyAsync(AgentTarget target, CancellationToken ct)
    {
        var output = RequireSuccess(await target.ExecuteAsync(AgentProcessScript.Build("powershell",
            "(Microsoft.PowerShell.Security\\Get-ExecutionPolicy).ToString() | Microsoft.PowerShell.Utility\\ConvertTo-Json -Compress", null, null, 15), ct, 20));
        using var process = JsonDocument.Parse(output);
        using var policy = JsonDocument.Parse(process.RootElement.GetProperty("stdout").GetString()!);
        ValidatePolicy(policy.RootElement.GetString());
    }

    private static void ValidatePolicy(string? policy)
    {
        if (policy == "Restricted")
            throw new AgentToolExecutionException("execution_policy_blocked",
                "The target uses Restricted and does not permit script files, including signed scripts. An administrator must configure script execution separately. Do not retry this script as inline code or with another shell.");
        if (policy is not ("AllSigned" or "RemoteSigned" or "Unrestricted" or "Bypass"))
            throw new AgentToolExecutionException("execution_policy_unknown", "The target execution policy could not be determined.");
    }

    internal static async Task VerifyPowerShellFileAsync(AgentTarget target, string path, string sha256, CancellationToken ct)
    {
        var command = $$"""
            $ErrorActionPreference='Stop'
            $path={{PowerShellOperation.Literal(path)}}
            $stream=[IO.File]::OpenRead($path)
            $sha=[Security.Cryptography.SHA256]::Create()
            try { $hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
            finally { $sha.Dispose(); $stream.Dispose() }
            $policy=Microsoft.PowerShell.Security\Get-ExecutionPolicy
            $signature=Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $path
            $trusted=$false
            if($signature.SignerCertificate){
                foreach($location in @('CurrentUser','LocalMachine')){
                    $store=New-Object Security.Cryptography.X509Certificates.X509Store('TrustedPublisher',$location)
                    try {
                        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
                        if($store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$signature.SignerCertificate.Thumbprint,$false).Count -gt 0){$trusted=$true}
                    } finally { $store.Dispose() }
                }
            }
            @{policy=$policy.ToString();hash=$hash;signature=$signature.Status.ToString();trustedPublisher=$trusted}|Microsoft.PowerShell.Utility\ConvertTo-Json -Compress
            """;
        var output = RequireSuccess(await target.ExecuteAsync(AgentProcessScript.Build("powershell", command, null, null, 30), ct, 35));
        using var process = JsonDocument.Parse(output);
        using var metadata = JsonDocument.Parse(process.RootElement.GetProperty("stdout").GetString()!);
        ValidateFileMetadata(metadata.RootElement, sha256);
    }

    internal static void ValidateFileMetadata(JsonElement result, string sha256)
    {
        if (!string.Equals(result.GetProperty("hash").GetString(), sha256, StringComparison.OrdinalIgnoreCase))
            throw new AgentToolExecutionException("skill_integrity_failed", "The staged script no longer matches the selected package.");
        ValidatePolicy(result.GetProperty("policy").GetString());
        if (result.GetProperty("policy").GetString() == "AllSigned")
        {
            if (result.GetProperty("signature").GetString() != "Valid")
                throw new AgentToolExecutionException("skill_signature_required", "AllSigned requires a valid script signature trusted by the target.", result);
            if (!result.GetProperty("trustedPublisher").GetBoolean())
                throw new AgentToolExecutionException("skill_publisher_untrusted", "The signing publisher must be trusted on the target for noninteractive execution.", result);
        }
    }
}
