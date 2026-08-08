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
        """Этап 4: блок calc пробрасывается, шаги по расчёту, отчёт
        в выходе (кейс республиканской: 800/450)."""
        res = fe.run({
            "op": "frame", "system": "Вектор-1",
            "contours": [{"id": "A", "pts": rect(0, 0, 5000, 6000)}],
            "joints_x": [i * 608.0 + 304 for i in range(8)],
            "floors_y": [3000],
            "calc": {"wind_region": "II", "terrain": "B",
                     "height": 41.2, "q_clad": 25, "offset": 230,
                     "na_max": 1880, "profile": "ШП-60-20-20-1,2",
                     "q_rails": 1.21}})
        self.assertTrue(res["ok"], res)
        self.assertEqual(res["summary"]["calc_steps"],
                         {"main": 800, "corner": 450})
        self.assertEqual(len(res["calc_report"]["row"]["checks"]), 8)
        self.assertTrue(any("РАСЧЁТУ" in n for n in res["notes"]))

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


if __name__ == "__main__":
    unittest.main()
