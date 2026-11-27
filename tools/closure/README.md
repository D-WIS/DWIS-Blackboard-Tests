# The shipped closure of the vocabulary and the units (decision D-47, M5.6)

Decision D-47 (16 November 2026): everything the reasoner derives about the vocabulary and the units themselves (the subclass and subproperty chains, the domains and ranges a property inherits, the two sides of an equivalence or an inverse, the members of a union or an intersection, the types and property values of the unit and quantity individuals) is computed once, offline, and shipped with the Blackboard as part of the vocabulary version, so that a commit reasons over the manifest only. This folder holds the tool, the evidence of M5.6 (27 November 2026) and the two alternatives that were tried. Needs `pip install rdflib`.

| File | What it is |
|---|---|
| `make-closure.py` | Computes the closure from `fixtures/vocabulary/` (the vocabulary, the advanced definitions, the unit and quantity individuals, `http://ddhub.no/` translated as the loader does) with the schema rules of Jena's `owl-fb-micro.rules`, the reasoner of the inference view, each rule named after the one it mirrors; 3 s. Writes `fixtures/vocabulary/closure/vocabulary.nt` and `units.nt` (sorted N-Triples, no blank node, no axiom about the built-in terms, a marker triple `<graph> <http://d-wis.org/blackboard/closedBy> "…"` each). `--check` compares the shipped files with a fresh run; the pull request workflow runs it. Stops on an OWL construct the rules do not cover (`owl:allValuesFrom`, `owl:hasValue`, the plain cardinalities); ignores what OWL micro ignores (`owl:hasKey`, `owl:propertyChainAxiom`, the qualified cardinalities). |
| `../../compose/load-vocabulary.sh` | Loads the two files into the vocabulary graph and the units graph when `VOCABULARY_CLOSURE=on` (off by default: see the numbers). |
| `../../compose/closure-evidence.sh` | The measurement: the big manifest, the 26 small ones, the vocabulary swap and back, a remove and a re-add, the digest of the big manifest's inferred graph; one line per run. |
| `manifest.rules` | Tried and set aside: the inference view on the instance rules only, as backward rules over the closed schema. Correct, slower. |
| `sparql-rules.sh` | The prototype of option C: the per-manifest inference as eight SPARQL `INSERT` updates over the closed schema, no view. Correct, seven times faster. |

Why the view cannot compute its own closure: asked for `rdf:type` with an unbound subject over the vocabulary and the units, the view ran 257 s and failed with `Java heap space` (2 GB); asked in batches of 200 bound subjects, the batches took 3, 44 and 80 s and grew. The tool's subclass part was checked against the view's own answer for `rdfs:subClassOf` (7,562 triples, identical).

## The numbers (27 November 2026, the owner's laptop, Fuseki 6.2.0 with a 2 GB heap)

Every configuration gives the same inferred graph for the SmartConnect manifest (5,454 triples, digest `869c6495221b`). "270 predicates" is the service as published (edge, 26 November 2026), "24 predicates" the service of Semantic M5.6, which asks the view only about `rdf:type` and the properties a rule can derive a value for.

| View | Closure loaded | Predicates asked | Big manifest commit | 26 small, average | Swap, back | Re-add |
|---|---|---|---|---|---|---|
| OWL micro (today) | no | 270 | 20.1 s | 924 ms | 34.6 s, 26.0 s | 9.4 s |
| OWL micro | yes (D-47 as taken) | 270 | 59.2 s | 1,180 ms | 57.9 s, 52.6 s | 6.7 s |
| instance rules (`manifest.rules`) | yes | 270 | 142.9 s | 4,630 ms | 316.9 s, 229.1 s | 100.8 s |
| instance rules | yes | 24 | 54.6 s | 1,749 ms | 62.3 s, 47.9 s | 14.1 s |
| OWL micro | yes | 24 | 22.4 s | 1,026 ms | 32.7 s, 23.0 s | 3.7 s |
| OWL micro | no | 24 | 17.4 s | 861 ms | 23.7 s, 22.5 s | 4.6 s |
| none: SPARQL updates (`sparql-rules.sh`) | yes | — | 2.7 s for the inference (one round; the second, empty round 2.2 s more) | not measured | not measured | not measured |
| SPARQL updates | no | — | misses 6 triples (a two-step subproperty chain) | | | |
| none: the semantic service of M5.6 (D-64 built: the rules as updates in the commit, `DWISVerb` values and `DWISNoun` types left out) | yes | — | 4.7 s (2,159 inferred triples) | 575 ms (15 s in all) | 22.4 s, 21.4 s | 2.1 s |

What they say: loaded under the OWL micro view, the closure makes commits slower, not faster. The reasoner compiles one backward rule per `rdfs:domain` and `rdfs:range` axiom (551 in the vocabulary, 1,259 with the closure) and tries each one for every type question, so a commit costs 2 to 3 s per 200 subjects whatever the predicates asked; the pruned questions save 15 % on the commit and a third on the swap. The instance rules alone are correct but Jena's backward engine is slow on rules whose head matches every goal. The saving D-47 was after comes with option C: the per-manifest inference as a fixed sequence of SPARQL updates over the closed schema (2.7 s instead of 18 s), which needs the closure. Decision D-64 (taken 27 November 2026): option C. The semantic service of M5.6 derives a manifest's consequences with the SPARQL rules (`src/DWIS.Semantic.Ingest/inference-rules.sparql` of the Semantic repository, the prototype here made data), leaves out the `DWISVerb` values and the `DWISNoun` types (60 % of the inferred graph, saying nothing), and refuses a vocabulary version loaded without its closure markers; the loader's default is `VOCABULARY_CLOSURE=on`. The inference view `/dwis-inf` stays in the Fuseki configuration for questions by hand and for the smoke's checks, unused by the service.
