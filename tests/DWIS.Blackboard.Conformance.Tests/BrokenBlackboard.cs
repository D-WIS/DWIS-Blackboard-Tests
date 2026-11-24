using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace DWIS.Blackboard.Conformance.Tests;

/// <summary>What is wrong with a broken endpoint.</summary>
public enum Flaw
{
    /// <summary>The <c>DDHubServer</c> object sits under a folder of its own, not under <c>Objects</c>: a client that browses for it finds nothing.</summary>
    ObjectElsewhere,

    /// <summary>The provided variables are created, their <c>_validitySignal</c> booleans are not.</summary>
    NoValidityBooleans,
}

/// <summary>
/// A small OPC Foundation server that looks like a Blackboard but for one flaw (T-MOD-7): the <c>DDHubServer</c>
/// object with the nine methods declared as the old server declares them, an <c>Inject</c> that creates the provided
/// variables and answers the result JSON, the other methods answering the least a client accepts.
/// </summary>
public sealed class BrokenBlackboard : IAsyncDisposable
{
    private readonly ApplicationInstance _application;
    private readonly StandardServer _server;

    private BrokenBlackboard(ApplicationInstance application, StandardServer server, string endpointUrl)
    {
        _application = application;
        _server = server;
        EndpointUrl = endpointUrl;
    }

    public string EndpointUrl { get; }

    public static async Task<BrokenBlackboard> StartAsync(Flaw flaw)
    {
        var endpointUrl = $"opc.tcp://localhost:{FreePort()}";
        var application = new ApplicationInstance { ApplicationName = "DWIS Blackboard broken endpoint", ApplicationType = ApplicationType.Server };
        var configuration = await application.Build("urn:d-wis:blackboard:tests:broken:" + flaw, "https://github.com/D-WIS/DWIS-Blackboard-Tests")
            .AsServer([endpointUrl])
            .AddUnsecurePolicyNone()
            .AddUserTokenPolicy(UserTokenType.Anonymous)
            .AddSecurityConfiguration("CN=DWIS Blackboard broken endpoint", Path.Combine(Path.GetTempPath(), "dwis-blackboard-tests", "broken-pki"))
            .SetAutoAcceptUntrustedCertificates(true)
            .Create();
        configuration.ServerConfiguration.DiagnosticsEnabled = false;
        await application.CheckApplicationInstanceCertificate(true, 0);
        var server = new Server(flaw);
        await application.Start(server);
        return new BrokenBlackboard(application, server, endpointUrl);
    }

    public ValueTask DisposeAsync()
    {
        _application.Stop();
        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Server(Flaw flaw) : StandardServer
    {
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration) =>
            new(server, configuration, null, new NodeManager(server, configuration, flaw));

        protected override ServerProperties LoadServerProperties() => new() { ManufacturerName = "D-WIS", ProductName = "DWIS Blackboard broken endpoint", ProductUri = "urn:d-wis:blackboard:tests:broken", SoftwareVersion = "0", BuildNumber = "0", BuildDate = DateTime.UtcNow };
    }

    private sealed class NodeManager(IServerInternal server, ApplicationConfiguration configuration, Flaw flaw) : CustomNodeManager2(server, configuration, "http://d-wis.org/tests/broken")
    {
        private static readonly (string Name, string Display, string[] Inputs, (string Name, bool Boolean)[] Outputs)[] Declarations =
        [
            ("Inject", "Inject Manifest", ["ManifestFile"], [("InjectionResult", false)]),
            ("Update", "Update Manifest", ["ManifestFile"], [("InjectionResult", false)]),
            ("Delete", "Delete Manifest", ["ManifestFile"], [("Removed", true)]),
            ("Register", "Register Acquisition File", ["AcquisitionFile"], [("AcquisitionFileID", false)]),
            ("Resolve", "Resolve Acquisition File", ["AcquisitionFile"], [("ResolutionResult", false)]),
            ("RegisterQuery", "Register Query", ["Query"], [("Initial query diff", false), ("QueryID", false)]),
            ("UnregisterQuery", "Unregister Query", ["Query"], [("Released", true)]),
            ("ResolveQuery", "Resolve Query", ["Query"], [("QueryResolution", false)]),
            ("GetResources", "Get resources", [], [("Resources", false)]),
        ];

        private readonly HashSet<ushort> _claimedIndexes = [];
        private readonly List<string> _claimedUris = [];
        private BaseObjectState _object = null!;
        private FolderState _providers = null!;

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                var index = NamespaceIndexes[0];
                _claimedIndexes.Add(index);
                _claimedUris.Add(NamespaceUris.First());
                NodeState parent;
                if (flaw == Flaw.ObjectElsewhere)
                {
                    // The object under a folder of its own, under Objects: a browse of Objects does not find DDHubServer.
                    var elsewhere = new FolderState(null) { NodeId = new NodeId("Elsewhere", index), BrowseName = new QualifiedName("Elsewhere", index), DisplayName = "Elsewhere", TypeDefinitionId = ObjectTypeIds.FolderType, EventNotifier = EventNotifiers.None };
                    Attach(externalReferences, elsewhere);
                    parent = elsewhere;
                }
                else
                {
                    parent = null!;
                }
                _object = new BaseObjectState(parent is null ? null : parent)
                {
                    NodeId = new NodeId("DDHubServer", index),
                    BrowseName = new QualifiedName("DDHubServer", index),
                    DisplayName = "DDHubServer",
                    TypeDefinitionId = ObjectTypeIds.BaseObjectType,
                    EventNotifier = EventNotifiers.None,
                };
                if (parent is null)
                {
                    Attach(externalReferences, _object);
                }
                else
                {
                    _object.ReferenceTypeId = ReferenceTypeIds.Organizes;
                    parent.AddChild(_object);
                    AddPredefinedNode(SystemContext, _object);
                }
                foreach (var (name, display, inputs, outputs) in Declarations)
                {
                    var method = new MethodState(_object)
                    {
                        NodeId = new NodeId(name, index),
                        BrowseName = new QualifiedName(name, index),
                        DisplayName = display,
                        ReferenceTypeId = ReferenceTypeIds.HasComponent,
                        Executable = true,
                        UserExecutable = true,
                    };
                    if (inputs.Length > 0)
                    {
                        method.InputArguments = Arguments(method, name + "_InputArguments", BrowseNames.InputArguments, inputs.Select(i => new Argument(i, DataTypeIds.String, ValueRanks.OneDimension, "")).ToArray());
                    }
                    method.OutputArguments = Arguments(method, name + "_OutputArguments", BrowseNames.OutputArguments, outputs.Select(o => new Argument(o.Name, o.Boolean ? DataTypeIds.Boolean : DataTypeIds.String, ValueRanks.Scalar, "")).ToArray());
                    method.OnCallMethod2 = OnCall;
                    _object.AddChild(method);
                    AddPredefinedNode(SystemContext, method);
                }
                _providers = new FolderState(_object) { NodeId = new NodeId("Providers", index), BrowseName = new QualifiedName("Providers", index), DisplayName = "Providers", TypeDefinitionId = ObjectTypeIds.FolderType, ReferenceTypeId = ReferenceTypeIds.Organizes, EventNotifier = EventNotifiers.None };
                _object.AddChild(_providers);
                AddPredefinedNode(SystemContext, _providers);
            }
        }

        private void Attach(IDictionary<NodeId, IList<IReference>> externalReferences, NodeState node)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }
            node.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, node.NodeId));
            AddPredefinedNode(SystemContext, node);
        }

        private PropertyState<Argument[]> Arguments(MethodState method, string nodeId, string browseName, Argument[] arguments) => new(method)
        {
            NodeId = new NodeId(nodeId, NamespaceIndexes[0]),
            BrowseName = new QualifiedName(browseName, 0),
            DisplayName = browseName,
            TypeDefinitionId = VariableTypeIds.PropertyType,
            ReferenceTypeId = ReferenceTypeIds.HasProperty,
            DataType = DataTypeIds.Argument,
            ValueRank = ValueRanks.OneDimension,
            Value = arguments,
        };

        private ServiceResult OnCall(ISystemContext context, MethodState method, NodeId objectId, IList<object> inputArguments, IList<object> outputArguments)
        {
            var input = inputArguments.Count > 0 && inputArguments[0] is string[] texts && texts.Length > 0 ? texts[0] : "";
            switch (method.BrowseName.Name)
            {
                case "Inject":
                case "Update":
                    return Inject(input, outputArguments);
                case "Delete":
                    outputArguments[0] = true;
                    return ServiceResult.Good;
                case "Register":
                case "Resolve":
                    return StatusCodes.BadNotSupported;
                case "RegisterQuery":
                    outputArguments[0] = "{\"QueryID\":\"QueryClient1\",\"QueryResultID\":\"ns=0;i=2255\",\"Added\":[],\"Removed\":[]}";
                    outputArguments[1] = "ns=0;i=2253";
                    return ServiceResult.Good;
                case "UnregisterQuery":
                    outputArguments[0] = true;
                    return ServiceResult.Good;
                case "ResolveQuery":
                    outputArguments[0] = "{\"VariablesHeader\":[\"?SIGNAL\"],\"Results\":[],\"Count\":0,\"IsReadOnly\":false}";
                    return ServiceResult.Good;
                default:
                    outputArguments[0] = "{\"DDHubIndividuals\":[],\"DDHubSentences\":[]}";
                    return ServiceResult.Good;
            }
        }

        private ServiceResult Inject(string manifestJson, IList<object> outputArguments)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(manifestJson);
            }
            catch (JsonException)
            {
                return StatusCodes.BadInvalidArgument;
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Provider", out var providerElement) || !providerElement.TryGetProperty("Name", out var nameElement) || string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    return StatusCodes.BadInvalidArgument;
                }
                var provider = nameElement.GetString()!;
                var variables = root.TryGetProperty("ProvidedVariables", out var list) ? list.EnumerateArray().Select(v => v.GetProperty("VariableID").GetString()!).ToList() : [];
                ushort index;
                lock (Lock)
                {
                    index = Claim($"http://ddhub.no/{provider}/Variables/");
                    var folder = new FolderState(_providers) { NodeId = new NodeId(provider, index), BrowseName = new QualifiedName(provider, index), DisplayName = provider, TypeDefinitionId = ObjectTypeIds.FolderType, ReferenceTypeId = ReferenceTypeIds.Organizes, EventNotifier = EventNotifiers.None };
                    _providers.AddChild(folder);
                    AddPredefinedNode(SystemContext, folder);
                    foreach (var id in variables)
                    {
                        AddVariable(folder, index, $"{provider}.{id}", id, DataTypeIds.Double, 0.0);
                        if (flaw != Flaw.NoValidityBooleans)
                        {
                            AddVariable(folder, index, $"{provider}.{id}_validitySignal", id + "_validitySignal", DataTypeIds.Boolean, false);
                        }
                    }
                }
                var mappings = string.Join(",", variables.Select(id => $"{{\"ManifestItemID\":\"{id}\",\"InjectedID\":{{\"NameSpaceIndex\":{index.ToString(CultureInfo.InvariantCulture)},\"ID\":\"{provider}.{id}\"}}}}"));
                outputArguments[0] = $"{{\"Success\":true,\"InjectedVariableNamespace\":null,\"InjectedNodesNamespace\":\"http://ddhub.no/{provider}/\",\"ProvidedVariablesNamespace\":\"http://ddhub.no/{provider}/Variables/\",\"InjectedVariables\":null,\"InjectedNodes\":[],\"ProvidedVariables\":[{mappings}]}}";
                return ServiceResult.Good;
            }
        }

        private void AddVariable(NodeState parent, ushort index, string nodeId, string name, NodeId dataType, object value)
        {
            var variable = new BaseDataVariableState(parent)
            {
                NodeId = new NodeId(nodeId, index),
                BrowseName = new QualifiedName(name, index),
                DisplayName = name,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                DataType = dataType,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentReadOrWrite,
                UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                Value = value,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow,
            };
            parent.AddChild(variable);
            AddPredefinedNode(SystemContext, variable);
        }

        private ushort Claim(string uri)
        {
            var index = Server.NamespaceUris.GetIndexOrAppend(uri);
            if (_claimedIndexes.Add(index))
            {
                _claimedUris.Add(uri);
                SetNamespaces([.. _claimedUris]);
                SetNamespaceIndexes([.. _claimedIndexes]);
                Server.NodeManager.RegisterNamespaceManager(uri, this);
            }
            return index;
        }
    }
}
