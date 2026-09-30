#!/usr/bin/env python3
"""Source completeness at the clad_engine JSON entry point.

Run from the repository root:
    python3 tools/fixes_3009/test_clad_source_completeness.py

The two expected failures document the direct JSON/CLI limitation: an unsupported
raw opening is removed before containment grouping. The C# commands reject the
entire selected raw source set before calling this engine; that host guard is not
executed by these tests. An unsupported independent zone may still be skipped.
Shapely is an independent test oracle, never a production dependency.
"""
import copy
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AClad" / "engine"))
import clad_engine
from shapely.geometry import Polygon, box
from shapely.ops import unary_union

OPS = ("cladding", "tile_pattern")
OUTER = [[0, 0], [2400, 0], [2400, 2400], [0, 2400]]
HOLE = [[600, 600], [1200, 600], [1200, 1200], [600, 1200]]
FAR = [[6000, 0], [8400, 0], [8400, 2400], [6000, 2400]]
# This inner rectangle is inside the opening for either direction of its
# 0.1-bulge edge: the edge's 30 mm sagitta cannot reach this 50 mm inset.
HOLE_CORE = box(650, 650, 1150, 1150)


def request(op):
    return {"op": op, "tile": {"w": 600, "h": 600},
            "gap": {"v": 0, "h": 0}, "min_cut": 0, "min_piece": 0,
            "bond": {"kind": "none"}, "merge_touching": False,
            "shaped": "keep", "tiny_mode": "layer", "mode": "edge"}


def zone(zone_id, outer, arc_hole=False):
    return {"zone_id": zone_id, "zone": {
        "id": zone_id, "units": "mm", "outer": {"pts": copy.deepcopy(outer)},
        "openings": [{"id": "HOLE", "poly": {"pts": copy.deepcopy(HOLE),
                      "bulges": [0.1, 0, 0, 0]}}] if arc_hole else []}}


def coverage(result):
    output = result.get("pieces", result.get("inserts", []))
    polygons = []
    for piece in output:
        if piece.get("rings"):
            polygons.append(Polygon(piece["rings"][0], piece["rings"][1:]))
        elif piece.get("pts"):
            polygons.append(Polygon(piece["pts"]))
        else:
            polygons.append(box(piece["x"], piece["y"],
                                piece["x"] + piece["w"], piece["y"] + piece["h"]))
    return unary_union(polygons)


class CladSourceCompleteness(unittest.TestCase):
    def assert_exact_coverage(self, result, expected, label):
        actual = coverage(result)
        self.assertTrue(actual.is_valid, label)
        difference = actual.symmetric_difference(expected).area
        print(f"{label}: area_mm2={actual.area:.1f}, difference_mm2={difference:.1f}")
        self.assertLessEqual(difference, 0.01, label)

    def raw_arc_hole(self, op):
        req = request(op)
        req["contours"] = [{"id": "A", "pts": OUTER},
                           {"id": "HOLE", "pts": HOLE, "bulges": [0.1, 0, 0, 0]}]
        result = clad_engine.run(req)
        covered = coverage(result).intersection(HOLE_CORE).area
        owners = [p["zone_id"] for p in result.get("per_zone", [])]
        print(f"{op}/raw-arc-hole: ok={result['ok']}, "
              f"covered_hole_core_mm2={covered:.1f}, owners={owners}")
        # Rejecting the containing facade or supporting the arc geometrically
        # are both safe; silently removing the opening is not.
        self.assertLessEqual(covered, 0.01, result.get("notes"))

    @unittest.expectedFailure
    def test_atclad_raw_arc_hole_known_cli_limitation(self):
        self.raw_arc_hole("cladding")

    @unittest.expectedFailure
    def test_attile_raw_arc_hole_known_cli_limitation(self):
        self.raw_arc_hole("tile_pattern")

    def test_zone_arc_opening_rejects_entire_zone(self):
        for op in OPS:
            with self.subTest(op=op):
                req = request(op)
                req["zones"] = [zone("BAD_ZONE", OUTER, arc_hole=True)]
                result = clad_engine.run(req)
                self.assertFalse(result["ok"], result)
                self.assertFalse(result.get("per_zone"), result)
                self.assert_exact_coverage(result, Polygon(), op + "/arc-zone-only")
                self.assertTrue(any("дуг" in n for n in result.get("notes", [])))

    def test_independent_raw_arc_does_not_reject_valid_facade(self):
        for op in OPS:
            with self.subTest(op=op):
                req = request(op)
                req["contours"] = [{"id": "A", "pts": OUTER},
                                   {"id": "FAR_ARC", "pts": FAR, "bulges": [0.1, 0, 0, 0]}]
                result = clad_engine.run(req)
                self.assertTrue(result["ok"], result)
                self.assertEqual([p["zone_id"] for p in result["per_zone"]], ["контур A"])
                self.assert_exact_coverage(result, Polygon(OUTER), op + "/independent-raw-arc")

    def test_zone_arc_opening_preserves_independent_valid_zone(self):
        for op in OPS:
            with self.subTest(op=op):
                req = request(op)
                req["zones"] = [zone("BAD_ZONE", OUTER, arc_hole=True), zone("FAR", FAR)]
                result = clad_engine.run(req)
                self.assertTrue(result["ok"], result)
                self.assertEqual([p["zone_id"] for p in result["per_zone"]], ["FAR"])
                self.assert_exact_coverage(result, Polygon(FAR), op + "/arc-zone-plus-valid-zone")

    def test_zone_arc_opening_preserves_independent_raw_facade(self):
        for op in OPS:
            with self.subTest(op=op):
                req = request(op)
                req["zones"] = [zone("BAD_ZONE", OUTER, arc_hole=True)]
                req["contours"] = [{"id": "FAR", "pts": FAR}]
                result = clad_engine.run(req)
                self.assertTrue(result["ok"], result)
                self.assertEqual([p["zone_id"] for p in result["per_zone"]], ["контур FAR"])
                self.assert_exact_coverage(result, Polygon(FAR), op + "/arc-zone-plus-raw-facade")


if __name__ == "__main__":
    unittest.main(verbosity=2)
