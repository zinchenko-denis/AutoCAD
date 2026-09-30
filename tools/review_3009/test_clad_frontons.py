#!/usr/bin/env python3
"""Independent AClad review regressions, 30.09.2026.

These assertions describe required geometry/safety invariants. The original six
reproduce failures on reviewed HEAD e2a4e4d; all tests must pass after fixes.
All input geometry is synthetic. Run from repository root:
  python3 tools/review_3009/test_clad_frontons.py
  python3 tools/review_3009/test_clad_frontons.py --render
  python3 tools/review_3009/test_clad_frontons.py --render-fixed
"""
import copy
import json
import math
from pathlib import Path
import random
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AClad" / "engine"))
from shapely.geometry import Polygon, box
from shapely.affinity import translate
from shapely.ops import unary_union
import clad_engine
from cladding_plan import cladding_plan
from tile_pattern import tile_pattern

GABLE = [[0, 0], [6000, 0], [6000, 3000], [3000, 4500], [0, 3000]]
OPENING = [[5000, 2600], [5300, 2600], [5300, 2900], [5000, 2900]]
DOUBLE_GABLE = [[0, 0], [6000, 0], [6000, 3000], [4500, 4500],
                [3000, 3000], [1500, 4500], [0, 3000]]

# Simple synthetic polygons. The last fixture has TWO disconnected regions
# inside a single 2400 x 1200 source tile in its upper row.
ORACLE_CASES = {
    "triangle": [[0, 0], [6000, 0], [3000, 1800]],
    "diamond": [[1800, 0], [3600, 1800], [1800, 3600], [0, 1800]],
    "double_gable": DOUBLE_GABLE,
    "irregular_roof": [[0, 0], [6000, 0], [6000, 3000], [5000, 4000],
                       [4200, 2800], [3000, 4200], [2100, 2700],
                       [1200, 3900], [0, 3000]],
    "side_notch": [[0, 0], [4800, 0], [4800, 3600], [3000, 2400],
                   [3600, 1200], [1800, 1800], [0, 3600]],
    "disconnected_cell": [[0, 0], [2400, 0], [2400, 1200],
                          [1800, 2400], [1200, 600], [600, 2400], [0, 1200]],
}


def request(outer=GABLE, holes=(), gap=10):
    return {"tile": {"w": 1200, "h": 1200}, "gap": {"v": gap, "h": gap},
            "bond": {"kind": "none"}, "anchor": {"h": "R", "v": "B"},
            "contours": [{"id": "G", "outer": copy.deepcopy(outer),
                          "holes": copy.deepcopy(list(holes))}],
            "min_piece": 0, "min_cut": 0, "tiny_mode": "layer",
            "shaped": "keep", "merge_touching": False, "mode": "edge"}


def geom(piece):
    if piece.get("pts"):
        return Polygon(piece["pts"])
    if piece.get("rings"):
        return Polygon(piece["rings"][0], piece["rings"][1:])
    return box(piece["x"], piece["y"], piece["x"] + piece["w"],
               piece["y"] + piece["h"])


def pieces(result):
    return result.get("pieces", result.get("inserts", []))


class FrontonReview(unittest.TestCase):
    def test_attile_keeps_enclosed_opening_after_slope_clip(self):
        result = tile_pattern(request(holes=[OPENING]))
        self.assertTrue(result["ok"])
        covered = sum(geom(p).intersection(Polygon(OPENING)).area
                      for p in pieces(result))
        self.assertLessEqual(covered, 0.01,
                             f"Opening must remain empty; covered {covered} mm2")

    def test_attile_concave_roof_preserves_coverage(self):
        result = tile_pattern(request(outer=DOUBLE_GABLE, gap=0))
        missing = Polygon(DOUBLE_GABLE).difference(
            unary_union([geom(p) for p in pieces(result)])).area
        self.assertLessEqual(missing, 0.01,
                             f"Zero joints, zero minimum cut; missing {missing} mm2")

    def test_atclad_concave_roof_preserves_coverage(self):
        result = cladding_plan(request(outer=DOUBLE_GABLE, gap=0))
        missing = Polygon(DOUBLE_GABLE).difference(
            unary_union([geom(p) for p in pieces(result)])).area
        self.assertLessEqual(missing, 0.01,
                             f"Zero joints, zero minimum cut; missing {missing} mm2")

    def test_atclad_accepts_triangular_fronton(self):
        result = cladding_plan(request(outer=[[0, 0], [6000, 0], [3000, 1500]]))
        self.assertTrue(result["ok"])
        self.assertGreater(len(result["inserts"]), 0, result["notes"])

    def test_atclad_rejected_zone_not_listed_as_success(self):
        result = clad_engine.run({
            "op": "cladding", "tile": {"w": 600, "h": 600},
            "gap": {"v": 8, "h": 8},
            "contours": [
                {"id": "GOOD", "pts": [[0, 0], [2400, 0], [2400, 2400], [0, 2400]]},
                {"id": "BAD", "pts": [[4000, 0], [6400, 0], [6410, 2400], [4000, 2400]]},
            ]})
        self.assertTrue(result["ok"])
        # CladCommand.OkOwners treats every per_zone entry as successful and
        # EraseFor removes old geometry for its owner (source review, no CAD).
        successful = [p["outer_id"] for p in result["per_zone"]]
        self.assertNotIn("BAD", successful, json.dumps(result["per_zone"]))

    def test_attile_large_coordinate_translation_preserves_pieces(self):
        original = request()
        original.update(tile={"w": 290, "h": 82}, gap={"v": 7, "h": 7},
                        min_piece=10, tiny_mode="absorb")
        translated = copy.deepcopy(original)
        offset = 4_000_000_000
        translated["contours"][0]["outer"] = [
            [x + offset, y + offset] for x, y in GABLE]
        a, b = tile_pattern(original), tile_pattern(translated)
        self.assertEqual(len(a["pieces"]), len(b["pieces"]),
                         "Translation must not delete tiles at the slope")


class FrontonOracleReview(unittest.TestCase):
    """Shapely is a test oracle only; product code must remain stdlib-only.

    Check unions, overlaps and each source grid cell, without constraining how
    an implementation represents disconnected pieces or polygon holes.
    """

    def assert_coverage(self, result, expected, offset=(0, 0)):
        self.assertTrue(result["ok"], result)
        actual_parts = []
        for index, piece in enumerate(pieces(result)):
            polygon = translate(geom(piece), -offset[0], -offset[1])
            self.assertTrue(polygon.is_valid, f"Invalid piece {index}: {piece}")
            self.assertGreater(polygon.area, 0, f"Empty piece {index}: {piece}")
            actual_parts.append(polygon)
        self.assertTrue(actual_parts, result.get("notes"))
        actual = unary_union(actual_parts)
        # Output vertices are serialized to 4 decimal places. This bound
        # permits only that rounding error (including at x/y = 4e9 mm).
        tolerance = max(0.02, sum(p.length for p in actual_parts) * 0.0001)
        missing = expected.difference(actual).area
        excess = actual.difference(expected).area
        overlap = sum(p.area for p in actual_parts) - actual.area
        self.assertLessEqual(missing, tolerance, f"Missing {missing} mm2")
        self.assertLessEqual(excess, tolerance, f"Outside facade/hole {excess} mm2")
        self.assertLessEqual(overlap, tolerance, f"Overlapping pieces {overlap} mm2")
        return actual

    def assert_attile_grid(self, req, result):
        """Independently intersect facade with the R/B, no-bond tile grid."""
        contour = req["contours"][0]
        facade = Polygon(contour["outer"], contour["holes"])
        x0, y0, x1, y1 = facade.bounds
        width, height = req["tile"]["w"], req["tile"]["h"]
        gx, gy = req["gap"]["v"], req["gap"]["h"]
        actual_by_cell = {}
        for piece in pieces(result):
            poly = geom(piece)
            cx, cy = poly.representative_point().coords[0]
            ix = math.floor((x1 - cx) / (width + gx))
            iy = math.floor((cy - y0) / (height + gy))
            actual_by_cell.setdefault((ix, iy), []).append(poly)
        expected_parts = []
        for ix in range(math.ceil((x1 - x0) / (width + gx))):
            right = x1 - ix * (width + gx)
            for iy in range(math.ceil((y1 - y0) / (height + gy))):
                bottom = y0 + iy * (height + gy)
                cell = box(right - width, bottom, right, bottom + height)
                expected = facade.intersection(cell)
                if expected.area:
                    expected_parts.append(expected)
                found = actual_by_cell.pop((ix, iy), [])
                actual = unary_union(found)
                tolerance = max(0.02, sum(p.length for p in found) * 0.0001)
                self.assertLessEqual(expected.symmetric_difference(actual).area,
                                     tolerance, f"Wrong grid cell {(ix, iy)}")
        self.assertFalse(actual_by_cell, "Pieces appear outside the reference grid")
        return unary_union(expected_parts)

    def test_attile_general_slopes_match_independent_grid(self):
        for name, outer in ORACLE_CASES.items():
            for axis in ("rows", "cols"):
                for reverse in (False, True):
                    with self.subTest(shape=name, axis=axis, reverse=reverse):
                        ring = outer[::-1] if reverse else outer
                        req = request(outer=ring, gap=0)
                        req.update(tile={"w": 1200, "h": 600}, axis=axis)
                        result = tile_pattern(req)
                        self.assert_coverage(result, Polygon(outer))
                        self.assert_attile_grid(req, result)

    def test_atclad_general_slopes_match_independent_polygon(self):
        for name, outer in ORACLE_CASES.items():
            for reverse in (False, True):
                with self.subTest(shape=name, reverse=reverse):
                    req = request(outer=outer[::-1] if reverse else outer, gap=0)
                    req.update(tile={"w": 1200, "h": 600})
                    self.assert_coverage(cladding_plan(req), Polygon(outer))

    def test_disconnected_fragments_of_one_source_tile_survive(self):
        outer = ORACLE_CASES["disconnected_cell"]
        upper_cell = box(0, 1200, 2400, 2400)
        expected_upper = Polygon(outer).intersection(upper_cell)
        self.assertEqual(expected_upper.geom_type, "MultiPolygon")
        self.assertEqual(len(expected_upper.geoms), 2)  # Fixture property only.
        for name, engine in (("ATTILE", tile_pattern), ("ATCLAD", cladding_plan)):
            with self.subTest(engine=name):
                req = request(outer=outer, gap=0)
                req["tile"] = {"w": 2400, "h": 1200}
                result = engine(req)
                self.assert_coverage(result, Polygon(outer))
                if name == "ATTILE":
                    self.assert_attile_grid(req, result)

    def test_enclosed_holes_and_winding_match_polygon(self):
        holes = [OPENING, [[900, 2700], [1150, 2700], [1150, 2950], [900, 2950]]]
        for name, engine in (("ATTILE", tile_pattern), ("ATCLAD", cladding_plan)):
            for reverse_outer in (False, True):
                for reverse_holes in (False, True):
                    with self.subTest(engine=name, outer=reverse_outer, holes=reverse_holes):
                        req = request(outer=GABLE[::-1] if reverse_outer else GABLE,
                                      holes=[h[::-1] if reverse_holes else h for h in holes],
                                      gap=0)
                        self.assert_coverage(engine(req), Polygon(GABLE, holes))

    def test_attile_nonzero_joints_preserve_concave_boundary_and_holes(self):
        # Physical X/Y grid is the same in rows/cols mode with no bond.
        for axis in ("rows", "cols"):
            for outer, holes in ((DOUBLE_GABLE, []), (GABLE, [OPENING])):
                with self.subTest(axis=axis, holes=bool(holes)):
                    req = request(outer=outer, holes=holes, gap=10)
                    req.update(axis=axis, gap_around=False)
                    result = tile_pattern(req)
                    self.assertTrue(result["ok"], result)
                    expected = self.assert_attile_grid(req, result)
                    self.assert_coverage(result, expected)

    def test_large_coordinate_translation_preserves_geometry(self):
        offset = (4_000_000_000, 4_000_000_000)
        for name, engine in (("ATTILE", tile_pattern), ("ATCLAD", cladding_plan)):
            for outer, holes in ((DOUBLE_GABLE, []), (GABLE, [OPENING])):
                with self.subTest(engine=name, holes=bool(holes)):
                    req = request(outer=outer, holes=holes, gap=0)
                    reference = self.assert_coverage(engine(req), Polygon(outer, holes))
                    shifted = copy.deepcopy(req)
                    contour = shifted["contours"][0]
                    contour["outer"] = [[x + offset[0], y + offset[1]] for x, y in outer]
                    contour["holes"] = [[[x + offset[0], y + offset[1]] for x, y in h]
                                        for h in holes]
                    if name == "ATCLAD":
                        shifted["origin"] = {"x": offset[0], "y": offset[1]}
                    translated = self.assert_coverage(engine(shifted), Polygon(outer, holes), offset)
                    self.assertLessEqual(reference.symmetric_difference(translated).area, 0.1)

    def test_atclad_cli_triangle_and_rejected_zone_contract(self):
        req = {"op": "cladding", "tile": {"w": 1200, "h": 600},
               "gap": {"v": 0, "h": 0}, "min_cut": 0, "mode": "edge",
               "contours": [
                   {"id": "TRIANGLE", "pts": ORACLE_CASES["triangle"]},
                   {"id": "REJECTED", "pts": [[7000, 0], [9400, 0],
                                                 [9410, 2400], [7000, 2400]]}]}
        with tempfile.TemporaryDirectory(prefix="fronton-review-") as tmp:
            input_path, output_path = Path(tmp) / "in.json", Path(tmp) / "out.json"
            input_path.write_text(json.dumps(req), encoding="utf-8")
            process = subprocess.run([sys.executable, str(ROOT / "AClad/engine/clad_engine.py"),
                                      str(input_path), str(output_path)], capture_output=True,
                                     text=True, timeout=30)
            self.assertEqual(process.returncode, 0, process.stderr)
            result = json.loads(output_path.read_text(encoding="utf-8"))
        owners = {entry.get("outer_id") for entry in result["per_zone"]}
        self.assertEqual(owners, {"TRIANGLE"}, result["per_zone"])
        self.assert_coverage(result, Polygon(ORACLE_CASES["triangle"]))


class PolygonClipOracleReview(unittest.TestCase):
    def assert_intersection(self, rings, outer, offset=(0, 0)):
        from polygon_clip import intersect
        subject = Polygon(rings[0], rings[1:])
        target = Polygon(outer)
        self.assertTrue(subject.is_valid, "Invalid test subject")
        self.assertTrue(target.is_valid, "Invalid test target")
        expected = subject.intersection(target)

        def shifted(ring):
            return [[x + offset[0], y + offset[1]] for x, y in ring]

        components = intersect([shifted(ring) for ring in rings], shifted(outer))
        polys = []
        for component in components:
            local_rings = [[(x - offset[0], y - offset[1]) for x, y in ring]
                           for ring in component]
            actual = Polygon(local_rings[0], local_rings[1:])
            self.assertTrue(actual.is_valid, f"Invalid clipped component: {component}")
            self.assertGreater(actual.area, 0)
            polys.append(actual)
        actual = unary_union(polys)
        # The clipper itself retains 1e-7 mm precision; large drawing origins
        # add at most sub-micrometre floating-point error to these fixtures.
        tolerance = max(0.0001, (expected.length + actual.length) * 0.000001)
        self.assertLessEqual(expected.symmetric_difference(actual).area, tolerance)
        self.assertLessEqual(sum(p.area for p in polys) - actual.area, tolerance)

    def test_deterministic_200_intersections_against_shapely(self):
        rng = random.Random(30092026)
        shapes = list(ORACLE_CASES.values())
        for index in range(200):
            outer = copy.deepcopy(shapes[index % len(shapes)])
            x = rng.randrange(-600, 5101, 100)
            y = rng.randrange(-600, 3601, 100)
            w = rng.randrange(200, 3601, 100)
            h = rng.randrange(200, 3001, 100)
            rings = [[[x, y], [x + w, y], [x + w, y + h], [x, y + h]]]
            if index % 3 == 0:
                rings.append([[x + w / 4, y + h / 4], [x + 3 * w / 4, y + h / 4],
                              [x + 3 * w / 4, y + 3 * h / 4], [x + w / 4, y + 3 * h / 4]])
            if index % 2:
                outer.reverse()
            if index % 4:
                rings[0].reverse()
            if len(rings) > 1 and index % 5:
                rings[1].reverse()
            offset = (4_000_000_000, 4_000_000_000) if index % 7 == 0 else (0, 0)
            with self.subTest(case=index, shifted=bool(offset[0])):
                self.assert_intersection(rings, outer, offset)

    def test_collinear_boundaries_and_point_contacts(self):
        square = [[0, 0], [1200, 0], [1200, 1200], [0, 1200]]
        hole = [[300, 300], [900, 300], [900, 900], [300, 900]]
        fixtures = [
            ("identical", [square], square),
            ("edge_contact", [square], [[1200, 0], [2400, 0], [2400, 1200], [1200, 1200]]),
            ("point_contact", [square], [[1200, 1200], [2400, 1200], [2400, 2400], [1200, 2400]]),
            ("collinear_overlap", [square], [[600, 0], [1800, 0], [1800, 600], [600, 600]]),
            ("hole_cut_open", [square, hole], [[0, 0], [600, 0], [600, 600],
                                                [1200, 600], [1200, 1200], [0, 1200]]),
            ("hole_edge_contact", [square, hole], hole),
            ("valley_vertex_contact", [[[0, 600], [2400, 600], [2400, 2400], [0, 2400]]],
             ORACLE_CASES["disconnected_cell"]),
        ]
        for name, rings, outer in fixtures:
            for reverse in (False, True):
                with self.subTest(shape=name, reverse=reverse):
                    self.assert_intersection([r[::-1] if reverse else r for r in rings],
                                             outer[::-1] if reverse else outer)


def render():
    from io import BytesIO
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.patches import Polygon as Patch
    req = request(holes=[OPENING])
    result = tile_pattern(req)
    hole = Polygon(OPENING)
    bad = [p for p in pieces(result) if geom(p).intersection(hole).area > .01]
    covered = sum(geom(p).intersection(hole).area for p in pieces(result))
    failed = covered > .01
    diagnostic_color = "#ef4444" if failed else "#22c55e"
    diagnostic_edge = "#991b1b" if failed else "#166534"
    fig, (ax, zoom) = plt.subplots(1, 2, figsize=(12.6, 5.8),
                                 gridspec_kw={"width_ratios": [1.5, 1]})
    fig.patch.set_facecolor("#f8fafc")
    for a in (ax, zoom):
        a.set_facecolor("#f8fafc")
        a.add_patch(Patch(GABLE, closed=True, facecolor="#e2e8f0", edgecolor="#0f172a", lw=2))
        for p in pieces(result):
            a.add_patch(Patch(list(geom(p).exterior.coords), closed=True,
                             facecolor="#cbd5e1", edgecolor="white", linewidth=1))
        for p in bad:
            a.add_patch(Patch(list(geom(p).exterior.coords), closed=True,
                             facecolor="#fb923c", edgecolor="#9a3412", alpha=.9, lw=1.5))
        a.add_patch(Patch(OPENING, closed=True, facecolor=diagnostic_color, edgecolor=diagnostic_edge,
                         linewidth=2, hatch="////", alpha=.9))
        a.set_aspect("equal")
        a.set_xlabel("X, мм")
        a.set_ylabel("Y, мм")
        a.spines[["top", "right"]].set_visible(False)
    ax.set_title("Синтетический фасад: 6 × 4,5 м", fontsize=13)
    ax.set_xlim(-150, 6200); ax.set_ylim(-150, 4650)
    zoom.set_title("Проём исчезает после обрезки по скату" if failed else
                   "Проём сохранён после обрезки по скату", fontsize=12)
    zoom.set_xlim(4700, 6150); zoom.set_ylim(2350, 3650)
    zoom.annotate("Окно 300 × 300 мм\n" + ("перекрыто: %.0f мм²" % covered if failed else
                  "свободно от облицовки"), (5150, 2750), (4870, 3470),
                  fontsize=11, arrowprops={"arrowstyle": "->", "color": diagnostic_edge},
                  color=diagnostic_edge)
    fig.suptitle("ATTILE: внутреннее кольцо проёма " + ("теряется у ската" if failed else
                  "сохраняется у ската"), fontsize=16,
                 fontweight="bold", x=.03, ha="left")
    fig.text(.035, .035, "Плитка 1200 × 1200 мм, руст 10 мм, режим «фигурный кусок». "
             "Перекрытие проёма: %.6f м².\n" % (covered / 1e6) +
             "Источник: независимый прогон текущего tile_pattern; "
             "это геометрия движка, а не снимок AutoCAD.", fontsize=10, color="#334155")
    fig.subplots_adjust(left=.07, right=.98, top=.86, bottom=.19, wspace=.25)
    target = (ROOT / "docs" / "fixes_3009" / "gable_opening_fixed.png" if "--render-fixed" in sys.argv
              else ROOT / "docs" / "review_3009" / "gable_opening.png")
    target.parent.mkdir(parents=True, exist_ok=True)
    buffer = BytesIO()
    fig.savefig(buffer, format="png", dpi=170)
    target.write_bytes(buffer.getvalue())
    print(target)


if __name__ == "__main__":
    if "--render" in sys.argv or "--render-fixed" in sys.argv:
        render()
    else:
        unittest.main(verbosity=2)
