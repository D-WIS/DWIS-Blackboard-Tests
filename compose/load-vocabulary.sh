#!/bin/sh
# Loads the vocabulary and the advanced definitions into the vocabulary graph of /dwis
# (Appendix C.8: http://d-wis.org/vocabulary/{version}) after translating the legacy namespace
# http://ddhub.no/ to http://d-wis.org/ (specification 3.6). Runs in the curl image (busybox sh).
set -eu

FUSEKI_URL="${FUSEKI_URL:-http://fuseki:3030}"
VOCABULARY_DIR="${VOCABULARY_DIR:-/vocabulary}"
VERSION="${VOCABULARY_VERSION:?set VOCABULARY_VERSION}"
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

query() {
  curl -sS --fail -G "${FUSEKI_URL}/dwis/sparql" -H 'Accept: text/csv' --data-urlencode "query=$1" | tail -n 1 | tr -d '\r'
}

count="$(query "SELECT (COUNT(*) AS ?n) WHERE { GRAPH <${GRAPH}> { ?s ?p ?o } }")"
units="$(query "SELECT (COUNT(*) AS ?n) WHERE { GRAPH <${UNITS_GRAPH}> { ?s ?p ?o } }")"
legacy="$(query "ASK { ?s ?p ?o . FILTER (STRSTARTS(STR(?s), 'http://ddhub.no/') || STRSTARTS(STR(?p), 'http://ddhub.no/') || (isIRI(?o) && STRSTARTS(STR(?o), 'http://ddhub.no/'))) }")"
bar="$(query "ASK { GRAPH <${UNITS_GRAPH}> { <http://d-wis.org/UnitAndQuantity/Bar> a <http://d-wis.org/Bar> ; <http://d-wis.org/IsUnitForQuantity> <http://d-wis.org/UnitAndQuantity/PressureQuantity> } }")"

echo "vocabulary graph <${GRAPH}>: ${count} triples; units graph <${UNITS_GRAPH}>: ${units} triples; legacy namespace present: ${legacy}"
[ "$count" -gt 0 ] || { echo "the vocabulary graph is empty"; exit 1; }
[ "$units" -gt 0 ] || { echo "the units graph is empty"; exit 1; }
[ "$legacy" = "false" ] || { echo "http://ddhub.no/ IRIs remain after translation (T-SEM-4)"; exit 1; }
[ "$bar" = "true" ] || { echo "the unit individual Bar is missing or untyped"; exit 1; }
echo "vocabulary and units loaded"
