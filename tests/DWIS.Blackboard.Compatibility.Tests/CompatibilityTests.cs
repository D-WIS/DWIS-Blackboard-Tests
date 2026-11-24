using System.Text.RegularExpressions;
using DWIS.API.DTO;
using DWIS.Client.ReferenceImplementation;
using Opc.Ua;

namespace DWIS.Blackboard.Compatibility.Tests;

/// <summary>
/// The compatibility rows of the test plan (G.2) that a running stack answers: T-BC-4, T-EP-1, T-RT-3 and T-RT-4.
/// Each test injects a manifest of its own provider through the old client and removes it at the end, so that the
/// stack is left as found (the smoke's manifests stay).
/// </summary>
[Collection("stack")]
public sealed class CompatibilityTests(StackFixture stack)
{
    [Fact]
    public void T_BC_4_Register_and_Resolve_answer_BadNotSupported()
    {
        Assert.SkipWhen(StackFixture.NoStack, StackFixture.NoStackReason);
        var session = stack.Session;

        var register = () => session.Call(StackFixture.ServerObject, StackFixture.Method("Register"), new object[] { new[] { "{}" } });
        var resolve = () => session.Call(StackFixture.ServerObject, StackFixture.Method("Resolve"), new object[] { new[] { "{}" } });

        register.Should().Throw<ServiceResultException>().Which.StatusCode.Should().Be(StatusCodes.BadNotSupported);
        resolve.Should().Throw<ServiceResultException>().Which.StatusCode.Should().Be(StatusCodes.BadNotSupported);
    }

    [Fact]
    public void T_EP_1_A_client_using_only_OPC_UA_completes_inject_query_register_subscribe_read_and_delete()
    {
        Assert.SkipWhen(StackFixture.NoStack, StackFixture.NoStackReason);
        const string provider = "CompatEndpoint";
        var manifest = StackFixture.Manifest(provider);
        var providerClient = stack.Client;
        try
        {
            // Inject: the answer maps every provided variable to a node the client writes to.
            var injected = providerClient.Inject(manifest);
            injected.Success.Should().BeTrue();
            injected.ProvidedVariables.Select(m => m.ManifestItemID).Should().BeEquivalentTo("SPP", "WOB");
            injected.ProvidedVariablesNamespace.Should().Be($"http://ddhub.no/{provider}/Variables/");

            // Query: a consumer, connected after the provider so that it knows the provider's namespace, finds the signal.
            var consumer = StackFixture.Connect();
            var result = consumer.GetQueryResult(StackFixture.SignalQuery(provider));
            result.Should().NotBeNull();
            var row = result.Results.Should().ContainSingle().Subject.Items.Should().ContainSingle().Subject;
            row.NameSpace.Should().Be($"http://ddhub.no/{provider}/Variables/");
            row.ID.Should().Be($"{provider}.SPP");

            // Register: the registration lists the same row and names the diff variable to watch.
            var (diffJson, _) = consumer.RegisterQuery(StackFixture.SignalQuery(provider), _ => { });
            var registration = QueryResultsDiff.FromJsonString(diffJson);
            registration.Added.Should().ContainSingle();
            registration.QueryResultID.Should().StartWith($"ns={StackFixture.ServerNamespaceIndex};s=QueryDiff");

            // Subscribe: the initial value arrives, then the value the provider writes.
            var seen = new List<object?>();
            var arrived = new ManualResetEventSlim(false);
            consumer.Subscribe("signal", (_, changes) =>
            {
                lock (seen)
                {
                    seen.AddRange(changes.Select(c => c.Value));
                    if (seen.Count >= 2)
                    {
                        arrived.Set();
                    }
                }
            }, (row.NameSpace, row.ID, (object)row.ID));
            providerClient.UpdateProvidedVariables([("SPP", 42.0, DateTime.UtcNow)]).Should().BeTrue();
            arrived.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue("the subscription should deliver the initial value and the written one");
            lock (seen)
            {
                seen.Should().Contain(12.5).And.Contain(42.0);
            }

            // Read: the written value, over the wire.
            consumer.ReadAnyVariables(out var values, (row.NameSpace, row.ID)).Should().BeTrue();
            values.Should().ContainSingle().Which.Should().Be(42.0);
        }
        finally
        {
            // Delete: the manifest goes, with its nodes and its triples.
            stack.Delete(manifest).Should().BeTrue();
        }
        stack.Client.GetQueryResult(StackFixture.SignalQuery(provider)).Results.Should().BeEmpty("the data point went with the manifest");
    }

    [Fact]
    public void T_RT_3_The_address_space_holds_only_folders_variables_booleans_methods_and_query_nodes()
    {
        Assert.SkipWhen(StackFixture.NoStack, StackFixture.NoStackReason);
        const string provider = "CompatSpace";
        var manifest = StackFixture.Manifest(provider);
        stack.Client.Inject(manifest).Success.Should().BeTrue();
        try
        {
            var session = stack.Session;
            session.FetchNamespaceTables();
            var nodes = new List<(NodeClass Class, string Name, NodeId Type)>();
            Walk(StackFixture.ServerObject, nodes);

            nodes.Should().NotBeEmpty();
            nodes.Select(n => n.Class).Distinct().Should().BeSubsetOf([NodeClass.Object, NodeClass.Variable, NodeClass.Method],
                "no type, data type, reference type or view is created: the semantics live in the service (R-RT-3)");
            nodes.Where(n => n.Class == NodeClass.Object).Select(n => n.Type).Distinct()
                .Should().BeSubsetOf([ObjectTypeIds.FolderType, ObjectTypeIds.BaseObjectType], "an object is a folder or a plain object");
            var providerFolder = StackFixture.Children(session, StackFixture.ServerObject).Single(r => r.BrowseName.Name == "Providers");
            var folder = StackFixture.Children(session, StackFixture.Id(session, providerFolder)).Single(r => r.BrowseName.Name == provider);
            var inFolder = StackFixture.Children(session, StackFixture.Id(session, folder));
            inFolder.Should().OnlyContain(r => r.NodeClass == NodeClass.Variable, "a provider folder holds variables and their validity booleans, nothing else");
            inFolder.Select(r => r.BrowseName.Name).Should().BeEquivalentTo("SPP", "SPP_validitySignal", "WOB", "WOB_validitySignal");
            inFolder.Select(r => r.NodeId.Identifier).Should().AllSatisfy(id => id.Should().BeOfType<string>().Which.Should().StartWith(provider + "."));

            void Walk(NodeId node, List<(NodeClass, string, NodeId)> into)
            {
                foreach (var reference in StackFixture.Children(session, node))
                {
                    into.Add((reference.NodeClass, reference.BrowseName.Name, ExpandedNodeId.ToNodeId(reference.TypeDefinition, session.NamespaceUris)));
                    if (reference.NodeClass == NodeClass.Object)
                    {
                        Walk(StackFixture.Id(session, reference), into);
                    }
                }
            }
        }
        finally
        {
            stack.Delete(manifest).Should().BeTrue();
        }
    }

    [Fact]
    public async Task T_RT_4_Every_provided_variable_has_its_validity_boolean_and_the_IsValidBy_triple_is_in_the_store()
    {
        Assert.SkipWhen(StackFixture.NoStack, StackFixture.NoStackReason);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(StackFixture.SemanticApi), "DWIS_SEMANTIC_API is not set: no store to look into.");
        const string provider = "CompatValidity";
        var manifest = StackFixture.Manifest(provider);
        var injected = stack.Client.Inject(manifest);
        injected.Success.Should().BeTrue();
        try
        {
            // The sibling next to every variable, readable, false until the provider says otherwise (R-RT-4).
            var index = injected.ProvidedVariables.First().InjectedID.NameSpaceIndex;
            foreach (var id in new[] { "SPP", "WOB" })
            {
                stack.Client.ReadAnyVariables(out var values, (index, $"{provider}.{id}_validitySignal")).Should().BeTrue($"{id} has its validity boolean");
                values.Should().ContainSingle().Which.Should().Be(false);
            }

            // The triple in the store: the data point's signal is valid by its boolean.
            var rows = await stack.QueryStoreAsync(
                $"PREFIX ddhub: <http://ddhub.no/> SELECT ?flag WHERE {{ <http://ddhub.no/{provider}/SPPPoint> ddhub:HasDynamicValue ?v . ?v ddhub:IsValidBy ?flag }}");
            rows.Should().ContainSingle().Which.Should().ContainSingle().Which.Should().EndWith($"/{provider}.SPP_validitySignal");
        }
        finally
        {
            stack.Delete(manifest).Should().BeTrue();
        }
    }

    [Fact]
    public void T_BC_3_Inject_and_ResolveQuery_answer_the_same_json_as_the_old_server()
    {
        // The old server's answers were recorded once from digiwells/ddhubserver:latest (fixtures/compatibility/old-server,
        // its README says how); with DWIS_RECORD_OLD_ANSWERS=1 this test records them again from DWIS_REALTIME_ENDPOINT
        // instead of comparing. Two known differences are taken out on both sides: the InjectedNodes list (the old server
        // created nodes for injected nodes and listed them; the Rig Edition creates none, D-21) and the namespace index
        // values (they depend on what was injected before on that server). Everything else must match byte for byte.
        Assert.SkipWhen(StackFixture.NoStack, StackFixture.NoStackReason);
        var recording = Environment.GetEnvironmentVariable("DWIS_RECORD_OLD_ANSWERS") == "1";
        var folder = Path.Combine(StackFixture.RepositoryRoot(), "fixtures", "compatibility", "old-server");
        var manifest = StackFixture.Manifest("CompatOld");
        var manifestJson = recording ? ManifestFile.ToJsonString(manifest) : File.ReadAllText(Path.Combine(folder, "manifest.json"));
        var query = recording ? StackFixture.SignalQuery("CompatOld") : File.ReadAllText(Path.Combine(folder, "query.sparql"));
        var session = stack.Session;

        var injectAnswer = (string)session.Call(StackFixture.ServerObject, StackFixture.Method("Inject"), new object[] { new[] { manifestJson } })[0];
        var queryAnswer = (string)session.Call(StackFixture.ServerObject, StackFixture.Method("ResolveQuery"), new object[] { new[] { query } })[0];

        if (recording)
        {
            // The old server reasons after the injection, in the background: ask again until the row is there.
            StackFixture.Eventually(() =>
            {
                queryAnswer = (string)session.Call(StackFixture.ServerObject, StackFixture.Method("ResolveQuery"), new object[] { new[] { query } })[0];
                return queryAnswer.Contains("CompatOld.SPP", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(60)).Should().BeTrue("the recorded answer must hold the row");
            File.WriteAllText(Path.Combine(folder, "manifest.json"), manifestJson);
            File.WriteAllText(Path.Combine(folder, "query.sparql"), query);
            File.WriteAllText(Path.Combine(folder, "inject-answer.json"), injectAnswer);
            File.WriteAllText(Path.Combine(folder, "query-answer.json"), queryAnswer);
            File.WriteAllText(Path.Combine(folder, "recorded.txt"), $"recorded from {StackFixture.Endpoint} on {DateTime.UtcNow:yyyy-MM-dd}; see the README for the image\n");
            return;
        }
        try
        {
            var expectedInject = File.ReadAllText(Path.Combine(folder, "inject-answer.json"));
            var expectedQuery = File.ReadAllText(Path.Combine(folder, "query-answer.json"));
            WithoutKnownDifferences(injectAnswer).Should().Be(WithoutKnownDifferences(expectedInject),
                "the ManifestInjectionResult must be the old server's, byte for byte, but for the injected nodes (D-21) and the namespace index values");
            queryAnswer.Should().Be(expectedQuery, "the QueryResult must be the old server's, byte for byte");
        }
        finally
        {
            stack.Delete(manifest).Should().BeTrue();
        }
    }

    private static string WithoutKnownDifferences(string injectionResult)
    {
        var withoutNodes = Regex.Replace(injectionResult, @"""InjectedNodes"": \[[^\]]*\]", @"""InjectedNodes"": []", RegexOptions.Singleline);
        return Regex.Replace(withoutNodes, @"""NameSpaceIndex"": \d+", @"""NameSpaceIndex"": N");
    }
}
