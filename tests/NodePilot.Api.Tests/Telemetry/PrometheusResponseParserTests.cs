using FluentAssertions;
using NodePilot.Telemetry;
using Xunit;

namespace NodePilot.Api.Tests.Telemetry;

/// <summary>
/// Pure parser coverage. The PrometheusClient.QueryScalarAsync path runs every Dashboard
/// scalar through this — a subtle parse miss surfaces as "—" in the UI, hard to debug.
/// Pin every shape: vector, scalar, malformed, empty result, non-numeric value.
/// </summary>
public class PrometheusResponseParserTests
{
    [Fact]
    public void TryExtractScalar_VectorResult_ReturnsFirstSampleValue()
    {
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\"," +
            "\"result\":[{\"metric\":{\"job\":\"up\"},\"value\":[1700000000,\"42.5\"]}]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().Be(42.5);
    }

    [Fact]
    public void TryExtractScalar_VectorWithMultipleSamples_TakesFirst()
    {
        // Prometheus may return multiple time series for ambiguous queries. The Dashboard
        // surfaces a single number, so we deliberately pick the first sample. Pin it so a
        // refactor doesn't silently switch to "max" or "sum".
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\"," +
            "\"result\":[" +
            "{\"metric\":{\"a\":\"1\"},\"value\":[1700000000,\"100\"]}," +
            "{\"metric\":{\"a\":\"2\"},\"value\":[1700000000,\"200\"]}]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().Be(100);
    }

    [Fact]
    public void TryExtractScalar_VectorEmptyResult_ReturnsNull()
    {
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().BeNull();
    }

    [Fact]
    public void TryExtractScalar_ScalarResultType_ReturnsValue()
    {
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"scalar\"," +
            "\"result\":[1700000000,\"3.14\"]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().Be(3.14);
    }

    [Fact]
    public void TryExtractScalar_NaNStringValue_ReturnsNullForJsonSafety()
    {
        // Prometheus encodes "NaN" as the literal string "NaN". double.TryParse with
        // NumberStyles.Float accepts that and returns double.NaN. Pin the actual
        // behaviour: the parser returns NaN, NOT null. The Dashboard layer is responsible
        // for filtering NaN before display if it wants to render "—".
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\"," +
            "\"result\":[{\"metric\":{},\"value\":[1700000000,\"NaN\"]}]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().BeNull();
    }

    [Theory]
    [InlineData("+Inf")]
    [InlineData("-Inf")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void TryExtractScalar_InfiniteValue_ReturnsNullForJsonSafety(string raw)
    {
        var body = $"{{\"status\":\"success\",\"data\":{{\"resultType\":\"vector\",\"result\":[{{\"metric\":{{}},\"value\":[1700000000,\"{raw}\"]}}]}}}}";
        PrometheusResponseParser.TryExtractScalar(body).Should().BeNull();
    }

    [Fact]
    public void TryExtractScalar_TotallyUnparseableString_ReturnsNull()
    {
        // The TryParse path falls through when the value string can't be coerced — e.g.
        // a malicious upstream injecting an HTML fragment. Pin null in that case.
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\"," +
            "\"result\":[{\"metric\":{},\"value\":[1700000000,\"<script>\"]}]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().BeNull();
    }

    [Fact]
    public void TryExtractScalar_MatrixResultType_ReturnsNull()
    {
        // Range queries return matrix; this parser is instant-only — return null so
        // a misuse surfaces as missing data instead of a wrong number.
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\"," +
            "\"result\":[{\"metric\":{},\"values\":[[1700000000,\"1\"]]}]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":{\"resultType\":\"vector\"}}")]
    [InlineData("{\"data\":{\"resultType\":\"vector\",\"result\":[{}]}}")]
    public void TryExtractScalar_MalformedShapes_ReturnsNullWithoutThrowing(string body)
    {
        // Defensive: a Prometheus-side bug or an upstream proxy injecting HTML must
        // never crash the dashboard request thread.
        var act = () => PrometheusResponseParser.TryExtractScalar(body);
        act.Should().NotThrow();
        PrometheusResponseParser.TryExtractScalar(body).Should().BeNull();
    }

    [Fact]
    public void TryExtractScalar_InvariantCultureFormat_NotLocaleDependent()
    {
        // Prometheus emits decimal points (e.g. "1.5"). On a German-locale CI runner,
        // a culture-sensitive parse could try to read 1,5 → 15. Pin invariant parsing.
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\"," +
            "\"result\":[{\"metric\":{},\"value\":[1700000000,\"1234.567\"]}]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().Be(1234.567);
    }

    [Fact]
    public void TryExtractScalar_NegativeValue_Parsed()
    {
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"scalar\"," +
            "\"result\":[1700000000,\"-42\"]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().Be(-42);
    }

    [Fact]
    public void TryExtractScalar_ScientificNotation_Parsed()
    {
        // Prometheus may emit e-notation for very large or very small values.
        const string body =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\"," +
            "\"result\":[{\"metric\":{},\"value\":[1700000000,\"1.5e+06\"]}]}}";

        PrometheusResponseParser.TryExtractScalar(body).Should().Be(1_500_000);
    }
}
