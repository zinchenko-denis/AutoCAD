#!/usr/bin/env python3
"""Independent AClad review regressions, 30.09.2026.

These assertions describe required geometry/safety invariants. They intentionally
fail on reviewed HEAD e2a4e4d; no production code is modified. All input geometry
is synthetic. Run from repository root:
  python3 tools/review_3009/test_clad_frontons.py
  python3 tools/review_3009/test_clad_frontons.py --render
"""
import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AClad" / "engine"))
from shapely.geometry import Polygon, box
from shapely.ops import unary_union
import clad_engine
from cladding_plan import cladding_plan
from tile_pattern import tile_pattern

GABLE = [[0, 0], [6000, 0], [6000, 3000], [3000, 4500], [0, 3000]]
OPENING = [[5000, 2600], [5300, 2600], [5300, 2900], [5000, 2900]]
DOUBLE_GABLE = [[0, 0], [6000, 0], [6000, 3000], [4500, 4500],
                [3000, 3000], [1500, 4500], [0, 3000]]


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
        a.add_patch(Patch(OPENING, closed=True, facecolor="#ef4444", edgecolor="#991b1b",
                         linewidth=2, hatch="////", alpha=.9))
        a.set_aspect("equal")
        a.set_xlabel("X, мм")
        a.set_ylabel("Y, мм")
        a.spines[["top", "right"]].set_visible(False)
    ax.set_title("Синтетический фасад: 6 × 4,5 м", fontsize=13)
    ax.set_xlim(-150, 6200); ax.set_ylim(-150, 4650)
    zoom.set_title("Проём исчезает после обрезки по скату", fontsize=12)
    zoom.set_xlim(4700, 6150); zoom.set_ylim(2350, 3650)
    zoom.annotate("Окно 300 × 300 мм\nперекрыто целиком", (5150, 2750), (4870, 3470),
                  fontsize=11, arrowprops={"arrowstyle": "->", "color": "#7f1d1d"},
                  color="#7f1d1d")
    fig.suptitle("ATTILE: внутреннее кольцо проёма теряется у ската", fontsize=16,
                 fontweight="bold", x=.03, ha="left")
    fig.text(.035, .035, "Плитка 1200 × 1200 мм, руст 10 мм, режим «фигурный кусок». "
             "Красная штриховка: ошибочное перекрытие 0,09 м².\n"
             "Источник: независимый прогон tile_pattern на HEAD e2a4e4d; "
             "это геометрия движка, а не снимок AutoCAD.", fontsize=10, color="#334155")
    fig.subplots_adjust(left=.07, right=.98, top=.86, bottom=.19, wspace=.25)
    target = ROOT / "docs" / "review_3009" / "gable_opening.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    buffer = BytesIO()
    fig.savefig(buffer, format="png", dpi=170)
    target.write_bytes(buffer.getvalue())
    print(target)


if __name__ == "__main__":
    if "--render" in sys.argv:
        render()
    else:
        unittest.main(verbosity=2)
