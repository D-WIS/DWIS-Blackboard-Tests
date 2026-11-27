#!/usr/bin/env bash
# Evidence for decision D-47 (M5.6, Semantic #43): what the shipped closure of the vocabulary and the
# units changes for the commit cost. Against a running stack (docker compose up -d, vocabulary loaded,
# no manifest live) it injects the SmartConnect manifest and the 26 small ones of M3.F, swaps the
# vocabulary to a copy with one more shape and back (as smoke.sh does), removes and re-adds the big one,
# prints the times on one line and removes every manifest again, so that the next run starts clean.
# The digest of the big manifest's inferred graph is printed too: two configurations that give the same
# digest derive the same consequences.
#   ./closure-evidence.sh [label]
# Variants to compare (27 November 2026, the numbers in docs/decisions.md of the Design repository):
#   the closure loaded or not: VOCABULARY_CLOSURE=on|off on the loader (docker compose run --rm --no-deps
#   -e VOCABULARY_CLOSURE=off vocabulary), a Fuseki restart in between so that the reasoner starts afresh;
#   the view's rules: fuseki/config/dwis.ttl as shipped (OWL micro) or tools/closure/manifest.rules.
set -euo pipefail
cd "$(dirname "$0")"
label="${1:-run}"
SEMANTIC="${SEMANTIC:-http://127.0.0.1:8080/api/v1}"
FUSEKI="${FUSEKI:-http://127.0.0.1:3030}"
version="${VOCABULARY_VERSION:-2026-10-14}"
field() { grep -o -m 1 "\"$2\": *[^,}]*" <<< "$1" | sed 's/^[^:]*: *//; s/^"//; s/"$//'; }
inject() { curl -sS --fail -X POST "${SEMANTIC}/manifests" -H 'Content-Type: application/json' --data-binary "@$1"; }
graph_param() { printf '%s' "$1" | sed 's#:#%3A#g; s#/#%2F#g'; }
ms() { echo $(( ($(date +%s%N) - $1) / 1000000 )); }
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

i=0; until curl -sf "${SEMANTIC}/health" > /dev/null; do i=$((i+1)); [ $i -lt 60 ] || { echo "the semantic service is not healthy"; exit 1; }; sleep 2; done
live="$(curl -sS --fail "${SEMANTIC}/manifests" | { grep -o '"value":"[^"]*"' || true; } | wc -l | tr -d ' ')"
[ "$live" -eq 0 ] || { echo "${live} manifests are live; start from a clean store"; exit 1; }

# The first query on the view: the reasoner's preparation (the forward phase of OWL micro), once per Fuseki start.
start=$(date +%s%N)
curl -sS --fail -G "${FUSEKI}/dwis-inf/sparql" -H 'Accept: text/csv' --data-urlencode 'query=PREFIX dwis: <http://d-wis.org/> ASK { dwis:SPP a ?c }' > /dev/null
first=$(ms $start)

report="$(inject ../fixtures/manifests/smartconnect.manifest.json)"
[ "$(field "$report" accepted)" = "true" ] || { echo "the SmartConnect manifest was refused"; printf '%s\n' "$report" | head -40; exit 1; }
big_pre=$(field "$report" preprocessingMs); big_commit=$(field "$report" commitMs); big_inf=$(field "$report" inferred)

n=0; total=0; max=0; min=''
for f in ../fixtures/manifests/small/*.manifest.json; do
  r="$(inject "$f")"
  [ "$(field "$r" accepted)" = "true" ] || { echo "a small manifest was refused: $f"; exit 1; }
  c=$(field "$r" commitMs); n=$((n+1)); total=$((total+c)); [ $c -le $max ] || max=$c; [ -n "$min" ] && [ $c -ge $min ] || min=$c
done

inferred_graph='http://d-wis.org/manifests/SmartConnect/smartconnect-rig-signals/inferred'
curl -sS --fail -G "${FUSEKI}/dwis/data" -H 'Accept: application/n-triples' --data-urlencode "graph=${inferred_graph}" | sed 's/[[:space:]]*$//' | sort -u > "$work/inferred.nt"
digest="$(sha1sum "$work/inferred.nt" | cut -c1-12) ($(wc -l < "$work/inferred.nt") triples)"

# The vocabulary swap as smoke.sh does it: copies of the two graphs plus one shape, PUT, then back.
shaped="${version}-shaped"
copy_graph() {
  curl -sS --fail -G "${FUSEKI}/dwis/data" -H 'Accept: application/n-triples' --data-urlencode "graph=$1" \
    | curl -sS --fail -X PUT -H 'Content-Type: application/n-triples' --data-binary @- "${FUSEKI}/dwis/data?graph=$(graph_param "$2")" > /dev/null
}
copy_graph "http://d-wis.org/vocabulary/${version}" "http://d-wis.org/vocabulary/${shaped}"
copy_graph "http://d-wis.org/units/${version}" "http://d-wis.org/units/${shaped}"
sed 's#http://ddhub\.no/#http://d-wis.org/#g' ../fixtures/shapes/after-reasoning.ttl \
  | curl -sS --fail -X POST -H 'Content-Type: text/turtle' --data-binary @- "${FUSEKI}/dwis/data?graph=$(graph_param "http://d-wis.org/vocabulary/${shaped}")" > /dev/null
curl -sS --fail -G "${FUSEKI}/dwis/data" -H 'Accept: application/n-triples' --data-urlencode "graph=http://d-wis.org/vocabulary/${version}" > "$work/v.nt"
curl -sS --fail -G "${FUSEKI}/dwis/data" -H 'Accept: application/n-triples' --data-urlencode "graph=http://d-wis.org/units/${version}" > "$work/u.nt"
start=$(date +%s%N)
swap="$(curl -sS --fail -X PUT "${SEMANTIC}/vocabulary" -H 'Content-Type: application/json' --data "{\"version\": \"${shaped}\"}")"
swap_ms=$(ms $start)
core_only="$(grep -o '"becameCoreOnly":\[[^]]*\]' <<< "$swap" | grep -o '"SmartConnect[^"]*"' | wc -l | tr -d ' ')"
curl -sS --fail -X PUT -H 'Content-Type: application/n-triples' --data-binary "@$work/v.nt" "${FUSEKI}/dwis/data?graph=$(graph_param "http://d-wis.org/vocabulary/${version}")" > /dev/null
curl -sS --fail -X PUT -H 'Content-Type: application/n-triples' --data-binary "@$work/u.nt" "${FUSEKI}/dwis/data?graph=$(graph_param "http://d-wis.org/units/${version}")" > /dev/null
start=$(date +%s%N)
curl -sS --fail -X PUT "${SEMANTIC}/vocabulary" -H 'Content-Type: application/json' --data "{\"version\": \"${version}\"}" > /dev/null
back_ms=$(ms $start)

start=$(date +%s%N)
curl -sS --fail -X DELETE "${SEMANTIC}/manifests/SmartConnect/smartconnect-rig-signals" > /dev/null
del_ms=$(ms $start)
r="$(inject ../fixtures/manifests/smartconnect.manifest.json)"
readd_commit=$(field "$r" commitMs)

memory="$(docker stats --no-stream --format '{{.MemUsage}}' "$(docker compose ps -q fuseki)" | cut -d/ -f1 | tr -d ' ')"
echo "RESULT ${label}: first view query ${first} ms; the big manifest: pre-processing ${big_pre} ms, commit ${big_commit} ms, ${big_inf} inferred triples; ${n} small: commit ${min} to ${max} ms, ${total} ms in all; swap ${swap_ms} ms (${core_only} core-only), back ${back_ms} ms; remove ${del_ms} ms, re-add commit ${readd_commit} ms; inferred digest ${digest}; Fuseki memory ${memory}"

for id in $(curl -sS --fail "${SEMANTIC}/manifests" | grep -o '"value":"[^"]*"' | sed 's/.*"value":"//; s/"$//'); do
  curl -sS --fail -X DELETE "${SEMANTIC}/manifests/${id}" > /dev/null
done
echo "every manifest removed again"
