using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodePilot.Core.Activities;

/// <summary>One config key an activity reads from its <c>data.config</c> object.</summary>
public sealed record ActivityConfigKeyDescriptor(
    string Key,
    string Type,
    bool Required,
    string Description);

/// <summary>
/// Purpose and config-key schema for one activity type. <see cref="Description"/> is the one-line
/// purpose the MCP tools surface; <see cref="PromptNotes"/> holds the extra semantics the AI prompt
/// needs, such as success rules and per-operation key matrices.
/// </summary>
public sealed record ActivityConfigEntry(
    string Description,
    IReadOnlyList<string> PromptNotes,
    IReadOnlyList<ActivityConfigKeyDescriptor> ConfigKeys);

/// <summary>
/// The curated per-activity config reference, parsed once from the embedded JSON.
///
/// <para><see cref="ActivityCatalog"/> holds only cross-cutting facts (category, remote flag,
/// stable output params), not config keys. This type covers the config keys for both consumers:
/// <c>NodePilot.Mcp</c> serves it as a resource and tool, and <c>NodePilot.Ai</c> renders the
/// activity-catalog section of the AI prompts from it. It lives in Core so neither project has to
/// depend on the other.</para>
///
/// <para>The keys must match <c>NodePilot.Engine/Activities/*.cs</c> and <c>Triggers/*.cs</c>. A
/// wrong key makes an agent author a node that sets a key the engine never reads, so the node looks
/// correct and does nothing. <c>ActivityConfigReferenceTests</c> guards coverage and shape.</para>
/// </summary>
public static class ActivityConfigReference
{
    private const string ResourceName = "NodePilot.Core.Activities.Embedded.activity-config-reference.json";

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The raw embedded JSON, served verbatim as the MCP resource body.</summary>
    public static string RawJson { get; }

    /// <summary>Parsed entries keyed by activity type.</summary>
    public static IReadOnlyDictionary<string, ActivityConfigEntry> ByType { get; }

    /// <summary>Schema version of the embedded document.</summary>
    public static int SchemaVersion { get; }

    static ActivityConfigReference()
    {
        RawJson = LoadResource();

        var doc = JsonSerializer.Deserialize<ReferenceDocument>(RawJson, ReadOptions)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' did not deserialize into a reference document.");

        SchemaVersion = doc.SchemaVersion;
        ByType = doc.Activities.ToDictionary(
            kv => kv.Key,
            kv => new ActivityConfigEntry(
                kv.Value.Description,
                kv.Value.PromptNotes ?? [],
                kv.Value.ConfigKeys ?? []),
            StringComparer.Ordinal);
    }

    /// <summary>The entry for <paramref name="activityType"/>, or null when it is not
    /// documented.</summary>
    public static ActivityConfigEntry? TryGet(string activityType)
        => ByType.TryGetValue(activityType, out var entry) ? entry : null;

    private static string LoadResource()
    {
        var asm = typeof(ActivityConfigReference).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found. Ensure NodePilot.Core.csproj includes "
                + "<EmbeddedResource Include=\"Activities\\Embedded\\activity-config-reference.json\" />.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record ReferenceDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("activities")] Dictionary<string, RawEntry> Activities);

    private sealed record RawEntry(
        string Description,
        IReadOnlyList<string>? PromptNotes,
        IReadOnlyList<ActivityConfigKeyDescriptor>? ConfigKeys);
}
