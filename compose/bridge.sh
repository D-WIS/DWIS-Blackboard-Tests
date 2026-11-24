#!/usr/bin/env bash
# T-BC-2 (M4.4, Tests #22; R-BC-1): the SmartConnect bridge image of the old demo, digiwells/dwis.smartconnect.bridge
# (BRIDGE_TAG, default latest), runs unchanged against the stack: it connects to the test broker with mutual TLS,
# subscribes to the topics of its signal map, connects to the real-time process by its service name on the network,
# injects its manifest through DDHubServer.Inject, and writes the values of the MQTT messages it receives. A message
# is published on the broker's plain listener from inside the broker's container, and a consumer on the host sees the
# value arrive over OPC UA (tools/consumer). The broker and the bridge are the "bridge" profile of compose.yaml.
#   ./bridge.sh            on a running stack (./smoke.sh or docker compose up -d first); leaves the profile running
#   ./bridge.sh --down     stops the broker and the bridge at the end
# Needs openssl, docker and dotnet.
set -euo pipefail
cd "$(dirname "$0")"
down=false
for arg in "$@"; do case "$arg" in --down) down=true ;; *) echo "unknown option: $arg"; exit 2 ;; esac; done
REALTIME="${REALTIME:-opc.tcp://127.0.0.1:48030}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

./mqtt/make-certs.sh
docker compose --profile bridge up -d --no-build mosquitto bridge > /dev/null 2>&1
echo "T-BC-2: the SmartConnect bridge connects to the broker, then to the real-time process, and injects"
i=0
until docker compose --profile bridge logs --no-log-prefix bridge 2> /dev/null | grep -q -E "provided variables created|Manifest .* injected|ProvidedVariablesNamespace|injected"; do
  i=$((i + 1)); [ "$i" -lt 90 ] || { echo "the bridge did not inject its manifest"; docker compose --profile bridge logs --no-log-prefix mosquitto | tail -20; docker compose --profile bridge logs --no-log-prefix bridge | tail -40; exit 1; }; sleep 2
done
docker compose --profile bridge logs --no-log-prefix bridge | grep -E "Connected to|Subscribed to|injected" | sed 's/^ *//; s/^/  /' | head -6

# A measurement for the standpipe pressure, as SmartConnect publishes it (the topic and the fields of the signal map).
topic="rigs/554fae81-e079-4a0f-bc35-a9a0d81a2309/drill-well/mainwell/circulation/common/realtime/pressure-stand-pipe"
value="137.25"
echo "T-BC-2: a message on the broker, the bridge writes it, a consumer reads it over OPC UA"
consumer_project=../tools/consumer/DWIS.Blackboard.Consumer.csproj
dotnet build "$consumer_project" -c Release -v q --nologo > "$work/consumer-build.log" 2>&1 || { echo "the consumer console does not build"; cat "$work/consumer-build.log"; exit 1; }
consumer_dll=../tools/consumer/bin/Release/net10.0/DWIS.Blackboard.Consumer.dll
spp_query='PREFIX ddhub: <http://ddhub.no/> SELECT ?signal WHERE { <http://ddhub.no/SmartConnect/mainwellCirculationCommon_pressureStandPipe#01> ddhub:HasDynamicValue ?signal }'
dotnet "$consumer_dll" consume --address "$REALTIME" --query "$spp_query" --until-values 3 --timeout 60 > "$work/consumer.log" 2>&1 &
consumer=$!
sleep 4
for _ in 1 2 3; do
  docker compose --profile bridge exec -T mosquitto mosquitto_pub -h 127.0.0.1 -p 1883 -t "$topic" -m "{\"v\": $value, \"t\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}"
  sleep 1
done
wait "$consumer" || { echo "the consumer did not see the bridge's value over OPC UA"; cat "$work/consumer.log"; docker compose --profile bridge logs --no-log-prefix bridge | tail -30; exit 1; }
grep -q "^value .* = $value " "$work/consumer.log" || { echo "the consumer saw values, but not the bridge's $value"; cat "$work/consumer.log"; exit 1; }
echo "  $(grep -m 1 "^value .* = $value " "$work/consumer.log")"
echo "  bridge: $(docker compose --profile bridge logs --no-log-prefix bridge | grep -E "values sent" | tail -1 | sed 's/^ *//')"
echo "T-BC-2 passed: the unchanged SmartConnect bridge injected and wrote through the real-time process"
if $down; then docker compose --profile bridge rm -sf mosquitto bridge > /dev/null 2>&1; fi
