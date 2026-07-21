using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NodePilot.Api.Ai;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Ai;

public class SettingsKnowledgeReaderTests
{
    // Minimal adapter: the reader only touches Descriptor + BuildCurrentPayload; the rest is unused.
    private sealed class StubAdapter(SettingsSectionDescriptor descriptor, object payload) : ISettingsSectionAdapter
    {
        public SettingsSectionDescriptor Descriptor => descriptor;
        public IReadOnlyList<string> ConfigKeys => Array.Empty<string>();
        public object BuildCurrentPayload() => payload;
        public object BuildPayloadFromJson(JsonObject? section) => throw new NotSupportedException();
        public object Deserialize(JsonElement payload, JsonSerializerOptions options) => throw new NotSupportedException();
        public IReadOnlyList<ValidationResult> Validate(object payload) => throw new NotSupportedException();
        public JsonObject BuildSectionObject(object payload, JsonObject? previousSection) => throw new NotSupportedException();
    }

    private sealed class StubRegistry(IReadOnlyList<ISettingsSectionAdapter> all) : ISettingsSectionAdapterRegistry
    {
        public IReadOnlyList<ISettingsSectionAdapter> All => all;
        public ISettingsSectionAdapter? Find(string sectionPath) =>
            all.FirstOrDefault(a => string.Equals(a.Descriptor.SectionPath, sectionPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetRedactedSnapshot_SerializesEverySection_WithMaskedSecretPreserved()
    {
        var smtp = SettingsSchema.Find("Smtp")!;
        var engine = SettingsSchema.Find("Engine")!;
        var reader = new SettingsKnowledgeReader(new StubRegistry(new ISettingsSectionAdapter[]
        {
            // BuildCurrentPayload already masks secrets to "********" (see SettingsSections.BuildSmtpDto).
            new StubAdapter(smtp, new { host = "localhost", port = 25, password = "********" }),
            new StubAdapter(engine, new { maxConcurrentSteps = 600 }),
        }));

        var snapshot = reader.GetRedactedSnapshot();

        snapshot.Should().HaveCount(2);
        snapshot[0].Section.Should().Be("Smtp");
        snapshot[0].DisplayName.Should().Be(smtp.DisplayName);
        snapshot[0].HotReloadable.Should().Be(smtp.IsHotReloadable);
        // camelCase, nested JSON, and the masked secret survives verbatim (no plaintext leak).
        snapshot[0].Values.GetProperty("password").GetString().Should().Be("********");
        snapshot[0].Values.GetProperty("host").GetString().Should().Be("localhost");
        snapshot[1].Values.GetProperty("maxConcurrentSteps").GetInt32().Should().Be(600);
    }
}
