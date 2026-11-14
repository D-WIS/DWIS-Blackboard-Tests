# Fixtures

Snapshots used by the tests and the compose files. The source repository stays the master; each row gives where the file came from and the short commit of that repository at copy time (14 November 2026, from the checkouts under `C:\repos\D-WIS\`).

| File | Source | Commit |
|---|---|---|
| `vocabulary/DWISVocabulary.ttl` | `DWIS.DDBot-toolkit\model-dwis-config\assets\DWISVocabulary.ttl` (generated from the DWIS vocabulary; the file carries `owl:versionInfo "0.1"` and no version IRI; the DWIS.Vocabulary packages pinned by the Blackboard at that time were 1.0.172) | e6cf0ca (last change of the file, 14 October 2026; repository head 140d0b7) |
| `vocabulary/advancedDefinitions/*.ttl` | `DDHub-Semantic-Interoperability\docs\vocabulary_development\definitions\advancedDefinitions\` (OWL axioms and the 16 SHACL shapes written in 2026) | c3737d9 |
| `manifests/smartconnect.manifest.json` | `DWIS-SmartConnect\src\DWIS.SmartConnect.Bridge\smartconnect.manifest.json` (254 provided variables, 391 nodes, 1,537 references; the standard ingest fixture) | d559ea6 |

The vocabulary files keep the legacy namespace `http://ddhub.no/`; the loader in `compose/load-vocabulary.sh` translates it to `http://d-wis.org/` when it loads them (specification 3.6). The vocabulary graph is `http://d-wis.org/vocabulary/2026-10-14`, named after the date of the generated file, until the vocabulary project emits a proper version IRI (Appendix D). Change `VOCABULARY_VERSION` in `compose/compose.yaml` when the file is replaced.

Edited copies of the manifest (kilogram unit, dangling reference, unknown class, injected variables; Appendix C.10) arrive with M1.4 and M1.5.
