#!/usr/bin/env python3
"""T-SEM-7: compare a manifest's inferred graph, as the semantic service wrote it from the Fuseki
inference view (OWL micro rules), with the OWL 2 RL closure computed outside the store by owlrl.

Usage:
    python owlrl-compare.py --vocabulary vocabulary.nt --units units.nt --asserted asserted.nt --inferred inferred.nt

The four files are the graphs of /dwis as N-Triples (Graph Store Protocol GET with
Accept: application/n-triples). The closure runs on vocabulary + units + asserted; only triples whose
subject is one of the asserted graph's subjects count, minus the asserted triples, minus the trivia the
service drops (owl:Thing, rdfs:Resource, blank-node objects, owl:sameAs / owl:differentFrom).
Prints what the view has that owlrl lacks and the reverse, grouped by predicate and by type, with the
likely cause. Needs: pip install owlrl rdflib (owlrl 7.x).
"""
import argparse
import sys
from collections import Counter

import rdflib
from owlrl import DeductiveClosure, OWLRL_Semantics
from rdflib import RDF, RDFS, OWL

TRIVIA_TYPES = {OWL.Thing, RDFS.Resource}
TRIVIA_PREDICATES = {OWL.sameAs, OWL.differentFrom}


def load(path):
    g = rdflib.Graph()
    g.parse(path, format="nt")
    return g


def interesting(triples, subjects, asserted):
    out = set()
    for s, p, o in triples:
        if s not in subjects or (s, p, o) in asserted:
            continue
        if isinstance(o, rdflib.BNode) or p in TRIVIA_PREDICATES or (p == RDF.type and o in TRIVIA_TYPES):
            continue
        out.add((s, p, o))
    return out


def summarize(label, triples):
    print(f"\n{label}: {len(triples)} triples")
    by_predicate = Counter(p for _, p, _ in triples)
    for p, n in by_predicate.most_common(12):
        print(f"  {n:7d}  {p}")
    types = Counter(o for _, p, o in triples if p == RDF.type)
    if types:
        print("  types:")
        for o, n in types.most_common(12):
            print(f"  {n:7d}  a {o}")


def main():
    ap = argparse.ArgumentParser()
    for name in ("vocabulary", "units", "asserted", "inferred"):
        ap.add_argument(f"--{name}", required=True)
    args = ap.parse_args()

    vocabulary, units, asserted, inferred = (load(getattr(args, n)) for n in ("vocabulary", "units", "asserted", "inferred"))
    subjects = {s for s in asserted.subjects() if isinstance(s, rdflib.URIRef)}
    asserted_set = set(asserted)
    print(f"vocabulary {len(vocabulary)}, units {len(units)}, asserted {len(asserted)} ({len(subjects)} IRI subjects), inferred by the view {len(inferred)}")

    closure = rdflib.Graph()
    for g in (vocabulary, units, asserted):
        closure += g
    before = len(closure)
    DeductiveClosure(OWLRL_Semantics, axiomatic_triples=False, datatype_axioms=False).expand(closure)
    print(f"owlrl OWL 2 RL closure: {before} -> {len(closure)} triples")

    rl = interesting(closure, subjects, asserted_set)
    view = interesting(inferred, subjects, asserted_set)
    summarize("owlrl, about the manifest's subjects, minus asserted and trivia", rl)
    summarize("the view (inferred graph), same reduction", view)
    summarize("in the view, not in owlrl", view - rl)
    summarize("in owlrl, not in the view", rl - view)
    print(f"\nagreement: {len(view & rl)} shared; view-only {len(view - rl)}; owlrl-only {len(rl - view)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
