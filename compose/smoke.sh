#!/usr/bin/env bash
# Builds the images, starts the stack, waits for the vocabulary to be loaded and runs the milestone 1
# checks: the store from the host, then the acceptance of roadmap F.3 through the semantic service
# (the SmartConnect manifest accepted, the two queries with either prefix returning the same signal,
# the kilogram copy core-only with one error); then the milestone 2 checks: the inference view
# /dwis-inf (a write through /dwis is seen without a restart, a subclass entailment appears, the
# quarantine stays out, timings and memory noted for decision D-15) and the demo of roadmap F.2 (the
# kilogram report, asserted versus inferred by GRAPH, a vocabulary swap through PUT /vocabulary that
# re-derives the live manifests and is rolled back). From the F.3 queries on, the small manifests of
# M3.F (fixtures/manifests/small/) are live next to the big one, with their times printed. Then the
# milestone 3 demo (M3.5): two watch consoles over gRPC see the diffs of a remove and a re-add, and come
# back after a restart of the service. Used by CI and by hand.
#   ./smoke.sh              keep the stack running afterwards
#   ./smoke.sh --down       stop it and remove the volume at the end
#   ./smoke.sh --no-build   use the images at hand (behind a proxy that breaks image builds)
# Needs curl; no jq (the JSON checks are plain greps on the indented report).
set -euo pipefail
cd "$(dirname "$0")"

down=false
build=true
for arg in "$@"; do
  case "$arg" in
    --down) down=true ;;
    --no-build) build=false ;;
    *) echo "unknown option: $arg"; exit 2 ;;
  esac
done

if $build; then
  docker compose build
  docker compose up -d
else
  docker compose up -d --no-build
fi
# The service depends on the loader having completed, so `up -d` returns once the loader has exited;
# `docker compose wait` would then find no running container (and some versions answer 1), so the
# exit code is read from the container itself.
echo "checking the vocabulary loader"
status="$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose ps -aq vocabulary)")"
docker compose logs --no-log-prefix vocabulary
[ "$status" -eq 0 ] || { echo "vocabulary loader failed with $status"; docker compose logs fuseki | tail -40; exit 1; }

FUSEKI=http://127.0.0.1:3030
SEMANTIC=http://127.0.0.1:8080/api/v1

query() {
  curl -sS --fail -G "${FUSEKI}/dwis/sparql" -H 'Accept: text/csv' --data-urlencode "query=$1" | tail -n 1 | tr -d '\r'
}

# Union default graph: a plain query sees the vocabulary graph.
total="$(query 'SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }')"
echo "union default graph: ${total} triples"
[ "$total" -gt 9000 ] || { echo "expected the vocabulary (more than 9000 triples) in the union"; exit 1; }

# Both prefixes resolve to the same terms once translated (specification 3.6): the SPP class exists
# under d-wis.org and nothing is left under ddhub.no.
spp="$(query 'PREFIX dwis: <http://d-wis.org/> ASK { dwis:SPP ?p ?o }')"
[ "$spp" = "true" ] || { echo "dwis:SPP not found in the vocabulary"; exit 1; }
legacy="$(query 'ASK { ?s ?p ?o . FILTER (STRSTARTS(STR(?s), "http://ddhub.no/")) }')"
[ "$legacy" = "false" ] || { echo "ddhub.no subjects found (T-SEM-4)"; exit 1; }

# The unit and quantity individuals are visible in the union with their classes (decision D-44).
pressure="$(query 'PREFIX dwis: <http://d-wis.org/> PREFIX uq: <http://d-wis.org/UnitAndQuantity/> ASK { uq:PressureDrillingQuantity a dwis:PressureDrillingQuantity . uq:Bar a dwis:Bar }')"
[ "$pressure" = "true" ] || { echo "unit and quantity individuals not found in the union"; exit 1; }

# The quarantine dataset exists and is empty.
quarantine="$(curl -sS --fail -G "${FUSEKI}/dwis-quarantine/sparql" -H 'Accept: text/csv' --data-urlencode 'query=SELECT (COUNT(*) AS ?n) WHERE { GRAPH ?g { ?s ?p ?o } }' | tail -n 1 | tr -d '\r')"
[ "$quarantine" = "0" ] || { echo "quarantine is not empty: ${quarantine}"; exit 1; }
echo "store checks passed"

# The semantic service: healthy once it has read the vocabulary and the units from the store.
echo "waiting for the semantic service"
i=0
until curl -sf "${SEMANTIC}/health" > /dev/null; do
  i=$((i + 1))
  [ "$i" -lt 45 ] || { echo "the semantic service did not become healthy in time"; curl -s "${SEMANTIC}/health" || true; docker compose logs --no-log-prefix semantic | tail -60; exit 1; }
  sleep 2
done
curl -sS --fail "${SEMANTIC}/vocabulary"; echo

# A field of the indented report JSON, by name: "accepted": true, "errors": 1, "manifestId": "x/y".
field() {
  grep -o -m 1 "\"$2\": *[^,}]*" <<< "$1" | sed 's/^[^:]*: *//; s/^"//; s/"$//'
}

host_query() {
  curl -sS --fail -G "${SEMANTIC}/sparql" -H 'Accept: text/csv' --data-urlencode "query=$1" | tail -n +2 | tr -d '\r' | sort
}

inject() {
  curl -sS --fail -X POST "${SEMANTIC}/manifests" -H 'Content-Type: application/json' --data-binary "@$1"
}

# 1. The SmartConnect manifest goes live whole: accepted, no error (the warnings are explained by the policy).
report="$(inject ../fixtures/manifests/smartconnect.manifest.json)"
id="$(field "$report" manifestId)"
echo "injected ${id}: accepted $(field "$report" accepted), errors $(field "$report" errors), warnings $(field "$report" warnings), asserted $(field "$report" asserted) triples"
big_ms=$(( $(field "$report" preprocessingMs) + $(field "$report" commitMs) ))
[ "$(field "$report" accepted)" = "true" ] || { echo "the SmartConnect manifest was not accepted"; printf '%s\n' "$report" | head -60; exit 1; }
[ "$(field "$report" errors)" = "0" ] || { echo "the SmartConnect manifest has errors"; exit 1; }
curl -sS --fail "${SEMANTIC}/manifests" | grep -q "\"${id}\"" || { echo "the manifest is not listed"; exit 1; }

# 2. The two queries of roadmap F.3, one per prefix, return the same signal.
by_class="$(host_query 'PREFIX dwis: <http://d-wis.org/>
SELECT ?signal WHERE { ?dp a dwis:SPP ; dwis:HasDynamicValue ?signal . }')"
by_quantity="$(host_query 'PREFIX ddhub: <http://ddhub.no/>
SELECT ?signal WHERE { ?dp ddhub:IsOfMeasurableQuantity ?q ; ddhub:HasDynamicValue ?signal .
                       ?q a ddhub:PressureDrillingQuantity . ?dp ddhub:IsLocatedAtEquipment ?e . ?e a ddhub:MudStandpipe . }')"
echo "SPP by class: ${by_class}"
echo "SPP by quantity and place: ${by_quantity}"
[ -n "$by_class" ] || { echo "the first F.3 query returned nothing"; exit 1; }
[ "$(printf '%s\n' "$by_class" | wc -l)" -eq 1 ] || { echo "the first F.3 query returned more than one signal"; exit 1; }
[ "$by_class" = "$by_quantity" ] || { echo "the two F.3 queries disagree"; exit 1; }
legacy="$(query 'ASK { ?s ?p ?o . FILTER (STRSTARTS(STR(?s), "http://ddhub.no/") || STRSTARTS(STR(?p), "http://ddhub.no/") || (isIRI(?o) && STRSTARTS(STR(?o), "http://ddhub.no/"))) }')"
[ "$legacy" = "false" ] || { echo "ddhub.no IRIs found after the ingest (T-SEM-4)"; exit 1; }

# 2b. One big and many small (M3.F, Tests #14). The owner's standard scenario is many small manifests
# of about 10 variables, next to one big one: fixtures/manifests/small/, cut from the SmartConnect
# manifest by tools/split/split-manifest.py. Every small one goes live whole, and its time
# (pre-processing plus commit, from the report) is printed next to the big one's. From here on
# every check and timing runs with one big and many small manifests live.
small_count=0; small_total_ms=0; small_max_ms=0; small_min_ms=''
for f in ../fixtures/manifests/small/*.manifest.json; do
  small_report="$(inject "$f")"
  small_id="$(field "$small_report" manifestId)"
  [ "$(field "$small_report" accepted)" = "true" ] && [ "$(field "$small_report" errors)" = "0" ] || { echo "the small manifest ${small_id} was not accepted whole"; printf '%s\n' "$small_report" | head -40; exit 1; }
  ms=$(( $(field "$small_report" preprocessingMs) + $(field "$small_report" commitMs) ))
  echo "  ${small_id}: ${ms} ms (pre-processing $(field "$small_report" preprocessingMs), commit $(field "$small_report" commitMs)), asserted $(field "$small_report" asserted), inferred $(field "$small_report" inferred)"
  small_count=$((small_count + 1)); small_total_ms=$((small_total_ms + ms))
  [ "$ms" -le "$small_max_ms" ] || small_max_ms=$ms
  [ -n "$small_min_ms" ] && [ "$ms" -ge "$small_min_ms" ] || small_min_ms=$ms
done
[ "$small_count" -gt 0 ] || { echo "no small manifests found"; exit 1; }
echo "one big and ${small_count} small: the big one ${big_ms} ms; the small ones ${small_min_ms} to ${small_max_ms} ms, $((small_total_ms / small_count)) ms on average, ${small_total_ms} ms in all"
listed="$(curl -sS --fail "${SEMANTIC}/manifests" | grep -o '"value":"SmartConnect-[0-9]*/' | wc -l | tr -d ' ')"
[ "$listed" -eq "$small_count" ] || { echo "expected ${small_count} small manifests listed, found ${listed}"; exit 1; }
# The small manifests are separate copies: the SPP data point now exists twice (the big manifest and
# the part that holds the standpipe pressure), under two providers.
by_class_both="$(host_query 'PREFIX dwis: <http://d-wis.org/>
SELECT ?signal WHERE { ?dp a dwis:SPP ; dwis:HasDynamicValue ?signal . }')"
[ "$(printf '%s\n' "$by_class_both" | wc -l)" -eq 2 ] || { echo "expected the SPP signal from the big manifest and from one small one"; printf '%s\n' "$by_class_both"; exit 1; }

# 2c. The manifest library (M4.F, Tests #21): real manifests from the projects that wrote them, one
# folder each under fixtures/manifests/library/ with the validation report the service is expected to
# return. tools/library/check-library.py injects each one, compares the stable part of the report
# with expected-report.json, prints the timings, and removes the manifest again, so the steps below
# see the same store as before. A difference is a failure; a missing expected report prints the
# report to record.
echo "manifest library:"
python3 ../tools/library/check-library.py --semantic "${SEMANTIC}" ../fixtures/manifests/library

# 3. The kilogram copy: one error, core-only, the whole manifest in quarantine (T-SEM-9, T-SEM-10).
# Demo of milestone 2: the report says what is wrong, where, and what is live.
report="$(inject ../fixtures/manifests/smartconnect-kilogram.manifest.json)"
kilogram_id="$(field "$report" manifestId)"
echo "injected ${kilogram_id}: accepted $(field "$report" accepted), errors $(field "$report" errors), live core $(grep -o -m 1 '"core": *[a-z]*' <<< "$report" | sed 's/.*: *//'), rest $(grep -o -m 1 '"rest": *[a-z]*' <<< "$report" | sed 's/.*: *//')"
grep -o -m 1 '"message": "[^"]*"' <<< "$report" | sed 's/^/  the error: /'
[ "$(field "$report" accepted)" = "false" ] || { echo "the kilogram copy was accepted"; exit 1; }
[ "$(field "$report" errors)" = "1" ] || { echo "the kilogram copy should have exactly one error"; exit 1; }
quarantined="$(curl -sS --fail -G "${FUSEKI}/dwis-quarantine/sparql" -H 'Accept: text/csv' --data-urlencode 'query=SELECT (COUNT(*) AS ?n) WHERE { GRAPH ?g { ?s ?p ?o } }' | tail -n 1 | tr -d '\r')"
[ "$quarantined" -gt 0 ] || { echo "the quarantine dataset is empty after the kilogram copy"; exit 1; }
echo "quarantine holds ${quarantined} triples, invisible to /dwis"

# 4. The inference view (milestone 2, M2.1): the same store under the OWL micro reasoner.
inf_query() {
  curl -sS --fail -G "${FUSEKI}/dwis-inf/sparql" -H 'Accept: text/csv' --data-urlencode "query=$1" | tail -n 1 | tr -d '\r'
}
spp_dp='<http://d-wis.org/SmartConnect/mainwellCirculationCommon_pressureStandPipe#01>'
# The data point was written through /dwis after Fuseki started: the view must see it, and type it
# with the superclasses of SPP (rdfs:subClassOf closure) that /dwis itself does not hold.
start=$(date +%s%N)
superclasses="$(inf_query "PREFIX dwis: <http://d-wis.org/> SELECT (COUNT(DISTINCT ?c) AS ?n) WHERE { ${spp_dp} a ?c . ?c a <http://www.w3.org/2002/07/owl#Class> . FILTER (?c != dwis:SPP) }")"
first_ms=$(( ($(date +%s%N) - start) / 1000000 ))
asserted="$(query "PREFIX dwis: <http://d-wis.org/> SELECT (COUNT(DISTINCT ?c) AS ?n) WHERE { ${spp_dp} a ?c . FILTER (?c != dwis:SPP) }")"
echo "inference view: the SPP data point has ${superclasses} inferred classes besides SPP (asserted in /dwis: ${asserted}); first query ${first_ms} ms"
[ "$superclasses" -gt "$asserted" ] || { echo "the inference view adds no class to the data point (no entailment, or /dwis writes not visible)"; docker compose logs --no-log-prefix fuseki | tail -40; exit 1; }
prototype="$(inf_query "PREFIX dwis: <http://d-wis.org/> ASK { ${spp_dp} a dwis:PrototypeData }")"
[ "$prototype" = "true" ] || { echo "the SPP data point is not inferred to be PrototypeData"; exit 1; }
# The quarantine dataset is not part of the view.
kilo="$(inf_query 'PREFIX dwis: <http://d-wis.org/> PREFIX uq: <http://d-wis.org/UnitAndQuantity/> ASK { ?s dwis:HasUnitOfMeasure uq:Kilogram }')"
[ "$kilo" = "false" ] || { echo "the quarantine leaks into the inference view"; exit 1; }
# Repeated queries: time and the container's memory, for the record (decision D-15, roadmap F.4).
start=$(date +%s%N)
for _ in 1 2 3 4 5 6 7 8 9 10; do inf_query "PREFIX dwis: <http://d-wis.org/> SELECT (COUNT(*) AS ?n) WHERE { ?dp a dwis:PrototypeData }" > /dev/null; done
repeat_ms=$(( ($(date +%s%N) - start) / 10000000 ))
memory="$(docker stats --no-stream --format '{{.MemUsage}}' "$(docker compose ps -q fuseki)")"
echo "inference view: ten repeated queries, ${repeat_ms} ms each on average; Fuseki memory ${memory}"

# 5. Demo of milestone 2: GRAPH separates asserted from inferred (T-SEM-6); the data point is a
# PrototypeData in the inferred graph only.
asserted_graph='<http://d-wis.org/manifests/SmartConnect/smartconnect-rig-signals>'
inferred_graph='<http://d-wis.org/manifests/SmartConnect/smartconnect-rig-signals/inferred>'
n_asserted="$(query "SELECT (COUNT(*) AS ?n) WHERE { GRAPH ${asserted_graph} { ?s ?p ?o } }")"
n_inferred="$(query "SELECT (COUNT(*) AS ?n) WHERE { GRAPH ${inferred_graph} { ?s ?p ?o } }")"
in_asserted="$(query "PREFIX dwis: <http://d-wis.org/> ASK { GRAPH ${asserted_graph} { ${spp_dp} a dwis:PrototypeData } }")"
in_inferred="$(query "PREFIX dwis: <http://d-wis.org/> ASK { GRAPH ${inferred_graph} { ${spp_dp} a dwis:PrototypeData } }")"
shared="$(query "ASK { GRAPH ${asserted_graph} { ?s ?p ?o } GRAPH ${inferred_graph} { ?s ?p ?o } }")"
echo "SmartConnect by GRAPH: ${n_asserted} asserted triples, ${n_inferred} inferred; the SPP data point is a PrototypeData in the asserted graph: ${in_asserted}, in the inferred graph: ${in_inferred}; a triple in both: ${shared}"
[ "$n_inferred" -gt 0 ] && [ "$in_asserted" = "false" ] && [ "$in_inferred" = "true" ] && [ "$shared" = "false" ] || { echo "asserted and inferred are not separated as expected (T-SEM-5, T-SEM-6)"; exit 1; }

# 6. Demo of milestone 2: a vocabulary swap re-derives the live manifests (T-SEM-8, Appendix C.7).
# The second version is the same vocabulary plus one project shape on an inferred property
# (fixtures/shapes/after-reasoning.ttl), loaded next to the first one; PUT /vocabulary switches to it,
# drops the first version's graphs and re-derives: the SmartConnect manifest becomes core-only. Then the
# first version is loaded again and put back: the manifest is whole again.
version="${VOCABULARY_VERSION:-2026-10-14}"
shaped="${version}-shaped"
graph_param() { printf '%s' "$1" | sed 's#:#%3A#g; s#/#%2F#g'; }
copy_graph() { # from-IRI to-IRI
  curl -sS --fail -G "${FUSEKI}/dwis/data" -H 'Accept: application/n-triples' --data-urlencode "graph=$1" \
    | curl -sS --fail -X PUT -H 'Content-Type: application/n-triples' --data-binary @- "${FUSEKI}/dwis/data?graph=$(graph_param "$2")" > /dev/null
}
vocabulary_graph="http://d-wis.org/vocabulary/${version}"
units_graph="http://d-wis.org/units/${version}"
work="$(mktemp -d)"
curl -sS --fail -G "${FUSEKI}/dwis/data" -H 'Accept: application/n-triples' --data-urlencode "graph=${vocabulary_graph}" > "$work/vocabulary.nt"
curl -sS --fail -G "${FUSEKI}/dwis/data" -H 'Accept: application/n-triples' --data-urlencode "graph=${units_graph}" > "$work/units.nt"
copy_graph "$vocabulary_graph" "http://d-wis.org/vocabulary/${shaped}"
copy_graph "$units_graph" "http://d-wis.org/units/${shaped}"
sed 's#http://ddhub\.no/#http://d-wis.org/#g' ../fixtures/shapes/after-reasoning.ttl \
  | curl -sS --fail -X POST -H 'Content-Type: text/turtle' --data-binary @- "${FUSEKI}/dwis/data?graph=$(graph_param "http://d-wis.org/vocabulary/${shaped}")" > /dev/null
start=$(date +%s%N)
swap="$(curl -sS --fail -X PUT "${SEMANTIC}/vocabulary" -H 'Content-Type: application/json' --data "{\"version\": \"${shaped}\"}")"
swap_ms=$(( ($(date +%s%N) - start) / 1000000 ))
echo "vocabulary swapped to ${shaped} in ${swap_ms} ms, $((small_count + 2)) manifests re-derived: $(grep -o '"manifestId":"[^"]*","acceptedBefore":[a-z]*,"accepted":[a-z]*,"errors":[0-9]*' <<< "$swap" | grep -v 'SmartConnect-[0-9]*/' | sed 's/"manifestId"://; s/,"acceptedBefore":/ was whole: /; s/,"accepted":/, is whole: /; s/,"errors":/, errors: /' | tr '\n' ';')"
core_only="$(grep -o '"becameCoreOnly":\[[^]]*\]' <<< "$swap")"
echo "became core-only: $(grep -o '"SmartConnect[^"]*"' <<< "$core_only" | wc -l | tr -d ' ') manifests (the big one and the small parts whose data points lack a quantity)"
grep -q '"SmartConnect/smartconnect-rig-signals"' <<< "$core_only" || { echo "the swap should have turned the SmartConnect manifest core-only"; printf '%s\n' "$swap"; exit 1; }
curl -sS --fail "${SEMANTIC}/vocabulary" | grep -q "\"version\":\"${shaped}\"" || { echo "the version in force is not ${shaped}"; exit 1; }
# Roll back: load the first version again (the loader did it the first time; here from the copies), put it back.
curl -sS --fail -X PUT -H 'Content-Type: application/n-triples' --data-binary "@$work/vocabulary.nt" "${FUSEKI}/dwis/data?graph=$(graph_param "$vocabulary_graph")" > /dev/null
curl -sS --fail -X PUT -H 'Content-Type: application/n-triples' --data-binary "@$work/units.nt" "${FUSEKI}/dwis/data?graph=$(graph_param "$units_graph")" > /dev/null
start=$(date +%s%N)
back="$(curl -sS --fail -X PUT "${SEMANTIC}/vocabulary" -H 'Content-Type: application/json' --data "{\"version\": \"${version}\"}")"
back_ms=$(( ($(date +%s%N) - start) / 1000000 ))
grep -q '"becameCoreOnly":\[\]' <<< "$back" || { echo "the rollback should have made every manifest whole again"; printf '%s\n' "$back"; exit 1; }
curl -sS --fail "${SEMANTIC}/manifests" | grep -o '"value":"SmartConnect/smartconnect-rig-signals"[^}]*}[^}]*"accepted":[a-z]*' | grep -q '"accepted":true' || { echo "the SmartConnect manifest is not whole after the rollback"; exit 1; }
echo "vocabulary rolled back to ${version} in ${back_ms} ms: the SmartConnect manifest is whole again"

# 7. Demo of milestone 3 (roadmap F.2, M3.5): two subscribers over gRPC see the diff when the SmartConnect
# manifest is removed and re-added, and come back with a full snapshot after a restart of the service
# (T-QR-2, the client side of T-QR-4). The subscribers are the watch console of tools/watch, run on the
# host against the published gRPC port 8081; each registers the F.3 "SPP by quantity and place" query (two
# rows: the big manifest and part 07; the kilogram copy, live core-only under the same provider, has no
# quantity and no place, so the big manifest's row goes when it is removed; the plain "SPP by class" row
# would survive, since the copy shares the data point IRIs) and exits once it has seen four diffs: -1 and
# +1 for the first remove and re-add, then, after the restart, -1 and +1 again.
echo "milestone 3 demo: two subscribers, a remove, a re-add, a restart, a remove, a re-add"
watch_project=../tools/watch/DWIS.Blackboard.Watch.csproj
dotnet build "$watch_project" -c Release -v q --nologo > "$work/watch-build.log" 2>&1 || { echo "the watch console does not build"; cat "$work/watch-build.log"; exit 1; }
watch_dll=../tools/watch/bin/Release/net10.0/DWIS.Blackboard.Watch.dll
spp_query='PREFIX ddhub: <http://ddhub.no/> SELECT ?signal WHERE { ?dp ddhub:IsOfMeasurableQuantity ?q ; ddhub:HasDynamicValue ?signal . ?q a ddhub:PressureDrillingQuantity . ?dp ddhub:IsLocatedAtEquipment ?e . ?e a ddhub:MudStandpipe . }'
for n in 1 2; do
  dotnet "$watch_dll" --address http://127.0.0.1:8081 --owner "subscriber-$n" --query "$spp_query" --until-diffs 4 --timeout 420 > "$work/subscriber-$n.log" 2>&1 &
  eval "subscriber_$n=\$!"
done
for n in 1 2; do
  i=0
  until grep -q "^snapshot q" "$work/subscriber-$n.log"; do
    i=$((i + 1)); [ "$i" -lt 60 ] || { echo "subscriber $n got no snapshot"; cat "$work/subscriber-$n.log"; exit 1; }; sleep 1
  done
done
echo "  both subscribers hold the snapshot: $(grep -m 1 '^snapshot' "$work/subscriber-1.log")"
remove_and_readd() { # label
  start=$(date +%s%N)
  curl -sS --fail -X DELETE "${SEMANTIC}/manifests/SmartConnect/smartconnect-rig-signals" > /dev/null
  removed_ms=$(( ($(date +%s%N) - start) / 1000000 ))
  start=$(date +%s%N)
  inject ../fixtures/manifests/smartconnect.manifest.json > /dev/null
  readded_ms=$(( ($(date +%s%N) - start) / 1000000 ))
  echo "  $1: removed in ${removed_ms} ms, re-added in ${readded_ms} ms"
}
remove_and_readd "first round"
for n in 1 2; do
  i=0
  until [ "$(grep -c '^diff q' "$work/subscriber-$n.log")" -ge 2 ]; do
    i=$((i + 1)); [ "$i" -lt 120 ] || { echo "subscriber $n did not see two diffs"; cat "$work/subscriber-$n.log"; exit 1; }; sleep 1
  done
done
echo "  the service restarts: the streams end, the subscribers register again"
docker compose restart semantic > /dev/null 2>&1
i=0
until curl -sf "${SEMANTIC}/health" > /dev/null; do i=$((i + 1)); [ "$i" -lt 60 ] || { echo "the semantic service did not come back"; exit 1; }; sleep 2; done
for n in 1 2; do
  i=0
  until [ "$(grep -c '^snapshot q' "$work/subscriber-$n.log")" -ge 2 ]; do
    i=$((i + 1)); [ "$i" -lt 60 ] || { echo "subscriber $n did not register again after the restart"; cat "$work/subscriber-$n.log"; exit 1; }; sleep 1
  done
done
remove_and_readd "second round"
for n in 1 2; do
  wait "$(eval "echo \$subscriber_$n")" || { echo "subscriber $n failed"; cat "$work/subscriber-$n.log"; exit 1; }
  diffs="$(grep '^diff q' "$work/subscriber-$n.log" | sed 's/ at .*//' | sed 's/^diff q[0-9]* //' | tr '\n' ' ')"
  echo "  subscriber $n saw: ${diffs}"
  [ "$diffs" = "+0 -1 +1 -0 +0 -1 +1 -0 " ] || { echo "subscriber $n did not see -1, +1, -1, +1"; cat "$work/subscriber-$n.log"; exit 1; }
  grep -q '^stream ended' "$work/subscriber-$n.log" || { echo "subscriber $n did not notice the restart"; cat "$work/subscriber-$n.log"; exit 1; }
done
echo "milestone 3 demo passed: $(grep -m 1 'done:' "$work/subscriber-1.log")"

# 8. Demo of milestone 4 (M4.4, Tests #22): the real-time process in the stack, used through OPC UA only by
# the unchanged C# client 2.2.x from nuget.org (tools/consumer; R-BC-1, T-EP-1). The manifests injected over
# HTTP above have no OPC UA nodes until the process reads the service's list, which it does at start-up: so
# it is restarted first and rebuilds the folders and variables of every live manifest (R-RT-6). Then a
# provider injects the SmartConnect manifest through DDHubServer.Inject (identical content: already live,
# the nodes answer) and writes a value to every variable every half second, while a consumer resolves the
# two F.3 queries through ResolveQuery, registers the first through RegisterQuery, subscribes to the signals
# it returned and prints the live values it sees.
echo "milestone 4 demo: the real-time process restarts, a provider writes over OPC UA, a consumer reads"
REALTIME=opc.tcp://127.0.0.1:48030
rebuilds_before="$(docker compose logs --no-log-prefix realtime 2>/dev/null | grep -c 'Rebuild 2 from the semantic service' || true)"
docker compose restart realtime > /dev/null 2>&1
i=0
until [ "$(docker compose logs --no-log-prefix realtime 2>/dev/null | grep -c 'Rebuild 2 from the semantic service' || true)" -gt "$rebuilds_before" ]; do
  i=$((i + 1)); [ "$i" -lt 60 ] || { echo "the real-time process did not rebuild after the restart"; docker compose logs --no-log-prefix realtime | tail -40; exit 1; }; sleep 2
done
echo "  $(docker compose logs --no-log-prefix realtime | grep 'Rebuild 2 from the semantic service' | tail -1 | sed 's/^ *//')"
consumer_project=../tools/consumer/DWIS.Blackboard.Consumer.csproj
dotnet build "$consumer_project" -c Release -v q --nologo > "$work/consumer-build.log" 2>&1 || { echo "the consumer console does not build"; cat "$work/consumer-build.log"; exit 1; }
consumer_dll=../tools/consumer/bin/Release/net10.0/DWIS.Blackboard.Consumer.dll
dotnet "$consumer_dll" write --address "$REALTIME" --manifest ../fixtures/manifests/smartconnect.manifest.json --seconds 90 > "$work/provider.log" 2>&1 &
provider=$!
i=0
until grep -q '^injected' "$work/provider.log"; do
  i=$((i + 1)); [ "$i" -lt 60 ] || { echo "the provider did not inject over OPC UA"; cat "$work/provider.log"; exit 1; }; sleep 1
done
echo "  $(grep -m 1 '^injected' "$work/provider.log")"
grep -q '^injected.*success True' "$work/provider.log" || { echo "the injection over OPC UA did not succeed"; cat "$work/provider.log"; exit 1; }
if dotnet "$consumer_dll" consume --address "$REALTIME" --until-values 6 --timeout 60 > "$work/consumer.log" 2>&1; then
  grep -E '^(query|  =|registered|diff|value|done)' "$work/consumer.log" | head -24 | sed 's/^/  /'
else
  echo "the consumer did not see live values over OPC UA"; cat "$work/consumer.log"; exit 1
fi
grep -q '^registered q1' "$work/consumer.log" || { echo "the consumer did not register its query"; cat "$work/consumer.log"; exit 1; }
grep -q '^value .* = [1-9]' "$work/consumer.log" || { echo "the consumer saw no written value, only initial ones"; cat "$work/consumer.log"; exit 1; }
kill "$provider" 2> /dev/null || true
wait "$provider" 2> /dev/null || true
echo "milestone 4 demo passed: $(grep -m 1 '^done' "$work/consumer.log"); the provider $(grep -m 1 '^wrote' "$work/provider.log" | sed 's/, round.*//')"
rm -rf "$work"

echo "smoke test passed"
if $down; then
  docker compose down -v
fi
