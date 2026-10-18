#!/usr/bin/env python3
"""Completeness gate for a conformance results.json (ADR-0032 section 3 / Compliance).

Usage: check-conformance-results.py <results.json> <manifest.json> [<repo-root>]

Shared by ci.yml and release.yml so a tag cannot publish an asset CI would have refused.
"""
import json
import os
import sys

if len(sys.argv) < 3:
    print("usage: check-conformance-results.py <results.json> <manifest.json> [<repo-root>]", file=sys.stderr)
    sys.exit(2)

results = json.load(open(sys.argv[1]))
manifest = json.load(open(sys.argv[2]))
root = sys.argv[3] if len(sys.argv) > 3 else "."
errors = []

# Bindings this package carries, derived from its code: the core always, plus `otel` when the W3C
# traceparent extract/inject code is in the core. The .NET broker transports are separate packages
# (own repositories, own conformance runs).
carried = {"core"}
if os.path.exists(os.path.join(root, "src/BabelQueue.Core/Tracing/Traceparent.cs")):
    carried.add("otel")
for binding in carried - {"core"}:
    if binding not in results["bindings"]:
        errors.append(f'binding "{binding}" is carried but missing from results.bindings')
for binding in results["bindings"]:
    if binding not in carried:
        errors.append(f'results.bindings lists "{binding}", which the package does not carry')

if results["conformance_version"] != manifest["conformance_version"]:
    errors.append("results.conformance_version differs from the vendored manifest")


def in_scope(scope):
    return scope == "core" or scope in results["bindings"]


for name, definition in manifest["sections"].items():
    present = name in results["sections"]
    if in_scope(definition["scope"]) and not present:
        errors.append(f'in-scope section "{name}" (scope {definition["scope"]}) is missing')
    if present and not results["sections"][name]:
        errors.append(f'section "{name}" has no cases')
    if not in_scope(definition["scope"]) and present:
        errors.append(f'section "{name}" (scope {definition["scope"]}) must be absent')

fail = 0
for section, cases in results["sections"].items():
    for case_id, outcome in cases.items():
        if outcome["status"] == "fail":
            fail += 1
            errors.append(f"{section}/{case_id}: fail")
if errors:
    print("\n".join(errors), file=sys.stderr)
    sys.exit(1)
total = sum(len(c) for c in results["sections"].values())
print(f"results.json complete: {len(results['sections'])} sections, {total} cases, {fail} failing")
