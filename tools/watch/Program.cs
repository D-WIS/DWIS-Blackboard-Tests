// The watch console (M3.5, Tests #16): a subscriber of the semantic service over gRPC, on the published
// contract package. It registers one or more queries for an owner, prints the snapshot and then every diff
// as it arrives, and when the stream ends (the service restarted, the connection dropped) it registers again
// and gets a full snapshot (the client side of T-QR-4). The smoke demo runs two of these.
//
//   dotnet run --project tools/watch -- --address http://127.0.0.1:8081 --owner demo-1 [--query "SELECT …"]...
//                                     [--until-diffs N] [--timeout SECONDS] [--quiet]
//
// Without --query the two queries of roadmap F.3 are registered. --until-diffs N exits with 0 once N diffs
// were printed (1 at the timeout, 120 s by default); without it the console runs until Ctrl+C.
// Output, one line per event, so that a script can read it:
//   snapshot q<id> <rows> rows            then one "  = value | value" line per row
//   diff q<id> +<added> -<removed> at <time>   then "  + value" and "  - value" lines
//   stream ended: <reason>; registering again in 1 s
using System.Diagnostics;
using System.Globalization;
using DWIS.Blackboard.Contracts.Grpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

var address = "http://127.0.0.1:8081";
var owner = "watch";
var queries = new List<string>();
var untilDiffs = int.MaxValue;
var timeout = TimeSpan.FromSeconds(120);
var quiet = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--address": address = args[++i]; break;
        case "--owner": owner = args[++i]; break;
        case "--query": queries.Add(args[++i]); break;
        case "--until-diffs": untilDiffs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--timeout": timeout = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture)); break;
        case "--quiet": quiet = true; break;
        case "--help" or "-h":
            Console.WriteLine("dotnet run --project tools/watch -- [--address http://host:8081] [--owner name] [--query \"SELECT …\"]... [--until-diffs N] [--timeout seconds] [--quiet]");
            return 0;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

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

using var stop = new CancellationTokenSource(timeout);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var channel = GrpcChannel.ForAddress(address);
var client = new Semantic.SemanticClient(channel);
var diffs = 0;
var watch = Stopwatch.StartNew();
Console.WriteLine($"watch {owner}: {address}, {queries.Count} queries, until {(untilDiffs == int.MaxValue ? "stopped" : untilDiffs + " diffs")}, timeout {timeout.TotalSeconds:0} s");

while (!stop.IsCancellationRequested && diffs < untilDiffs)
{
    try
    {
        // Ready? The service answers once it has read the vocabulary from the store.
        var info = await client.GetVocabularyAsync(new Empty(), cancellationToken: stop.Token);
        if (!info.Loaded)
        {
            Console.WriteLine("the service has no vocabulary yet; waiting");
            await Task.Delay(1000, stop.Token);
            continue;
        }

        // Register: a full snapshot per query (the stored answer, fresh when the query is new).
        foreach (var query in queries)
        {
            var snapshot = await client.RegisterQueryAsync(new RegisterRequest { Query = query, Owner = owner }, cancellationToken: stop.Token);
            Console.WriteLine($"snapshot q{snapshot.QueryId} {snapshot.Rows.Count} rows ({string.Join(", ", snapshot.Variables)})");
            if (!quiet)
            {
                foreach (var row in snapshot.Rows)
                {
                    Console.WriteLine("  = " + string.Join(" | ", row.Values));
                }
            }
        }

        // Watch: every diff of every registered query, until the stream ends.
        using var stream = client.WatchQueries(new WatchRequest { Watcher = owner }, cancellationToken: stop.Token);
        await foreach (var diff in stream.ResponseStream.ReadAllAsync(stop.Token))
        {
            diffs++;
            Console.WriteLine($"diff q{diff.QueryId} +{diff.Added.Count} -{diff.Removed.Count} at {diff.At.ToDateTimeOffset():HH:mm:ss.fff} ({watch.Elapsed.TotalSeconds:0.0} s)");
            if (!quiet)
            {
                foreach (var row in diff.Removed)
                {
                    Console.WriteLine("  - " + string.Join(" | ", row.Values));
                }

                foreach (var row in diff.Added)
                {
                    Console.WriteLine("  + " + string.Join(" | ", row.Values));
                }
            }

            if (diffs >= untilDiffs)
            {
                break;
            }
        }

        if (diffs < untilDiffs && !stop.IsCancellationRequested)
        {
            Console.WriteLine("stream ended: the service closed it; registering again in 1 s");
            await Task.Delay(1000, stop.Token);
        }
    }
    catch (RpcException) when (stop.IsCancellationRequested)
    {
        // The timeout or Ctrl+C: the open call is cancelled on the way out.
    }
    catch (RpcException e)
    {
        // Unavailable while the service restarts, Cancelled when the connection drops, ResourceExhausted
        // when this subscriber fell behind: in every case, register again for a full snapshot.
        Console.WriteLine($"stream ended: {e.StatusCode} {e.Status.Detail}; registering again in 1 s");
        try
        {
            await Task.Delay(1000, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }
    catch (OperationCanceledException)
    {
    }
}

if (untilDiffs != int.MaxValue && diffs < untilDiffs)
{
    Console.WriteLine($"timeout after {watch.Elapsed.TotalSeconds:0} s with {diffs} of {untilDiffs} diffs");
    return 1;
}

Console.WriteLine($"done: {diffs} diffs in {watch.Elapsed.TotalSeconds:0.0} s");
return 0;
