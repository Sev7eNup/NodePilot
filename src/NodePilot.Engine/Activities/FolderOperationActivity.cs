using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Folder-scoped operations: copy, move, delete, exists, list, create, rename. PowerShell-side
/// checks assert <c>-PathType Container</c> on destructive paths so a file accidentally typed
/// into a folder activity fails fast. File-equivalent operations live in
/// <see cref="FileOperationActivity"/>.
///
/// Output format: every operation emits a JSON result object between marker lines, which
/// PostProcess projects into OutputParameters (param.operation, param.path, param.destination,
/// param.newPath, param.exists, param.fullName, param.items, param.count — depending on the
/// operation). This guarantees that <c>{{step.param.exists}}</c> is always "true"/"false" and
/// <c>{{step.param.items}}</c> is always a JSON array.
/// </summary>
public class FolderOperationActivity : BaseRemoteActivity
{
    public override string ActivityType => "folderOperation";

    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("FOLDEROP");

    private readonly IConfiguration _config;

    public FolderOperationActivity(
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
        var operation = config.GetStringOrNull("operation")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation))
            throw new InvalidOperationException("Folder Operation: 'operation' is required (copy, move, delete, exists, list, create, rename)");

        var path = config.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Folder Operation: 'path' is required");

        var destination = config.GetStringOrNull("destination");
        var newName = config.GetStringOrNull("newName");

        PathGuard.Validate(_config, path, allowWildcards: false);
        if (!string.IsNullOrWhiteSpace(destination))
            PathGuard.Validate(_config, destination, allowWildcards: false);
        if (string.Equals(operation, "rename", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(newName))
            PathGuard.ValidateSiblingRenameTarget(_config, path, newName);

        if ((operation == "copy" || operation == "move") && string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException($"Folder Operation '{operation}' requires 'destination'");
        if (operation == "rename" && string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("Folder Operation 'rename' requires 'newName'");

        var qPath = PowerShellOperation.Literal(path);
        var qDest = PowerShellOperation.Literal(destination);
        var qNewName = PowerShellOperation.Literal(newName);

        var opBody = operation switch
        {
            "copy" => BuildCopy(),
            "move" => BuildMove(),
            "delete" => BuildDelete(),
            "exists" => BuildExists(),
            "list" => BuildList(),
            "create" => BuildCreate(),
            "rename" => BuildRename(),
            _ => throw new InvalidOperationException($"Unknown folder operation: {operation}")
        };

        return $$"""
            $ErrorActionPreference = 'Stop'
            $__path = {{qPath}}
            $__destination = {{qDest}}
            $__newName = {{qNewName}}
            $__result = [ordered]@{ operation = '{{operation}}'; path = $__path; ok = $true }
            try {
            {{opBody}}
            } catch {
                $__result.ok = $false
                $__result.error = $_.Exception.Message
            }
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 6)}}
            """;
    }

    // Container-Assertion: ensures the path is a folder before mutation, so a file typed
    // here by mistake throws cleanly instead of being copied/moved/deleted as if it were
    // a directory tree. Skipped for `create` (target must NOT exist yet).
    private const string AssertContainer =
        "    if (-not (Test-Path -LiteralPath $__path -PathType Container)) { throw \"Not a directory: \" + $__path }";

    private static string BuildCopy() => $$"""
        {{AssertContainer}}
            Copy-Item -LiteralPath $__path -Destination $__destination -Force -Recurse
            $__result.destination = $__destination
        """;

    private static string BuildMove() => $$"""
        {{AssertContainer}}
            Move-Item -LiteralPath $__path -Destination $__destination -Force
            $__result.destination = $__destination
        """;

    private static string BuildDelete() => $$"""
        {{AssertContainer}}
            Remove-Item -LiteralPath $__path -Force -Recurse
        """;

    // Returns true only when the path exists AND is a folder.
    private static string BuildExists() => """
            $__result.exists = [bool](Test-Path -LiteralPath $__path -PathType Container)
        """;

    // Hard cap of 5000 listed entries per call — stops a list operation accidentally aimed at a
    // huge root folder (\\, C:\, a network share) from bloating OutputParametersJson (each entry
    // is ~80-150 bytes; 5000 entries = ~750 KB of JSON). count holds the true entry count (before
    // truncation) so consumers can detect the overflow; truncated signals whether the cap kicked in.
    private const int ListMaxItems = 5000;
    private static string BuildList() => $$"""
        {{AssertContainer}}
            $__items = New-Object System.Collections.ArrayList
            $__total = 0
            $__cap = {{ListMaxItems}}
            Get-ChildItem -LiteralPath $__path | ForEach-Object {
                $__total++
                if ($__items.Count -lt $__cap) {
                    [void]$__items.Add([ordered]@{
                        name = $_.Name
                        length = if ($_.PSIsContainer) { $null } else { $_.Length }
                        lastWriteTime = $_.LastWriteTime.ToString('o')
                        isFolder = [bool]$_.PSIsContainer
                    })
                }
            }
            $__result.items = @($__items)
            $__result.count = $__total
            $__result.truncated = [bool]($__total -gt $__cap)
        """;

    private static string BuildCreate() => """
            $__item = New-Item -Path $__path -ItemType Directory -Force
            $__result.fullName = $__item.FullName
            $__result.creationTime = $__item.CreationTime.ToString('o')
        """;

    private static string BuildRename() => $$"""
        {{AssertContainer}}
            $__parentDir = Split-Path -LiteralPath $__path
            $__target = Join-Path -Path $__parentDir -ChildPath $__newName
            if (Test-Path -LiteralPath $__target) { throw "Target already exists: " + $__target }
            Rename-Item -LiteralPath $__path -NewName $__newName -Force
            $__result.newPath = $__target
            $__result.newName = $__newName
        """;

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        var output = raw.Output ?? string.Empty;
        if (!PowerShellOperation.TryParseJsonBlock(output, ResultMarkers, out var doc, out var parseError))
        {
            if (parseError is null) return raw;
            return new ActivityResult
            {
                Success = false,
                Output = raw.Output,
                ErrorOutput = $"Folder Operation: could not parse result JSON: {parseError}",
                Duration = raw.Duration,
            };
        }

        using (doc!)
        {
            var root = doc!.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var operation = root.TryGetProperty("operation", out var opEl) ? opEl.GetString() ?? "" : "";

            if (!ok)
            {
                var err = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                return new ActivityResult
                {
                    Success = false,
                    Output = null,
                    ErrorOutput = string.IsNullOrEmpty(err) ? raw.ErrorOutput : err,
                    Duration = raw.Duration,
                };
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["operation"] = operation,
            };
            if (root.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
                parameters["path"] = pathEl.GetString() ?? "";

            string display;
            switch (operation)
            {
                case "copy":
                case "move":
                    if (root.TryGetProperty("destination", out var destEl))
                        parameters["destination"] = destEl.GetString() ?? "";
                    display = $"{operation}: {parameters.GetValueOrDefault("path")} -> {parameters.GetValueOrDefault("destination")}";
                    break;

                case "exists":
                    var exists = root.TryGetProperty("exists", out var eEl) && eEl.GetBoolean();
                    parameters["exists"] = exists ? "true" : "false";
                    display = exists ? "True" : "False";
                    break;

                case "list":
                    if (root.TryGetProperty("items", out var itemsEl))
                        parameters["items"] = itemsEl.GetRawText();
                    var count = root.TryGetProperty("count", out var cEl) ? cEl.GetInt32() : 0;
                    parameters["count"] = count.ToString();
                    if (root.TryGetProperty("truncated", out var truncEl) && truncEl.ValueKind != JsonValueKind.Null)
                        parameters["truncated"] = truncEl.GetBoolean() ? "true" : "false";
                    display = root.TryGetProperty("items", out var itemsEl2) ? itemsEl2.GetRawText() : "[]";
                    break;

                case "create":
                    if (root.TryGetProperty("fullName", out var fnEl))
                        parameters["fullName"] = fnEl.GetString() ?? "";
                    if (root.TryGetProperty("creationTime", out var ctEl))
                        parameters["creationTime"] = ctEl.GetString() ?? "";
                    display = parameters.GetValueOrDefault("fullName") ?? "";
                    break;

                case "rename":
                    if (root.TryGetProperty("newPath", out var npEl))
                        parameters["newPath"] = npEl.GetString() ?? "";
                    if (root.TryGetProperty("newName", out var nnEl))
                        parameters["newName"] = nnEl.GetString() ?? "";
                    display = parameters.GetValueOrDefault("newPath") ?? "";
                    break;

                default:
                    display = "OK";
                    break;
            }

            return new ActivityResult
            {
                Success = true,
                Output = display,
                ErrorOutput = raw.ErrorOutput,
                Duration = raw.Duration,
                OutputParameters = parameters,
            };
        }
    }
}
