using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Shared base for the two path-scoped activities: <see cref="FileOperationActivity"/> and
/// <see cref="FolderOperationActivity"/>. Both validate the same config surface (operation, path,
/// destination, newName) and emit the same JSON result envelope, projected into the same
/// OutputParameters. Subclasses supply only what differs: PowerShell bodies, wording, envelope
/// depth, marker token, and (for folders) the <c>list</c> operation.
/// </summary>
public abstract class FileSystemOperationActivityBase : BaseRemoteActivity
{
    private readonly IConfiguration _config;
    private readonly PowerShellOperationMarkers _resultMarkers;

    protected FileSystemOperationActivityBase(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration config,
        string markerToken)
        : base(sessionFactory, credentialStore, db, engineFactory, config)
    {
        _config = config;
        _resultMarkers = PowerShellOperation.Markers(markerToken);
    }

    /// <summary>Prefix of every user-facing message — "File Operation" / "Folder
    /// Operation".</summary>
    protected abstract string OperationLabel { get; }

    /// <summary>Operations named in the "'operation' is required" message, in UI order.</summary>
    protected abstract string SupportedOperations { get; }

    /// <summary><c>ConvertTo-Json -Depth</c> for the result envelope.</summary>
    protected abstract int ResultJsonDepth { get; }

    /// <summary>
    /// PowerShell body for the (already lower-cased and validated-as-present) operation. The
    /// subclass owns the unknown-operation throw, because its wording names the scope.
    /// </summary>
    protected abstract string BuildOperationBody(string operation);

    protected override string BuildScript(JsonElement config, StepExecutionContext context)
    {
        var operation = config.GetStringOrNull("operation")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation))
            throw new InvalidOperationException($"{OperationLabel}: 'operation' is required ({SupportedOperations})");

        var path = config.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"{OperationLabel}: 'path' is required");

        var destination = config.GetStringOrNull("destination");
        var newName = config.GetStringOrNull("newName");

        PathGuard.Validate(_config, path, allowWildcards: false);
        if (!string.IsNullOrWhiteSpace(destination))
            PathGuard.Validate(_config, destination, allowWildcards: false);
        if (string.Equals(operation, "rename", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(newName))
            PathGuard.ValidateSiblingRenameTarget(_config, path, newName);

        if ((operation == "copy" || operation == "move") && string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException($"{OperationLabel} '{operation}' requires 'destination'");
        if (operation == "rename" && string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException($"{OperationLabel} 'rename' requires 'newName'");

        var qPath = PowerShellOperation.Literal(path);
        var qDest = PowerShellOperation.Literal(destination);
        var qNewName = PowerShellOperation.Literal(newName);
        var targetPathGuard = TargetPathGuardScript.Build(
            _config,
            ("$__path", "path"),
            ("$__destination", "destination"));

        var opBody = BuildOperationBody(operation);

        // operation is validated above (any other value throws), so interpolating it is safe.
        return $$"""
            $ErrorActionPreference = 'Stop'
            $__path = {{qPath}}
            $__destination = {{qDest}}
            $__newName = {{qNewName}}
            $__result = [ordered]@{ operation = '{{operation}}'; path = $__path; ok = $true }
            try {
            {{targetPathGuard}}
            {{opBody}}
            } catch {
                $__result.ok = $false
                $__result.error = $_.Exception.Message
            }
            {{_resultMarkers.RenderJsonEnvelope("$__result", depth: ResultJsonDepth)}}
            """;
    }

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        if (!TryParseResultEnvelope(raw, _resultMarkers, OperationLabel, out var doc, out var passthrough))
            return passthrough!;

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

            var display = ProjectOperationOutputs(operation, root, parameters);

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

    private string ProjectOperationOutputs(string operation, JsonElement root, Dictionary<string, string> parameters)
    {
        switch (operation)
        {
            case "copy":
            case "move":
                if (root.TryGetProperty("destination", out var destEl))
                    parameters["destination"] = destEl.GetString() ?? "";
                return $"{operation}: {parameters.GetValueOrDefault("path")} -> {parameters.GetValueOrDefault("destination")}";

            case "exists":
                var exists = root.TryGetProperty("exists", out var eEl) && eEl.GetBoolean();
                parameters["exists"] = exists ? "true" : "false";
                return exists ? "True" : "False";

            case "create":
                if (root.TryGetProperty("fullName", out var fnEl))
                    parameters["fullName"] = fnEl.GetString() ?? "";
                if (root.TryGetProperty("creationTime", out var ctEl))
                    parameters["creationTime"] = ctEl.GetString() ?? "";
                return parameters.GetValueOrDefault("fullName") ?? "";

            case "rename":
                if (root.TryGetProperty("newPath", out var npEl))
                    parameters["newPath"] = npEl.GetString() ?? "";
                if (root.TryGetProperty("newName", out var nnEl))
                    parameters["newName"] = nnEl.GetString() ?? "";
                return parameters.GetValueOrDefault("newPath") ?? "";

            default:
                return ProjectExtraOperation(operation, root, parameters) ?? "OK";
        }
    }

    /// <summary>
    /// Hook for an operation only one of the two activities offers (folder: <c>list</c>). Returns
    /// the display string, or null to fall back to the shared default.
    /// </summary>
    protected virtual string? ProjectExtraOperation(
        string operation,
        JsonElement root,
        Dictionary<string, string> parameters) => null;
}
