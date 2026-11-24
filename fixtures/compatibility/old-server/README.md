# The old server's answers (T-BC-3)

The answers of the previous implementation, `digiwells/ddhubserver:latest` on Docker Hub, to one manifest and one query, recorded once so that the Rig Edition's answers can be compared with them byte for byte (R-BC-1, test plan G.2, test `T_BC_3` of `tests/DWIS.Blackboard.Compatibility.Tests`).

| File | What |
| --- | --- |
| `manifest.json` | The manifest sent to `DDHubServer.Inject`, exactly as the C# client 2.2.x serialises it (`ManifestFile.ToJsonString`): provider `CompatOld`, two provided variables, one data point holding the first. |
| `query.sparql` | The query sent to `DDHubServer.ResolveQuery`: the signal of that data point. |
| `inject-answer.json` | The old server's `ManifestInjectionResult`, byte for byte. |
| `query-answer.json` | The old server's `QueryResult`, byte for byte. |
| `recorded.txt` | Where and when the answers were recorded, and the image's digest. |

**Known differences, taken out before the comparison** (the test says so in its assertions):

- `InjectedNodes`: the old server created an OPC UA node for every injected node and listed it; the Rig Edition creates none (decision D-21, R-RT-3) and answers an empty list. The list's content is dropped on both sides.
- `NameSpaceIndex` values: a namespace index depends on what was injected before on that server (the old one here was fresh; the stack has the smoke's manifests live), so the numbers are replaced by a placeholder on both sides. The namespace URIs and the node ids stay and must match.

**To record again** (a fresh old server, so that its indexes are the first ones):

```bash
docker run -d --name ddhubserver -p 127.0.0.1:48031:48030 --hostname localhost digiwells/ddhubserver:latest
DWIS_RECORD_OLD_ANSWERS=1 DWIS_REALTIME_ENDPOINT=opc.tcp://127.0.0.1:48031 dotnet test DWIS.Blackboard.Tests.slnx -c Release --filter-method "*T_BC_3*"
docker rm -f ddhubserver
```
