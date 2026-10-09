using System.Diagnostics;
using System.Globalization;

namespace NodePilot.Api.Services;

/// <summary>
/// Log-scale histogram of run durations in milliseconds. Eight bins per doubling keep every value
/// within 4.5 % of its bin's representative, which is enough for a trend chart and lets hourly
/// histograms be merged into any window instead of sorting raw rows.
/// </summary>
internal sealed class DurationHistogram
{
    private const double BinsPerDoubling = 8;
    private readonly SortedDictionary<int, long> _counts = [];

    public long Count { get; private set; }

    /// <summary>Bin 0 holds runs under one millisecond; bin k covers [2^((k-1)/8), 2^(k/8)) ms.</summary>
    public static int BinOf(double milliseconds)
        => milliseconds < 1 ? 0 : 1 + (int)Math.Floor(BinsPerDoubling * Math.Log2(milliseconds));

    /// <summary>The geometric centre of a bin, reported for every run in it.</summary>
    public static double ValueOf(int bin)
        => bin <= 0 ? 0 : Math.Pow(2, (bin - 0.5) / BinsPerDoubling);

    /// <summary>Encodes durations as <c>bin:count</c> pairs in ascending bin order; empty for none.</summary>
    public static string Encode(IEnumerable<double> durationsMs)
    {
        var histogram = new DurationHistogram();
        foreach (var ms in durationsMs) histogram.AddBin(BinOf(ms), 1);
        return histogram.ToString();
    }

    /// <summary>Adds an encoded histogram to this one.</summary>
    public void Add(string encoded)
    {
        foreach (var pair in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf(':');
            AddBin(int.Parse(pair.AsSpan(0, separator), CultureInfo.InvariantCulture),
                long.Parse(pair.AsSpan(separator + 1), CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The middle value; averages the middle pair, as the raw query does.</summary>
    public double? Median => Count == 0 ? null : (ValueAt((Count + 1) / 2) + ValueAt((Count + 2) / 2)) / 2;

    /// <summary>Nearest-rank 95th percentile, as the raw query computes it.</summary>
    public double? P95 => Count == 0 ? null : ValueAt((Count * 95 + 99) / 100);

    public override string ToString()
        => string.Join(',', _counts.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}:{kv.Value}")));

    private void AddBin(int bin, long count)
    {
        _counts[bin] = _counts.GetValueOrDefault(bin) + count;
        Count += count;
    }

    /// <summary>The value of the run at a 1-based rank in ascending order.</summary>
    private double ValueAt(long rank)
    {
        long seen = 0;
        foreach (var (bin, count) in _counts)
        {
            seen += count;
            if (seen >= rank) return ValueOf(bin);
        }
        throw new UnreachableException("A rank is never larger than the number of runs.");
    }
}
