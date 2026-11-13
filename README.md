# DWIS Blackboard Rig Edition: tests

Part of DWIS Blackboard Rig Edition (GitHub topic `dwis-blackboard-rig-edition`); design and decisions in [DWIS-Blackboard-Design](https://github.com/D-WIS/DWIS-Blackboard-Design).

**Holds:** integration, compatibility, regression, performance and replication tests; the conformance suite as a runnable tool; fixtures (manifests, recorded hub sessions, pinned vocabulary); the `compose.yaml` files used by the tests and by users; the Fuseki configuration and image build.

**Produces:** the Fuseki image `digiwells/dwis.blackboard.fuseki`; the conformance tool image `digiwells/dwis.blackboard.conformance`; test reports as workflow artefacts.

**Depends on:** the `edge` or tagged images of `DWIS-Blackboard-Semantic` and `DWIS-Blackboard-RealTime`; the C# client NuGet 2.2.x from `DWIS-Blackboard-Base`.

## Layout (planned)

- `compose/`: compose files for the lab and the test runs.
- `fuseki/`: Fuseki configuration and the image build from the Apache Jena build kit.
- `fixtures/`: manifests, recorded sessions, pinned vocabulary.
- `tests/`: the test projects.
- `conformance/`: the conformance suite and tool.
- `models/`: the optional TLA+ models.

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
