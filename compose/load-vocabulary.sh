#!/bin/sh
# Loads the vocabulary and the advanced definitions into the vocabulary graph of /dwis
# (Appendix C.8: http://d-wis.org/vocabulary/{version}) after translating the legacy namespace
# http://ddhub.no/ to http://d-wis.org/ (specification 3.6), the unit and quantity individuals into
# the units graph, then the shipped closure of both (decision D-47, tools/closure/make-closure.py)
# into the same two graphs when VOCABULARY_CLOSURE=on (off by default: measured in M5.6, the closure
# makes the OWL micro view slower; it pays only with the SPARQL rules of D-64). Runs in the curl image (busybox sh).
set -eu

FUSEKI_URL="${FUSEKI_URL:-http://fuseki:3030}"
VOCABULARY_DIR="${VOCABULARY_DIR:-/vocabulary}"
VERSION="${VOCABULARY_VERSION:?set VOCABULARY_VERSION}"
CLOSURE="${VOCABULARY_CLOSURE:-off}"
GRAPH="http://d-wis.org/vocabulary/${VERSION}"
GRAPH_PARAM="$(printf '%s' "$GRAPH" | sed 's#:#%3A#g; s#/#%2F#g')"

echo "waiting for ${FUSEKI_URL}"
i=0
until curl -sf "${FUSEKI_URL}/\$/ping" > /dev/null; do
  i=$((i + 1))
  [ "$i" -lt 60 ] || { echo "Fuseki did not answer in time"; exit 1; }
  sleep 2
done

method=PUT   # the first file replaces the graph, the others are appended
for file in "${VOCABULARY_DIR}/DWISVocabulary.ttl" "${VOCABULARY_DIR}"/advancedDefinitions/*.ttl; do
  echo "${method} $(basename "$file") -> <${GRAPH}>"
  sed 's#http://ddhub\.no/#http://d-wis.org/#g' "$file" \
    | curl -sS --fail -X "$method" -H 'Content-Type: text/turtle' --data-binary @- \
        "${FUSEKI_URL}/dwis/data?graph=${GRAPH_PARAM}" > /dev/null
  method=POST
done

# The unit and quantity individuals (decision D-44) go to their own graph next to the vocabulary.
UNITS_GRAPH="http://d-wis.org/units/${VERSION}"
UNITS_PARAM="$(printf '%s' "$UNITS_GRAPH" | sed 's#:#%3A#g; s#/#%2F#g')"
echo "PUT unit-and-quantity-individuals.ttl -> <${UNITS_GRAPH}>"
sed 's#http://ddhub\.no/#http://d-wis.org/#g' "${VOCABULARY_DIR}/unit-and-quantity-individuals.ttl" \
  | curl -sS --fail -X PUT -H 'Content-Type: text/turtle' --data-binary @- \
      "${FUSEKI_URL}/dwis/data?graph=${UNITS_PARAM}" > /dev/null

# The closure (decision D-47): what the reasoner would derive about the schema and the individuals,
# computed offline and shipped as closure/vocabulary.nt and closure/units.nt, appended to the two
# graphs. The inference view's rules (fuseki/config/manifest.rules) take the schema as closed.
if [ "$CLOSURE" = "off" ]; then
  echo "closure not loaded (VOCABULARY_CLOSURE=off, the default while decision D-64 is open): the inference view derives the schema itself"
else
  for pair in "vocabulary.nt ${GRAPH_PARAM}" "units.nt ${UNITS_PARAM}"; do
    file="${VOCABULARY_DIR}/closure/${pair%% *}"
    [ -f "$file" ] || { echo "the closure file ${file} is missing: run tools/closure/make-closure.py"; exit 1; }
    echo "POST closure/$(basename "$file") -> <$(printf '%s' "${pair#* }" | sed 's#%3A#:#g; s#%2F#/#g')>"
    curl -sS --fail -X POST -H 'Content-Type: application/n-triples' --data-binary "@${file}" \
      "${FUSEKI_URL}/dwis/data?graph=${pair#* }" > /dev/null
  done
fi

query() {
  curl -sS --fail -G "${FUSEKI_URL}/dwis/sparql" -H 'Accept: text/csv' --data-urlencode "query=$1" | tail -n 1 | tr -d '\r'
}

count="$(query "SELECT (COUNT(*) AS ?n) WHERE { GRAPH <${GRAPH}> { ?s ?p ?o } }")"
units="$(query "SELECT (COUNT(*) AS ?n) WHERE { GRAPH <${UNITS_GRAPH}> { ?s ?p ?o } }")"
legacy="$(query "ASK { ?s ?p ?o . FILTER (STRSTARTS(STR(?s), 'http://ddhub.no/') || STRSTARTS(STR(?p), 'http://ddhub.no/') || (isIRI(?o) && STRSTARTS(STR(?o), 'http://ddhub.no/'))) }")"
bar="$(query "ASK { GRAPH <${UNITS_GRAPH}> { <http://d-wis.org/UnitAndQuantity/Bar> a <http://d-wis.org/Bar> ; <http://d-wis.org/IsUnitForQuantity> <http://d-wis.org/UnitAndQuantity/PressureQuantity> } }")"
closed="$(query "ASK { GRAPH <${GRAPH}> { <${GRAPH}> <http://d-wis.org/blackboard/closedBy> ?by } GRAPH <${UNITS_GRAPH}> { <${UNITS_GRAPH}> <http://d-wis.org/blackboard/closedBy> ?by2 } }")"

echo "vocabulary graph <${GRAPH}>: ${count} triples; units graph <${UNITS_GRAPH}>: ${units} triples; legacy namespace present: ${legacy}; closure loaded: ${closed}"
[ "$count" -gt 0 ] || { echo "the vocabulary graph is empty"; exit 1; }
[ "$units" -gt 0 ] || { echo "the units graph is empty"; exit 1; }
[ "$legacy" = "false" ] || { echo "http://ddhub.no/ IRIs remain after translation (T-SEM-4)"; exit 1; }
[ "$bar" = "true" ] || { echo "the unit individual Bar is missing or untyped"; exit 1; }
[ "$CLOSURE" = "off" ] || [ "$closed" = "true" ] || { echo "the closure markers are missing after the load"; exit 1; }
echo "vocabulary and units loaded"
