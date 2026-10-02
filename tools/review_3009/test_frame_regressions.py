"""Independent AFrame regressions, review 30.09.2026.

Run from repo root: python3 tools/review_3009/test_frame_regressions.py
These requirements failed on audited HEAD e2a4e4d. Local member defects must
retain drawable geometry with addressed issues and no false calculation pass;
unsupported global schemes must still refuse without emitted elements.
The native metadata reader is separately exercised by
tools/fixes_3009/test_frame_rail_metadata.py using the actual C# methods.
"""
import copy
import json
import sys
import unittest
from unittest.mock import patch
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
    with patch.object(fc, "report", wraps=fc.report) as observed:
        result = fp.frame_plan(copy.deepcopy(request))
    if not result["ok"]:
        return request, result, {"resolved_input": observed.call_args.args[0]}
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
    # Stock cutting must redistribute the former 150 mm tail before metadata
    # readback; no single-support physical piece may reach the drawing.
    manual = copy.deepcopy(request)
    manual.pop("calc")
    full = fp.frame_plan(manual)
    # ATFRAME_RAIL metadata supplies direction and role without aspect filtering.
    fixed = [{k: r[k] for k in ("x", "y0", "y1", "clamp_role")} for r in full["rails"]]
    again = fp.frame_plan(dict(manual, parts="clamps", rails_fixed=fixed))
    return request, full, fixed, again


def vertical_only_clamps():
    request = {"op": "frame", "system": "Standart", "sub_type": "vertical",
               "contours": [
                   {"id": "O", "pts": [[0, 0], [5800, 0], [5800, 8800],
                                         [4300, 8800], [4300, 3900], [0, 3900]]},
                   {"id": "H0", "pts": rect(3800, 0, 5600, 1200)},
                   {"id": "H1", "pts": rect(4110, 1570, 4910, 3120)},
                   {"id": "H2", "pts": rect(140, 2490, 1540, 3140)},
                   {"id": "H3", "pts": rect(5080, 1680, 5780, 3630)}],
               "joints_x": [421.881, 1029.881, 1637.881, 2245.881, 2853.881,
                            3461.881, 4069.881, 4677.881, 5285.881],
               "rows_y": [1208, 2416, 3624, 4832, 6040, 7248, 8456]}
    # Raise the ledge above the windows for a supported clamp-readback
    # positive. The original 3200 mm ledge produced 10/30 mm one-support
    # fragments, now covered explicitly by the local-warning test below.
    # The separate 320–510 mm piers use explicit 100 mm test offsets.
    request["system"] = {"name": "Standart", "bracket_start_offset": 100}
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
        if not result["ok"]:
            self.assertEqual(result.get("error_code"), "E_CALC_NOT_PASSED")
            self.assertEqual(recheck["resolved_input"]["rail_len"], 4000)
            self.assertFalse(any(result.get(k) for k in ("rails", "hrails", "brackets", "clamps")))
            return
        self.assertTrue(result["ok"])
        self.assertTrue(all(r["passed"] for r in recheck.values()),
                        "Engine approved 3000 mm, but emitted a 4000 mm floor span: " +
                        json.dumps(recheck, ensure_ascii=False))

    def test_generated_short_rails_survive_native_clamp_readback(self):
        request, full, fixed, again = native_short_rails()
        self.assertTrue(full["ok"] and again["ok"])
        self.assertTrue(fixed)
        self.assertEqual(clamp_set(full), clamp_set(again),
                         "Native RailGeom kept %d of %d generated rails" %
                         (len(fixed), len(full["rails"])))
        calculated = fp.frame_plan(request)
        self.assertTrue(calculated["ok"], calculated.get("error"))
        model = calculated["calc_report"]["static_model"]
        self.assertTrue(all(m["support_count"] >= 2 for m in model["members"]))
        self.assertTrue(all(c["chain"]["passed"] for c in model["member_calculation"]["cases"]))
        self.assertEqual(model["status"], "not_verified")

    def test_vertical_only_clamps_repeats_full_layout(self):
        _, full, again = vertical_only_clamps()
        self.assertEqual(clamp_set(full), clamp_set(again),
                         "The known mismatch is also present in vertical systems")

    def test_original_clamp_fixture_builds_with_addressed_short_piece_warnings(self):
        request, _, _ = vertical_only_clamps()
        request["contours"][0]["pts"][4][1] = 3200
        request["contours"][0]["pts"][5][1] = 3200
        result = fe.run(request)
        self.assertTrue(result["ok"], result.get("error"))
        self.assertEqual(result["calculation_status"], "not_requested")
        self.assertNotIn("calc_report", result)
        self.assertEqual(len(result["rails"]), 27)
        self.assertTrue(result["brackets"] and result["clamps"])
        self.assertEqual([(p["member_index"], p["x"], p["y0"], p["y1"])
                          for p in result["local_issues"]],
                         [(1, 421.881, 3140, 3200), (3, 1029.881, 3140, 3200),
                          (5, 1637.881, 3190, 3200), (14, 4069.881, 3170, 3200)])
        for issue_index, issue in enumerate(result["local_issues"]):
            rail = result["rails"][issue["member_index"]]
            self.assertEqual(issue["zone_id"], "контур O")
            self.assertEqual(issue["reason"], "insufficient_supports")
            self.assertEqual(issue["status"], "not_verified")
            self.assertEqual(issue["support_count"], 1)
            self.assertEqual(issue["failed_checks"], [])
            self.assertEqual(rail["check_status"], "not_verified")
            self.assertEqual(rail["issue_index"], issue_index)
            self.assertEqual({k: rail[k] for k in ("x", "y0", "y1")},
                             {k: issue[k] for k in ("x", "y0", "y1")})
            supports = [b["y"] for b in result["brackets"] if b["x"] == rail["x"]
                        and rail["y0"] <= b["y"] <= rail["y1"]]
            self.assertEqual(supports, [(rail["y0"] + rail["y1"]) / 2])

    def test_shina_has_at_least_one_vertical_support(self):
        _, result = unsupported_gable_shina()
        if not result["ok"]:
            self.assertEqual(result.get("error_code"), "E_UNSUPPORTED_SHINA")
            self.assertEqual(result["unsupported_counts"], {"pieces": 1, "joints": 0})
            self.assertFalse(any(result.get(k) for k in ("rails", "hrails", "brackets", "clamps")))
            return
        unsupported = [h for h in result["hrails"] if h["kind"].startswith("шина")
                       and not any(r["y0"] - 20 <= h["y"] <= r["y1"] + 20
                                   and h["x0"] <= r["x"] <= h["x1"]
                                   for r in result["rails"])]
        self.assertFalse(unsupported,
                         "A warning/INFO does not supply a physical connection: " +
                         json.dumps(unsupported, ensure_ascii=False))

    def test_supported_tile_rectangle_still_builds(self):
        request, _ = unsupported_gable_shina()
        request["contours"] = [{"id": "rectangle", "pts": rect(0, 0, 4800, 6000)}]
        result = fe.run(request)
        self.assertTrue(result["ok"], result.get("error"))
        self.assertTrue(result["rails"] and result["hrails"])

    def test_interfloor_calc_requires_two_geometric_ngp_supports(self):
        request = {"system": "Межэтажная", "sub_type": "interfloor",
                   "contours": [{"outer": rect(0, 0, 3600, 6000)}],
                   "joints_x": [300, 900, 1500, 2100, 2700, 3300], "floors_y": [3000],
                   "calc": {"terrain": "B", "height": 15, "q_clad": 8,
                            "offset": 150, "na_max": 3000, "wind_region": "II"}}
        single = fp.frame_plan(copy.deepcopy(request))
        self.assertFalse(single["ok"])
        self.assertEqual(single["error_code"], "E_CALC_TOPOLOGY_UNSUPPORTED")
        self.assertGreater(single["unsupported_counts"]["nsp_pieces"], 0)
        self.assertTrue(all(len(r["support_y"]) == 1 for r in single["unsupported"]))
        self.assertFalse(any(single.get(k) for k in ("rails", "hrails", "brackets", "clamps")))
        normal = fp.frame_plan(dict(copy.deepcopy(request), floors_y=[0, 3000, 6000]))
        self.assertFalse(normal["ok"])
        self.assertEqual(normal["error_code"], "E_CALC_MODEL_UNCONFIRMED")
        self.assertEqual(normal["static_model"]["geometric_screening"]["reasons"],
                         [{"reason": "interfloor_model_unconfirmed"}])
        self.assertEqual(normal["static_model"]["status"], "not_verified")
        self.assertFalse(any(normal.get(k) for k in ("rails", "hrails", "brackets", "clamps", "fittings")))
        manual = copy.deepcopy(request)
        manual.pop("calc")
        self.assertTrue(fp.frame_plan(manual)["ok"])

    def test_shifted_window_rail_uses_its_actual_corner_zone(self):
        request = {"system": "Вектор-1", "sub_type": "vertical",
                   "contours": [{"outer": rect(0, 0, 6000, 6000),
                                 "holes": [rect(1590, 2170, 2490, 3820)]}],
                   "joints_x": [600, 1200, 1586, 2040, 2494, 3100, 3700, 4300, 4900, 5500],
                   "rows_y": [600, 1200, 1800, 2400, 3000, 3600, 4200, 4800, 5400],
                   "calc": {"terrain": "B", "height": 30, "q_clad": 25,
                            "offset": 230, "na_max": 3000, "wind_region": "II"}}
        result = fp.frame_plan(request)
        self.assertTrue(result["ok"], result.get("error"))
        model = result["calc_report"]["static_model"]
        shifted = [m for m in model["members"] if m["geometry"]["x"] == 1490]
        self.assertTrue(shifted)
        self.assertTrue(all(m["zone"] == "corner" for m in shifted))
        cases = model["member_calculation"]["cases"]
        self.assertTrue(all(cases[m["calculation_case"]]["zone"] == "corner" and
                            cases[m["calculation_case"]]["chain"]["passed"] for m in shifted))
        # The manual mode still honours the same corner pitch independently.
        manual = copy.deepcopy(request)
        manual.pop("calc")
        arithmetic = fc.report(result["calc_report"]["inputs"])
        steps = {"main": arithmetic["row"]["step"], "corner": arithmetic["corner"]["step"]}
        manual["system"] = {"name": "Вектор-1", "bracket_step": steps["main"],
                            "bracket_step_corner": steps["corner"]}
        manual_result = fp.frame_plan(manual)
        self.assertTrue(manual_result["ok"], manual_result.get("error"))
        points = sorted(b["y"] for b in manual_result["brackets"] if b["x"] == 1490)
        self.assertGreaterEqual(len(points), 3)
        maximum = max(b - a for a, b in zip(points, points[1:]))
        self.assertLessEqual(maximum, steps["corner"] + .001)
        self.assertNotIn("calc_report", manual_result)

    def test_actual_generated_spacing_is_checked_before_output(self):
        request, _, _, _ = native_short_rails()
        request["contours"] = [{"outer": rect(0, 0, 1800, 6000)}]
        self.assertTrue(fp.frame_plan(request)["ok"])
        # Simulate an over-wide generated interval to exercise the final gate.
        # The initial calculator remains real and approves the normal steps.
        # Local refinement can now repair these intervals. It must check the
        # repaired physical supports before any drawable result is returned.
        with patch.object(fp, "_rail_brackets", side_effect=lambda a, b, *args:
                          [a + 100, a + 1000, a + 2000, b - 100]):
            result = fp.frame_plan(request)
        self.assertTrue(result["ok"], result.get("error"))
        report = result["calc_report"]
        self.assertTrue(report["support_refinements"])
        self.assertTrue(report["layout_verification"]["passed"])
        self.assertTrue(all(c["chain"]["passed"] for c in report["static_model"]["member_calculation"]["cases"]))

    def test_explicit_invalid_calc_dimensions_are_not_defaults(self):
        request, _, _ = long_floor()
        for field in ("b", "b_corner", "rail_len", "max_step"):
            for value in (0, -1, float("nan"), float("inf")):
                with self.subTest(field=field, value=value):
                    req = copy.deepcopy(request)
                    req["calc"][field] = value
                    result = fp.frame_plan(req)
                    self.assertFalse(result["ok"])
                    self.assertEqual(result["error_code"], "E_CALC_NOT_PASSED")
                    self.assertIn(field, result["error"])
                    self.assertFalse(any(result.get(k) for k in ("rails", "hrails", "brackets", "clamps")))

    def test_vertical_profile_option_cannot_override_nsp_section(self):
        request, _, _ = long_floor()
        request["rail_profile"] = "ГП-40-40"
        with patch.object(fc, "report", wraps=fc.report) as observed:
            fp.frame_plan(request)
        self.assertEqual(observed.call_args.args[0]["profile"], "НСП-69-60-1,2")

    def test_no_common_vertical_profile_refuses_instead_of_mixing_sections(self):
        request, _, _, _ = native_short_rails()
        request.pop("rail_profile", None)
        differing = {"row": {"profile": "ГП-40-40-1,2", "step": 600},
                     "corner": {"profile": "ШП-60-20-1,2", "step": 600}}
        failed = {"row": {"step": None}, "corner": {"step": None}}
        # Isolate the contractual branch: independent winners are not one
        # materialised section, even if every shared candidate fails.
        with patch.object(fc, "report", side_effect=lambda inp: differing if inp["auto_profile"] else failed):
            result = fp.frame_plan(request)
        self.assertFalse(result["ok"])
        self.assertEqual(result["error_code"], "E_CALC_NOT_PASSED")
        self.assertIn("единого профиля", result["error"])
        self.assertFalse(result.get("rails"))

    def test_shorter_explicit_span_cannot_override_actual_floors(self):
        request, _, _ = long_floor()
        request["calc"]["rail_len"] = 2000
        with patch.object(fc, "report", wraps=fc.report) as observed:
            fp.frame_plan(request)
        self.assertEqual(observed.call_args.args[0]["rail_len"], 4000)


if __name__ == "__main__":
    unittest.main(verbosity=2)
