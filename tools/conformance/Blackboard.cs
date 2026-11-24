using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace DWIS.Blackboard.Conformance;

/// <summary>
/// The endpoint under test, seen through a plain session of the OPC Foundation stack with the old client's transport
/// quotas, no security and an anonymous user (the lab profile; credentials come with a later version). The
/// <c>DDHubServer</c> object is found by browse name under <c>Objects</c> (D-57), its methods as its children.
/// </summary>
public sealed class Blackboard : IDisposable
{
    public const string ObjectName = "DDHubServer";

    private Blackboard(Session session)
    {
        Session = session;
    }

    public Session Session { get; }

    /// <summary>The <c>DDHubServer</c> object, once found; null until <see cref="FindServerObject"/> found it.</summary>
    public NodeId? ServerObject { get; private set; }

    /// <summary>The method nodes by name, once found.</summary>
    public Dictionary<string, ReferenceDescription> Methods { get; } = new(StringComparer.Ordinal);

    public static Blackboard Connect(string endpointUrl)
    {
        var application = new ApplicationInstance { ApplicationName = "DWIS Blackboard conformance suite", ApplicationType = ApplicationType.Client };
        var configuration = application.Build("urn:d-wis:blackboard:conformance", "https://github.com/D-WIS/DWIS-Blackboard-Tests")
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
            .AddSecurityConfiguration("CN=DWIS Blackboard conformance suite", Path.Combine(Path.GetTempPath(), "dwis-blackboard-conformance", "pki"))
            .SetAutoAcceptUntrustedCertificates(true)
            .Create().GetAwaiter().GetResult();
        application.CheckApplicationInstanceCertificate(true, 0).GetAwaiter().GetResult();
        var endpoint = CoreClientUtils.SelectEndpoint(configuration, endpointUrl, useSecurity: false);
        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));
        var session = Session.Create(configuration, configured, false, "conformance", 120_000, new UserIdentity(new AnonymousIdentityToken()), null).GetAwaiter().GetResult();
        return new Blackboard(session);
    }

    /// <summary>The hierarchical children of a node.</summary>
    public ReferenceDescriptionCollection Children(NodeId node)
    {
        Session.Browse(null, null, node, 0, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, 0, out _, out var references);
        return references;
    }

    public NodeId Id(ReferenceDescription reference) => ExpandedNodeId.ToNodeId(reference.NodeId, Session.NamespaceUris);

    /// <summary>Finds <c>DDHubServer</c> under <c>Objects</c> by browse name and its method nodes; null with the reason when not found.</summary>
    public string? FindServerObject()
    {
        var candidates = Children(ObjectIds.ObjectsFolder).Where(r => r.BrowseName.Name == ObjectName && r.NodeClass == NodeClass.Object).ToList();
        if (candidates.Count != 1)
        {
            return candidates.Count == 0 ? $"no object named {ObjectName} under Objects" : $"{candidates.Count} objects named {ObjectName} under Objects";
        }
        ServerObject = Id(candidates[0]);
        Methods.Clear();
        foreach (var method in Children(ServerObject).Where(r => r.NodeClass == NodeClass.Method))
        {
            Methods[method.BrowseName.Name] = method;
        }
        return null;
    }

    /// <summary>Calls a method of <c>DDHubServer</c> with one string argument wrapped in an array, as the old client does.</summary>
    public IList<object> Call(string method, params string[] input)
    {
        if (ServerObject is null || !Methods.TryGetValue(method, out var reference))
        {
            throw new InvalidOperationException($"the method {method} was not found");
        }
        return Session.Call(ServerObject, Id(reference), input.Length == 0 ? [] : new object[] { input });
    }

    /// <summary>Calls a method and gives the status code it answered, Good when it did not throw.</summary>
    public (StatusCode Status, IList<object> Outputs) TryCall(string method, params string[] input)
    {
        try
        {
            return (StatusCodes.Good, Call(method, input));
        }
        catch (ServiceResultException e)
        {
            return (e.StatusCode, []);
        }
    }

    /// <summary>The value of a variable, by namespace URI and node id string, or null when the read fails.</summary>
    public DataValue? Read(string namespaceUri, string nodeId)
    {
        Session.FetchNamespaceTables();
        var index = Session.NamespaceUris.GetIndex(namespaceUri);
        if (index < 0)
        {
            return null;
        }
        try
        {
            var value = Session.ReadValue(new NodeId(nodeId, (ushort)index));
            return StatusCode.IsGood(value.StatusCode) ? value : null;
        }
        catch (ServiceResultException)
        {
            return null; // the stack throws BadNodeIdUnknown for a node that is gone
        }
    }

    public void Dispose()
    {
        try
        {
            Session.Close();
        }
        catch (Exception)
        {
            // the server may be gone
        }
        Session.Dispose();
    }
}
