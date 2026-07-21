using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// WMI/CIM activity. Supports three modes (selected via <c>config.mode</c>):
/// <list type="bullet">
///   <item><description><c>query</c> (default) — class-based <c>Get-CimInstance</c> with
///     optional WHERE-style filter. Backwards-compatible with the original activity shape.</description></item>
///   <item><description><c>wql</c> — raw WQL via <c>Get-CimInstance -Query</c> against the chosen
///     namespace. Lets the user write the full SELECT including JOINs and ASSOCIATORS OF.</description></item>
///   <item><description><c>invokeMethod</c> — call a WMI method via <c>Invoke-CimMethod</c>.
///     Static methods are scoped by <c>className</c> alone; instance methods scope via
///     <c>filter</c> (the <c>Get-CimInstance | Invoke-CimMethod</c> pipe). Method arguments come
///     from <c>config.arguments</c> (a JSON object) and are emitted as a PowerShell hashtable
///     literal — keys are validated as PS identifiers, values are typed-quoted.</description></item>
/// </list>
/// All user-supplied strings flow through <see cref="PowerShellQuoter.Literal"/> before landing
/// in the script. Argument <b>keys</b> are restricted to <c>^[A-Za-z_][A-Za-z0-9_]*$</c> because
/// they're emitted unquoted into the hashtable literal — anything else would let the upstream
/// step inject arbitrary PS by naming an argument something like <c>x; rm -rf …</c>.
///
/// <para><b>captureProperties</b> (optional, added 2026-05-17): JSON array of CIM property
/// names to project into <see cref="ActivityResult.OutputParameters"/>. Without it the
/// activity only emits a formatted text output and downstream <c>{{step.param.X}}</c>
/// references can never resolve. With it the activity wraps the CIM call, captures the
/// first row's named properties (plus a <c>count</c> param with the row total), and
/// exposes each as <c>param.&lt;PropName&gt;</c>. Property names must match the same
/// identifier rules as method-argument keys — otherwise they'd land unquoted in the
/// projection script and become an injection vector.</para>
/// </summary>
public class WmiQueryActivity : BaseRemoteActivity
{
    public override string ActivityType => "wmiQuery";

    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("WMI");

    // Cap to keep the projected hashtable script bounded and to discourage authors
    // from blindly dumping the entire CIM class into params — that's what `.output`
    // is for. 50 covers every real-world CIM class shape we cared about during
    // design (Win32_OperatingSystem has ~60 properties total).
    internal const int MaxCaptureProperties = 50;

    public WmiQueryActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration configuration)
        : base(sessionFactory, credentialStore, db, engineFactory, configuration) { }

    protected override string BuildScript(JsonElement config, StepExecutionContext context)
    {
        var mode = (config.GetStringOrNull("mode") ?? "query").Trim().ToLowerInvariant();
        var ns = config.TryGetProperty("namespace", out var nsVal) ? nsVal.GetString() : "root\\cimv2";
        var qNs = PowerShellQuoter.Literal(ns);

        var coreScript = mode switch
        {
            "wql" => BuildWqlScript(config, qNs),
            "invokemethod" => BuildInvokeMethodScript(config, qNs),
            "query" => BuildQueryScript(config, qNs),
            _ => throw new InvalidOperationException($"WMI Query: unknown mode '{mode}' (expected 'query', 'wql', or 'invokeMethod')"),
        };

        var captureProperties = ParseCaptureProperties(config);
        if (captureProperties.Count == 0)
            return coreScript;

        return WrapWithCapture(coreScript, captureProperties);
    }

    /// <summary>
    /// Parses + validates <c>config.captureProperties</c>. Returns an empty list when the
    /// key is absent or explicitly null; throws on any other malformed input so a bad
    /// fixture fails loud instead of silently producing zero params.
    /// </summary>
    internal static IReadOnlyList<string> ParseCaptureProperties(JsonElement config)
    {
        if (!config.TryGetProperty("captureProperties", out var prop))
            return [];
        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (prop.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                "WMI Query: 'captureProperties' must be a JSON array of CIM property names (e.g. [\"Caption\", \"Name\"])");

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in prop.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("WMI Query: each 'captureProperties' entry must be a string");
            var name = entry.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var trimmed = name.Trim();
            if (!IdentifierPattern.IsMatch(trimmed))
                throw new InvalidOperationException(
                    $"WMI Query: capture property '{trimmed}' is not a valid CIM property identifier " +
                    "(letters/digits/underscore, must not start with a digit). " +
                    "This restriction prevents PowerShell injection through the projection script.");
            // Reserved key — would clash with our auto-emitted row counter.
            if (string.Equals(trimmed, "count", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "WMI Query: 'count' is a reserved capture property name (it's auto-populated with the row total).");
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        if (result.Count > MaxCaptureProperties)
            throw new InvalidOperationException(
                $"WMI Query: too many captureProperties ({result.Count}); the limit is {MaxCaptureProperties}. " +
                "Narrow the projection or use a downstream runScript step to drill into the full object.");

        return result;
    }

    /// <summary>
    /// Wraps the core CIM script with a capture envelope: collects all rows into an array,
    /// projects the first row's named properties into a hashtable, and emits a JSON
    /// payload between marker lines that PostProcess can extract into OutputParameters.
    /// The original CIM output (formatted-table view of all rows) still flows to stdout
    /// before the marker block so <c>{{step.output}}</c> stays useful for log inspection.
    /// </summary>
    private static string WrapWithCapture(string coreScript, IReadOnlyList<string> captureProperties)
    {
        // Each property name is already validated against IdentifierPattern, so emitting
        // them unquoted into the projection loop is safe — they cannot escape the script.
        var sb = new StringBuilder();
        sb.Append("$__npRows = @(& {").Append('\n');
        sb.Append(coreScript).Append('\n');
        sb.Append("})\n");
        sb.Append("$__npRows | Format-Table -AutoSize | Out-String\n");
        sb.Append("$__npResult = @{ Count = $__npRows.Count; Properties = @{} }\n");
        sb.Append("if ($__npRows.Count -gt 0) {\n");
        sb.Append("    $__npFirst = $__npRows[0]\n");
        foreach (var prop in captureProperties)
        {
            sb.Append("    $__npVal = $null\n");
            sb.Append("    if ($null -ne $__npFirst.PSObject -and ($__npFirst.PSObject.Properties.Name -contains '")
                .Append(prop).Append("')) { $__npVal = $__npFirst.").Append(prop).Append(" }\n");
            sb.Append("    $__npResult.Properties['").Append(prop).Append("'] = $__npVal\n");
        }
        sb.Append("}\n");
        sb.Append(ResultMarkers.RenderJsonEnvelope("$__npResult", depth: 4));
        return sb.ToString();
    }

    /// <summary>
    /// When captureProperties is set the script emits a JSON envelope after the formatted
    /// rows. Parse it, populate <c>OutputParameters</c> with <c>count</c> + each projected
    /// property, and strip the marker block from the user-visible output. When the envelope
    /// is missing the activity falls through to the inherited pass-through (no params, raw
    /// output preserved) — that matches the no-captureProperties branch's behaviour.
    /// </summary>
    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        var captureProperties = ParseCaptureProperties(config);
        if (captureProperties.Count == 0)
            return raw;

        var output = raw.Output ?? string.Empty;
        if (!PowerShellOperation.TryExtractJsonBlock(output, ResultMarkers, out var block))
            return raw;

        if (!PowerShellOperation.TryParseJson(block.Json, out var doc, out var parseError) || doc is null)
        {
            return new ActivityResult
            {
                Success = false,
                Output = block.LeadingOutput,
                ErrorOutput = $"WMI Query: could not parse capture envelope: {parseError}",
                Duration = raw.Duration,
            };
        }

        try
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var root = doc.RootElement;

            var count = root.TryGetProperty("Count", out var countEl) && countEl.TryGetInt32(out var n) ? n : 0;
            parameters["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (root.TryGetProperty("Properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
            {
                // Iterate the REQUESTED list (not the JSON object) so a missing key in the
                // JSON still surfaces as an empty-string param — the contract is "all
                // requested keys are always present in OutputParameters".
                foreach (var requested in captureProperties)
                {
                    if (propsEl.TryGetProperty(requested, out var valEl))
                        parameters[requested] = PowerShellOperation.JsonElementToScalarString(valEl);
                    else
                        parameters[requested] = string.Empty;
                }
            }
            else
            {
                foreach (var requested in captureProperties)
                    parameters[requested] = string.Empty;
            }

            return new ActivityResult
            {
                Success = raw.Success,
                Output = block.LeadingOutput,
                ErrorOutput = raw.ErrorOutput,
                Duration = raw.Duration,
                OutputParameters = parameters,
            };
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static string BuildQueryScript(JsonElement config, string qNs)
    {
        var className = config.GetStringOrNull("className");
        if (string.IsNullOrWhiteSpace(className))
            throw new InvalidOperationException("WMI Query: 'className' is required");

        var filter = config.GetStringOrNull("filter");
        var qClass = PowerShellQuoter.Literal(className);

        if (string.IsNullOrWhiteSpace(filter))
            return $"Get-CimInstance -ClassName {qClass} -Namespace {qNs}";

        var qFilter = PowerShellQuoter.Literal(filter);
        return $"$__npFilter = {qFilter}; Get-CimInstance -ClassName {qClass} -Namespace {qNs} -Filter $__npFilter";
    }

    private static string BuildWqlScript(JsonElement config, string qNs)
    {
        var query = config.GetStringOrNull("query");
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("WMI Query: 'query' is required for wql mode");

        var qQuery = PowerShellQuoter.Literal(query);
        return $"$__npQuery = {qQuery}; Get-CimInstance -Query $__npQuery -Namespace {qNs}";
    }

    private static string BuildInvokeMethodScript(JsonElement config, string qNs)
    {
        var className = config.GetStringOrNull("className");
        if (string.IsNullOrWhiteSpace(className))
            throw new InvalidOperationException("WMI Query: 'className' is required for invokeMethod mode");

        var methodName = config.GetStringOrNull("methodName");
        if (string.IsNullOrWhiteSpace(methodName))
            throw new InvalidOperationException("WMI Query: 'methodName' is required for invokeMethod mode");

        // Method names are PS-identifier-like (e.g. Create, Terminate, GetOwner). Reject anything
        // weirder so we can emit them unquoted into -MethodName.
        if (!IdentifierPattern.IsMatch(methodName))
            throw new InvalidOperationException($"WMI Query: 'methodName' must be a valid identifier, got '{methodName}'");

        var qClass = PowerShellQuoter.Literal(className);
        var qMethod = PowerShellQuoter.Literal(methodName);
        var filter = config.GetStringOrNull("filter");
        var argsLiteral = BuildArgumentsHashtable(config);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            // Instance method: locate target instances first, then pipe into Invoke-CimMethod.
            // Without -Filter the cmdlet would target the class itself (static-method semantics),
            // which is rarely what the user wants when they've supplied a filter.
            var qFilter = PowerShellQuoter.Literal(filter);
            sb.Append("$__npFilter = ").Append(qFilter).Append("; ");
            sb.Append("Get-CimInstance -ClassName ").Append(qClass)
              .Append(" -Namespace ").Append(qNs)
              .Append(" -Filter $__npFilter | Invoke-CimMethod -MethodName ").Append(qMethod);
        }
        else
        {
            // Static method on the class.
            sb.Append("Invoke-CimMethod -ClassName ").Append(qClass)
              .Append(" -Namespace ").Append(qNs)
              .Append(" -MethodName ").Append(qMethod);
        }

        if (argsLiteral is not null)
            sb.Append(" -Arguments ").Append(argsLiteral);

        return sb.ToString();
    }

    /// <summary>
    /// Renders <c>config.arguments</c> as a PowerShell hashtable literal (e.g.
    /// <c>@{ Name = 'svc'; Count = 5; Force = $true }</c>) or returns <c>null</c> when there
    /// are no arguments. JSON object only — arrays and scalars at the top level are rejected.
    /// Each value is type-mapped: string → single-quoted literal; number → numeric literal;
    /// boolean → <c>$true</c>/<c>$false</c>; null → <c>$null</c>; nested object/array → JSON
    /// re-serialised then single-quoted (so it lands as a string the WMI provider may parse).
    /// </summary>
    private static string? BuildArgumentsHashtable(JsonElement config)
    {
        if (!config.TryGetProperty("arguments", out var argsEl)) return null;
        if (argsEl.ValueKind == JsonValueKind.Null || argsEl.ValueKind == JsonValueKind.Undefined) return null;
        if (argsEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("WMI Query: 'arguments' must be a JSON object (key/value pairs)");

        var entries = new List<string>();
        foreach (var prop in argsEl.EnumerateObject())
        {
            if (!IdentifierPattern.IsMatch(prop.Name))
                throw new InvalidOperationException(
                    $"WMI Query: argument name '{prop.Name}' is not a valid identifier (letters, digits, underscore; must not start with a digit)");

            entries.Add($"{prop.Name} = {RenderJsonValueAsPowerShell(prop.Value)}");
        }

        return entries.Count == 0 ? null : "@{ " + string.Join("; ", entries) + " }";
    }

    private static string RenderJsonValueAsPowerShell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => PowerShellQuoter.Literal(value.GetString()),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "$true",
        JsonValueKind.False => "$false",
        JsonValueKind.Null => "$null",
        // Nested object/array: serialise back to JSON and pass as a string. The WMI provider
        // typically wants scalars; emitting a JSON blob is the least-surprising fallback.
        _ => PowerShellQuoter.Literal(value.GetRawText()),
    };
}
