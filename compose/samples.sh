#!/usr/bin/env bash
# T-BC-1 (M4.4, Tests #22; R-BC-1): the three samples of DWIS-Blackboard-Samples run unchanged against the
# running stack, as a user would run them from the samples' README: InjectManifestAndWriteData injects its
# manifest through DDHubServer.Inject and writes values every half second; AcquireSignals resolves one query
# per noun, subscribes to the signals and prints their values; GetQueryResults prints the rows of a query.
# The samples are built from their repository as published (a shallow clone, or a checkout at SAMPLES_SOURCE),
# on the client 2.2.x they reference, with their own configuration file. Used by CI after the smoke, and by hand.
#   ./samples.sh                       against opc.tcp://localhost:48030 and the service on localhost:8080
#   REALTIME=opc.tcp://host:48030 SAMPLES_SOURCE=../../DWIS-Blackboard-Samples ./samples.sh
# Needs dotnet (the samples target .NET 8), git and curl.
set -euo pipefail
cd "$(dirname "$0")"

REALTIME="${REALTIME:-opc.tcp://localhost:48030}"
SEMANTIC="${SEMANTIC:-http://127.0.0.1:8080/api/v1}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ -n "${SAMPLES_SOURCE:-}" ]; then
  samples="$SAMPLES_SOURCE/Samples"
else
  git clone -q --depth 1 https://github.com/D-WIS/DWIS-Blackboard-Samples "$work/samples"
  samples="$work/samples/Samples"
  echo "samples: $(git -C "$work/samples" log -1 --format='%h %s' | cut -c1-80)"
fi

# The samples' configuration file keeps the OPC UA certificate stores and the trace file under the machine's
# common application data (%CommonApplicationData%, /usr/share on Linux), which a CI runner cannot write to
# without this. The samples stay as they are; the folders are made writable instead.
if [ "$(uname -s)" = "Linux" ] && command -v sudo > /dev/null; then
  for folder in /usr/share/NORCE "/usr/share/OPC Foundation"; do
    sudo mkdir -p "$folder" && sudo chown -R "$(id -u):$(id -g)" "$folder"
  done
fi

for project in InjectManifestAndWriteData AcquireSignals GetQueryResults; do
  dotnet build "$samples/$project/$project.csproj" -c Release -v q --nologo > "$work/build-$project.log" 2>&1 || { echo "$project does not build"; cat "$work/build-$project.log"; exit 1; }
done
# Each sample runs from its own folder, as the README says (dotnet run --project ...): the client 2.2.34 they pin
# looks for config/Quickstarts.ReferenceClient.Config.xml under the current directory only.
sample() { # name, then its arguments
  local name="$1"; shift
  (cd "$samples/$name" && exec dotnet "bin/Release/net8.0/$name.dll" "$@")
}

# 1. The provider: injects its manifest (8 signals of the SamplesApp provider) and writes them every half second
#    once its "press any key" read returns (stdin closed).
echo "T-BC-1: InjectManifestAndWriteData injects and writes"
timeout 90 bash -c "$(declare -f sample); samples='$samples'; sample InjectManifestAndWriteData '$REALTIME'" < /dev/null > "$work/writer.log" 2>&1 &
writer=$!
i=0
until grep -q "Manifest injected" "$work/writer.log"; do
  i=$((i + 1)); [ "$i" -lt 60 ] || { echo "the sample did not inject its manifest"; cat "$work/writer.log"; exit 1; }; sleep 1
done
i=0
until [ "$(grep -c "Write data" "$work/writer.log")" -ge 8 ]; do
  i=$((i + 1)); [ "$i" -lt 30 ] || { echo "the sample did not write its signals"; cat "$work/writer.log"; exit 1; }; sleep 1
done
echo "  injected and writing: $(grep -m 1 'Write data' "$work/writer.log" | sed 's/.*Write data: //')"

# 2. The consumer: one query per noun (measured BitDepth, HookPosition, HookVelocity, FlowRateIn, SPP, SurfaceRPM,
#    HoleDepth), a subscription on the signals, the values printed every half second.
echo "T-BC-1: AcquireSignals resolves, subscribes and prints the values"
timeout 25 bash -c "$(declare -f sample); samples='$samples'; sample AcquireSignals '$REALTIME'" < /dev/null > "$work/reader.log" 2>&1 || true
# A noun may match several data points (the SmartConnect manifest's as well, which nobody writes here): a value of
# this run's writer in the noun's block is what counts. The writer's values are a random prefix plus a fraction.
prefix="$(grep -m 1 'Write data' "$work/writer.log" | sed 's/.*Write data: [^,]*, *//; s/[,.].*//')"
for noun in SPP BitDepth HookPosition FlowRateIn HoleDepth; do
  awk -v noun="$noun" -v prefix="$prefix" '$0 ~ ("Signal name: " noun "$") { block = 1; next } /Signal name:/ { block = 0 } block && $0 ~ ("Value: " prefix) { found = 1 } END { exit !found }' "$work/reader.log" \
    || { echo "AcquireSignals saw no value of this run's writer (prefix $prefix) for $noun"; tail -40 "$work/reader.log"; exit 1; }
done
echo "  values of this run's writer (prefix $prefix) seen for every noun, for example $(awk -v prefix="$prefix" '/Signal name: SPP$/ { block = 1; next } /Signal name:/ { block = 0 } block && $0 ~ ("Value: " prefix) { print; exit }' "$work/reader.log" | sed 's/^\s*//')"

# 3. The query console: the rows of the query for SPP with its data point, then exit.
echo "T-BC-1: GetQueryResults prints the rows of a query"
printf 'print-query-results SPP -d true\nexit\n' | timeout 60 bash -c "$(declare -f sample); samples='$samples'; sample GetQueryResults '$REALTIME'" > "$work/query.log" 2>&1 || true
grep -q "SamplesApp.SPP" "$work/query.log" || { echo "GetQueryResults did not list the sample's SPP signal"; cat "$work/query.log" | tail -30; exit 1; }
echo "  the query lists the sample's signal SamplesApp.SPP with its data point"

kill "$writer" 2> /dev/null || true
wait "$writer" 2> /dev/null || true
# The sample has no delete (the client's Delete is a stub): the manifest is removed through the service, so that
# the script can run again on the same stack; the process drops the nodes at its next restart.
curl -sS -o /dev/null -X DELETE "${SEMANTIC}/manifests/SamplesApp/sample-manifest" || true
echo "T-BC-1 passed: the three samples ran unchanged"
