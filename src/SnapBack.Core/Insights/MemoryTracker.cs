using SnapBack.Core.Grouping;
using SnapBack.Core.Model;

namespace SnapBack.Core.Insights;

/// <summary>Committed private memory of one application instance at one moment.</summary>
public readonly record struct MemorySample(DateTime AtUtc, long Bytes);

/// <summary>Memory history of one running instance of an application.</summary>
public sealed class MemorySeries
{
    private readonly List<MemorySample> _samples = [];

    public MemorySeries(ProcessIdentity instance, string appKey, string displayName)
    {
        Instance = instance;
        AppKey = appKey;
        DisplayName = displayName;
    }

    /// <summary>Root process of the instance; a restarted app starts a new series.</summary>
    public ProcessIdentity Instance { get; }

    public string AppKey { get; }

    public string DisplayName { get; }

    public IReadOnlyList<MemorySample> Samples => _samples;

    public long LatestBytes => _samples.Count > 0 ? _samples[^1].Bytes : 0;

    internal void Add(MemorySample sample, int maxSamples)
    {
        _samples.Add(sample);
        if (_samples.Count <= maxSamples)
        {
            return;
        }

        // Thin out the older half (every other sample): the series keeps covering the whole
        // session with less detail further back, and never grows beyond maxSamples.
        var half = _samples.Count / 2;
        var older = _samples.Take(half).Where((_, i) => i % 2 == 0).ToList();
        var newer = _samples.Skip(half).ToList();
        _samples.Clear();
        _samples.AddRange(older);
        _samples.AddRange(newer);
    }
}

public enum GrowthLevel
{
    /// <summary>Not enough history yet, or no notable growth.</summary>
    None,

    /// <summary>Memory has grown noticeably.</summary>
    Growing,

    /// <summary>Memory has grown steadily and a lot for hours; worth a look.</summary>
    Unusual,
}

public sealed record GrowthAssessment(GrowthLevel Level, long FromBytes, long ToBytes, TimeSpan Span)
{
    public static readonly GrowthAssessment None = new(GrowthLevel.None, 0, 0, TimeSpan.Zero);

    public long GrowthBytes => ToBytes - FromBytes;
}

/// <summary>
/// Keeps a small, bounded memory history per application instance to spot steady growth.
/// </summary>
/// <remarks>
/// <para>Samples are taken from analyses that happen anyway (at most one sample per
/// <see cref="MinInterval"/>), so tracking adds no scanning of its own. Each series holds at most
/// <see cref="MaxSamples"/> samples and at most <see cref="MaxSeries"/> instances are tracked; series
/// of instances that exited are dropped. Nothing is written to disk.</para>
/// <para>Growth is never called a leak: steady growth can be a cache or more work (more tabs, a
/// bigger project). It is reported as unusual when, over at least two hours, committed memory grew by
/// 500 MB or more, to at least 1.5× its starting value, and rose in at least 70 % of the steps.</para>
/// </remarks>
public sealed class MemoryTracker
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    public const int MaxSamples = 96;
    public const int MaxSeries = 64;
    public static readonly TimeSpan MinSpan = TimeSpan.FromHours(2);
    public const int MinSamples = 6;
    public const long UnusualGrowthBytes = 500L << 20;
    public const double UnusualRatio = 1.5;
    public const long NotableGrowthBytes = 200L << 20;
    public const double NotableRatio = 1.3;

    private readonly object _gate = new();
    private readonly Dictionary<ProcessIdentity, MemorySeries> _series = [];
    private DateTime _lastSampleUtc = DateTime.MinValue;

    public DateTime? LastSampleUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastSampleUtc == DateTime.MinValue ? null : _lastSampleUtc;
            }
        }
    }

    /// <summary>Records one sample per application instance if <see cref="MinInterval"/> has passed.</summary>
    /// <returns>Whether a sample was recorded.</returns>
    public bool Record(IReadOnlyList<AppGroup> groups, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(groups);
        lock (_gate)
        {
            if (nowUtc - _lastSampleUtc < MinInterval)
            {
                return false;
            }

            _lastSampleUtc = nowUtc;
            var running = groups.Where(g => !g.IsProtected).ToDictionary(g => g.Id);

            foreach (var gone in _series.Keys.Where(id => !running.ContainsKey(id)).ToList())
            {
                _series.Remove(gone);
            }

            foreach (var g in running.Values)
            {
                if (!_series.TryGetValue(g.Id, out var series))
                {
                    series = new MemorySeries(g.Id, g.AppKey, g.DisplayName);
                    _series[g.Id] = series;
                }

                series.Add(new MemorySample(nowUtc, BytesOf(g)), MaxSamples);
            }

            // Keep the biggest consumers when there are too many instances.
            if (_series.Count > MaxSeries)
            {
                foreach (var drop in _series.Values.OrderByDescending(s => s.LatestBytes).Skip(MaxSeries).ToList())
                {
                    _series.Remove(drop.Instance);
                }
            }

            return true;
        }
    }

    /// <summary>A copy of the series of one instance (safe to read while sampling continues).</summary>
    public MemorySeries? For(ProcessIdentity instance)
    {
        lock (_gate)
        {
            return _series.TryGetValue(instance, out var s) ? Copy(s) : null;
        }
    }

    /// <summary>Copies of all series with their assessment, biggest growth first.</summary>
    public IReadOnlyList<(MemorySeries Series, GrowthAssessment Growth)> Assessments()
    {
        lock (_gate)
        {
            return _series.Values
                .Select(s => (Series: Copy(s), Growth: Assess(s)))
                .OrderByDescending(x => x.Growth.Level)
                .ThenByDescending(x => x.Growth.GrowthBytes)
                .ToList();
        }
    }

    public IReadOnlyList<(MemorySeries Series, GrowthAssessment Growth)> Unusual() =>
        Assessments().Where(x => x.Growth.Level == GrowthLevel.Unusual).ToList();

    /// <summary>Committed private memory of the group (falls back to the private working set).</summary>
    public static long BytesOf(AppGroup group) =>
        group.ActionableMembers.Sum(m => m.PrivateBytes > 0 ? m.PrivateBytes : m.PrivateWorkingSet);

    public static GrowthAssessment Assess(MemorySeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var s = series.Samples;
        if (s.Count < MinSamples)
        {
            return GrowthAssessment.None;
        }

        var span = s[^1].AtUtc - s[0].AtUtc;
        if (span < MinSpan)
        {
            return GrowthAssessment.None;
        }

        var from = Median(s.Take(3).Select(x => x.Bytes));
        var to = Median(s.Skip(s.Count - 3).Select(x => x.Bytes));
        var growth = to - from;
        var ratio = from > 0 ? (double)to / from : double.PositiveInfinity;

        var steps = 0;
        var rising = 0;
        for (var i = 1; i < s.Count; i++)
        {
            steps++;
            if (s[i].Bytes >= s[i - 1].Bytes * 0.98)
            {
                rising++;
            }
        }

        var steady = steps > 0 ? (double)rising / steps : 0;
        var stillHigh = s[^1].Bytes >= 0.9 * s.Max(x => x.Bytes);

        var level = growth >= UnusualGrowthBytes && ratio >= UnusualRatio && steady >= 0.7 && stillHigh
            ? GrowthLevel.Unusual
            : growth >= NotableGrowthBytes && ratio >= NotableRatio && steady >= 0.6
                ? GrowthLevel.Growing
                : GrowthLevel.None;

        return new GrowthAssessment(level, from, to, span);
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static MemorySeries Copy(MemorySeries s)
    {
        var copy = new MemorySeries(s.Instance, s.AppKey, s.DisplayName);
        foreach (var sample in s.Samples)
        {
            copy.Add(sample, int.MaxValue);
        }

        return copy;
    }
}
