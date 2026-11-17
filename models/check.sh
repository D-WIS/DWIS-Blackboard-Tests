#!/usr/bin/env bash
# Checks the models with TLC (test plan G, T-TST-1 to T-TST-3):
#   Injection.tla with Injection.cfg        must pass (the design)
#   Queries.tla with Queries.cfg            must pass (the design)
#   Injection.tla with InjectionBroken.cfg  must fail on LiveHasNodesOrRebuild (the broken variant)
# Needs Java 17 or later and tla2tools.jar (downloaded here when missing). Without Java, run it in a
# container from the repository root:
#   docker run --rm -v "$PWD/models:/models" -w /models eclipse-temurin:21-jre bash check.sh
# (download tla2tools.jar into models/ first when the container has no network.)
set -euo pipefail
cd "$(dirname "$0")"

jar="${TLA2TOOLS:-tla2tools.jar}"
if [ ! -f "$jar" ]; then
  echo "downloading tla2tools.jar (TLA+ tools 1.8.0)"
  curl -sSL -o "$jar" https://github.com/tlaplus/tlaplus/releases/download/v1.8.0/tla2tools.jar
fi

tlc() { # config module log
  java -XX:+UseParallelGC -Dtlc2.TLC.ide=none -cp "$jar" tlc2.TLC -workers auto -deadlock -config "$1" "$2" > "$3" 2>&1
}

check() { # name config module: the design must pass
  echo "== $1"
  local start; start=$(date +%s)
  if tlc "$2" "$3" "$2.log"; then
    grep -E "states generated|Model checking completed" "$2.log" | grep -v "^Progress" | sed 's/^/   /'
  else
    echo "   TLC failed:"; grep -v "^$" "$2.log" | tail -40; exit 1
  fi
  echo "   $(( $(date +%s) - start )) s"
}

check "Injection (T-TST-1)" Injection.cfg Injection.tla
check "Queries (T-TST-2)" Queries.cfg Queries.tla

echo "== Injection, broken variant (T-TST-3): must fail"
if tlc InjectionBroken.cfg Injection.tla broken.log; then
  echo "the broken variant was accepted: the invariant LiveHasNodesOrRebuild does not catch a commit before node creation"
  exit 1
fi
grep -q "Invariant LiveHasNodesOrRebuild is violated" broken.log || { echo "the broken variant failed for another reason:"; tail -30 broken.log; exit 1; }
echo "   rejected as expected: $(grep -c "^State" broken.log) states in the shortest trace"

# TLC reports a passing run with an exit code of 0 and a violation with 12; the design runs above would
# have ended the script (set -e) on anything but success.
rm -rf states ./*.log *_TTrace_*.tla
echo "models checked"
