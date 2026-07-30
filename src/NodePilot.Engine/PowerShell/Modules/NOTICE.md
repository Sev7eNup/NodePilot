# Bundled PowerShell modules

## Microsoft.PowerShell.Archive 1.2.5

- Source: PowerShell Gallery, `Microsoft.PowerShell.Archive` 1.2.5 (unmodified; the same version
  that full pwsh 7.x installations bundle). Files: module manifest, script module, en-US resources.
- License: MIT — https://github.com/PowerShell/Microsoft.PowerShell.Archive
- Copyright © Microsoft Corporation. All rights reserved.

Why it is bundled: the in-process runspace pool runs on the *PowerShell SDK* NuGet package, which
ships only the eight core modules — `Microsoft.PowerShell.Archive` is not among them. Without it,
a local `Compress-Archive`/`Expand-Archive` call (the `zipOperation` activity on the localhost
bypass path) triggered PowerShell's implicit Windows-PowerShell compatibility: a
`powershell.exe -Version 5.1 -s` child process per pool runspace whose `WinPSCompatSession` was
never closed — the memory/thread leak diagnosed on 2026-07-30. The module is imported eagerly into
every pool runspace via the InitialSessionState (see `RunspaceExecutionEngine`), so the module
auto-loader never goes looking for a Windows-PowerShell copy in the first place. Implicit WinCompat
is additionally disabled process-wide for the SDK via `powershell.config.json`
(`DisableImplicitWinCompat`) so future desktop-only cmdlets fail loudly instead of silently
spawning compatibility sessions.
