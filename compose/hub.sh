#!/usr/bin/env bash
# M5.3 (Tests #33; R-HUB-1 to R-HUB-3, test plan G.2): the "hub" profile of compose.yaml, the old DWIS hub
# (digiwells/dwissignalr, unchanged) and a second Blackboard "b" (Fuseki, vocabulary, semantic service and
# real-time process on ports 3031, 8090/8091 and 48031), with both real-time processes joined to the same hub
# group. The running real-time process of the stack is recreated with the hub URL (HUB_URL), the second one
# starts with it. The script waits until the second semantic service is healthy and both processes report
# their registration to the group, and prints the hub's view. The replication tests T-HUB-1 and T-HUB-2
# (tests/DWIS.Blackboard.Compatibility.Tests/ReplicationTests.cs) then run against both endpoints, that class
# alone, since the old hub replays every manifest it ever saw to a process that reconnects (a restart test
# with the hub role on would replay into the tests after it):
#   DWIS_REALTIME_ENDPOINT=opc.tcp://127.0.0.1:48030 DWIS_SEMANTIC_API=http://127.0.0.1:8080/api/v1 \
#   DWIS_REALTIME_ENDPOINT_B=opc.tcp://127.0.0.1:48031 DWIS_SEMANTIC_API_B=http://127.0.0.1:8090/api/v1 \
#   dotnet ../tests/DWIS.Blackboard.Compatibility.Tests/bin/Release/net10.0/DWIS.Blackboard.Compatibility.Tests.dll \
#     -class DWIS.Blackboard.Compatibility.Tests.ReplicationTests
#   ./hub.sh            on a running stack (./smoke.sh or docker compose up -d first); leaves the profile running
#   ./hub.sh --down     stops the hub and the second Blackboard at the end
# The stack's tags (FUSEKI_TAG, SEMANTIC_TAG, REALTIME_TAG) must be the running stack's. Needs docker and curl.
set -euo pipefail
cd "$(dirname "$0")"
down=false
for arg in "$@"; do case "$arg" in --down) down=true ;; *) echo "unknown option: $arg"; exit 2 ;; esac; done
export HUB_URL="${HUB_URL:-http://hub:8080/blackboard/applications}"
SEMANTIC_B="${SEMANTIC_B:-http://127.0.0.1:8090/api/v1}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "hub profile: the hub, the second Blackboard, and the first real-time process joined to ${HUB_URL}"
docker compose --profile hub up -d --no-build > "$work/up.log" 2>&1 || { echo "the hub profile did not start"; cat "$work/up.log"; exit 1; }

echo "waiting for the second semantic service"
i=0
until curl -sf "${SEMANTIC_B}/health" > /dev/null; do
  i=$((i + 1))
  [ "$i" -lt 60 ] || { echo "the second semantic service did not become healthy in time"; docker compose --profile hub logs --no-log-prefix vocabulary-b | tail -20; docker compose --profile hub logs --no-log-prefix semantic-b | tail -40; exit 1; }
  sleep 2
done
status="$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose --profile hub ps -aq vocabulary-b)")"
[ "$status" -eq 0 ] || { echo "the second vocabulary loader failed with $status"; docker compose --profile hub logs --no-log-prefix vocabulary-b; exit 1; }

registered() { # service
  docker compose --profile hub logs --no-log-prefix "$1" 2> /dev/null | grep -q "registered to group"
}
echo "waiting for both real-time processes to register to the hub group"
i=0
until registered realtime && registered realtime-b; do
  i=$((i + 1))
  [ "$i" -lt 60 ] || { echo "a real-time process did not register to the hub"; docker compose --profile hub logs --no-log-prefix hub | tail -30; docker compose --profile hub logs --no-log-prefix realtime | tail -30; docker compose --profile hub logs --no-log-prefix realtime-b | tail -30; exit 1; }
  sleep 2
done
for service in realtime realtime-b; do
  docker compose --profile hub logs --no-log-prefix "$service" | grep "registered to group" | tail -1 | sed "s/^/  $service: /"
done
docker compose --profile hub logs --no-log-prefix hub | grep -i -E "connect|group" | tail -3 | sed 's/^/  hub: /' || true
echo "hub profile up: opc.tcp://127.0.0.1:48030 and opc.tcp://127.0.0.1:48031 replicate through the hub"
if $down; then docker compose --profile hub rm -sf hub fuseki-b vocabulary-b semantic-b realtime-b > /dev/null 2>&1; fi
