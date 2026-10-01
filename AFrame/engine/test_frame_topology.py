# -*- coding: utf-8 -*-
"""Applicability regressions against actual engine output and piece inventories.

The short-wall and interfloor fixtures returned positive layout_verification
on ad2a0a2 despite absent/insufficient supports or unconfirmed beam models.
No independently invented section properties or load formulas are used here.
"""
import json
from pathlib import Path
import tempfile
import unittest

import frame_engine as fe
import frame_plan as fp
import frame_topology as ft


def request(sub="vertical", height=3000, width=1200):
    return dict(op="frame", system={"vertical": "Вектор-1", "ortho": "Ортогональная",
                                    "interfloor": "Межэтажная"}[sub],
        sub_type=sub, parts="frame", cladding="porcelain", corners_x=[],
        contours=[dict(id="A", pts=[[0, 0], [width, 0], [width, height], [0, height]])],
        joints_x=[300, 900],
        calc=dict(wind_region="II", terrain="B", height=6,
                  q_clad=25, offset=170, na_max=3000, auto_profile=False))


class TestFrameTopology(unittest.TestCase):
    def refused(self, result, code, reason=None):
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["error_code"], code)
        for key in ("rails", "hrails", "brackets", "clamps", "fittings"):
            self.assertFalse(result.get(key), key)
        self.assertEqual(result["static_model"]["status"], "not_verified")
        self.assertNotIn("calc_report", result)
        if reason:
            self.assertIn(reason, {r["reason"] for r in result["static_model"]["geometric_screening"]["reasons"]})

    def test_before_repro_zero_and_one_support_are_refused(self):
        for sub, height, count in (("vertical", 140, 1), ("vertical", 600, 1),
                                   ("ortho", 140, 0), ("ortho", 600, 1)):
            with self.subTest(sub=sub, height=height):
                result = fe.op_frame(request(sub, height))
                self.refused(result, "E_CALC_TOPOLOGY_UNSUPPORTED", "insufficient_supports")
                self.assertTrue(result["static_model"]["members"])
                self.assertEqual({m["support_count"] for m in result["static_model"]["members"]}, {count})

    def test_single_span_is_calculated_as_one_with_actual_free_ends(self):
        for sub in ("vertical", "ortho"):
            result = fe.op_frame(request(sub, 1000))
            self.assertTrue(result["ok"], result.get("error"))
            model = result["calc_report"]["static_model"]
            self.assertEqual({m["span_count"] for m in model["members"]}, {1})
            for case in model["member_calculation"]["cases"]:
                self.assertEqual(case["chain"]["member_coefficients"]["span_count"], 1)
                self.assertEqual(case["bottom_free"], 300)
                self.assertEqual(case["top_free"], 300 if sub == "vertical" else 100)

    def test_real_two_spans_use_the_two_span_row(self):
        for sub in ("vertical", "ortho"):
            result = fe.op_frame(request(sub, 1800))
            self.assertTrue(result["ok"], result.get("error"))
            model = result["calc_report"]["static_model"]
            for member in model["members"]:
                self.assertEqual(member["actual_span_class"], "2")
                self.assertEqual(member["coefficient_span_class"], "2")
            for case in model["member_calculation"]["cases"]:
                self.assertGreaterEqual(case["chain"]["member_coefficients"]["k_reaction"], 1.25)

    def test_regular_positive_controls_do_not_certify_static_model(self):
        for sub in ("vertical", "ortho"):
            result = fe.op_frame(request(sub, 3000))
            self.assertTrue(result["ok"], result.get("error"))
            model = result["calc_report"]["static_model"]
            self.assertEqual(model["status"], "not_verified")
            self.assertEqual(model["geometric_screening"], {"status": "passed", "reasons": []})
            self.assertEqual(model["fixed_sliding"], "not_modeled")
            self.assertEqual(model["splice_continuity"], "not_modeled")
            self.assertEqual(model["cantilevers"], "uniform_load_screened")
            self.assertTrue(any("Статическая модель не подтверждена" in note for note in result["notes"]))

    def test_interfloor_two_span_counterexample_refused(self):
        req = request("interfloor", 6000, 5400)
        req.update(system={"name": "Межэтажная", "rail_std": 6000},
                   joints_x=list(range(0, 5401, 450)), floors_y=[0, 3000, 6000],
                   corners_x=[0, 5400])
        req["calc"].update(height=30, offset=230)
        result = fe.op_frame(req)
        self.refused(result, "E_CALC_MODEL_UNCONFIRMED", "interfloor_model_unconfirmed")
        self.assertEqual({m["span_count"] for m in result["static_model"]["members"]}, {2})
        self.assertTrue(all(m["support_y"] == [0.0, 3000.0, 6000.0]
                            for m in result["static_model"]["members"]))

    def test_interfloor_count_is_not_an_authorization_threshold(self):
        for floors in ([0, 1500, 3000], [0, 750, 1500, 2250, 3000]):
            req = request("interfloor", 3000)
            req["floors_y"] = floors
            result = fe.op_frame(req)
            self.refused(result, "E_CALC_MODEL_UNCONFIRMED", "interfloor_model_unconfirmed")

    def test_existing_interfloor_free_end_error_retains_precedence(self):
        req = request("interfloor", 3000)
        req["floors_y"] = [600, 2400]
        result = fe.op_frame(req)
        self.refused(result, "E_CALC_TOPOLOGY_UNSUPPORTED")
        self.assertTrue(any(item["reason"] == "unsupported_free_end" for item in result["unsupported"]))

    def test_real_separate_pieces_remain_separate_even_when_gap_zero(self):
        for gap in (0, 10):
            req = request("interfloor", 6000)
            req.update(system={"name": "Межэтажная", "rail_gap": gap}, floors_y=[0, 3000, 6000])
            result = fe.op_frame(req)
            self.refused(result, "E_CALC_MODEL_UNCONFIRMED")
            members = result["static_model"]["members"]
            same_axis = sorted((m for m in members if m["geometry"]["x"] == members[0]["geometry"]["x"]),
                               key=lambda m: m["geometry"]["y0"])
            self.assertEqual(len(same_axis), 2)
            self.assertEqual([m["span_count"] for m in same_axis], [1, 1])
            self.assertEqual(same_axis[1]["geometry"]["y0"] - same_axis[0]["geometry"]["y1"], gap)
            self.assertFalse(result["static_model"]["pieces_merged"])

    def test_manual_geometry_and_clamps_only_do_not_acquire_calculation_refusal(self):
        for sub in ("vertical", "ortho", "interfloor"):
            req = request(sub, 6000)
            req.pop("calc")
            req["floors_y"] = [0, 3000, 6000]
            result = fe.op_frame(req)
            self.assertTrue(result["ok"], result.get("error"))
            self.assertNotIn("calc_report", result)
            req.update(parts="clamps", rails_fixed=result["rails"], rows_y=[600, 1200, 1800])
            repeated = fe.op_frame(req)
            self.assertTrue(repeated["ok"], repeated.get("error"))
            self.assertNotIn("calc_report", repeated)

    def test_true_intersections_not_nominal_rows_or_nearby_brackets(self):
        rail = dict(x=100, y0=0, y1=3000, kind="ШП-60-20")
        horizontal = [dict(y=y, x0=0, x1=200, kind="ГП-40-40") for y in (300, 900, 1500, 2100, 2700)]
        horizontal += [dict(y=600, x0=201, x1=400, kind="ГП-40-40"),
                       dict(y=1200, x0=0, x1=200, kind="шина рядовая")]
        model = ft.screen_layout("ortho", [rail], horizontal, [], dict(rail_len=3000, v_step=600))
        self.assertEqual(model["members"][0]["support_y"], [300, 900, 1500, 2100, 2700])
        self.assertEqual(model["geometric_screening"]["status"], "passed")
        brackets = [dict(x=100, y=y, kind="рядовой") for y in (300, 1100, 1900, 2700)]
        brackets += [dict(x=101, y=700, kind="несущий"), brackets[0]]
        vertical = ft.screen_layout("vertical", [rail], [], brackets, dict(rail_len=3000))
        self.assertEqual(vertical["members"][0]["support_y"], [300, 1100, 1900, 2700])
        self.assertEqual(vertical["members"][0]["support_count"], 4)
        self.assertEqual(vertical["fixed_sliding"], "not_modeled")

    def test_ortho_long_interval_cannot_hide_behind_multi_class(self):
        rail = dict(x=100, y0=0, y1=3600, kind="ШП-60-20")
        horizontal = [dict(y=y, x0=0, x1=200, kind="ГП-40-40") for y in (300, 900, 2100, 2700, 3300)]
        from test_frame_beam import INPUTS
        model = ft.screen_layout("ortho", [rail], horizontal, [],
            dict(INPUTS, scheme="ortho", v_step=600), bracket_steps={"main": 500, "corner": 500})
        case = model["member_calculation"]["cases"][0]
        self.assertEqual(case["response"]["span"], 1200)
        self.assertEqual(case["intervals"], [600, 1200, 600, 600])
        self.assertGreater(case["chain"]["member_coefficients"]["c_f_local"], 0)
        # Geometry can no longer be approved by silently checking only 600 mm.
        self.assertEqual(model["members"][0]["coefficient_span"], 1200)

    def test_empty_geometry_is_not_a_vacuous_pass(self):
        model = ft.screen_layout("vertical", [], [], [], dict(rail_len=3000))
        self.assertEqual(model["geometric_screening"],
                         dict(status="refused", reasons=[dict(reason="empty_geometry")]))
        self.assertEqual(ft.refusal(model)["error_code"], "E_CALC_TOPOLOGY_UNSUPPORTED")
        direct = fp.frame_plan(dict(system="Вектор-1", contours=[], joints_x=[300, 900],
                                    corners_x=[], calc=request()["calc"]))
        self.refused(direct, "E_CALC_TOPOLOGY_UNSUPPORTED", "empty_geometry")

    def test_later_failed_zone_discards_all_previous_drawable_output(self):
        req = request("vertical", 3000)
        req["contours"] += [dict(id="short", pts=[[2000, 0], [3200, 0], [3200, 140], [2000, 140]],
                                 joints_x=[2300, 2900])]
        result = fe.op_frame(req)
        self.refused(result, "E_CALC_TOPOLOGY_UNSUPPORTED", "insufficient_supports")
        self.assertEqual(result["failed_zone"], "контур short")
        self.assertEqual({m["geometry"]["y1"] for m in result["static_model"]["members"]}, {140.0})

    def test_cli_serializes_refusal_diagnostics_without_partial_geometry(self):
        with tempfile.TemporaryDirectory() as directory:
            src, dst = Path(directory) / "input.json", Path(directory) / "output.json"
            src.write_text(json.dumps(request("ortho", 140), ensure_ascii=False), encoding="utf-8")
            fe.main(["frame_engine", str(src), str(dst)])
            result = json.loads(dst.read_text(encoding="utf-8"))
            self.refused(result, "E_CALC_TOPOLOGY_UNSUPPORTED", "insufficient_supports")
            self.assertEqual(result["static_model"]["members"][0]["support_count"], 0)


if __name__ == "__main__":
    unittest.main()
