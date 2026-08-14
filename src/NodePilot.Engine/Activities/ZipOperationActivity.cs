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

    // Kept compatible with Windows PowerShell 5.1/.NET Framework. File.GetAttributes returns
    // link-local metadata, so a dangling junction (including one aimed at a UNC share) is
    // rejected without the validation itself dereferencing the target.
    private const string ReparseGuardScript = """
        function Get-NodePilotPathAttributes {
            param([Parameter(Mandatory = $true)][string]$Path)

            try {
                return [System.IO.File]::GetAttributes($Path)
            } catch {
                $__npAttributeException = $_.Exception.GetBaseException()
                if ($__npAttributeException -is [System.IO.FileNotFoundException] -or
                    $__npAttributeException -is [System.IO.DirectoryNotFoundException]) {
                    return $null
                }
                throw
            }
        }

        function Assert-NodePilotNoReparsePath {
            param([Parameter(Mandatory = $true)][string]$Path)

            $__npFull = [System.IO.Path]::GetFullPath($Path)
            $__npVolume = [System.IO.Path]::GetPathRoot($__npFull)
            if ([string]::IsNullOrEmpty($__npVolume)) {
                throw "Zip operation path '$Path' has no filesystem root"
            }
            $__npSeparators = [char[]]@(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            $__npCurrent = $__npVolume
            $__npVolumeAttributes = Get-NodePilotPathAttributes -Path $__npCurrent
            if ($null -ne $__npVolumeAttributes -and
                ($__npVolumeAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Zip operation blocked: path traverses reparse point '$__npCurrent'"
            }
            $__npRelative = $__npFull.Substring($__npVolume.Length)
            foreach ($__npSegment in $__npRelative.Split(
                $__npSeparators, [System.StringSplitOptions]::RemoveEmptyEntries)) {
                if ($__npSegment.IndexOfAny([char[]]@('*', '?')) -ge 0) { break }
                $__npCurrent = [System.IO.Path]::Combine($__npCurrent, $__npSegment)
                $__npAttributes = Get-NodePilotPathAttributes -Path $__npCurrent
                if ($null -eq $__npAttributes) { break }
                if (($__npAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Zip operation blocked: path traverses reparse point '$__npCurrent'"
                }
            }
        }
        """;

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
        var sourceHasWildcards = source.IndexOfAny(['*', '?']) >= 0;
        if (sourceHasWildcards)
        {
            var lastSeparator = Math.Max(source.LastIndexOf('/'), source.LastIndexOf('\\'));
            var parentPart = lastSeparator < 0 ? string.Empty : source[..lastSeparator];
            if (parentPart.IndexOfAny(['*', '?']) >= 0)
                throw new InvalidOperationException(
                    "Zip Operation: source wildcards are allowed only in the final path segment");
        }
        PathGuard.Validate(_config, source, allowWildcards: allowSourceWildcards);
        PathGuard.Validate(_config, destination, allowWildcards: false);

        var force = config.GetBool("force", false);
        var qSrc = PowerShellOperation.Literal(source);
        var qDst = PowerShellOperation.Literal(destination);
        var targetPathGuard = TargetPathGuardScript.Build(
            _config,
            ("$__npSource", "source"),
            ("$__npDestination", "destination"));

        return operation switch
        {
            "compress" => BuildCompressScript(
                config, qSrc, qDst, force, targetPathGuard, sourceHasWildcards),
            "extract" => BuildExtractScript(qSrc, qDst, force, targetPathGuard),
            _ => throw new InvalidOperationException($"Unknown zip operation: {operation}"),
        };
    }

    private static string BuildCompressScript(
        JsonElement config,
        string qSrc,
        string qDst,
        bool force,
        string targetPathGuard,
        bool sourceHasWildcards)
    {
        var level = config.GetString("compressionLevel", "Optimal");
        if (!AllowedCompressionLevels.Contains(level))
            throw new InvalidOperationException(
                $"Zip Operation: unsupported compressionLevel '{level}'. Allowed: Optimal, Fastest, NoCompression");
        var canonicalLevel = AllowedCompressionLevels.Single(
            candidate => candidate.Equals(level, StringComparison.OrdinalIgnoreCase));
        var sourceSetup = sourceHasWildcards
            ? """
                # Windows PowerShell 5.1 runs on .NET Framework, whose GetFullPath rejects
                # wildcards. Normalize the already-enforced literal parent separately.
                $__npLastSourceSeparator = [Math]::Max(
                    $__npSource.LastIndexOf([char]92),
                    $__npSource.LastIndexOf([char]47))
                if ($__npLastSourceSeparator -lt 0) {
                    $__npSourceParentInput = '.'
                    $__npSourcePattern = $__npSource
                } else {
                    $__npSourceParentLength = if ($__npLastSourceSeparator -eq 2 -and
                        $__npSource.Length -gt 1 -and $__npSource[1] -eq ':') {
                        3
                    } else {
                        $__npLastSourceSeparator
                    }
                    $__npSourceParentInput = $__npSource.Substring(0, $__npSourceParentLength)
                    $__npSourcePattern = $__npSource.Substring($__npLastSourceSeparator + 1)
                }
                $__npSourceParent = [System.IO.Path]::GetFullPath($__npSourceParentInput)
                $__npSourceFull = [System.IO.Path]::Combine($__npSourceParent, $__npSourcePattern)
                Assert-NodePilotNoReparsePath -Path $__npSourceParent
                $__npParentAttributes = Get-NodePilotPathAttributes -Path $__npSourceParent
                if ($null -eq $__npParentAttributes -or
                    ($__npParentAttributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                    throw "Zip compression source parent is not a directory: '$__npSourceParent'"
                }
                $__npSourcePaths = @([System.IO.Directory]::EnumerateFileSystemEntries(
                    $__npSourceParent,
                    $__npSourcePattern,
                    [System.IO.SearchOption]::TopDirectoryOnly))
                """
            : """
                $__npSourceFull = [System.IO.Path]::GetFullPath($__npSource)
                $__npSourcePaths = @($__npSourceFull)
                """;

        return $$"""
            $ErrorActionPreference = 'Stop'
            $__npSource = {{qSrc}}
            $__npDestination = {{qDst}}
            $__npForce = ${{(force ? "true" : "false")}}
            {{targetPathGuard}}

            Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
            Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
            {{ReparseGuardScript}}

            $__npSeparators = [char[]]@(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            {{sourceSetup}}
            if ($__npSourcePaths.Count -eq 0) {
                throw "Zip compression source '$__npSource' did not match any filesystem item"
            }

            # Build a literal manifest without recursive provider/cmdlet expansion. Every item
            # is inspected before a directory is enumerated, so selected or nested junctions
            # are rejected rather than followed. Files are rechecked immediately before open.
            $__npManifest = New-Object 'System.Collections.Generic.List[object]'
            $__npPendingDirectories = New-Object System.Collections.Stack
            foreach ($__npRootPath in $__npSourcePaths) {
                Assert-NodePilotAllowedPath -Candidate ($__npRootPath) -Label 'expandedSource'
                Assert-NodePilotNoReparsePath -Path $__npRootPath
                $__npRootAttributes = Get-NodePilotPathAttributes -Path $__npRootPath
                if ($null -eq $__npRootAttributes) {
                    throw "Zip compression source changed or disappeared: '$__npRootPath'"
                }
                if (($__npRootAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Zip compression blocked: selected source is a reparse point '$__npRootPath'"
                }

                $__npTrimmedRoot = $__npRootPath.TrimEnd($__npSeparators)
                $__npEntryBase = [System.IO.Path]::GetFileName($__npTrimmedRoot)
                if ([string]::IsNullOrEmpty($__npEntryBase)) {
                    $__npEntryBase = $__npRootPath.Substring(0, 1)
                }

                if (($__npRootAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    $__npManifest.Add([pscustomobject]@{
                        Path = $__npRootPath
                        EntryName = $__npEntryBase + '/'
                        IsDirectory = $true
                    })
                    $__npPendingDirectories.Push([pscustomobject]@{
                        Path = $__npRootPath
                        EntryPrefix = $__npEntryBase
                    })
                } else {
                    $__npManifest.Add([pscustomobject]@{
                        Path = $__npRootPath
                        EntryName = $__npEntryBase
                        IsDirectory = $false
                    })
                }
            }
            while ($__npPendingDirectories.Count -gt 0) {
                $__npDirectory = $__npPendingDirectories.Pop()
                Assert-NodePilotAllowedPath -Candidate ($__npDirectory.Path) -Label 'sourceDirectory'
                Assert-NodePilotNoReparsePath -Path $__npDirectory.Path
                foreach ($__npChildPath in [System.IO.Directory]::EnumerateFileSystemEntries(
                    $__npDirectory.Path,
                    '*',
                    [System.IO.SearchOption]::TopDirectoryOnly)) {
                    Assert-NodePilotAllowedPath -Candidate ($__npChildPath) -Label 'sourceItem'
                    Assert-NodePilotNoReparsePath -Path $__npChildPath
                    $__npChildAttributes = Get-NodePilotPathAttributes -Path $__npChildPath
                    if ($null -eq $__npChildAttributes) {
                        throw "Zip compression source changed or disappeared: '$__npChildPath'"
                    }
                    if (($__npChildAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw "Zip compression blocked: source tree contains reparse point '$__npChildPath'"
                    }

                    $__npChildName = [System.IO.Path]::GetFileName($__npChildPath)
                    $__npEntryName = $__npDirectory.EntryPrefix + '/' + $__npChildName
                    if (($__npChildAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                        $__npManifest.Add([pscustomobject]@{
                            Path = $__npChildPath
                            EntryName = $__npEntryName + '/'
                            IsDirectory = $true
                        })
                        $__npPendingDirectories.Push([pscustomobject]@{
                            Path = $__npChildPath
                            EntryPrefix = $__npEntryName
                        })
                    } else {
                        $__npManifest.Add([pscustomobject]@{
                            Path = $__npChildPath
                            EntryName = $__npEntryName
                            IsDirectory = $false
                        })
                    }
                }
            }

            $__npDestinationFull = [System.IO.Path]::GetFullPath($__npDestination)
            Assert-NodePilotNoReparsePath -Path $__npDestinationFull
            $__npDestinationAttributes = Get-NodePilotPathAttributes -Path $__npDestinationFull
            if ($null -ne $__npDestinationAttributes) {
                if (($__npDestinationAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    throw "Zip compression destination exists as a directory: '$__npDestinationFull'"
                }
                if (-not $__npForce) {
                    throw "Zip compression destination already exists: '$__npDestinationFull'"
                }
                [System.IO.File]::Delete($__npDestinationFull)
            }

            $__npDestinationParent = [System.IO.Path]::GetDirectoryName($__npDestinationFull)
            Assert-NodePilotNoReparsePath -Path $__npDestinationParent
            $__npDestinationParentAttributes = Get-NodePilotPathAttributes -Path $__npDestinationParent
            if ($null -eq $__npDestinationParentAttributes -or
                ($__npDestinationParentAttributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                throw "Zip compression destination parent is not a directory: '$__npDestinationParent'"
            }

            $__npCompressionLevel = [System.IO.Compression.CompressionLevel]::{{canonicalLevel}}
            $__npOutput = $null
            $__npArchive = $null
            $__npCreatedDestination = $false
            $__npCompleted = $false
            $__npSizeBytes = 0
            try {
                # CreateNew rejects a final-leaf swap. Existing parent components were checked
                # immediately above; preventing a concurrent parent rename requires OS handles
                # and ACLs beyond path-based PowerShell/.NET APIs.
                Assert-NodePilotNoReparsePath -Path $__npDestinationParent
                $__npOutput = [System.IO.File]::Open(
                    $__npDestinationFull,
                    [System.IO.FileMode]::CreateNew,
                    [System.IO.FileAccess]::Write,
                    [System.IO.FileShare]::None)
                $__npCreatedDestination = $true
                $__npArchive = [System.IO.Compression.ZipArchive]::new(
                    $__npOutput,
                    [System.IO.Compression.ZipArchiveMode]::Create,
                    $true)

                foreach ($__npManifestEntry in $__npManifest) {
                    if ($__npManifestEntry.IsDirectory) {
                        [void]$__npArchive.CreateEntry(
                            $__npManifestEntry.EntryName,
                            $__npCompressionLevel)
                        continue
                    }

                    Assert-NodePilotAllowedPath -Candidate ($__npManifestEntry.Path) -Label 'sourceFile'
                    Assert-NodePilotNoReparsePath -Path $__npManifestEntry.Path
                    $__npFileAttributes = Get-NodePilotPathAttributes -Path $__npManifestEntry.Path
                    if ($null -eq $__npFileAttributes -or
                        ($__npFileAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                        throw "Zip compression source changed or is not a file: '$($__npManifestEntry.Path)'"
                    }

                    $__npInput = [System.IO.File]::Open(
                        $__npManifestEntry.Path,
                        [System.IO.FileMode]::Open,
                        [System.IO.FileAccess]::Read,
                        [System.IO.FileShare]::Read)
                    try {
                        $__npZipEntry = $__npArchive.CreateEntry(
                            $__npManifestEntry.EntryName,
                            $__npCompressionLevel)
                        $__npEntryOutput = $__npZipEntry.Open()
                        try {
                            $__npInput.CopyTo($__npEntryOutput)
                        } finally {
                            $__npEntryOutput.Dispose()
                        }
                    } finally {
                        $__npInput.Dispose()
                    }
                }

                $__npArchive.Dispose()
                $__npArchive = $null
                $__npSizeBytes = $__npOutput.Length
                $__npCompleted = $true
            } finally {
                if ($null -ne $__npArchive) { $__npArchive.Dispose() }
                if ($null -ne $__npOutput) { $__npOutput.Dispose() }
                if ($__npCreatedDestination -and -not $__npCompleted) {
                    [System.IO.File]::Delete($__npDestinationFull)
                }
            }

            $__result = [ordered]@{
                operation = 'compress'
                destination = $__npDestinationFull
                sizeBytes = $__npSizeBytes
            }
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 4)}}
            """;
    }

    private static string BuildExtractScript(
        string qSrc,
        string qDst,
        bool force,
        string targetPathGuard)
        => $$"""
            $ErrorActionPreference = 'Stop'
            $__npSource = {{qSrc}}
            $__npDestination = {{qDst}}
            $__npForce = ${{(force ? "true" : "false")}}
            {{targetPathGuard}}

            # A separate pre-scan plus the built-in archive cmdlet performs two path walks, so
            # a writable destination can be swapped to a junction in between. Extract entries:
            # validate and create each parent immediately before opening the output with
            # CreateNew. Reparse points present at a validation point are rejected, including
            # with force=true.
            Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
            Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
            {{ReparseGuardScript}}

            Assert-NodePilotNoReparsePath -Path $__npSource
            $__npSourceAttributes = Get-NodePilotPathAttributes -Path $__npSource
            if ($null -eq $__npSourceAttributes -or
                ($__npSourceAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                throw "Zip source does not exist as a file: '$__npSource'"
            }

            $__npResolvedDest = [System.IO.Path]::GetFullPath($__npDestination)
            Assert-NodePilotNoReparsePath -Path $__npResolvedDest
            $__npDestinationAttributes = Get-NodePilotPathAttributes -Path $__npResolvedDest
            if ($null -ne $__npDestinationAttributes -and
                ($__npDestinationAttributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                throw "Zip destination exists as a file: '$__npResolvedDest'"
            }
            [void][System.IO.Directory]::CreateDirectory($__npResolvedDest)
            Assert-NodePilotNoReparsePath -Path $__npResolvedDest

            $__npDestinationPrefix = $__npResolvedDest
            if (-not $__npDestinationPrefix.EndsWith(
                [string][System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::Ordinal)) {
                $__npDestinationPrefix += [System.IO.Path]::DirectorySeparatorChar
            }

            $__npSourceStream = [System.IO.File]::Open(
                $__npSource,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            try {
                $__npZip = [System.IO.Compression.ZipArchive]::new(
                    $__npSourceStream,
                    [System.IO.Compression.ZipArchiveMode]::Read,
                    $false)
                try {
                    foreach ($__npEntry in $__npZip.Entries) {
                        if ([System.IO.Path]::IsPathRooted($__npEntry.FullName)) {
                            throw "Zip-Slip blocked: rooted entry '$($__npEntry.FullName)'"
                        }

                        $__npEntryName = $__npEntry.FullName.Replace(
                            [System.IO.Path]::AltDirectorySeparatorChar,
                            [System.IO.Path]::DirectorySeparatorChar)
                        $__npEntryPath = [System.IO.Path]::GetFullPath(
                            [System.IO.Path]::Combine($__npDestinationPrefix, $__npEntryName))
                        if (-not $__npEntryPath.StartsWith(
                            $__npDestinationPrefix,
                            [System.StringComparison]::OrdinalIgnoreCase)) {
                            throw "Zip-Slip blocked: entry '" + $__npEntry.FullName + "' escapes destination"
                        }

                        $__npIsDirectory = [string]::IsNullOrEmpty($__npEntry.Name) -or
                            $__npEntry.FullName.EndsWith('/') -or
                            $__npEntry.FullName.EndsWith('\')
                        if ($__npIsDirectory) {
                            Assert-NodePilotNoReparsePath -Path $__npEntryPath
                            [void][System.IO.Directory]::CreateDirectory($__npEntryPath)
                            Assert-NodePilotNoReparsePath -Path $__npEntryPath
                            continue
                        }

                        $__npParent = [System.IO.Path]::GetDirectoryName($__npEntryPath)
                        Assert-NodePilotNoReparsePath -Path $__npParent
                        [void][System.IO.Directory]::CreateDirectory($__npParent)
                        Assert-NodePilotNoReparsePath -Path $__npParent

                        Assert-NodePilotNoReparsePath -Path $__npEntryPath
                        $__npEntryAttributes = Get-NodePilotPathAttributes -Path $__npEntryPath
                        if ($null -ne $__npEntryAttributes -and
                            ($__npEntryAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                            throw "Zip extraction blocked: file entry collides with directory '$__npEntryPath'"
                        }
                        if ($null -ne $__npEntryAttributes) {
                            if (-not $__npForce) {
                                throw "Zip extraction target already exists: '$__npEntryPath'"
                            }
                            [System.IO.File]::Delete($__npEntryPath)
                        }

                        # Recheck after directory creation/deletion. CreateNew refuses an
                        # already-present final leaf (including a link). A concurrent parent
                        # replacement remains outside what path-based PowerShell/.NET APIs can
                        # close; destination ACLs must prevent untrusted renames while extracting.
                        Assert-NodePilotNoReparsePath -Path $__npParent
                        $__npOutput = [System.IO.File]::Open(
                            $__npEntryPath,
                            [System.IO.FileMode]::CreateNew,
                            [System.IO.FileAccess]::Write,
                            [System.IO.FileShare]::None)
                        try {
                            $__npInput = $__npEntry.Open()
                            try {
                                $__npInput.CopyTo($__npOutput)
                            } finally {
                                $__npInput.Dispose()
                            }
                        } finally {
                            $__npOutput.Dispose()
                        }
                    }
                } finally {
                    $__npZip.Dispose()
                }
            } finally {
                $__npSourceStream.Dispose()
            }
            $__result = [ordered]@{
                operation = 'extract'
                destination = $__npResolvedDest
                sizeBytes = 0
            }
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 4)}}
            """;

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        if (!raw.Success) return raw;

        if (!TryParseResultEnvelope(raw, ResultMarkers, "Zip Operation", out var doc, out var passthrough))
            return passthrough!;

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
