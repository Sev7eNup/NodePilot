# Example agent skills

## SCCM troubleshooting handbook

`sccm-troubleshooting/` is a portable, instruction-only skill with 40 numbered
failure/diagnostic patterns, targeted remediation guidance and verification steps.
It covers ConfigMgr, WSUS, Windows Update, content distribution, client cache,
task sequences and application detection. Repairs remain recommendations;
selecting this skill grants no mutation rights. No scripts or fixed lab identities
are included. See [SKILL.md](sccm-troubleshooting/SKILL.md) and the
[coverage record](../../docs/testing/sccm-troubleshooting-skill-coverage.md).

Package with forward-slash entry names and **SKILL.md at the ZIP root**; import as
version `1.0.2` in Settings → AI agents, then select it on the desired agent/member.
The folder itself is also usable by other Agent Skills-compatible hosts.
The ready-to-import [version 1.0.2 ZIP](sccm-troubleshooting-1.0.2.zip) contains the
same Markdown files. Versions 1.0.0 and 1.0.1 remain available for existing pinned references.

## Windows diagnostics example

To package the Windows diagnosis example from this directory:

```powershell
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$source = (Resolve-Path -LiteralPath .\windows-diagnostics).Path
$zip = [System.IO.Compression.ZipFile]::Open((Join-Path $PWD 'windows-diagnostics.zip'), 'Create')
try {
    Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($source.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entryName)
    }
} finally { $zip.Dispose() }
```

`Compress-Archive` in Windows PowerShell 5.1 writes `\` into entry names such as
`resources\log-locations.md`, and the import rejects those. The snippet above writes `/` instead.

Import the ZIP in Settings → AI agents with version `1.0.2`. SKILL.md must be at the ZIP
root. Select the imported version on an agent and select PowerShell to allow its inspection
script. For bounded file search/collection, separately select those native tools and
allow the relevant log directories. Bind the agent to a machine and credential.

Skills follow the SKILL.md/YAML instruction convention. NodePilot additionally binds a
versioned ZIP, integrity hash and execution rights. Installing a skill grants no tools.

The scripts use the current checked read language. `inspect.ps1` reads Windows Update
service states; `inspect-sccm.ps1` separately checks CcmExec when Configuration Manager
is relevant. Missing services cause a tool failure with error details, not an automatic
diagnosis of installation damage. Read file metadata and log excerpts using the existing
file tools and resource instructions.

On the target, `Restricted` blocks all PowerShell script files. `AllSigned` requires
signing the scripts with an appropriately trusted code-signing certificate **before**
packaging; preserve their bytes thereafter. NodePilot leaves policy and certificate
trust unchanged. It never falls back to executing a blocked script inline. Administrators
provide the appropriate target configuration; instruction-only skills work without it.
