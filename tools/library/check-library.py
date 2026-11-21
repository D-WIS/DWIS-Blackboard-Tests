#!/usr/bin/env python3
"""Run the manifest library through the semantic service and compare the reports (M4.F, Tests #21).

The library (fixtures/manifests/library/) holds real manifests from the projects that wrote them, one
folder each, with the validation report the semantic service is expected to return for it
(expected-report.json). This script injects every manifest through the HTTP API, trims the report to
what is stable (the status, accepted, the vocabulary version, the counts, the live parts and the
findings; not the timings and sizes), compares it with the expected one, prints the timings, and
removes the manifest again so that whatever runs next sees the store as it was.

    check-library.py [--semantic URL] [--record] [--keep] [LIBRARY]

    --semantic URL  the API, default http://127.0.0.1:8080/api/v1 (the compose stack)
    --record        write expected-report.json from what the service answers (first run, or after a
                    change of the service or the vocabulary that the owner has reviewed)
    --keep          leave the manifests live (for a look at the store afterwards)
    LIBRARY         the library folder, default fixtures/manifests/library next to this repository

Exit code 0 when every report matches; 1 when one differs or has no expected report yet (the trimmed
report is printed in full so that it can be recorded from a CI log).
"""
import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

STABLE_FINDING_FIELDS = ("stage", "severity", "code", "message", "focus", "path", "value", "source")


def trim(status, body):
    """The stable part of an answer: a report, or an error body when the service refused the manifest."""
    if not isinstance(body, dict) or "findings" not in body:
        return {"status": status, "answer": body}
    findings = [{k: f.get(k) for k in STABLE_FINDING_FIELDS if f.get(k) is not None} for f in body.get("findings", [])]
    findings.sort(key=lambda f: json.dumps(f, sort_keys=True))
    return {
        "status": status,
        "manifestId": body.get("manifestId"),
        "accepted": body.get("accepted"),
        "vocabularyVersion": body.get("vocabularyVersion"),
        "counts": body.get("counts"),
        "live": body.get("live"),
        "findings": findings,
    }


def call(method, url, data=None):
    request = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=600) as response:
            raw = response.read()
            return response.status, raw
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def parse(raw):
    text = raw.decode("utf-8", errors="replace")
    try:
        return json.loads(text) if text else None
    except json.JSONDecodeError:
        return text


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--semantic", default="http://127.0.0.1:8080/api/v1")
    parser.add_argument("--record", action="store_true")
    parser.add_argument("--keep", action="store_true")
    parser.add_argument("library", nargs="?", default=str(Path(__file__).resolve().parents[2] / "fixtures" / "manifests" / "library"))
    args = parser.parse_args()

    library = Path(args.library)
    folders = sorted(p for p in library.iterdir() if (p / "manifest.json").is_file())
    if not folders:
        print(f"no manifest.json under {library}", file=sys.stderr)
        return 1

    failed = []
    live = []
    for folder in folders:
        manifest = (folder / "manifest.json").read_bytes()
        if manifest.startswith(b"\xef\xbb\xbf"):
            manifest = manifest[3:]
        start = time.monotonic()
        status, raw = call("POST", f"{args.semantic}/manifests", manifest)
        ms = int((time.monotonic() - start) * 1000)
        body = parse(raw)
        actual = trim(status, body)
        statistics = body.get("statistics", {}) if isinstance(body, dict) else {}
        summary = (f"accepted {actual.get('accepted')}, errors {actual.get('counts', {}).get('errors')}, "
                   f"warnings {actual.get('counts', {}).get('warnings')}, asserted {statistics.get('asserted')}, "
                   f"inferred {statistics.get('inferred')}" if "findings" in actual else f"status {status}")
        print(f"  {folder.name}: {ms} ms ({summary})")
        if actual.get("manifestId"):
            live.append(actual["manifestId"])

        expected_path = folder / "expected-report.json"
        if args.record:
            expected_path.write_text(json.dumps(actual, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            print(f"    recorded {expected_path}")
            continue
        if not expected_path.is_file():
            failed.append(folder.name)
            print(f"    no expected report yet; the trimmed report is:\n{json.dumps(actual, indent=2, ensure_ascii=False)}")
            continue
        expected = json.loads(expected_path.read_text(encoding="utf-8"))
        if expected != actual:
            failed.append(folder.name)
            print(f"    the report differs from {expected_path}:")
            print(f"    expected: {json.dumps(expected, sort_keys=True, ensure_ascii=False)}")
            print(f"    actual:   {json.dumps(actual, sort_keys=True, ensure_ascii=False)}")

    if not args.keep:
        for manifest_id in live:
            status, _ = call("DELETE", f"{args.semantic}/manifests/{manifest_id}")
            if status != 204:
                print(f"  could not remove {manifest_id}: {status}", file=sys.stderr)
                failed.append(f"remove {manifest_id}")

    if failed:
        print(f"manifest library: {len(failed)} of {len(folders)} not as expected: {', '.join(failed)}")
        return 1
    print(f"manifest library: {len(folders)} manifests, every report as expected")
    return 0


if __name__ == "__main__":
    sys.exit(main())
