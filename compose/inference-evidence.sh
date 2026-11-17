#!/usr/bin/env bash
# Evidence for decision D-15 (M2.6): the inference view /dwis-inf under repeated commits and concurrent
# queries. Against a running stack (./smoke.sh or docker compose up -d, vocabulary loaded):
#   ./inference-evidence.sh [cycles]      default 6 cycles
# Each cycle re-injects the SmartConnect manifest with a changed publishing interval (same id, new content
# hash, so the commit drops and rewrites the asserted, inferred and provenance graphs), while a reader
# runs queries on the service's /sparql and on /dwis-inf and counts answers, errors and empty answers.
# Each cycle also re-injects one small manifest of M3.F (fixtures/manifests/small/, the part that holds
# the standpipe pressure) the same way, so the numbers cover one big and one small.
# Prints per cycle: commit ms and inferred triples of the big one and of the small one, Fuseki memory;
# at the end: the reader's counts.
set -euo pipefail
cd "$(dirname "$0")"
cycles="${1:-6}"
SEMANTIC=http://127.0.0.1:8080/api/v1
FUSEKI=http://127.0.0.1:3030
manifest=../fixtures/manifests/smartconnect.manifest.json
small=../fixtures/manifests/small/smartconnect-07.manifest.json
work="$(mktemp -d)"
trap 'rm -rf "$work"; kill "${reader:-0}" 2>/dev/null || true' EXIT

field() { printf '%s' "$1" | grep -o "\"$2\": *[^,}]*" | head -n 1 | sed 's/^[^:]*: *//; s/^"//; s/"$//'; }
spp='PREFIX dwis: <http://d-wis.org/> SELECT ?signal WHERE { ?dp a dwis:SPP ; dwis:HasDynamicValue ?signal . }'
proto='PREFIX dwis: <http://d-wis.org/> SELECT (COUNT(*) AS ?n) WHERE { <http://d-wis.org/SmartConnect/mainwellCirculationCommon_pressureStandPipe#01> a ?c . ?c a <http://www.w3.org/2002/07/owl#Class> }'

# The reader: alternates a query on the service (live union) and one on the view, until told to stop.
reader_log="$work/reader.log"
(
  ok=0; empty=0; errors=0
  while [ ! -f "$work/stop" ]; do
    for target in "$SEMANTIC/sparql|$spp" "$FUSEKI/dwis-inf/sparql|$proto"; do
      url="${target%%|*}"; q="${target#*|}"
      body="$(curl -s -m 60 -G "$url" -H 'Accept: text/csv' --data-urlencode "query=$q" 2>/dev/null || true)"
      lines="$(printf '%s' "$body" | grep -c . || true)"
      if [ "$lines" -ge 2 ]; then ok=$((ok + 1)); elif [ "$lines" -eq 1 ]; then empty=$((empty + 1)); else errors=$((errors + 1)); fi
    done
    echo "$ok $empty $errors" > "$reader_log"
  done
) &
reader=$!

echo "cycle,bigCommitMs,bigInferred,smallCommitMs,smallInferred,fusekiMemory"
for i in $(seq 1 "$cycles"); do
  sed "s/\"PublishingIntervalInMS\": [0-9.]*/\"PublishingIntervalInMS\": $i.0/" "$manifest" > "$work/m.json"
  report="$(curl -s -m 900 -X POST "$SEMANTIC/manifests" -H 'Content-Type: application/json' --data-binary "@$work/m.json")"
  sed "s/\"PublishingIntervalInMS\": [0-9.]*/\"PublishingIntervalInMS\": $i.0/" "$small" > "$work/s.json"
  small_report="$(curl -s -m 900 -X POST "$SEMANTIC/manifests" -H 'Content-Type: application/json' --data-binary "@$work/s.json")"
  mem="$(docker stats --no-stream --format '{{.MemUsage}}' "$(docker compose ps -q fuseki)" | cut -d/ -f1 | tr -d ' ')"
  echo "$i,$(field "$report" commitMs),$(field "$report" inferred),$(field "$small_report" commitMs),$(field "$small_report" inferred),$mem"
done
touch "$work/stop"; wait "$reader" 2>/dev/null || true
read -r ok empty errors < "$reader_log"
echo "reader during the ${cycles} commits: $ok answers with rows, $empty empty answers (a query between the drop and the rewrite), $errors errors"
