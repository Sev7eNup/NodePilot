using FluentAssertions;
using NodePilot.Api.Services;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// Covers <see cref="DurationHistogram"/>, which turns the dashboard's duration percentiles into a
/// sum over hourly buckets. Its answers are approximate by design; these tests pin how approximate.
/// </summary>
public sealed class DurationHistogramTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(7)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1024)]
    [InlineData(60_000)]
    [InlineData(3_600_000)]
    [InlineData(2_592_000_000)]
    public void ValueOf_BinOfAnyDuration_StaysWithinFourAndAHalfPercent(double milliseconds)
    {
        var reported = DurationHistogram.ValueOf(DurationHistogram.BinOf(milliseconds));

        Math.Abs(reported - milliseconds).Should().BeLessThanOrEqualTo(milliseconds * 0.045);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.4)]
    [InlineData(0.999)]
    public void BinOf_RunUnderOneMillisecond_ReportsZero(double milliseconds)
    {
        DurationHistogram.BinOf(milliseconds).Should().Be(0);
        DurationHistogram.ValueOf(0).Should().Be(0);
    }

    [Fact]
    public void BinOf_LongerRun_NeverLandsInAnEarlierBin()
    {
        var previous = 0;
        for (var ms = 1.0; ms < 1e10; ms *= 1.01)
        {
            var bin = DurationHistogram.BinOf(ms);
            bin.Should().BeGreaterThanOrEqualTo(previous);
            previous = bin;
        }
    }

    [Fact]
    public void Encode_ListsBinsAscendingWithTheirCounts()
    {
        var encoded = DurationHistogram.Encode([5000, 1000, 1000]);

        var low = DurationHistogram.BinOf(1000);
        var high = DurationHistogram.BinOf(5000);
        encoded.Should().Be($"{low}:2,{high}:1");
    }

    [Fact]
    public void Encode_NoRuns_IsEmpty()
    {
        DurationHistogram.Encode([]).Should().BeEmpty();
        var histogram = new DurationHistogram();
        histogram.Count.Should().Be(0);
        histogram.Median.Should().BeNull();
        histogram.P95.Should().BeNull();
    }

    [Fact]
    public void Add_MergesEncodedHistogramsBinByBin()
    {
        var merged = new DurationHistogram();
        merged.Add(DurationHistogram.Encode([1000, 2000]));
        merged.Add(DurationHistogram.Encode([1000]));
        merged.Add(string.Empty);

        merged.Count.Should().Be(3);
        merged.ToString().Should().Be(DurationHistogram.Encode([1000, 1000, 2000]));
    }

    [Fact]
    public void MedianAndP95_FollowTheRankRulesOfTheRawQuery()
    {
        // Twenty runs of 1..20 s: the raw query reports the mean of ranks 10 and 11 as the median
        // and rank 19 as P95.
        var histogram = new DurationHistogram();
        histogram.Add(DurationHistogram.Encode(Enumerable.Range(1, 20).Select(s => s * 1000.0)));

        histogram.Median.Should().Be(
            (DurationHistogram.ValueOf(DurationHistogram.BinOf(10_000)) + DurationHistogram.ValueOf(DurationHistogram.BinOf(11_000))) / 2);
        histogram.P95.Should().Be(DurationHistogram.ValueOf(DurationHistogram.BinOf(19_000)));
        histogram.Median!.Value.Should().BeApproximately(10_500, 10_500 * 0.045);
        histogram.P95!.Value.Should().BeApproximately(19_000, 19_000 * 0.045);
    }

    [Fact]
    public void MedianAndP95_SingleRun_ReportThatRun()
    {
        var histogram = new DurationHistogram();
        histogram.Add(DurationHistogram.Encode([42_000]));

        var expected = DurationHistogram.ValueOf(DurationHistogram.BinOf(42_000));
        histogram.Median.Should().Be(expected);
        histogram.P95.Should().Be(expected);
    }
}
