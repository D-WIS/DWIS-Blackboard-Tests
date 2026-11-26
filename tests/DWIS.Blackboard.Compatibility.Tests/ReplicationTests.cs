using System.Diagnostics;
using System.Text.RegularExpressions;
using Opc.Ua;

namespace DWIS.Blackboard.Compatibility.Tests;

/// <summary>
/// The replication rows of the test plan (G.2; M5.3, Tests #33): two Blackboards joined to the same group of the old
/// DWIS hub (the <c>hub</c> profile of the compose file, <c>compose/hub.sh</c>). T-HUB-1: a value written on one is
/// read on the other within a second, in both directions (R-HUB-1, R-HUB-2: the old hub image and its wire format,
/// unchanged). T-HUB-2: a manifest injected on one arrives on the other through the hub, goes through the injection
/// pipeline there and is reported with its origin in the provenance graph (R-HUB-3). Each test injects a provider of
/// its own on the first Blackboard and removes it from both at the end. In the stack collection, so that the restart
/// test never runs at the same time.
/// </summary>
[Collection("stack")]
public sealed class ReplicationTests(StackFixture stack)
{
    [Fact]
    public void T_HUB_1_A_value_written_on_one_Blackboard_is_read_on_the_other_within_a_second()
    {
        Assert.SkipWhen(StackFixture.NoHub, StackFixture.NoHubReason);
        const string provider = "HubValues";
        var manifest = StackFixture.Manifest(provider);
        var ns = $"http://ddhub.no/{provider}/Variables/";
        var a = stack.Client;
        using var sessionB = StackFixture.OpenSession(StackFixture.EndpointB!);
        Forget(manifest, sessionB);
        a.Inject(manifest).Success.Should().BeTrue();
        try
        {
            // The manifest goes through the hub first: the provider's variables appear on the second Blackboard and
            // its semantic service lists the manifest once the injection there is done (the SignalR client of the
            // process handles one message at a time, so a value sent while a peer's manifest is still being
            // committed waits for that commit; the measurement below starts after it).
            StackFixture.Eventually(() => TryRead(sessionB, ns, $"{provider}.SPP", out _), TimeSpan.FromSeconds(30))
                .Should().BeTrue("the manifest injected on the first Blackboard should reach the second through the hub");
            StackFixture.Eventually(() => IsLive(StackFixture.SemanticApiB!, provider), TimeSpan.FromSeconds(30))
                .Should().BeTrue("the second semantic service should list the manifest once its injection is done");

            // First to second: the provider writes through the old client, as the applications do; the second
            // Blackboard is read with plain OPC UA reads every 20 ms (the old client's read path browses first and
            // takes a second of its own), and the clock starts once the write has returned.
            a.UpdateProvidedVariables([("SPP", 42.0, DateTime.UtcNow)]).Should().BeTrue();
            var watch = Stopwatch.StartNew();
            StackFixture.Eventually(() => TryRead(sessionB, ns, $"{provider}.SPP", out var v) && v is 42.0, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(20))
                .Should().BeTrue("the value written on the first Blackboard should be read on the second");
            watch.Stop();
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "a value replicates within a second (R-HUB-1)");

            // Second to first: a client of the second Blackboard writes one of the same provider's variables (a plain
            // OPC UA write, since the old client writes only what it injected itself), and the first one reads it.
            var sessionA = stack.Session;
            sessionA.FetchNamespaceTables();
            var write = new WriteValue { NodeId = Node(sessionB, ns, $"{provider}.WOB"), AttributeId = Attributes.Value, Value = new DataValue(new Variant(7.5)) { SourceTimestamp = DateTime.UtcNow } };
            sessionB.Write(null, [write], out var results, out _);
            StatusCode.IsGood(results[0]).Should().BeTrue(results[0].ToString());
            watch.Restart();
            StackFixture.Eventually(() => TryRead(sessionA, ns, $"{provider}.WOB", out var v) && v is 7.5, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(20))
                .Should().BeTrue("the value written on the second Blackboard should be read on the first");
            watch.Stop();
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "a value replicates within a second, both ways (R-HUB-1)");
            TryRead(sessionB, ns, $"{provider}.SPP", out var kept).Should().BeTrue();
            kept.Should().Be(42.0, "the first value stays where it arrived");

            // The old client reads the replicated value too, as a consumer application on the second Blackboard would.
            var b = StackFixture.Connect(StackFixture.EndpointB!);
            b.ReadAnyVariables(out var values, (ns, $"{provider}.SPP"), (ns, $"{provider}.WOB")).Should().BeTrue();
            values.Should().Equal(42.0, 7.5);
        }
        finally
        {
            stack.Delete(manifest).Should().BeTrue();
            StackFixture.Delete(sessionB, manifest).Should().BeTrue("the second Blackboard holds the manifest too, as its own live manifest");
        }
    }

    [Fact]
    public async Task T_HUB_2_A_manifest_injected_on_one_Blackboard_is_injected_on_the_other_with_its_origin_in_the_provenance()
    {
        Assert.SkipWhen(StackFixture.NoHub, StackFixture.NoHubReason);
        const string provider = "HubManifest";
        var manifest = StackFixture.Manifest(provider);
        var ns = $"http://ddhub.no/{provider}/Variables/";
        using var sessionB = StackFixture.OpenSession(StackFixture.EndpointB!);
        Forget(manifest, sessionB);
        stack.Client.Inject(manifest).Success.Should().BeTrue();
        try
        {
            // The second semantic service lists the manifest: it went through the pipeline there and was accepted.
            StackFixture.Eventually(() => IsLive(StackFixture.SemanticApiB!, provider), TimeSpan.FromSeconds(30))
                .Should().BeTrue("the manifest injected on the first Blackboard should be live on the second");
            var entryB = Entry(await stack.ListManifestsAsync(StackFixture.SemanticApiB!), provider)!.Value;
            entryB.GetProperty("accepted").GetBoolean().Should().BeTrue("the pipeline of the second Blackboard accepted it as the first did");

            // Its origin names the hub group and the sender, the first real-time process (R-HUB-3); on the first
            // Blackboard the same manifest is local.
            var origin = entryB.GetProperty("origin").GetString();
            origin.Should().MatchRegex("^hub:default:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", "the old protocol carries the sender id of the peer, not a site name");
            Entry(await stack.ListManifestsAsync(StackFixture.SemanticApi!), provider)!.Value.GetProperty("origin").GetString().Should().Be("local");
            var rows = await stack.QueryStoreAsync(StackFixture.SemanticApiB!,
                $"SELECT ?origin WHERE {{ GRAPH ?g {{ ?m <http://d-wis.org/blackboard/manifestId> \"{entryB.GetProperty("id").GetProperty("value").GetString()}\" ; <http://d-wis.org/blackboard/origin> ?origin }} }}");
            rows.Should().ContainSingle().Which.Should().Equal(origin);

            // The same injection path as a local one: the variables with their validity booleans, and the data point
            // answers the query on the second Blackboard as on the first.
            var b = StackFixture.Connect(StackFixture.EndpointB!);
            StackFixture.Eventually(() => b.ReadAnyVariables(out _, (ns, $"{provider}.SPP")), TimeSpan.FromSeconds(30)).Should().BeTrue();
            b.ReadAnyVariables(out var values, (ns, $"{provider}.SPP"), (ns, $"{provider}.SPP_validitySignal"), (ns, $"{provider}.WOB"), (ns, $"{provider}.WOB_validitySignal")).Should().BeTrue();
            values.Should().Equal(12.5, false, 3.0, false);
            var row = b.GetQueryResult(StackFixture.SignalQuery(provider)).Results.Should().ContainSingle().Subject.Items.Should().ContainSingle().Subject;
            row.NameSpace.Should().Be(ns);
            row.ID.Should().Be($"{provider}.SPP");
        }
        finally
        {
            stack.Delete(manifest).Should().BeTrue();
            StackFixture.Delete(sessionB, manifest).Should().BeTrue();
        }
    }

    /// <summary>
    /// Removes the test's manifest from both Blackboards when a run before left it there (<see cref="StackFixture.Forget"/>:
    /// the old hub replays every manifest it ever saw to a process that reconnects, deleted ones included); injecting
    /// it while it is live would change nothing and send nothing to the hub.
    /// </summary>
    private void Forget(DWIS.API.DTO.ManifestFile manifest, Opc.Ua.Client.Session sessionB)
    {
        stack.Forget(manifest);
        try { StackFixture.Delete(sessionB, manifest); } catch (ServiceResultException) { /* not there */ }
    }

    /// <summary>The list entry of a provider's manifest on a semantic service, null when it is not live there.</summary>
    private static System.Text.Json.JsonElement? Entry(System.Text.Json.JsonElement list, string provider)
    {
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.GetProperty("id").GetProperty("provider").GetString() == provider)
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>True once a semantic service lists a provider's manifest (its injection there is done).</summary>
    private bool IsLive(string semanticApi, string provider) => Entry(stack.ListManifestsAsync(semanticApi).GetAwaiter().GetResult(), provider) is not null;

    /// <summary>A node of a provider's variable in a session's namespace table (the table is fetched again: the provider may be new).</summary>
    private static NodeId Node(Opc.Ua.Client.Session session, string ns, string id)
    {
        var index = session.NamespaceUris.GetIndex(ns);
        if (index < 0)
        {
            session.FetchNamespaceTables();
            index = session.NamespaceUris.GetIndex(ns);
        }
        return index < 0 ? throw new InvalidOperationException($"{ns} is not a namespace of {session.Endpoint.EndpointUrl}") : new NodeId(id, (ushort)index);
    }

    /// <summary>One plain read of a variable; false while the variable or its namespace is not there yet.</summary>
    private static bool TryRead(Opc.Ua.Client.Session session, string ns, string id, out object? value)
    {
        value = null;
        try
        {
            var read = session.ReadValue(Node(session, ns, id));
            value = read.Value;
            return StatusCode.IsGood(read.StatusCode);
        }
        catch (Exception e) when (e is ServiceResultException or InvalidOperationException)
        {
            return false;
        }
    }
}
