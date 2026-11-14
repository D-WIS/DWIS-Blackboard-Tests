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

query() {
  curl -sS --fail -G "${FUSEKI_URL}/dwis/sparql" -H 'Accept: text/csv' --data-urlencode "query=$1" | tail -n 1 | tr -d '\r'
}

count="$(query "SELECT (COUNT(*) AS ?n) WHERE { GRAPH <${GRAPH}> { ?s ?p ?o } }")"
legacy="$(query "ASK { GRAPH <${GRAPH}> { ?s ?p ?o . FILTER (STRSTARTS(STR(?s), 'http://ddhub.no/') || STRSTARTS(STR(?p), 'http://ddhub.no/') || (isIRI(?o) && STRSTARTS(STR(?o), 'http://ddhub.no/'))) } }")"

echo "vocabulary graph <${GRAPH}>: ${count} triples; legacy namespace present: ${legacy}"
[ "$count" -gt 0 ] || { echo "the vocabulary graph is empty"; exit 1; }
[ "$legacy" = "false" ] || { echo "http://ddhub.no/ IRIs remain after translation (T-SEM-4)"; exit 1; }
echo "vocabulary loaded"
