"""Резка вертикальных направляющих: зазоры, отступы, окна и точный отказ."""
import unittest
from collections import Counter

from frame_plan import _rail_cuts, _rail_brackets, frame_plan
from frame_topology import geometric_members
from frame_engine import op_frame
from frame_supports import refine_vertical_supports


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


class RailCuttingTests(unittest.TestCase):
    def test_rebalances_only_last_pair_without_changing_gap_or_bounds(self):
        self.assertEqual(_rail_cuts(0, 6100, 3000, 10, 300),
                         [(0, 3000), (3010, 4550), (4560, 6100)])
        for length in (3001, 3005, 3010, 3240, 3290, 3300, 3600, 6010, 6015, 6100):
            with self.subTest(length=length):
                parts = _rail_cuts(100, 100 + length, 3000, 10, 300)
                self.assertEqual(parts[0][0], 100)
                self.assertEqual(parts[-1][1], 100 + length)
                self.assertTrue(all(600 < b - a <= 3000 for a, b in parts))
                self.assertTrue(all(c - b == 10 for (_, b), (c, _) in zip(parts, parts[1:])))
                self.assertTrue(all(len(_rail_brackets(a, b, 300, 800)) >= 2 for a, b in parts))

    def test_regular_stock_and_explicit_floor_seams_stay_unchanged(self):
        self.assertEqual(_rail_cuts(0, 5600, 3000, 10, 300), [(0, 3000), (3010, 5600)])
        result = frame_plan(dict(system="Standart", contours=[dict(outer=rect(0, 0, 1200, 6000))],
                                 joints_x=[600], floors_y=[3000]))
        self.assertTrue(result["ok"], result.get("error"))
        self.assertEqual([(r["y0"], r["y1"]) for r in result["rails"]], [(0, 2995), (3005, 6000)])

    def test_two_supports_depend_on_setting_not_invented_minimum(self):
        request = dict(system={"name": "Standart", "bracket_start_offset": 300},
                       contours=[dict(outer=rect(0, 0, 1200, 500))], joints_x=[600])
        result = frame_plan(request)
        self.assertFalse(result["ok"])
        self.assertEqual(result["error_code"], "E_UNSUPPORTED_RAIL")
        self.assertEqual(result["unsupported"][0], dict(member_index=0, x=600, y0=0, y1=500,
            length=500, support_y=[250], support_count=1, bracket_start_offset=300))
        self.assertFalse(any(result.get(k) for k in ("rails", "hrails", "brackets", "clamps")))
        request["system"]["bracket_start_offset"] = 100  # Explicit test input, not a design recommendation.
        result = frame_plan(request)
        self.assertTrue(result["ok"], result.get("error"))
        self.assertEqual([b["y"] for b in result["brackets"]], [100, 400])

    def test_window_tail_is_repaired_in_manual_equal_and_exact_spacing(self):
        for exact in (False, True):
            result = frame_plan(dict(system="Standart", exact_step=exact, corners_x=[],
                contours=[dict(outer=rect(0, 0, 9000, 12000), holes=[rect(1200, 7200, 2700, 8700)])],
                joints_x=list(range(600, 9000, 600)), parts="frame"))
            self.assertTrue(result["ok"], result.get("error"))
            members, _ = geometric_members("vertical", result["rails"], result["hrails"], result["brackets"])
            self.assertTrue(all(m["support_count"] >= 2 for m in members))
            self.assertTrue(all(r["len"] <= 3000 for r in result["rails"]))
            self.assertFalse(any(1200 < r["x"] < 2700 and r["y0"] < 8700 and r["y1"] > 7200
                                 for r in result["rails"]))
            tail = sorted((r["y0"], r["y1"]) for r in result["rails"] if r["x"] == 1800 and r["y0"] >= 8700)
            self.assertEqual(tail, [(8700, 10345), (10355, 12000)])
            self.assertTrue(any("Перераспределены" in n for n in result["notes"]))

    def test_zero_gap_keeps_two_physical_pieces(self):
        self.assertEqual(_rail_cuts(0, 3300, 3000, 0, 300), [(0, 1650), (1650, 3300)])
        self.assertEqual(_rail_cuts(0, 500, 3000, 10, 300), [(0, 500)])

    def test_fifteen_windows_calculate_and_only_overloaded_piece_gets_extra_support(self):
        # Контрольный аналог ревизии. Координаты явные; число 190 из чужого
        # отчёта не воспроизводится без его JSON. Поля расчёта — defaults формы.
        request = dict(op="frame", system={"name": "Вектор-1"}, sub_type="vertical",
            parts="frame", cladding="porcelain", corners_x=[0, 18000],
            contours=[dict(id="wall", pts=rect(0, 0, 18000, 12000))] +
                [dict(id="w%d_%d" % (x, y), pts=rect(x, y, x + 1500, y + 1500))
                 for x in (1500, 4500, 7500, 10500, 13500) for y in (1200, 4200, 7200)],
            joints_x=list(range(608, 18000, 608)),
            calc=dict(wind_region="II", terrain="B", height=30, q_clad=25, offset=230,
                      na_max=3000, gamma_clad=1.1))
        result = op_frame(request)
        self.assertTrue(result["ok"], result.get("error"))
        model = result["calc_report"]["static_model"]
        self.assertEqual(model["member_count"], 148)
        self.assertTrue(all(m["support_count"] >= 2 for m in model["members"]))
        self.assertTrue(all(c["chain"]["passed"] for c in model["member_calculation"]["cases"]))
        self.assertEqual(result["calc_report"]["support_refinements"], [dict(member_index=142,
            x=1400, y0=1150, y1=2750, previous_max_step=500, actual_max_step=333.3334,
            brackets_before=3, brackets_after=4)])
        self.assertEqual(result["summary"]["brackets_row"], 553)
        manual = dict(request)
        manual.pop("calc")
        manual["system"] = dict(name="Вектор-1", bracket_step=800, bracket_step_corner=500)
        before = op_frame(manual)
        self.assertTrue(before["ok"], before.get("error"))
        self.assertEqual(result["rails"], before["rails"])
        unchanged = lambda brackets: {(b["x"], b["y"], b["kind"]) for b in brackets
                                     if not (b["x"] == 1400 and 1150 <= b["y"] <= 2750)}
        self.assertEqual(unchanged(result["brackets"]), unchanged(before["brackets"]))

    def test_refinement_does_not_move_support_shared_with_another_piece(self):
        rails = [dict(x=600, y0=0, y1=1000), dict(x=600, y0=1000, y1=2000)]
        brackets = [dict(x=600, y=y, kind="рядовой") for y in (0, 500, 1000, 1500, 2000)]
        members, horizontal = geometric_members("vertical", rails, [], brackets)
        model = dict(members=members, geometric_screening=dict(reasons=[
            dict(reason="member_capacity_exceeded", member_index=i) for i in (0, 1)]))
        result, changes = refine_vertical_supports(rails, brackets, model, {}, 0)
        self.assertIs(result, brackets)
        self.assertEqual(changes, [])

    def test_tall_window_side_stock_preserves_clamps_only_roundtrip(self):
        request = dict(system="Standart", contours=[dict(outer=rect(0, 0, 4000, 9000),
            holes=[rect(1500, 1200, 2500, 6200)])], joints_x=[1000, 3000],
            rows_y=list(range(600, 9000, 600)))
        result = frame_plan(request)
        self.assertTrue(result["ok"], result.get("error"))
        sides = [r for r in result["rails"] if r["x"] in (1400, 2600)]
        self.assertEqual(len(sides), 4)
        self.assertTrue(all(r["len"] <= 3000 for r in sides))
        clamps = frame_plan(dict(request, parts="clamps", rails_fixed=result["rails"]))
        key = lambda items: Counter((c["x"], c["y"], c["kind"], c.get("orient")) for c in items)
        self.assertEqual(key(result["clamps"]), key(clamps["clamps"]))


if __name__ == "__main__":
    unittest.main()
