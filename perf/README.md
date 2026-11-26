# The performance suite

M5.5 (Tests #35), requirement R-RT-7, test plan T-RT-7: the write latency of the Rig Edition real-time process under load must be no worse than the old implementation's semantic-worker branch (p99 under 10 ms, maximum under 50 ms) and show no pause caused by reasoning. The old NBomber suite of the old server repository (`perf-tests/DWIS.Blackboard.PerfTests`, which started hub and server processes itself) re-pointed at the compose stack: the stack is up, the suite only loads it.

## What it does

- **Writers.** `--writers` (5) clients of the unchanged C# client 2.2.56, one provider each (`perf-writer-{i}`, a manifest of `--variables` (3) double variables, removed and injected again at the start so that every run begins the same), each writing a fresh random value of every variable `--frequency` (50) times a second, for `--duration` (3 minutes) after `--warmup` (15 s). One NBomber scenario per writer, so each has its own rate and its own line in NBomber's report.
- **Reader.** A plain session of the OPC Foundation stack reads every writer's variables in one call, `--reads` (10) times a second: what a consumer sees while the writers write.
- **Interference** (`--interference true`). After `--interference-start` (30) s of baseline, a client of its own registers `--interference-queries` (5) SPARQL queries, injects `--interference-manifests` (30) small distinct manifests one every `--interference-interval-ms` (1000), the way the DWIS modules do at a cold start (three variables, six nodes, references of the three kinds the semantic service reasons about), then removes them at the same pace (`--interference-remove`, true) and settles `--interference-settle` (30) s. Each manifest goes through the two-phase injection (the service validates, stores and reasons; the process adds the nodes) and makes the service answer the registered queries again. The phases mark the ledger.
- **Report.** `report.txt` and `report.json` under `--report` (`TestResults/perf`), NBomber's HTML and text next to them. For every writer and for the reads: count, failures, p50, p95, p99, max and the samples over 20 ms, per phase (baseline, burst, remove, settle, after) and overall; the slow samples per 10 s; the interference's counts.
- **Thresholds.** `--p99-ms` (10) and `--max-ms` (50) for every writer over every phase after the warm-up; a missed threshold, a failed write or a failed interference exits with 1 (`--fail false` reports only).

The suite times every call itself, around the client call, as the old suite did: NBomber's own statistics cover the whole run and cannot split it by phase. A high thread pool minimum keeps the measurement about the server (a blocking OPC UA call from an async scenario waits for a pool thread otherwise, which looks like a server stall). The garbage collector of the process cannot be read from outside its container: a pause shows as slow samples in every writer at once, which the per-phase lines and the 10 s timeline make visible.

## Running it

Against a running stack (`compose/smoke.sh`, or the hub profile):

```
dotnet run --project perf/DWIS.Blackboard.Perf -c Release -- --endpoint opc.tcp://127.0.0.1:48030 --interference true
```

A short check: `--duration 00:00:40 --warmup 00:00:05 --interference-start 5 --interference-manifests 4 --interference-settle 8`. Every option is in `appsettings.json` next to the exe; the command line overrides it.

The merge workflow runs it nightly after the replication tests (the hub role on, as on a rig) and keeps the report 30 days with the other results (`suite-results-{run}`). The thresholds were set for a laptop: a runner that misses them by a little is a number to read, not a bug.

## Numbers

26 November 2026, the owner's laptop (14 cores, Docker Desktop), the hub profile up (the hub role on, a second Blackboard receiving every manifest), the edge images of that day. Five writers at 50 writes/s each (three variables), a reader at 10 reads/s, three minutes; the interference at 30 s: 5 registered queries, 30 manifests one per second, removed one per second, settle 30 s. Writer 0 of five (the five are alike within a millisecond); latency in ms.

| Run | Phase | p50 | p95 | p99 | max | slow (> 20 ms) |
|---|---|---|---|---|---|---|
| 1, providers new to the process | baseline | 2.4 | 3.9 | 7.6 | 15.9 | 0 |
| | **burst** | 3.3 | 30.2 | **80.0** | **131.0** | **166** |
| | remove | 2.2 | 4.0 | 9.7 | 27.2 | 1 |
| | settle | 2.1 | 3.4 | 4.0 | 5.6 | 0 |
| | after | 2.3 | 3.9 | 15.0 | 49.0 | 21 |
| 2, the same providers again | baseline | 2.3 | 4.0 | 15.2 | 51.2 | 15 (all in its first 10 s) |
| | burst | 2.1 | 3.6 | 5.1 | 16.2 | 0 |
| | remove | 2.2 | 3.5 | 4.3 | 12.3 | 0 |
| | settle | 2.1 | 3.4 | 4.1 | 8.4 | 0 |
| | after | 2.1 | 3.6 | 4.4 | 7.9 | 0 |
| 3, thirty fresh providers (`--interference-first 100`), warm-up 30 s | baseline | 2.3 | 3.8 | 5.1 | 8.4 | 0 |
| | **burst** | 2.8 | 7.8 | **37.3** | **108.0** | **32** (all in its first 10 s) |
| | remove | 2.1 | 3.5 | 4.1 | 5.0 | 0 |
| | settle | 2.1 | 3.4 | 4.7 | 14.1 | 0 |
| | after | 2.2 | 4.0 | 6.9 | 85.9 | 12 (one cluster) |

The reads behave as the writes. No write or read failed; every manifest was accepted (the semantic service's commit took 250 to 330 ms each, with the five queries answered again after each) and removed.

What the three runs say:

- **The semantic side does not touch the writes.** In run 2 the burst, the removal and the settle are as quiet as the baseline (p99 4 to 5 ms, max 16 ms) while the service validates, stores, reasons and re-answers the queries thirty times; in runs 1 and 3 the removal and the settle are as quiet. That is R-RT-7's "no pause caused by reasoning": the reasoning runs in other processes.
- **A provider the process has never seen costs the writes.** A burst of thirty new providers has p99 37 to 80 ms and max 108 to 131 ms (32 to 166 slow writes per writer, the amount varying between runs and front-loaded in the first seconds of the burst); the same thirty again (run 2, their namespaces registered since) has p99 5 ms, max 16 ms, none. The process registers a new provider's two namespaces in the OPC UA server under the node manager's lock (`BlackboardNodeManager.ClaimNamespace`, called from `CreateVariable`), and the writes wait for it; removal does not unregister, which is why the second run does not pay. The prime suspect, to be profiled in a follow-up for the real-time process (design note 67); `--interference-first` picks a fresh range of providers to see it again.
- **An unexplained cluster** of a dozen slow writes (max 49 and 86 ms) about 30 s after the last removal, in the two runs with new providers and not in the other; a garbage collection after the burst's allocations would look like that. Noted for the same follow-up.
- **The first seconds after the warm-up are the noisiest** (15 slow writes, max 51 ms in run 2's first 10 s, as the old suite saw on its baseline), which is why the warm-up is 30 s now (run 3's baseline is clean).

Against the old implementation's semantic-worker branch measured on 11 November 2026 with the old suite (one instance, same load shape; `perf-tests/README.md` of the old server repository): OPC Foundation stack, settle phase p99 6.4 ms, max 31 ms; baseline p99 16.4 ms, max 74 ms. The Rig Edition at rest and under the semantic load is at p99 4 to 5 ms, max 8 to 16 ms; the new-provider cost is the one thing above the thresholds.
