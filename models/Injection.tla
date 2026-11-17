---------------------------- MODULE Injection ----------------------------
(***************************************************************************)
(* The two-phase injection of the DWIS Blackboard Rig Edition               *)
(* (specification section 4; Appendix C.5 staging, C.6 commit, C.7 remove;  *)
(* decision D-41), as the real-time process and the semantic service run    *)
(* it, with crashes and retries.                                            *)
(*                                                                         *)
(* The real-time process prepares a manifest (the service validates and    *)
(* stages it in memory), creates the nodes, then commits; the service      *)
(* writes the store in two updates (the staging graph, then the publish of *)
(* the asserted, inferred and provenance graphs in one request). A staged  *)
(* entry expires after TimeLimit ticks. Either process can crash: the      *)
(* service loses its staging table, the real-time process loses its nodes  *)
(* and rebuilds them from ListManifests. A commit answer can be lost.      *)
(*                                                                         *)
(* Readers see the asserted graph "live": a whole content or none, never a *)
(* mix, because the publish is one update (one action here).              *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS
  Manifests,          \* manifest ids, e.g. {m1, m2}
  Contents,           \* content versions a manifest can have, e.g. {a, b}
  TimeLimit,          \* ticks a staged manifest waits for its commit (60 s in the service)
  CommitRequiresNodes,\* TRUE in the design; FALSE is the deliberately broken variant (T-TST-3)
  None                \* a model value: no entry, no content

VARIABLES
  staged,       \* the staging table of the service: manifest -> [content, ttl], or None
  committing,   \* a commit in progress in the service, after the entry was taken: manifest -> content, or None
  stagingGraph, \* the staging graph in the store (first update of a commit): manifest -> content, or None
  live,         \* the asserted graph in the store, what readers see: manifest -> content, or None
  provenance,   \* the provenance graph in the store: manifest -> BOOLEAN
  nodes,        \* the nodes in the memory of the real-time process: manifest -> BOOLEAN
  wanted,       \* what the real-time process is injecting, until it knows the commit is done: manifest -> content, or None
  rt            \* the real-time process: "running", "down" (crashed), "rebuilding"

vars == <<staged, committing, stagingGraph, live, provenance, nodes, wanted, rt>>

Entry == [content: Contents, ttl: 1..TimeLimit]

TypeOK ==
  /\ staged \in [Manifests -> Entry \cup {None}]
  /\ committing \in [Manifests -> Contents \cup {None}]
  /\ stagingGraph \in [Manifests -> Contents \cup {None}]
  /\ live \in [Manifests -> Contents \cup {None}]
  /\ provenance \in [Manifests -> BOOLEAN]
  /\ nodes \in [Manifests -> BOOLEAN]
  /\ wanted \in [Manifests -> Contents \cup {None}]
  /\ rt \in {"running", "down", "rebuilding"}

Init ==
  /\ staged = [m \in Manifests |-> None]
  /\ committing = [m \in Manifests |-> None]
  /\ stagingGraph = [m \in Manifests |-> None]
  /\ live = [m \in Manifests |-> None]
  /\ provenance = [m \in Manifests |-> FALSE]
  /\ nodes = [m \in Manifests |-> FALSE]
  /\ wanted = [m \in Manifests |-> None]
  /\ rt = "running"

-----------------------------------------------------------------------------
(* The real-time process *)

(* PrepareManifest: the service validates and stages. Content identical to what is live answers
   already_live and stages nothing (C.5), so the real-time process only prepares other content. *)
Prepare(m, c) ==
  /\ rt = "running"
  /\ wanted[m] = None
  /\ live[m] # c
  /\ wanted' = [wanted EXCEPT ![m] = c]
  /\ staged' = [staged EXCEPT ![m] = [content |-> c, ttl |-> TimeLimit]]
  /\ UNCHANGED <<committing, stagingGraph, live, provenance, nodes, rt>>

(* A commit that found nothing staged (expired, or the service restarted): prepare again. *)
PrepareAgain(m) ==
  /\ rt = "running"
  /\ wanted[m] # None
  /\ staged[m] = None
  /\ committing[m] = None
  /\ live[m] # wanted[m]
  /\ staged' = [staged EXCEPT ![m] = [content |-> wanted[m], ttl |-> TimeLimit]]
  /\ UNCHANGED <<committing, stagingGraph, live, provenance, nodes, wanted, rt>>

(* Step 6: the provider folder and the variables are created before the commit, always. *)
CreateNodes(m) ==
  /\ rt = "running"
  /\ wanted[m] # None
  /\ nodes' = [nodes EXCEPT ![m] = TRUE]
  /\ UNCHANGED <<staged, committing, stagingGraph, live, provenance, wanted, rt>>

(* The real-time process gives up a staged manifest: the entry goes, and so do the nodes, unless the
   manifest is live with another content (an aborted update): the nodes of the live version stay. TLC
   found this one: an abort that always deleted the nodes left a live manifest without them. *)
Abort(m) ==
  /\ rt = "running"
  /\ staged[m] # None
  /\ staged' = [staged EXCEPT ![m] = None]
  /\ wanted' = [wanted EXCEPT ![m] = None]
  /\ nodes' = [nodes EXCEPT ![m] = live[m] # None]
  /\ UNCHANGED <<committing, stagingGraph, live, provenance, rt>>

(* A commit answer that was lost, or a retry after a lost answer: ListManifests says it is live. *)
SeeItIsLive(m) ==
  /\ rt = "running"
  /\ wanted[m] # None
  /\ live[m] = wanted[m]
  /\ wanted' = [wanted EXCEPT ![m] = None]
  /\ UNCHANGED <<staged, committing, stagingGraph, live, provenance, nodes, rt>>

(* RemoveManifest: the graphs and the nodes go (C.7). *)
Remove(m) ==
  /\ rt = "running"
  /\ wanted[m] = None
  /\ live[m] # None
  /\ live' = [live EXCEPT ![m] = None]
  /\ provenance' = [provenance EXCEPT ![m] = FALSE]
  /\ stagingGraph' = [stagingGraph EXCEPT ![m] = None]
  /\ nodes' = [nodes EXCEPT ![m] = FALSE]
  /\ UNCHANGED <<staged, committing, wanted, rt>>

(* The real-time process crashes: its nodes and its in-flight injections are lost. Its gRPC calls are
   cancelled with the connection, so the service abandons the commits in progress before their publish
   (a leftover staging graph is dropped by the next commit of the id). TLC found the need for this rule:
   a commit published after the rebuild from ListManifests left a live manifest without nodes. *)
RtCrash ==
  /\ rt = "running"
  /\ rt' = "down"
  /\ nodes' = [m \in Manifests |-> FALSE]
  /\ wanted' = [m \in Manifests |-> None]
  /\ committing' = [m \in Manifests |-> None]
  /\ UNCHANGED <<staged, stagingGraph, live, provenance>>

RtRestart ==
  /\ rt = "down"
  /\ rt' = "rebuilding"
  /\ UNCHANGED <<staged, committing, stagingGraph, live, provenance, nodes, wanted>>

(* The rebuild from ListManifests (R-SEM-7): nodes for every live manifest, none for anything else. *)
RtRebuild ==
  /\ rt = "rebuilding"
  /\ rt' = "running"
  /\ nodes' = [m \in Manifests |-> live[m] # None]
  /\ UNCHANGED <<staged, committing, stagingGraph, live, provenance, wanted>>

-----------------------------------------------------------------------------
(* The semantic service and the store *)

(* CommitManifest, first update: the entry is taken out of the table; the previous graphs of the id are
   dropped and the new asserted triples go to the staging graph, where the inference view reasons over
   them. A reader sees no manifest from here until the publish. *)
CommitWriteStaging(m) ==
  /\ rt = "running"
  /\ staged[m] # None
  /\ wanted[m] = staged[m].content   \* the real-time process commits what it is injecting; an entry nobody wants expires
  /\ committing[m] = None
  /\ (CommitRequiresNodes => nodes[m])
  /\ committing' = [committing EXCEPT ![m] = staged[m].content]
  /\ staged' = [staged EXCEPT ![m] = None]
  /\ stagingGraph' = [stagingGraph EXCEPT ![m] = staged[m].content]
  /\ live' = [live EXCEPT ![m] = None]
  /\ provenance' = [provenance EXCEPT ![m] = FALSE]
  /\ UNCHANGED <<nodes, wanted, rt>>

(* CommitManifest, last update: the staging graph becomes the asserted graph and the inferred and
   provenance graphs land with it, in one request. The answer reaches the real-time process... *)
CommitPublish(m) ==
  /\ committing[m] # None
  /\ stagingGraph[m] = committing[m]
  /\ live' = [live EXCEPT ![m] = committing[m]]
  /\ provenance' = [provenance EXCEPT ![m] = TRUE]
  /\ stagingGraph' = [stagingGraph EXCEPT ![m] = None]
  /\ committing' = [committing EXCEPT ![m] = None]
  /\ wanted' = [wanted EXCEPT ![m] = IF rt = "running" THEN None ELSE wanted[m]]
  /\ UNCHANGED <<staged, nodes, rt>>

(* ... or is lost: the manifest is live, the real-time process does not know yet. *)
CommitPublishAnswerLost(m) ==
  /\ committing[m] # None
  /\ stagingGraph[m] = committing[m]
  /\ live' = [live EXCEPT ![m] = committing[m]]
  /\ provenance' = [provenance EXCEPT ![m] = TRUE]
  /\ stagingGraph' = [stagingGraph EXCEPT ![m] = None]
  /\ committing' = [committing EXCEPT ![m] = None]
  /\ UNCHANGED <<staged, nodes, wanted, rt>>

(* Time passes: every staged entry ages; one that reaches the limit is dropped (C.5). *)
Tick ==
  /\ \E m \in Manifests : staged[m] # None
  /\ staged' = [m \in Manifests |->
       IF staged[m] = None THEN None
       ELSE IF staged[m].ttl = 1 THEN None
       ELSE [staged[m] EXCEPT !.ttl = @ - 1]]
  /\ UNCHANGED <<committing, stagingGraph, live, provenance, nodes, wanted, rt>>

(* The service crashes: the staging table and the commits in progress are lost; the store keeps
   what was written (a leftover staging graph is dropped by the next commit of the id). *)
ServiceCrash ==
  /\ \E m \in Manifests : staged[m] # None \/ committing[m] # None
  /\ staged' = [m \in Manifests |-> None]
  /\ committing' = [m \in Manifests |-> None]
  /\ UNCHANGED <<stagingGraph, live, provenance, nodes, wanted, rt>>

-----------------------------------------------------------------------------
Next ==
  \/ \E m \in Manifests, c \in Contents : Prepare(m, c)
  \/ \E m \in Manifests : PrepareAgain(m) \/ CreateNodes(m) \/ Abort(m) \/ SeeItIsLive(m) \/ Remove(m)
  \/ \E m \in Manifests : CommitWriteStaging(m) \/ CommitPublish(m) \/ CommitPublishAnswerLost(m)
  \/ Tick \/ ServiceCrash \/ RtCrash \/ RtRestart \/ RtRebuild

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
(* Invariants (test plan G, T-TST-1) *)

(* No committed manifest without provenance: the publish writes both in one request. *)
NoLiveWithoutProvenance == \A m \in Manifests : live[m] # None => provenance[m]

(* A manifest live in the store has its nodes, or a rebuild is pending (the real-time process is down
   or rebuilding). The broken variant (commit before node creation) violates this. *)
LiveHasNodesOrRebuild == rt = "running" => \A m \in Manifests : live[m] # None => nodes[m]

(* Nodes exist only for a manifest that is live or being injected: no orphan after an abort, a
   remove or a rebuild. *)
NoOrphanNodes == rt = "running" => \A m \in Manifests : nodes[m] => (live[m] # None \/ wanted[m] # None)

(* Two prepares of the same content never give two live copies: content already live is never staged
   (C.5 idempotence), and the store holds one asserted graph per id. *)
NoStagedCopyOfLive == \A m \in Manifests : staged[m] # None => staged[m].content # live[m]

(* An expired entry is never committed: an entry that reached the limit is gone before any commit
   can take it (CommitWriteStaging needs the entry). *)
NoExpiredEntry == \A m \in Manifests : staged[m] # None => staged[m].ttl >= 1

(* A reader sees a whole content or none, never a mix: live holds one content, and the only action
   that sets it is the one-request publish. *)
WholeOrNone == \A m \in Manifests : live[m] \in Contents \cup {None}

=============================================================================
