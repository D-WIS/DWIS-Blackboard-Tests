#!/usr/bin/env python3
"""Cut a big manifest into small self-contained manifests (M3.F, Tests #14).

The owner's standard scenario is many small manifests of about 10 variables, 30 nodes and 100
references, not one big one. This script takes the SmartConnect manifest (254 provided variables,
391 nodes, 1,537 references) and writes one small manifest per group of provided variables:

- the group's provided variables, in the order of the big manifest;
- the data point nodes that give them a dynamic value, and every node reachable from those through
  references between nodes (locations, equipment, the provider node), copied into the small manifest,
  since a different provider name means different IRIs and no reference can cross manifests;
- every reference whose subject is one of those nodes or one of the group's variables, except a
  `HasDynamicValue` reference to a variable outside the group (the node is then copied without its
  dynamic value, so that the reference is never dangling);
- its own provider name (`SmartConnect-01`, ...) and manifest name.

Usage:
    python split-manifest.py [--input fixtures/manifests/smartconnect.manifest.json]
                             [--output fixtures/manifests/small] [--size 10] [--prefix SmartConnect]

Prints one line per small manifest with its counts. Deterministic: the same input gives the same files.
"""
from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path

HAS_DYNAMIC_VALUE = "HasDynamicValue"


def verb_name(reference: dict) -> str:
    return reference["VerbURI"].rstrip("/").rsplit("/", 1)[-1]


def split(manifest: dict, size: int, prefix: str) -> list[dict]:
    info = manifest["InjectionInformation"]
    nodes_ns = info.get("InjectedNodesNamespaceAlias") or "Nodes"
    variables_ns = info.get("ProvidedVariablesNamespaceAlias") or "ProvidedVariables"
    nodes_by_name = {node["UniqueName"]: node for node in manifest["InjectedNodes"]}
    references = manifest["InjectedReferences"]

    # Outgoing references per node, and the data point nodes per provided variable.
    outgoing: dict[str, list[dict]] = defaultdict(list)
    data_points: dict[str, list[str]] = defaultdict(list)
    for reference in references:
        subject, obj = reference["Subject"], reference["Object"]
        if subject["NameSpace"] == nodes_ns:
            outgoing[subject["ID"]].append(reference)
            if verb_name(reference) == HAS_DYNAMIC_VALUE and obj["NameSpace"] == variables_ns:
                data_points[obj["ID"]].append(subject["ID"])
    by_variable: dict[str, list[dict]] = defaultdict(list)
    for reference in references:
        if reference["Subject"]["NameSpace"] == variables_ns:
            by_variable[reference["Subject"]["ID"]].append(reference)

    variables = manifest["ProvidedVariables"]
    groups = [variables[i : i + size] for i in range(0, len(variables), size)]
    width = max(2, len(str(len(groups))))
    small: list[dict] = []
    for index, group in enumerate(groups, start=1):
        variable_ids = {variable["VariableID"] for variable in group}
        # Closure over the references between nodes, from the data points of the group's variables.
        reached: list[str] = []
        seen: set[str] = set()
        queue = [name for variable in group for name in data_points.get(variable["VariableID"], [])]
        while queue:
            name = queue.pop(0)
            if name in seen or name not in nodes_by_name:
                continue
            seen.add(name)
            reached.append(name)
            for reference in outgoing[name]:
                obj = reference["Object"]
                if obj["NameSpace"] == nodes_ns and obj["ID"] not in seen:
                    queue.append(obj["ID"])
        # Keep the order of the big manifest for the nodes, so the output is stable and readable.
        node_set = set(reached)
        nodes = [node for node in manifest["InjectedNodes"] if node["UniqueName"] in node_set]
        kept: list[dict] = []
        for reference in references:
            subject, obj = reference["Subject"], reference["Object"]
            if subject["NameSpace"] == nodes_ns and subject["ID"] in node_set:
                if obj["NameSpace"] == variables_ns and obj["ID"] not in variable_ids:
                    continue  # a dynamic value of another group's variable: dropped, never dangling
                kept.append(reference)
            elif subject["NameSpace"] == variables_ns and subject["ID"] in variable_ids:
                kept.append(reference)
        number = str(index).zfill(width)
        small.append(
            {
                "ManifestName": f"{manifest['ManifestName']}, part {number}",
                "Provider": {"Name": f"{prefix}-{number}", "Company": manifest["Provider"]["Company"]},
                "InjectionInformation": dict(info),
                "InjectedVariables": [],
                "InjectedNodes": nodes,
                "InjectedReferences": kept,
                "ProvidedVariables": list(group),
                "InjectedVocabulary": manifest.get("InjectedVocabulary"),
            }
        )
    return small


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    root = Path(__file__).resolve().parents[2]
    parser.add_argument("--input", type=Path, default=root / "fixtures" / "manifests" / "smartconnect.manifest.json")
    parser.add_argument("--output", type=Path, default=root / "fixtures" / "manifests" / "small")
    parser.add_argument("--size", type=int, default=10, help="provided variables per small manifest")
    parser.add_argument("--prefix", default="SmartConnect", help="provider name prefix; the part number is appended")
    args = parser.parse_args()

    manifest = json.loads(args.input.read_text(encoding="utf-8-sig"))
    small = split(manifest, args.size, args.prefix)
    args.output.mkdir(parents=True, exist_ok=True)
    for old in args.output.glob(f"{args.prefix.lower()}-*.manifest.json"):
        old.unlink()
    total_nodes = total_references = 0
    for part in small:
        name = f"{part['Provider']['Name'].lower()}.manifest.json"
        (args.output / name).write_text(json.dumps(part, indent=2) + "\n", encoding="utf-8", newline="\n")
        total_nodes += len(part["InjectedNodes"])
        total_references += len(part["InjectedReferences"])
        print(
            f"{name}: {len(part['ProvidedVariables'])} variables, {len(part['InjectedNodes'])} nodes, "
            f"{len(part['InjectedReferences'])} references"
        )
    print(
        f"{len(small)} small manifests from {len(manifest['ProvidedVariables'])} variables, "
        f"{len(manifest['InjectedNodes'])} nodes and {len(manifest['InjectedReferences'])} references; "
        f"copied nodes in all: {total_nodes}, references: {total_references}"
    )


if __name__ == "__main__":
    main()
