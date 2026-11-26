namespace DWIS.Blackboard.Perf;

/// <summary>
/// The latency of every write and read the suite makes, one series per writer and one for the reader, with a timeline
/// of named phases (baseline, burst, remove, settle, after). The report splits the samples by phase, so the writes made
/// while the semantic side was busy can be compared with the writes made while it was idle; NBomber's own statistics
/// cover the whole run and cannot make that split. From the old suite's <c>WriteLatencyLedger</c>.
/// </summary>
public sealed class LatencyLedger
{
    public readonly record struct Sample(long UtcTicks, double LatencyMs, bool Ok);
    public readonly record struct Phase(string Name, DateTime StartUtc);
    public sealed record Stats(int Count, int Failed, double P50, double P95, double P99, double Max, int Slow);

    private readonly List<Sample>[] _samples;
    private readonly List<Phase> _phases = [];

    /// <summary>A write slower than this counts as slow in the report (a pause seen from the client).</summary>
    public const double SlowMs = 20;

    public LatencyLedger(int series)
    {
        _samples = Enumerable.Range(0, series).Select(_ => new List<Sample>(capacity: 20_000)).ToArray();
    }

    public int Series => _samples.Length;

    public void Record(int series, DateTime startedUtc, double latencyMs, bool ok)
    {
        var list = _samples[series];
        lock (list)
        {
            list.Add(new Sample(startedUtc.Ticks, latencyMs, ok));
        }
    }

    /// <summary>Starts a phase now. Samples before the first phase (the warm-up) are not reported.</summary>
    public void MarkPhase(string name)
    {
        lock (_phases)
        {
            _phases.Add(new Phase(name, DateTime.UtcNow));
        }
    }

    public IReadOnlyList<Phase> Phases
    {
        get { lock (_phases) { return [.. _phases]; } }
    }

    /// <summary>The statistics of one series, one entry per phase, in the order the phases started.</summary>
    public IReadOnlyList<(Phase Phase, Stats Stats)> Report(int series)
    {
        var phases = Phases;
        Sample[] samples;
        lock (_samples[series])
        {
            samples = [.. _samples[series]];
        }
        var result = new List<(Phase, Stats)>();
        for (var i = 0; i < phases.Count; i++)
        {
            var from = phases[i].StartUtc.Ticks;
            var to = i + 1 < phases.Count ? phases[i + 1].StartUtc.Ticks : long.MaxValue;
            result.Add((phases[i], Compute(samples.Where(s => s.UtcTicks >= from && s.UtcTicks < to).ToArray())));
        }
        return result;
    }

    /// <summary>The statistics of one series over every phase (after the warm-up).</summary>
    public Stats Overall(int series)
    {
        var phases = Phases;
        if (phases.Count == 0)
        {
            return Compute([]);
        }
        var from = phases[0].StartUtc.Ticks;
        lock (_samples[series])
        {
            return Compute(_samples[series].Where(s => s.UtcTicks >= from).ToArray());
        }
    }

    /// <summary>
    /// When the slow samples happened: per bucket of time since the first phase, the samples, the slow ones and the
    /// slowest. Buckets without a slow sample are left out.
    /// </summary>
    public IReadOnlyList<(TimeSpan At, int Count, int Slow, double MaxMs)> SlowTimeline(int series, TimeSpan bucket)
    {
        var phases = Phases;
        if (phases.Count == 0)
        {
            return [];
        }
        var origin = phases[0].StartUtc.Ticks;
        Sample[] samples;
        lock (_samples[series])
        {
            samples = _samples[series].Where(s => s.UtcTicks >= origin).ToArray();
        }
        return samples
            .GroupBy(s => (s.UtcTicks - origin) / bucket.Ticks)
            .OrderBy(g => g.Key)
            .Select(g => (TimeSpan.FromTicks(g.Key * bucket.Ticks), g.Count(), g.Count(s => s.LatencyMs > SlowMs), g.Max(s => s.LatencyMs)))
            .Where(b => b.Item3 > 0)
            .ToArray();
    }

    /// <summary>The statistics of a set of samples: the percentiles of the ones that succeeded, the failures, the slow ones.</summary>
    public static Stats Compute(IReadOnlyCollection<Sample> samples)
    {
        var ok = samples.Where(s => s.Ok).Select(s => s.LatencyMs).OrderBy(v => v).ToArray();
        var failed = samples.Count - ok.Length;
        if (ok.Length == 0)
        {
            return new Stats(samples.Count, failed, double.NaN, double.NaN, double.NaN, double.NaN, 0);
        }
        return new Stats(samples.Count, failed, Percentile(ok, 50), Percentile(ok, 95), Percentile(ok, 99), ok[^1], ok.Count(v => v > SlowMs));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
