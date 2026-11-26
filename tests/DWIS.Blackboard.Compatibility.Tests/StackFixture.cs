using System.Globalization;
using System.Net.Http.Headers;
using DWIS.API.DTO;
using DWIS.Client.ReferenceImplementation;
using DWIS.Client.ReferenceImplementation.OPCFoundation;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace DWIS.Blackboard.Compatibility.Tests;

/// <summary>
/// The running stack, as the tests see it: the unchanged C# client 2.2.x (the one every DWIS application uses, which
/// finds <c>DDHubServer</c> at namespace index 2), a plain OPC UA session of the same stack for what the client does
/// not expose (browsing, a method's status code), and the semantic service's HTTP API for a look into the store.
/// Everything comes from the environment; without <c>DWIS_REALTIME_ENDPOINT</c> the tests skip.
/// </summary>
public sealed class StackFixture : IDisposable
{
    /// <summary>The OPC UA endpoint of the real-time process, for example <c>opc.tcp://127.0.0.1:48030</c>.</summary>
    public static readonly string? Endpoint = Environment.GetEnvironmentVariable("DWIS_REALTIME_ENDPOINT");

    /// <summary>The semantic service's HTTP API, for example <c>http://127.0.0.1:8080/api/v1</c>.</summary>
    public static readonly string? SemanticApi = Environment.GetEnvironmentVariable("DWIS_SEMANTIC_API");

    /// <summary>The compose file of the stack, for the test that restarts the real-time process.</summary>
    public static readonly string? ComposeFile = Environment.GetEnvironmentVariable("DWIS_COMPOSE_FILE");

    /// <summary>True when there is no stack to talk to.</summary>
    public static bool NoStack => string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>Why a test skips without a stack.</summary>
    public const string NoStackReason = "DWIS_REALTIME_ENDPOINT is not set: no real-time process to talk to.";

    /// <summary>The OPC UA endpoint of the second Blackboard of the hub profile (M5.3), for example <c>opc.tcp://127.0.0.1:48031</c>.</summary>
    public static readonly string? EndpointB = Environment.GetEnvironmentVariable("DWIS_REALTIME_ENDPOINT_B");

    /// <summary>The semantic service of the second Blackboard, for example <c>http://127.0.0.1:8090/api/v1</c>.</summary>
    public static readonly string? SemanticApiB = Environment.GetEnvironmentVariable("DWIS_SEMANTIC_API_B");

    /// <summary>True when there is no second Blackboard joined to a hub with the first.</summary>
    public static bool NoHub => NoStack || string.IsNullOrWhiteSpace(EndpointB) || string.IsNullOrWhiteSpace(SemanticApi) || string.IsNullOrWhiteSpace(SemanticApiB);

    /// <summary>Why a replication test skips.</summary>
    public const string NoHubReason = "DWIS_REALTIME_ENDPOINT_B, DWIS_SEMANTIC_API and DWIS_SEMANTIC_API_B are not all set: no second Blackboard behind a hub (compose/hub.sh).";

    /// <summary>The namespace index the old client hard-codes for <c>DDHubServer</c> and its methods.</summary>
    public const ushort ServerNamespaceIndex = 2;

    private IOPCUADWISClient? _client;
    private Session? _session;
    private readonly HttpClient _http = new();

    public StackFixture()
    {
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
    }

    /// <summary>The old client, connected once for the collection; a provider in the tests.</summary>
    public IOPCUADWISClient Client => _client ??= Connect();

    /// <summary>A plain session of the OPC Foundation stack, for browsing and raw calls.</summary>
    public Session Session => _session ??= OpenSession();

    /// <summary>The semantic service's HTTP API, asking for CSV.</summary>
    public HttpClient Http => _http;

    /// <summary>Forgets the client and the session, after a restart of the process closed them; the next use reconnects.</summary>
    public void Reset()
    {
        try { _session?.Dispose(); } catch (Exception) { /* the server is gone */ }
        _session = null;
        _client = null;
    }

    /// <summary>Connects the old client as the applications do: the default configuration with the server address, no logger.</summary>
    public static IOPCUADWISClient Connect() => Connect(Endpoint!);

    /// <summary>The old client connected to any endpoint (the second Blackboard of the hub profile, for instance).</summary>
    public static IOPCUADWISClient Connect(string endpoint)
    {
        var client = new DWISClientOPCF(new DefaultDWISClientConfiguration { ServerAddress = endpoint }, null);
        if (!client.Connected)
        {
            throw new InvalidOperationException($"The old client did not connect to {endpoint}.");
        }
        return client;
    }

    /// <summary>Opens a plain session with the old client's transport quotas, no security, anonymous.</summary>
    public static Session OpenSession() => OpenSession(Endpoint!);

    /// <summary>A plain session to any endpoint.</summary>
    public static Session OpenSession(string endpointUrl)
    {
        var application = new ApplicationInstance { ApplicationName = "DWIS Blackboard compatibility tests", ApplicationType = ApplicationType.Client };
        var configuration = application.Build("urn:d-wis:blackboard:tests:compatibility", "https://github.com/D-WIS/DWIS-Blackboard-Tests")
            .SetTransportQuotas(new TransportQuotas
            {
                OperationTimeout = 600_000,
                MaxStringLength = 16 * 1024 * 1024,
                MaxByteStringLength = 16 * 1024 * 1024,
                MaxArrayLength = 65535,
                MaxMessageSize = 16 * 1024 * 1024,
                MaxBufferSize = 65535,
                ChannelLifetime = 300_000,
                SecurityTokenLifetime = 3_600_000,
            })
            .AsClient()
            .AddSecurityConfiguration("CN=DWIS Blackboard compatibility tests", Path.Combine(Path.GetTempPath(), "dwis-blackboard-tests", "pki"))
            .SetAutoAcceptUntrustedCertificates(true)
            .Create().GetAwaiter().GetResult();
        application.CheckApplicationInstanceCertificate(true, 0).GetAwaiter().GetResult();
        var endpoint = CoreClientUtils.SelectEndpoint(configuration, endpointUrl, useSecurity: false);
        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));
        return Session.Create(configuration, configured, false, "compatibility", 60_000, new UserIdentity(new AnonymousIdentityToken()), null).GetAwaiter().GetResult();
    }

    /// <summary>The <c>DDHubServer</c> object as the old client addresses it (<c>ObjectsIds.DDHubServerInstanceName</c> in the schemas, index 2).</summary>
    public static NodeId ServerObject => new("DDHubServer", ServerNamespaceIndex);

    /// <summary>A method of <c>DDHubServer</c> as the old client addresses it.</summary>
    public static NodeId Method(string name) => new(name, ServerNamespaceIndex);

    /// <summary>
    /// Removes a manifest through <c>DDHubServer.Delete</c> with a plain call: the old client's <c>Delete</c> (and its
    /// <c>Update</c>) are stubs that throw, so no application ever called them; the Rig Edition answers them anyway
    /// (specification 3.1). The nodes and the triples go with the manifest.
    /// </summary>
    public bool Delete(ManifestFile manifest) => Delete(Session, manifest);

    /// <summary>
    /// Deletes a manifest that a run before may have left live, so that the inject that follows creates the variables
    /// anew with the manifest's initial values. With the hub profile (M5.3) the old hub replays every manifest it ever
    /// saw, with the latest value of each variable as its initial value, to a real-time process that reconnects (a
    /// restart, a hub outage), deleted ones included: a provider of an earlier run may be live again with those values,
    /// and an inject of the same provider then keeps its nodes and values. That is also why the replication tests run
    /// on their own, after the other tests of this collection: a restart with the hub role on replays into the tests
    /// that follow it.
    /// </summary>
    public void Forget(ManifestFile manifest)
    {
        try { Delete(manifest); } catch (ServiceResultException) { /* not there */ }
    }

    /// <summary>The same through any session (the second Blackboard's, for instance).</summary>
    public static bool Delete(Session session, ManifestFile manifest)
    {
        var outputs = session.Call(ServerObject, Method("Delete"), new object[] { new[] { ManifestFile.ToJsonString(manifest) } });
        return outputs.Count == 1 && outputs[0] is true;
    }

    /// <summary>The root of this repository (the folder with the solution), to resolve a relative compose file from the test's own folder.</summary>
    public static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "DWIS.Blackboard.Tests.slnx")))
        {
            folder = folder.Parent;
        }
        return folder?.FullName ?? throw new InvalidOperationException("The repository root (DWIS.Blackboard.Tests.slnx) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The compose file as an absolute path.</summary>
    public static string ComposePath() => Path.IsPathRooted(ComposeFile!) ? ComposeFile! : Path.Combine(RepositoryRoot(), ComposeFile!);

    /// <summary>The hierarchical children of a node.</summary>
    public static ReferenceDescriptionCollection Children(Session session, NodeId node)
    {
        session.Browse(null, null, node, 0, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, 0, out _, out var references);
        return references;
    }

    /// <summary>A referenced node's id in the session's namespace table.</summary>
    public static NodeId Id(Session session, ReferenceDescription reference) => ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);

    /// <summary>
    /// A small manifest of a provider: two provided variables and one data point that holds the first one, in the
    /// shape the old client sends (the service completes the validity booleans and their <c>IsValidBy</c> references).
    /// </summary>
    public static ManifestFile Manifest(string provider) => ManifestFile.FromJsonString($$"""
        {
          "ManifestName": "Compatibility signals",
          "Provider": { "Name": "{{provider}}", "Company": "D-WIS" },
          "InjectionInformation": { "InjectedNodesNamespaceAlias": "nodes", "ProvidedVariablesNamespaceAlias": "pv", "InjectedVariablesNamespaceAlias": "variables" },
          "ProvidedVariables": [
            { "VariableID": "SPP", "DataType": "double", "Rank": 0, "InitialValue": "12.5" },
            { "VariableID": "WOB", "DataType": "double", "Rank": 0, "InitialValue": "3" }
          ],
          "InjectedNodes": [ { "UniqueName": "SPPPoint", "BrowseName": "SPPPoint", "DisplayName": "SPPPoint", "TypeDictionaryURI": "DrillingDataPoint" } ],
          "InjectedReferences": [ { "Subject": { "NameSpace": "nodes", "ID": "SPPPoint" }, "VerbURI": "http://ddhub.no/HasDynamicValue", "Object": { "NameSpace": "pv", "ID": "SPP" } } ]
        }
        """);

    /// <summary>The query for the signal of the manifest's data point, written with the legacy namespace as the applications write it.</summary>
    public static string SignalQuery(string provider) =>
        $"PREFIX ddhub: <http://ddhub.no/> SELECT ?signal WHERE {{ <http://ddhub.no/{provider}/SPPPoint> ddhub:HasDynamicValue ?signal }}";

    /// <summary>The rows of a SPARQL query through the service's HTTP API (CSV: the header line is dropped).</summary>
    public Task<List<string[]>> QueryStoreAsync(string sparql) => QueryStoreAsync(SemanticApi!, sparql);

    /// <summary>The same against any semantic service (the second Blackboard's, for instance).</summary>
    public async Task<List<string[]>> QueryStoreAsync(string semanticApi, string sparql)
    {
        var csv = await _http.GetStringAsync($"{semanticApi}/sparql?query={Uri.EscapeDataString(sparql)}");
        return csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(line => line.TrimEnd('\r').Split(',')).ToList();
    }

    /// <summary>The manifests a semantic service lists (<c>GET /manifests</c>), as JSON.</summary>
    public async Task<System.Text.Json.JsonElement> ListManifestsAsync(string semanticApi)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{semanticApi}/manifests");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>Waits until a condition holds, polling every half second (or <paramref name="every"/>).</summary>
    public static bool Eventually(Func<bool> condition, TimeSpan timeout, TimeSpan? every = null)
    {
        var stop = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stop)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(every ?? TimeSpan.FromMilliseconds(500));
        }
        return condition();
    }

    /// <summary>A value as text, for messages.</summary>
    public static string Text(object? value) => value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "null";

    public void Dispose()
    {
        _session?.Close();
        _session?.Dispose();
        _http.Dispose();
    }
}

[CollectionDefinition("stack")]
public sealed class StackCollection : ICollectionFixture<StackFixture>;
