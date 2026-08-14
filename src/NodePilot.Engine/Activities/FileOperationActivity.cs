using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;

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
/// a consistent set of keys. Validation, envelope and projection live in
/// <see cref="FileSystemOperationActivityBase"/>.
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
}
