# Agent read composition follow-up

Date: 2026-09-20. Branch: feature/ai-agent-activities.

The user requested broader reads without replacing PowerShell/CMD/Bash or
maintaining command registrations. The comparison with E:/swvagent was read-only.
Its AST validation and data-processing support are relevant. Its verb-based
Get/Test command approval does not inspect the requested CIM class and would not
exclude Win32_Product. Its FullLanguage runspace and execution-policy Bypass are
not adopted. No files or processes in that project were changed.

## Changes

- Inline literal PowerShell bindings are substituted into canonical commands;
  arbitrary assignments, methods, loops and dynamic invocation remain denied.
- Parameter-based Where-Object, JSON/CSV decoding, Get-ItemPropertyValue,
  noninteractive ErrorAction and event XPath/Oldest options are supported.
- Single-class WQL SELECT is translated to the checked class/property/filter
  query. The namespace/class contract still rejects Win32_Product.
- Bash pipelines validate every stage and retain fixed executable paths.
- Agent-called workflows admit checked WMI query/SELECT and bodyless HTTP
  GET/HEAD. WMI execution canonicalizes its query; HTTP does not follow redirects.
  The executor checks and post-substitution step guard retain read-only enforcement.
- Model tool descriptions advertise the supported syntax.

## Verification

217 targeted tests passed: permission policy, workflow scope, WMI activity,
REST redirect activity and skills. Tests include actual PowerShell/Bash pipelines
with unchanged fixture contents, rejected write operations and dynamic code,
WMI canonicalization, and an HTTP handler proving no redirect/POST dispatch.
No new CLIENT1 fault or remote acceptance run was performed for this change.

## Limits

This is broader checked read composition, not universal classification of arbitrary
code. Unknown provider semantics, complex scripts and unclassified workflow
activities remain denied. SQL SELECT text alone, arbitrary Get-* names and MCP
annotations do not prove absence of side effects. The user need not maintain a
whitelist, but new command families still require a reviewed host implementation.

Packaged scripts execute their original bytes for integrity/signature checks;
inline binding substitution is therefore not extended to mutable package scripts.
Remote machine/credential overrides, policy bypass and write mode remain disabled.
The original request for literally all possible read operations is not fully
satisfied, and this report does not label it complete.
