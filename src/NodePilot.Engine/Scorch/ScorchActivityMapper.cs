using System.Globalization;
using System.Xml.Linq;
using NodePilot.Core.Activities;

namespace NodePilot.Engine.Scorch;

/// <summary>
/// Translates a SCOrch activity <c>&lt;Object&gt;</c> into NodePilot activity metadata.
///
/// <para>Classification is driven by <c>&lt;ObjectTypeName&gt;</c>, the human-readable type string.
/// The names here are the ones SCOrch actually writes, which are not always the ones its designer
/// shows: "Invoke Runbook" is <c>Trigger Policy</c> on the wire, and the property names differ from
/// the designer labels too (<c>Run Program</c> carries <c>Program</c>/<c>Parameters</c>/
/// <c>StartupDir</c>, not <c>FilePath</c>/<c>Arguments</c>/<c>WorkingDirectory</c>).</para>
///
/// <para>Every builder degrades rather than guesses. <see cref="EnforceContract"/> checks the result
/// against the shipped config schema and turns a mapping that failed to fill a REQUIRED key back
/// into a placeholder. Without that rule a wrong property-name assumption produces a node that
/// looks correct and does nothing — which is exactly how <c>Run Program</c> imported with an empty
/// <c>filePath</c>.</para>
/// </summary>
internal static class ScorchActivityMapper
{
    public record Mapping(
        string ActivityType,
        Dictionary<string, object?> Config,
        string? OutputVariable = null,
        bool UsedHeuristic = false,
        bool Fallback = false,
        string? Note = null,
        string? TargetMachine = null,
        bool Disabled = false);

    private delegate Mapping Builder(XElement obj, Dictionary<string, string> props);

    /// <summary>
    /// SCOrch type name → builder. Case-insensitive because the type string is a display name and
    /// nothing guarantees its casing across versions; the previous <c>switch</c> was ordinal-exact.
    /// </summary>
    private static readonly Dictionary<string, Builder> Builders = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- shapes verified against a real SCOrch 2016 export -------------------------------
        ["Run .Net Script"] = (_, p) => BuildRunScript(p),
        ["Run Program"] = (_, p) => BuildRunProgram(p),
        ["Trigger Policy"] = BuildStartWorkflow,
        ["Invoke Runbook"] = BuildStartWorkflow,
        ["Query XML"] = (_, p) => BuildXmlQuery(p),
        ["Delete File"] = BuildDeleteFile,
        ["Delete Folder"] = (_, p) => BuildFolderOperation(p, "delete"),
        ["Generate Random Text"] = (_, p) => BuildGenerateText(p),
        ["Monitor File"] = (_, p) => BuildFileWatcher(p),
        ["Compare Values"] = (_, p) => BuildCompareValues(p),
        ["Initialize Data"] = (o, _) => BuildManualTrigger(o),
        ["Return Data"] = (o, _) => BuildReturnData(o),

        // --- already supported before ---------------------------------------------------------
        ["Send Email"] = (_, p) => BuildEmail(p),
        ["Monitor Date/Time"] = (_, p) => BuildScheduleTrigger(p),
        ["Get File Status"] = (_, p) => BuildFileStatus(p),
        ["Query Database"] = (_, p) => BuildSql(p, "Query", "SqlQuery", "Statement"),
        ["Write to Database"] = (_, p) => BuildSqlWrite(p),
        ["Invoke Web Services"] = (_, p) => BuildRestApi(p),
        ["Start/Stop Service"] = (_, p) => BuildService(p),
        ["Junction"] = (_, p) => BuildJunction(p),

        // --- standard activities with a clean NodePilot counterpart ---------------------------
        // Property names are probed with several candidates: unlike the block above these were not
        // observed in a real export, so a miss must degrade to a placeholder, never to an empty node.
        ["Copy File"] = (_, p) => BuildFileOperation(p, "copy"),
        ["Move File"] = (_, p) => BuildFileOperation(p, "move"),
        ["Rename File"] = (_, p) => BuildFileOperation(p, "rename"),
        ["Create Folder"] = (_, p) => BuildFolderOperation(p, "create"),
        ["Move Folder"] = (_, p) => BuildFolderOperation(p, "move"),
        ["Compress File"] = (_, p) => BuildZip(p, "compress"),
        ["Decompress File"] = (_, p) => BuildZip(p, "extract"),
        ["Append Line"] = (_, p) => BuildTextFileEdit(p, "append"),
        ["Insert Line"] = (_, p) => BuildTextFileEdit(p, "insert"),
        ["Delete Line"] = (_, p) => BuildTextFileEdit(p, "delete"),
        ["Search and Replace Text"] = (_, p) => BuildTextFileEdit(p, "replace"),
        ["Restart System"] = (_, p) => BuildPowerManagement(p),
        ["Query WMI"] = (_, p) => BuildWmiQuery(p),
        ["Monitor Folder"] = (_, p) => BuildFileWatcher(p),
        ["Monitor Event Log"] = (_, p) => BuildEventLogTrigger(p),
        ["Get Service Status"] = (_, p) => BuildService(p, forceAction: "status"),
    };

    /// <summary>
    /// SCOrch activities we recognise but deliberately do not map, with the reason. Naming them is
    /// the point: "Unrecognised SCOrch activity 'Run SSH Command'" tells an operator nothing they
    /// could not see on the node, whereas the reason says what to build instead.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnsupported = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Read Line"] = "NodePilot has no read-a-line activity — use runScript with Get-Content -TotalCount.",
        ["Get Lines"] = "NodePilot has no read-lines activity — use runScript with Get-Content.",
        ["Find Text"] = "NodePilot has no text-search activity — use runScript with Select-String.",
        ["Run SSH Command"] = "NodePilot is Windows/WinRM-only and has no SSH activity.",
        ["Monitor Service"] = "NodePilot has no service-monitor trigger — use scheduleTrigger plus serviceManagement(status), or waitForCondition(serviceRunning) for a one-shot wait.",
        ["Monitor Process"] = "NodePilot has no process-monitor trigger — poll with scheduleTrigger plus runScript.",
        ["Monitor WMI"] = "NodePilot has no WMI-event trigger — poll with scheduleTrigger plus wmiQuery.",
        ["Monitor Counter"] = "NodePilot has no performance-counter trigger.",
        ["Monitor Disk Space"] = "NodePilot has no disk-space trigger — poll with scheduleTrigger plus wmiQuery on Win32_LogicalDisk.",
        ["Monitor Internet Application"] = "NodePilot has no HTTP monitor trigger — poll with scheduleTrigger plus restApi, or waitForCondition(httpOk).",
        ["Check Schedule"] = "NodePilot has no schedule-gate activity — express the window in the scheduleTrigger's cron.",
        ["Apply XSLT"] = "NodePilot has no XSLT activity — use runScript.",
        ["Map Published Data"] = "Value mapping has no NodePilot counterpart — express it in the consuming step's template or in runScript.",
        ["Format Date/Time"] = "NodePilot has no date-formatting activity — use runScript with Get-Date -Format.",
        ["Print File"] = "NodePilot has no printing activity.",
        ["PGP Encrypt File"] = "NodePilot has no PGP activity.",
        ["PGP Decrypt File"] = "NodePilot has no PGP activity.",
        ["Get SNMP Variable"] = "NodePilot has no SNMP activity.",
        ["Set SNMP Variable"] = "NodePilot has no SNMP activity.",
        ["Send SNMP Trap"] = "NodePilot has no SNMP activity.",
        ["Monitor SNMP Trap"] = "NodePilot has no SNMP activity.",
        ["Send Syslog Message"] = "NodePilot has no syslog activity — use restApi or runScript.",
        ["Save Event Log"] = "NodePilot has no event-log export activity — use runScript with wevtutil.",
    };

    /// <summary>
    /// Property names that carry the machine an activity runs against. The value is copied verbatim
    /// into <c>data.targetMachineId</c>: MachineResolver matches a registered machine by Name or
    /// Hostname and otherwise synthesizes an ad-hoc WinRM target, so no GUID lookup is needed.
    /// </summary>
    private static readonly string[] ComputerKeys =
        ["ComputerName", "Computer", "TargetComputer", "RunOnComputer", "ServerName", "MachineName", "Host"];

    /// <summary>Values that mean "the engine host", where a WinRM target would be wrong.</summary>
    private static readonly HashSet<string> LocalComputerNames =
        new(StringComparer.OrdinalIgnoreCase) { "localhost", ".", "127.0.0.1", "::1" };

    /// <summary>Every SCOrch type name this mapper claims to translate. Drives the contract guard.</summary>
    internal static IReadOnlyCollection<string> SupportedTypeNames => Builders.Keys;

    /// <summary>
    /// Config key the placeholder carries deliberately outside any activity schema: the raw SCOrch
    /// property bag, so nothing is lost when an activity could not be mapped.
    /// <c>WorkflowSecretRedactor</c> knows it and masks it on read.
    /// </summary>
    internal const string RawPropertiesConfigKey = "scorchRaw";

    public static Mapping Map(XElement obj, Dictionary<string, string> rawProps)
    {
        // Normalize the comparer instead of trusting the caller's. SCOrch's own casing is not
        // stable (IncludeSubFolders vs. IncludeSubfolders in the same format), so every lookup here
        // is meant to be case-insensitive — and a caller passing an ordinal dictionary would
        // silently get different mappings than the importer does.
        var props = rawProps.Comparer == StringComparer.OrdinalIgnoreCase
            ? rawProps
            : new Dictionary<string, string>(rawProps, StringComparer.OrdinalIgnoreCase);

        var typeName = (obj.Element("ObjectTypeName")?.Value ?? "").Trim();
        var name = obj.Element("Name")?.Value ?? "";

        if (KnownUnsupported.TryGetValue(typeName, out var reason))
            return Placeholder(name, typeName, props, $"SCOrch '{typeName}' has no NodePilot equivalent. {reason}");

        var mapped = Builders.TryGetValue(typeName, out var build)
            ? build(obj, props)
            : Infer(props);

        if (mapped is null)
            return Placeholder(name, typeName, props,
                $"Unrecognised SCOrch activity '{typeName}' — imported as a disabled log placeholder.");

        var target = ProbeTargetMachine(props);
        if (target is not null) mapped = mapped with { TargetMachine = target };

        return EnforceContract(mapped, name, typeName, props);
    }

    /// <summary>
    /// Rejects a mapping that did not fill every REQUIRED config key of its target activity, per the
    /// shipped schema. This is what keeps a wrong property-name assumption from producing a node
    /// that looks configured and silently does nothing.
    /// </summary>
    private static Mapping EnforceContract(
        Mapping m, string name, string typeName, Dictionary<string, string> props)
    {
        if (m.Fallback) return m;

        var entry = ActivityConfigReference.TryGet(m.ActivityType);
        if (entry is null) return m;

        var missing = entry.ConfigKeys
            .Where(k => k.Required)
            .Where(k => !m.Config.TryGetValue(k.Key, out var v) || IsBlank(v))
            .Select(k => k.Key)
            .ToList();
        if (missing.Count == 0) return m;

        return Placeholder(name, typeName, props,
            $"SCOrch '{typeName}' looks like a NodePilot {m.ActivityType}, but the export carries no " +
            $"value for its required config ({string.Join(", ", missing)}) — imported as a disabled " +
            "placeholder rather than as an empty node.");
    }

    private static bool IsBlank(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        System.Collections.IDictionary d => d.Count == 0,
        System.Collections.ICollection c => c.Count == 0,
        _ => false,
    };

    /// <summary>
    /// The placeholder every un-mappable activity becomes. It is DISABLED on purpose: a log node
    /// always succeeds, so an enabled placeholder let a half-translated runbook run green from end
    /// to end. Disabled, it stops the branch and WorkflowAnalyzer reports the rest as unreachable.
    /// </summary>
    private static Mapping Placeholder(XElement obj, Dictionary<string, string> props, string note) =>
        Placeholder(
            obj.Element("Name")?.Value ?? "",
            (obj.Element("ObjectTypeName")?.Value ?? "").Trim(),
            props,
            note);

    private static Mapping Placeholder(
        string name, string typeName, Dictionary<string, string> props, string note) =>
        new(
            ActivityType: "log",
            Config: new Dictionary<string, object?>
            {
                ["level"] = "warning",
                ["message"] = $"[SCOrch import placeholder] Original activity: '{name}'. " +
                              $"ObjectTypeName: '{typeName}'. Properties: {string.Join(", ", props.Keys)}. " +
                              "Replace this with the appropriate NodePilot activity, then enable the node.",
                [RawPropertiesConfigKey] = props,
            },
            Fallback: true,
            Note: note,
            Disabled: true);

    // -------- per-activity builders ---------------------------------------------------

    private static Mapping BuildRunScript(Dictionary<string, string> p)
    {
        // SCOrch "Run .Net Script" exposes published variables via <PublishedData><ItemRoot><Entry>.
        // Those need no translation: NodePilot auto-captures every script-scope `$var` as
        // {{step.param.<var>}}, under the same name SCOrch published.
        var scriptType = FirstNonEmpty(p, "ScriptType");
        var body = FirstNonEmpty(p, "ScriptBody", "Script", "ScriptText");
        var isPowerShell = scriptType.Length == 0
            || scriptType.Equals("PowerShell", StringComparison.OrdinalIgnoreCase);

        var mapping = new Mapping(
            ActivityType: "runScript",
            Config: new()
            {
                ["script"] = body,
                // 'powershell' runs the body in Windows PowerShell 5.1, which is what the script was
                // written against. 'auto' would use the in-process runspace, where implicit WinPS
                // compatibility is off and Desktop-only modules fail.
                ["engine"] = "powershell",
                ["timeoutSeconds"] = 300,
            });

        if (isPowerShell) return mapping;

        // A VBScript/JScript/C# body is not PowerShell and must not be presented as if it were.
        // It is kept on the node so the operator can port it, but the node stays disabled.
        return mapping with
        {
            Disabled = true,
            Fallback = true,
            Note = $"SCOrch 'Run .Net Script' is {scriptType}, not PowerShell. The original body is on " +
                   "the node but will not run as-is — port it to PowerShell, then enable the node.",
        };
    }

    private static Mapping BuildRunProgram(Dictionary<string, string> p)
    {
        var program = FirstNonEmpty(p, "Program", "FilePath", "ProgramPath", "ApplicationPath");
        var arguments = FirstNonEmpty(p, "Parameters", "Arguments", "CommandLineArguments");
        var workingDirectory = FirstNonEmpty(p, "StartupDir", "WorkingDirectory", "StartInFolder");

        // SCOrch lets <Program> hold either an executable path or a whole command line
        // ("cmd /C attrib -h -r ..."). startProgram.filePath rejects anything that is not an
        // executable path, so a command line has to become a script instead of a broken node.
        var looksLikeCommandLine = arguments.Length == 0 && ContainsShellSyntax(program);
        if (looksLikeCommandLine)
        {
            return new Mapping(
                ActivityType: "runScript",
                Config: new()
                {
                    ["script"] = program,
                    ["engine"] = "powershell",
                    ["timeoutSeconds"] = 300,
                },
                Note: "SCOrch 'Run Program' held a full command line rather than an executable path, " +
                      "so it was imported as runScript — startProgram only accepts an executable path.");
        }

        return new Mapping(
            ActivityType: "startProgram",
            Config: new()
            {
                ["filePath"] = program,
                ["arguments"] = arguments,
                ["workingDirectory"] = workingDirectory,
                ["waitForExit"] = ParseBool(p, "WaitForCompletion", true),
                ["timeoutSeconds"] = 300,
            });
    }

    private static bool ContainsShellSyntax(string value) =>
        value.Length > 0 && (value.IndexOfAny(['|', '&', '>', '<']) >= 0 || value.Contains(' '));

    private static Mapping BuildEmail(Dictionary<string, string> p) =>
        new(
            ActivityType: "emailNotification",
            Config: new()
            {
                ["to"] = FirstNonEmpty(p, "To", "Recipients", "Recipient"),
                ["subject"] = FirstNonEmpty(p, "Subject"),
                ["body"] = FirstNonEmpty(p, "MessageContent", "Body", "Message"),
                ["isHtml"] = p.TryGetValue("MailFormat", out var mf) && mf == "1",
            },
            // The SCOrch activity carries its own SMTP host/port/TLS/sender. emailNotification reads
            // none of them — the relay comes from the installation's SMTP settings — so writing them
            // into the node would only look like they applied.
            Note: HasAny(p, "OutgoingServer", "SmtpServer", "SenderAddress", "From")
                ? $"SCOrch 'Send Email' sent via {FirstNonEmpty(p, "OutgoingServer", "SmtpServer")} as " +
                  $"{FirstNonEmpty(p, "SenderAddress", "From")}. NodePilot sends through the relay " +
                  "configured in Admin Settings; check it matches before enabling."
                : null);

    /// <summary>
    /// SCOrch Monitor Date/Time. Collapses Every{Day,Hour,Minute}Value to a total and emits a Quartz
    /// cron that approximates it.
    ///
    /// <para>Shapes are chosen so the result is always VALID and always armable: an increment above
    /// 59 in a minute field makes Quartz throw <c>FormatException</c> when the trigger is armed, and
    /// intervals below the scheduler's minimum are refused. "Every N days" has no cron form at all —
    /// <c>*/N</c> on day-of-month restarts each month — so it degrades to a daily fire.</para>
    /// </summary>
    private static Mapping BuildScheduleTrigger(Dictionary<string, string> p)
    {
        int days = ParseInt(p, "EveryDayValue", 0);
        int hours = ParseInt(p, "EveryHourValue", 0);
        int minutes = ParseInt(p, "EveryMinuteValue", 0);
        var totalMinutes = days * 24 * 60 + hours * 60 + minutes;

        string cron;
        string? note = null;
        if (totalMinutes <= 0)
        {
            cron = "0 0 * * * ?";
            note = "SCOrch 'Monitor Date/Time' carried no usable interval — imported as hourly.";
        }
        else if (totalMinutes < 60)
        {
            cron = $"0 0/{totalMinutes} * * * ?";
            if (60 % totalMinutes != 0)
                note = $"SCOrch fired every {totalMinutes} min, which no cron expresses exactly — " +
                       $"'{cron}' restarts the cycle each hour.";
        }
        else if (totalMinutes < 24 * 60)
        {
            var everyHours = totalMinutes / 60;
            var offset = totalMinutes % 60;
            cron = $"0 {offset} 0/{everyHours} * * ?";
            note = $"SCOrch fired every {totalMinutes} min — approximated as '{cron}'.";
        }
        else
        {
            cron = "0 0 0 * * ?";
            note = $"SCOrch fired every {totalMinutes / (24 * 60)} day(s). Cron has no multi-day " +
                   "interval, so this runs daily at midnight — adjust if that is too often.";
        }

        return new Mapping(
            ActivityType: "scheduleTrigger",
            Config: new() { ["cronExpression"] = cron },
            Note: note);
    }

    private static Mapping BuildFileWatcher(Dictionary<string, string> p)
    {
        var (watchType, narrowed) = MapWatchType(p);
        return new Mapping(
            ActivityType: "fileWatcherTrigger",
            Config: new()
            {
                ["directory"] = FirstNonEmpty(p, "Path", "DirectoryToMonitor", "Directory"),
                ["filter"] = ExtractFileFilter(p),
                ["watchType"] = watchType,
                ["includeSubdirectories"] = ParseBool(p, "IncludeSubFolders", ParseBool(p, "IncludeSubfolders", false)),
            },
            Note: narrowed
                ? "SCOrch watched several file events at once; NodePilot's fileWatcherTrigger takes a " +
                  "single watchType, so this was imported as 'any'."
                : null);
    }

    /// <summary>
    /// SCOrch keeps the file filter in a nested <c>&lt;Filters&gt;</c> XML document rather than in a
    /// plain string, so a flat property read returns the concatenated markup.
    /// </summary>
    private static string ExtractFileFilter(Dictionary<string, string> p)
    {
        var direct = FirstNonEmpty(p, "FileFilter", "Filter");
        if (direct.Length > 0) return direct;

        var raw = FirstNonEmpty(p, "Filters");
        if (raw.Length > 0 && raw.Contains('<'))
        {
            try
            {
                var value = XElement.Parse(raw).Descendants("FilterValue").FirstOrDefault()?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch (System.Xml.XmlException)
            {
                // Not parseable as XML — fall through to the catch-all filter.
            }
        }
        return "*";
    }

    private static (string WatchType, bool Narrowed) MapWatchType(Dictionary<string, string> p)
    {
        // Real exports express the event set as four independent booleans.
        var selected = new List<string>();
        if (ParseBool(p, "NotifyIfCreated", false)) selected.Add("created");
        if (ParseBool(p, "NotifyIfChanged", false)) selected.Add("changed");
        if (ParseBool(p, "NotifyIfDeleted", false)) selected.Add("deleted");
        if (ParseBool(p, "NotifyIfRenamed", false)) selected.Add("renamed");
        if (selected.Count == 1) return (selected[0], false);
        if (selected.Count > 1) return ("any", true);

        var raw = FirstNonEmpty(p, "WatchType", "TriggerEvent").ToLowerInvariant();
        return raw switch
        {
            "created" or "added" or "create" => ("created", false),
            "changed" or "modified" or "change" => ("changed", false),
            "deleted" or "removed" or "delete" => ("deleted", false),
            "renamed" or "rename" => ("renamed", false),
            "any" or "all" => ("any", false),
            _ => ("created", false),
        };
    }

    private static Mapping BuildFileStatus(Dictionary<string, string> p) =>
        new(
            ActivityType: "fileOperation",
            Config: new()
            {
                ["operation"] = "exists",
                ["path"] = FirstNonEmpty(p, "SourcePath", "Path", "FilePath", "FileName"),
            },
            Note: "SCOrch 'Get File Status' mapped to fileOperation(exists) — verify the path is " +
                  "reachable from the target machine.");

    private static Mapping BuildFileOperation(Dictionary<string, string> p, string operation)
    {
        var config = new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["path"] = FirstNonEmpty(p, "SourceFileName", "SourcePath", "Path", "FileName", "OriginFolder"),
        };
        if (operation is "copy" or "move")
            config["destination"] = FirstNonEmpty(p, "DestinationFolder", "DestinationPath", "Destination");
        if (operation == "rename")
            config["newName"] = FirstNonEmpty(p, "NewName", "DestinationFileName");
        return new Mapping("fileOperation", config);
    }

    /// <summary>
    /// SCOrch "Delete File" deletes the contents of a folder, optionally filtered by file age. Only
    /// the unfiltered form has a NodePilot counterpart; an age filter has none, and importing it as
    /// an unconditional delete would delete more than the runbook did.
    /// </summary>
    private static Mapping BuildDeleteFile(XElement obj, Dictionary<string, string> p)
    {
        var ageDays = ParseInt(p, "FileAgeDays", 0);
        if (ageDays > 0)
        {
            return Placeholder(obj, p,
                $"SCOrch 'Delete File' only deleted files older than {ageDays} day(s). " +
                "fileOperation(delete) has no age filter and would delete everything, so this needs a " +
                "runScript with Get-ChildItem | Where-Object LastWriteTime.");
        }

        return new Mapping(
            ActivityType: "fileOperation",
            Config: new()
            {
                ["operation"] = "delete",
                ["path"] = FirstNonEmpty(p, "OriginFolder", "SourcePath", "Path", "FileName"),
            });
    }

    private static Mapping BuildFolderOperation(Dictionary<string, string> p, string operation)
    {
        var config = new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["path"] = FirstNonEmpty(p, "Folder", "SourceFolder", "Path", "DirectoryPath"),
        };
        if (operation is "copy" or "move")
            config["destination"] = FirstNonEmpty(p, "DestinationFolder", "Destination");
        return new Mapping("folderOperation", config);
    }

    private static Mapping BuildZip(Dictionary<string, string> p, string operation) =>
        new(
            ActivityType: "zipOperation",
            Config: new()
            {
                ["operation"] = operation,
                ["source"] = FirstNonEmpty(p, "SourceFileName", "SourcePath", "Source", "FileName", "ArchiveName"),
                ["destination"] = FirstNonEmpty(p, "DestinationFolder", "DestinationPath", "Destination", "ArchiveName"),
            });

    private static Mapping BuildTextFileEdit(Dictionary<string, string> p, string operation)
    {
        var config = new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["path"] = FirstNonEmpty(p, "FileName", "Path", "FilePath"),
        };
        var text = FirstNonEmpty(p, "Text", "Line", "Content", "InsertText");
        if (operation is "append" or "insert") config["content"] = text;
        if (operation == "insert") config["lineNumber"] = ParseInt(p, "LineNumber", 1);
        if (operation == "delete") config["lineNumber"] = ParseInt(p, "LineNumber", 1);
        if (operation == "replace")
        {
            config["matchPattern"] = FirstNonEmpty(p, "SearchText", "FindText", "Pattern");
            config["replace"] = FirstNonEmpty(p, "ReplaceText", "ReplaceWith", "Replacement");
        }
        return new Mapping("textFileEdit", config);
    }

    private static Mapping BuildPowerManagement(Dictionary<string, string> p)
    {
        var action = FirstNonEmpty(p, "Action", "Operation", "RestartOption").ToLowerInvariant() switch
        {
            "shutdown" or "poweroff" or "turnoff" => "shutdown",
            "logoff" or "logout" => "logoff",
            "abort" or "cancel" => "abort",
            "hibernate" => "hibernate",
            _ => "restart",
        };
        return new Mapping(
            ActivityType: "powerManagement",
            Config: new()
            {
                ["action"] = action,
                ["force"] = ParseBool(p, "Force", false),
                ["delaySeconds"] = ParseInt(p, "Delay", 0),
            });
    }

    private static Mapping BuildWmiQuery(Dictionary<string, string> p) =>
        new(
            ActivityType: "wmiQuery",
            Config: new()
            {
                ["mode"] = "wql",
                ["query"] = FirstNonEmpty(p, "Query", "WQL", "WmiQuery"),
                ["namespace"] = FirstNonEmpty(p, "Namespace", "WmiNamespace"),
            });

    private static Mapping BuildEventLogTrigger(Dictionary<string, string> p) =>
        new(
            ActivityType: "eventLogTrigger",
            Config: new()
            {
                ["logName"] = FirstNonEmpty(p, "LogName", "EventLog") is var l && l.Length > 0 ? l : "Application",
                ["source"] = FirstNonEmpty(p, "Source", "EventSource"),
                ["entryType"] = FirstNonEmpty(p, "EntryType", "Level", "Type"),
                ["messagePattern"] = FirstNonEmpty(p, "Description", "MessagePattern"),
            });

    private static Mapping BuildXmlQuery(Dictionary<string, string> p)
    {
        var fromFile = ParseBool(p, "InputXmlFile", false);
        var config = new Dictionary<string, object?>
        {
            ["source"] = fromFile ? "file" : "inline",
            ["xpath"] = FirstNonEmpty(p, "XmlTag", "XPath", "Query"),
            ["resultMode"] = "all",
        };
        if (fromFile) config["path"] = FirstNonEmpty(p, "XmlFile", "FileName", "Path");
        else config["content"] = FirstNonEmpty(p, "XmlBlock", "Xml", "Content");
        return new Mapping("xmlQuery", config);
    }

    /// <summary>
    /// SCOrch "Generate Random Text" selects character classes independently. generateText offers
    /// preset modes instead, so a combination such as "upper-case letters plus digits" can only be
    /// widened to the nearest preset; the note says so rather than pretending the charset survived.
    /// </summary>
    private static Mapping BuildGenerateText(Dictionary<string, string> p)
    {
        bool upper = ParseBool(p, "UseUpperCase", false);
        bool lower = ParseBool(p, "UseLowerCase", false);
        bool numbers = ParseBool(p, "UseNumbers", false);
        bool symbols = ParseBool(p, "UseSymbols", false);
        bool letters = upper || lower;

        var mode = symbols ? "password"
            : letters && numbers ? "alphanumeric"
            : letters ? "alphabetic"
            : numbers ? "numeric"
            : "alphanumeric";

        var caseRestricted = letters && !(upper && lower);
        return new Mapping(
            ActivityType: "generateText",
            Config: new()
            {
                ["mode"] = mode,
                ["length"] = ParseInt(p, "StringLength", 16),
            },
            Note: caseRestricted
                ? $"SCOrch generated {(upper ? "upper" : "lower")}-case characters only; generateText's " +
                  $"'{mode}' mode uses both cases."
                : null);
    }

    private static Mapping BuildSql(Dictionary<string, string> p, params string[] queryKeys) =>
        new(
            ActivityType: "sql",
            Config: new()
            {
                ["provider"] = "sqlserver",
                // Deliberately no connectionString: the SCOrch value routinely embeds a password, and
                // copying it would persist that secret in the workflow definition. SqlActivity also
                // requires a named connectionRef unless the deployment opts out, so a raw string
                // would fail on a default install anyway.
                ["connectionRef"] = SanitizeRef(FirstNonEmpty(p, "ConnectionName", "DSN", "DataSource", "Database")),
                ["query"] = FirstNonEmpty(p, queryKeys),
                ["timeoutSeconds"] = 60,
            },
            Note: "SCOrch database connection details were not copied — point 'connectionRef' at a " +
                  "named connection in Admin Settings. Any password in the export is not carried over.");

    private static Mapping BuildSqlWrite(Dictionary<string, string> p) =>
        BuildSql(p, "Query", "SqlStatement", "Statement") with
        {
            Note = "SCOrch 'Write to Database' imported as sql — verify the statement is a valid " +
                   "mutation, and point 'connectionRef' at a named connection in Admin Settings.",
        };

    private static Mapping BuildRestApi(Dictionary<string, string> p) =>
        new(
            ActivityType: "restApi",
            Config: new()
            {
                ["url"] = FirstNonEmpty(p, "URL", "Url", "RequestUrl", "Endpoint"),
                ["method"] = FirstNonEmpty(p, "Method", "HttpMethod", "Verb") is var m && m.Length > 0
                    ? m.ToUpperInvariant() : "GET",
                ["body"] = FirstNonEmpty(p, "Body", "RequestBody", "Content"),
                ["timeoutSeconds"] = 60,
            });

    private static Mapping BuildService(Dictionary<string, string> p, string? forceAction = null) =>
        new(
            ActivityType: "serviceManagement",
            Config: new()
            {
                ["serviceName"] = FirstNonEmpty(p, "ServiceName", "Service"),
                ["action"] = forceAction ?? FirstNonEmpty(p, "Action", "Operation").ToLowerInvariant() switch
                {
                    "start" => "start",
                    "stop" => "stop",
                    "restart" => "restart",
                    _ => "status",
                },
            });

    private static Mapping BuildJunction(Dictionary<string, string> p) =>
        new(
            ActivityType: "junction",
            Config: new()
            {
                ["mode"] = ParseBool(p, "WaitForAll", true) ? "waitAll" : "waitAny",
            });

    /// <summary>
    /// SCOrch calls this "Trigger Policy" on the wire. The child is addressed by NAME: the export's
    /// PolicyObjectID is a SCOrch identifier that means nothing to NodePilot, and the last segment of
    /// PolicyPath is the runbook's name — which is what the imported child workflow is called.
    /// </summary>
    private static Mapping BuildStartWorkflow(XElement obj, Dictionary<string, string> p)
    {
        var path = FirstNonEmpty(p, "PolicyPath", "RunbookPath");
        var childName = path.Length > 0
            ? path.Split('\\', '/').Last().Trim()
            : FirstNonEmpty(p, "RunbookName", "PolicyName");

        return new Mapping(
            ActivityType: "startWorkflow",
            Config: new()
            {
                ["workflowNameOrId"] = childName,
                ["parameters"] = ExtractTriggerParameters(obj),
                ["waitForCompletion"] = ParseBool(p, "WaitToComplete", true),
                ["timeoutSeconds"] = 3600,
            });
    }

    /// <summary>
    /// The child runbook's inputs live in a nested <c>&lt;TRIGGER_POLICY_PARAMETERS&gt;</c> block, so
    /// the flat property bag cannot see them. They map straight onto <c>startWorkflow.parameters</c>;
    /// dropping them left every sub-runbook call without its arguments.
    /// </summary>
    private static Dictionary<string, object?> ExtractTriggerParameters(XElement obj)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var block = obj.Element("TRIGGER_POLICY_PARAMETERS");
        if (block is null) return parameters;

        foreach (var entry in block.Elements("Entry"))
        {
            var key = entry.Element("ParameterName")?.Value?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            parameters[key] = entry.Element("Value")?.Value ?? "";
        }
        return parameters;
    }

    /// <summary>
    /// SCOrch "Initialize Data" declares a runbook's inputs. It is the entry point of every invoked
    /// runbook, and NodePilot roots are exclusively trigger nodes — so without this mapping such a
    /// runbook imports with zero roots and cannot start at all.
    /// </summary>
    private static Mapping BuildManualTrigger(XElement obj)
    {
        var parameters = PublishedDataNames(obj)
            .Select(object (n) => new Dictionary<string, object?>
            {
                ["name"] = n,
                ["type"] = "string",
                ["required"] = false,
                ["default"] = "",
            })
            .ToList();

        return new Mapping(
            ActivityType: "manualTrigger",
            Config: new() { ["parameters"] = parameters });
    }

    private static Mapping BuildReturnData(XElement obj)
    {
        var names = PublishedDataNames(obj).ToList();
        if (names.Count == 0)
        {
            return Placeholder(obj, [],
                "SCOrch 'Return Data' declared no return values — add the workflow's outputs by hand.");
        }

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var n in names) data[n] = "";

        return new Mapping(
            ActivityType: "returnData",
            Config: new() { ["data"] = data },
            Note: "SCOrch 'Return Data' names the outputs but not where they come from — fill each " +
                  "value with the upstream reference it should return.");
    }

    /// <summary>Names declared in an object's <c>&lt;PublishedData&gt;&lt;ItemRoot&gt;</c> block.</summary>
    private static IEnumerable<string> PublishedDataNames(XElement obj)
    {
        var raw = obj.Element("PublishedData")?.Value;
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains('<')) yield break;

        XElement root;
        try { root = XElement.Parse(raw); }
        catch (System.Xml.XmlException) { yield break; }

        foreach (var entry in root.Descendants("Entry"))
        {
            var name = entry.Element("Variable")?.Value ?? entry.Element("Name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) yield return name.Trim();
        }
    }

    /// <summary>
    /// SCOrch "Compare Values" is a logic-only node with no NodePilot counterpart: the comparison it
    /// performs is re-expressed on the outgoing links' conditions, which the link translation
    /// handles. The node is kept as an info-level log so the original intent stays visible on canvas.
    /// </summary>
    private static Mapping BuildCompareValues(Dictionary<string, string> p)
    {
        var left = FirstNonEmpty(p, "StringToCompare", "ValueToCompare", "ValueA", "Value1");
        var op = FirstNonEmpty(p, "StringTestOption", "ValueTestOption", "ComparisonOperator", "Operator");
        var right = FirstNonEmpty(p, "StringToCompareTo", "ValueToCompareTo", "ValueB", "Value2");

        return new Mapping(
            ActivityType: "log",
            Config: new()
            {
                ["level"] = "info",
                ["message"] = $"[SCOrch Compare Values] Left='{left}', TestOption='{op}', Right='{right}'. " +
                              "The comparison itself is carried by the outgoing link conditions.",
            },
            Note: "SCOrch 'Compare Values' imported as log — the actual comparison lives on the " +
                  "outgoing link conditions.");
    }

    // -------- heuristics ----------------------------------------------------------------

    /// <summary>
    /// Last resort before the placeholder: the property bag sometimes reveals intent when the type
    /// name does not.
    ///
    /// <para>Every branch requires the EVIDENCE the target activity actually needs, not merely a
    /// suggestive key name. The previous rule fired on any key whose name contained "script", so an
    /// activity carrying a <c>ScriptType</c> or <c>ScriptTimeout</c> property became a runScript with
    /// an empty body — and because heuristics carried no note, nothing reported it. Each branch now
    /// states itself in the import report.</para>
    /// </summary>
    private static Mapping? Infer(Dictionary<string, string> p)
    {
        // A script BODY, never a key whose name merely contains "script": ScriptType and
        // ScriptTimeout do too, and the old rule turned any activity carrying one into a runScript
        // with an empty body.
        if (FirstNonEmpty(p, "ScriptBody", "Script", "ScriptText").Length > 0)
            return BuildRunScript(p) with { UsedHeuristic = true, Note = InferNote("runScript", "a script body") };

        if (HasAny(p, "To", "Recipients", "Recipient") && HasAny(p, "Subject", "Body", "MessageContent"))
            return BuildEmail(p) with { UsedHeuristic = true, Note = InferNote("emailNotification", "recipient and subject/body properties") };

        // For url/query the evidence is the VALUE, so a loose key match is safe: whatever the
        // property is called, a non-empty value lands in the required config key, and a miss is
        // caught by the contract check rather than producing an empty node.
        var url = Probe(p, ["URL", "Url", "RequestUrl", "Endpoint"], "url");
        if (url is not null)
        {
            var m = BuildRestApi(p);
            m.Config["url"] = url;
            return m with { UsedHeuristic = true, Note = InferNote("restApi", "a URL-shaped property") };
        }

        var query = Probe(p, ["Query", "SqlQuery", "Statement"], "query");
        if (query is not null)
        {
            var m = BuildSql(p, "Query", "SqlQuery", "Statement");
            m.Config["query"] = query;
            return m with { UsedHeuristic = true, Note = InferNote("sql", "a query-shaped property") };
        }

        if (FirstNonEmpty(p, "ServiceName", "Service").Length > 0)
            return BuildService(p) with { UsedHeuristic = true, Note = InferNote("serviceManagement", "a service-name property") };

        if (FirstNonEmpty(p, "Program", "FilePath", "ProgramPath", "ApplicationPath").Length > 0)
            return BuildRunProgram(p) with { UsedHeuristic = true, Note = InferNote("startProgram", "an executable-path property") };

        return null;
    }

    /// <summary>
    /// Exact candidate keys first, then any key whose NAME contains <paramref name="fragment"/> and
    /// whose value is non-empty. Returns null when nothing matched.
    /// </summary>
    private static string? Probe(Dictionary<string, string> p, string[] exact, string fragment)
    {
        var direct = FirstNonEmpty(p, exact);
        if (direct.Length > 0) return direct;

        foreach (var (key, value) in p)
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
                return value;
        return null;
    }

    private static string InferNote(string activityType, string evidence) =>
        $"Activity type was not recognised; inferred as {activityType} from {evidence}. Check the " +
        "configuration before enabling.";

    // -------- small helpers ----------------------------------------------------------

    private static string? ProbeTargetMachine(Dictionary<string, string> p)
    {
        var value = FirstNonEmpty(p, ComputerKeys).Trim();
        if (value.Length == 0 || LocalComputerNames.Contains(value)) return null;
        return value;
    }

    private static string FirstNonEmpty(Dictionary<string, string> p, params string[] keys)
    {
        foreach (var k in keys)
            if (p.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v))
                return v;
        return string.Empty;
    }

    private static bool HasAny(Dictionary<string, string> p, params string[] keys)
        => FirstNonEmpty(p, keys).Length > 0;

    /// <summary>Trims a SCOrch connection/DSN name down to the ref grammar, or returns null.</summary>
    private static string? SanitizeRef(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray())
            .Trim('_');
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static int ParseInt(Dictionary<string, string> p, string key, int fallback)
        => p.TryGetValue(key, out var v)
           && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    private static bool ParseBool(Dictionary<string, string> p, string key, bool fallback)
    {
        if (!p.TryGetValue(key, out var v)) return fallback;
        return v.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
}
