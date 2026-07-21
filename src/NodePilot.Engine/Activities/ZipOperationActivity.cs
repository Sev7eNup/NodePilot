using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Compresses or extracts ZIP archives on a remote machine via PowerShell's archive cmdlets.
/// </summary>
public class ZipOperationActivity : BaseRemoteActivity
{
    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("ZIP");

    private static readonly HashSet<string> AllowedCompressionLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Optimal", "Fastest", "NoCompression",
    };

    private readonly IConfiguration _config;

    public override string ActivityType => "zipOperation";

    public ZipOperationActivity(
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
        // Default to "compress" when operation is missing or empty — the UI dropdown shows
        // "Compress (zip)" as visual default but won't persist 'compress' to config unless
        // the user actively changes the dropdown. Workflows authored without touching the
        // dropdown used to fail with "'operation' is required". source/destination are still
        // mandatory below — defaulting operation only heals the dropdown-not-touched case.
        var rawOperation = config.GetStringOrNull("operation");
        var operation = (string.IsNullOrWhiteSpace(rawOperation) ? "compress" : rawOperation).ToLowerInvariant();

        var source = config.GetStringOrNull("source");
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Zip Operation: 'source' is required");

        var destination = config.GetStringOrNull("destination");
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("Zip Operation: 'destination' is required");

        var allowSourceWildcards = string.Equals(operation, "compress", StringComparison.Ordinal);
        PathGuard.Validate(_config, source, allowWildcards: allowSourceWildcards);
        PathGuard.Validate(_config, destination, allowWildcards: false);

        var force = config.GetBool("force", false);
        var qSrc = PowerShellOperation.Literal(source);
        var qDst = PowerShellOperation.Literal(destination);
        var forceFlag = force ? " -Force" : string.Empty;

        return operation switch
        {
            "compress" => BuildCompressScript(config, qSrc, qDst, forceFlag),
            "extract" => BuildExtractScript(qSrc, qDst, forceFlag),
            _ => throw new InvalidOperationException($"Unknown zip operation: {operation}"),
        };
    }

    private static string BuildCompressScript(JsonElement config, string qSrc, string qDst, string forceFlag)
    {
        var level = config.GetString("compressionLevel", "Optimal");
        if (!AllowedCompressionLevels.Contains(level))
            throw new InvalidOperationException(
                $"Zip Operation: unsupported compressionLevel '{level}'. Allowed: Optimal, Fastest, NoCompression");

        return $$"""
            $ErrorActionPreference = 'Stop'
            $__npDestination = {{qDst}}
            Compress-Archive -Path {{qSrc}} -DestinationPath $__npDestination -CompressionLevel {{level}}{{forceFlag}}
            $__npItem = Get-Item -LiteralPath $__npDestination
            $__result = [ordered]@{
                operation = 'compress'
                destination = $__npDestination
                sizeBytes = $__npItem.Length
            }
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 4)}}
            """;
    }

    private static string BuildExtractScript(string qSrc, string qDst, string forceFlag)
        => $$"""
            $ErrorActionPreference = 'Stop'
            $__npSource = {{qSrc}}
            $__npDestination = {{qDst}}
            # Zip-Slip pre-scan: Expand-Archive on PS 5.1 does NOT validate entry paths.
            # A malicious archive with entries like "..\..\Windows\System32\evil.dll" would
            # write outside the destination. Resolve the destination, then verify every
            # entry's full path lands inside it before touching Expand-Archive.
            Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
            if (-not (Test-Path -LiteralPath $__npDestination)) {
                [void](New-Item -ItemType Directory -Path $__npDestination -Force)
            }
            $__npResolvedDest = [System.IO.Path]::GetFullPath($__npDestination)
            if (-not $__npResolvedDest.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
                $__npResolvedDest = $__npResolvedDest + [System.IO.Path]::DirectorySeparatorChar
            }
            $__npZip = [System.IO.Compression.ZipFile]::OpenRead($__npSource)
            try {
                foreach ($__npEntry in $__npZip.Entries) {
                    $__npEntryPath = [System.IO.Path]::GetFullPath(
                        [System.IO.Path]::Combine($__npResolvedDest, $__npEntry.FullName))
                    if (-not $__npEntryPath.StartsWith($__npResolvedDest, [System.StringComparison]::OrdinalIgnoreCase)) {
                        throw "Zip-Slip blocked: entry '" + $__npEntry.FullName + "' escapes destination"
                    }
                }
            } finally {
                $__npZip.Dispose()
            }
            Expand-Archive -LiteralPath $__npSource -DestinationPath $__npDestination{{forceFlag}}
            $__result = [ordered]@{
                operation = 'extract'
                destination = $__npDestination
                sizeBytes = 0
            }
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 4)}}
            """;

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
                ErrorOutput = $"Zip Operation: could not parse result JSON: {parseError}",
                Duration = raw.Duration,
            };
        }

        using (doc!)
        {
            var root = doc!.RootElement;
            var parameters = PowerShellOperation.MapObjectFields(
                root,
                ("destination", "destination"),
                ("sizeBytes", "sizeBytes"));

            var destination = parameters.TryGetValue("destination", out var dest) ? dest : string.Empty;
            var sizeBytes = parameters.TryGetValue("sizeBytes", out var size) ? size : "0";
            var operation = root.TryGetProperty("operation", out var opEl)
                ? PowerShellOperation.JsonElementToScalarString(opEl)
                : config.GetStringOrNull("operation") ?? string.Empty;

            return new ActivityResult
            {
                Success = true,
                Output = operation == "compress"
                    ? $"{destination} ({sizeBytes} bytes)"
                    : destination,
                ErrorOutput = raw.ErrorOutput,
                Duration = raw.Duration,
                OutputParameters = parameters,
            };
        }
    }
}
