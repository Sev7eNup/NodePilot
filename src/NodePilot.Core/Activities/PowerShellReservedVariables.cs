namespace NodePilot.Core.Activities;

/// <summary>
/// Variable names a <c>runScript</c> step neither binds an upstream parameter to nor publishes
/// as an output parameter.
///
/// <para>Two groups, both load-bearing. PowerShell <b>automatic</b> variables carry engine state —
/// binding an upstream parameter named <c>error</c> would replace <c>$Error</c> for the whole user
/// script. <b>Preference</b> variables steer every cmdlet in the script, so binding one silently
/// rewrites its semantics.</para>
///
/// <para>The wrapper also snapshots its scope at run time, but that snapshot cannot stand in for
/// this list: measured in a default runspace it holds neither the preference variables nor the
/// automatics that only materialise while the script runs (<c>$_</c>, <c>$PSItem</c>,
/// <c>$foreach</c>, <c>$switch</c>, <c>$Matches</c>, <c>$Error</c>, <c>$LASTEXITCODE</c>).</para>
///
/// <para>This lives in Core because two consumers need the same list and Core may not depend on
/// Engine: the script wrapper (<c>NodePilot.Engine.PowerShell.PowerShellScriptWrapper</c>) and the
/// static data-bus analysis (<see cref="WorkflowDefinitions.WorkflowDataBusAnalyzer"/>), which
/// drives the designer's variable picker. The frontend mirror is
/// <c>src/nodepilot-ui/src/lib/upstreamVariables.ts</c>; <c>PowerShellReservedVariablesParityTests</c>
/// keeps the two in step.</para>
/// </summary>
public static class PowerShellReservedVariables
{
    /// <summary>Prefix the script wrapper reserves for its own variables.</summary>
    public const string InternalPrefix = "__np";

    /// <summary>PowerShell automatic variables.</summary>
    public static readonly IReadOnlyList<string> Automatic =
    [
        "_", "PSItem", "args", "input", "this", "foreach", "switch", "Matches",
        "Error", "LASTEXITCODE", "StackTrace", "MyInvocation", "PSBoundParameters",
        "PSCmdlet", "PSCommandPath", "PSScriptRoot", "PSVersionTable", "PID", "HOME", "PWD",
        "ExecutionContext", "Host", "ShellId", "ConsoleFileName", "PSCulture", "PSUICulture",
        "PSEdition", "PSHOME", "NestedPromptLevel", "OutputEncoding", "PSStyle",
        "true", "false", "null",
    ];

    /// <summary>Preference variables — binding any of these rewrites the script's own semantics.</summary>
    public static readonly IReadOnlyList<string> Preference =
    [
        "ErrorActionPreference", "WarningPreference", "VerbosePreference", "DebugPreference",
        "InformationPreference", "ProgressPreference", "ConfirmPreference", "WhatIfPreference",
        "ErrorView", "MaximumErrorCount", "MaximumAliasCount", "MaximumDriveCount",
        "MaximumFunctionCount", "MaximumVariableCount", "PSDefaultParameterValues",
        "PSModuleAutoLoadingPreference", "PSNativeCommandUseErrorActionPreference",
        "PSNativeCommandArgumentPassing", "PSEmailServer", "PSSessionApplicationName",
        "PSSessionConfigurationName", "PSSessionOption", "Transcript",
    ];

    /// <summary>Both groups, ordinal-case-insensitive as PowerShell resolves variable names.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([.. Automatic, .. Preference], StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a reserved name or anything in the wrapper's internal namespace.</summary>
    public static bool IsReserved(string? name) =>
        !string.IsNullOrEmpty(name)
        && (All.Contains(name) || name.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase));
}
