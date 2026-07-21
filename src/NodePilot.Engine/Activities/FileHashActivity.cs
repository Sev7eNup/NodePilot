using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Computes a file hash on a remote machine and optionally verifies it against an expected value.
/// </summary>
public class FileHashActivity : BaseRemoteActivity
{
    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("FILEHASH");

    private static readonly HashSet<string> AllowedAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "MD5", "SHA1", "SHA256", "SHA384", "SHA512",
    };

    private readonly IConfiguration _config;

    public override string ActivityType => "fileHash";

    public FileHashActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration config)
        : base(sessionFactory, credentialStore, db, engineFactory, config)
    {
        _config = config;
    }

    protected override string BuildScript(JsonElement config, StepExecutionContext context)
    {
        var path = config.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("File Hash: 'path' is required");

        var algorithm = config.GetString("algorithm", "SHA256").ToUpperInvariant();
        if (!AllowedAlgorithms.Contains(algorithm))
            throw new InvalidOperationException(
                $"File Hash: unsupported algorithm '{algorithm}'. Allowed: MD5, SHA1, SHA256, SHA384, SHA512");

        PathGuard.Validate(_config, path);

        var qPath = PowerShellOperation.Literal(path);

        return $$"""
            $ErrorActionPreference = 'Stop'
            $__npAlgorithm = '{{algorithm}}'
            $__npPath = {{qPath}}
            $__npAlg = [System.Security.Cryptography.HashAlgorithm]::Create($__npAlgorithm)
            try {
                $__npStream = [System.IO.File]::OpenRead($__npPath)
                try {
                    $__npHashBytes = $__npAlg.ComputeHash($__npStream)
                } finally {
                    $__npStream.Dispose()
                }
            } finally {
                $__npAlg.Dispose()
            }
            $__npHash = [System.BitConverter]::ToString($__npHashBytes).Replace('-','')
            $__result = [ordered]@{
                hash = $__npHash
                algorithm = $__npAlgorithm
            }
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 4)}}
            """;
    }

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        if (!raw.Success) return raw;

        if (!PowerShellOperation.TryParseJsonBlock(raw.Output, ResultMarkers, out var doc, out var parseError))
        {
            if (parseError is null) return raw;
            return new ActivityResult
            {
                Success = false,
                Output = raw.Output,
                ErrorOutput = $"File Hash: could not parse result JSON: {parseError}",
                Duration = raw.Duration,
            };
        }

        using (doc!)
        {
            var root = doc!.RootElement;
            var hash = root.TryGetProperty("hash", out var hashEl)
                ? PowerShellOperation.JsonElementToScalarString(hashEl)
                : string.Empty;
            var algorithm = root.TryGetProperty("algorithm", out var algorithmEl)
                ? PowerShellOperation.JsonElementToScalarString(algorithmEl).ToUpperInvariant()
                : config.GetString("algorithm", "SHA256").ToUpperInvariant();

            var outputParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hash"] = hash,
                ["algorithm"] = algorithm,
                ["match"] = string.Empty,
            };

            var expected = config.GetStringOrNull("expected");
            if (!string.IsNullOrWhiteSpace(expected))
            {
                var matched = string.Equals(hash, expected.Trim(), StringComparison.OrdinalIgnoreCase);
                outputParameters["match"] = matched ? "true" : "false";
                if (!matched)
                {
                    return new ActivityResult
                    {
                        Success = false,
                        Output = hash,
                        ErrorOutput = $"Hash mismatch: expected {expected.Trim()}, got {hash}",
                        Duration = raw.Duration,
                        OutputParameters = outputParameters,
                    };
                }
            }

            return new ActivityResult
            {
                Success = true,
                Output = hash,
                ErrorOutput = raw.ErrorOutput,
                Duration = raw.Duration,
                OutputParameters = outputParameters,
            };
        }
    }
}
