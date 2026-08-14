using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;

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
/// <c>{{step.param.items}}</c> is always a JSON array. Validation, envelope and projection live
/// in <see cref="FileSystemOperationActivityBase"/>; only <c>list</c> is folder-specific.
/// </summary>
public class FolderOperationActivity : FileSystemOperationActivityBase
{
    public override string ActivityType => "folderOperation";

    protected override string OperationLabel => "Folder Operation";

    protected override string SupportedOperations => "copy, move, delete, exists, list, create, rename";

    protected override int ResultJsonDepth => 6;

    public FolderOperationActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration config)
        : base(sessionFactory, credentialStore, db, engineFactory, config, "FOLDEROP")
    {
    }

    protected override string BuildOperationBody(string operation) => operation switch
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

    protected override string? ProjectExtraOperation(
        string operation,
        JsonElement root,
        Dictionary<string, string> parameters)
    {
        if (operation != "list") return null;

        if (root.TryGetProperty("items", out var itemsEl))
            parameters["items"] = itemsEl.GetRawText();
        var count = root.TryGetProperty("count", out var cEl) ? cEl.GetInt32() : 0;
        parameters["count"] = count.ToString();
        if (root.TryGetProperty("truncated", out var truncEl) && truncEl.ValueKind != JsonValueKind.Null)
            parameters["truncated"] = truncEl.GetBoolean() ? "true" : "false";
        return root.TryGetProperty("items", out var itemsEl2) ? itemsEl2.GetRawText() : "[]";
    }
}
