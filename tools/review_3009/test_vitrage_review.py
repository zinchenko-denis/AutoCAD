"""Read-only ABlockGen regression probes, audit 30 September 2026.
Tests exercise the actual engine, expecting safe, invariant plans.
"""
import copy
import sys
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "ABlockGen/engine"))
from vitrage_plan import build_plan


def request():
    return {"op": "plan", "opening": {"x0": 0, "y0": 0, "x1": 3000, "y1": 3000},
        "grid": {"n_cols": 2, "rail_y": [1500]},
        "blocks": {"stand": {"name": "S", "body_w": 50},
                   "rigel": {"name": "R", "body_w": 50}, "fill": {"name": "F", "fold": 15}}}


class VitrageReview(unittest.TestCase):
    def test_equal_clear_sizes_have_equal_glass_size_and_mark(self):
        # Minimal recorded case from existing synth seed 2309, case 928.
        req = request()
        req["opening"] = {"x0": -1041.7442939452735, "y0": -1349.5663437473095,
                          "x1": 5783.255706054726, "y1": 3425.4336562526905}
        req["grid"]["rail_y"] = [400, 3300, 3660]
        req["blocks"]["rigel"]["body_w"] = 45.6
        fills = [i for i in build_plan(req)["inserts"] if i["kind"] == "fill"]
        a, b = fills[:2]
        self.assertEqual(a["dyn"], b["dyn"])
        print("equal dynamic size / differing glass:", a["dyn"], a["attrs"], b["attrs"])
        self.assertEqual(a["attrs"], b["attrs"])

    def test_equal_rigel_lengths_have_one_mark(self):
        req = request()
        req["opening"] = {"x0": -830.9211232809985, "y0": -835.0131896329881,
                          "x1": 7164.0788767190015, "y1": 4779.986810367012}
        req["grid"] = {"n_cols": 4, "rail_y": [2410]}
        req["blocks"]["stand"]["body_w"] = 54.4
        rigels = [i for i in build_plan(req)["inserts"] if i["kind"] == "rigel"]
        self.assertEqual(len({i["dyn"]["Длина"] for i in rigels}), 1)
        print("equal rigels:", [i["attrs"] for i in rigels])
        self.assertEqual(len({i["attrs"]["ИМЯ"] for i in rigels}), 1)

    def test_negative_body_is_rejected(self):
        req = request()
        req["blocks"]["stand"]["body_w"] = -50
        try:
            plan = build_plan(req)
        except ValueError:
            return
        print("negative body response:", plan)
        self.assertFalse(plan.get("ok"), "Reject invalid dimensions by exception or ok=false")

    def test_negative_thermal_gap_is_rejected(self):
        req = request()
        req["grid"].update(tiers=[1500, 1510], tier_gap=-10)
        try:
            plan = build_plan(req)
        except ValueError:
            return
        print("negative gap response:", plan)
        self.assertFalse(plan.get("ok"), "Reject invalid gap by exception or ok=false")

    def test_duplicate_rail_levels_do_not_duplicate_rigels(self):
        req = request()
        req["grid"]["rail_y"] = [1500, 1500]
        plan = build_plan(req)
        rigels = [i for i in plan["inserts"] if i["kind"] == "rigel"]
        print("duplicate rail input: rigels:", len(rigels), "notes:", plan["notes"])
        self.assertEqual(len(rigels), 2, rigels)

    def test_positive_input_is_accepted(self):
        self.assertTrue(build_plan(request())["ok"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
