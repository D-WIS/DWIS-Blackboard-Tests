# TLA+ models of the two protocols (M3.0, decision D-41)

Two small models, checked with TLC on every pull request (`check.sh`, the "TLC model checks" job) and
written before the gRPC contract and the real-time process so that the code follows a checked design
(test plan G, R-TST-3). They are not the code: each says in a page what the protocol promises, with the
crashes and the retries the code must survive, and TLC tries every order of events on a small instance.

Run them with Java 17 or later (`./models/check.sh`; the TLA+ tools jar is downloaded on first use), or
without Java through a container, from the repository root:

```bash
docker run --rm -v "$PWD/models:/models" -w /models eclipse-temurin:21-jre bash check.sh
```

(On Git Bash for Windows, prefix the command with `MSYS_NO_PATHCONV=1` and write the volume as
`C:/.../models:/models`; put `tla2tools.jar` into `models/` first when the container cannot reach GitHub.)

## Injection.tla: the two-phase injection (specification 4, Appendix C.5 to C.7)

Two processes. The real-time process prepares a manifest (the service validates and stages it in
memory), creates the nodes, then commits; the service writes the store in two updates (the staging
graph, then the publish of the asserted, inferred and provenance graphs in one request). A staged
entry expires after `TimeLimit` ticks. The service can crash (its staging table and its commits in
progress are lost; the store keeps what was written); the real-time process can crash (its nodes and
its in-flight injections are lost; it rebuilds its nodes from `ListManifests`); a commit answer can be
lost (the real-time process then sees from the list that the manifest is live).

`Injection.cfg` (T-TST-1): two manifests, two contents, a limit of two ticks. 4,699 distinct states, a
few seconds. `InjectionBroken.cfg` (T-TST-3): the same with `CommitRequiresNodes = FALSE`, a commit
that no longer waits for the nodes; TLC must reject it, and `check.sh` fails if it does not.

| Invariant | In one line |
|---|---|
| `NoLiveWithoutProvenance` | a manifest live in the store has its provenance: the publish writes both in one request |
| `LiveHasNodesOrRebuild` | while the real-time process runs, every live manifest has its nodes (a crash leaves a rebuild pending) |
| `NoOrphanNodes` | while it runs, nodes exist only for a manifest that is live or being injected: none left by an abort, a remove or a rebuild |
| `NoStagedCopyOfLive` | content already live is never staged (C.5 idempotence), so two prepares of the same content never give two live copies |
| `NoExpiredEntry` | an entry that reached the time limit is gone before any commit can take it |
| `WholeOrNone` | a reader sees a whole content or no manifest, never a mix: the publish is one request |

What TLC found while the model was written, now rules of the design (notes for review of 17 November
2026, for M3.3 and M4):

1. An aborted update must keep the nodes of the live version: aborting while another content of the
   same manifest is live, then deleting the nodes, left a live manifest without nodes.
2. A crash of the real-time process must abandon its commits in progress in the service, before their
   publish: otherwise a commit started before the crash lands after the rebuild from `ListManifests`,
   and the manifest is live without nodes. The gRPC call is cancelled with the connection; the service
   observes the cancellation before the publish (a leftover staging graph is dropped by the next commit
   of the id).
3. A commit belongs to the process that is injecting: after a restart, a staged entry nobody wants
   expires; it is never committed by the rebuilt process.

## Queries.tla: the query lifecycle (specification 3.5, R-QR-1 to R-QR-3)

Sessions open and close; a session registers a query text (the same text is the same entry, with
owners) and gets a full first answer; every change to the live description starts a round that stores
the new answer of every registered query and delivers the diff (rows added, rows removed) to every
owner; a round can fail (the stored answers stay); a first answer asked during a round waits for it.
To stay small, every query answers the same set of rows, `data`.

`Queries.cfg` (T-TST-2): two sessions sharing one query text, two rows. 9,476 distinct states, about half a
minute (a second query text multiplies the owners' views and takes TLC past two million states for
nothing new).

| Invariant or property | In one line |
|---|---|
| `NoOwnerClosed` | a query has no owner whose session is closed |
| `EntryIffOwned` | an entry exists exactly while the query has an owner |
| `ViewsConsistentWhenIdle` | when no round runs and nothing changed since, every owner holds exactly the stored answer, which is the answer of the live description: no gap, no stale row |
| `WaitingOnlyDuringRound` | a first answer waits only while a round runs |
| `FirstAnswerArrives` (liveness) | a session that asked for a first answer gets it, or closes |

What TLC found, now a rule of the design (M3.4 changes the ported `QueryManager` accordingly):

4. A new subscriber gets the stored answer of the query (a fresh one only when the query has none),
   not a fresh evaluation next to an older stored answer: a row that appeared and disappeared between
   the two was never removed from that subscriber's view, since the diffs are computed from the stored
   answer. With the stored answer as the snapshot, every owner always holds exactly what the diffs
   move forward, and the pending diff of the other owners is still not consumed.
