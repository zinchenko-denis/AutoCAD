#!/usr/bin/env python3
"""Reproduce support-join work and timings without AutoCAD or private fixtures.

Usage: python tools/catalog_connections_0110/reproduce_topology_performance.py \
           --out /path/to/evidence

The frozen brute-force oracle ships with the regression test, so a shallow CI
checkout is sufficient. Full outputs must match; runtime is observational and
is not a test threshold. The 10,000-member brute-force controls can take minutes.
"""
import argparse
import gc
import hashlib
import json
from pathlib import Path
import platform
import sys
import time


ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / "AFrame" / "engine"
sys.path.insert(0, str(ENGINE))

import frame_topology as indexed
from test_frame_topology_index import _legacyscreen_layout as brute_force


class CountingBracket(dict):
    x_reads = 0

    def __getitem__(self, key):
        if key == "x":
            type(self).x_reads += 1
        return super().__getitem__(key)


def fixture(count, tall=False, counting=False):
    rails = [dict(x=0.0 if tall else i * 100.0,
                  y0=i * 4000.0 if tall else 0.0,
                  y1=i * 4000.0 + 3000.0 if tall else 3000.0,
                  kind="ШП-60-20") for i in range(count)]
    factory = CountingBracket if counting else dict
    brackets = [factory(x=rail["x"], y=rail["y0"] + dy, kind="рядовой")
                for rail in rails for dy in (0.0, 1500.0, 3000.0)]
    return rails, brackets


def screen(function, rails, brackets):
    return function("vertical", rails, [], brackets, dict(rail_len=3000.0))


def digest(value):
    data = json.dumps(value, ensure_ascii=False, sort_keys=True,
                      separators=(",", ":"), allow_nan=False).encode("utf-8")
    return hashlib.sha256(data).hexdigest()


def source_hashes():
    paths = ["AFrame/engine/frame_topology.py", "AFrame/engine/frame_spatial.py",
             "AFrame/engine/test_frame_topology_index.py",
             "tools/catalog_connections_0110/reproduce_topology_performance.py"]
    return {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest()
            for path in paths}


def require(condition, message):
    # These checks also run under python -O.
    if not condition:
        raise AssertionError(message)


def check_geometric_api(rails, brackets, expected):
    diagnostics = {}
    members, horizontal_members = indexed.geometric_members(
        "vertical", rails, [], brackets, diagnostics=diagnostics)
    expected_members = [{key: value for key, value in member.items()
                         if key not in ("coefficient_span", "coefficient_span_class")}
                        for member in expected["members"]]
    require((members, horizontal_members) == (expected_members, expected["horizontal_members"]),
            "geometric_members disagrees with the frozen oracle")
    count = len(rails)
    require(diagnostics["point_queries"] == count, "unexpected point-query count")
    require(diagnostics["segment_queries"] == 0, "unexpected segment queries")
    require(diagnostics["matches_emitted"] == 3 * count, "unexpected match count")
    require(diagnostics["candidates_tested"] == 3 * count,
            "sparse joins regressed to unnecessary candidate comparisons")
    require(diagnostics["index_nodes_visited"] < 40 * count,
            "sparse index traversal no longer scales for this fixture")
    return diagnostics


def counting_measurement(count):
    rails, brackets = fixture(count, counting=True)
    CountingBracket.x_reads = 0
    expected = screen(brute_force, rails, brackets)
    baseline_reads = CountingBracket.x_reads
    require(baseline_reads == count * len(brackets), "brute-force reproduction changed")
    CountingBracket.x_reads = 0
    actual = screen(indexed.screen_layout, rails, brackets)
    indexed_reads = CountingBracket.x_reads
    require(actual == expected, "indexed and brute-force full outputs differ")
    require(indexed_reads <= 10 * count, "indexed bracket access is not linear on this fixture")
    diagnostics = check_geometric_api(rails, brackets, expected)
    return dict(rails=count, brackets=len(brackets),
                baseline_bracket_x_reads=baseline_reads,
                indexed_bracket_x_reads=indexed_reads,
                full_output_equal=True, output_sha256=digest(actual),
                diagnostics=diagnostics)


def timing_measurement(count, tall):
    rails, brackets = fixture(count, tall=tall)
    name = "tall_same_axis_stack" if tall else "separate_axes"
    print("Normal dictionaries: %s, %d rails; brute-force control" % (name, count), flush=True)
    gc.collect()
    start = time.perf_counter()
    expected = screen(brute_force, rails, brackets)
    baseline_seconds = time.perf_counter() - start
    print("Normal dictionaries: %s, %d rails; indexed control" % (name, count), flush=True)
    gc.collect()
    start = time.perf_counter()
    actual = screen(indexed.screen_layout, rails, brackets)
    indexed_seconds = time.perf_counter() - start
    require(actual == expected, "indexed and brute-force full outputs differ")
    diagnostics = check_geometric_api(rails, brackets, expected)
    return dict(fixture=name, rails=count, brackets=len(brackets),
                baseline_seconds=baseline_seconds, indexed_seconds=indexed_seconds,
                full_output_equal=True, output_sha256=digest(actual),
                diagnostics=diagnostics)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True, help="Output evidence directory")
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    before_hashes = source_hashes()
    results = dict(schema="facade_topology_performance/1",
                   environment=dict(python=platform.python_version(), platform=platform.platform()),
                   scope="Pure Python synthetic geometry; no native AutoCAD measurements.",
                   timing_policy="One measured run per fixture; observational, not a pass/fail threshold.",
                   baseline="Frozen 0a397c2 brute-force oracle from test_frame_topology_index.py",
                   source_sha256=before_hashes,
                   counting_dictionaries=[], normal_dictionaries=[])
    for count in (100, 500, 1000):
        results["counting_dictionaries"].append(counting_measurement(count))
        print("Counting dictionaries: %d rails, full equality confirmed" % count, flush=True)
    for tall in (False, True):
        for count in (1000, 10000):
            results["normal_dictionaries"].append(timing_measurement(count, tall))
    require(source_hashes() == before_hashes, "sources changed while the probe was running")
    results["status"] = "passed"
    target = args.out / "topology_performance.json"
    target.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("PASS: %s" % target, flush=True)


if __name__ == "__main__":
    main()
