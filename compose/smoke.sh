#!/usr/bin/env bash
# Builds the Fuseki image, starts the stack, waits for the vocabulary to be loaded and runs the
# milestone 1 checks against /dwis from the host. Used by CI and by hand.
#   ./smoke.sh            keep the stack running afterwards
#   ./smoke.sh --down     stop it and remove the volume at the end
set -euo pipefail
cd "$(dirname "$0")"

docker compose build
docker compose up -d
echo "waiting for the vocabulary loader"
status=0
docker compose wait vocabulary || status=$?
docker compose logs --no-log-prefix vocabulary
[ "$status" -eq 0 ] || { echo "vocabulary loader failed with $status"; docker compose logs fuseki | tail -40; exit 1; }

query() {
  curl -sS --fail -G http://127.0.0.1:3030/dwis/sparql -H 'Accept: text/csv' --data-urlencode "query=$1" | tail -n 1 | tr -d '\r'
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

# The quarantine dataset exists and is empty.
quarantine="$(curl -sS --fail -G http://127.0.0.1:3030/dwis-quarantine/sparql -H 'Accept: text/csv' --data-urlencode 'query=SELECT (COUNT(*) AS ?n) WHERE { GRAPH ?g { ?s ?p ?o } }' | tail -n 1 | tr -d '\r')"
[ "$quarantine" = "0" ] || { echo "quarantine is not empty: ${quarantine}"; exit 1; }

echo "smoke test passed"
if [ "${1:-}" = "--down" ]; then
  docker compose down -v
fi
