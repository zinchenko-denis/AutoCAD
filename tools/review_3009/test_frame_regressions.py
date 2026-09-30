"""Independent AFrame regressions, review 30.09.2026.

Run from repo root: python3 tools/review_3009/test_frame_regressions.py
These assertions intentionally fail on audited HEAD e2a4e4d: they describe
required behavior, not an assertion that the current defect is acceptable.
The C# readback case mirrors only the explicit rectangle-aspect-ratio gate;
it does not pretend to run AutoCAD or validate dynamic-block extents.
"""
import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AFrame" / "engine"))
import frame_calc as fc
import frame_engine as fe
import frame_plan as fp


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def clamp_set(result):
    return {(c["x"], c["y"], c["kind"], c.get("orient", ""))
            for c in result["clamps"]}


def empty_zone_rows():
    a = {"zone_id": "A", "zone": {"outer": {"pts": rect(0, 0, 1800, 1000)}},
         "joints_x": [300, 900, 1500], "rows_y": []}
    b = {"zone_id": "B", "zone": {"outer": {"pts": rect(2500, 0, 4300, 2000)}},
         "joints_x": [2800, 3400, 4000], "rows_y": [600, 1200, 1800]}
    request = {"op": "frame", "system": "Standart", "zones": [a, b],
               "joints_x": [300, 900, 1500, 2800, 3400, 4000],
               "rows_y": [600, 1200, 1800]}
    together = fe.run(copy.deepcopy(request))
    alone = fe.run(dict(copy.deepcopy(request), zones=[copy.deepcopy(a)], rows_y=[]))
    actual_a = {"clamps": [c for c in together["clamps"] if c["zone"] == "A"],
                "rails": [r for r in together["rails"] if r["zone"] == "A"]}
    return request, together, alone, actual_a


def long_floor():
    request = {"system": "Межэтажная", "sub_type": "interfloor",
               "contours": [{"outer": rect(0, 0, 5000, 11000)}],
               "joints_x": [100 + i * 600 for i in range(9)],
               "floors_y": [0, 3000, 6000, 10000],
               "calc": {"terrain": "B", "height": 15, "q_clad": 8,
                        "offset": 150, "na_max": 3000, "wind_region": "II"}}
    result = fp.frame_plan(copy.deepcopy(request))
    rep = result["calc_report"]
    # Recheck the exact profile that the calculation itself approved, so this
    # failure is independent of any separate mapping of NSP-1/NSP-2 to sections.
    xs = sorted(b["x"] for b in result["brackets"] if b["y"] == 6000)
    emitted_steps = {"row": [], "corner": []}
    for xa, xb in zip(xs, xs[1:]):
        mid = (xa + xb) / 2
        emitted_steps["corner" if mid < 1500 or mid > 3500 else "row"].append(xb - xa)
    actual_checks = {}
    for zone in ("row", "corner"):
        inp = dict(fp.load_system("Межэтажная")["calc"])
        inp.update(request["calc"])
        inp.update(rep["inputs"])
        inp.update(profile=rep["profile"][zone], rail_len=4000)
        step = max(emitted_steps[zone])
        actual_checks[zone] = fc.calc_chain(inp, step, zone)
    return request, result, actual_checks


def native_short_rails():
    request = {"system": "Вектор-1",
               "contours": [{"outer": rect(0, 0, 1800, 3150)}],
               "joints_x": [300, 900, 1500],
               "rows_y": [600, 1200, 1800, 2400, 3020],
               "calc": {"terrain": "B", "height": 30, "q_clad": 25,
                        "offset": 230, "na_max": 3000, "wind_region": "II"}}
    full = fp.frame_plan(copy.deepcopy(request))
    width = full["system_used"]["rail_width"]
    # Conditional rails are emitted by FrameCommand as rectangles of railW.
    # RailGeom accepts a rectangle/block only when height >= 3*max(width,1).
    fixed = [{"x": r["x"], "y0": r["y0"], "y1": r["y1"]}
             for r in full["rails"] if r["len"] >= 3 * max(width, 1)]
    again = fp.frame_plan(dict(copy.deepcopy(request), parts="clamps", rails_fixed=fixed))
    return request, full, fixed, again


def vertical_only_clamps():
    request = {"op": "frame", "system": "Standart", "sub_type": "vertical",
               "contours": [
                   {"id": "O", "pts": [[0, 0], [5800, 0], [5800, 8800],
                                         [4300, 8800], [4300, 3200], [0, 3200]]},
                   {"id": "H0", "pts": rect(3800, 0, 5600, 1200)},
                   {"id": "H1", "pts": rect(4110, 1570, 4910, 3120)},
                   {"id": "H2", "pts": rect(140, 2490, 1540, 3140)},
                   {"id": "H3", "pts": rect(5080, 1680, 5780, 3630)}],
               "joints_x": [421.881, 1029.881, 1637.881, 2245.881, 2853.881,
                            3461.881, 4069.881, 4677.881, 5285.881],
               "rows_y": [1208, 2416, 3624, 4832, 6040, 7248, 8456]}
    full = fe.run(copy.deepcopy(request))
    fixed = [{"x": r["x"], "y0": r["y0"], "y1": r["y1"]} for r in full["rails"]]
    again = fe.run(dict(copy.deepcopy(request), parts="clamps", rails_fixed=fixed))
    return request, full, again


def unsupported_gable_shina():
    request = {"op": "frame", "system": "Standart", "sub_type": "vertical",
               "contours": [{"id": "gable", "pts": [[0, 0], [4800, 0], [4800, 3000],
                                                      [2400, 6000], [0, 3000]]}],
               "cladding": "clinker", "tile_step_x": 600,
               "tile_whip": 2500, "rows_y": [5900]}
    return request, fe.run(copy.deepcopy(request))


class FrameContractRegressions(unittest.TestCase):
    def test_empty_local_rows_do_not_inherit_other_zone_rows(self):
        _, _, alone, actual_a = empty_zone_rows()
        self.assertEqual(clamp_set(alone), clamp_set(actual_a),
                         "Selecting a neighboring zone must not change A's clamps")

    def test_calculation_covers_longest_given_floor_span(self):
        _, result, recheck = long_floor()
        self.assertTrue(result["ok"])
        self.assertTrue(all(r["passed"] for r in recheck.values()),
                        "Engine approved 3000 mm, but emitted a 4000 mm floor span: " +
                        json.dumps(recheck, ensure_ascii=False))

    def test_generated_short_rails_survive_native_clamp_readback(self):
        _, full, fixed, again = native_short_rails()
        self.assertEqual(clamp_set(full), clamp_set(again),
                         "Native RailGeom kept %d of %d generated rails" %
                         (len(fixed), len(full["rails"])))

    def test_vertical_only_clamps_repeats_full_layout(self):
        _, full, again = vertical_only_clamps()
        self.assertEqual(clamp_set(full), clamp_set(again),
                         "The known mismatch is also present in vertical systems")

    def test_shina_has_at_least_one_vertical_support(self):
        _, result = unsupported_gable_shina()
        unsupported = [h for h in result["hrails"] if h["kind"].startswith("шина")
                       and not any(r["y0"] - 20 <= h["y"] <= r["y1"] + 20
                                   and h["x0"] <= r["x"] <= h["x1"]
                                   for r in result["rails"])]
        self.assertFalse(unsupported,
                         "A warning/INFO does not supply a physical connection: " +
                         json.dumps(unsupported, ensure_ascii=False))


if __name__ == "__main__":
    unittest.main(verbosity=2)
