using System.Text.Json;
using FluentAssertions;
using NodePilot.Api.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace NodePilot.Api.Tests.Logging;

/// <summary>
/// Pin the ECS field-naming contract: SIEM ingest pipelines (Elastic Filebeat module,
/// Splunk HEC parser, Sentinel data connector) all bind to specific JSON field paths.
/// A regression that renamed <c>@timestamp</c> to <c>timestamp</c> would silently
/// breaks dashboards months later — these tests are the only thing standing between
/// "log shipping works" and "the SIEM gets unparseable rows".
/// </summary>
public class EcsJsonFormatterTests
{
    private static string Format(LogEvent evt)
    {
        var formatter = new EcsJsonFormatter();
        using var sw = new StringWriter();
        formatter.Format(evt, sw);
        return sw.ToString().Trim();
    }

    private static LogEvent Make(LogEventLevel level, string template, IEnumerable<LogEventProperty>? props = null,
        Exception? exception = null)
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception,
            parser.Parse(template),
            props ?? []);
    }

    [Fact]
    public void Format_EmitsEcsTimestampLevelAndMessage()
    {
        var evt = Make(LogEventLevel.Information, "step started");
        var json = JsonDocument.Parse(Format(evt)).RootElement;

        json.GetProperty("@timestamp").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("log.level").GetString().Should().Be("info");
        json.GetProperty("message").GetString().Should().Be("step started");
        json.GetProperty("ecs.version").GetString().Should().Be("1.12.0");
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, "trace")]
    [InlineData(LogEventLevel.Debug, "debug")]
    [InlineData(LogEventLevel.Information, "info")]
    [InlineData(LogEventLevel.Warning, "warn")]
    [InlineData(LogEventLevel.Error, "error")]
    [InlineData(LogEventLevel.Fatal, "fatal")]
    public void Format_MapsSerilogLevelToEcsLevel(LogEventLevel input, string expected)
    {
        var evt = Make(input, "x");
        var json = JsonDocument.Parse(Format(evt)).RootElement;
        json.GetProperty("log.level").GetString().Should().Be(expected);
    }

    [Fact]
    public void Format_NodePilotPropertiesGoUnderCustomNamespace()
    {
        var props = new[]
        {
            new LogEventProperty("WorkflowId", new ScalarValue(Guid.Parse("11111111-1111-1111-1111-111111111111"))),
            new LogEventProperty("ExecutionId", new ScalarValue(Guid.Parse("22222222-2222-2222-2222-222222222222"))),
            new LogEventProperty("StepId", new ScalarValue("step-99")),
        };
        var evt = Make(LogEventLevel.Information, "step ran", props);
        var json = JsonDocument.Parse(Format(evt)).RootElement;

        var np = json.GetProperty("nodepilot");
        np.GetProperty("workflow_id").GetString().Should().Be("11111111-1111-1111-1111-111111111111");
        np.GetProperty("execution_id").GetString().Should().Be("22222222-2222-2222-2222-222222222222");
        np.GetProperty("step_id").GetString().Should().Be("step-99");
    }

    [Fact]
    public void Format_PascalCaseProperty_IsConvertedToSnakeCase()
    {
        var props = new[] { new LogEventProperty("StepStartedAt", new ScalarValue(42L)) };
        var evt = Make(LogEventLevel.Information, "x", props);
        var json = JsonDocument.Parse(Format(evt)).RootElement;
        json.GetProperty("nodepilot").GetProperty("step_started_at").GetInt64().Should().Be(42L);
    }

    [Fact]
    public void Format_ExceptionIsEmittedAsErrorObject()
    {
        Exception ex;
        try { throw new InvalidOperationException("expected boom"); }
        catch (Exception caught) { ex = caught; }

        var evt = Make(LogEventLevel.Error, "step failed", null, ex);
        var json = JsonDocument.Parse(Format(evt)).RootElement;

        var error = json.GetProperty("error");
        error.GetProperty("type").GetString().Should().Be("System.InvalidOperationException");
        error.GetProperty("message").GetString().Should().Be("expected boom");
        error.GetProperty("stack_trace").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Format_NoProperties_OmitsNodepilotObject()
    {
        var evt = Make(LogEventLevel.Information, "plain message");
        var json = JsonDocument.Parse(Format(evt)).RootElement;
        json.TryGetProperty("nodepilot", out _).Should().BeFalse();
    }

    [Fact]
    public void Format_EmitsOneJsonLineWithTrailingNewline()
    {
        var evt = Make(LogEventLevel.Information, "x");
        var formatter = new EcsJsonFormatter();
        using var sw = new StringWriter();
        formatter.Format(evt, sw);
        var raw = sw.ToString();

        raw.Should().EndWith(Environment.NewLine,
            "every formatted event must terminate the line so log shippers like filebeat can split events on \\n");
        raw.Trim().Split('\n').Should().HaveCount(1, "one event = one line");
    }

    [Fact]
    public void Format_NumericProperty_IsEmittedAsNumberNotString()
    {
        var props = new[] { new LogEventProperty("Duration", new ScalarValue(123L)) };
        var evt = Make(LogEventLevel.Information, "x", props);
        var json = JsonDocument.Parse(Format(evt)).RootElement;

        var dur = json.GetProperty("nodepilot").GetProperty("duration");
        dur.ValueKind.Should().Be(JsonValueKind.Number, "numeric properties must round-trip as JSON numbers, not strings");
        dur.GetInt64().Should().Be(123L);
    }

    /// <summary>
    /// SIEM dashboards filter by service.name + deployment.environment; both must land
    /// at the JSON root in their proper nested ECS shape, NOT under nodepilot.*.
    /// Without this hoist, a Kibana board grouping by service.name would see no data.
    /// </summary>
    [Fact]
    public void Format_EcsRootProperties_AreHoistedToJsonRoot()
    {
        var props = new[]
        {
            new LogEventProperty("service.name", new ScalarValue("nodepilot-api")),
            new LogEventProperty("service.version", new ScalarValue("1.0.0")),
            new LogEventProperty("host.name", new ScalarValue("np-prod-01")),
            new LogEventProperty("deployment.environment", new ScalarValue("prod")),
            new LogEventProperty("WorkflowId", new ScalarValue("wf-1")),
        };
        var evt = Make(LogEventLevel.Information, "x", props);
        var json = JsonDocument.Parse(Format(evt)).RootElement;

        json.GetProperty("service").GetProperty("name").GetString().Should().Be("nodepilot-api");
        json.GetProperty("service").GetProperty("version").GetString().Should().Be("1.0.0");
        json.GetProperty("host").GetProperty("name").GetString().Should().Be("np-prod-01");
        json.GetProperty("deployment").GetProperty("environment").GetString().Should().Be("prod");

        // ECS-prefixed props must NOT also appear under nodepilot — that would double the
        // bytes per event and break dashboards that count distinct service names.
        var np = json.GetProperty("nodepilot");
        np.TryGetProperty("service_name", out _).Should().BeFalse();
        np.TryGetProperty("host_name", out _).Should().BeFalse();
        np.GetProperty("workflow_id").GetString().Should().Be("wf-1");
    }

    /// <summary>
    /// S2 (a SIEM-integration finding) — per-event ECS fields must also land at the JSON root. Standard SIEM
    /// detection rules (Sigma, Sentinel analytics, Elastic detection) bind to
    /// <c>event.action</c>, <c>user.id</c>, <c>source.ip</c>, <c>trace.id</c> etc.
    /// Hiding those under nodepilot.* would force every operator to write custom
    /// field-mapping pipelines.
    /// </summary>
    [Fact]
    public void Format_PerEventEcsFields_AreHoistedToJsonRoot()
    {
        var props = new[]
        {
            new LogEventProperty("event.action", new ScalarValue("WORKFLOW_PUBLISHED")),
            new LogEventProperty("event.category", new ScalarValue("configuration")),
            new LogEventProperty("event.outcome", new ScalarValue("success")),
            new LogEventProperty("user.id", new ScalarValue("user-42")),
            new LogEventProperty("user.name", new ScalarValue("alice")),
            new LogEventProperty("source.ip", new ScalarValue("10.1.2.3")),
            new LogEventProperty("trace.id", new ScalarValue("trace-abc")),
            new LogEventProperty("span.id", new ScalarValue("span-xyz")),
        };
        var evt = Make(LogEventLevel.Information, "audit", props);
        var json = JsonDocument.Parse(Format(evt)).RootElement;

        json.GetProperty("event").GetProperty("action").GetString().Should().Be("WORKFLOW_PUBLISHED");
        json.GetProperty("event").GetProperty("category").GetString().Should().Be("configuration");
        json.GetProperty("event").GetProperty("outcome").GetString().Should().Be("success");
        json.GetProperty("user").GetProperty("id").GetString().Should().Be("user-42");
        json.GetProperty("user").GetProperty("name").GetString().Should().Be("alice");
        json.GetProperty("source").GetProperty("ip").GetString().Should().Be("10.1.2.3");
        json.GetProperty("trace").GetProperty("id").GetString().Should().Be("trace-abc");
        json.GetProperty("span").GetProperty("id").GetString().Should().Be("span-xyz");
        json.TryGetProperty("nodepilot", out _).Should().BeFalse(
            "no nodepilot.* properties were emitted; the wrapper object should not appear");
    }

    /// <summary>
    /// S3 (a SIEM-integration finding) — two source-property names that normalize to the same snake_case form must
    /// not produce duplicate JSON keys. Several SIEM ingest pipelines (Filebeat strict
    /// mode, Splunk HEC validating endpoint) reject duplicate-key documents outright;
    /// others silently last-wins. Pinning last-wins explicitly keeps every ingest
    /// pipeline consistent.
    /// </summary>
    [Fact]
    public void Format_DuplicateNormalizedKeys_AreDeduplicated_LastWins()
    {
        var props = new[]
        {
            // Both normalize to "workflow_id".
            new LogEventProperty("WorkflowId", new ScalarValue("first-value")),
            new LogEventProperty("workflow_id", new ScalarValue("last-value")),
        };
        var evt = Make(LogEventLevel.Information, "x", props);
        var json = JsonDocument.Parse(Format(evt)).RootElement;

        var np = json.GetProperty("nodepilot");
        // No double-write: the JSON parser would have rejected on duplicate keys before
        // reaching here, but explicitly assert one-and-only-one workflow_id.
        np.GetProperty("workflow_id").GetString().Should().Be("last-value",
            "second source-name in property bag must overwrite the first when they normalize to the same target");
    }

    [Fact]
    public void Format_DuplicateKeysWithinEcsRootGroup_AlsoDeduped()
    {
        // Same dedup contract for ECS-root-prefixed groups. user.Id and user.id both
        // normalize to id within the user object.
        var props = new[]
        {
            new LogEventProperty("user.Id", new ScalarValue("first")),
            new LogEventProperty("user.id", new ScalarValue("second")),
        };
        var evt = Make(LogEventLevel.Information, "x", props);
        var json = JsonDocument.Parse(Format(evt)).RootElement;

        json.GetProperty("user").GetProperty("id").GetString().Should().Be("second");
    }

    [Fact]
    public void Format_UnknownLevel_FallsBackToInfo()
    {
        // Defensive default branch in MapLevel — an out-of-range level must still map to a
        // valid ECS level string rather than throwing while formatting a log line.
        var evt = Make((LogEventLevel)99, "x");
        var json = JsonDocument.Parse(Format(evt)).RootElement;
        json.GetProperty("log.level").GetString().Should().Be("info");
    }

    [Fact]
    public void Format_AllScalarTypes_RoundTripWithCorrectJsonKind()
    {
        var utc = new DateTime(2026, 7, 8, 12, 30, 45, DateTimeKind.Utc);
        var dto = new DateTimeOffset(2026, 7, 8, 14, 30, 45, TimeSpan.FromHours(2));
        var guid = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var props = new[]
        {
            new LogEventProperty("flag", new ScalarValue(true)),
            new LogEventProperty("n32", new ScalarValue(7)),
            new LogEventProperty("n64", new ScalarValue(42L)),
            new LogEventProperty("dbl", new ScalarValue(3.5d)),
            new LogEventProperty("dec", new ScalarValue(9.99m)),
            new LogEventProperty("dt", new ScalarValue(utc)),
            new LogEventProperty("dto", new ScalarValue(dto)),
            new LogEventProperty("guidv", new ScalarValue(guid)),
            new LogEventProperty("nullv", new ScalarValue(null)),
            // TimeSpan is not a first-class scalar → default branch stringifies it.
            new LogEventProperty("fallbackv", new ScalarValue(TimeSpan.FromSeconds(5))),
        };
        var evt = Make(LogEventLevel.Information, "x", props);
        var np = JsonDocument.Parse(Format(evt)).RootElement.GetProperty("nodepilot");

        np.GetProperty("flag").ValueKind.Should().Be(JsonValueKind.True);
        np.GetProperty("n32").GetInt32().Should().Be(7);
        np.GetProperty("n64").GetInt64().Should().Be(42L);
        np.GetProperty("dbl").GetDouble().Should().Be(3.5d);
        np.GetProperty("dec").GetDecimal().Should().Be(9.99m);
        np.GetProperty("dt").GetString().Should().Be(utc.ToString("o"));
        np.GetProperty("dto").GetString().Should().Be(dto.UtcDateTime.ToString("o"));
        np.GetProperty("guidv").GetString().Should().Be(guid.ToString());
        np.GetProperty("nullv").ValueKind.Should().Be(JsonValueKind.Null);
        np.GetProperty("fallbackv").GetString().Should().Be(TimeSpan.FromSeconds(5).ToString());
    }

    [Fact]
    public void Format_SequenceProperty_EmittedAsJsonArray_WithMixedElementKinds()
    {
        var seq = new SequenceValue(new LogEventPropertyValue[]
        {
            new ScalarValue(null),
            new ScalarValue("txt"),
            new ScalarValue(true),
            new ScalarValue(7),
            new ScalarValue(8L),
            new ScalarValue(1.5d),
            // Non-first-class scalar (default branch of the element writer).
            new ScalarValue(TimeSpan.FromMinutes(1)),
            // Non-scalar element → stringified fallback.
            new StructureValue(new[] { new LogEventProperty("k", new ScalarValue("v")) }),
        });
        var evt = Make(LogEventLevel.Information, "x", new[] { new LogEventProperty("tags", seq) });
        var arr = JsonDocument.Parse(Format(evt)).RootElement.GetProperty("nodepilot").GetProperty("tags");

        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.GetArrayLength().Should().Be(8);
        arr[0].ValueKind.Should().Be(JsonValueKind.Null);
        arr[1].GetString().Should().Be("txt");
        arr[2].ValueKind.Should().Be(JsonValueKind.True);
        arr[3].GetInt32().Should().Be(7);
        arr[4].GetInt64().Should().Be(8L);
        arr[5].GetDouble().Should().Be(1.5d);
        arr[6].ValueKind.Should().Be(JsonValueKind.String, "unhandled scalar types fall back to a string");
        arr[7].ValueKind.Should().Be(JsonValueKind.String, "non-scalar elements are stringified");
    }

    [Fact]
    public void Format_StructureProperty_EmittedAsNestedObject_WithNormalizedKeys()
    {
        var structure = new StructureValue(new[]
        {
            new LogEventProperty("InnerName", new ScalarValue("alpha")),
            new LogEventProperty("InnerCount", new ScalarValue(3)),
        });
        var evt = Make(LogEventLevel.Information, "x", new[] { new LogEventProperty("Detail", structure) });
        var np = JsonDocument.Parse(Format(evt)).RootElement.GetProperty("nodepilot");

        var detail = np.GetProperty("detail");
        detail.ValueKind.Should().Be(JsonValueKind.Object);
        detail.GetProperty("inner_name").GetString().Should().Be("alpha");
        detail.GetProperty("inner_count").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Format_DictionaryProperty_EmittedAsNestedObject()
    {
        var dict = new DictionaryValue(new[]
        {
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("first"), new ScalarValue(1)),
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("second"), new ScalarValue("two")),
        });
        var evt = Make(LogEventLevel.Information, "x", new[] { new LogEventProperty("counters", dict) });
        var np = JsonDocument.Parse(Format(evt)).RootElement.GetProperty("nodepilot");

        var counters = np.GetProperty("counters");
        counters.ValueKind.Should().Be(JsonValueKind.Object);
        counters.GetProperty("first").GetInt32().Should().Be(1);
        counters.GetProperty("second").GetString().Should().Be("two");
    }

    [Fact]
    public void Format_UnknownPropertyValueType_FallsBackToToString()
    {
        // A LogEventPropertyValue that is none of Scalar/Sequence/Structure/Dictionary must
        // still serialize via its ToString(), never throw.
        var evt = Make(LogEventLevel.Information, "x", new[] { new LogEventProperty("weird", new CustomValue()) });
        var np = JsonDocument.Parse(Format(evt)).RootElement.GetProperty("nodepilot");
        np.GetProperty("weird").GetString().Should().Be("custom-rendered");
    }

    private sealed class CustomValue : LogEventPropertyValue
    {
        public override void Render(TextWriter output, string? format = null, IFormatProvider? formatProvider = null)
            => output.Write("custom-rendered");
    }
}
