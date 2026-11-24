using System.Diagnostics;
using System.Text.Json;
using DWIS.API.DTO;
using Opc.Ua;

namespace DWIS.Blackboard.Conformance;

/// <summary>One check: its id, name, outcome and the reason when it failed.</summary>
public sealed record Check(string Id, string Name, bool Passed, string Reason, long Ms);

/// <summary>A manifest the suite injects: its JSON, where it came from, and whether the service is expected to refuse it.</summary>
public sealed record ManifestCase(string Name, string Json, bool ExpectRefused);

/// <summary>
/// The checks of the first version (M4.5; Appendix H.5 lists them). Every check is one line of the report; the
/// suite goes on after a failure, except when the object or its methods are not found, since nothing else can run.
/// </summary>
public sealed class Suite
{
    private readonly Blackboard _blackboard;
    private readonly List<ManifestCase> _manifests;
    private readonly List<Check> _checks = [];
    private readonly Action<Check> _report;

    public Suite(Blackboard blackboard, IEnumerable<ManifestCase> manifests, Action<Check> report)
    {
        _blackboard = blackboard;
        _manifests = manifests.ToList();
        _report = report;
    }

    public IReadOnlyList<Check> Checks => _checks;

    /// <summary>The expected declarations of the nine methods (DWISNodeManager.cs:306-363 of the old server): the input names, one output or two.</summary>
    private static readonly (string Name, string[] Inputs, int Outputs)[] Declarations =
    [
        ("Inject", ["ManifestFile"], 1), ("Update", ["ManifestFile"], 1), ("Delete", ["ManifestFile"], 1),
        ("Register", ["AcquisitionFile"], 1), ("Resolve", ["AcquisitionFile"], 1),
        ("RegisterQuery", ["Query"], 2), ("UnregisterQuery", ["Query"], 1), ("ResolveQuery", ["Query"], 1), ("GetResources", [], 1),
    ];

    /// <summary>The suite's own small manifest, injected after a query was registered so that a diff is pushed.</summary>
    public const string OwnProvider = "Conformance";

    public static string OwnManifest => """
        {
          "ManifestName": "Conformance signals",
          "Provider": { "Name": "Conformance", "Company": "D-WIS" },
          "InjectionInformation": { "InjectedNodesNamespaceAlias": "nodes", "ProvidedVariablesNamespaceAlias": "pv", "InjectedVariablesNamespaceAlias": "variables" },
          "ProvidedVariables": [ { "VariableID": "SPP", "DataType": "double", "Rank": 0, "InitialValue": "12.5" } ],
          "InjectedNodes": [ { "UniqueName": "SPPPoint", "BrowseName": "SPPPoint", "DisplayName": "SPPPoint", "TypeDictionaryURI": "DrillingDataPoint" } ],
          "InjectedReferences": [ { "Subject": { "NameSpace": "nodes", "ID": "SPPPoint" }, "VerbURI": "http://ddhub.no/HasDynamicValue", "Object": { "NameSpace": "pv", "ID": "SPP" } } ]
        }
        """;

    private static string OwnQuery => $"PREFIX ddhub: <http://ddhub.no/> SELECT ?signal WHERE {{ <http://ddhub.no/{OwnProvider}/SPPPoint> ddhub:HasDynamicValue ?signal }}";

    /// <summary>Runs every check; true when all passed.</summary>
    public bool Run()
    {
        if (!Record("C-01", "the DDHubServer object is under Objects, found by browse name", () =>
        {
            var reason = _blackboard.FindServerObject();
            return reason is null ? null : reason;
        }))
        {
            return false;
        }
        if (!Record("C-02", "the nine methods are children of the object, declared as the old server declares them", CheckDeclarations))
        {
            return false;
        }

        var injected = new List<(ManifestCase Case, ManifestFile Manifest)>();
        foreach (var manifest in _manifests)
        {
            Record("C-03", $"Inject answers for {manifest.Name}: a ManifestInjectionResult, or BadInvalidArgument when the service refuses it", () =>
            {
                var (status, outputs) = _blackboard.TryCall("Inject", manifest.Json);
                if (manifest.ExpectRefused)
                {
                    return status == StatusCodes.BadInvalidArgument ? null : $"expected BadInvalidArgument, got {StatusCode.LookupSymbolicId(status.Code)}";
                }
                if (status != StatusCodes.Good)
                {
                    return $"Inject answered {StatusCode.LookupSymbolicId(status.Code)}";
                }
                var parsed = ManifestFile.FromJsonString(manifest.Json);
                var reason = CheckInjectionResult(outputs, parsed);
                if (reason is null)
                {
                    injected.Add((manifest, parsed));
                }
                return reason;
            });
        }
        foreach (var (manifestCase, manifest) in injected)
        {
            Record("C-04", $"every provided variable of {manifestCase.Name} exists with its validity boolean", () => CheckVariables(manifest));
        }

        Record("C-05", "ResolveQuery answers the QueryResult shape with http://ddhub.no/ namespaces", () =>
        {
            var (status, outputs) = _blackboard.TryCall("ResolveQuery", "PREFIX ddhub: <http://ddhub.no/> SELECT ?signal WHERE { ?dp ddhub:HasDynamicValue ?signal }");
            if (status != StatusCodes.Good || outputs.Count != 1 || outputs[0] is not string json)
            {
                return $"ResolveQuery answered {StatusCode.LookupSymbolicId(status.Code)} with {outputs.Count} output(s)";
            }
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("VariablesHeader", out var header) || !root.TryGetProperty("Results", out var results))
            {
                return "the answer has no VariablesHeader or no Results";
            }
            if (header.GetArrayLength() != 1 || header[0].GetString() != "?SIGNAL")
            {
                return $"the header is {header}, not [\"?SIGNAL\"] (the old server's naming)";
            }
            var rows = results.EnumerateArray().ToList();
            if (rows.Count == 0 && injected.Count > 0)
            {
                return "no row, although manifests with signals are live";
            }
            foreach (var row in rows)
            {
                foreach (var item in row.GetProperty("Items").EnumerateArray())
                {
                    var ns = item.GetProperty("NameSpace").GetString() ?? "";
                    if (!ns.StartsWith("http://ddhub.no/", StringComparison.Ordinal))
                    {
                        return $"a row names the namespace {ns}, not a legacy http://ddhub.no/ one";
                    }
                }
            }
            return null;
        });

        // The suite's own manifest: registered as a query first, injected next, so that the registration gets a diff.
        NodeId? diffNode = null;
        Record("C-06", "RegisterQuery answers the initial diff and a diff variable the client can read", () =>
        {
            var (status, outputs) = _blackboard.TryCall("RegisterQuery", OwnQuery);
            if (status != StatusCodes.Good || outputs.Count != 2 || outputs[0] is not string json)
            {
                return $"RegisterQuery answered {StatusCode.LookupSymbolicId(status.Code)} with {outputs.Count} output(s)";
            }
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("QueryID", out _) || !root.TryGetProperty("QueryResultID", out var resultId) || !root.TryGetProperty("Added", out var added) || !root.TryGetProperty("Removed", out _))
            {
                return "the initial diff lacks QueryID, QueryResultID, Added or Removed";
            }
            if (added.GetArrayLength() != 0)
            {
                return $"the initial diff has {added.GetArrayLength()} rows before the suite's manifest was injected";
            }
            var node = NodeId.Parse(resultId.GetString());
            var before = _blackboard.Session.ReadValue(node);
            if (!StatusCode.IsGood(before.StatusCode) || before.Value is not string)
            {
                return $"the diff variable {resultId} cannot be read as a string ({StatusCode.LookupSymbolicId(before.StatusCode.Code)})";
            }
            diffNode = node;
            return null;
        });
        var ownInjected = Record("C-03", "Inject answers for the suite's own manifest: a ManifestInjectionResult", () =>
        {
            var (status, outputs) = _blackboard.TryCall("Inject", OwnManifest);
            return status != StatusCodes.Good ? $"Inject answered {StatusCode.LookupSymbolicId(status.Code)}" : CheckInjectionResult(outputs, ManifestFile.FromJsonString(OwnManifest));
        });
        if (ownInjected)
        {
            Record("C-04", "every provided variable of the suite's own manifest exists with its validity boolean", () => CheckVariables(ManifestFile.FromJsonString(OwnManifest)));
        }
        Record("C-06", "the registered query gets a diff with the suite's signal once its manifest is injected", () =>
        {
            if (diffNode is null || !ownInjected)
            {
                return "the registration or the injection failed above";
            }
            var stop = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < stop)
            {
                var value = _blackboard.Session.ReadValue(diffNode).Value as string;
                if (value is not null && value.Contains($"{OwnProvider}.SPP", StringComparison.Ordinal) && value.Contains("\"Added\"", StringComparison.Ordinal))
                {
                    return null;
                }
                Thread.Sleep(500);
            }
            return "no diff with the suite's signal arrived on the diff variable within 60 s";
        });

        Record("C-07", "UnregisterQuery answers true for the registered query", () =>
        {
            var (status, outputs) = _blackboard.TryCall("UnregisterQuery", OwnQuery);
            if (status != StatusCodes.Good || outputs.Count != 1 || outputs[0] is not bool released)
            {
                return $"UnregisterQuery answered {StatusCode.LookupSymbolicId(status.Code)} with {outputs.Count} output(s)";
            }
            return released ? null : "UnregisterQuery answered false for a query this session registered";
        });

        Record("C-08", "Register and Resolve answer BadNotSupported", () =>
        {
            foreach (var method in new[] { "Register", "Resolve" })
            {
                var (status, _) = _blackboard.TryCall(method, "{}");
                if (status != StatusCodes.BadNotSupported)
                {
                    return $"{method} answered {StatusCode.LookupSymbolicId(status.Code)}";
                }
            }
            return null;
        });

        Record("C-09", "GetResources answers the resource file shape", () =>
        {
            var (status, outputs) = _blackboard.TryCall("GetResources");
            if (status != StatusCodes.Good || outputs.Count != 1 || outputs[0] is not string json)
            {
                return $"GetResources answered {StatusCode.LookupSymbolicId(status.Code)} with {outputs.Count} output(s)";
            }
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("DDHubIndividuals", out var i) && i.ValueKind == JsonValueKind.Array
                && document.RootElement.TryGetProperty("DDHubSentences", out var s) && s.ValueKind == JsonValueKind.Array
                ? null : "the answer lacks the DDHubIndividuals or DDHubSentences arrays";
        });

        Record("C-10", "Inject of a text that is not a manifest answers BadInvalidArgument", () =>
        {
            var (status, _) = _blackboard.TryCall("Inject", "this is not JSON");
            return status == StatusCodes.BadInvalidArgument ? null : $"Inject answered {StatusCode.LookupSymbolicId(status.Code)}";
        });

        Record("C-11", "the address space under DDHubServer holds only objects, variables and methods", () =>
        {
            var classes = new HashSet<NodeClass>();
            Walk(_blackboard.ServerObject!, classes, 0);
            var others = classes.Where(c => c is not (NodeClass.Object or NodeClass.Variable or NodeClass.Method)).ToList();
            return others.Count == 0 ? null : "found node classes " + string.Join(", ", others);
        });

        foreach (var (manifestCase, manifest) in injected.Append((new ManifestCase("the suite's own manifest", OwnManifest, false), ManifestFile.FromJsonString(OwnManifest))))
        {
            Record("C-12", $"Delete answers true for {manifestCase.Name} and its variables go", () =>
            {
                var (status, outputs) = _blackboard.TryCall("Delete", manifestCase.Json);
                if (status != StatusCodes.Good || outputs.Count != 1 || outputs[0] is not bool removed)
                {
                    return $"Delete answered {StatusCode.LookupSymbolicId(status.Code)} with {outputs.Count} output(s)";
                }
                if (!removed)
                {
                    return "Delete answered false for a live manifest";
                }
                var first = manifest.ProvidedVariables?.FirstOrDefault();
                if (first is not null && _blackboard.Read($"http://ddhub.no/{manifest.Provider.Name}/Variables/", $"{manifest.Provider.Name}.{first.VariableID}") is not null)
                {
                    return $"the variable {first.VariableID} is still there after the delete";
                }
                return null;
            });
        }

        return _checks.All(c => c.Passed);
    }

    private string? CheckDeclarations()
    {
        var missing = Declarations.Where(d => !_blackboard.Methods.ContainsKey(d.Name)).Select(d => d.Name).ToList();
        if (missing.Count > 0)
        {
            return "methods missing: " + string.Join(", ", missing);
        }
        foreach (var (name, inputs, outputs) in Declarations)
        {
            var node = _blackboard.Id(_blackboard.Methods[name]);
            var properties = _blackboard.Children(node).ToDictionary(r => r.BrowseName.Name, r => r, StringComparer.Ordinal);
            var declaredInputs = Arguments(properties, "InputArguments");
            var declaredOutputs = Arguments(properties, "OutputArguments");
            if (!declaredInputs.Select(a => a.Name).SequenceEqual(inputs))
            {
                return $"{name} declares inputs [{string.Join(", ", declaredInputs.Select(a => a.Name))}], expected [{string.Join(", ", inputs)}]";
            }
            if (declaredInputs.Any(a => a.DataType != DataTypeIds.String || a.ValueRank != ValueRanks.OneDimension))
            {
                return $"{name}: every input must be a one-dimensional string array";
            }
            if (declaredOutputs.Count != outputs)
            {
                return $"{name} declares {declaredOutputs.Count} output(s), expected {outputs}";
            }
        }
        return null;
    }

    private List<Argument> Arguments(Dictionary<string, ReferenceDescription> properties, string name)
    {
        if (!properties.TryGetValue(name, out var reference))
        {
            return [];
        }
        var value = _blackboard.Session.ReadValue(_blackboard.Id(reference)).Value as ExtensionObject[];
        return value?.Select(e => e.Body).OfType<Argument>().ToList() ?? [];
    }

    private static string? CheckInjectionResult(IList<object> outputs, ManifestFile manifest)
    {
        if (outputs.Count != 1 || outputs[0] is not string json)
        {
            return $"Inject answered {outputs.Count} output(s), expected the result JSON";
        }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("Success", out var success) || !success.GetBoolean())
        {
            return "the result says Success false";
        }
        var expectedNamespace = $"http://ddhub.no/{manifest.Provider.Name}/Variables/";
        if (root.GetProperty("ProvidedVariablesNamespace").GetString() != expectedNamespace)
        {
            return $"ProvidedVariablesNamespace is {root.GetProperty("ProvidedVariablesNamespace")}, expected {expectedNamespace}";
        }
        var mapped = root.GetProperty("ProvidedVariables").EnumerateArray().Select(m => m.GetProperty("ManifestItemID").GetString()).ToHashSet(StringComparer.Ordinal);
        var declared = (manifest.ProvidedVariables ?? []).Select(v => v.VariableID).ToList();
        var unmapped = declared.Where(id => !mapped.Contains(id)).ToList();
        return unmapped.Count == 0 ? null : $"{unmapped.Count} of {declared.Count} provided variables have no mapping, for example {unmapped[0]}";
    }

    private string? CheckVariables(ManifestFile manifest)
    {
        var ns = $"http://ddhub.no/{manifest.Provider.Name}/Variables/";
        foreach (var variable in manifest.ProvidedVariables ?? [])
        {
            if (_blackboard.Read(ns, $"{manifest.Provider.Name}.{variable.VariableID}") is null)
            {
                return $"the variable {variable.VariableID} cannot be read in {ns}";
            }
            var flag = variable.VariableID.EndsWith("_validitySignal", StringComparison.Ordinal) ? null : $"{manifest.Provider.Name}.{variable.VariableID}_validitySignal";
            if (flag is not null && _blackboard.Read(ns, flag)?.Value is not bool)
            {
                return $"the validity boolean of {variable.VariableID} cannot be read as a boolean";
            }
        }
        return null;
    }

    private void Walk(NodeId node, HashSet<NodeClass> classes, int depth)
    {
        if (depth > 6)
        {
            return;
        }
        foreach (var reference in _blackboard.Children(node))
        {
            classes.Add(reference.NodeClass);
            if (reference.NodeClass == NodeClass.Object)
            {
                Walk(_blackboard.Id(reference), classes, depth + 1);
            }
        }
    }

    private bool Record(string id, string name, Func<string?> check)
    {
        var watch = Stopwatch.StartNew();
        string? reason;
        try
        {
            reason = check();
        }
        catch (Exception e)
        {
            reason = e.GetType().Name + ": " + e.Message;
        }
        var result = new Check(id, name, reason is null, reason ?? "", watch.ElapsedMilliseconds);
        _checks.Add(result);
        _report(result);
        return result.Passed;
    }
}
