"""Independent material/fastening regression cases, originally red on e2a4e4d.

These compare program behavior with supplied project sources, not an independent
normative certification. Source METHOD_CALC section 1 and original type-4 AKP
calculation specify gamma_clad=1.2 for AKP and 1.1 for porcelain/clinker;
the source ATR Vector-5 page 135 describes AKP fasteners, not porcelain clamps.
"""
import copy
import pathlib
import sys
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AFrame" / "engine"))
from frame_plan import frame_plan
import frame_calc


def request(material, sub="vertical", **loads):
    calc = dict(wind_region="II", terrain="B", height=30, q_clad=25,
                offset=230, na_max=3000)
    calc.update(loads)
    return dict(system="Межэтажная" if sub == "interfloor" else "Вектор-1",
                sub_type=sub, cladding=material, parts="frame",
                contours=[dict(outer=[[0, 0], [6000, 0], [6000, 6000], [0, 6000]])],
                joints_x=list(range(0, 6001, 600)), rows_y=list(range(600, 6000, 600)),
                floors_y=[0, 3000, 6000], tile_step_x=600, calc=calc)


class MaterialRules(unittest.TestCase):
    def compare_source_gamma(self, req, gamma):
        # The repaired planner may correctly refuse these historical examples
        # after constraining the actual NSP-1 section. Still verify the material
        # factor reaching the real calculator, not merely equal refusals.
        observed = []
        original_report = frame_calc.report
        def checked_report(inp, *args, **kwargs):
            observed.append(inp["gamma_clad"])
            return original_report(inp, *args, **kwargs)
        with patch.object(frame_calc, "report", checked_report):
            actual = frame_plan(req)
        reference_req = copy.deepcopy(req)
        reference_req["calc"]["gamma_clad"] = gamma
        reference = frame_plan(reference_req)
        self.assertTrue(observed, "Request must reach the actual calculator")
        self.assertTrue(all(value == gamma for value in observed), observed)
        self.assertEqual(actual["ok"], reference["ok"])
        if actual["ok"]:
            self.assertEqual(actual["calc_report"]["steps"], reference["calc_report"]["steps"],
                             "Material selection must retain the load factor from the source method")
        else:
            self.assertEqual(actual["error"], reference["error"])
            self.assertFalse(actual.get("rails"), "Refused calculation must not issue a frame")

    def test_porcelain_interfloor_keeps_porcelain_load_factor(self):
        self.compare_source_gamma(request("porcelain", "interfloor", na_max=2820), 1.1)

    def test_clinker_interfloor_keeps_clinker_load_factor(self):
        self.compare_source_gamma(request("clinker", "interfloor", q_clad=45, na_max=3500), 1.1)

    def test_composite_interfloor_uses_akp_load_factor(self):
        self.compare_source_gamma(request("composite", "interfloor", q_clad=7.2,
                                          height=28, terrain="A", offset=400, na_max=3040), 1.2)

    def test_composite_all_must_not_produce_porcelain_clamps(self):
        req = request("composite")
        req["parts"] = "all"
        actual = frame_plan(req)
        porcelain_req = request("porcelain")
        porcelain_req["parts"] = "all"
        porcelain = frame_plan(porcelain_req)
        self.assertTrue(not actual["ok"] or not actual.get("clamps")
                        or actual["clamps"] != porcelain["clamps"],
                        "AKP must not silently reuse the identical porcelain fastening output; "
                        "a future material-specific collection is allowed by this probe")


if __name__ == "__main__":
    unittest.main(verbosity=2)
