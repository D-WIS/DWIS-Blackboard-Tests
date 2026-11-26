namespace DWIS.Blackboard.Perf;

/// <summary>The settings of a run: <c>appsettings.json</c> next to the exe, then the command line (<c>--endpoint</c>, <c>--writers</c>, … see <c>Program.cs</c>).</summary>
public sealed class PerfOptions
{
    /// <summary>The OPC UA endpoint of the real-time process.</summary>
    public string Endpoint { get; set; } = "opc.tcp://127.0.0.1:48030";

    /// <summary>Where the report goes (<c>report.json</c>, <c>report.txt</c> and NBomber's own files).</summary>
    public string Report { get; set; } = "TestResults/perf";

    public WriteOptions Write { get; set; } = new();
    public ReadOptions Read { get; set; } = new();
    public InterferenceOptions Interference { get; set; } = new();
    public ThresholdOptions Thresholds { get; set; } = new();
}

/// <summary>The load: writers on the old client, each a provider of its own, each at its own rate.</summary>
public sealed class WriteOptions
{
    public int Writers { get; set; } = 5;
    public int VariablesPerWriter { get; set; } = 3;
    public int WritesPerSecondPerWriter { get; set; } = 50;
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan Warmup { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>A reader on a plain session of the same stack: one read of every writer's variables per iteration.</summary>
public sealed class ReadOptions
{
    public int ReadsPerSecond { get; set; } = 10;
}

/// <summary>The semantic side loaded while the writes go on: registered queries, a burst of manifests, their removal.</summary>
public sealed class InterferenceOptions
{
    public bool Enabled { get; set; }

    /// <summary>Seconds after the warm-up to the first registered query (the baseline phase lasts that long).</summary>
    public int StartAtSeconds { get; set; } = 30;
    public int RegisteredQueries { get; set; } = 5;
    public int ManifestCount { get; set; } = 30;
    public int ManifestIntervalMs { get; set; } = 1000;

    /// <summary>
    /// The index of the first manifest (<c>perf-interference-{index:000}</c>). A provider the process has never seen
    /// makes it register two namespaces, which blocks the writes for about 100 ms (the finding of M5.5); a run with
    /// providers it has seen since its start measures the semantic side alone. Pick a fresh range to see the first.
    /// </summary>
    public int FirstManifest { get; set; }

    /// <summary>Remove the manifests after the burst, one after the other, as a phase of its own.</summary>
    public bool Remove { get; set; } = true;

    /// <summary>How long the settle phase lasts after the last manifest (or removal).</summary>
    public int SettleSeconds { get; set; } = 30;
}

/// <summary>R-RT-7: p99 under 10 ms and maximum under 50 ms for every writer, after the warm-up, whatever the phase.</summary>
public sealed class ThresholdOptions
{
    public double P99Ms { get; set; } = 10;
    public double MaxMs { get; set; } = 50;

    /// <summary>Exit with 1 when a threshold is missed; false reports only.</summary>
    public bool Fail { get; set; } = true;
}
