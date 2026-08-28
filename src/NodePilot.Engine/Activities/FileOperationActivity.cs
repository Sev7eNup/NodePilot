using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

/// <summary>
/// File-scoped operations: copy, move, delete, exists, create, rename. PowerShell-side
/// attribute checks require a non-reparse leaf on destructive paths, so a folder or link
/// typed in by mistake fails fast instead of being followed, deleted, or renamed. Folder
/// operations live in <see cref="FolderOperationActivity"/>; validation, the result envelope,
/// and OutputParameters projection live in <see cref="FileSystemOperationActivityBase"/>.
/// </summary>
public class FileOperationActivity : FileSystemOperationActivityBase
{
    public override string ActivityType => "fileOperation";

    protected override string OperationLabel => "File Operation";

    protected override string SupportedOperations => "copy, move, delete, exists, create, rename";

    protected override int ResultJsonDepth => 4;

    public FileOperationActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration config)
        : base(sessionFactory, credentialStore, db, engineFactory, config, "FILEOP")
    {
    }

    protected override string BuildOperationBody(string operation) => operation switch
    {
        "copy" => BuildCopy(),
        "move" => BuildMove(),
        "delete" => BuildDelete(),
        "exists" => BuildExists(),
        "create" => BuildCreate(),
        "rename" => BuildRename(),
        _ => throw new InvalidOperationException($"Unknown file operation: {operation}")
    };

    // Leaf assertion: confirms the path is a file before mutation, so a folder typed here
    // by mistake throws cleanly instead of being copied/moved/deleted as if it were a file.
    private const string AssertLeaf = """
            $__pathAttributes = Get-NodePilotPathAttributes -Path $__path
            if ($null -eq $__pathAttributes -or
                ($__pathAttributes -band [System.IO.FileAttributes]::Directory) -ne 0 -or
                ($__pathAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Not a file: " + $__path
            }
        """;

    private static string BuildCopy() => $$"""
        {{AssertLeaf}}
            $__effectiveDestination = Get-NodePilotEffectiveDestination `
                -Source $__path -Destination $__destination -Label 'copy destination'
            # Re-check both endpoints immediately before the non-recursive copy. This blocks
            # pre-existing destination\sourceLeaf junctions when Destination is a directory.
            Assert-NodePilotAllowedPath -Candidate $__path -Label 'copy source'
            Assert-NodePilotAllowedPath -Candidate $__effectiveDestination -Label 'copy destination effective path'
            [System.IO.File]::Copy($__path, $__effectiveDestination, $true)
            $__result.destination = $__destination
        """;

    private static string BuildMove() => $$"""
        {{AssertLeaf}}
            $__effectiveDestination = Get-NodePilotEffectiveDestination `
                -Source $__path -Destination $__destination -Label 'move destination'
            Assert-NodePilotAllowedPath -Candidate $__path -Label 'move source'
            Assert-NodePilotAllowedPath -Candidate $__effectiveDestination -Label 'move destination effective path'
            $__moveDestinationAttributes = Get-NodePilotPathAttributes -Path $__effectiveDestination
            if ($null -ne $__moveDestinationAttributes -and
                ($__moveDestinationAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                throw "File System Operation: file move destination is a directory: '$__effectiveDestination'"
            }
            Assert-NodePilotAllowedPath -Candidate $__effectiveDestination -Label 'move destination effective path'
            Move-Item -LiteralPath $__path -Destination $__effectiveDestination -Force
            $__result.destination = $__destination
        """;

    private static string BuildDelete() => $$"""
        {{AssertLeaf}}
            Remove-Item -LiteralPath $__path -Force
        """;

    // Returns true only when the path exists and is a file. Folders return false here —
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
}
