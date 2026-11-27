#!/usr/bin/env python3
"""Decision D-47: the closure of the vocabulary and the units, computed once, offline, and shipped with the
Blackboard as part of the vocabulary version.

The closure is everything the inference view's reasoner (Apache Jena, OWL micro rules, fuseki/config/dwis.ttl)
derives about the schema itself and about the unit and quantity individuals: the subclass and subproperty
chains, the domains and ranges a property inherits, the two sides of an equivalence or an inverse, the
members of a union or an intersection, and the types and property values of the individuals that follow.
The rules below are the schema-level rules of Jena's owl-fb-micro.rules, named after them, so that the
view and the shipped file never disagree (Appendix D.10). The view is not used to compute it: asked for
the whole store it runs out of memory, asked in batches it takes minutes per batch (27 November 2026).

Usage:
    python make-closure.py [--vocabulary fixtures/vocabulary] [--version 2026-10-14] [--out fixtures/vocabulary/closure]
    python make-closure.py --check          # exit 1 when the shipped files differ from a fresh run

Input: DWISVocabulary.ttl, advancedDefinitions/*.ttl and unit-and-quantity-individuals.ttl, read as the
loader (compose/load-vocabulary.sh) reads them, http://ddhub.no/ translated to http://d-wis.org/.
Output: closure/vocabulary.nt and closure/units.nt, sorted N-Triples without blank nodes, each with a
marker triple <graph> <http://d-wis.org/blackboard/closedBy> "…", which the loader appends to the
vocabulary graph and the units graph. The reasoner's trivia is left out (blank nodes, axioms about the
rdf, rdfs, owl and xsd terms, owl:Thing and rdfs:Resource as types, domains and ranges, reflexive
subclass, subproperty and equivalence triples).

The tool stops with an error on an OWL construct the rules do not cover (owl:allValuesFrom,
owl:hasValue, the plain cardinalities), so that a vocabulary that starts using one is noticed; the
OWL 2 constructs the OWL micro rules ignore (owl:hasKey, owl:propertyChainAxiom, the qualified
cardinalities, owl:AllDisjointClasses) are ignored here too. Needs: pip install rdflib.
"""
import argparse
import os
import re
import sys
import tempfile
from collections import defaultdict

import rdflib
from rdflib import BNode, Literal, URIRef
from rdflib.namespace import OWL, RDF, RDFS, XSD

ENGINE = "OWLMicroFBRuleReasoner rules, make-closure.py"
MARKER = URIRef("http://d-wis.org/blackboard/closedBy")
UNITS_NS = "http://d-wis.org/UnitAndQuantity/"
BUILT_IN = (str(RDF), str(RDFS), str(OWL), str(XSD), "http://jena.hpl.hp.com/")
TRIVIA_OBJECTS = {OWL.Thing, OWL.Nothing, RDFS.Resource, RDFS.Class, RDF.Property, RDFS.Datatype, RDFS.Literal}
UNSUPPORTED = (OWL.allValuesFrom, OWL.hasValue, OWL.minCardinality, OWL.maxCardinality, OWL.cardinality)


def load(path):
    with open(path, encoding="utf-8") as f:
        text = f.read().replace("http://ddhub.no/", "http://d-wis.org/")
    g = rdflib.Graph()
    g.parse(data=text, format="turtle")
    return g


def list_members(g, head):
    while head and head != RDF.nil:
        first = g.value(head, RDF.first)
        if first is not None:
            yield first
        head = g.value(head, RDF.rest)


class Closure:
    """The triples, as a set, with indexes by predicate; add() returns True when the triple is new."""

    def __init__(self, triples):
        self.triples = set()
        self.by_p = defaultdict(set)   # p -> {(s, o)}
        self.by_sp = defaultdict(set)  # (s, p) -> {o}
        self.by_po = defaultdict(set)  # (p, o) -> {s}
        for t in triples:
            self.add(*t)

    def add(self, s, p, o):
        t = (s, p, o)
        if t in self.triples:
            return False
        self.triples.add(t)
        self.by_p[p].add((s, o))
        self.by_sp[(s, p)].add(o)
        self.by_po[(p, o)].add(s)
        return True

    def objects(self, s, p):
        return set(self.by_sp.get((s, p), ()))

    def subjects(self, p, o):
        return set(self.by_po.get((p, o), ()))

    def pairs(self, p):
        return set(self.by_p.get(p, ()))


def transitive(c, p):
    """rdfs8/rdfs5 (Jena's transitive closure cache): p is transitive."""
    added = 0
    changed = True
    while changed:
        changed = False
        for a, b in list(c.pairs(p)):
            for d in list(c.objects(b, p)):
                if a != d and c.add(a, p, d):
                    added += 1
                    changed = True
    return added


def close(g):
    c = Closure(g)
    for p in UNSUPPORTED:
        if c.pairs(p):
            sys.exit(f"the vocabulary uses {p.n3(g.namespace_manager)}, which these rules do not cover; extend make-closure.py first")

    # The restrictions, by node: (onProperty, kind, value) for restrictionEq1 to 6 and someRec2b.
    restrictions = {}
    for r, p in c.pairs(OWL.onProperty):
        for kind in (OWL.someValuesFrom,):
            for v in c.objects(r, kind):
                restrictions[r] = (p, kind, v)

    rounds = 0
    while True:
        rounds += 1
        before = len(c.triples)

        # equivalentClass1, equivalentProperty1: an equivalence is a subclass (subproperty) both ways.
        for a, b in c.pairs(OWL.equivalentClass):
            c.add(a, RDFS.subClassOf, b)
            c.add(b, RDFS.subClassOf, a)
        for a, b in c.pairs(OWL.equivalentProperty):
            c.add(a, RDFS.subPropertyOf, b)
            c.add(b, RDFS.subPropertyOf, a)
        # unionOf1: every member of a union is a subclass of it. intersectionOf1: an intersection is a
        # subclass of every member.
        for cls, head in c.pairs(OWL.unionOf):
            for m in list_members(g, head):
                c.add(m, RDFS.subClassOf, cls)
        for cls, head in c.pairs(OWL.intersectionOf):
            for m in list_members(g, head):
                c.add(cls, RDFS.subClassOf, m)
        # restrictionEq1 to 6: two restrictions on the same property with the same value are equivalent.
        by_shape = defaultdict(list)
        for r, shape in restrictions.items():
            by_shape[shape].append(r)
        for nodes in by_shape.values():
            for r1 in nodes:
                for r2 in nodes:
                    if r1 != r2:
                        c.add(r1, OWL.equivalentClass, r2)
        # The transitive closures of subClassOf and subPropertyOf.
        transitive(c, RDFS.subClassOf)
        transitive(c, RDFS.subPropertyOf)
        # equivalentClass2, equivalentProperty2: subclass (subproperty) both ways is an equivalence.
        for a, b in list(c.pairs(RDFS.subClassOf)):
            if a != b and b in c.objects(a, RDFS.subClassOf) and a in c.objects(b, RDFS.subClassOf):
                c.add(a, OWL.equivalentClass, b)
        for a, b in list(c.pairs(RDFS.subPropertyOf)):
            if a != b and a in c.objects(b, RDFS.subPropertyOf):
                c.add(a, OWL.equivalentProperty, b)
        # inverseOf1: an inverse is symmetric. inverseof5, inverseof6: the range of a property is the
        # domain of its inverse and the reverse. inverseOf3, inverseOf4: functional and inverse functional
        # swap over an inverse.
        for p, q in list(c.pairs(OWL.inverseOf)):
            c.add(q, OWL.inverseOf, p)
            for cls in c.objects(p, RDFS.range):
                c.add(q, RDFS.domain, cls)
            for cls in c.objects(p, RDFS.domain):
                c.add(q, RDFS.range, cls)
            if OWL.FunctionalProperty in c.objects(p, RDF.type):
                c.add(q, RDF.type, OWL.InverseFunctionalProperty)
            if OWL.InverseFunctionalProperty in c.objects(p, RDF.type):
                c.add(q, RDF.type, OWL.FunctionalProperty)
        # rdfs2a, rdfs3a: a domain (range) holds for every superclass of it. rdfsder1, rdfsder2: a
        # subproperty inherits the domain and the range of its superproperties.
        for p, cls in list(c.pairs(RDFS.domain)):
            for sup in c.objects(cls, RDFS.subClassOf):
                c.add(p, RDFS.domain, sup)
        for p, cls in list(c.pairs(RDFS.range)):
            for sup in c.objects(cls, RDFS.subClassOf):
                c.add(p, RDFS.range, sup)
        for p, q in list(c.pairs(RDFS.subPropertyOf)):
            if p != q:
                for cls in c.objects(q, RDFS.domain):
                    c.add(p, RDFS.domain, cls)
                for cls in c.objects(q, RDFS.range):
                    c.add(p, RDFS.range, cls)

        # The individuals (the units and quantities, and anything else with instance triples).
        # rdfs6: a subproperty's value is the superproperty's value. inverseOf2b: an inverse's value.
        # symmetricProperty1b, transitiveProperty1b: for a symmetric or transitive property.
        for p, q in list(c.pairs(RDFS.subPropertyOf)):
            if p != q:
                for s, o in list(c.pairs(p)):
                    c.add(s, q, o)
        for p, q in list(c.pairs(OWL.inverseOf)):
            for s, o in list(c.pairs(p)):
                if not isinstance(o, Literal):
                    c.add(o, q, s)
        for p in c.subjects(RDF.type, OWL.SymmetricProperty):
            for s, o in list(c.pairs(p)):
                c.add(o, p, s)
        for p in c.subjects(RDF.type, OWL.TransitiveProperty):
            transitive(c, p)
        # rdfs2, rdfs3: a subject gets the property's domain as a type, an object its range.
        for p, cls in list(c.pairs(RDFS.domain)):
            for s, o in list(c.pairs(p)):
                c.add(s, RDF.type, cls)
        for p, cls in list(c.pairs(RDFS.range)):
            for s, o in list(c.pairs(p)):
                if not isinstance(o, Literal):
                    c.add(o, RDF.type, cls)
        # someRec2b: a subject with a value of the right type is of the restriction's class.
        for r, (p, kind, v) in restrictions.items():
            if kind == OWL.someValuesFrom:
                for s, o in list(c.pairs(p)):
                    if v in c.objects(o, RDF.type) or (isinstance(o, Literal) and o.datatype == v):
                        c.add(s, RDF.type, r)
        # rdfs9: a type's superclasses are types too.
        for s, cls in list(c.pairs(RDF.type)):
            for sup in c.objects(cls, RDFS.subClassOf):
                c.add(s, RDF.type, sup)

        if len(c.triples) == before:
            return c, rounds


def is_trivia(s, p, o):
    if isinstance(s, BNode) or isinstance(o, BNode):
        return True
    if any(str(s).startswith(ns) for ns in BUILT_IN):
        return True
    if o in TRIVIA_OBJECTS:
        return True
    return s == o


def n3(t):
    return t.n3()


def lines(triples, graph_iri, engine):
    out = {f"{n3(s)} {n3(p)} {n3(o)} ." for s, p, o in triples}
    out.add(f"{URIRef(graph_iri).n3()} {MARKER.n3()} {Literal(engine).n3()} .")
    return sorted(out)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    tests = os.path.dirname(os.path.dirname(here))
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--vocabulary", default=os.path.join(tests, "fixtures", "vocabulary"), help="the folder with DWISVocabulary.ttl, advancedDefinitions/ and unit-and-quantity-individuals.ttl")
    ap.add_argument("--version", default="2026-10-14", help="the vocabulary version: the graph IRIs of the marker triples")
    ap.add_argument("--out", default=None, help="output folder (default: <vocabulary>/closure)")
    ap.add_argument("--check", action="store_true", help="compare the files in --out with a fresh run instead of writing them")
    args = ap.parse_args()
    out = args.out or os.path.join(args.vocabulary, "closure")

    g = rdflib.Graph()
    files = [os.path.join(args.vocabulary, "DWISVocabulary.ttl")]
    adv = os.path.join(args.vocabulary, "advancedDefinitions")
    files += sorted(os.path.join(adv, f) for f in os.listdir(adv) if f.endswith(".ttl"))
    files.append(os.path.join(args.vocabulary, "unit-and-quantity-individuals.ttl"))
    for f in files:
        g += load(f)
    asserted = set(g)
    print(f"{len(files)} files, {len(asserted)} asserted triples")

    c, rounds = close(g)
    derived = {t for t in c.triples if t not in asserted and not is_trivia(*t)}
    print(f"closure: {len(c.triples) - len(asserted)} derived triples in {rounds} rounds, {len(derived)} kept after the trivia")
    units = {t for t in derived if str(t[0]).startswith(UNITS_NS)}
    vocabulary = derived - units
    by_predicate = defaultdict(int)
    for s, p, o in derived:
        by_predicate[p] += 1
    for p, n in sorted(by_predicate.items(), key=lambda x: -x[1]):
        print(f"  {n:6d} {p.n3(g.namespace_manager)}")

    results = {
        "vocabulary.nt": lines(vocabulary, f"http://d-wis.org/vocabulary/{args.version}", ENGINE),
        "units.nt": lines(units, f"http://d-wis.org/units/{args.version}", ENGINE),
    }
    if args.check:
        differences = 0
        for name, content in results.items():
            path = os.path.join(out, name)
            try:
                with open(path, encoding="utf-8") as f:
                    shipped = [line.rstrip("\n") for line in f]
            except FileNotFoundError:
                print(f"missing: {path}")
                differences += 1
                continue
            missing = sorted(set(content) - set(shipped))
            extra = sorted(set(shipped) - set(content))
            if missing or extra:
                differences += 1
                print(f"{path}: {len(missing)} triples a fresh run adds, {len(extra)} it does not have")
                for line in (missing + extra)[:10]:
                    print(f"  {line}")
        print("the shipped closure is current" if differences == 0 else "the shipped closure is out of date: run make-closure.py")
        sys.exit(0 if differences == 0 else 1)

    os.makedirs(out, exist_ok=True)
    for name, content in results.items():
        path = os.path.join(out, name)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(content) + "\n")
        print(f"written: {path} ({len(content)} triples)")


if __name__ == "__main__":
    main()
