using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// File-scoped operations: copy, move, delete, exists, create, rename. Operates on individual
/// files; PowerShell-side checks assert <c>-PathType Leaf</c> on destructive paths so a folder
/// accidentally typed into a file activity fails fast instead of silently being deleted or
/// renamed. Folder-equivalent operations live in <see cref="FolderOperationActivity"/>.
///
/// Output format: every operation emits a JSON result object between marker lines, which
/// PostProcess projects into OutputParameters (param.operation, param.path, param.destination,
/// param.newPath, param.exists, param.fullName — depending on the operation). This guarantees
/// that <c>{{step.param.exists}}</c> is always "true"/"false" and downstream steps can rely on
/// a consistent set of keys.
/// </summary>
public class FileOperationActivity : BaseRemoteActivity
{
    public override string ActivityType => "fileOperation";

    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("FILEOP");

    private readonly IConfiguration _config;

    public FileOperationActivity(
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
            throw new InvalidOperationException("File Operation: 'operation' is required (copy, move, delete, exists, create, rename)");

        var path = config.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("File Operation: 'path' is required");

        var destination = config.GetStringOrNull("destination");
        var newName = config.GetStringOrNull("newName");

        PathGuard.Validate(_config, path, allowWildcards: false);
        if (!string.IsNullOrWhiteSpace(destination))
            PathGuard.Validate(_config, destination, allowWildcards: false);
        if (string.Equals(operation, "rename", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(newName))
            PathGuard.ValidateSiblingRenameTarget(_config, path, newName);

        if ((operation == "copy" || operation == "move") && string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException($"File Operation '{operation}' requires 'destination'");
        if (operation == "rename" && string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("File Operation 'rename' requires 'newName'");

        var qPath = PowerShellOperation.Literal(path);
        var qDest = PowerShellOperation.Literal(destination);
        var qNewName = PowerShellOperation.Literal(newName);

        var opBody = operation switch
        {
            "copy" => BuildCopy(),
            "move" => BuildMove(),
            "delete" => BuildDelete(),
            "exists" => BuildExists(),
            "create" => BuildCreate(),
            "rename" => BuildRename(),
            _ => throw new InvalidOperationException($"Unknown file operation: {operation}")
        };

        // operation is whitelisted (any other variant throws above), so direct interpolation is safe.
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
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 4)}}
            """;
    }

    // Leaf-Assertion: ensures the path is a file before mutation, so a folder typed here
    // by mistake throws cleanly instead of being copied/moved/deleted as if it were a file.
    private const string AssertLeaf =
        "    if (-not (Test-Path -LiteralPath $__path -PathType Leaf)) { throw \"Not a file: \" + $__path }";

    private static string BuildCopy() => $$"""
        {{AssertLeaf}}
            Copy-Item -LiteralPath $__path -Destination $__destination -Force
            $__result.destination = $__destination
        """;

    private static string BuildMove() => $$"""
        {{AssertLeaf}}
            Move-Item -LiteralPath $__path -Destination $__destination -Force
            $__result.destination = $__destination
        """;

    private static string BuildDelete() => $$"""
        {{AssertLeaf}}
            Remove-Item -LiteralPath $__path -Force
        """;

    // Returns true only when the path exists AND is a file. Folders return false here —
    // use folderOperation/exists for the symmetric check.
    private static string BuildExists() => """
            $__result.exists = [bool](Test-Path -LiteralPath $__path -PathType Leaf)
        """;

    // Create an empty file. Refuses if a folder already exists at the target path
    // (otherwise New-Item would fail with a confusing "ItemNotFound" error). With -Force
    // an existing file is truncated to empty — same idempotent semantics as folder.create.
    private static string BuildCreate() => """
            if (Test-Path -LiteralPath $__path -PathType Container) {
                throw "Cannot create file: path exists as directory: " + $__path
            }
            $__item = New-Item -Path $__path -ItemType File -Force
            $__result.fullName = $__item.FullName
            $__result.creationTime = $__item.CreationTime.ToString('o')
        """;

    private static string BuildRename() => $$"""
        {{AssertLeaf}}
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
                ErrorOutput = $"File Operation: could not parse result JSON: {parseError}",
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
