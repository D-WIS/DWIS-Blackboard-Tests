using System.Diagnostics;
using DWIS.Blackboard.Perf;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using Opc.Ua;

var switches = new Dictionary<string, string>
{
    ["--endpoint"] = "Endpoint",
    ["--report"] = "Report",
    ["--writers"] = "Write:Writers",
    ["--variables"] = "Write:VariablesPerWriter",
    ["--frequency"] = "Write:WritesPerSecondPerWriter",
    ["--duration"] = "Write:Duration",
    ["--warmup"] = "Write:Warmup",
    ["--reads"] = "Read:ReadsPerSecond",
    ["--interference"] = "Interference:Enabled",
    ["--interference-start"] = "Interference:StartAtSeconds",
    ["--interference-queries"] = "Interference:RegisteredQueries",
    ["--interference-manifests"] = "Interference:ManifestCount",
    ["--interference-interval-ms"] = "Interference:ManifestIntervalMs",
    ["--interference-first"] = "Interference:FirstManifest",
    ["--interference-remove"] = "Interference:Remove",
    ["--interference-settle"] = "Interference:SettleSeconds",
    ["--p99-ms"] = "Thresholds:P99Ms",
    ["--max-ms"] = "Thresholds:MaxMs",
    ["--fail"] = "Thresholds:Fail",
};

// Every write is a blocking OPC UA call made from an async NBomber scenario, on a pool thread. When writes slow down for
// a moment, blocked threads pile up and the pool adds new ones at about one per second: writes then wait about a second
// each for a thread in this process, which looks like a server stall but is not one. A high minimum keeps the
// measurement about the server (the old suite's finding).
ThreadPool.SetMinThreads(512, 512);

var options = new PerfOptions();
new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).AddCommandLine(args, switches).Build().Bind(options);
var runUtc = DateTime.UtcNow;
Console.WriteLine("DWIS Blackboard Rig Edition, performance suite (R-RT-7)");
Console.WriteLine($"  endpoint      {options.Endpoint}");
Console.WriteLine($"  writers       {options.Write.Writers} x {options.Write.WritesPerSecondPerWriter} writes/s x {options.Write.VariablesPerWriter} variable(s)");
Console.WriteLine($"  reads         {options.Read.ReadsPerSecond}/s of every writer's variables, on a plain session");
Console.WriteLine($"  duration      {options.Write.Duration} after a warm-up of {options.Write.Warmup}");
Console.WriteLine(options.Interference.Enabled
    ? $"  interference  at {options.Interference.StartAtSeconds} s: {options.Interference.RegisteredQueries} registered queries, {options.Interference.ManifestCount} manifests from perf-interference-{options.Interference.FirstManifest:000} every {options.Interference.ManifestIntervalMs} ms{(options.Interference.Remove ? ", then removed" : "")}, settle {options.Interference.SettleSeconds} s"
    : "  interference  off (--interference true loads the semantic side during the run)");
Console.WriteLine($"  thresholds    p99 under {options.Thresholds.P99Ms} ms, max under {options.Thresholds.MaxMs} ms{(options.Thresholds.Fail ? "" : " (report only)")}");
Console.WriteLine($"  report        {Path.GetFullPath(options.Report)}");
if (options.Interference.Enabled)
{
    var busy = TimeSpan.FromSeconds(options.Interference.StartAtSeconds + options.Interference.ManifestCount * options.Interference.ManifestIntervalMs / 1000.0 * (options.Interference.Remove ? 2 : 1) + options.Interference.SettleSeconds);
    if (busy >= options.Write.Duration)
    {
        Console.WriteLine($"  warning: the interference ends at about {busy.TotalSeconds:0} s, after the test ({options.Write.Duration.TotalSeconds:0} s): lengthen --duration for an 'after' phase");
    }
}
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// The writers, one provider each. A provider a run before left live keeps its nodes on an inject (the hub may have
// brought it back too): the manifest is removed first, so that every run starts the same.
var writers = new List<Writer>();
using var control = Sessions.Open(options.Endpoint, "perf-control");
for (var i = 0; i < options.Write.Writers; i++)
{
    var writer = new Writer(options.Endpoint, $"perf-writer-{i}", options.Write.VariablesPerWriter);
    Sessions.Delete(control, writer.Manifest);
    var injected = false;
    for (var attempt = 1; attempt <= 3 && !injected; attempt++)
    {
        injected = writer.Inject();
        if (!injected)
        {
            await Task.Delay(2000, cts.Token);
        }
    }
    if (!injected)
    {
        Console.WriteLine($"{writer.Provider}: the manifest was not injected; nothing to measure.");
        return 2;
    }
    writers.Add(writer);
}
foreach (var manifest in Enumerable.Range(options.Interference.FirstManifest, options.Interference.ManifestCount).Select(Interference.Manifest))
{
    Sessions.Delete(control, manifest);
}
Console.WriteLine($"[setup] {writers.Count} writer(s) connected, their manifests injected");

using var readerSession = Sessions.Open(options.Endpoint, "perf-reader");
readerSession.FetchNamespaceTables();
var nodes = new ReadValueIdCollection(writers.SelectMany(w => w.VariableIds.Select(id => new ReadValueId { NodeId = Sessions.Variable(readerSession, w.Provider, id), AttributeId = Attributes.Value })));
var reader = new Reader(readerSession, nodes);
if (!reader.ReadOnce())
{
    Console.WriteLine("[setup] the reader did not read every variable; nothing to measure.");
    return 2;
}
Console.WriteLine($"[setup] the reader reads {reader.Count} variable(s) in one call");

var ledger = new LatencyLedger(writers.Count + 1);
var scenarios = new List<ScenarioProps>();
for (var i = 0; i < writers.Count; i++)
{
    var index = i;
    scenarios.Add(Scenario.Create($"write_{writers[i].Provider}", async _ =>
        {
            await Task.CompletedTask;
            var startedUtc = DateTime.UtcNow;
            var started = Stopwatch.GetTimestamp();
            try
            {
                var ok = writers[index].WriteOnce();
                ledger.Record(index, startedUtc, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ok);
                return ok ? Response.Ok() : Response.Fail(statusCode: "WriteFalse", message: "the client returned false", sizeBytes: 0, customLatencyMs: 0);
            }
            catch (Exception e)
            {
                ledger.Record(index, startedUtc, Stopwatch.GetElapsedTime(started).TotalMilliseconds, false);
                return Response.Fail(statusCode: "WriteError", message: e.Message, sizeBytes: 0, customLatencyMs: 0);
            }
        })
        .WithWarmUpDuration(options.Write.Warmup)
        .WithLoadSimulations(Simulation.Inject(rate: options.Write.WritesPerSecondPerWriter, interval: TimeSpan.FromSeconds(1), during: options.Write.Duration)));
}
var readIndex = writers.Count;
scenarios.Add(Scenario.Create("read", async _ =>
    {
        await Task.CompletedTask;
        var startedUtc = DateTime.UtcNow;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var ok = reader.ReadOnce();
            ledger.Record(readIndex, startedUtc, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ok);
            return ok ? Response.Ok() : Response.Fail(statusCode: "ReadBad", message: "a value came back bad", sizeBytes: 0, customLatencyMs: 0);
        }
        catch (Exception e)
        {
            ledger.Record(readIndex, startedUtc, Stopwatch.GetElapsedTime(started).TotalMilliseconds, false);
            return Response.Fail(statusCode: "ReadError", message: e.Message, sizeBytes: 0, customLatencyMs: 0);
        }
    })
    .WithWarmUpDuration(options.Write.Warmup)
    .WithLoadSimulations(Simulation.Inject(rate: options.Read.ReadsPerSecond, interval: TimeSpan.FromSeconds(1), during: options.Write.Duration)));

Console.WriteLine("[run] starting the load");
var interferenceTask = Interference.RunAsync(options, ledger, cts.Token);
var reportFolder = Path.GetFullPath(options.Report);
var stats = await Task.Run(() => NBomberRunner.RegisterScenarios([.. scenarios]).WithReportFolder(Path.Combine(reportFolder, "nbomber")).WithReportFormats(ReportFormat.Html, ReportFormat.Txt).Run(), cts.Token);
var interference = await interferenceTask;

foreach (var writer in writers)
{
    Sessions.Delete(control, writer.Manifest);
}

var document = Report.Build(options, runUtc, stats, ledger, writers, interference);
Report.Write(document, reportFolder);
Console.WriteLine();
Console.WriteLine(Report.Text(document));
Console.WriteLine($"report in {reportFolder}");
return document.Verdict.Passed || !options.Thresholds.Fail ? 0 : 1;
