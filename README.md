# DWIS Blackboard Rig Edition: tests

Part of DWIS Blackboard Rig Edition (GitHub topic `dwis-blackboard-rig-edition`); design and decisions in [DWIS-Blackboard-Design](https://github.com/D-WIS/DWIS-Blackboard-Design).

**Holds:** integration, compatibility, regression, performance and replication tests; the conformance suite as a runnable tool; fixtures (manifests, recorded hub sessions, pinned vocabulary); the `compose.yaml` files used by the tests and by users; the Fuseki configuration and image build.

**Produces:** the Fuseki image `digiwells/dwis.blackboard.fuseki`; the conformance tool image `digiwells/dwis.blackboard.conformance`; test reports as workflow artefacts.

**Depends on:** the `edge` or tagged images of `DWIS-Blackboard-Semantic` and `DWIS-Blackboard-RealTime`; the C# client NuGet 2.2.x from `DWIS-Blackboard-Base`.

## Layout

- `compose/`: `compose.yaml` for the stack (Fuseki, the vocabulary loader and the semantic service; the real-time process joins in M4), `load-vocabulary.sh`, `smoke.sh`, `inference-evidence.sh` (decision D-15: repeated commits with concurrent queries against the inference view; time, memory, consistency).
- `tools/inference/`: `owlrl-compare.py` (T-SEM-7: a manifest's inferred graph compared with the OWL 2 RL closure computed outside the store by owlrl; needs `pip install owlrl rdflib`).
- `tools/split/`: `split-manifest.py` (M3.F: cuts the SmartConnect manifest into the small self-contained manifests of `fixtures/manifests/small/`, about 10 variables each; plain Python 3, no packages).
- `fuseki/`: the image `digiwells/dwis.blackboard.fuseki` built from the Apache Jena Docker build kit at Jena 6.2.0 (decision D-28) and the dataset configuration `config/dwis.ttl` (`/dwis` with union default graph, `/dwis-quarantine`, the inference view `/dwis-inf`). See [fuseki/README.md](fuseki/README.md).
- `fixtures/`: the pinned vocabulary and shapes, the SmartConnect manifest, later edited copies and recorded hub sessions. Provenance in [fixtures/README.md](fixtures/README.md).
- `tests/` (planned): the test projects. `conformance/` (M4): the conformance suite and tool. `models/` (optional): the TLA+ models.

## Running the stack

```bash
docker compose -f compose/compose.yaml up -d
```

Builds the Fuseki image on first use, starts it with a health check, then runs the one-shot `vocabulary` service, which loads `fixtures/vocabulary` into the graph `http://d-wis.org/vocabulary/2026-10-14` of `/dwis` and the unit and quantity individuals into `http://d-wis.org/units/2026-10-14`, after translating `http://ddhub.no/` to `http://d-wis.org/`, and checks that no legacy IRI remains. Then the semantic service starts, reads both graphs from the store and answers on `http://127.0.0.1:8080/api/v1` (`POST` and `GET /manifests`, `GET /sparql`, `GET /vocabulary`, `GET /health`; see the Semantic repository's README). Fuseki answers on `http://127.0.0.1:3030` (SPARQL at `/dwis/sparql`, Graph Store Protocol at `/dwis/data`, SHACL at `/dwis/shacl`, health at `/$/ping`). The data lives in the volume `dwis-blackboard_fuseki-databases`.

The semantic image is `digiwells/dwis.blackboard.semantic:${SEMANTIC_TAG:-edge}`. When it is not at hand, `docker compose build` builds it from a checkout of `DWIS-Blackboard-Semantic` at `SEMANTIC_SOURCE` (default: the sibling folder `../DWIS-Blackboard-Semantic` of this repository). The pull request workflow checks that repository out and builds it; once the Docker Hub secrets exist, the `edge` tag from every merge there can be pulled instead.

```bash
compose/smoke.sh --down
```

Does the same from a clean state, runs the milestone 1 checks and stops the stack: the store (vocabulary visible in the union default graph, `dwis:SPP` found, no `ddhub.no` subject, units typed, quarantine empty), then the acceptance of roadmap F.3 through the service (the SmartConnect manifest injected and accepted with no error, the two queries with either prefix returning the same signal, no legacy IRI stored, the kilogram copy core-only with one error and its graph in quarantine), then the inference view `/dwis-inf` (the data point written through `/dwis` is typed with the superclasses of `SPP` there, the quarantine stays out, query times and memory printed), then the milestone 2 demo of roadmap F.2: the kilogram report with its error, asserted versus inferred by `GRAPH` for the SmartConnect manifest, and a vocabulary swap through `PUT /vocabulary` to a second version carrying `fixtures/shapes/after-reasoning.ttl`, which re-derives the SmartConnect manifest core-only, followed by the rollback that makes it whole again. The pull request workflow runs it on every pull request. Behind a proxy that breaks image builds (the Fuseki build's `apk add`), `compose/smoke.sh --no-build --down` uses the images at hand: tag any Fuseki 6.2.0 image with the Rig Edition configuration as `digiwells/dwis.blackboard.fuseki:dev` and set `SEMANTIC_TAG` to a semantic image you have.

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
