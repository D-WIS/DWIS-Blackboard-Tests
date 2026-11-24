#!/usr/bin/env bash
# Makes the certificates and the password file of the test broker (M4.4, T-BC-2) in compose/mqtt/certs/, which git
# ignores: a CA, the broker's certificate (for the host name "mosquitto", the service's name on the stack's network;
# the bridge checks the chain, not the name) and the bridge's client certificate, all self-signed by that CA, plus
# the mosquitto password file with the user "bridge". Test material only, made again on every run of bridge.sh.
# Needs openssl and docker (mosquitto_passwd runs in the broker's image).
set -euo pipefail
# Git Bash on Windows turns "/CN=..." into a Windows path unless told not to; harmless elsewhere.
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"
certs="$PWD/certs"
rm -rf "$certs"
mkdir -p "$certs"
cd "$certs"

openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=DWIS Blackboard test CA" -keyout ca.key -out ca.crt 2> /dev/null
for name in broker client; do
  if [ "$name" = broker ]; then subject="/CN=mosquitto"; san="subjectAltName=DNS:mosquitto,DNS:localhost"; else subject="/CN=bridge"; san="subjectAltName=DNS:bridge"; fi
  openssl req -newkey rsa:2048 -nodes -subj "$subject" -keyout "$name.key" -out "$name.csr" 2> /dev/null
  printf '%s\n' "$san" > "$name.ext"
  openssl x509 -req -in "$name.csr" -CA ca.crt -CAkey ca.key -CAcreateserial -days 30 -extfile "$name.ext" -out "$name.crt" 2> /dev/null
  rm "$name.csr" "$name.ext"
done
# The bridge (user "app", 1654) and the broker (user "mosquitto", 1883) read the keys through bind mounts.
chmod 644 ./*.key ./*.crt
host_certs="$(pwd -W 2> /dev/null || pwd)" # the Windows form of the folder for Docker Desktop, the plain one elsewhere
docker run --rm -v "$host_certs:/w" eclipse-mosquitto:2.0 sh -c "mosquitto_passwd -b -c /w/passwd bridge bridge-secret && chmod 644 /w/passwd"
echo "certificates and password file made in $certs"
