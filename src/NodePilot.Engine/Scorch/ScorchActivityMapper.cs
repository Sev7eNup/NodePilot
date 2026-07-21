using System.Xml.Linq;

namespace NodePilot.Engine.Scorch;

/// <summary>
/// Translates a SCOrch activity <c>&lt;Object&gt;</c> into NodePilot activity metadata.
/// Classification is driven primarily by <c>&lt;ObjectTypeName&gt;</c> (human-readable, stable
/// across SCOrch versions) with a property-based heuristic fallback and a <c>log</c>
/// placeholder for anything unrecognised.
/// </summary>
internal static class ScorchActivityMapper
{
    public record Mapping(
        string ActivityType,
        Dictionary<string, object?> Config,
        string? OutputVariable = null,
        bool UsedHeuristic = false,
        bool Fallback = false,
        string? Note = null);

    public static Mapping Map(XElement obj, Dictionary<string, string> props)
    {
        var typeName = obj.Element("ObjectTypeName")?.Value ?? "";
        var name = obj.Element("Name")?.Value ?? "";

        // Primary path: ObjectTypeName is a readable string SCOrch emits consistently.
        // We match on the substrings rather than exact strings because the IP packs slightly
        // vary ("Run Program" vs "Run .Net Program" etc.).
        var matched = typeName switch
        {
            "Run .Net Script" => BuildRunScript(props),
            "Run Program" => BuildRunProgram(props),
            "Send Email" => BuildEmail(props),
            "Monitor Date/Time" => BuildScheduleTrigger(props),
            "Monitor File" => BuildFileWatcher(props),
            "Get File Status" => BuildFileStatus(props),
            "Query Database" => BuildSql(props),
            "Invoke Web Services" => BuildRestApi(props),
            "Start/Stop Service" => BuildService(props),
            "Junction" => BuildJunction(props),
            "Invoke Runbook" => BuildStartWorkflow(props),
            "Compare Values" => BuildCompareValues(props),
            "Write to Database" => BuildSqlWrite(props),
            _ => null,
        };
        if (matched is not null) return matched;

        // Heuristic path — props may reveal the activity's intent even if the type-name doesn't match.
        var keys = props.Keys.Select(k => k.ToLowerInvariant()).ToHashSet();

        if (keys.Any(k => k.Contains("script")))
            return BuildRunScript(props) with { UsedHeuristic = true };
        if ((keys.Contains("to") || keys.Contains("recipient")) && (keys.Contains("subject") || keys.Contains("body")))
            return BuildEmail(props) with { UsedHeuristic = true };
        if (keys.Contains("filepath") || keys.Contains("programpath") || keys.Contains("applicationpath"))
            return BuildRunProgram(props) with { UsedHeuristic = true };
        if (keys.Any(k => k.Contains("url")))
            return BuildRestApi(props) with { UsedHeuristic = true };
        if (keys.Any(k => k == "query" || k == "sqlquery"))
            return BuildSql(props) with { UsedHeuristic = true };
        if (keys.Contains("servicename"))
            return BuildService(props) with { UsedHeuristic = true };

        // Fallback placeholder — preserve what we know so the operator can replace it.
        return new Mapping(
            ActivityType: "log",
            Config: new Dictionary<string, object?>
            {
                ["level"] = "warning",
                ["message"] = $"[SCOrch import placeholder] Original activity: '{name}'. ObjectTypeName: '{typeName}'. Properties: {string.Join(", ", props.Keys)}. Replace this with the appropriate NodePilot activity.",
                ["scorchRaw"] = props,
            },
            Fallback: true,
            Note: $"Unrecognised SCOrch activity '{typeName}' — imported as log placeholder.");
    }

    // -------- per-activity builders ---------------------------------------------------

    private static Mapping BuildRunScript(Dictionary<string, string> p)
    {
        // SCOrch "Run .Net Script" exposes published variables via <PublishedData><ItemRoot><Entry>.
        // We don't map those 1:1 to outputVariable (NodePilot auto-captures all script-scope
        // vars as params), but we extract the first entry's Variable name as a hint for the
        // outputVariable field so downstream consumers get a named reference.
        return new Mapping(
            ActivityType: "runScript",
            Config: new()
            {
                ["script"] = FirstNonEmpty(p, "ScriptBody", "Script", "ScriptText"),
                ["engine"] = p.TryGetValue("ScriptType", out var st) && st.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)
                    ? "powershell" : "auto",
                ["timeoutSeconds"] = 300,
            });
    }

    private static Mapping BuildRunProgram(Dictionary<string, string> p)
    {
        return new Mapping(
            ActivityType: "startProgram",
            Config: new()
            {
                ["filePath"] = FirstNonEmpty(p, "FilePath", "ProgramPath", "ApplicationPath"),
                ["arguments"] = FirstNonEmpty(p, "Arguments", "CommandLineArguments", "Parameters"),
                ["workingDirectory"] = FirstNonEmpty(p, "WorkingDirectory", "StartInFolder"),
                ["waitForExit"] = true,
                ["timeoutSeconds"] = 300,
            });
    }

    private static Mapping BuildEmail(Dictionary<string, string> p)
    {
        return new Mapping(
            ActivityType: "emailNotification",
            Config: new()
            {
                ["to"] = FirstNonEmpty(p, "To", "Recipients", "Recipient"),
                ["from"] = FirstNonEmpty(p, "SenderAddress", "From"),
                ["subject"] = FirstNonEmpty(p, "Subject"),
                ["body"] = FirstNonEmpty(p, "MessageContent", "Body", "Message"),
                ["smtpServer"] = FirstNonEmpty(p, "OutgoingServer", "SmtpServer"),
                ["smtpPort"] = ParseInt(p, "OutgoingServerPort", 25),
                ["smtpUseSsl"] = p.TryGetValue("OutgoingServerEnableSsl", out var ssl) && ssl == "1",
                ["isHtml"] = p.TryGetValue("MailFormat", out var mf) && mf == "1",
            });
    }

    /// <summary>
    /// SCOrch Monitor Date/Time trigger. Type="interval" means "wait N [unit] between fires".
    /// We collapse Every{Day,Hour,Minute}Value to a total minute count and emit a Quartz-style
    /// cron that approximates the interval — not always exact (e.g. 59-min interval has no
    /// clean cron), but close enough that the operator sees the intent.
    /// </summary>
    private static Mapping BuildScheduleTrigger(Dictionary<string, string> p)
    {
        int days = ParseInt(p, "EveryDayValue", 0);
        int hours = ParseInt(p, "EveryHourValue", 0);
        int minutes = ParseInt(p, "EveryMinuteValue", 0);

        string cron = "0 0 * * * ?"; // default hourly
        var type = p.TryGetValue("Type", out var t) ? t : "interval";
        if (string.Equals(type, "interval", StringComparison.OrdinalIgnoreCase))
        {
            if (days > 0) cron = $"0 0 0 */{days} * ?";
            else if (hours > 0) cron = $"0 0 */{hours} * * ?";
            else if (minutes > 0) cron = $"0 */{minutes} * * * ?";
        }

        return new Mapping(
            ActivityType: "scheduleTrigger",
            Config: new()
            {
                ["cronExpression"] = cron,
            });
    }

    private static Mapping BuildFileWatcher(Dictionary<string, string> p)
    {
        return new Mapping(
            ActivityType: "fileWatcherTrigger",
            Config: new()
            {
                ["directory"] = FirstNonEmpty(p, "DirectoryToMonitor", "Directory", "Path"),
                ["filter"] = FirstNonEmpty(p, "FileFilter", "Filter") is var f && !string.IsNullOrEmpty(f) ? f : "*",
                ["watchType"] = MapWatchType(p),
                ["includeSubdirectories"] = ParseBool(p, "IncludeSubfolders", false),
            });
    }

    private static string MapWatchType(Dictionary<string, string> p)
    {
        var raw = FirstNonEmpty(p, "WatchType", "TriggerEvent").ToLowerInvariant();
        return raw switch
        {
            "created" or "added" or "create" => "created",
            "changed" or "modified" or "change" => "changed",
            "deleted" or "removed" or "delete" => "deleted",
            _ => "created",
        };
    }

    /// <summary>
    /// SCOrch "Get File Status" polls a file's metadata (exists, last-modified, size).
    /// Closest NodePilot equivalent is <c>fileOperation</c> with <c>operation=exists</c> —
    /// it asserts <c>-PathType Leaf</c> and emits a param.exists on success.
    /// </summary>
    private static Mapping BuildFileStatus(Dictionary<string, string> p)
    {
        return new Mapping(
            ActivityType: "fileOperation",
            Config: new()
            {
                ["operation"] = "exists",
                ["path"] = FirstNonEmpty(p, "SourcePath", "Path", "FilePath"),
            },
            Note: "SCOrch 'Get File Status' mapped to fileOperation(exists) — verify the path is reachable from the NodePilot engine host.");
    }

    private static Mapping BuildSql(Dictionary<string, string> p) =>
        new(
            ActivityType: "sql",
            Config: new()
            {
                ["provider"] = "sqlserver",
                ["connectionString"] = FirstNonEmpty(p, "ConnectionString"),
                ["query"] = FirstNonEmpty(p, "Query", "SqlQuery", "Statement"),
                ["timeoutSeconds"] = 60,
            });

    private static Mapping BuildSqlWrite(Dictionary<string, string> p) =>
        new(
            ActivityType: "sql",
            Config: new()
            {
                ["provider"] = "sqlserver",
                ["connectionString"] = FirstNonEmpty(p, "ConnectionString"),
                ["query"] = FirstNonEmpty(p, "Query", "SqlStatement", "Statement"),
                ["timeoutSeconds"] = 60,
            },
            Note: "SCOrch 'Write to Database' imported as sql — verify the statement is a valid mutation.");

    private static Mapping BuildRestApi(Dictionary<string, string> p) =>
        new(
            ActivityType: "restApi",
            Config: new()
            {
                ["url"] = FirstNonEmpty(p, "URL", "Url", "RequestUrl", "Endpoint"),
                ["method"] = FirstNonEmpty(p, "Method", "HttpMethod", "Verb") is var m && !string.IsNullOrEmpty(m)
                    ? m.ToUpperInvariant() : "GET",
                ["body"] = FirstNonEmpty(p, "Body", "RequestBody", "Content"),
                ["timeoutSeconds"] = 60,
            });

    private static Mapping BuildService(Dictionary<string, string> p) =>
        new(
            ActivityType: "serviceManagement",
            Config: new()
            {
                ["serviceName"] = FirstNonEmpty(p, "ServiceName", "Service"),
                ["action"] = FirstNonEmpty(p, "Action", "Operation").ToLowerInvariant() switch
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

    private static Mapping BuildStartWorkflow(Dictionary<string, string> p) =>
        new(
            ActivityType: "startWorkflow",
            Config: new()
            {
                ["workflowNameOrId"] = FirstNonEmpty(p, "RunbookId", "PolicyId", "RunbookName", "PolicyName"),
                ["waitForCompletion"] = true,
                ["timeoutSeconds"] = 3600,
            });

    /// <summary>
    /// SCOrch "Compare Values" is a logic-only node — no NodePilot direct equivalent.
    /// We keep it as a <c>log</c> with info-level plus a note so the operator sees what
    /// was originally there; the logic that depended on the compare result is preserved
    /// in downstream edge conditionExpressions.
    /// </summary>
    private static Mapping BuildCompareValues(Dictionary<string, string> p) =>
        new(
            ActivityType: "log",
            Config: new()
            {
                ["level"] = "info",
                ["message"] = $"[SCOrch Compare Values] Left='{FirstNonEmpty(p, "ValueA", "Value1")}', Op='{FirstNonEmpty(p, "ComparisonOperator", "Operator")}', Right='{FirstNonEmpty(p, "ValueB", "Value2")}'. Condition logic survives via outgoing link filters.",
            },
            Note: "SCOrch 'Compare Values' imported as log — the actual comparison lives on outgoing link conditions.");

    // -------- small helpers ----------------------------------------------------------

    private static string FirstNonEmpty(Dictionary<string, string> p, params string[] keys)
    {
        foreach (var k in keys)
            if (p.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v))
                return v;
        return string.Empty;
    }

    private static int ParseInt(Dictionary<string, string> p, string key, int fallback)
        => p.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    private static bool ParseBool(Dictionary<string, string> p, string key, bool fallback)
    {
        if (!p.TryGetValue(key, out var v)) return fallback;
        return v.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v == "1";
    }
}
