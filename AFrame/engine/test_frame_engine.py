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
        """floor_step доезжает до frame_plan (26.07)."""
        res = fe.run({
            "op": "frame", "system": "Standart",
            "contours": [{"id": "A", "pts": rect(0, 0, 1220, 6100)}],
            "joints_x": [610], "floor_step": 3000})
        self.assertTrue(res["ok"], res)
        self.assertEqual(res["summary"]["rails"], 3)
        self.assertEqual(res["summary"]["brackets_main"], 2)

    def test_bad_op_and_empty(self):
        self.assertFalse(fe.run({"op": "nope"})["ok"])
        self.assertFalse(fe.run({"op": "frame", "system": "Standart",
                                 "joints_x": [1]})["ok"])

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
