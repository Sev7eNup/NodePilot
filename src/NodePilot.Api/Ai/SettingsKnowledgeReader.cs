using System.Text.Json;
using NodePilot.Api.Configuration;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Ai;

/// <summary>
/// <see cref="ISettingsKnowledgeReader"/> over the admin settings adapter registry. Each section's
/// <see cref="ISettingsSectionAdapter.BuildCurrentPayload"/> is exactly the payload the admin
/// Settings API serves — secrets are already masked to <c>"********"</c> there — so serializing it
/// is safe to hand to the LLM. Scoped, because the underlying adapter registry is scoped.
/// </summary>
public sealed class SettingsKnowledgeReader(ISettingsSectionAdapterRegistry registry) : ISettingsKnowledgeReader
{
    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IReadOnlyList<SettingsSectionKnowledge> GetRedactedSnapshot() =>
        registry.All
            .Select(a => new SettingsSectionKnowledge(
                a.Descriptor.SectionPath,
                a.Descriptor.DisplayName,
                a.Descriptor.IsHotReloadable,
                JsonSerializer.SerializeToElement(a.BuildCurrentPayload(), SnapshotJson)))
            .ToList();
}
