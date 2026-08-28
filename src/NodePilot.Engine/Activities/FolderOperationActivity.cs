using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Folder-scoped operations: copy, move, delete, exists, list, create, rename. PowerShell-side
/// attribute checks require a non-reparse directory on destructive paths, so a file or link
/// typed in by mistake fails fast. File operations live in <see cref="FileOperationActivity"/>;
/// validation, the result envelope, and OutputParameters projection live in
/// <see cref="FileSystemOperationActivityBase"/>. Only <c>list</c> is folder-specific.
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

    // Container assertion: confirms the path is a folder before mutation, so a file typed
    // here by mistake throws cleanly instead of being copied/moved/deleted as if it were
    // a directory tree. Skipped for `create`, where the target must not exist yet.
    private const string AssertContainer = """
            $__pathAttributes = Get-NodePilotPathAttributes -Path $__path
            if ($null -eq $__pathAttributes -or
                ($__pathAttributes -band [System.IO.FileAttributes]::Directory) -eq 0 -or
                ($__pathAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Not a directory: " + $__path
            }
        """;

    private static string BuildCopy() => $$"""
        {{AssertContainer}}
            $__effectiveDestination = Get-NodePilotEffectiveDestination `
                -Source $__path -Destination $__destination -Label 'copy destination'
            Assert-NodePilotAllowedPath -Candidate $__effectiveDestination -Label 'copy destination effective path'
            $__copySeparators = [char[]]@(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            $__sourceFull = [System.IO.Path]::GetFullPath($__path)
            $__sourceVolume = [System.IO.Path]::GetPathRoot($__sourceFull)
            if ($__sourceFull.Length -gt $__sourceVolume.Length) {
                $__sourceFull = $__sourceFull.TrimEnd($__copySeparators)
            }
            $__effectiveFull = [System.IO.Path]::GetFullPath($__effectiveDestination)
            $__effectiveVolume = [System.IO.Path]::GetPathRoot($__effectiveFull)
            if ($__effectiveFull.Length -gt $__effectiveVolume.Length) {
                $__effectiveFull = $__effectiveFull.TrimEnd($__copySeparators)
            }
            $__sourcePrefix = $__sourceFull + [System.IO.Path]::DirectorySeparatorChar
            if ($__effectiveFull.Equals($__sourceFull, [System.StringComparison]::OrdinalIgnoreCase) -or
                $__effectiveFull.StartsWith($__sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "File System Operation: copy destination must not be the source or one of its descendants"
            }

            # Build/copy the tree ourselves. Copy-Item -Recurse performs a second provider walk
            # which can follow nested junctions after only the source root was checked.
            $__copyPending = New-Object 'System.Collections.Generic.Stack[object]'
            $__copyPending.Push([pscustomobject]@{
                Source = $__sourceFull
                Destination = $__effectiveFull
            })
            while ($__copyPending.Count -gt 0) {
                $__copyPair = $__copyPending.Pop()
                $__copySourceDirectory = [string]$__copyPair.Source
                $__copyDestinationDirectory = [string]$__copyPair.Destination

                Assert-NodePilotAllowedPath -Candidate $__copySourceDirectory -Label 'copy source tree'
                $__copySourceDirectoryAttributes = Get-NodePilotPathAttributes -Path $__copySourceDirectory
                if ($null -eq $__copySourceDirectoryAttributes -or
                    ($__copySourceDirectoryAttributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                    throw "File System Operation: copy source directory changed or disappeared: '$__copySourceDirectory'"
                }
                if (($__copySourceDirectoryAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "File System Operation: copy source tree contains reparse point '$__copySourceDirectory'"
                }

                Assert-NodePilotAllowedPath -Candidate $__copyDestinationDirectory -Label 'copy destination tree'
                $__copyDestinationAttributes = Get-NodePilotPathAttributes -Path $__copyDestinationDirectory
                if ($null -eq $__copyDestinationAttributes) {
                    [void][System.IO.Directory]::CreateDirectory($__copyDestinationDirectory)
                    Assert-NodePilotAllowedPath -Candidate $__copyDestinationDirectory -Label 'copy destination tree'
                    $__copyDestinationAttributes = Get-NodePilotPathAttributes -Path $__copyDestinationDirectory
                }
                if ($null -eq $__copyDestinationAttributes -or
                    ($__copyDestinationAttributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                    throw "File System Operation: copy destination is not a directory: '$__copyDestinationDirectory'"
                }
                if (($__copyDestinationAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "File System Operation: copy destination tree contains reparse point '$__copyDestinationDirectory'"
                }

                foreach ($__copySourceChild in [System.IO.Directory]::EnumerateFileSystemEntries(
                    $__copySourceDirectory,
                    '*',
                    [System.IO.SearchOption]::TopDirectoryOnly)) {
                    Assert-NodePilotAllowedPath -Candidate $__copySourceChild -Label 'copy source tree'
                    $__copySourceChildAttributes = Get-NodePilotPathAttributes -Path $__copySourceChild
                    if ($null -eq $__copySourceChildAttributes) {
                        throw "File System Operation: copy source item changed or disappeared: '$__copySourceChild'"
                    }
                    if (($__copySourceChildAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw "File System Operation: copy source tree contains reparse point '$__copySourceChild'"
                    }

                    $__copyDestinationChild = [System.IO.Path]::Combine(
                        $__copyDestinationDirectory,
                        [System.IO.Path]::GetFileName($__copySourceChild))
                    Assert-NodePilotAllowedPath -Candidate $__copyDestinationChild -Label 'copy destination item'
                    if (($__copySourceChildAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                        $__copyPending.Push([pscustomobject]@{
                            Source = $__copySourceChild
                            Destination = $__copyDestinationChild
                        })
                    } else {
                        # Re-check after resolving the destination and immediately before opening
                        # either path. Handles are still path-bound, so trusted root ACLs remain
                        # necessary to exclude a concurrent parent-directory swap.
                        Assert-NodePilotAllowedPath -Candidate $__copySourceChild -Label 'copy source item'
                        Assert-NodePilotAllowedPath -Candidate $__copyDestinationChild -Label 'copy destination item'
                        $__copySourceChildAttributes = Get-NodePilotPathAttributes -Path $__copySourceChild
                        if ($null -eq $__copySourceChildAttributes -or
                            ($__copySourceChildAttributes -band [System.IO.FileAttributes]::Directory) -ne 0 -or
                            ($__copySourceChildAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                            throw "File System Operation: copy source file changed or became unsafe: '$__copySourceChild'"
                        }
                        [System.IO.File]::Copy($__copySourceChild, $__copyDestinationChild, $true)
                    }
                }
            }
            $__result.destination = $__destination
        """;

    private static string BuildMove() => $$"""
        {{AssertContainer}}
            Assert-NodePilotReparseFreeTree -Root $__path -Label 'move source'
            $__effectiveDestination = Get-NodePilotEffectiveDestination `
                -Source $__path -Destination $__destination -Label 'move destination'
            Assert-NodePilotAllowedPath -Candidate $__path -Label 'move source'
            Assert-NodePilotAllowedPath -Candidate $__effectiveDestination -Label 'move destination effective path'
            if ($null -ne (Get-NodePilotPathAttributes -Path $__effectiveDestination)) {
                throw "File System Operation: folder move target already exists: '$__effectiveDestination'"
            }
            Assert-NodePilotAllowedPath -Candidate $__effectiveDestination -Label 'move destination effective path'
            Move-Item -LiteralPath $__path -Destination $__effectiveDestination -Force
            $__result.destination = $__destination
        """;

    private static string BuildDelete() => $$"""
        {{AssertContainer}}
            Assert-NodePilotReparseFreeTree -Root $__path -Label 'delete source'
            Assert-NodePilotAllowedPath -Candidate $__path -Label 'delete source'
            Remove-Item -LiteralPath $__path -Force -Recurse
        """;

    // Returns true only when the path exists and is a folder.
    private static string BuildExists() => """
            $__result.exists = [bool](Test-Path -LiteralPath $__path -PathType Container)
        """;

    // Hard cap on listed entries per call, so a list operation accidentally aimed at a huge
    // root folder (\\, C:\, a network share) does not bloat OutputParametersJson. count holds
    // the true entry count before truncation; truncated signals whether the cap kicked in.
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
            Assert-NodePilotAllowedPath -Candidate $__target -Label 'rename target'
            if ($null -ne (Get-NodePilotPathAttributes -Path $__target)) {
                throw "Target already exists: " + $__target
            }
            Assert-NodePilotAllowedPath -Candidate $__path -Label 'rename source'
            Assert-NodePilotAllowedPath -Candidate $__target -Label 'rename target'
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
