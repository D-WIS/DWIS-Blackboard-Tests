using System.Globalization;
using System.Text;
using System.Text.Json;
using NBomber.Contracts.Stats;

namespace DWIS.Blackboard.Perf;

/// <summary>The report of a run: text on the console and in <c>report.txt</c>, the same as <c>report.json</c>; the thresholds' verdict.</summary>
public static class Report
{
    public sealed record SeriesPhase(string Phase, int Count, int Failed, double P50, double P95, double P99, double Max, int Slow);
    public sealed record Series(string Name, int Reconnects, SeriesPhase Overall, IReadOnlyList<SeriesPhase> Phases, IReadOnlyList<string> SlowTimeline);
    public sealed record Verdict(bool Passed, double P99Ms, double MaxMs, double WorstP99, double WorstMax, IReadOnlyList<string> Missed);
    public sealed record Document(DateTime RunUtc, string Endpoint, PerfOptions Options, string Nbomber, IReadOnlyList<Series> Writers, Series Reads, Interference.Result? Interference, Verdict Verdict);

    public static Document Build(PerfOptions options, DateTime runUtc, NodeStats stats, LatencyLedger ledger, IReadOnlyList<Writer> writers, Interference.Result? interference)
    {
        var writerSeries = writers.Select((w, i) => SeriesOf(ledger, i, w.Provider, w.Reconnects)).ToList();
        var reads = SeriesOf(ledger, writers.Count, "reads", 0);
        var missed = new List<string>();
        var worstP99 = 0.0;
        var worstMax = 0.0;
        foreach (var series in writerSeries)
        {
            if (series.Overall.Count == 0)
            {
                missed.Add($"{series.Name}: no write after the warm-up");
                continue;
            }
            if (series.Overall.Failed > 0)
            {
                missed.Add($"{series.Name}: {series.Overall.Failed} write(s) failed");
            }
            worstP99 = Math.Max(worstP99, series.Overall.P99);
            worstMax = Math.Max(worstMax, series.Overall.Max);
            if (series.Overall.P99 >= options.Thresholds.P99Ms)
            {
                missed.Add($"{series.Name}: p99 {Ms(series.Overall.P99)} ms, not under {Ms(options.Thresholds.P99Ms)}");
            }
            if (series.Overall.Max >= options.Thresholds.MaxMs)
            {
                missed.Add($"{series.Name}: max {Ms(series.Overall.Max)} ms, not under {Ms(options.Thresholds.MaxMs)}");
            }
        }
        if (interference is { Error: not null } failed)
        {
            missed.Add("interference: " + failed.Error);
        }
        var verdict = new Verdict(missed.Count == 0, options.Thresholds.P99Ms, options.Thresholds.MaxMs, worstP99, worstMax, missed);
        var nbomber = $"{stats.AllOkCount} ok / {stats.AllFailCount} failed across {stats.ScenarioStats.Length} scenario(s), {stats.Duration}";
        return new Document(runUtc, options.Endpoint, options, nbomber, writerSeries, reads, interference, verdict);
    }

    private static Series SeriesOf(LatencyLedger ledger, int index, string name, int reconnects)
    {
        var phases = ledger.Report(index).Select(p => PhaseOf(p.Phase.Name, p.Stats)).ToList();
        var overall = PhaseOf("overall", ledger.Overall(index));
        var timeline = ledger.SlowTimeline(index, TimeSpan.FromSeconds(10)).Select(b => $"{(int)b.At.TotalSeconds}s: {b.Slow}/{b.Count}, {Ms(b.MaxMs)}").ToList();
        return new Series(name, reconnects, overall, phases, timeline);
    }

    private static SeriesPhase PhaseOf(string name, LatencyLedger.Stats s) => new(name, s.Count, s.Failed, s.P50, s.P95, s.P99, s.Max, s.Slow);

    public static string Text(Document d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DWIS Blackboard Rig Edition, performance suite (R-RT-7)");
        sb.AppendLine($"  run {d.RunUtc:u}, endpoint {d.Endpoint}");
        sb.AppendLine($"  {d.Options.Write.Writers} writer(s) x {d.Options.Write.WritesPerSecondPerWriter} writes/s x {d.Options.Write.VariablesPerWriter} variable(s), {d.Options.Write.Duration} after a warm-up of {d.Options.Write.Warmup}; reads {d.Options.Read.ReadsPerSecond}/s");
        sb.AppendLine(d.Options.Interference.Enabled
            ? $"  interference: {d.Options.Interference.RegisteredQueries} registered queries, {d.Options.Interference.ManifestCount} manifests from perf-interference-{d.Options.Interference.FirstManifest:000} every {d.Options.Interference.ManifestIntervalMs} ms{(d.Options.Interference.Remove ? ", then removed" : "")}, settle {d.Options.Interference.SettleSeconds} s"
            : "  interference: off (--interference true loads the semantic side during the run)");
        sb.AppendLine($"  NBomber: {d.Nbomber}");
        sb.AppendLine();
        sb.AppendLine("Latency in ms, per phase (baseline = after the warm-up, nothing else going on; burst = manifests being injected;");
        sb.AppendLine($"remove = being removed; settle = after the last one; after = quiet again). slow = samples over {LatencyLedger.SlowMs} ms,");
        sb.AppendLine("a pause seen from the client; the process's own garbage collector cannot be read from outside its container.");
        foreach (var series in d.Writers.Append(d.Reads))
        {
            sb.AppendLine();
            sb.AppendLine($"  {series.Name}{(series.Reconnects > 0 ? $" ({series.Reconnects} write(s) retried after a reconnect, about a second each)" : "")}");
            sb.AppendLine($"    {"phase",-9} {"count",7} {"failed",7} {"p50",8} {"p95",8} {"p99",8} {"max",9} {"slow",6}");
            foreach (var p in series.Phases.Append(series.Overall))
            {
                sb.AppendLine($"    {p.Phase,-9} {p.Count,7} {p.Failed,7} {Ms(p.P50),8} {Ms(p.P95),8} {Ms(p.P99),8} {Ms(p.Max),9} {p.Slow,6}");
            }
            if (series.SlowTimeline.Count > 0)
            {
                sb.AppendLine($"    slow samples per 10 s since the baseline started (seconds: slow/count, slowest ms): {string.Join("  ", series.SlowTimeline)}");
            }
        }
        if (d.Interference is { } i)
        {
            sb.AppendLine();
            sb.AppendLine($"Interference: {i.QueriesRegistered} quer{(i.QueriesRegistered == 1 ? "y" : "ies")} registered ({i.QueriesFailed} failed), {i.ManifestsInjected} manifest(s) injected ({i.ManifestsFailed} failed) between {i.BurstStartUtc:HH:mm:ss} and {i.BurstEndUtc:HH:mm:ss} UTC, {i.ManifestsRemoved} removed ({i.ManifestsNotRemoved} not), {i.QueryNotifications} query notification(s).");
            if (i.Error is not null)
            {
                sb.AppendLine($"  error: {i.Error}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"Thresholds (R-RT-7, every writer, after the warm-up): p99 under {Ms(d.Verdict.P99Ms)} ms, max under {Ms(d.Verdict.MaxMs)} ms; worst p99 {Ms(d.Verdict.WorstP99)}, worst max {Ms(d.Verdict.WorstMax)}.");
        sb.AppendLine(d.Verdict.Passed ? "PASSED" : "MISSED: " + string.Join("; ", d.Verdict.Missed));
        return sb.ToString();
    }

    public static void Write(Document d, string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "report.txt"), Text(d));
        File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Ms(double value) => double.IsNaN(value) ? "-" : value.ToString("0.0", CultureInfo.InvariantCulture);
}
