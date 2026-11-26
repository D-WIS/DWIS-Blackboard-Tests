using DWIS.API.DTO;
using DWIS.Client.ReferenceImplementation;
using DWIS.Client.ReferenceImplementation.OPCFoundation;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace DWIS.Blackboard.Perf;

/// <summary>A writer: the unchanged C# client connected once, a provider of its own, a fresh random value per variable on every write.</summary>
public sealed class Writer
{
    private readonly DWISClientOPCF _client;
    private readonly Random _random = new();
    private int _reconnects;

    public string Provider { get; }
    public IReadOnlyList<string> VariableIds { get; }
    public ManifestFile Manifest { get; }

    /// <summary>How many writes threw and were retried after a reconnect (each costs about a second).</summary>
    public int Reconnects => Volatile.Read(ref _reconnects);

    public Writer(string endpoint, string provider, int variables)
    {
        Provider = provider;
        VariableIds = Enumerable.Range(0, variables).Select(i => $"metric_{i}").ToList();
        Manifest = new ManifestFile
        {
            ManifestName = $"{provider} manifest",
            Provider = new InjectionProvider { Name = provider, Company = "D-WIS" },
            InjectionInformation = new InjectionInformation { InjectedNodesNamespaceAlias = "nodes", ProvidedVariablesNamespaceAlias = "pv", InjectedVariablesNamespaceAlias = "variables" },
            ProvidedVariables = VariableIds.Select(id => new ProvidedVariable { DataType = "double", Rank = 0, VariableID = id }).ToList(),
            InjectedNodes = [],
            InjectedReferences = [],
        };
        _client = new DWISClientOPCF(new DefaultDWISClientConfiguration { ServerAddress = endpoint }, null);
        if (!_client.Connected)
        {
            throw new InvalidOperationException($"The old client did not connect to {endpoint}.");
        }
    }

    public bool Inject() => _client.Inject(Manifest)?.Success ?? false;

    /// <summary>One write of every variable. One reconnect and retry when the session died, as the old suite did; a second failure propagates.</summary>
    public bool WriteOnce()
    {
        try
        {
            return WriteCore();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _reconnects);
            _client.TryReconnect();
            return WriteCore();
        }
    }

    private bool WriteCore()
    {
        var now = DateTime.UtcNow;
        return _client.UpdateProvidedVariables(VariableIds.Select(id => (id, (object)_random.NextDouble(), now)).ToList());
    }
}

/// <summary>Plain sessions of the OPC Foundation stack: the reader, and the calls the old client does not make (<c>Delete</c>).</summary>
public static class Sessions
{
    /// <summary>The namespace index the old client hard-codes for <c>DDHubServer</c> and its methods.</summary>
    public const ushort ServerNamespaceIndex = 2;

    /// <summary>Opens a plain session with the old client's transport quotas, no security, anonymous (as the compatibility tests do).</summary>
    public static Session Open(string endpointUrl, string name)
    {
        var application = new ApplicationInstance { ApplicationName = "DWIS Blackboard performance suite", ApplicationType = ApplicationType.Client };
        var configuration = application.Build("urn:d-wis:blackboard:tests:perf", "https://github.com/D-WIS/DWIS-Blackboard-Tests")
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
            .AddSecurityConfiguration("CN=DWIS Blackboard performance suite", Path.Combine(Path.GetTempPath(), "dwis-blackboard-tests", "pki"))
            .SetAutoAcceptUntrustedCertificates(true)
            .Create().GetAwaiter().GetResult();
        application.CheckApplicationInstanceCertificate(true, 0).GetAwaiter().GetResult();
        var endpoint = CoreClientUtils.SelectEndpoint(configuration, endpointUrl, useSecurity: false);
        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));
        return Session.Create(configuration, configured, false, name, 60_000, new UserIdentity(new AnonymousIdentityToken()), null).GetAwaiter().GetResult();
    }

    /// <summary>Removes a manifest through <c>DDHubServer.Delete</c>; false when the server refused (not live, for instance).</summary>
    public static bool Delete(Session session, ManifestFile manifest)
    {
        try
        {
            var outputs = session.Call(new NodeId("DDHubServer", ServerNamespaceIndex), new NodeId("Delete", ServerNamespaceIndex), new object[] { new[] { ManifestFile.ToJsonString(manifest) } });
            return outputs.Count == 1 && outputs[0] is true;
        }
        catch (ServiceResultException)
        {
            return false;
        }
    }

    /// <summary>The node of a provider's variable, in the session's namespace table (fetched again when the provider is new).</summary>
    public static NodeId Variable(Session session, string provider, string variableId)
    {
        var ns = $"http://ddhub.no/{provider}/Variables/";
        var index = session.NamespaceUris.GetIndex(ns);
        if (index < 0)
        {
            session.FetchNamespaceTables();
            index = session.NamespaceUris.GetIndex(ns);
        }
        return index < 0 ? throw new InvalidOperationException($"{ns} is not a namespace of {session.Endpoint.EndpointUrl}") : new NodeId($"{provider}.{variableId}", (ushort)index);
    }
}

/// <summary>The reader: one plain read of every writer's variables per iteration, on a session of its own.</summary>
public sealed class Reader(Session session, ReadValueIdCollection nodes)
{
    public int Count => nodes.Count;

    /// <summary>True when every value came back good.</summary>
    public bool ReadOnce()
    {
        session.Read(null, 0, TimestampsToReturn.Source, nodes, out var results, out _);
        return results.Count == nodes.Count && results.All(r => StatusCode.IsGood(r.StatusCode));
    }
}
