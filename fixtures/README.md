# Fixtures

Snapshots used by the tests and the compose files. The source repository stays the master; each row gives where the file came from and the short commit of that repository at copy time (14 November 2026, from the checkouts under `C:\repos\D-WIS\`).

| File | Source | Commit |
|---|---|---|
| `vocabulary/DWISVocabulary.ttl` | `DWIS.DDBot-toolkit\model-dwis-config\assets\DWISVocabulary.ttl` (generated from the DWIS vocabulary; the file carries `owl:versionInfo "0.1"` and no version IRI; the DWIS.Vocabulary packages pinned by the Blackboard at that time were 1.0.172) | e6cf0ca (last change of the file, 14 October 2026; repository head 140d0b7) |
| `vocabulary/advancedDefinitions/*.ttl` | `DDHub-Semantic-Interoperability\docs\vocabulary_development\definitions\advancedDefinitions\` (OWL axioms and the 16 SHACL shapes written in 2026) | c3737d9 |
| `manifests/smartconnect.manifest.json` | `DWIS-SmartConnect\src\DWIS.SmartConnect.Bridge\smartconnect.manifest.json` (254 provided variables, 391 nodes, 1,537 references; the standard ingest fixture) | d559ea6 |
| `manifests/smartconnect-kilogram.manifest.json`, `-dangling`, `-unknown-class`, `-injected-variables` | the edited copies of Appendix C.10, made from the file above by a script on 14 November 2026: the standpipe pressure variable with `Kilogram` as its unit (expected: one error, core-only); the standpipe data point located at a node that does not exist (one error); that data point typed `MeasurementOfTheFuture` (one error); one injected variable added (accepted with one warning) | — |
| `manifests/unit-and-quantity.manifest.json` | the manifest "Units and quantities" that the old server injects at start-up, generated with `DWIS.API.DTO` 2.2.35 and `DWIS.Vocabulary.Standard` 1.0.177 (`ManifestFile.FromDWISInstance(StandardSetProvider.UnitsAndQuantities, "UnitAndQuantity", "NORCE")`): 1,638 individuals with camelCase names, 1,648 references | packages of 14 November 2026 |
| `vocabulary/unit-and-quantity-individuals.ttl` | derived from the file above (decision D-44): one individual per Unit or Quantity class, named after the class (`uq:Bar a dwis:Bar`), individuals of the same class merged, with `IsUnitForQuantity`, `HasSIUnit`, `IsOfBaseQuantity` and the attributes as typed literals; 1,470 individuals, 7,729 triples. Loaded by the compose loader into `http://d-wis.org/units/2026-10-14` | — |
| `vocabulary/validation-policy.json` | the severity policy of the pre-processing pass for milestone 1 (decision D-45): which checks and shapes report as warnings while the vocabulary definitions are unsettled, with the reason for each entry | — |

The vocabulary files keep the legacy namespace `http://ddhub.no/`; the loader in `compose/load-vocabulary.sh` translates it to `http://d-wis.org/` when it loads them (specification 3.6). The vocabulary graph is `http://d-wis.org/vocabulary/2026-10-14`, named after the date of the generated file, until the vocabulary project emits a proper version IRI (Appendix D). Change `VOCABULARY_VERSION` in `compose/compose.yaml` when the file is replaced.

Edited copies of the manifest (kilogram unit, dangling reference, unknown class, injected variables; Appendix C.10) arrive with M1.4 and M1.5.
