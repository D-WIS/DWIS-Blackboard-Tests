----------------------------- MODULE Queries -----------------------------
(***************************************************************************)
(* The query lifecycle of the DWIS Blackboard Rig Edition (specification    *)
(* 3.5, R-QR-1 to R-QR-3; decision D-41): registration per query text with *)
(* owners (sessions), release on session close, a first answer that is a  *)
(* full snapshot, and a round of every registered query after each change *)
(* to the live description, with the diffs delivered to the owners.        *)
(*                                                                         *)
(* To stay small, every query answers the same thing: the set "data" (the  *)
(* rows of the live description). A round evaluates the data as it was    *)
(* when the round started; a change during the round leaves "dirty" set,  *)
(* so that another round follows. A round can fail: the stored answers     *)
(* stay, and the service re-evaluates at the next change (here: at once).  *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS
  Sessions, \* the sessions of the real-time process, e.g. {s1, s2}
  Queries,  \* query texts, e.g. {q1, q2}
  Rows,     \* the rows the description can answer, e.g. {r1, r2}
  None      \* a model value: no entry

VARIABLES
  open,      \* the sessions that are open: SUBSET Sessions
  owners,    \* the owners of each query: query -> SUBSET Sessions (an entry exists while it has owners)
  stored,    \* the stored answer of each query: query -> SUBSET Rows, or None when there is no entry
  view,      \* what each owner holds after its snapshot and the diffs: session -> query -> SUBSET Rows
  data,      \* the answer the live description gives now
  dirty,     \* the description changed since the last round started
  round,     \* "idle", or "running" with the data the round evaluates
  roundData, \* the data the running round evaluates
  waiting    \* first answers that wait for the running round: SUBSET (Sessions \X Queries)

vars == <<open, owners, stored, view, data, dirty, round, roundData, waiting>>

Registered == {q \in Queries : owners[q] # {}}

TypeOK ==
  /\ open \subseteq Sessions
  /\ owners \in [Queries -> SUBSET Sessions]
  /\ stored \in [Queries -> (SUBSET Rows) \cup {None}]
  /\ view \in [Sessions -> [Queries -> SUBSET Rows]]
  /\ data \subseteq Rows
  /\ dirty \in BOOLEAN
  /\ round \in {"idle", "running"}
  /\ roundData \subseteq Rows
  /\ waiting \subseteq (Sessions \X Queries)

Init ==
  /\ open = {}
  /\ owners = [q \in Queries |-> {}]
  /\ stored = [q \in Queries |-> None]
  /\ view = [s \in Sessions |-> [q \in Queries |-> {}]]
  /\ data = {}
  /\ dirty = FALSE
  /\ round = "idle"
  /\ roundData = {}
  /\ waiting = {}

-----------------------------------------------------------------------------
(* Sessions *)

Open(s) ==
  /\ s \notin open
  /\ open' = open \cup {s}
  /\ UNCHANGED <<owners, stored, view, data, dirty, round, roundData, waiting>>

(* A session closes: it loses every ownership; a query with no owner left loses its entry. *)
Close(s) ==
  /\ s \in open
  /\ open' = open \ {s}
  /\ owners' = [q \in Queries |-> owners[q] \ {s}]
  /\ stored' = [q \in Queries |-> IF owners[q] \ {s} = {} THEN None ELSE stored[q]]
  /\ waiting' = {w \in waiting : w[1] # s}
  /\ UNCHANGED <<view, data, dirty, round, roundData>>

(* RegisterQuery while no round runs: the session becomes an owner (the same text registered by
   another session is the same entry) and gets the full answer: the stored one, or a fresh one when the
   entry had none. TLC found why it must be the stored one: a fresh snapshot next to an older stored
   answer left the new owner with a row that no later diff ever removed. The pending diff of the other
   owners is not consumed either way. *)
Register(s, q) ==
  /\ s \in open
  /\ round = "idle"
  /\ owners' = [owners EXCEPT ![q] = @ \cup {s}]
  /\ stored' = [stored EXCEPT ![q] = IF @ = None THEN data ELSE @]
  /\ view' = [view EXCEPT ![s][q] = IF stored[q] = None THEN data ELSE stored[q]]
  /\ UNCHANGED <<open, data, dirty, round, roundData, waiting>>

(* RegisterQuery while a round runs: the first answer waits for the round (one evaluation at a
   time), so that it is never older than the answer the round stores. *)
RegisterWaits(s, q) ==
  /\ s \in open
  /\ round = "running"
  /\ <<s, q>> \notin waiting
  /\ waiting' = waiting \cup {<<s, q>>}
  /\ UNCHANGED <<open, owners, stored, view, data, dirty, round, roundData>>

(* UnregisterQuery: one owner lets go; the entry lives on for the others. *)
Unregister(s, q) ==
  /\ s \in owners[q]
  /\ owners' = [owners EXCEPT ![q] = @ \ {s}]
  /\ stored' = [stored EXCEPT ![q] = IF owners[q] \ {s} = {} THEN None ELSE @]
  /\ UNCHANGED <<open, view, data, dirty, round, roundData, waiting>>

-----------------------------------------------------------------------------
(* The live description and the rounds *)

(* A commit, a remove or a re-derive changes what the queries answer. *)
Change(d) ==
  /\ d # data
  /\ data' = d
  /\ dirty' = TRUE
  /\ UNCHANGED <<open, owners, stored, view, round, roundData, waiting>>

(* A round starts after a change, when something is registered. *)
RoundStart ==
  /\ round = "idle"
  /\ dirty
  /\ Registered # {}
  /\ round' = "running"
  /\ roundData' = data
  /\ dirty' = FALSE
  /\ UNCHANGED <<open, owners, stored, view, data, waiting>>

(* The round ends: every registered query stores the round's answer and every owner gets the diff
   (rows added, rows removed), so every owner holds exactly the stored answer. The first answers
   that waited are served now with the answer just stored (fresh when the query had no entry); if the
   data moved on during the round, dirty is still set and another round follows. *)
RoundEnd ==
  /\ round = "running"
  /\ round' = "idle"
  /\ stored' = [q \in Queries |->
       IF owners[q] # {} THEN roundData
       ELSE IF \E s \in open : <<s, q>> \in waiting THEN data
       ELSE None]
  /\ view' = [s \in Sessions |-> [q \in Queries |->
       IF s \in owners[q] THEN (view[s][q] \cup (roundData \ stored[q])) \ (stored[q] \ roundData)
       ELSE IF <<s, q>> \in waiting THEN (IF owners[q] # {} THEN roundData ELSE data)
       ELSE view[s][q]]]
  /\ owners' = [q \in Queries |-> owners[q] \cup {s \in open : <<s, q>> \in waiting}]
  /\ waiting' = {}
  /\ UNCHANGED <<open, data, dirty, roundData>>

(* The round fails (the store did not answer): the stored answers and the views stay as they were, and
   the queries are evaluated again at the next change. The first answers that waited get the stored
   answer (fresh when the query had no entry). *)
RoundFails ==
  /\ round = "running"
  /\ round' = "idle"
  /\ dirty' = TRUE
  /\ view' = [s \in Sessions |-> [q \in Queries |->
       IF <<s, q>> \in waiting THEN (IF stored[q] = None THEN data ELSE stored[q]) ELSE view[s][q]]]
  /\ owners' = [q \in Queries |-> owners[q] \cup {s \in open : <<s, q>> \in waiting}]
  /\ stored' = [q \in Queries |-> IF stored[q] = None /\ (\E s \in open : <<s, q>> \in waiting) THEN data ELSE stored[q]]
  /\ waiting' = {}
  /\ UNCHANGED <<open, data, roundData>>

-----------------------------------------------------------------------------
Next ==
  \/ \E s \in Sessions : Open(s) \/ Close(s)
  \/ \E s \in Sessions, q \in Queries : Register(s, q) \/ RegisterWaits(s, q) \/ Unregister(s, q)
  \/ \E d \in SUBSET Rows : Change(d)
  \/ RoundStart \/ RoundEnd \/ RoundFails

Spec == Init /\ [][Next]_vars /\ WF_vars(RoundEnd) /\ WF_vars(RoundStart)

-----------------------------------------------------------------------------
(* Invariants and properties (test plan G, T-TST-2) *)

(* A query has no owner whose session is closed. *)
NoOwnerClosed == \A q \in Queries : owners[q] \subseteq open

(* An entry exists exactly while the query has an owner. *)
EntryIffOwned == \A q \in Queries : (stored[q] # None) <=> (owners[q] # {})

(* No gap and no stale row: when nothing is in flight, every owner holds exactly the stored answer,
   which is the answer of the live description. *)
ViewsConsistentWhenIdle ==
  (round = "idle" /\ ~dirty) =>
    \A q \in Queries : \A s \in owners[q] : view[s][q] = stored[q] /\ stored[q] = data

(* A first answer waits only while a round runs; it is never dropped. *)
WaitingOnlyDuringRound == waiting # {} => round = "running"

(* Liveness: a session that asked for a first answer gets it, or closes. *)
FirstAnswerArrives ==
  \A s \in Sessions, q \in Queries : (<<s, q>> \in waiting) ~> (s \in owners[q] \/ s \notin open)

=============================================================================
