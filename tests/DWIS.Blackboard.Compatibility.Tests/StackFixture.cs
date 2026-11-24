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
    public static IOPCUADWISClient Connect()
    {
        var client = new DWISClientOPCF(new DefaultDWISClientConfiguration { ServerAddress = Endpoint! }, null);
        if (!client.Connected)
        {
            throw new InvalidOperationException($"The old client did not connect to {Endpoint}.");
        }
        return client;
    }

    /// <summary>Opens a plain session with the old client's transport quotas, no security, anonymous.</summary>
    public static Session OpenSession()
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
        var endpoint = CoreClientUtils.SelectEndpoint(configuration, Endpoint, useSecurity: false);
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
    public bool Delete(ManifestFile manifest)
    {
        var outputs = Session.Call(ServerObject, Method("Delete"), new object[] { new[] { ManifestFile.ToJsonString(manifest) } });
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
    public async Task<List<string[]>> QueryStoreAsync(string sparql)
    {
        var csv = await _http.GetStringAsync($"{SemanticApi}/sparql?query={Uri.EscapeDataString(sparql)}");
        return csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(line => line.TrimEnd('\r').Split(',')).ToList();
    }

    /// <summary>Waits until a condition holds, polling every half second.</summary>
    public static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var stop = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stop)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(500);
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
