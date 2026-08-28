using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Conditions;
using NodePilot.Scheduler.SystemAlerts;
using NodePilot.Scheduler.SystemAlerts.Sources;
using Xunit;

namespace NodePilot.Engine.Tests.SystemAlerts;

/// <summary>
/// Guards the invariants of the system-alert catalog (the modular "system alert sources"
/// architecture
/// introduced by ADR-0008 for infra/signal alerts): unique/consistent source ids, well-formed
/// descriptors (operators match field type, enum fields declare values), and preset conditions that
/// parse
/// against the same <see cref="ConditionEvaluator"/> the alerting filters run on.
/// </summary>
public class SystemAlertCatalogTests
{
    private static IReadOnlyList<ISystemAlertSource> RealSources() =>
    [
        new BacklogSource(),
        new PendingSource(),
        new CancelRateSource(),
        new MachineUnreachableSource(),
        new ServiceStaleSource(),
        new CredentialExpirySource(),
        new WorkflowNoRecentSuccessSource(),
        new ScheduleMissedSource(),
        new ExecutionResultSource(),
        new StuckExecutionSource(),
        new WorkflowHealthSource(),
        new AlertDeliveryFailureSource(),
        new TriggerUnhealthySource(new NodePilot.Scheduler.TriggerHealthRegistry()),
        new AuditEventSource(),
    ];

    [Fact]
    public void Catalog_BuildsFromSources_OrdersDescriptorsBySourceId()
    {
        var catalog = new SystemAlertCatalog(RealSources());

        catalog.Descriptors.Select(d => d.SourceId).Should().Equal(
            "alert-delivery-failed", "audit-event", "backlog", "cancel-rate", "credential-expiring",
            "execution-result", "execution-stuck", "machine-unreachable", "pending", "schedule-missed",
            "service-stale", "trigger-unhealthy", "workflow-health", "workflow-no-recent-success");
    }

    [Fact]
    public void Catalog_Find_ResolvesRegisteredSources_AndNullForUnknown()
    {
        var catalog = new SystemAlertCatalog(RealSources());

        catalog.Find("backlog").Should().BeOfType<BacklogSource>();
        catalog.Find("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void Catalog_DuplicateSourceId_ThrowsAtConstruction()
    {
        var act = () => new SystemAlertCatalog([new BacklogSource(), new BacklogSource()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*backlog*");
    }

    [Fact]
    public void Catalog_SourceIdMismatchingDescriptor_ThrowsAtConstruction()
    {
        var act = () => new SystemAlertCatalog([new MismatchedSource()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SourceId*");
    }

    [Fact]
    public void EveryDescriptor_FieldOperators_MatchCanonicalSetForType()
    {
        foreach (var d in new SystemAlertCatalog(RealSources()).Descriptors)
        foreach (var field in d.Fields)
            field.Operators.Should().Equal(SystemAlertOperators.For(field.Type),
                $"field '{d.SourceId}.{field.Name}' ({field.Type}) must expose the canonical operator set");
    }

    [Fact]
    public void EveryDescriptor_EnumFields_DeclareValues()
    {
        foreach (var d in new SystemAlertCatalog(RealSources()).Descriptors)
        foreach (var field in d.Fields.Where(f => f.Type == SystemAlertFieldType.Enum))
            field.EnumValues.Should().NotBeNullOrEmpty($"enum field '{d.SourceId}.{field.Name}' must list its values");
    }

    [Fact]
    public void ExecutionResult_CancelledBy_DeclaresNeverStartedRecoveryMarkers()
    {
        var descriptor = new ExecutionResultSource().Describe();
        var field = descriptor.Fields.Single(candidate => candidate.Name == "cancelledBy");

        field.EnumValues.Should().Contain(["failover-pending", "reconciler-pending"]);
    }

    [Fact]
    public void EveryPresetCondition_ParsesAndEvaluatesAgainstTheConditionEvaluator()
    {
        var empty = new Dictionary<string, string>();

        foreach (var d in new SystemAlertCatalog(RealSources()).Descriptors)
        foreach (var preset in d.Presets.Where(p => p.ConditionJson is not null))
        {
            using var doc = JsonDocument.Parse(preset.ConditionJson!);
            var eval = () => ConditionEvaluator.Evaluate(doc.RootElement,
                new ConditionContext(new Dictionary<string, ActivityResult>(), null, null, null, empty));

            eval.Should().NotThrow($"preset '{d.SourceId}/{preset.PresetId}' must be a valid condition AST");
        }
    }

    [Fact]
    public void PresetConditions_ReferenceOnlyDeclaredFields()
    {
        foreach (var d in new SystemAlertCatalog(RealSources()).Descriptors)
        foreach (var preset in d.Presets.Where(p => p.ConditionJson is not null))
        {
            var fieldNames = d.Fields.Select(f => f.Name).ToHashSet();
            foreach (var referenced in EventOperandNames(preset.ConditionJson!))
                fieldNames.Should().Contain(referenced,
                    $"preset '{d.SourceId}/{preset.PresetId}' references undeclared field '{referenced}'");
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EverySource_AndCategory_HasFrontendLabel(string lang)
    {
        // The UI falls back to the raw id when a label is missing, so nothing else would notice a
        // forgotten `system.sourceLabels` / `system.categories` entry when a source is added.
        var system = ReadAlertsJson(lang).GetProperty("system");
        var sourceLabels = system.GetProperty("sourceLabels").EnumerateObject().Select(p => p.Name).ToHashSet();
        var categories = system.GetProperty("categories").EnumerateObject().Select(p => p.Name).ToHashSet();

        foreach (var d in new SystemAlertCatalog(RealSources()).Descriptors)
        {
            sourceLabels.Should().Contain(d.SourceId, $"{lang}/alerts.json must label source '{d.SourceId}'");
            categories.Should().Contain(d.Category.ToString(), $"{lang}/alerts.json must label category '{d.Category}'");
        }
        foreach (var category in Enum.GetNames<SystemAlertCategory>())
            categories.Should().Contain(category, $"{lang}/alerts.json must label category '{category}'");
    }

    private static JsonElement ReadAlertsJson(string lang)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "nodepilot-ui", "src", "i18n", "locales", lang, "alerts.json");
        File.Exists(path).Should().BeTrue($"{lang}/alerts.json must exist at {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }

    // Collects every source:"event" operand name in an AST so a preset can't reference a field the
    // descriptor doesn't declare (which the strict alerting validator will later reject at save
    // time).
    private static IEnumerable<string> EventOperandNames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var names = new List<string>();
        Walk(doc.RootElement, names);
        return names;

        static void Walk(JsonElement el, List<string> acc)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("kind", out var k) && k.GetString() == "variable"
                    && el.TryGetProperty("source", out var s) && s.GetString() == "event"
                    && el.TryGetProperty("name", out var n) && n.GetString() is { } name)
                    acc.Add(name);
                foreach (var prop in el.EnumerateObject()) Walk(prop.Value, acc);
            }
            else if (el.ValueKind == JsonValueKind.Array)
                foreach (var item in el.EnumerateArray()) Walk(item, acc);
        }
    }

    /// <summary>A source whose runtime SourceId disagrees with its descriptor — must fail catalog
    /// construction.</summary>
    private sealed class MismatchedSource : ISystemAlertSource
    {
        public string SourceId => "runtime-id";
        public SystemAlertSourceDescriptor Describe() => new(
            "descriptor-id", SystemAlertCategory.Queue, SystemAlertScopeCapability.GlobalOnly,
            NotificationSeverity.Warning, [], [], []);
        public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SystemAlertObservation>>([]);
    }
}
