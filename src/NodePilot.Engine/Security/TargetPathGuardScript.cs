using Microsoft.Extensions.Configuration;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Security;

/// <summary>
/// Builds the target-side half of <see cref="PathGuard"/>. The C# guard runs before a
/// WinRM session is opened, so its view of reparse points is authoritative only for local
/// execution. This script repeats the allow-root decision on the machine that will actually
/// touch the path and fails closed when an existing path component is a junction/symlink.
/// </summary>
internal static class TargetPathGuardScript
{
    internal static string Build(
        IConfiguration config,
        params (string Expression, string Label)[] candidates)
    {
        var roots = PathGuard.ReadConfiguredRoots(
            config,
            "FileSystemOperation:AllowedRoots",
            out _);
        if (candidates.Length == 0)
            return string.Empty;

        var rootLiterals = string.Join(", ", roots.Select(PowerShellOperation.Literal));
        var enforceRootsLiteral = roots.Length > 0 ? "$true" : "$false";
        var assertions = string.Join(
            Environment.NewLine,
            candidates.Select(candidate =>
                $"Assert-NodePilotAllowedPath -Candidate ({candidate.Expression}) " +
                $"-Label {PowerShellOperation.Literal(candidate.Label)}"));

        return $$"""
            # Authoritative target-side path check. Reparse rejection is unconditional; only
            # containment is optional when AllowedRoots is empty. File.GetAttributes is
            # deliberately used instead of Test-Path/Get-Item so a dangling junction, including
            # one aimed at a UNC share, is inspected without dereferencing its target.
            $__npAllowedRoots = @({{rootLiterals}})
            $__npEnforceAllowedRoots = {{enforceRootsLiteral}}
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

            function Assert-NodePilotAllowedPath {
                param(
                    [AllowNull()][string]$Candidate,
                    [Parameter(Mandatory = $true)][string]$Label
                )

                if ([string]::IsNullOrWhiteSpace($Candidate)) { return }

                if ($Candidate.StartsWith('\\') -or $Candidate.StartsWith('//')) {
                    throw "File System Operation: $Label '$Candidate' is a UNC or device path on target"
                }

                try {
                    $__npWildcardIndex = $Candidate.IndexOfAny([char[]]@('*', '?'))
                    if ($__npWildcardIndex -ge 0) {
                        # .NET Framework's Path.GetFullPath rejects wildcard characters. Only
                        # the leaf may contain them; normalize the literal parent first and then
                        # append the untrusted-as-pattern leaf without provider expansion.
                        $__npLastSeparator = [Math]::Max(
                            $Candidate.LastIndexOf([char]92),
                            $Candidate.LastIndexOf([char]47))
                        if ($__npLastSeparator -lt 0) {
                            $__npCandidateParent = '.'
                            $__npCandidateLeaf = $Candidate
                        } else {
                            $__npParentLength = if ($__npLastSeparator -eq 2 -and
                                $Candidate.Length -gt 1 -and $Candidate[1] -eq ':') {
                                3
                            } else {
                                $__npLastSeparator
                            }
                            $__npCandidateParent = $Candidate.Substring(0, $__npParentLength)
                            $__npCandidateLeaf = $Candidate.Substring($__npLastSeparator + 1)
                        }
                        if ($__npCandidateParent.IndexOfAny([char[]]@('*', '?')) -ge 0) {
                            throw "wildcards are allowed only in the final path segment"
                        }
                        $__npParentFull = [System.IO.Path]::GetFullPath($__npCandidateParent)
                        $__npCandidateFull = [System.IO.Path]::Combine(
                            $__npParentFull,
                            $__npCandidateLeaf)
                    } else {
                        $__npCandidateFull = [System.IO.Path]::GetFullPath($Candidate)
                    }
                } catch {
                    throw "File System Operation: $Label '$Candidate' is not a valid absolute path on target: $($_.Exception.Message)"
                }

                $__npSeparators = [char[]]@(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar)
                $__npCandidateVolume = [System.IO.Path]::GetPathRoot($__npCandidateFull)
                if ([string]::IsNullOrEmpty($__npCandidateVolume)) {
                    throw "File System Operation: $Label '$Candidate' has no filesystem root on target"
                }
                if ($__npCandidateFull.Length -gt $__npCandidateVolume.Length) {
                    $__npCandidateFull = $__npCandidateFull.TrimEnd($__npSeparators)
                }

                # Walk existing components from the volume down. Stop at the first missing or
                # wildcard component; a descendant cannot exist below a missing literal parent.
                $__npCurrent = $__npCandidateVolume
                $__npVolumeAttributes = Get-NodePilotPathAttributes -Path $__npCurrent
                if ($null -ne $__npVolumeAttributes -and
                    ($__npVolumeAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "File System Operation: $Label '$Candidate' traverses reparse point '$__npCurrent' on target"
                }
                $__npRelative = $__npCandidateFull.Substring($__npCandidateVolume.Length)
                foreach ($__npSegment in $__npRelative.Split(
                    $__npSeparators, [System.StringSplitOptions]::RemoveEmptyEntries)) {
                    if ($__npSegment.IndexOfAny([char[]]@('*', '?')) -ge 0) { break }
                    $__npCurrent = [System.IO.Path]::Combine($__npCurrent, $__npSegment)
                    $__npAttributes = Get-NodePilotPathAttributes -Path $__npCurrent
                    if ($null -eq $__npAttributes) { break }
                    if (($__npAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw "File System Operation: $Label '$Candidate' traverses reparse point '$__npCurrent' on target"
                    }
                }

                if (-not $__npEnforceAllowedRoots) { return }

                $__npMatchedRoot = $null
                foreach ($__npConfiguredRoot in $__npAllowedRoots) {
                    if ($__npConfiguredRoot.StartsWith('\\') -or $__npConfiguredRoot.StartsWith('//')) {
                        throw "File System Operation: configured AllowedRoot '$__npConfiguredRoot' must not be a UNC or device path on target"
                    }
                    try {
                        $__npRootFull = [System.IO.Path]::GetFullPath($__npConfiguredRoot)
                    } catch {
                        throw "File System Operation: configured AllowedRoot '$__npConfiguredRoot' is invalid on target: $($_.Exception.Message)"
                    }
                    $__npRootVolume = [System.IO.Path]::GetPathRoot($__npRootFull)
                    if ([string]::IsNullOrEmpty($__npRootVolume)) { continue }
                    if ($__npRootFull.Length -gt $__npRootVolume.Length) {
                        $__npRootFull = $__npRootFull.TrimEnd($__npSeparators)
                    }

                    $__npAtRoot = $__npCandidateFull.Equals(
                        $__npRootFull, [System.StringComparison]::OrdinalIgnoreCase)
                    $__npRootPrefix = if ($__npRootFull.EndsWith(
                        [string][System.IO.Path]::DirectorySeparatorChar,
                        [System.StringComparison]::Ordinal)) {
                        $__npRootFull
                    } else {
                        $__npRootFull + [System.IO.Path]::DirectorySeparatorChar
                    }
                    $__npBelowRoot = $__npCandidateFull.StartsWith(
                        $__npRootPrefix,
                        [System.StringComparison]::OrdinalIgnoreCase)
                    if ($__npAtRoot -or $__npBelowRoot) {
                        $__npRootAttributes = Get-NodePilotPathAttributes -Path $__npRootFull
                        if ($null -eq $__npRootAttributes -or
                            ($__npRootAttributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                            throw "File System Operation: configured AllowedRoot '$__npConfiguredRoot' does not exist as a directory on target"
                        }
                        if (($__npRootAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                            throw "File System Operation: configured AllowedRoot '$__npConfiguredRoot' is a reparse point on target"
                        }
                        $__npMatchedRoot = $__npRootFull
                        break
                    }
                }

                if ($null -eq $__npMatchedRoot) {
                    throw "File System Operation: $Label '$Candidate' is not within any configured FileSystemOperation:AllowedRoots on target"
                }
            }

            function Get-NodePilotEffectiveDestination {
                param(
                    [Parameter(Mandatory = $true)][string]$Source,
                    [Parameter(Mandatory = $true)][string]$Destination,
                    [Parameter(Mandatory = $true)][string]$Label
                )

                # Copy-Item/Move-Item append the source leaf when Destination already names a
                # directory. Validate that effective leaf as well: an existing junction at
                # destination\sourceLeaf must never redirect the operation.
                Assert-NodePilotAllowedPath -Candidate $Destination -Label $Label
                $__npDestinationFull = [System.IO.Path]::GetFullPath($Destination)
                $__npDestinationAttributes = Get-NodePilotPathAttributes -Path $__npDestinationFull
                if ($null -ne $__npDestinationAttributes -and
                    ($__npDestinationAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    $__npSourceFull = [System.IO.Path]::GetFullPath($Source)
                    $__npSourceVolume = [System.IO.Path]::GetPathRoot($__npSourceFull)
                    if ($__npSourceFull.Length -gt $__npSourceVolume.Length) {
                        $__npSourceFull = $__npSourceFull.TrimEnd([char[]]@(
                            [System.IO.Path]::DirectorySeparatorChar,
                            [System.IO.Path]::AltDirectorySeparatorChar))
                    }
                    $__npSourceLeaf = [System.IO.Path]::GetFileName($__npSourceFull)
                    if ([string]::IsNullOrEmpty($__npSourceLeaf)) {
                        throw "File System Operation: cannot derive a destination name from source '$Source'"
                    }
                    $__npEffectiveDestination = [System.IO.Path]::Combine(
                        $__npDestinationFull,
                        $__npSourceLeaf)
                } else {
                    $__npEffectiveDestination = $__npDestinationFull
                }

                Assert-NodePilotAllowedPath -Candidate $__npEffectiveDestination -Label "$Label effective path"
                return $__npEffectiveDestination
            }

            function Assert-NodePilotReparseFreeTree {
                param(
                    [Parameter(Mandatory = $true)][string]$Root,
                    [Parameter(Mandatory = $true)][string]$Label
                )

                $__npPendingDirectories = New-Object 'System.Collections.Generic.Stack[string]'
                $__npPendingDirectories.Push([System.IO.Path]::GetFullPath($Root))
                while ($__npPendingDirectories.Count -gt 0) {
                    $__npDirectory = $__npPendingDirectories.Pop()
                    Assert-NodePilotAllowedPath -Candidate $__npDirectory -Label $Label
                    $__npDirectoryAttributes = Get-NodePilotPathAttributes -Path $__npDirectory
                    if ($null -eq $__npDirectoryAttributes -or
                        ($__npDirectoryAttributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                        throw "File System Operation: $Label directory changed or disappeared: '$__npDirectory'"
                    }
                    if (($__npDirectoryAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw "File System Operation: $Label tree contains reparse point '$__npDirectory'"
                    }

                    foreach ($__npChild in [System.IO.Directory]::EnumerateFileSystemEntries(
                        $__npDirectory,
                        '*',
                        [System.IO.SearchOption]::TopDirectoryOnly)) {
                        Assert-NodePilotAllowedPath -Candidate $__npChild -Label $Label
                        $__npChildAttributes = Get-NodePilotPathAttributes -Path $__npChild
                        if ($null -eq $__npChildAttributes) {
                            throw "File System Operation: $Label item changed or disappeared: '$__npChild'"
                        }
                        if (($__npChildAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                            throw "File System Operation: $Label tree contains reparse point '$__npChild'"
                        }
                        if (($__npChildAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                            $__npPendingDirectories.Push($__npChild)
                        }
                    }
                }
            }
            {{assertions}}
            """;
    }
}
