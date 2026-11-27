#!/usr/bin/env bash
# Prototype of option C of decision D-47 (M5.6, 27 November 2026; the decision row D-64 in the Design
# repository): the per-manifest inference as a fixed sequence of SPARQL updates over the closed schema,
# with no inference view. Against a stack with the closure loaded (VOCABULARY_CLOSURE=on) and the
# SmartConnect manifest live (its inferred graph written by the view, for the comparison):
#   ./sparql-rules.sh
# Derives the manifest's consequences into a scratch graph <…/proto> with eight INSERT updates, run
# twice to show the second round adds nothing, keeps what the service keeps (the manifest's subjects,
# minus what is asserted in any non-inferred graph, minus the trivia) and compares with the view's
# inferred graph. Measured: the same 5,454 triples, digest for digest, in 2.7 s for the first round
# (18 to 20 s for the view's answer in the commit); without the closure loaded, 6 triples are missed
# (a two-step subproperty chain), so these rules need the closure and the closure pays with these rules.
# The rules are the instance rules of Jena's owl-fb-micro.rules, named after them; hasValue, symmetric
# and transitive properties are not in the vocabulary of 2026-10-14 and are not written here.
set -euo pipefail
FUSEKI="${FUSEKI:-http://127.0.0.1:3030}"
G="${MANIFEST_GRAPH:-http://d-wis.org/manifests/SmartConnect/smartconnect-rig-signals}"
P="${G}/proto"
PFX='PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#> PREFIX owl: <http://www.w3.org/2002/07/owl#>'
update() { curl -sS --fail -X POST "${FUSEKI}/dwis/update" -H 'Content-Type: application/sparql-update' --data-binary "${PFX} $1" > /dev/null; }
count() { curl -sS --fail -G "${FUSEKI}/dwis/sparql" -H 'Accept: text/csv' --data-urlencode "query=SELECT (COUNT(*) AS ?n) WHERE { GRAPH <${P}> { ?s ?p ?o } }" | tail -1 | tr -d '\r'; }
ms() { echo $(( ($(date +%s%N) - $1) / 1000000 )); }
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

update "DROP SILENT GRAPH <${P}>"
start=$(date +%s%N)
for round in 1 2; do
  before=$(count)
  # rdfs6: super-property values (closed: one step). inverseOf2b: inverse values.
  update "INSERT { GRAPH <${P}> { ?s ?q ?o } } WHERE { { GRAPH <${G}> { ?s ?p ?o } } UNION { GRAPH <${P}> { ?s ?p ?o } } ?p rdfs:subPropertyOf ?q FILTER (?p != ?q) }"
  update "INSERT { GRAPH <${P}> { ?o ?q ?s } } WHERE { { GRAPH <${G}> { ?s ?p ?o } } UNION { GRAPH <${P}> { ?s ?p ?o } } ?p owl:inverseOf ?q FILTER (isIRI(?o)) }"
  # rdfs2, rdfs3: domain and range types (closed: the inherited domains and ranges are there).
  update "INSERT { GRAPH <${P}> { ?s rdf:type ?c } } WHERE { { GRAPH <${G}> { ?s ?p ?o } } UNION { GRAPH <${P}> { ?s ?p ?o } } ?p rdfs:domain ?c }"
  update "INSERT { GRAPH <${P}> { ?o rdf:type ?c } } WHERE { { GRAPH <${G}> { ?s ?p ?o } } UNION { GRAPH <${P}> { ?s ?p ?o } } ?p rdfs:range ?c FILTER (isIRI(?o)) }"
  # rdfs9: superclasses (closed: one step).
  update "INSERT { GRAPH <${P}> { ?s rdf:type ?d } } WHERE { { GRAPH <${G}> { ?s rdf:type ?c } } UNION { GRAPH <${P}> { ?s rdf:type ?c } } ?c rdfs:subClassOf ?d FILTER (?c != ?d) }"
  # someRec2b: the restriction's class, then its named equivalents and their superclasses.
  update "INSERT { GRAPH <${P}> { ?x rdf:type ?r } } WHERE { ?r owl:onProperty ?p ; owl:someValuesFrom ?d . { GRAPH <${G}> { ?x ?p ?a } } UNION { GRAPH <${P}> { ?x ?p ?a } } { { GRAPH <${G}> { ?a rdf:type ?d } } UNION { GRAPH <${P}> { ?a rdf:type ?d } } UNION { ?a rdf:type ?d } } }"
  update "INSERT { GRAPH <${P}> { ?x rdf:type ?n } } WHERE { GRAPH <${P}> { ?x rdf:type ?r } { ?n owl:equivalentClass ?r } UNION { ?r owl:equivalentClass ?n } FILTER (isIRI(?n)) }"
  update "INSERT { GRAPH <${P}> { ?s rdf:type ?d } } WHERE { GRAPH <${P}> { ?s rdf:type ?c } ?c rdfs:subClassOf ?d FILTER (?c != ?d) }"
  after=$(count)
  echo "round ${round}: ${before} -> ${after} triples in the scratch graph after $(ms $start) ms"
done
total_ms=$(ms $start)

# What the service keeps (Inference.Subtract): the subjects of the asserted graph, minus what is asserted
# in any non-inferred graph, minus the trivia (blank-node objects, owl:Thing, rdfs:Resource).
curl -sS --fail -G "${FUSEKI}/dwis/sparql" -H 'Accept: text/csv' --data-urlencode "query=SELECT DISTINCT ?s WHERE { GRAPH <${G}> { ?s ?p ?o } FILTER (isIRI(?s)) }" | tail -n +2 | tr -d '\r' | sort -u > "$work/subjects"
curl -sS --fail -G "${FUSEKI}/dwis/sparql" -H 'Accept: application/n-triples' --data-urlencode "query=${PFX} CONSTRUCT { ?s ?p ?o } WHERE { GRAPH <${P}> { ?s ?p ?o } FILTER (isIRI(?o) || isLiteral(?o)) FILTER (?o != owl:Thing && ?o != rdfs:Resource) FILTER NOT EXISTS { GRAPH ?g { ?s ?p ?o } FILTER (!STRENDS(STR(?g), '/inferred') && ?g != <${P}>) } }" \
  | sed 's/[[:space:]]*$//' | sort -u > "$work/all.nt"
awk 'NR==FNR { s["<" $0 ">"]=1; next } ($1 in s)' "$work/subjects" "$work/all.nt" | sort -u > "$work/inferred.nt"
curl -sS --fail -G "${FUSEKI}/dwis/data" -H 'Accept: application/n-triples' --data-urlencode "graph=${G}/inferred" | sed 's/[[:space:]]*$//' | sort -u > "$work/view.nt"
echo "the SPARQL updates: $(wc -l < "$work/inferred.nt") inferred triples in ${total_ms} ms for two rounds (digest $(sha1sum "$work/inferred.nt" | cut -c1-12))"
echo "the view's inferred graph: $(wc -l < "$work/view.nt") triples (digest $(sha1sum "$work/view.nt" | cut -c1-12))"
echo "view only: $(comm -23 "$work/view.nt" "$work/inferred.nt" | wc -l | tr -d ' '); updates only: $(comm -13 "$work/view.nt" "$work/inferred.nt" | wc -l | tr -d ' ')"
comm -23 "$work/view.nt" "$work/inferred.nt" | head -5
comm -13 "$work/view.nt" "$work/inferred.nt" | head -5
update "DROP SILENT GRAPH <${P}>"
