// The consumer console (M4.4, Tests #22): a DWIS application that talks to the blackboard over OPC UA only,
// with the unchanged C# client 2.2.x from nuget.org, the one every application uses against the old server
// (R-BC-1, T-EP-1). Four modes, one per run:
//
//   dotnet run --project tools/consumer -- inject  --manifest FILE [--address opc.tcp://host:48030]
//   dotnet run --project tools/consumer -- write   --manifest FILE [--seconds N] [--period MILLISECONDS]
//   dotnet run --project tools/consumer -- consume [--query "SELECT …"]... [--until-values N] [--timeout SECONDS]
//   dotnet run --project tools/consumer -- delete  --manifest FILE
//
// inject calls DDHubServer.Inject with the manifest file and prints the answer. write injects, then writes a
// value to every provided variable of the manifest every period (half a second by default) for N seconds
// (60 by default). consume resolves the queries (the two of roadmap F.3 without --query) through ResolveQuery,
// registers the first one through RegisterQuery, subscribes to the signals it returned and prints every value
// it sees; it exits with 0 once N values were seen (5 by default), 1 at the timeout (60 s). delete calls Delete.
// Output, one line per event, so that a script can read it:
//   injected <manifest> by <provider>: success <bool>, <n> provided variables in <namespace>
//   wrote <n> variables, round <k>
//   query q<i>: <rows> rows, columns <names>         then one "  = namespace | id" line per row
//   registered q1: diff variable <node id>, <n> rows  then "diff +<added> -<removed>" on every change
//   value <id> = <value> at <source time>
//   done: <n> values seen   or   timeout: <n> values seen
using System.Globalization;
using DWIS.API.DTO;
using DWIS.Client.ReferenceImplementation;
using DWIS.Client.ReferenceImplementation.OPCFoundation;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("dotnet run --project tools/consumer -- inject|write|consume|delete [--address opc.tcp://host:48030] [--manifest FILE] [--seconds N] [--period MS] [--query \"SELECT …\"]... [--until-values N] [--timeout S]");
    return 0;
}

var mode = args[0];
var address = "opc.tcp://127.0.0.1:48030";
string? manifestPath = null;
var seconds = 60.0;
var period = TimeSpan.FromMilliseconds(500);
var queries = new List<string>();
var untilValues = 5;
var timeout = TimeSpan.FromSeconds(60);
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--address": address = args[++i]; break;
        case "--manifest": manifestPath = args[++i]; break;
        case "--seconds": seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--period": period = TimeSpan.FromMilliseconds(double.Parse(args[++i], CultureInfo.InvariantCulture)); break;
        case "--query": queries.Add(args[++i]); break;
        case "--until-values": untilValues = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--timeout": timeout = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture)); break;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

if (mode is not ("inject" or "write" or "consume" or "delete"))
{
    Console.Error.WriteLine($"unknown mode {mode}: inject, write, consume or delete");
    return 2;
}
if (mode is not "consume" && manifestPath is null)
{
    Console.Error.WriteLine($"{mode} needs --manifest FILE");
    return 2;
}

// The client as the applications build it: the default configuration with the server address, no logger.
// The client connects in its constructor and finds DDHubServer at namespace index 2, as it always did.
var configuration = new DefaultDWISClientConfiguration { ServerAddress = address };
IOPCUADWISClient client = new DWISClientOPCF(configuration, null);
if (!client.Connected)
{
    Console.Error.WriteLine($"not connected to {address}");
    return 1;
}

switch (mode)
{
    case "inject":
        return Inject(client, manifestPath!) is not null ? 0 : 1;
    case "delete":
    {
        var manifest = ManifestFile.FromJsonString(File.ReadAllText(manifestPath!));
        var removed = client.Delete(manifest);
        Console.WriteLine($"deleted {manifest.ManifestName} by {manifest.Provider.Name}: {removed}");
        return removed ? 0 : 1;
    }
    case "write":
        return Write(client, manifestPath!, seconds, period);
    default:
        return Consume(client, queries, untilValues, timeout);
}

static ManifestFile? Inject(IOPCUADWISClient client, string path)
{
    var manifest = ManifestFile.FromJsonString(File.ReadAllText(path));
    var result = client.Inject(manifest);
    var mapped = result?.ProvidedVariables?.Count() ?? 0;
    Console.WriteLine($"injected {manifest.ManifestName} by {manifest.Provider.Name}: success {result?.Success ?? false}, {mapped} provided variables in {result?.ProvidedVariablesNamespace}");
    return result?.Success == true ? manifest : null;
}

static int Write(IOPCUADWISClient client, string path, double seconds, TimeSpan period)
{
    var manifest = Inject(client, path);
    if (manifest is null)
    {
        return 1;
    }
    var scalars = manifest.ProvidedVariables.Where(v => v.Rank == 0).ToList();
    var stop = DateTime.UtcNow.AddSeconds(seconds);
    var round = 0;
    while (DateTime.UtcNow < stop)
    {
        round++;
        var now = DateTime.UtcNow;
        var values = scalars.Select(v => (v.VariableID, ValueFor(v.DataType, round), now)).ToList();
        if (!client.UpdateProvidedVariables(values))
        {
            Console.Error.WriteLine($"write failed at round {round}");
            return 1;
        }
        if (round == 1 || round % 20 == 0)
        {
            Console.WriteLine($"wrote {values.Count} variables, round {round}");
        }
        Thread.Sleep(period);
    }
    Console.WriteLine($"done: {round} rounds written");
    return 0;
}

// A value of the manifest's data type that a reader can tell apart from the initial value: the round number.
static object ValueFor(string? dataType, int round) => (dataType ?? "double").ToUpperInvariant() switch
{
    "FLOAT" => (float)round,
    "INT" => round,
    "LONG" => (long)round,
    "SHORT" => (short)round,
    "BOOL" => round % 2 == 0,
    "STRING" => "round " + round.ToString(CultureInfo.InvariantCulture),
    _ => (double)round,
};

static int Consume(IOPCUADWISClient client, List<string> queries, int untilValues, TimeSpan timeout)
{
    if (queries.Count == 0)
    {
        queries.Add("""
            PREFIX dwis: <http://d-wis.org/>
            SELECT ?signal WHERE { ?dp a dwis:SPP ; dwis:HasDynamicValue ?signal . }
            """);
        queries.Add("""
            PREFIX ddhub: <http://ddhub.no/>
            SELECT ?signal WHERE { ?dp ddhub:IsOfMeasurableQuantity ?q ; ddhub:HasDynamicValue ?signal .
                                   ?q a ddhub:PressureDrillingQuantity . ?dp ddhub:IsLocatedAtEquipment ?e . ?e a ddhub:MudStandpipe . }
            """);
    }

    // 1. Resolve every query through ResolveQuery and print the rows as the old client gives them.
    var first = new List<NodeIdentifier>();
    for (var i = 0; i < queries.Count; i++)
    {
        var result = client.GetQueryResult(queries[i]);
        if (result is null)
        {
            Console.Error.WriteLine($"query q{i + 1} got no answer");
            return 1;
        }
        Console.WriteLine($"query q{i + 1}: {result.Results.Count} rows, columns {string.Join(" ", result.VariablesHeader)}");
        var column = Math.Max(0, result.VariablesHeader.IndexOf("signal"));
        foreach (var row in result.Results)
        {
            Console.WriteLine($"  = {string.Join(" | ", row.Items.Select(item => item.NameSpace + " | " + item.ID))}");
            if (i == 0 && row.Items.Count > column)
            {
                first.Add(row.Items[column]);
            }
        }
    }
    if (first.Count == 0)
    {
        Console.Error.WriteLine("the first query returned no signal to subscribe to");
        return 1;
    }

    // 2. Register the first query: the answer is the current rows as a diff and the diff variable to watch.
    var (diffJson, _) = client.RegisterQuery(queries[0], diff => Console.WriteLine($"diff +{diff.Added.Count} -{diff.Removed.Count}"));
    var registration = string.IsNullOrEmpty(diffJson) ? null : QueryResultsDiff.FromJsonString(diffJson);
    if (registration is null)
    {
        Console.Error.WriteLine("RegisterQuery gave no registration");
        return 1;
    }
    Console.WriteLine($"registered q1: diff variable {registration.QueryResultID}, {registration.Added.Count} rows");

    // 3. Subscribe to the signals of the first query and print what arrives (the initial values first).
    var seen = 0;
    var enough = new ManualResetEventSlim(false);
    client.Subscribe("signals", (_, changes) =>
    {
        foreach (var change in changes)
        {
            Console.WriteLine($"value {change.UserData} = {FormatValue(change.Value)} at {change.SourceDateTime.ToUniversalTime():O}");
        }
        if (Interlocked.Add(ref seen, changes.Length) >= untilValues)
        {
            enough.Set();
        }
    }, first.Select(item => (item.NameSpace, item.ID, (object)item.ID)).ToArray());

    if (enough.Wait(timeout))
    {
        Console.WriteLine($"done: {seen} values seen");
        return 0;
    }
    Console.WriteLine($"timeout: {seen} values seen");
    return 1;
}

static string FormatValue(object? value) => value switch
{
    null => "null",
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
    _ => value.ToString() ?? "",
};
