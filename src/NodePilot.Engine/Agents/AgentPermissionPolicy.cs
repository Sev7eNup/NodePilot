using System.Globalization;
using System.Management.Automation.Language;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Agents;

// A deliberately small language, not a blacklist of destructive command names.
internal static class AgentPermissionPolicy
{
    private sealed record ReadCommand(string Module, string? Positional, string Parameters);
    private static readonly Dictionary<string, ReadCommand> PowerShellCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Get-Content"] = new("Microsoft.PowerShell.Management", "LiteralPath", "LiteralPath Path TotalCount Tail Raw Encoding"),
        ["Get-ChildItem"] = new("Microsoft.PowerShell.Management", "LiteralPath", "LiteralPath Path Filter File Directory Force Recurse Depth"),
        ["Get-Item"] = new("Microsoft.PowerShell.Management", "LiteralPath", "LiteralPath Path Force"),
        ["Get-ItemProperty"] = new("Microsoft.PowerShell.Management", "LiteralPath", "LiteralPath Path Name"),
        ["Get-ItemPropertyValue"] = new("Microsoft.PowerShell.Management", "LiteralPath", "LiteralPath Path Name"),
        ["Get-Service"] = new("Microsoft.PowerShell.Management", "Name", "Name DisplayName"),
        ["Get-SmbShare"] = new("SmbShare", "Name", "Name Special"),
        ["Get-SmbShareAccess"] = new("SmbShare", "Name", "Name"),
        ["Get-Website"] = new("WebAdministration", "Name", "Name"),
        ["Get-WebApplication"] = new("WebAdministration", "Name", "Name Site"),
        ["Get-WebBinding"] = new("WebAdministration", "Name", "Name Protocol Port IPAddress HostHeader"),
        ["Get-WebAppPoolState"] = new("WebAdministration", "Name", "Name"),
        ["Get-WebConfigurationProperty"] = new("WebAdministration", null, "PSPath Location Filter Name"),
        ["Get-AuthenticodeSignature"] = new("Microsoft.PowerShell.Security", "LiteralPath", "LiteralPath FilePath"),
        ["Get-WindowsCapability"] = new("Dism", null, "Online Name"),
        ["Get-WindowsOptionalFeature"] = new("Dism", null, "Online FeatureName"),
        ["Get-Process"] = new("Microsoft.PowerShell.Management", "Name", "Name Id"),
        ["Get-TimeZone"] = new("Microsoft.PowerShell.Management", null, ""),
        ["Get-NetFirewallRule"] = new("NetSecurity", "Name", "Name DisplayName Enabled Direction Action PolicyStore"),
        ["Get-NetFirewallProfile"] = new("NetSecurity", "Name", "Name PolicyStore"),
        ["Get-NetFirewallPortFilter"] = new("NetSecurity", null, "PolicyStore"),
        ["Get-NetFirewallAddressFilter"] = new("NetSecurity", null, "PolicyStore"),
        ["Get-NetFirewallApplicationFilter"] = new("NetSecurity", null, "PolicyStore"),
        ["Get-NetFirewallServiceFilter"] = new("NetSecurity", null, "PolicyStore"),
        ["Get-NetConnectionProfile"] = new("NetConnection", null, "InterfaceAlias InterfaceIndex"),
        ["Get-NetIPConfiguration"] = new("NetTCPIP", "InterfaceAlias", "InterfaceAlias InterfaceIndex Detailed All"),
        ["Get-NetIPAddress"] = new("NetTCPIP", "IPAddress", "IPAddress InterfaceAlias InterfaceIndex AddressFamily Type PrefixOrigin SuffixOrigin AddressState"),
        ["Get-NetIPInterface"] = new("NetTCPIP", "InterfaceAlias", "InterfaceAlias InterfaceIndex AddressFamily ConnectionState Dhcp"),
        ["Get-NetRoute"] = new("NetTCPIP", "DestinationPrefix", "DestinationPrefix InterfaceAlias InterfaceIndex AddressFamily NextHop RouteMetric"),
        ["Find-NetRoute"] = new("NetTCPIP", "RemoteIPAddress", "RemoteIPAddress LocalIPAddress InterfaceIndex"),
        ["Get-NetTCPConnection"] = new("NetTCPIP", null, "LocalAddress LocalPort RemoteAddress RemotePort State OwningProcess"),
        ["Get-NetUDPEndpoint"] = new("NetTCPIP", null, "LocalAddress LocalPort OwningProcess"),
        ["Get-NetNeighbor"] = new("NetTCPIP", "IPAddress", "IPAddress InterfaceAlias InterfaceIndex AddressFamily State"),
        ["Get-NetAdapter"] = new("NetAdapter", "Name", "Name InterfaceDescription IncludeHidden Physical"),
        ["Get-NetAdapterStatistics"] = new("NetAdapter", "Name", "Name InterfaceDescription IncludeHidden"),
        ["Get-DnsClient"] = new("DnsClient", "InterfaceAlias", "InterfaceAlias InterfaceIndex"),
        ["Get-DnsClientServerAddress"] = new("DnsClient", "InterfaceAlias", "InterfaceAlias InterfaceIndex AddressFamily"),
        ["Get-DnsClientCache"] = new("DnsClient", null, "Entry Name Type Status Section"),
        ["Get-ScheduledTask"] = new("ScheduledTasks", "TaskName", "TaskName TaskPath"),
        ["Get-ScheduledTaskInfo"] = new("ScheduledTasks", "TaskName", "TaskName TaskPath"),
        ["Get-MpComputerStatus"] = new("Defender", null, ""),
        ["Get-MpPreference"] = new("Defender", null, ""),
        ["Get-MpThreatDetection"] = new("Defender", null, "ThreatID"),
        ["Get-Disk"] = new("Storage", "Number", "Number FriendlyName SerialNumber UniqueId"),
        ["Get-Partition"] = new("Storage", null, "DiskNumber PartitionNumber DriveLetter"),
        ["Get-Volume"] = new("Storage", "DriveLetter", "DriveLetter FileSystemLabel UniqueId"),
        ["Get-PhysicalDisk"] = new("Storage", "FriendlyName", "FriendlyName SerialNumber UniqueId"),
        ["Get-HotFix"] = new("Microsoft.PowerShell.Management", "Id", "Id Description"),
        ["Get-ComputerInfo"] = new("Microsoft.PowerShell.Management", "Property", "Property"),
        ["Get-Acl"] = new("Microsoft.PowerShell.Security", "Path", "Path LiteralPath Audit"),
        ["Test-Path"] = new("Microsoft.PowerShell.Management", "Path", "Path LiteralPath PathType IsValid"),
        ["Resolve-DnsName"] = new("DnsClient", "Name", "Name Type DnsOnly"),
        ["Get-CimInstance"] = new("CimCmdlets", "ClassName", "ClassName Namespace Property Filter Query"),
        ["Get-CimClass"] = new("CimCmdlets", "ClassName", "ClassName Namespace"),
        ["Get-WinEvent"] = new("Microsoft.PowerShell.Diagnostics", null, "LogName FilterHashtable FilterXPath MaxEvents Oldest ListProvider ListLog"),
        ["Get-FileHash"] = new("Microsoft.PowerShell.Utility", "LiteralPath", "LiteralPath Path Algorithm"),
        ["Get-Date"] = new("Microsoft.PowerShell.Utility", null, "Format"),
        ["Select-Object"] = new("Microsoft.PowerShell.Utility", "Property", "Property ExpandProperty First Last Skip Unique"),
        ["Where-Object"] = new("Microsoft.PowerShell.Core", "Property", "Property Value EQ NE GT GE LT LE Like NotLike Contains NotContains In NotIn Not"),
        ["ConvertFrom-Json"] = new("Microsoft.PowerShell.Utility", null, "InputObject"),
        ["ConvertFrom-Csv"] = new("Microsoft.PowerShell.Utility", null, "InputObject Delimiter Header"),
        ["Sort-Object"] = new("Microsoft.PowerShell.Utility", "Property", "Property Descending Unique CaseSensitive"),
        ["Group-Object"] = new("Microsoft.PowerShell.Utility", "Property", "Property NoElement CaseSensitive"),
        ["Format-List"] = new("Microsoft.PowerShell.Utility", "Property", "Property"),
        ["Format-Table"] = new("Microsoft.PowerShell.Utility", "Property", "Property AutoSize Wrap HideTableHeaders"),
        ["Out-String"] = new("Microsoft.PowerShell.Utility", null, "Width Stream"),
        ["Measure-Object"] = new("Microsoft.PowerShell.Utility", "Property", "Property Sum Average Minimum Maximum Line Word Character"),
        ["ConvertTo-Json"] = new("Microsoft.PowerShell.Utility", null, "Depth Compress"),
        ["Select-String"] = new("Microsoft.PowerShell.Utility", null, "LiteralPath Path Pattern SimpleMatch CaseSensitive List Context"),
        ["Write-Output"] = new("Microsoft.PowerShell.Utility", "InputObject", "InputObject")
    };
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    { "Raw", "File", "Directory", "Force", "Recurse", "Unique", "Sum", "Average", "Minimum", "Maximum", "Line", "Word", "Character", "Compress", "SimpleMatch", "CaseSensitive", "List", "DnsOnly", "Detailed", "All", "IncludeHidden", "Physical", "Audit", "IsValid", "Descending", "NoElement", "AutoSize", "Wrap", "HideTableHeaders", "Stream", "Online" };
    private static readonly HashSet<string> CimClasses = new(StringComparer.OrdinalIgnoreCase)
    { "Win32_OperatingSystem", "Win32_ComputerSystem", "Win32_Service", "Win32_Process", "Win32_LogicalDisk", "Win32_DiskDrive", "Win32_QuickFixEngineering", "Win32_NetworkAdapterConfiguration", "Win32_BIOS", "Win32_Processor", "Win32_PhysicalMemory", "Win32_NetworkAdapter", "Win32_DiskPartition", "Win32_Volume", "Win32_ComputerSystemProduct", "Win32_BaseBoard", "Win32_SystemDriver", "Win32_Environment", "Win32_PageFileUsage", "Win32_TimeZone", "Win32_Share" };
    private static readonly Dictionary<string, string[]> CimNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["root/SMS"] = ["SMS_ProviderLocation"],
        ["root/ccm"] = ["SMS_Client"],
        ["root/ccm/ClientSDK"] = ["CCM_SoftwareUpdate", "CCM_Application", "CCM_Program", "CCM_ServiceWindow"],
        ["root/ccm/Policy/Machine/ActualConfig"] = ["CCM_UpdateCIAssignment", "CCM_SoftwareUpdatesClientConfig", "CCM_SoftwareDistribution", "CCM_ServiceWindow", "CCM_Scheduler_ScheduledMessage"],
        ["root/ccm/SoftwareUpdates/UpdatesStore"] = ["CCM_UpdateStatus"],
        ["root/ccm/SoftMgmtAgent"] = ["CacheInfoEx", "CacheConfig"]
    };
    private static readonly string[] SiteReadClasses = ["SMS_SoftwareUpdate", "SMS_UpdateComplianceStatus", "SMS_CIDeploymentUnknownAssetDetails", "SMS_UpdatesAssignment", "SMS_Package", "SMS_DistributionDPStatus", "SMS_PackageStatusDistPointsSummarizer", "SMS_DistributionPoint", "SMS_Boundary", "SMS_BoundaryGroup", "SMS_BoundaryGroupMembers", "SMS_BoundaryGroupSiteSystems", "SMS_R_System", "SMS_Collection", "SMS_FullCollectionMembership", "SMS_Site", "SMS_Advertisement", "SMS_TaskSequencePackage", "SMS_DeploymentSummary", "SMS_Application", "SMS_DeploymentType", "SMS_ApplicationAssignment", "SMS_CollectionSettings", "SMS_ComponentSummarizer"];

    internal static string DescribePowerShellReads() => " Supported PowerShell commands and named parameters: "
        + string.Join("; ", PowerShellCommands.Select(x => x.Key + " [" + x.Value.Parameters + "]"))
        + ". Approved CIM classes: root/cimv2 [" + string.Join(",", CimClasses) + "]; "
        + string.Join("; ", CimNamespaces.Select(x => x.Key + " [" + string.Join(",", x.Value) + "]"))
        + "; root/SMS/site_<three-character-site-code> [" + string.Join(",", SiteReadClasses) + "]. Queries run only on the configured target; no CIM methods. Filter is a literal WQL WHERE condition, not a full query."
        + " Get-CimClass exposes property names for approved classes: inspect it after a query/schema error. Application/deployment-type instance reads include lazy properties. Get-WinEvent -ListProvider/-ListLog discovers actual local names before filtering events. Get-Item/Get-ChildItem can inspect IIS:\\AppPools and Cert:\\LocalMachine or Cert:\\CurrentUser. Get-WebConfigurationProperty reads local effective IIS system.webServer/system.applicationHost configuration. DISM commands only inspect -Online feature/capability state. Select-String is literal: use -SimpleMatch and an array of separate patterns, with optional bounded -Context; regex syntax without -SimpleMatch is rejected, never silently changed."
        + " UNC paths on the bound machine are resolved through its local disk-share mapping and read locally; this verifies local existence/content, not SMB connectivity or the access of a different service account. Read Get-SmbShare/Get-SmbShareAccess and Get-Acl separately when permissions matter. Other UNC hosts are rejected without a network connection: delegate to a member bound to the owning server. Missing evidence is not proof that a path is absent.";

    internal static string? AuthorizeTool(string name, JsonElement input)
    {
        switch (name)
        {
            case "files_list": case "files_read": case "files_search": case "logs_collect": case "logs_search": return null;
            case "http_request": case "workflow_run": return null; // Checked at their dispatch boundaries.
            case "powershell": case "cmd": case "bash": return PrepareShell(name, input.GetProperty("command").GetString()!);
            default: throw Denied($"Tool '{name}' has no verified read-only execution contract. Writes and unclassified external/workflow calls are currently disabled.");
        }
    }

    internal static void ValidateCimRead(string? className, string? ns, string? query = null)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (className is not null) values["ClassName"] = className;
        if (ns is not null) values["Namespace"] = ns;
        if (query is not null) values["Query"] = query;
        CheckParameters("Get-CimInstance", values);
    }

    internal static string PrepareShell(string shell, string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 64_000) throw Denied("Invalid shell command length.");
        return shell switch
        {
            "powershell" => PreparePowerShell(command, null),
            "bash" => PrepareBashPipeline(command),
            "cmd" => PrepareSimpleShell(shell, command),
            _ => throw Denied("Unsupported shell.")
        };
    }

    internal static void ValidateSkillScript(string shell, string script, string[] arguments)
    {
        if (shell == "powershell") { _ = PreparePowerShell(script, arguments); return; }
        if (arguments.Length != 0) throw Denied("CMD/Bash skill arguments require a reviewed binding contract; only literal read scripts are currently supported.");
        _ = PrepareShell(shell, script);
    }

    private static string PreparePowerShell(string script, string[]? arguments)
    {
        if (script.Length > 64_000) throw Denied("Script exceeds 64,000 characters.");
        var ast = Parser.ParseInput(script, out _, out var errors);
        if (errors.Length > 0 || ast.BeginBlock is not null || ast.ProcessBlock is not null || ast.CleanBlock is not null
            || ast.DynamicParamBlock is not null || ast.UsingStatements.Count != 0 || ast.ScriptRequirements is not null
            || ast.EndBlock is null || ast.EndBlock.Traps?.Count > 0)
            throw Denied("Only literal read commands and pipelines are supported; no initialization, imports or dynamic code.");
        var bindings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments?.Any(a => a is null || a.StartsWith('-')) == true)
            throw Denied("Skill arguments must be positional literal values, not named parameters.");
        if (ast.ParamBlock is not null)
        {
            if (arguments is null || ast.ParamBlock.Attributes.Count != 0 || arguments.Length > ast.ParamBlock.Parameters.Count)
                throw Denied("Unsupported script parameters.");
            for (var i = 0; i < ast.ParamBlock.Parameters.Count; i++)
            {
                var parameter = ast.ParamBlock.Parameters[i];
                if (parameter.Attributes.Any(a => a is not TypeConstraintAst type || type.TypeName.FullName is not ("string" or "System.String"))
                    || !parameter.Name.VariablePath.IsUnqualified)
                    throw Denied("Only plain string skill parameters are supported.");
                bindings.Add(parameter.Name.VariablePath.UserPath, i < arguments.Length ? arguments[i]
                    : parameter.DefaultValue is null ? "" : Literal(parameter.DefaultValue, bindings));
            }
        }
        else if (arguments?.Length > 0) throw Denied("The skill script declares no parameters.");
        var statements = new List<string>();
        var usesLocalShares = false;
        var usesIis = false;
        foreach (var statement in ast.EndBlock.Statements)
        {
            // Literal bindings are substituted, never executed as mutable PowerShell state.
            if (arguments is null && statement is AssignmentStatementAst assignment && assignment.Operator == TokenKind.Equals
                && assignment.Left is VariableExpressionAst local && local.VariablePath.IsUnqualified
                && Regex.IsMatch(local.VariablePath.UserPath, "^[a-zA-Z_][a-zA-Z0-9_]*$")
                && !new[] { "true", "false", "null" }.Contains(local.VariablePath.UserPath, StringComparer.OrdinalIgnoreCase)
                && assignment.Right is CommandExpressionAst assigned)
            {
                bindings[local.VariablePath.UserPath] = Literal(assigned.Expression, bindings);
                continue;
            }
            if (statement is not PipelineAst pipeline || pipeline.Background) throw Denied("Assignments, control flow and background execution are not supported.");
            var commands = new List<string>();
            string? firstCommand = null;
            foreach (var element in pipeline.PipelineElements)
            {
                if (element is not CommandAst command || command.InvocationOperator != TokenKind.Unknown || command.Redirections.Count != 0)
                    throw Denied("Dynamic invocation, expressions and redirection are not permitted.");
                var requested = command.GetCommandName() ?? "";
                if (requested.Equals("netsh", StringComparison.OrdinalIgnoreCase) || requested.Equals("netsh.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (commands.Count != 0 || !command.CommandElements.Skip(1).Select(x => Literal(x, bindings) as string)
                        .SequenceEqual(new[] { "winhttp", "show", "proxy" }, StringComparer.OrdinalIgnoreCase))
                        throw Denied("Only the local netsh winhttp show proxy read is supported.");
                    firstCommand = "netsh";
                    commands.Add("& ($env:SystemRoot + '\\System32\\netsh.exe') winhttp show proxy");
                    continue;
                }
                var name = requested.Contains('\\') ? requested[(requested.LastIndexOf('\\') + 1)..] : requested;
                if (!PowerShellCommands.TryGetValue(name, out var allowed)
                    || requested.Contains('\\') && !requested.Equals(allowed.Module + "\\" + name, StringComparison.OrdinalIgnoreCase))
                    throw Denied($"Command '{requested}' is not an approved read command.");
                var firewallAssociation = commands.Count == 1 && string.Equals(firstCommand, "Get-NetFirewallRule", StringComparison.OrdinalIgnoreCase)
                    && new[] { "Get-NetFirewallPortFilter", "Get-NetFirewallAddressFilter", "Get-NetFirewallApplicationFilter", "Get-NetFirewallServiceFilter" }.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (commands.Count > 0 && !firewallAssociation && !new[] { "Select-Object", "Where-Object", "Measure-Object", "ConvertTo-Json", "ConvertFrom-Json", "ConvertFrom-Csv", "Select-String", "Write-Output", "Sort-Object", "Group-Object", "Format-List", "Format-Table", "Out-String" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw Denied("Pipeline output may only feed formatting, selection or text matching commands, not another system query.");
                firstCommand ??= name;
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var permitted = allowed.Parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 1; i < command.CommandElements.Count; i++)
                {
                    var entry = command.CommandElements[i];
                    string key; object? value;
                    if (entry is CommandParameterAst parameter)
                    {
                        key = parameter.ParameterName;
                        if (!permitted.Contains(key, StringComparer.OrdinalIgnoreCase) && !key.Equals("ErrorAction", StringComparison.OrdinalIgnoreCase)) throw Denied($"Parameter '-{key}' is not approved for {name}.");
                        if (parameter.Argument is not null) value = Literal(parameter.Argument, bindings);
                        else if (Switches.Contains(key) || name.Equals("Where-Object", StringComparison.OrdinalIgnoreCase) && !new[] { "Property", "Value", "ErrorAction" }.Contains(key, StringComparer.OrdinalIgnoreCase) || key.Equals("Oldest", StringComparison.OrdinalIgnoreCase)) value = true;
                        else if (++i < command.CommandElements.Count) value = Literal(command.CommandElements[i], bindings);
                        else throw Denied("Missing parameter value.");
                    }
                    else { key = name.Equals("Where-Object", StringComparison.OrdinalIgnoreCase) && values.ContainsKey("Property") ? "Value" : allowed.Positional ?? throw Denied("Positional arguments are not supported for this command."); value = Literal(entry, bindings); }
                    if (!values.TryAdd(key, value)) throw Denied("Duplicate parameters are not supported.");
                }
                CheckParameters(name, values);
                foreach (var pathKey in new[] { "Path", "LiteralPath", "FilePath" })
                {
                    if (!values.TryGetValue(pathKey, out var path)) continue;
                    object? PreparePath(object? value)
                    {
                        if (value is string providerPath && providerPath.StartsWith("IIS:\\", StringComparison.OrdinalIgnoreCase)) usesIis = true;
                        if (value is not string text || !text.StartsWith(@"\\", StringComparison.Ordinal)) return value;
                        if (arguments is not null)
                            throw Denied("Packaged scripts execute their original bytes: use a local path, or resolve this UNC read through the inline PowerShell tool.");
                        usesLocalShares = true;
                        return new AgentLocalSharePath(text);
                    }
                    values[pathKey] = path is object?[] paths ? paths.Select(PreparePath).ToArray() : PreparePath(path);
                }
                // CIM coerces DMTF wildcard offsets into instants. Preserve raw scheduling values.
                var schedulingRead = name.Equals("Get-CimInstance", StringComparison.OrdinalIgnoreCase)
                    && values.GetValueOrDefault("ClassName") is string schedulingClass
                    && new[] { "CCM_UpdateCIAssignment", "CCM_SoftwareUpdate", "CCM_ServiceWindow", "SMS_UpdatesAssignment" }.Contains(schedulingClass, StringComparer.OrdinalIgnoreCase);
                var executable = schedulingRead ? "Microsoft.PowerShell.Management\\Get-WmiObject" : allowed.Module + "\\" + name;
                if (schedulingRead && values.Remove("ClassName", out var className)) values["Class"] = className;
                var lazyRead = name.Equals("Get-CimInstance", StringComparison.OrdinalIgnoreCase)
                    && values.GetValueOrDefault("ClassName") is string lazyClass
                    && new[] { "SMS_Application", "SMS_DeploymentType" }.Contains(lazyClass, StringComparer.OrdinalIgnoreCase);
                // The SMS provider rejects SDMPackageXML in enumeration projections. Fetch it
                // through the instance GET below; preserve other fields so invalid names still fail.
                if (lazyRead && values.GetValueOrDefault("Property") is object?[] projected)
                {
                    var enumerated = projected.Where(p => !string.Equals(p as string, "SDMPackageXML", StringComparison.OrdinalIgnoreCase)).ToArray();
                    values["Property"] = enumerated.Length == 0 ? new object?[] { "CI_ID" } : enumerated;
                }
                commands.Add(executable + string.Concat(values.Select(v => Switches.Contains(v.Key) || v.Value is bool
                    ? " -" + v.Key + ":" + Render(v.Value) : " -" + v.Key + " " + Render(v.Value))));
                if (lazyRead)
                    commands.Add("CimCmdlets\\Get-CimInstance");
                if (name.Equals("Get-Service", StringComparison.OrdinalIgnoreCase))
                    commands.Add("Microsoft.PowerShell.Core\\ForEach-Object { $npService = $_ | Microsoft.PowerShell.Utility\\Select-Object *; $npService.Status = $_.Status.ToString(); $npService.StartType = $_.StartType.ToString(); $npService }");
            }
            statements.Add(string.Join(" | ", commands));
        }
        if (statements.Count == 0) throw Denied("No read commands found.");
        return (usesLocalShares ? AgentLocalSharePath.Preamble + "\n" : "")
            + (usesIis ? "Microsoft.PowerShell.Core\\Import-Module WebAdministration -ErrorAction Stop; " : "") + string.Join("; ", statements);
    }

    private static object? Literal(Ast value, Dictionary<string, object?> bindings) => value switch
    {
        StringConstantExpressionAst text => text.Value,
        ConstantExpressionAst constant when constant.Value is int or long or double => constant.Value,
        VariableExpressionAst variable when variable.VariablePath.IsUnqualified && variable.VariablePath.UserPath.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        VariableExpressionAst variable when variable.VariablePath.IsUnqualified && variable.VariablePath.UserPath.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        VariableExpressionAst variable when variable.VariablePath.IsUnqualified && bindings.TryGetValue(variable.VariablePath.UserPath, out var bound) => bound,
        ArrayLiteralAst array => array.Elements.Select(x => Literal(x, bindings)).ToArray(),
        HashtableAst table => table.KeyValuePairs.ToDictionary(p => Literal(p.Item1, bindings) as string ?? throw Denied("Hashtable keys must be strings."),
            p => p.Item2 is PipelineAst { PipelineElements.Count: 1 } pipeline
                && pipeline.PipelineElements[0] is CommandExpressionAst expression ? Literal(expression.Expression, bindings) : throw Denied("Computed filter values are not permitted."), StringComparer.OrdinalIgnoreCase),
        _ => throw Denied("Only literal values and bound string skill parameters are supported; no expressions, interpolation or script blocks.")
    };

    private static string Render(object? value) => value switch
    {
        AgentLocalSharePath path => path.Render(),
        string text => PowerShellOperation.Literal(text),
        bool flag => flag ? "$true" : "$false",
        object?[] array => "@(" + string.Join(",", array.Select(Render)) + ")",
        Dictionary<string, object?> table => "@{" + string.Join(";", table.Select(x => PowerShellOperation.Literal(x.Key) + "=" + Render(x.Value))) + "}",
        int or long or double => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw Denied("Unsupported literal type.")
    };

    private static void CheckParameters(string name, Dictionary<string, object?> values)
    {
        if (values.TryGetValue("ErrorAction", out var action) && (action is not string preference
            || !new[] { "Stop", "Continue", "SilentlyContinue", "Ignore" }.Contains(preference, StringComparer.OrdinalIgnoreCase)))
            throw Denied("Unsupported ErrorAction; interactive and debugger actions are not permitted.");
        if (name.Equals("Resolve-DnsName", StringComparison.OrdinalIgnoreCase))
        {
            if (values.GetValueOrDefault("Name") is not string host || host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns)
                throw Denied("DNS diagnosis requires one literal hostname.");
            if (values.TryGetValue("Type", out var type) && (type is not string record || !new[] { "A", "AAAA", "A_AAAA", "CNAME" }.Contains(record, StringComparer.OrdinalIgnoreCase)))
                throw Denied("Only address and alias DNS records are supported.");
            if (values.TryGetValue("DnsOnly", out var dnsOnly) && dnsOnly is not true)
                throw Denied("DNS diagnosis does not permit protocol fallback.");
            values["DnsOnly"] = true;
        }
        if (name.StartsWith("Get-NetFirewall", StringComparison.OrdinalIgnoreCase))
        {
            if (values.TryGetValue("PolicyStore", out var store) && !string.Equals(store as string, "ActiveStore", StringComparison.OrdinalIgnoreCase))
                throw Denied("Firewall diagnosis reads only the local effective ActiveStore.");
            values["PolicyStore"] = "ActiveStore";
        }
        foreach (var key in new[] { "LiteralPath", "Path", "FilePath" })
            if (values.TryGetValue(key, out var path))
                foreach (var item in path is object?[] paths ? paths : new[] { path })
                    if (item is not string text || !(IsInspectionProviderPath(name, text) || AgentLocalSharePath.IsValid(text)
                        || Regex.IsMatch(text, @"^(?:[A-Za-z]:\\|HKLM:\\|HKCU:\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                        && !text.Contains("::", StringComparison.Ordinal) && text.Count(c => c == ':') == 1))
                        throw Denied("Use an explicit local path, HKLM/HKCU registry path or regular UNC path on the bound machine; device paths and alternate streams are prohibited.");
        if (name.Equals("Get-CimInstance", StringComparison.OrdinalIgnoreCase) || name.Equals("Get-CimClass", StringComparison.OrdinalIgnoreCase))
        {
            if (values.Remove("Query", out var query))
            {
                if (values.ContainsKey("ClassName") || values.ContainsKey("Filter") || values.ContainsKey("Property") || query is not string wql)
                    throw Denied("Use either a class read or a SELECT query.");
                var select = Regex.Match(wql, @"\A\s*SELECT\s+(?<properties>\*|[a-z_][a-z0-9_]*(?:\s*,\s*[a-z_][a-z0-9_]*)*)\s+FROM\s+(?<class>[a-z_][a-z0-9_]*)(?:\s+WHERE\s+(?<filter>.+))?\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (!select.Success) throw Denied("Only single-class SELECT queries are supported.");
                values["ClassName"] = select.Groups["class"].Value;
                if (select.Groups["properties"].Value != "*") values["Property"] = select.Groups["properties"].Value.Split(',').Select(x => (object?)x.Trim()).ToArray();
                if (select.Groups["filter"].Success) values["Filter"] = select.Groups["filter"].Value;
            }
            var cls = values.GetValueOrDefault("ClassName") as string;
            if (values.TryGetValue("Property", out var projection))
            {
                var properties = (projection is object?[] items ? items : new[] { projection })
                    .SelectMany(item => item is string text ? text.Split(',').Select(part => (object?)part.Trim()) : new[] { item }).ToArray();
                foreach (var property in properties)
                    if (property is not string field || !Regex.IsMatch(field, @"\A(?:\*|[a-zA-Z_][a-zA-Z0-9_]*)\z", RegexOptions.CultureInvariant))
                        throw Denied("Property accepts only simple field names or *, not expressions or query syntax.");
                values["Property"] = properties;
            }
            var ns = (values.GetValueOrDefault("Namespace") as string ?? "root/cimv2").Replace('\\', '/');
            var siteNamespace = Regex.IsMatch(ns, @"^root/sms/site_[a-z0-9]{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            // Schema inspection does not enumerate instances or invoke provider methods.
            var schemaRead = name.Equals("Get-CimClass", StringComparison.OrdinalIgnoreCase)
                && cls is not null && !cls.Equals("Win32_Product", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(cls, @"\A[a-zA-Z_][a-zA-Z0-9_]{0,127}\z", RegexOptions.CultureInvariant)
                && (ns.Equals("root/cimv2", StringComparison.OrdinalIgnoreCase) || CimNamespaces.ContainsKey(ns) || siteNamespace);
            if (cls is null || !(ns.Equals("root/cimv2", StringComparison.OrdinalIgnoreCase) && CimClasses.Contains(cls)
                || CimNamespaces.TryGetValue(ns, out var classes) && classes.Contains(cls, StringComparer.OrdinalIgnoreCase)
                || siteNamespace && SiteReadClasses.Contains(cls, StringComparer.OrdinalIgnoreCase) || schemaRead))
                throw Denied("CIM class/namespace is not approved for read-only access. Win32_Product and arbitrary WQL are prohibited.");
        }
        if (name.Equals("Get-WinEvent", StringComparison.OrdinalIgnoreCase))
        {
            if (values.ContainsKey("ListProvider") || values.ContainsKey("ListLog"))
            {
                if (values.Keys.Count(k => !k.Equals("ErrorAction", StringComparison.OrdinalIgnoreCase)) != 1)
                    throw Denied("Discover providers or logs separately from querying events.");
                return;
            }
            if (values.TryGetValue("FilterHashtable", out var filter))
            {
                if (values.ContainsKey("LogName")) throw new ArgumentException("Get-WinEvent cannot combine -LogName with -FilterHashtable. Put LogName inside the filter: -FilterHashtable @{LogName='System';Id=7040} -MaxEvents 20. Use your actual log and filter; the rejected query was not executed.");
                if (filter is not Dictionary<string, object?> table || table.Keys.Any(k => !new[] { "LogName", "ProviderName", "Id", "Level", "StartTime", "EndTime" }.Contains(k, StringComparer.OrdinalIgnoreCase)))
                    throw Denied("Unsupported event filter.");
                if (!table.ContainsKey("LogName")) throw new ArgumentException("Get-WinEvent -FilterHashtable requires LogName inside the hashtable. Add LogName to that filter, not as a separate parameter.");
            }
            else if (!values.ContainsKey("LogName")) throw Denied("Event queries require a log name.");
            if (!values.ContainsKey("MaxEvents")) values.Add("MaxEvents", 20);
            if (values["MaxEvents"] is not int max || max is < 1 or > 100) throw Denied("MaxEvents must be 1–100.");
        }
        if (name.Equals("Select-String", StringComparison.OrdinalIgnoreCase))
        {
            var patterns = values.GetValueOrDefault("Pattern") is object?[] array ? array : new[] { values.GetValueOrDefault("Pattern") };
            if (values.TryGetValue("SimpleMatch", out var simple) && simple is not true
                || !values.ContainsKey("SimpleMatch") && patterns.Any(p => p is string s && s.IndexOfAny(['.', '*', '+', '?', '|', '[', ']', '(', ')', '{', '}', '^', '$', '\\']) >= 0))
                throw Denied("Regex matching is not supported by this bounded read contract. Use Select-String -SimpleMatch for literal text, or supply an array of separate literal patterns instead of regex alternatives. No search was executed.");
            values["SimpleMatch"] = true;
            if (values.TryGetValue("Context", out var context))
            {
                var sizes = context is object?[] items ? items : new[] { context };
                if (sizes.Length is < 1 or > 2 || sizes.Any(s => s is not int n || n is < 0 or > 20))
                    throw Denied("Context accepts one or two line counts from 0 to 20.");
            }
        }
        if ((name.Equals("Get-WindowsCapability", StringComparison.OrdinalIgnoreCase) || name.Equals("Get-WindowsOptionalFeature", StringComparison.OrdinalIgnoreCase)) && values.GetValueOrDefault("Online") is not true)
            throw Denied("Servicing inspection requires -Online; offline images and servicing mutations are not supported.");
        if (name.Equals("Get-WebConfigurationProperty", StringComparison.OrdinalIgnoreCase))
        {
            if (values.GetValueOrDefault("PSPath") is not string psPath
                || !(psPath.Equals("MACHINE/WEBROOT/APPHOST", StringComparison.OrdinalIgnoreCase)
                    || !psPath.Contains("..", StringComparison.Ordinal) && Regex.IsMatch(psPath, @"\AIIS:\\(?:Sites(?:\\[^:\r\n]+)?)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                || values.GetValueOrDefault("Filter") is not string filter || filter.Contains('|') || filter.Contains("..", StringComparison.Ordinal) || filter.Contains("//", StringComparison.Ordinal)
                || !new[] { "system.webServer", "system.applicationHost" }
                    .Any(section => filter.Equals(section, StringComparison.OrdinalIgnoreCase) || filter.StartsWith(section + "/", StringComparison.OrdinalIgnoreCase))
                || values.GetValueOrDefault("Name") is not string property || !Regex.IsMatch(property, @"\A(?:[a-zA-Z][a-zA-Z0-9.]*|\*|\.)\z"))
                throw Denied("Use local MACHINE/WEBROOT/APPHOST or IIS:\\Sites with a system.webServer/system.applicationHost section and a literal property name. Remote configuration paths and XPath traversal/unions are not supported.");
        }
        if (values.TryGetValue("Depth", out var depth) && (depth is not int d || d is < 0 or > 10)) throw Denied("Depth must be 0–10.");
    }

    private static bool IsInspectionProviderPath(string command, string path)
        => (command.Equals("Get-Item", StringComparison.OrdinalIgnoreCase) || command.Equals("Get-ChildItem", StringComparison.OrdinalIgnoreCase))
            && !path.Contains("..", StringComparison.Ordinal) && !path.Contains("::", StringComparison.Ordinal)
            && Regex.IsMatch(path, @"\A(?:Cert:\\(?:LocalMachine|CurrentUser)|IIS:\\AppPools)(?:\\[^:]+)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string PrepareSimpleShell(string shell, string command)
    {
        if (command.IndexOfAny(['\r', '\n', ';', '|', '&', '<', '>', '`', '$', '%', '!', '^', '(', ')', '{', '}']) >= 0)
            throw Denied("Only one literal command is supported; expansion, chaining and redirection are prohibited.");
        var tokens = new List<string>(); var token = new StringBuilder(); char quote = '\0'; var started = false;
        foreach (var c in command.Trim())
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else token.Append(c); started = true; }
            else if (c == '"' || shell == "bash" && c == '\'') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c)) { if (started) { tokens.Add(token.ToString()); token.Clear(); started = false; } }
            else { token.Append(c); started = true; }
        }
        if (quote != '\0') throw Denied("Unclosed quote.");
        if (started) tokens.Add(token.ToString());
        if (tokens.Count == 0) throw Denied("Missing command.");
        var name = tokens[0]; var args = tokens.Skip(1).ToArray();
        if (shell == "cmd")
        {
            name = name.ToLowerInvariant();
            var native = name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
            if (native is "ipconfig" or "netstat" or "sc" or "tasklist" or "systeminfo" or "netsh")
            {
                var valid = native switch
                {
                    "ipconfig" => args.Length == 0 || args.Length == 1 && new[] { "/all", "/displaydns" }.Contains(args[0], StringComparer.OrdinalIgnoreCase),
                    "netstat" => args.All(a => Regex.IsMatch(a, "^-[anorb]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
                    "sc" => args.Length is 1 or 2 && new[] { "query", "queryex", "qc", "qdescription", "qfailure" }.Contains(args[0], StringComparer.OrdinalIgnoreCase)
                        && (args.Length == 1 || Regex.IsMatch(args[1], @"^[a-z0-9_. -]{1,256}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
                    "tasklist" => args.All(a => new[] { "/svc", "/v", "/nh" }.Contains(a, StringComparer.OrdinalIgnoreCase)),
                    "systeminfo" => args.Length == 0,
                    "netsh" => args.SequenceEqual(new[] { "winhttp", "show", "proxy" }, StringComparer.OrdinalIgnoreCase),
                    _ => false
                };
                if (!valid) throw Denied("Only local read arguments are supported for this Windows command.");
                return "\"%SystemRoot%\\System32\\" + native + ".exe\" " + string.Join(" ", args.Select(a => "\"" + a + "\""));
            }
            if (name is not ("type" or "dir" or "ver" or "echo" or "whoami" or "hostname")) throw Denied("CMD command is not an approved read command.");
            if (name is "ver" or "whoami" or "hostname" && args.Length != 0) throw Denied("This CMD command accepts no arguments in read-only mode.");
            if (name is "type" or "dir")
                foreach (var arg in args)
                    if (!(name == "dir" && new[] { "/b", "/a", "/s", "/-c" }.Contains(arg, StringComparer.OrdinalIgnoreCase))
                        && !Regex.IsMatch(arg, @"^[A-Za-z]:\\[^:]*$", RegexOptions.CultureInvariant))
                        throw Denied("CMD file reads require an explicit local path or an approved DIR option.");
            if (name == "type" && args.Length == 0) throw Denied("TYPE requires a file path.");
            if (name is "whoami" or "hostname") name = "\"%SystemRoot%\\System32\\" + name + ".exe\"";
            return name + " " + string.Join(" ", args.Select(a => "\"" + a + "\""));
        }
        if (name is not ("cat" or "head" or "tail" or "wc" or "grep" or "ls" or "pwd" or "uname" or "whoami" or "hostname" or "stat" or "sha256sum" or "echo" or "du" or "df" or "ps" or "id" or "readlink"))
            throw Denied("Bash command is not an approved read command.");
        if (name is "pwd" or "whoami" or "hostname" && args.Length != 0) throw Denied("This Bash command accepts no arguments in read-only mode.");
        if (name == "uname" && args.Any(a => !Regex.IsMatch(a, "^-[asnrvmpio]+$", RegexOptions.CultureInvariant))) throw Denied("Unsupported uname option.");
        if (name == "df" && args.Any(a => a.StartsWith("--s", StringComparison.Ordinal))) throw Denied("Filesystem synchronization is not a read operation.");
        if (args.Any(a => a.Contains('\\') || a.Contains('\0'))) throw Denied("Use literal POSIX paths in Bash commands.");
        return (name == "echo" ? "builtin echo" : "'/usr/bin/" + name + "'") + " " + string.Join(" ", args.Select(a => "'" + a.Replace("'", "'\"'\"'") + "'"));
    }

    private static string PrepareBashPipeline(string command)
    {
        var parts = new List<string>();
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') quote = c;
            else if (c == '|') { parts.Add(command[start..i]); start = i + 1; }
        }
        parts.Add(command[start..]);
        if (parts.Count > 16 || parts.Any(string.IsNullOrWhiteSpace)) throw Denied("Invalid read pipeline.");
        return string.Join(" | ", parts.Select(p => PrepareSimpleShell("bash", p)));
    }

    private static UnauthorizedAccessException Denied(string reason)
        => new("Agent read-only policy: " + reason);
}
