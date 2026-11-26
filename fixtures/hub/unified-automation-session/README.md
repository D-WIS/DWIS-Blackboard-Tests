# A recorded Unified Automation session

The fixture of T-HUB-3 (M5.4, Tests #34; requirement R-HUB-2: the serialisation is not vendor-locked). A session of the old server, which is built on the Unified Automation OPC UA stack, recorded through the old DWIS hub by the old recorder, both unchanged. The replication test (`tests/DWIS.Blackboard.Compatibility.Tests/ReplicationTests.cs`) replays it into the hub of the `hub` profile, as the old player would, and checks what the Rig Edition real-time process, built on the OPC Foundation stack, made of it.

## Files

The old recorder's own format (`DWIS.Server.Recording.Base` in the old server repository), unchanged:

- `rtData`: the index, JSON: the chunk files with the first and last time of each.
- `rtData0`: the only chunk, MessagePack: the messages the recorder saw on its hub group, with the time it saw each. One manifest message (the manifest below as the old API's JSON) and 21 value messages. A value message is the hub's `RealTimeDataDTO` without its sender: the node id array and the data value array **in OPC UA binary as the Unified Automation stack encoded them**, the attribute ids and the sender's namespace table.

`Recording.cs` next to the tests reads both files.

## What it holds

One manifest, provider `UaSession` (company D-WIS), "Unified Automation session": five provided variables and two data points.

| Variable | Type | Values recorded | Last value, source timestamp |
|---|---|---|---|
| `SPP` | double | 21 (100 + batch; the closing batch 999) | 999, 2026-11-26T12:00:21Z |
| `WOB` | double | 20 (0.5 × batch) | 10, 2026-11-26T12:00:20Z |
| `pumpOn` | bool | 20 (true on even batches) | true, 2026-11-26T12:00:20Z |
| `rigName` | string | 20 (`rig-{batch}`) | `rig-20`, 2026-11-26T12:00:20Z |
| `stage` | int | 20 (the batch number) | 20, 2026-11-26T12:00:20Z |

The source timestamps are the ones the writer gave: 2026-11-26T12:00:00Z plus the batch number in seconds. The namespaces of the sender's table: the old server's nine, `http://ddhub.no/UaSession/Variables/` at index 6.

## How it was made

26 November 2026, on the owner's laptop, with the three old images from Docker Hub:

| Image | Digest |
|---|---|
| `digiwells/ddhubserver:latest` (the old server, Unified Automation stack) | `sha256:6cf168ff309440a1b2a03f3a9871575d15981f45199eb2c344e772d621a2b187` |
| `digiwells/dwissignalr:latest` (the old hub) | `sha256:c60ff8a3d425f849e68a3f26b03b51f9a06c79cc19ed4e55cc5831a57abebf7a` |
| `digiwells/dwis-blackboard-recorder:latest` (the old recorder) | `sha256:2f21767c33d51958fde3dda8a6520b4ec993cddb5c4392f3d80255c0c38a9ef6` |

1. A compose stack of the three, in this order: the hub (`--path /blackboard/applications`), the recorder (`--hubURL http://hub:8080/blackboard/applications --hubGroup default --output-folder /recording`, a bind-mounted folder, `stdin_open` and `tty` since it blocks on a console read), then the old server (`--useHub --hubURL http://hub:8080/blackboard/applications --hubGroup default`), as the Yggdrasil replay project does it. The recorder must be in the group before the manifest is injected: it records only what it sees.
2. A small console with the unchanged C# client 2.2.56 (`DWIS.Client.ReferenceImplementation.OPCFoundation`) against the old server: `Inject` of the manifest above, then twenty `UpdateProvidedVariables` of the five variables, one every 500 ms, with the source timestamps above.
3. The recorder writes a chunk only once it spans more than five minutes (its `ChunkDuration`), and the image's console does not flush on stop: the console held for 320 s and wrote one closing batch (`SPP` 999), which made the recorder write `rtData0` and the index. Nothing recorded after that would have been kept.

To record again: the same three images, the steps above; copy `rtData` and `rtData0` here. A recording from a rig (the Yggdrasil replay project's, for instance) replays the same way with `DWIS_HUB_RECORDING` pointing at its folder; the test checks the last recorded value of every variable, whatever the recording.
