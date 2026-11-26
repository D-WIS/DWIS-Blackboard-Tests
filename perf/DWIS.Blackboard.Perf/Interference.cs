using DWIS.API.DTO;
using DWIS.Client.ReferenceImplementation;
using DWIS.Client.ReferenceImplementation.OPCFoundation;
using Opc.Ua.Client;

namespace DWIS.Blackboard.Perf;

/// <summary>
/// Loads the semantic side while the writers keep writing: registers a few SPARQL queries, injects a burst of distinct
/// manifests one every interval, the way the DWIS modules do at a cold start, then removes them one after the other.
/// Each manifest goes through the two-phase injection (the semantic service validates, stores and reasons; the process
/// adds the nodes) and makes the service answer every registered query again. The phases mark the ledger, so the
/// report shows what the writes cost in each. A plain background task next to the NBomber run, as in the old suite.
/// </summary>
public static class Interference
{
    public sealed record Result(int QueriesRegistered, int QueriesFailed, int ManifestsInjected, int ManifestsFailed, int ManifestsRemoved, int ManifestsNotRemoved,
        int QueryNotifications, DateTime BurstStartUtc, DateTime BurstEndUtc, string? Error);

    public static async Task<Result?> RunAsync(PerfOptions options, LatencyLedger ledger, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var settings = options.Interference;

        // The warm-up is not part of the baseline.
        await DelayUntil(start + options.Write.Warmup, ct);
        ledger.MarkPhase("baseline");
        if (!settings.Enabled)
        {
            return null;
        }
        await DelayUntil(start + options.Write.Warmup + TimeSpan.FromSeconds(settings.StartAtSeconds), ct);

        var notifications = 0;
        int queriesRegistered = 0, queriesFailed = 0, injected = 0, injectFailed = 0, removed = 0, notRemoved = 0;
        string? error = null;
        DateTime burstStart = DateTime.UtcNow, burstEnd = DateTime.UtcNow;
        var manifests = Enumerable.Range(settings.FirstManifest, settings.ManifestCount).Select(Manifest).ToList();

        // Its own client and session: the writers' sessions must not be slowed down by this client's own calls.
        using var client = new DWISClientOPCF(new DefaultDWISClientConfiguration { ServerAddress = options.Endpoint }, null);
        using var session = Sessions.Open(options.Endpoint, "perf-interference");
        try
        {
            Console.WriteLine($"[interference] registering {settings.RegisteredQueries} quer{(settings.RegisteredQueries == 1 ? "y" : "ies")}");
            for (var q = 0; q < settings.RegisteredQueries; q++)
            {
                try
                {
                    var (_, nodeId) = client.RegisterQuery(Query(q), _ => Interlocked.Increment(ref notifications));
                    if (string.IsNullOrEmpty(nodeId)) { queriesFailed++; } else { queriesRegistered++; }
                }
                catch (Exception e)
                {
                    queriesFailed++;
                    Console.WriteLine($"[interference] RegisterQuery {q} failed: {e.Message}");
                }
            }

            ledger.MarkPhase("burst");
            burstStart = DateTime.UtcNow;
            Console.WriteLine($"[interference] injecting {settings.ManifestCount} manifest(s), one every {settings.ManifestIntervalMs} ms");
            var interval = TimeSpan.FromMilliseconds(settings.ManifestIntervalMs);
            foreach (var manifest in manifests)
            {
                var next = DateTime.UtcNow + interval;
                try
                {
                    if (client.Inject(manifest)?.Success == true) { injected++; } else { injectFailed++; }
                }
                catch (Exception e)
                {
                    injectFailed++;
                    Console.WriteLine($"[interference] Inject {manifest.Provider.Name} failed: {e.Message}");
                }
                await DelayUntil(next, ct);
            }
            burstEnd = DateTime.UtcNow;

            if (settings.Remove)
            {
                ledger.MarkPhase("remove");
                Console.WriteLine($"[interference] removing the {settings.ManifestCount} manifest(s), one every {settings.ManifestIntervalMs} ms");
                foreach (var manifest in manifests)
                {
                    var next = DateTime.UtcNow + interval;
                    if (Sessions.Delete(session, manifest)) { removed++; } else { notRemoved++; }
                    await DelayUntil(next, ct);
                }
            }

            ledger.MarkPhase("settle");
            Console.WriteLine($"[interference] done ({injected} injected, {injectFailed} failed, {removed} removed); settling {settings.SettleSeconds} s");
            await Task.Delay(TimeSpan.FromSeconds(settings.SettleSeconds), ct);
            ledger.MarkPhase("after");
            Console.WriteLine($"[interference] settled; {Volatile.Read(ref notifications)} query notification(s) so far");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e.Message;
            Console.WriteLine($"[interference] failed: {e.Message}");
        }
        finally
        {
            if (!settings.Remove)
            {
                foreach (var manifest in manifests)
                {
                    Sessions.Delete(session, manifest);
                }
            }
        }
        return new Result(queriesRegistered, queriesFailed, injected, injectFailed, removed, notRemoved, Volatile.Read(ref notifications), burstStart, burstEnd, error);
    }

    private const string DDHub = "http://ddhub.no/";

    /// <summary>
    /// A small manifest shaped like the ones the DWIS modules send at a cold start (the old suite's interference
    /// manifests): one provider, three provided variables, six nodes, and references of the three kinds the semantic
    /// service reasons about (node to node, class assertion, a class as the object). Each one brings new facts.
    /// </summary>
    public static ManifestFile Manifest(int index)
    {
        var provider = $"perf-interference-{index:000}";
        var info = new InjectionInformation { InjectedNodesNamespaceAlias = "Nodes", InjectedVariablesNamespaceAlias = "Variables", ProvidedVariablesNamespaceAlias = "ProvidedVariables" };
        var manifest = new ManifestFile
        {
            ManifestName = $"{provider} manifest",
            Provider = new InjectionProvider { Name = provider, Company = "D-WIS" },
            InjectionInformation = info,
            ProvidedVariables = [],
            InjectedNodes = [],
            InjectedReferences = [],
        };
        for (var s = 0; s < 3; s++)
        {
            var signal = $"Signal{s}";
            var dataPoint = $"DataPoint{s}";
            manifest.ProvidedVariables.Add(new ProvidedVariable { DataType = "double", Rank = 0, VariableID = signal });
            manifest.InjectedNodes.Add(Node(signal, "DrillingSignal"));
            manifest.InjectedNodes.Add(Node(dataPoint, "DrillingDataPoint"));
            manifest.InjectedReferences.Add(Reference(info.InjectedNodesNamespaceAlias, signal, "HasDynamicValue", info.ProvidedVariablesNamespaceAlias, signal));
            manifest.InjectedReferences.Add(Reference(info.InjectedNodesNamespaceAlias, dataPoint, "HasValue", info.InjectedNodesNamespaceAlias, signal));
            manifest.InjectedReferences.Add(Reference(info.InjectedNodesNamespaceAlias, dataPoint, "BelongsToClass", DDHub, DDHub + "Measurement"));
            manifest.InjectedReferences.Add(Reference(info.InjectedNodesNamespaceAlias, dataPoint, "IsMechanicallyLocatedAt", DDHub, DDHub + "BottomOfStringReferenceLocation"));
        }
        return manifest;
    }

    /// <summary>Distinct registered queries that touch what the manifests add (and what is inferred about it).</summary>
    public static string Query(int index)
    {
        var v = "?x" + index;
        return (index % 4) switch
        {
            0 => $"PREFIX ddhub: <{DDHub}> SELECT {v} ?v WHERE {{ {v} ddhub:HasDynamicValue ?v . }}",
            1 => $"PREFIX ddhub: <{DDHub}> SELECT {v} WHERE {{ {v} ddhub:HasValue ?v . }}",
            2 => $"PREFIX ddhub: <{DDHub}> PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> SELECT {v} WHERE {{ {v} rdf:type ddhub:DrillingDataPoint . }}",
            _ => $"PREFIX ddhub: <{DDHub}> PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> SELECT {v} WHERE {{ {v} rdf:type ddhub:Measurement . }}",
        };
    }

    private static InjectedNode Node(string name, string type) => new() { BrowseName = name, DisplayName = name, UniqueName = name, TypeDictionaryURI = type };

    private static InjectedReference Reference(string subjectNs, string subjectId, string verb, string objectNs, string objectId) =>
        new() { Subject = new NodeIdentifier { NameSpace = subjectNs, ID = subjectId }, VerbURI = DDHub + verb, Object = new NodeIdentifier { NameSpace = objectNs, ID = objectId } };

    private static async Task DelayUntil(DateTime utc, CancellationToken ct)
    {
        var delay = utc - DateTime.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct);
        }
    }
}
