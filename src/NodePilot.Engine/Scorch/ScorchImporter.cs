using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NodePilot.Engine.Scorch;

/// <summary>
/// Parses a System Center Orchestrator <c>.ois_export</c> XML payload into NodePilot
/// workflow definitions + global variables.
///
/// <para>Real-format caveats (observed in exports from SCOrch 2012/2016/2019):</para>
/// <list type="bullet">
/// <item><c>&lt;ExportData&gt;/&lt;Policies&gt;/&lt;Folder&gt;/&lt;Policy&gt;</c> tree — Policies
///   are Runbooks and may nest in sub-folders.</item>
/// <item>Links are <c>&lt;Object&gt;</c> elements with <c>&lt;ObjectTypeName&gt;Link&lt;/ObjectTypeName&gt;</c>,
///   NOT separate <c>&lt;Link&gt;</c> elements. They carry <c>&lt;SourceObject&gt;</c> +
///   <c>&lt;TargetObject&gt;</c> and an optional <c>&lt;TRIGGERS&gt;</c> block with condition logic.</item>
/// <item>Activity properties are direct children of <c>&lt;Object&gt;</c> (e.g.
///   <c>&lt;ScriptBody&gt;</c>, <c>&lt;Subject&gt;</c>), not a <c>&lt;Data&gt;/&lt;Property&gt;</c> nest.</item>
/// <item>Classification via <c>&lt;ObjectTypeName&gt;</c> (human-readable) is more reliable than
///   <c>&lt;ObjectType&gt;</c> GUID lookup — SCOrch emits consistent ObjectTypeName strings across versions.</item>
/// <item>Published-Data references use the pattern <c>`d.T.~Vb/{GUID}`d.T.~Vb/</c> (variable)
///   or <c>`d.T.~Ed/{GUID}.field`d.T.~Ed/</c> (step output). The <c>`</c> are literal backticks,
///   <c>Vb</c>/<c>Ed</c>/<c>Ec</c>/<c>De</c> are type-prefixes for Variable/ExecutionData/Encrypted/DataEncrypted.</item>
/// <item>Global Variables live under <c>&lt;GlobalSettings&gt;/&lt;Variables&gt;</c> as Objects with
///   <c>ObjectTypeName="Variable"</c>.</item>
/// </list>
///
/// Best-effort translation with warnings — unknown activities become <c>log</c> placeholders,
/// untranslatable link-triggers become unconditional edges with a review warning.
/// </summary>
public sealed class ScorchImporter
{
    // SCOrch's LinkObject GUID — distinguishes activity-objects from link-objects.
    private static readonly Guid LinkObjectTypeGuid = Guid.Parse("7A65BD17-9532-4D07-A6DA-E0F89FA0203E");

    // Every <Object> ships 30+ metadata fields (UniqueID, CreationTime, …). We strip them
    // from the property-bag so the activity-mapper sees only activity-specific config.
    private static readonly HashSet<string> StandardMetadataFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "UniqueID", "ParentID", "Name", "Description",
        "PositionX", "PositionY", "ObjectType", "ObjectTypeName", "SubType",
        "Enabled", "Flags",
        "ASC_UseServiceSecurity", "ASC_ThisAccount", "ASC_Username", "ASC_Password",
        "HasExtenders", "CreationTime", "CreatedBy", "LastModified", "LastModifiedBy",
        "Deleted", "Cost", "Savings", "Number", "AlternateDisplayData",
        "ASW_ObjectTimeout", "ASW_NotifyOnFail",
        "Flatten", "FlatUseLineBreak", "FlatUseCSV", "FlatUseCustomSep", "FlatCustomSep",
    };

    // M-14: XmlReaderSettings shared by both Parse overloads. Hardens against:
    //   - external-entity / DTD attacks (DtdProcessing=Prohibit + null XmlResolver)
    //   - billion-laughs / XML-bomb entity expansion (MaxCharactersFromEntities=0 disables
    //     entity text entirely — SCOrch exports don't use entities)
    //   - "how big could this possibly get" DoS via an attacker-supplied 10 GiB payload:
    //     MaxCharactersInDocument caps total character count to 50 MiB × 2 (chars are
    //     16-bit), matching the 50 MiB RequestSizeLimit on the controller.
    private const int MaxCharactersInScorchXml = 50 * 1024 * 1024;

    // Settings are immutable after first use by XmlReader, and the reader settings expose no
    // shared state — caching as a static readonly field is safe and saves the per-parse alloc.
    private static readonly XmlReaderSettings HardenedReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = MaxCharactersInScorchXml,
        CloseInput = false,
    };

    public ScorchImportResult Parse(string xml)
    {
        var result = new ScorchImportResult();

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), HardenedReaderSettings);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to parse XML: {ex.Message}");
            return result;
        }

        return ParseFromDocument(doc, result);
    }

    /// <summary>
    /// M-14: Stream-based overload. Preferred over <see cref="Parse(string)"/> for large
    /// uploads — reading the whole body into a <c>string</c> first, then into an
    /// <see cref="XDocument"/>, doubles peak memory (UTF-8 bytes + UTF-16 string + object
    /// tree). Streaming straight into the XmlReader lets the XML parser hold only one copy.
    /// </summary>
    public ScorchImportResult Parse(Stream xmlStream)
    {
        var result = new ScorchImportResult();

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(xmlStream, HardenedReaderSettings);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to parse XML: {ex.Message}");
            return result;
        }

        return ParseFromDocument(doc, result);
    }

    private ScorchImportResult ParseFromDocument(XDocument doc, ScorchImportResult result)
    {
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "ExportData")
        {
            result.Errors.Add("Root element is not <ExportData>. This does not look like a SCOrch .ois_export file.");
            return result;
        }

        // Extract global variables first — step scripts reference them by GUID, and we need
        // the GUID→Name map to rewrite those references to NodePilot's {{globals.Name}}.
        var variableMap = ExtractGlobalVariables(root, result);

        // Policies can live anywhere under <ExportData> — <Policies> is the canonical root,
        // but older exports sometimes use <PolicyFolders>. Descendants() flattens either.
        var policies = root.Descendants("Policy").ToList();
        if (policies.Count == 0)
        {
            result.Errors.Add("No <Policy> (Runbook) elements found in the export.");
            return result;
        }

        foreach (var policy in policies)
        {
            try
            {
                var runbook = BuildRunbook(policy, variableMap, result.Warnings);
                if (runbook is not null) result.Workflows.Add(runbook);
            }
            catch (Exception ex)
            {
                var name = policy.Element("Name")?.Value ?? "(unnamed)";
                result.Errors.Add($"Runbook '{name}': {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Walks <c>&lt;GlobalSettings&gt;/&lt;Variables&gt;</c> and returns each SCOrch Variable
    /// object as a (GUID, info) pair. Variables with <c>Name</c> incompatible with NodePilot's
    /// <c>[A-Za-z0-9_\-]{1,100}</c> grammar are sanitized (non-alphanumeric → underscore) and
    /// a warning is raised so the operator sees the rename.
    /// </summary>
    private static Dictionary<Guid, ScorchVariable> ExtractGlobalVariables(
        XElement root, ScorchImportResult result)
    {
        var map = new Dictionary<Guid, ScorchVariable>();
        var variables = root.Element("GlobalSettings")?.Element("Variables");
        if (variables is null) return map;

        // Find every <Object> with ObjectTypeName="Variable" anywhere under <Variables>.
        foreach (var obj in variables.Descendants("Object")
                     .Where(o => o.Element("ObjectTypeName")?.Value == "Variable"))
        {
            if (!Guid.TryParse(obj.Element("UniqueID")?.Value, out var guid)) continue;
            var rawName = obj.Element("Name")?.Value?.Trim();
            if (string.IsNullOrEmpty(rawName)) continue;
            var name = SanitizeVariableName(rawName);
            if (name != rawName)
                result.Warnings.Add($"Variable '{rawName}' renamed to '{name}' (NodePilot grammar).");

            var value = obj.Element("Value")?.Value ?? "";
            var description = obj.Element("Description")?.Value;
            if (string.IsNullOrEmpty(description)) description = null;

            // SCOrch marks encrypted values with the `d.T.~Ec/ prefix (Ec = Encrypted).
            // We can't decrypt them — flag as secret with a placeholder so the operator
            // knows to supply the actual value after import.
            const string EncryptedMarker = "`d.T.~Ec/";
            bool isSecret = value.StartsWith(EncryptedMarker, StringComparison.Ordinal);
            if (isSecret)
            {
                result.Warnings.Add(
                    $"Variable '{name}' is encrypted in the SCOrch export and cannot be decrypted. " +
                    $"Set the actual value manually in Global Variables after import.");
                value = "[ENCRYPTED - set actual value after import]";
            }

            var variable = new ScorchVariable(guid, name, description, value, IsSecret: isSecret);
            map[guid] = variable;
            result.Variables.Add(variable);
        }
        return map;
    }

    private static string SanitizeVariableName(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append((char.IsLetterOrDigit(c) || c is '_' or '-') ? c : '_');
        var sanitized = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "imported_variable" : sanitized[..Math.Min(100, sanitized.Length)];
    }

    private ScorchRunbook? BuildRunbook(
        XElement policy, Dictionary<Guid, ScorchVariable> variableMap, List<string> warnings)
    {
        var name = policy.Element("Name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            warnings.Add("Skipped a Policy without a <Name>.");
            return null;
        }
        var description = policy.Element("Description")?.Value?.Trim();
        if (string.IsNullOrEmpty(description)) description = null;

        // Partition the Policy's <Object> children into activities vs. links. Links have
        // ObjectType equal to the well-known Link-GUID; fallback to ObjectTypeName.
        var allObjects = policy.Elements("Object").ToList();
        var activityObjects = new List<XElement>();
        var linkObjects = new List<XElement>();
        foreach (var obj in allObjects)
        {
            var typeName = obj.Element("ObjectTypeName")?.Value;
            var objectTypeGuid = ParseGuidValue(obj.Element("ObjectType")?.Value);
            if (typeName == "Link" || objectTypeGuid == LinkObjectTypeGuid)
                linkObjects.Add(obj);
            else
                activityObjects.Add(obj);
        }

        // Map activity objects.
        var mapped = new List<(XElement Source, Guid Id, ScorchActivityMapper.Mapping Mapping)>();
        int fallbackCount = 0, heuristicCount = 0;
        foreach (var obj in activityObjects)
        {
            if (!Guid.TryParse(obj.Element("UniqueID")?.Value, out var objId))
            {
                warnings.Add($"'{name}': skipped an <Object> without a parseable UniqueID.");
                continue;
            }
            var props = ExtractProperties(obj);
            var mapping = ScorchActivityMapper.Map(obj, props);
            if (mapping.Fallback) fallbackCount++;
            else if (mapping.UsedHeuristic) heuristicCount++;
            if (mapping.Note is not null)
                warnings.Add($"'{name}' / '{obj.Element("Name")?.Value}': {mapping.Note}");
            mapped.Add((obj, objId, mapping));
        }

        // Rewrite Published-Data + Variable references in every config value.
        var activityGuids = new HashSet<Guid>(mapped.Select(m => m.Id));
        foreach (var (_, _, mapping) in mapped)
            RewriteReferences(mapping.Config, variableMap, activityGuids);

        // Build React Flow nodes.
        var nodes = new List<object>();
        foreach (var (obj, objId, mapping) in mapped)
        {
            var label = obj.Element("Name")?.Value ?? "(unnamed)";
            double x = ParseDouble(obj.Element("PositionX")?.Value);
            double y = ParseDouble(obj.Element("PositionY")?.Value);
            var disabled = obj.Element("Enabled")?.Value?.Equals("FALSE", StringComparison.OrdinalIgnoreCase) == true;

            nodes.Add(new
            {
                id = objId.ToString(),
                type = "activity",
                position = new { x, y },
                data = new
                {
                    label,
                    activityType = mapping.ActivityType,
                    config = mapping.Config,
                    outputVariable = mapping.OutputVariable,
                    disabled,
                },
            });
        }

        // Build edges from link-objects. SCOrch links without an explicit TRIGGERS block are
        // unconditional; links with TRIGGERS become conditionExpression edges.
        var edges = new List<object>();
        int linkIdx = 0;
        foreach (var linkObj in linkObjects)
        {
            if (!Guid.TryParse(linkObj.Element("SourceObject")?.Value, out var src)) continue;
            if (!Guid.TryParse(linkObj.Element("TargetObject")?.Value, out var dst)) continue;
            if (!activityGuids.Contains(src) || !activityGuids.Contains(dst)) continue;

            var disabled = linkObj.Element("Enabled")?.Value?.Equals("FALSE", StringComparison.OrdinalIgnoreCase) == true;
            var label = linkObj.Element("Name")?.Value;
            if (string.IsNullOrWhiteSpace(label) || label == "Link") label = null;

            var (conditionExpression, labelHint) = BuildLinkCondition(linkObj, warnings, name);
            label ??= labelHint;

            edges.Add(new
            {
                id = $"e{linkIdx++}-{src}-{dst}",
                source = src.ToString(),
                target = dst.ToString(),
                type = "labeled",
                data = new
                {
                    label,
                    disabled,
                    conditionExpression,
                },
            });
        }

        warnings.Add($"'{name}': {mapped.Count} activities, {edges.Count} links. " +
                     $"Heuristic mappings: {heuristicCount}, Placeholder fallbacks: {fallbackCount}.");

        var definitionJson = JsonSerializer.Serialize(new { nodes, edges },
            new JsonSerializerOptions { WriteIndented = false });

        return new ScorchRunbook(name, description, definitionJson,
            ActivityCount: mapped.Count,
            HeuristicCount: heuristicCount,
            FallbackCount: fallbackCount);
    }

    /// <summary>
    /// Flattens an Object's direct-child elements into a property bag, skipping SCOrch's
    /// standard metadata fields and null-typed values. Keeps the raw inner text — the
    /// activity mapper is responsible for further parsing (e.g. boolean coercion).
    /// </summary>
    private static Dictionary<string, string> ExtractProperties(XElement obj)
    {
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in obj.Elements())
        {
            var key = child.Name.LocalName;
            if (StandardMetadataFields.Contains(key)) continue;
            if (child.Attribute("datatype")?.Value == "null") continue;
            var val = child.Value;
            if (string.IsNullOrEmpty(val)) continue;
            if (!bag.ContainsKey(key)) bag[key] = val;
        }
        return bag;
    }

    // -------- reference rewriting ---------------------------------------------------------

    /// <summary>
    /// SCOrch Published-Data reference patterns. All share the prefix <c>`d.T.~</c> and end
    /// with a type-code + <c>/</c> before the closing mirror-marker. We accept both the
    /// symmetric form (<c>`d.T.~Vb/{GUID}`d.T.~Vb/</c>) and the curly-brace-only variants
    /// seen in older exports.
    /// </summary>
    private static readonly Regex VariableRefRx =
        new(@"`d\.T\.~Vb/\{([0-9a-fA-F\-]+)\}`d\.T\.~Vb/", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex ExecutionDataRefRx =
        new(@"`d\.T\.~Ed/\{([0-9a-fA-F\-]+)\}\.([A-Za-z0-9_\-]+)`d\.T\.~Ed/", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static void RewriteReferences(
        Dictionary<string, object?> cfg,
        IReadOnlyDictionary<Guid, ScorchVariable> variableMap,
        IReadOnlySet<Guid> activityGuids)
    {
        var keys = cfg.Keys.ToList();
        foreach (var k in keys)
            if (cfg[k] is string s) cfg[k] = RewriteString(s, variableMap, activityGuids);
    }

    private static string RewriteString(
        string input,
        IReadOnlyDictionary<Guid, ScorchVariable> variableMap,
        IReadOnlySet<Guid> activityGuids)
    {
        if (string.IsNullOrEmpty(input)) return input;

        // 1. Variable refs → {{globals.Name}}
        var rewritten = VariableRefRx.Replace(input, m =>
        {
            return Guid.TryParse(m.Groups[1].Value, out var g) && variableMap.TryGetValue(g, out var v)
                ? "{{globals." + v.Name + "}}"
                : m.Value;
        });

        // 2. Execution-data refs → {{stepId.param.field}} or {{stepId.output}}
        rewritten = ExecutionDataRefRx.Replace(rewritten, m =>
        {
            if (!Guid.TryParse(m.Groups[1].Value, out var g) || !activityGuids.Contains(g))
                return m.Value;
            var field = m.Groups[2].Value;
            var suffix = field.Equals("stdout", StringComparison.OrdinalIgnoreCase) ? "output"
                       : field.Equals("stderr", StringComparison.OrdinalIgnoreCase) ? "error"
                       : $"param.{field}";
            return "{{" + g + "." + suffix + "}}";
        });

        return rewritten;
    }

    // -------- link condition translation --------------------------------------------------

    /// <summary>
    /// Translates a SCOrch Link's <c>&lt;TRIGGERS&gt;</c> block into a NodePilot
    /// <c>conditionExpression</c> JSON-object-as-dictionary. Multiple entries in the same
    /// GroupID are AND-joined; different GroupIDs are OR-joined. No TRIGGERS block → null
    /// (unconditional edge). We emit the structured expression directly — the edge's
    /// <c>condition</c> shortcut string is only used for pure success/failure semantics
    /// which SCOrch's TRIGGERS model doesn't align with cleanly.
    /// </summary>
    private static (object? Expression, string? Label) BuildLinkCondition(
        XElement linkObj, List<string> warnings, string runbookName)
    {
        var triggers = linkObj.Element("TRIGGERS")?.Elements("Entry").ToList();
        if (triggers is null || triggers.Count == 0) return (null, null);

        // Group by GroupID → AND within, OR across.
        var groups = triggers
            .GroupBy(t => int.TryParse(t.Element("GroupID")?.Value, out var g) ? g : 0)
            .ToList();

        var groupExprs = new List<object?>();
        foreach (var group in groups)
        {
            var groupMembers = new List<object?>();
            foreach (var entry in group)
            {
                var expr = BuildComparisonFromTrigger(entry, warnings, runbookName);
                if (expr is not null) groupMembers.Add(expr);
            }
            if (groupMembers.Count == 0) continue;
            if (groupMembers.Count == 1) groupExprs.Add(groupMembers[0]);
            else groupExprs.Add(new
            {
                type = "group",
                op = "AND",
                children = groupMembers,
            });
        }

        object? expression = groupExprs.Count switch
        {
            0 => null,
            1 => groupExprs[0],
            _ => new { type = "group", op = "OR", children = groupExprs },
        };

        var labelHint = triggers.Count switch
        {
            1 => "if condition",
            _ => "if conditions",
        };
        return (expression, labelHint);
    }

    private static object? BuildComparisonFromTrigger(XElement entry, List<string> warnings, string runbookName)
    {
        var cond = entry.Element("Condition")?.Value ?? "equals";
        var dataStr = entry.Element("Data")?.Value ?? "";
        var valueStr = entry.Element("Value")?.Value ?? "";

        // Data format: "{GUID}.fieldname"
        var match = Regex.Match(dataStr, @"\{([0-9a-fA-F\-]+)\}\.([A-Za-z0-9_\-]+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success)
        {
            warnings.Add($"'{runbookName}': could not parse trigger Data '{dataStr}' — skipping this filter.");
            return null;
        }
        var srcGuid = match.Groups[1].Value;
        var field = match.Groups[2].Value;

        var op = MapScorchCondition(cond);
        if (op is null)
        {
            warnings.Add($"'{runbookName}': unsupported trigger condition '{cond}' — skipping this filter.");
            return null;
        }

        // Map SCOrch field to NodePilot operand: stdout → output, stderr → error, else param.
        var (npField, paramName) = field.ToLowerInvariant() switch
        {
            "stdout" => ("output", (string?)null),
            "stderr" => ("error", (string?)null),
            _ => ("param", field),
        };

        object leftOperand = paramName is null
            ? new { kind = "variable", stepId = srcGuid, field = npField }
            : new { kind = "variable", stepId = srcGuid, field = npField, paramName };

        return new
        {
            type = "comparison",
            op,
            left = leftOperand,
            right = new { kind = "literal", value = valueStr },
        };
    }

    private static string? MapScorchCondition(string cond) => cond.ToLowerInvariant() switch
    {
        "equals" => "==",
        "does not equal" => "!=",
        "is less than" => "<",
        "is less than or equals to" or "is less than or equal to" => "<=",
        "is greater than" => ">",
        "is greater than or equals to" or "is greater than or equal to" => ">=",
        "contains" => "contains",
        "does not contain" => "contains", // note: NodePilot has no "does not contain" op directly; we approximate
        "matches pattern" => "matches",
        "begins with" or "starts with" => "startsWith",
        "ends with" => "endsWith",
        _ => null,
    };

    // -------- helpers ---------------------------------------------------------------------

    private static double ParseDouble(string? s)
        => double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static Guid? ParseGuidValue(string? s)
        => s is not null && Guid.TryParse(s, out var g) ? g : null;
}

public sealed class ScorchImportResult
{
    public List<ScorchRunbook> Workflows { get; } = new();
    public List<ScorchVariable> Variables { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
}

public sealed record ScorchRunbook(
    string Name,
    string? Description,
    string DefinitionJson,
    int ActivityCount,
    int HeuristicCount,
    int FallbackCount);

public sealed record ScorchVariable(
    Guid SourceGuid,
    string Name,
    string? Description,
    string Value,
    bool IsSecret);
