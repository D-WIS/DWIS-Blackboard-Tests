#!/usr/bin/env bash
# Builds the images, starts the stack, waits for the vocabulary to be loaded and runs the milestone 1
# checks: the store from the host, then the acceptance of roadmap F.3 through the semantic service
# (the SmartConnect manifest accepted, the two queries with either prefix returning the same signal,
# the kilogram copy core-only with one error); then the milestone 2 checks of the inference view
# /dwis-inf (a write through /dwis is seen without a restart, a subclass entailment appears, the
# quarantine stays out, timings and memory noted for decision D-15). Used by CI and by hand.
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
  printf '%s' "$1" | grep -o "\"$2\": *[^,}]*" | head -n 1 | sed 's/^[^:]*: *//; s/^"//; s/"$//'
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

# 3. The kilogram copy: one error, core-only, the whole manifest in quarantine (T-SEM-9, T-SEM-10).
report="$(inject ../fixtures/manifests/smartconnect-kilogram.manifest.json)"
echo "injected $(field "$report" manifestId): accepted $(field "$report" accepted), errors $(field "$report" errors)"
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

echo "smoke test passed"
if $down; then
  docker compose down -v
fi
