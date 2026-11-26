using System.Text.Json;
using DWIS.API.DTO;
using MessagePack;
using Opc.Ua;

namespace DWIS.Blackboard.Compatibility.Tests;

/// <summary>
/// A recording of the old recorder (<c>DWIS.Server.Recording.Recorder</c> of the old server repository, image
/// <c>digiwells/dwis-blackboard-recorder</c>): the index file <c>rtData</c> (JSON, the list of the chunks with their
/// first and last times) and the chunks <c>rtData0</c>, <c>rtData1</c>, … (MessagePack: the value messages and the
/// manifest messages the recorder saw on its hub group, with the time it saw each). The value messages carry the
/// sender's bytes as they went over the hub: a node id array and a data value array in OPC UA binary, encoded by
/// the sender's stack (the old server encodes with the Unified Automation stack). The manifests are kept as the old
/// API's JSON. The classes below are the recorder's, property for property, so that its files read as they are.
/// </summary>
public sealed class Recording
{
    /// <summary>The value messages in the order they were recorded, with the time the recorder saw each.</summary>
    public List<(DateTime At, RecordedValues Values)> ValueMessages { get; } = [];

    /// <summary>The manifest messages in the order they were recorded; each holds one or more manifests as JSON.</summary>
    public List<(DateTime At, string[] ManifestJsons)> ManifestMessages { get; } = [];

    /// <summary>Reads a recording folder: the index, then every chunk it lists.</summary>
    public static Recording Load(string folder)
    {
        var index = JsonSerializer.Deserialize<RecordMetaInfo>(File.ReadAllText(Path.Combine(folder, "rtData")))
            ?? throw new InvalidOperationException($"{folder} has no readable index file rtData");
        var recording = new Recording();
        foreach (var info in index.DataInfos)
        {
            var chunk = MessagePackSerializer.Deserialize<RecordData>(File.ReadAllBytes(Path.Combine(folder, info.FileName)));
            for (var i = 0; i < chunk.RealTimeDatas.Count; i++)
            {
                recording.ValueMessages.Add((chunk.RealTimeDataTimes[i], chunk.RealTimeDatas[i]));
            }
            for (var i = 0; i < chunk.ManifestFiles.Count; i++)
            {
                recording.ManifestMessages.Add((chunk.ManifestsTimes[i], chunk.ManifestFiles[i]));
            }
        }
        return recording;
    }

    /// <summary>The manifests of the recording, in order, as the old API's type (what the old player sends).</summary>
    public List<ManifestFile> Manifests() => ManifestMessages.SelectMany(m => m.ManifestJsons).Select(ManifestFile.FromJsonString).ToList();

    /// <summary>
    /// The last recorded value of every variable (attribute Value), with its source timestamp, decoded as the
    /// real-time process decodes a batch: the sender's namespace table, then the OPC Foundation binary decoder on
    /// the node id array and the data value array (R-HUB-2: what the Unified Automation stack encoded).
    /// </summary>
    public Dictionary<(string Namespace, string Id), (object? Value, DateTime SourceTimestamp)> LastValues()
    {
        var last = new Dictionary<(string, string), (object?, DateTime)>();
        foreach (var (_, message) in ValueMessages)
        {
            var table = new NamespaceTable();
            table.Update(message.Namespaces);
            var context = new ServiceMessageContext { NamespaceUris = table };
            NodeIdCollection ids;
            using (var decoder = new BinaryDecoder(message.NodeIDs, context))
            {
                ids = decoder.ReadNodeIdArray("n");
            }
            DataValueCollection values;
            using (var decoder = new BinaryDecoder(message.Values, context))
            {
                values = decoder.ReadDataValueArray("v");
            }
            for (var i = 0; i < ids.Count && i < values.Count; i++)
            {
                if (i < message.Attributes.Length && message.Attributes[i] != Attributes.Value)
                {
                    continue;
                }
                var ns = ids[i].NamespaceIndex < message.Namespaces.Length ? message.Namespaces[ids[i].NamespaceIndex] : "";
                last[(ns, ids[i].Identifier?.ToString() ?? "")] = (values[i].Value, values[i].SourceTimestamp);
            }
        }
        return last;
    }
}

/// <summary>One chunk file of a recording (the recorder's <c>RecordData</c>).</summary>
[MessagePackObject]
public sealed class RecordData
{
    [Key(0)] public List<DateTime> RealTimeDataTimes { get; set; } = [];
    [Key(1)] public List<RecordedValues> RealTimeDatas { get; set; } = [];
    [Key(2)] public List<DateTime> ManifestsTimes { get; set; } = [];
    [Key(3)] public List<string[]> ManifestFiles { get; set; } = [];
}

/// <summary>A recorded batch of values (the recorder's <c>RealTimeDataDTOMP</c>): the hub message without its sender.</summary>
[MessagePackObject]
public sealed class RecordedValues
{
    [Key(0)] public byte[] NodeIDs { get; set; } = [];
    [Key(1)] public uint[] Attributes { get; set; } = [];
    [Key(2)] public byte[] Values { get; set; } = [];
    [Key(3)] public string[] Namespaces { get; set; } = [];
}

/// <summary>One line of the index file.</summary>
public sealed class RecordDataInfo
{
    public string FileName { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
}

/// <summary>The index file <c>rtData</c>.</summary>
public sealed class RecordMetaInfo
{
    public string? RootFolder { get; set; }
    public List<RecordDataInfo> DataInfos { get; set; } = [];
}

/// <summary>A batch of values as the hub carries it (<c>DWIS.SignalR.DTO.RealTimeDataDTO</c>, by property name).</summary>
public sealed class RealTimeDataDTO
{
    public Guid Sender { get; set; }
    public byte[]? NodeIDs { get; set; }
    public uint[]? Attributes { get; set; }
    public byte[]? Values { get; set; }
    public string[]? Namespaces { get; set; }
}

/// <summary>Manifests as the hub carries them (<c>DWIS.SignalR.DTO.ManifestFileDTO</c>): the old API's type, as the old player sends it.</summary>
public sealed class ManifestFileDTO
{
    public Guid Sender { get; set; }
    public ManifestFile[]? ManifestFiles { get; set; }
}
