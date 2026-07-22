#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Юниты CLI clad_engine (op=cladding) и группировки контуров.
Запуск: PYTHONUTF8=1 python3 -m unittest (из AClad/engine)."""

import json
import os
import subprocess
import sys
import tempfile
import unittest

import clad_engine as ce


def rect(x0, y0, w, h):
    return [[x0, y0], [x0 + w, y0], [x0 + w, y0 + h], [x0, y0 + h]]


def zone_dict(outer_pts, openings=(), **kw):
    d = {"schema": "facade_zone/1", "id": kw.pop("zid", "Z-01"),
         "units": kw.pop("units", "mm"),
         "outer": {"pts": outer_pts}, "openings": list(openings)}
    d.update(kw)
    return d


def opening(oid, pts, kind="window", bulges=None):
    o = {"id": oid, "kind": kind, "poly": {"pts": pts}}
    if bulges is not None:
        o["poly"]["bulges"] = bulges
    return o


class TestOpCladding(unittest.TestCase):
    """op="cladding" — раскладка по зонам и контурам."""

    def _req(self, **kw):
        base = {"op": "cladding", "tile": {"w": 600, "h": 600},
                "gap": {"v": 10, "h": 10}, "datum": 0.0, "mode": "edge"}
        base.update(kw)
        return base

    def test_zone_simple(self):
        # зона 3040×1210 без проёмов — 5×2 целых, метка зоны на вставках
        z = zone_dict(rect(0, 0, 3040, 1210), zid="Ф-1")
        res = ce.run(self._req(zones=[{"zone_id": "Ф-1", "zone": z}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"],
                         {"tiles": 10, "full": 10, "cut": 0, "zones": 1})
        self.assertTrue(all(t["zone"] == "Ф-1" for t in res["inserts"]))
        self.assertEqual(res["per_zone"][0]["zone_id"], "Ф-1")

    def test_zone_units_m(self):
        z = zone_dict(rect(0, 0, 3.04, 1.21), zid="Ф-2", units="m")
        res = ce.run(self._req(zones=[{"zone_id": "Ф-2", "zone": z}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["tiles"], 10)

    def test_zone_with_arc_skipped(self):
        z = zone_dict(rect(0, 0, 3040, 1210),
                      [opening("A", rect(500, 400, 600, 600),
                               bulges=[0.5, 0, 0, 0])],
                      zid="Ф-4")
        ok_z = zone_dict(rect(5000, 0, 3040, 1210), zid="Ф-5")
        res = ce.run(self._req(zones=[{"zone_id": "Ф-4", "zone": z},
                                      {"zone_id": "Ф-5", "zone": ok_z}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 1)
        self.assertTrue(any("Ф-4" in n and "дуг" in n for n in res["notes"]))

    def test_raw_contours_nesting(self):
        # голые полилинии: окно = контур в контуре, вложенность сама;
        # outer_id проброшен для метки ATCLAD на полилинии
        res = ce.run(self._req(contours=[
            {"id": "AAA", "pts": rect(0, 0, 3040, 1820)},
            {"id": "BBB", "pts": rect(1220, 610, 610, 600)}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 1)
        self.assertEqual(res["per_zone"][0]["outer_id"], "AAA")
        self.assertEqual(res["summary"]["tiles"], 14)

    def test_raw_contour_arc_skipped(self):
        res = ce.run(self._req(contours=[
            {"id": "ARC", "pts": rect(0, 0, 1220, 600),
             "bulges": [0.3, 0, 0, 0]},
            {"id": "OK", "pts": rect(5000, 0, 1220, 600)}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 1)
        self.assertTrue(any("ARC" in n and "дуг" in n
                            for n in res["notes"]))

    def test_nested_deep_skipped(self):
        # контур внутри проёма (глубина 2) — пропуск с note
        res = ce.run(self._req(contours=[
            {"id": "OUT", "pts": rect(0, 0, 3040, 1820)},
            {"id": "WIN", "pts": rect(600, 600, 1200, 900)},
            {"id": "DEEP", "pts": rect(900, 900, 300, 300)}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 1)
        self.assertTrue(any("DEEP" in n and "глубже" in n
                            for n in res["notes"]))

    def test_door_touching_bottom_is_hole(self):
        # дверь до низа участка (касание границы) — распознаётся проёмом
        res = ce.run(self._req(contours=[
            {"id": "OUT", "pts": rect(0, 0, 3040, 1820)},
            {"id": "DOOR", "pts": rect(1220, 0, 610, 1210)}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 1)
        # дверной столб пуст до отм. 1210 — камней с y<600 в нём нет
        self.assertFalse(any(1220 <= t["x"] < 1830 and t["y"] < 500
                             for t in res["inserts"]))

    def test_zones_and_contours_together(self):
        z = zone_dict(rect(0, 0, 1220, 600), zid="Ф-6")
        res = ce.run(self._req(
            zones=[{"zone_id": "Ф-6", "zone": z}],
            contours=[{"id": "CCC", "pts": rect(5000, 0, 1220, 600)}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 2)
        self.assertEqual(res["summary"]["tiles"], 4)

    def test_min_cut_default_150(self):
        z = zone_dict(rect(0, 0, 700, 600), zid="Ф-7")
        res = ce.run(self._req(zones=[{"zone_id": "Ф-7", "zone": z}]))
        self.assertEqual(res["summary"]["tiles"], 1)
        self.assertTrue(any("min_cut" in n for n in res["notes"]))
        res = ce.run(self._req(zones=[{"zone_id": "Ф-7", "zone": z}],
                               min_cut=50))
        self.assertEqual(res["summary"]["tiles"], 2)

    def test_empty_request(self):
        res = ce.run({"op": "cladding", "tile": {"w": 600, "h": 600}})
        self.assertFalse(res["ok"])
        self.assertIn("нет пригодных", res["error"])

    def test_unknown_op(self):
        res = ce.run({"op": "zones"})
        self.assertFalse(res["ok"])
        self.assertIn("cladding", res["error"])

    def test_cli_broken_input_writes_out(self):
        with tempfile.TemporaryDirectory() as td:
            fin = os.path.join(td, "in.json")
            fout = os.path.join(td, "out.json")
            with open(fin, "w", encoding="utf-8") as f:
                f.write("{broken json")
            p = subprocess.run(
                [sys.executable,
                 os.path.join(os.path.dirname(__file__),
                              "clad_engine.py"), fin, fout],
                capture_output=True)
            self.assertNotEqual(p.returncode, 0)
            with open(fout, "r", encoding="utf-8") as f:
                res = json.load(f)
            self.assertFalse(res["ok"])
            self.assertIn("ошибка", res["error"])


if __name__ == "__main__":
    unittest.main()
