# DWIS Blackboard Rig Edition: tests

Part of DWIS Blackboard Rig Edition (GitHub topic `dwis-blackboard-rig-edition`); design and decisions in [DWIS-Blackboard-Design](https://github.com/D-WIS/DWIS-Blackboard-Design).

**Holds:** integration, compatibility, regression, performance and replication tests; the conformance suite as a runnable tool; fixtures (manifests, recorded hub sessions, pinned vocabulary); the `compose.yaml` files used by the tests and by users; the Fuseki configuration and image build.

**Produces:** the Fuseki image `digiwells/dwis.blackboard.fuseki`; the conformance tool image `digiwells/dwis.blackboard.conformance`; test reports as workflow artefacts.

**Depends on:** the `edge` or tagged images of `DWIS-Blackboard-Semantic` and `DWIS-Blackboard-RealTime`; the C# client NuGet 2.2.x from `DWIS-Blackboard-Base`.

## Layout

- `compose/`: `compose.yaml` for the stack (Fuseki, the vocabulary loader, the semantic service and, since M4.4, the real-time process on `opc.tcp://localhost:48030`), `load-vocabulary.sh`, `smoke.sh`, `inference-evidence.sh` (decision D-15: repeated commits with concurrent queries against the inference view; time, memory, consistency), `samples.sh` (T-BC-1: the three samples of `DWIS-Blackboard-Samples`, built from a shallow clone or from `SAMPLES_SOURCE`, run unchanged against the stack: the writer injects and writes, the reader subscribes and prints values, the query console prints rows; both workflows run it after the compatibility tests), `bridge.sh` with `mqtt/` (T-BC-2: the `bridge` profile of the compose file, an MQTT broker with mutual TLS and a password as the real one, `eclipse-mosquitto`, and the SmartConnect bridge image of the old demo, `digiwells/dwis.smartconnect.bridge:${BRIDGE_TAG:-latest}`, unchanged, reaching the real-time process as `opc.tcp://realtime:48030`; `mqtt/make-certs.sh` makes the test certificates and the password file on every run; the script publishes a standpipe pressure on the broker and a consumer sees the bridge's value over OPC UA; both workflows run it after the samples).
- `tools/inference/`: `owlrl-compare.py` (T-SEM-7: a manifest's inferred graph compared with the OWL 2 RL closure computed outside the store by owlrl; needs `pip install owlrl rdflib`).
- `tools/split/`: `split-manifest.py` (M3.F: cuts the SmartConnect manifest into the small self-contained manifests of `fixtures/manifests/small/`, about 10 variables each; plain Python 3, no packages).
- `models/`: the TLA+ models of the two-phase injection and the query lifecycle (M3.0, decision D-41), checked with TLC on every pull request (`models/check.sh`, T-TST-1 to T-TST-3); `models/README.md` lists the invariants and what TLC found.
- `tools/watch/`: the watch console (M3.5), a subscriber of the semantic service over gRPC on the published package `DWIS.Blackboard.Contracts.Grpc`: registers queries, prints the snapshot and every diff, registers again when the stream ends. `dotnet run --project tools/watch -- --address http://127.0.0.1:8081 --owner me` watches the two F.3 queries against a running stack; `--query` chooses others, `--until-diffs N --timeout S` makes it exit for a script. The smoke runs two of them for the milestone 3 demo.
- `tests/DWIS.Blackboard.Compatibility.Tests/`: the compatibility tests (M4.4, test plan G.2) with the unchanged C# client 2.2.56 against a running stack: T-BC-4 (`Register` and `Resolve` answer `BadNotSupported`), T-EP-1 (a client using only OPC UA injects, queries, registers, subscribes, reads and deletes), T-RT-3 (the address space holds only folders, variables, validity booleans, methods and query nodes), T-RT-4 (every variable has its `_validitySignal` sibling and the `IsValidBy` triple is in the store), T-RT-6 (the folders and variables come back after a restart of the real-time process). They run when `DWIS_REALTIME_ENDPOINT` (`opc.tcp://127.0.0.1:48030`), `DWIS_SEMANTIC_API` (`http://127.0.0.1:8080/api/v1`) and `DWIS_COMPOSE_FILE` (`compose/compose.yaml`, for the restart) are set, and skip otherwise; `DWIS.Blackboard.Tests.slnx` at the root builds them with the tools (`dotnet test DWIS.Blackboard.Tests.slnx`). Both workflows run them against the stack after the smoke.
- `tools/consumer/`: the consumer console (M4.4), a DWIS application that talks to the real-time process over OPC UA only, with the unchanged C# client 2.2.x from nuget.org (`DWIS.Client.ReferenceImplementation.OPCFoundation`, R-BC-1, T-EP-1): `inject --manifest FILE` calls `Inject`; `write --manifest FILE --seconds N` injects and writes a value to every provided variable every half second; `consume` resolves the two F.3 queries (or `--query` ones), registers the first, subscribes to its signals and prints the values it sees, exiting after `--until-values N`. No delete: the client's `Delete` and `Update` are stubs that throw. `--address` names the endpoint (`opc.tcp://127.0.0.1:48030` by default). The smoke runs a provider and a consumer for the milestone 4 demo.
- `fuseki/`: the image `digiwells/dwis.blackboard.fuseki` built from the Apache Jena Docker build kit at Jena 6.2.0 (decision D-28) and the dataset configuration `config/dwis.ttl` (`/dwis` with union default graph, `/dwis-quarantine`, the inference view `/dwis-inf`). See [fuseki/README.md](fuseki/README.md).
- `fixtures/`: the pinned vocabulary and shapes, the SmartConnect manifest, later edited copies and recorded hub sessions. Provenance in [fixtures/README.md](fixtures/README.md).
- `tests/` (planned): the test projects. `conformance/` (M4): the conformance suite and tool. `models/` (optional): the TLA+ models.

## Running the stack

```bash
docker compose -f compose/compose.yaml up -d
```

Builds the Fuseki image on first use, starts it with a health check, then runs the one-shot `vocabulary` service, which loads `fixtures/vocabulary` into the graph `http://d-wis.org/vocabulary/2026-10-14` of `/dwis` and the unit and quantity individuals into `http://d-wis.org/units/2026-10-14`, after translating `http://ddhub.no/` to `http://d-wis.org/`, and checks that no legacy IRI remains. Then the semantic service starts, reads both graphs from the store and answers on `http://127.0.0.1:8080/api/v1` (`POST` and `GET /manifests`, `GET /sparql`, `GET /vocabulary`, `GET /health`; see the Semantic repository's README). Fuseki answers on `http://127.0.0.1:3030` (SPARQL at `/dwis/sparql`, Graph Store Protocol at `/dwis/data`, SHACL at `/dwis/shacl`, health at `/$/ping`). The data lives in the volume `dwis-blackboard_fuseki-databases`.

The images are `digiwells/dwis.blackboard.fuseki:${FUSEKI_TAG:-dev}` and `digiwells/dwis.blackboard.semantic:${SEMANTIC_TAG:-edge}`; both `edge` tags are pushed to Docker Hub by the merge workflows (this repository for Fuseki, DWIS-Blackboard-Semantic for the service), so `FUSEKI_TAG=edge docker compose -f compose/compose.yaml pull` then `compose/smoke.sh --no-build` runs the stack without building anything. The semantic image is `digiwells/dwis.blackboard.semantic:${SEMANTIC_TAG:-edge}`. When it is not at hand, `docker compose build` builds it from a checkout of `DWIS-Blackboard-Semantic` at `SEMANTIC_SOURCE` (default: the sibling folder `../DWIS-Blackboard-Semantic` of this repository). The pull request workflow checks that repository out and builds it; once the Docker Hub secrets exist, the `edge` tag from every merge there can be pulled instead.

```bash
compose/smoke.sh --down
```

Does the same from a clean state, runs the milestone 1 checks and stops the stack: the store (vocabulary visible in the union default graph, `dwis:SPP` found, no `ddhub.no` subject, units typed, quarantine empty), then the acceptance of roadmap F.3 through the service (the SmartConnect manifest injected and accepted with no error, the two queries with either prefix returning the same signal, no legacy IRI stored, the kilogram copy core-only with one error and its graph in quarantine), then the inference view `/dwis-inf` (the data point written through `/dwis` is typed with the superclasses of `SPP` there, the quarantine stays out, query times and memory printed), then the milestone 2 demo of roadmap F.2: the kilogram report with its error, asserted versus inferred by `GRAPH` for the SmartConnect manifest, and a vocabulary swap through `PUT /vocabulary` to a second version carrying `fixtures/shapes/after-reasoning.ttl`, which re-derives the SmartConnect manifest core-only, followed by the rollback that makes it whole again. The pull request workflow runs it on every pull request. Behind a proxy that breaks image builds (the Fuseki build's `apk add`), `compose/smoke.sh --no-build --down` uses the images at hand: tag any Fuseki 6.2.0 image with the Rig Edition configuration as `digiwells/dwis.blackboard.fuseki:dev` and set `SEMANTIC_TAG` to a semantic image you have.

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
