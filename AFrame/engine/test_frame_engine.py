# -*- coding: utf-8 -*-
"""Юниты CLI frame_engine (unittest — гоняется python3 -m unittest)."""
import json
import os
import tempfile
import unittest

import frame_engine as fe


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


class TestFrameEngine(unittest.TestCase):
    def test_frame_contours(self):
        """Голый контур с дырой-проёмом: группировка + расстановка."""
        res = fe.run({
            "op": "frame", "system": "Standart",
            "contours": [
                {"id": "A", "pts": rect(0, 0, 3000, 6000)},
                {"id": "B", "pts": rect(1000, 2000, 2000, 3500)}],
            "joints_x": [500, 1500, 2500], "floors_y": [3000],
            "rows_y": [605 * i for i in range(1, 10)]})
        self.assertTrue(res["ok"], res)
        self.assertEqual(res["summary"]["zones"], 1)
        r15 = sorted((r["y0"], r["y1"]) for r in res["rails"]
                     if abs(r["x"] - 1500) < 1e-6)
        self.assertEqual(r15, [(0.0, 2000.0), (3500.0, 6000.0)])
        self.assertTrue(all(t["zone"] == "контур A"
                            for t in res["rails"]))
        self.assertEqual(res["system_used"]["rail_width"], 40.0)

    def test_frame_zone(self):
        """Зона facade_zone/1 (contour+openings) как от ATCLAD."""
        res = fe.run({
            "op": "frame", "system": "Вектор-1",
            "zones": [{"zone_id": "Ф-1", "zone": {
                "contour": {"pts": rect(0, 0, 1220, 6000)},
                "openings": []}}],
            "joints_x": [610], "floors_y": [3000]})
        self.assertTrue(res["ok"], res)
        self.assertEqual(res["summary"]["brackets_main"], 1)
        self.assertEqual(res["summary"]["rails"], 2)

    def test_floor_step_passthrough(self):
        """floor_step доезжает до frame_plan; вертикальная БЕЗ
        отметок работает хлыстами — несущих нет (07.08a, В-ад)."""
        res = fe.run({
            "op": "frame", "system": "Standart",
            "contours": [{"id": "A", "pts": rect(0, 0, 1220, 6100)}],
            "joints_x": [610], "floor_step": 3000})
        self.assertTrue(res["ok"], res)
        self.assertEqual(res["summary"]["rails"], 3)
        self.assertEqual(res["summary"]["brackets_main"], 0)
        # межэтажной floor_step по-прежнему строит отметки
        res2 = fe.run({
            "op": "frame", "system": "Межэтажная",
            "sub_type": "interfloor",
            "contours": [{"id": "A", "pts": rect(0, 0, 2440, 6100)}],
            "joints_x": [610, 1830], "floor_step": 3000})
        self.assertTrue(res2["ok"], res2)
        self.assertGreater(res2["summary"]["brackets_main"], 0)

    def test_bad_op_and_empty(self):
        self.assertFalse(fe.run({"op": "nope"})["ok"])
        self.assertFalse(fe.run({"op": "frame", "system": "Standart",
                                 "joints_x": [1]})["ok"])


    def test_calc_passthrough(self):
        """CLI preserves both the real corner failure and an explicit row pass."""
        req = {
            "op": "frame", "system": "Вектор-1",
            "contours": [{"id": "A", "pts": rect(0, 0, 5000, 6000)}],
            "joints_x": [i * 608.0 + 304 for i in range(8)],
            "floors_y": [3000],
            "calc": {"wind_region": "II", "terrain": "B",
                     "height": 41.2, "q_clad": 25, "offset": 230,
                     "na_max": 1880, "profile": "ШП-60-20-20-1,2",
                     "q_rails": 1.21}}
        failed = fe.run(req)
        self.assertFalse(failed["ok"])
        self.assertEqual(failed["error_code"], "E_CALC_MEMBER_CAPACITY")
        self.assertEqual(failed["failed_zone"], "контур A")
        self.assertTrue(all(r["failed_checks"] == ["кронштейн 1-1, кг/см²"]
                            for r in failed["unsupported"]))
        self.assertFalse(failed.get("rails"))
        # Corner declaration is explicit, not a weakened load or missed failure.
        res = fe.run(dict(req, corners_x=[]))
        self.assertTrue(res["ok"], res.get("error"))
        self.assertEqual(res["summary"]["calc_steps"], {"main": 800, "corner": 450})
        self.assertEqual(len(res["calc_report"]["row"]["checks"]), 8)
        self.assertTrue(any("РАСЧЁТУ" in n for n in res["notes"]))
        self.assertEqual(res["calc_report"]["static_model"]["member_calculation"]["status"], "passed")

    def test_interfloor_hrails_via_cli(self):
        """Регресс 27.07: sub_type/hrails/fittings терялись в
        CLI-обёртке (26.07d правил только frame_plan)."""
        res = fe.run({
            "op": "frame", "system": "Межэтажная",
            "sub_type": "interfloor",
            "contours": [{"id": "A", "pts": rect(0, 0, 5000, 7000)}],
            "joints_x": [800, 1600, 2400, 3200, 4000],
            "floors_y": [3000, 6000], "rows_y": []})
        self.assertTrue(res["ok"], res)
        self.assertTrue(res["summary"]["hrails"] >= 2,
                        res["summary"])
        self.assertIn("hrails_lm", res["summary"])


    def test_corners_passthrough(self):
        """Фидбэк Германа 27.07: corners_x пробрасывается — угловая
        зона только у указанного угла."""
        res = fe.run({
            "op": "frame", "system": "Вектор-1",
            "contours": [{"id": "A", "pts": rect(0, 0, 6000, 3000)}],
            "joints_x": [304 + i * 608.0 for i in range(10)],
            "floors_y": [], "corners_x": [0.0]})
        self.assertTrue(res["ok"], res)
        def steps(x):
            ys = sorted(b["y"] for b in res["brackets"]
                        if abs(b["x"] - x) < 1)
            return [ys[i + 1] - ys[i] for i in range(len(ys) - 1)]
        self.assertLessEqual(max(steps(304.0)), 801)
        self.assertGreater(max(steps(304 + 9 * 608.0)), 801)

    def test_cli_files(self):
        d = tempfile.mkdtemp()
        pin = os.path.join(d, "in.json")
        pout = os.path.join(d, "out.json")
        with open(pin, "w", encoding="utf-8") as f:
            json.dump({"op": "frame", "system": "Standart",
                       "contours": [{"id": "A",
                                     "pts": rect(0, 0, 610, 3000)}],
                       "joints_x": [305]}, f)
        rc = fe.main(["frame_engine", pin, pout])
        self.assertEqual(rc, 0)
        with open(pout, encoding="utf-8") as f:
            res = json.load(f)
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["rails"], 1)

    def test_tile_cladding_passes_through_zones(self):
        """26.09 (Денис) / 29.09c (Герман): облицовка «клинкер», шаг
        направляющих, угловой шаг, хлыст и марка шины доходят до каждой зоны
        (ГРАБЛЯ-13): направляющие шагом, а не по своим швам зоны; шины трёх
        видов по рядам зоны; кляммеров нет; итог — шины по видам."""
        res = fe.run({
            "op": "frame", "sub_type": "vertical", "parts": "frame",
            "system": {"name": "Standart", "bracket_step": 600,
                       "bracket_step_corner": 600},
            "exact_step": True, "cladding": "clinker", "tile_step_x": 600,
            "tile_step_x_corner": 300, "corners_x": [0], "tile_whip": 1500,
            "tile_rail_brand": "ШК-1",
            "contours": [{"id": "A", "pts": rect(0, 0, 2000, 1500),
                          "joints_x": [130 * k for k in range(1, 15)],
                          "rows_y": [72 * k for k in range(1, 20)]}]})
        self.assertTrue(res["ok"], res)
        xs = sorted(set(r["x"] for r in res["rails"]))
        self.assertEqual(xs, [100.0, 400.0, 700.0, 1000.0, 1300.0, 1500.0, 1900.0])
        # 19 рядов + стартовая + концевая, каждый прогон 2000 = 1500 + 500
        self.assertEqual(len(res["hrails"]), 42)
        self.assertTrue(all(h["kind"].startswith("шина") and h["zone"] == "контур A" and
                            h["profile"] == "ШК-1" for h in res["hrails"]))
        self.assertEqual(len(res["clamps"]), 0)
        sm = res["summary"]
        self.assertEqual((sm["shina_start"], sm["shina_row"], sm["shina_end"]), (2, 38, 2))
        self.assertEqual(sm["tile_rail_brand"], "ШК-1")
        self.assertEqual(sm["hguides"], 0)


if __name__ == "__main__":
    unittest.main()
