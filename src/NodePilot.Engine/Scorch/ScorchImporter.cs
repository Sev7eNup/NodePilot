using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NodePilot.Core.Activities;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Engine.Execution;

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
        => ParseFromReader(() => XmlReader.Create(new StringReader(xml), HardenedReaderSettings));

    /// <summary>
    /// Stream-based overload, and the one the API uses. Two things it buys over
    /// <see cref="Parse(string)"/>: the reader sees the raw bytes, so the document's BOM and
    /// <c>&lt;?xml encoding=...?&gt;</c> declaration decide the encoding instead of a caller-chosen
    /// default; and no UTF-16 copy of the whole document is materialised alongside the tree.
    ///
    /// <para>Reads SYNCHRONOUSLY. Callers on an ASP.NET Core request path must pass a buffered
    /// stream, not <c>Request.Body</c> — Kestrel's <c>AllowSynchronousIO</c> is false by default
    /// and would throw. A MemoryStream-backed unit test does not reproduce that.</para>
    /// </summary>
    public ScorchImportResult Parse(Stream xmlStream)
        => ParseFromReader(() => XmlReader.Create(xmlStream, HardenedReaderSettings));

    // The factory is invoked INSIDE the try: XmlReader.Create itself can throw on a bad
    // source, and that has always been reported as a parse error rather than propagated.
    private ScorchImportResult ParseFromReader(Func<XmlReader> createReader)
    {
        var result = new ScorchImportResult();

        XDocument doc;
        try
        {
            using var reader = createReader();
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

            // SCOrch marks encrypted values with an Ec (Encrypted) or De (DataEncrypted) marker.
            // We can't decrypt them — flag as secret with a placeholder so the operator knows to
            // supply the actual value after import.
            //
            // Anchoring this on the type code rather than on a literal leading backtick matters:
            // real exports write the marker backslash-prefixed, so a StartsWith("`d.T.~Ec/") check
            // classified every encrypted variable in a real file as plaintext and imported the
            // ciphertext as its value.
            bool isSecret = EncryptedMarkerRx.IsMatch(value);
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
            var mapping = CarryActivityMetadata(obj, ScorchActivityMapper.Map(obj, props), name, warnings);
            if (mapping.Fallback) fallbackCount++;
            else if (mapping.UsedHeuristic) heuristicCount++;
            if (mapping.Note is not null)
                warnings.Add($"'{name}' / '{obj.Element("Name")?.Value}': {mapping.Note}");
            mapped.Add((obj, objId, mapping));
        }

        // Rewrite Published-Data + Variable references in every config value.
        var activityGuids = new HashSet<Guid>(mapped.Select(m => m.Id));
        var outputNames = AssignOutputVariables(mapped);
        var rewriteCtx = new RewriteContext(variableMap, activityGuids, outputNames, warnings, name);
        foreach (var (obj, _, mapping) in mapped)
            RewriteReferences(mapping.Config, rewriteCtx, obj.Element("Name")?.Value ?? "(unnamed)");

        ReportUnavailableParameters(mapped, outputNames, name, warnings);

        // Build React Flow nodes.
        var nodes = new List<object>();
        var nodeIds = new List<string>();
        var hasActiveTrigger = false;
        foreach (var (obj, objId, mapping) in mapped)
        {
            var label = obj.Element("Name")?.Value ?? "(unnamed)";
            double x = ParseDouble(obj.Element("PositionX")?.Value);
            double y = ParseDouble(obj.Element("PositionY")?.Value);
            var disabled = obj.Element("Enabled")?.Value?.Equals("FALSE", StringComparison.OrdinalIgnoreCase) == true
                           || mapping.Disabled;

            // The hybrids run on the NodePilot host when no target is set, which is silent and
            // surprising if the runbook meant a named server. The strictly-remote types are covered
            // by the analyzer's own missing-target-machine finding (it exempts exactly these two),
            // so reporting them here as well would only duplicate it.
            if (mapping.TargetMachine is null && mapping.ActivityType is "runScript" or "waitForCondition")
            {
                warnings.Add($"'{name}' / '{label}': no target machine in the export — this step will " +
                             "run on the NodePilot host, not on a remote server.");
            }

            if (!disabled && ActivityCatalog.TriggerTypes.Contains(mapping.ActivityType))
                hasActiveTrigger = true;

            nodeIds.Add(objId.ToString());
            nodes.Add(new
            {
                id = objId.ToString(),
                type = "activity",
                position = new { x, y },
                data = new
                {
                    label,
                    description = Trimmed(obj.Element("Description")?.Value),
                    activityType = mapping.ActivityType,
                    config = mapping.Config,
                    outputVariable = outputNames.GetValueOrDefault(objId),
                    targetMachineId = mapping.TargetMachine,
                    disabled,
                },
            });
        }

        // Build edges from link-objects. SCOrch links without an explicit TRIGGERS block are
        // unconditional; links with TRIGGERS become conditionExpression edges.
        var edges = new List<object>();
        var edgeTargets = new HashSet<string>(StringComparer.Ordinal);
        int linkIdx = 0;
        foreach (var linkObj in linkObjects)
        {
            if (!Guid.TryParse(linkObj.Element("SourceObject")?.Value, out var src)) continue;
            if (!Guid.TryParse(linkObj.Element("TargetObject")?.Value, out var dst)) continue;
            if (!activityGuids.Contains(src) || !activityGuids.Contains(dst))
            {
                // The link points at an object we did not import (or could not identify). Dropping
                // it silently made the summary's link count a claim the definition did not honour.
                warnings.Add($"'{name}': dropped a link between {src} and {dst} — one end is not an " +
                             "activity in this runbook.");
                continue;
            }

            var disabled = linkObj.Element("Enabled")?.Value?.Equals("FALSE", StringComparison.OrdinalIgnoreCase) == true;
            var label = linkObj.Element("Name")?.Value;
            if (string.IsNullOrWhiteSpace(label) || label == "Link") label = null;

            var (conditionExpression, labelHint) = BuildLinkCondition(linkObj, warnings, name);
            label ??= labelHint;

            edgeTargets.Add(dst.ToString());
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

        AddSyntheticTriggerIfMissing(name, hasActiveTrigger, nodes, nodeIds, edges, edgeTargets, ref linkIdx, warnings);

        warnings.Add($"'{name}': {mapped.Count} activities, {edges.Count} links. " +
                     $"Heuristic mappings: {heuristicCount}, Placeholder fallbacks: {fallbackCount}.");

        var definitionJson = JsonSerializer.Serialize(new { nodes, edges },
            new JsonSerializerOptions { WriteIndented = false });

        ReportGraphFindings(definitionJson, mapped, outputNames, name, warnings);

        return new ScorchRunbook(name, description, definitionJson,
            ActivityCount: mapped.Count,
            HeuristicCount: heuristicCount,
            FallbackCount: fallbackCount);
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Rescues three per-activity settings that <see cref="ExtractProperties"/> drops as "standard
    /// metadata". They are metadata to the parser but not to the operator: the timeout changes
    /// behaviour, and the run-as account changes who the step runs as.
    /// </summary>
    private static ScorchActivityMapper.Mapping CarryActivityMetadata(
        XElement obj, ScorchActivityMapper.Mapping mapping, string runbookName, List<string> warnings)
    {
        var label = obj.Element("Name")?.Value ?? "(unnamed)";

        // ASW_ObjectTimeout is SCOrch's per-activity timeout. Only set it where the target activity
        // documents the key — an undocumented key is one its executor never reads.
        if (int.TryParse(obj.Element("ASW_ObjectTimeout")?.Value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
            && ActivityConfigReference.TryGet(mapping.ActivityType) is { } entry
            && entry.ConfigKeys.Any(k => k.Key == "timeoutSeconds"))
        {
            mapping.Config["timeoutSeconds"] = seconds;
        }

        // The run-as password is DPAPI-encrypted in the export and cannot be recovered, so no
        // credential is invented — the account is named so the operator can attach the right one.
        var runAs = Trimmed(obj.Element("ASC_Username")?.Value);
        if (runAs is not null)
        {
            warnings.Add($"'{runbookName}' / '{label}': ran as '{runAs}' in SCOrch. The password is " +
                         "encrypted in the export, so no credential was created — attach one to the node " +
                         "if the step needs that identity.");
        }

        return mapping;
    }

    /// <summary>
    /// Runs the workflow analyzer over the produced definition and folds its findings into the
    /// import report, then adds the one check it cannot make.
    ///
    /// <para>Reusing the analyzer means "no trigger", "unreachable node", "cycle", "unknown activity
    /// type" and "missing target machine" are reported by the same code the designer and the MCP
    /// tools use, so the import report cannot drift from what the canvas says about the same
    /// workflow.</para>
    /// </summary>
    private static void ReportGraphFindings(
        string definitionJson,
        List<(XElement Source, Guid Id, ScorchActivityMapper.Mapping Mapping)> mapped,
        IReadOnlyDictionary<Guid, string> outputNames,
        string runbookName,
        List<string> warnings)
    {
        JsonElement definition;
        try
        {
            definition = JsonSerializer.Deserialize<JsonElement>(definitionJson);
        }
        catch (JsonException)
        {
            return; // The controller rejects an unparseable definition on its own.
        }

        foreach (var finding in WorkflowAnalyzer.Analyze(definition).Findings)
            warnings.Add($"'{runbookName}': [{finding.Code}] {finding.Message}");

        ReportCrossBranchReferences(definition, mapped, runbookName, warnings);
    }

    /// <summary>
    /// The one check the analyzer cannot make, because the referenced step genuinely exists.
    ///
    /// <para>SCOrch's data bus is run-scoped: any activity can read the published data of any
    /// activity that already ran, including one on a parallel branch. NodePilot's is ancestor-scoped
    /// — a reference resolves only if the target is on this step's own predecessor path. Such a
    /// reference is idiomatic in a runbook and never resolves after import, so it is worth naming at
    /// import time rather than at three in the morning.</para>
    /// </summary>
    private static void ReportCrossBranchReferences(
        JsonElement definition,
        List<(XElement Source, Guid Id, ScorchActivityMapper.Mapping Mapping)> mapped,
        string runbookName,
        List<string> warnings)
    {
        var doc = WorkflowDefinitionDocument.FromJsonElement(definition);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (obj, id, mapping) in mapped)
        {
            var nodeId = id.ToString();
            if (!doc.NodesById.ContainsKey(nodeId)) continue;

            var label = obj.Element("Name")?.Value ?? "(unnamed)";
            HashSet<string>? ancestors = null;

            foreach (var value in EnumerateConfigStrings(mapping.Config))
            {
                foreach (Match m in VariableResolver.StepPattern.Matches(value))
                {
                    var head = m.Groups[1].Value;
                    var targetId = doc.OutputVariableToStepId.TryGetValue(head, out var byName) ? byName
                                 : doc.NodesById.ContainsKey(head) ? head
                                 : null;
                    if (targetId is null || targetId == nodeId) continue;

                    ancestors ??= doc.FindAncestorNodeIds(nodeId);
                    if (ancestors.Contains(targetId)) continue;

                    if (!reported.Add($"{nodeId}|{head}")) continue;
                    warnings.Add(
                        $"'{runbookName}' / '{label}': references " + "{{" + head + ".…}}" +
                        ", which is not on this step's predecessor path. SCOrch let any activity read " +
                        "any earlier activity's published data; NodePilot resolves ancestors only, so " +
                        "this reference will never resolve. Re-route the link or move the value.");
                }
            }
        }
    }

    /// <summary>
    /// Checks every rewritten <c>{{step.param.X}}</c> against what the referenced activity actually
    /// publishes.
    ///
    /// <para>Translating the marker syntax is not the same as translating the DATA. SCOrch's Monitor
    /// File publishes <c>Path</c>, <c>FileName</c> and <c>FileNameExt</c>; NodePilot's
    /// fileWatcherTrigger publishes <c>filePath</c>, <c>fileName</c> and <c>fileAction</c>. A
    /// rewritten reference therefore looks perfectly well-formed and still resolves to nothing —
    /// and inside a runScript body an unresolved template is legitimate script text, so the step
    /// runs green with the literal placeholder in it. Renaming the fields would be guesswork
    /// (SCOrch's <c>Path</c> is a folder, <c>filePath</c> is a full file path), so the mismatch is
    /// reported instead, with the list of names that are actually available.</para>
    /// </summary>
    private static void ReportUnavailableParameters(
        List<(XElement Source, Guid Id, ScorchActivityMapper.Mapping Mapping)> mapped,
        IReadOnlyDictionary<Guid, string> outputNames,
        string runbookName,
        List<string> warnings)
    {
        var typeByHead = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, id, mapping) in mapped)
        {
            typeByHead[id.ToString()] = mapping.ActivityType;
            if (outputNames.TryGetValue(id, out var named)) typeByHead[named] = mapping.ActivityType;
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (obj, _, mapping) in mapped)
        {
            var label = obj.Element("Name")?.Value ?? "(unnamed)";
            foreach (var value in EnumerateConfigStrings(mapping.Config))
            {
                foreach (Match m in VariableResolver.StepPattern.Matches(value))
                {
                    // .output/.error/.success exist on every step; only named parameters can be wrong.
                    if (!m.Groups[3].Success) continue;

                    var head = m.Groups[1].Value;
                    var parameter = m.Groups[3].Value;
                    if (!typeByHead.TryGetValue(head, out var targetType)) continue;
                    if (!ActivityCatalog.TryGet(targetType, out var descriptor) || descriptor is null) continue;

                    // Activities whose outputs are dynamic declare none statically — runScript's
                    // script-scope variables, wmiQuery's captureProperties, registryOperation's
                    // per-operation results. Checking those would only produce false alarms.
                    if (descriptor.OutputParameters.Count == 0) continue;
                    if (descriptor.OutputParameters.Any(o => string.Equals(o.Name, parameter, StringComparison.Ordinal)))
                        continue;

                    if (!reported.Add($"{label}|{head}.{parameter}")) continue;
                    warnings.Add(
                        $"'{runbookName}' / '{label}': references " + "{{" + head + ".param." + parameter + "}}" +
                        $", but {targetType} publishes " +
                        string.Join(", ", descriptor.OutputParameters.Select(o => o.Name)) +
                        ". SCOrch published different field names than the NodePilot activity does — " +
                        "point the reference at one of those, or compute the value in a script step.");
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateConfigStrings(object? value)
    {
        switch (value)
        {
            case string s when s.Length > 0:
                yield return s;
                break;
            case IDictionary<string, object?> map:
                foreach (var v in map.Values)
                    foreach (var s in EnumerateConfigStrings(v)) yield return s;
                break;
            case IList<object> list:
                foreach (var v in list)
                    foreach (var s in EnumerateConfigStrings(v)) yield return s;
                break;
        }
    }

    /// <summary>
    /// Gives a runbook an explicit entry point when the translation produced no active trigger.
    ///
    /// <para>NodePilot's roots are exclusively enabled trigger nodes; a definition with none yields
    /// zero roots and the execution fails on the spot. SCOrch has no equivalent rule — an invoked
    /// runbook simply starts at its first activity, and most runbooks in a real estate are invoked
    /// rather than monitored — so a faithful translation of one imports as something that can never
    /// run. A manual trigger wired to every source-less activity is the smallest honest fix.</para>
    /// </summary>
    private static void AddSyntheticTriggerIfMissing(
        string runbookName,
        bool hasActiveTrigger,
        List<object> nodes,
        List<string> nodeIds,
        List<object> edges,
        HashSet<string> edgeTargets,
        ref int linkIdx,
        List<string> warnings)
    {
        if (hasActiveTrigger || nodes.Count == 0) return;

        // Source-less activities are the runbook's real entry points. A runbook whose every node has
        // an incoming link is a pure cycle; attach to the first node so the graph still has a root.
        var entryPoints = nodeIds.Where(id => !edgeTargets.Contains(id)).ToList();
        if (entryPoints.Count == 0) entryPoints.Add(nodeIds[0]);

        var triggerId = DeriveSyntheticTriggerId(runbookName).ToString();
        nodes.Insert(0, new
        {
            id = triggerId,
            type = "activity",
            position = new { x = 0d, y = 0d },
            data = new
            {
                label = "Start (imported)",
                activityType = "manualTrigger",
                config = new Dictionary<string, object?> { ["parameters"] = new List<object>() },
                outputVariable = (string?)null,
                targetMachineId = (string?)null,
                disabled = false,
            },
        });

        foreach (var entry in entryPoints)
        {
            edges.Add(new
            {
                id = $"e{linkIdx++}-{triggerId}-{entry}",
                source = triggerId,
                target = entry,
                type = "labeled",
                data = new
                {
                    label = (string?)null,
                    disabled = false,
                    conditionExpression = (object?)null,
                },
            });
        }

        warnings.Add(
            $"'{runbookName}': the runbook has no trigger of its own (SCOrch runbooks invoked by " +
            $"another runbook do not need one). A manual trigger was added and wired to " +
            $"{entryPoints.Count} entry activit{(entryPoints.Count == 1 ? "y" : "ies")}; without it the " +
            "workflow would have no root and every run would fail.");
    }

    /// <summary>
    /// Derived from the runbook name rather than random so that importing the same export twice
    /// produces byte-identical definitions.
    /// </summary>
    private static Guid DeriveSyntheticTriggerId(string runbookName)
    {
        var hash = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("nodepilot/scorch/synthetic-trigger/" + runbookName));
        return new Guid(hash.AsSpan(0, 16));
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
    /// SCOrch Published-Data reference patterns. All share the prefix <c>`d.T.~</c>, a two-letter
    /// type code (Vb = Variable, Ed = ExecutionData, Ec/De = encrypted) and a mirrored closing
    /// marker.
    ///
    /// <para>The leading <c>\</c> is NOT optional decoration — every marker in a real export is
    /// written as backslash-backtick (bytes <c>5c 60</c>), e.g.
    /// <c>\`d.T.~Ed/{GUID}.FileName\`d.T.~Ed/</c>. Patterns that expected a bare backtick right
    /// after the field name matched none of the 147 references in the reference export, so the raw
    /// markers travelled into the node configs untouched. It stays optional here only because we
    /// have not seen every SCOrch version's writer.</para>
    ///
    /// <para>The field group accepts dots so that names like <c>{GUID}.Policy.Name</c> are
    /// RECOGNISED — they cannot be expressed (NodePilot's <c>param</c> tail has no nested dots),
    /// but matching them lets us report them instead of silently emitting a broken template.</para>
    /// </summary>
    private static readonly Regex VariableRefRx =
        new(@"\\?`d\.T\.~Vb/\{([0-9a-fA-F\-]+)\}\\?`d\.T\.~Vb/", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex ExecutionDataRefRx =
        new(@"\\?`d\.T\.~Ed/\{([0-9a-fA-F\-]+)\}\.([A-Za-z0-9_\-]+(?:\.[A-Za-z0-9_\-]+)*)\\?`d\.T\.~Ed/",
            RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Any <c>`d.T.~XX/</c> still present after rewriting is a reference we could not express:
    /// an encrypted value (<c>Ec</c>/<c>De</c>), a field reference (<c>F</c>, used inside
    /// Monitor File's nested filter XML), a variable GUID that isn't in this export, or a step in
    /// a different runbook. Detecting the leftover generically beats enumerating the type codes —
    /// the operator gets told the value is incomplete either way.
    /// </summary>
    private static readonly Regex ResidualMarkerRx =
        new(@"\\?`d\.T\.~([A-Za-z]{1,2})/", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Encrypted (<c>Ec</c>) and data-encrypted (<c>De</c>) value markers.</summary>
    private static readonly Regex EncryptedMarkerRx =
        new(@"\\?`d\.T\.~(Ec|De)/", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Everything a rewrite needs to resolve a marker and report what it could not.</summary>
    private sealed record RewriteContext(
        IReadOnlyDictionary<Guid, ScorchVariable> Variables,
        IReadOnlySet<Guid> ActivityGuids,
        IReadOnlyDictionary<Guid, string> OutputNames,
        List<string> Warnings,
        string RunbookName);

    /// <summary>
    /// Gives every activity a readable <c>outputVariable</c> derived from its SCOrch name, so
    /// references read <c>{{Check_Package_Contents.param.hasPayload}}</c> rather than
    /// <c>{{8dc7ff8a-1ea1-4037-baeb-65416a060aac.param.hasPayload}}</c>. Both resolve; only one is
    /// legible in a 47-node imported canvas.
    ///
    /// <para>Names are forced into the template grammar (<c>[\w-]+</c>) and de-duplicated, because
    /// two steps sharing an outputVariable make downstream references resolve to whichever ran
    /// last — SCOrch activity names are not unique within a runbook.</para>
    /// </summary>
    private static Dictionary<Guid, string> AssignOutputVariables(
        IEnumerable<(XElement Source, Guid Id, ScorchActivityMapper.Mapping Mapping)> mapped)
    {
        var assigned = new Dictionary<Guid, string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (obj, id, _) in mapped)
        {
            var baseName = SanitizeIdentifier(obj.Element("Name")?.Value ?? "");
            if (baseName.Length == 0) baseName = "step";

            var candidate = baseName;
            for (var n = 2; !taken.Add(candidate); n++) candidate = $"{baseName}_{n}";
            assigned[id] = candidate;
        }
        return assigned;
    }

    private static string SanitizeIdentifier(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        // Collapse runs of underscores so "Query XML - Status?" does not become "Query_XML___Status_".
        var collapsed = Regex.Replace(sb.ToString(), "_{2,}", "_", RegexOptions.None, TimeSpan.FromSeconds(1));
        return collapsed.Trim('_', '-');
    }

    private static void RewriteReferences(
        Dictionary<string, object?> cfg, RewriteContext ctx, string activityName)
    {
        foreach (var k in cfg.Keys.ToList())
            cfg[k] = RewriteValue(cfg[k], ctx, activityName, k);
    }

    /// <summary>
    /// Rewrites nested values too, not just top-level strings. startWorkflow's <c>parameters</c> is a
    /// map and manualTrigger's <c>parameters</c> a list, and those carry references as often as any
    /// scalar does — a string-only pass shipped a sub-runbook call whose every argument was still
    /// raw SCOrch marker text.
    /// </summary>
    private static object? RewriteValue(object? value, RewriteContext ctx, string activityName, string configKey)
    {
        switch (value)
        {
            case string s:
                return RewriteString(s, ctx, activityName, configKey);
            case IDictionary<string, object?> nested:
                foreach (var k in nested.Keys.ToList())
                    nested[k] = RewriteValue(nested[k], ctx, activityName, $"{configKey}.{k}");
                return nested;
            case IList<object> list:
                for (var i = 0; i < list.Count; i++)
                    list[i] = RewriteValue(list[i], ctx, activityName, configKey)!;
                return list;
            default:
                return value;
        }
    }

    private static string RewriteString(
        string input, RewriteContext ctx, string activityName, string configKey)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var dottedFields = new List<string>();

        // 1. Variable refs → {{globals.Name}}
        var rewritten = VariableRefRx.Replace(input, m =>
            Guid.TryParse(m.Groups[1].Value, out var g) && ctx.Variables.TryGetValue(g, out var v)
                ? "{{globals." + v.Name + "}}"
                : m.Value);

        // 2. Execution-data refs → {{stepId.param.field}} or {{stepId.output}}
        rewritten = ExecutionDataRefRx.Replace(rewritten, m =>
        {
            if (!Guid.TryParse(m.Groups[1].Value, out var g) || !ctx.ActivityGuids.Contains(g))
                return m.Value;

            var field = m.Groups[2].Value;
            if (field.Contains('.'))
            {
                // SCOrch runbook metadata (Policy.Name, Policy.PID) and other nested names.
                // NodePilot's {{step.param.X}} tail has no nested dots, so substituting here would
                // emit a template that can never resolve. Leave the marker visible and report it.
                dottedFields.Add(field);
                return m.Value;
            }

            var suffix = field.Equals("stdout", StringComparison.OrdinalIgnoreCase) ? "output"
                       : field.Equals("stderr", StringComparison.OrdinalIgnoreCase) ? "error"
                       : $"param.{field}";
            var head = ctx.OutputNames.TryGetValue(g, out var named) ? named : g.ToString();
            return "{{" + head + "." + suffix + "}}";
        });

        ReportResidualMarkers(rewritten, dottedFields, ctx, activityName, configKey);
        return rewritten;
    }

    /// <summary>
    /// A config value that still carries a SCOrch marker after rewriting is incomplete, and the
    /// step will run with literal marker text in it. That used to be invisible; now it is named
    /// together with the config key so the operator knows exactly which field to fix.
    /// </summary>
    private static void ReportResidualMarkers(
        string value, List<string> dottedFields, RewriteContext ctx, string activityName, string configKey)
    {
        var codes = ResidualMarkerRx.Matches(value)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0) return;

        var reason = dottedFields.Count > 0
            ? $"nested published-data name(s) {string.Join(", ", dottedFields.Distinct(StringComparer.Ordinal))} " +
              "have no NodePilot equivalent"
            : "the referenced variable/step is not part of this export, or the value is encrypted";

        ctx.Warnings.Add(
            $"'{ctx.RunbookName}' / '{activityName}': config '{configKey}' still contains an unresolved " +
            $"SCOrch reference [{string.Join(", ", codes)}] — {reason}. Set the value manually.");
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
