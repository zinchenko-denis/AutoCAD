#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Юниты кромок проёмов, группировки контуров и CLI facades_engine."""

import json
import math
import os
import subprocess
import sys
import tempfile
import unittest

import facade_zones as fz
import facades_engine as fe


def rect(x0, y0, w, h):
    return [[x0, y0], [x0 + w, y0], [x0 + w, y0 + h], [x0, y0 + h]]


def zone_dict(outer_pts, openings=(), **kw):
    d = {"schema": fz.SCHEMA, "id": kw.pop("zid", "Z-01"),
         "units": kw.pop("units", "mm"),
         "outer": {"pts": outer_pts}, "openings": list(openings)}
    d.update(kw)
    return d


def opening(oid, pts, kind="window", bulges=None):
    o = {"id": oid, "kind": kind, "poly": {"pts": pts}}
    if bulges is not None:
        o["poly"]["bulges"] = bulges
    return o


class TestOpeningEdges(unittest.TestCase):
    def test_window_edges(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("W", rect(1500, 900, 1500, 1500))]))
        rep = fz.zone_report(z)
        e = rep["openings"][0]["edges"]
        self.assertAlmostEqual(e["bottom_m"], 1.5, places=9)
        self.assertAlmostEqual(e["top_m"], 1.5, places=9)
        self.assertAlmostEqual(e["sides_m"], 3.0, places=9)
        self.assertAlmostEqual(e["on_boundary_m"], 0.0, places=9)
        self.assertAlmostEqual(rep["sills_total_m"], 1.5, places=9)
        self.assertAlmostEqual(rep["jambs_total_m"], 4.5, places=9)

    def test_door_on_zone_bottom_no_sill(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("D", rect(5000, 0, 900, 2100), kind="door")]))
        rep = fz.zone_report(z)
        e = rep["openings"][0]["edges"]
        self.assertAlmostEqual(e["on_boundary_m"], 0.9, places=9)
        self.assertAlmostEqual(e["bottom_m"], 0.0, places=9)
        self.assertAlmostEqual(e["top_m"], 0.9, places=9)
        self.assertAlmostEqual(e["sides_m"], 4.2, places=9)
        self.assertAlmostEqual(rep["sills_total_m"], 0.0, places=9)
        self.assertAlmostEqual(rep["jambs_total_m"], 5.1, places=9)

    def test_arched_window_edges(self):
        # прямоугольник 1000 ширина, 1000 бока + полукруглый верх R=500
        wpts = [[1000, 900], [2000, 900], [2000, 1900], [1000, 1900]]
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 4000),
            [opening("W", wpts, bulges=[0, 0, 1.0, 0])]))
        rep = fz.zone_report(z)
        e = rep["openings"][0]["edges"]
        self.assertAlmostEqual(e["bottom_m"], 1.0, places=9)
        self.assertAlmostEqual(e["on_boundary_m"], 0.0, places=9)
        # дуга R=0.5: полная pi*R=1.571; «пологий» сектор ±45° от вершины
        # (наклон хорд <45°) = R*pi/2 = 0.785 идёт в top, крутые четверти —
        # в sides: 2*1.0 + (1.571-0.785) = 2.785
        arc = math.pi * 0.5
        self.assertAlmostEqual(e["top_m"] + e["sides_m"], 2.0 + arc, delta=0.01)
        self.assertAlmostEqual(e["top_m"], 0.785, delta=0.01)
        self.assertAlmostEqual(e["sides_m"], 2.785, delta=0.01)

    def test_sum_edges_equals_perimeter(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("W", rect(1000, 500, 2000, 1500)),
             opening("D", rect(6000, 0, 900, 2100))]))
        rep = fz.zone_report(z)
        for o in rep["openings"]:
            e = o["edges"]
            s = e["bottom_m"] + e["top_m"] + e["sides_m"] + e["on_boundary_m"]
            self.assertAlmostEqual(s, o["perimeter_m"], places=6)


class TestBuildZones(unittest.TestCase):
    def contours(self):
        return [
            {"id": "A", "pts": rect(0, 0, 12000, 3000)},
            {"id": "A1", "pts": rect(1000, 900, 1500, 1500)},
            {"id": "A2", "pts": rect(4000, 900, 1500, 1500)},
            {"id": "B", "pts": rect(20000, 0, 6000, 3000)},
            {"id": "B1", "pts": rect(21000, 0, 900, 2100)},
        ]

    def test_grouping(self):
        zds, issues = fz.build_zones_from_contours(
            self.contours(), cladding="керамогранит 600х600",
            zone_prefix="Ф-", start_index=1)
        self.assertEqual(len(zds), 2)
        self.assertFalse(issues)
        # оба контура в одном «ряду» (одинаковый minY) -> справа налево:
        # B (x до 26000) правее A (x до 12000) -> Ф-1 = B
        self.assertEqual(zds[0]["meta"]["outer_contour_id"], "B")
        self.assertEqual(zds[0]["id"], "Ф-1")
        self.assertEqual([o["id"] for o in zds[0]["openings"]], ["B1"])
        big = zds[1]
        self.assertEqual(big["meta"]["outer_contour_id"], "A")
        self.assertEqual(big["id"], "Ф-2")
        self.assertEqual(sorted(o["id"] for o in big["openings"]),
                         ["A1", "A2"])
        self.assertEqual(big["cladding"], "керамогранит 600х600")
        # результат — валидные facade_zone/1
        for zd in zds:
            z = fz.load_zone(zd)
            self.assertFalse(fz._has_errors(fz.validate_zone(z)))

    def test_nested_too_deep(self):
        cs = self.contours() + [{"id": "X", "pts": rect(1200, 1000, 300, 300)}]
        zds, issues = fz.build_zones_from_contours(cs)
        self.assertEqual(len(zds), 2)
        self.assertIn("E_NESTED_DEEP", [i.code for i in issues])

    def test_bad_contour_skipped(self):
        cs = self.contours() + [{"id": "BAD", "pts": [[0, 0], [1, 1]]}]
        zds, issues = fz.build_zones_from_contours(cs)
        self.assertEqual(len(zds), 2)
        self.assertIn("E_BAD_CONTOUR", [i.code for i in issues])

    def test_tail_autoclose(self):
        cs = [{"id": "A", "pts": rect(0, 0, 5000, 3000) + [[0.3, 0.0]]}]
        zds, issues = fz.build_zones_from_contours(cs)
        self.assertEqual(len(zds), 1)
        z = fz.load_zone(zds[0])
        self.assertAlmostEqual(abs(z.outer.signed_area()), 15e6, delta=1e3)

    def test_numbering_bottom_up_right_to_left(self):
        # правило Германа: снизу вверх, справа налево (2 ряда x 2 столбца,
        # в ряду minY гуляет на 200 мм при высоте зон 3000 -> один ряд)
        cs = [
            {"id": "TL", "pts": rect(0, 3600, 5000, 3000)},
            {"id": "BL", "pts": rect(0, 200, 5000, 3000)},
            {"id": "BR", "pts": rect(6000, 0, 5000, 3000)},
            {"id": "TR", "pts": rect(6000, 3400, 5000, 3000)},
        ]
        zds, _ = fz.build_zones_from_contours(cs, zone_prefix="Ф-")
        order = [z["meta"]["outer_contour_id"] for z in zds]
        self.assertEqual(order, ["BR", "BL", "TR", "TL"])
        self.assertEqual([z["id"] for z in zds],
                         ["Ф-1", "Ф-2", "Ф-3", "Ф-4"])

    def test_numbering_stack_bottom_up(self):
        # столбик этажных поясов как в «Пробе» Германа: строго снизу вверх
        cs = [{"id": "E%d" % i,
               "pts": rect(0, i * 2850, 7000, 2590)} for i in (3, 0, 2, 1, 4)]
        zds, _ = fz.build_zones_from_contours(cs, zone_prefix="Ф-")
        order = [z["meta"]["outer_contour_id"] for z in zds]
        self.assertEqual(order, ["E0", "E1", "E2", "E3", "E4"])

    def test_bbox_in_meta_and_engine(self):
        zds, _ = fz.build_zones_from_contours(
            [{"id": "A", "pts": rect(100, 200, 5000, 3000)}])
        self.assertEqual(zds[0]["meta"]["bbox"], [100, 200, 5100, 3200])


class TestEngineRun(unittest.TestCase):
    def req(self):
        return {"op": "zones",
                "contours": TestBuildZones().contours(),
                "cladding": "керамогранит идальго 600х600",
                "zone_prefix": "Ф-", "start_index": 1, "units": "mm"}

    def test_run_ok(self):
        res = fe.run(self.req())
        self.assertTrue(res["ok"], msg=str(res))
        self.assertEqual(res["summary"]["count"], 2)
        self.assertEqual(res["summary"]["openings_total"], 3)
        # B правее A -> Ф-1 = B (правило снизу-вверх/справа-налево)
        z1 = res["zones"][0]
        self.assertEqual(z1["zone_id"], "Ф-1")
        self.assertEqual(z1["outer_id"], "B")
        # площадь B: 18 - 1.89 = 16.11; зона A: 36 - 2.25*2 = 31.5
        self.assertAlmostEqual(z1["report"]["area_net_m2"], 16.11, places=9)
        self.assertAlmostEqual(res["summary"]["area_net_total_m2"],
                               31.5 + 16.11, places=9)
        # label внутри контура B, bbox корректен
        lx, ly = z1["label_pt"]
        self.assertTrue(20000 < lx < 26000 and 0 < ly < 3000)
        self.assertEqual(z1["bbox"], [20000, 0, 26000, 3000])
        self.assertEqual(len(res["zones_full"]), 2)
        self.assertEqual(res["failed"], [])

    def test_run_failed_zone(self):
        req = self.req()
        # бабочка как отдельный top-level контур
        req["contours"].append(
            {"id": "BOW", "pts": [[40000, 0], [41000, 1000],
                                  [41000, 0], [40000, 1000]]})
        res = fe.run(req)
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["count"], 2)
        self.assertEqual(len(res["failed"]), 1)
        codes = [i["code"] for i in res["failed"][0]["issues"]]
        self.assertIn("E_SELF_INTERSECT", codes)

    def test_bad_op_and_empty(self):
        self.assertFalse(fe.run({"op": "nope"})["ok"])
        self.assertFalse(fe.run({"op": "zones", "contours": []})["ok"])

    def test_cli_files(self):
        with tempfile.TemporaryDirectory() as td:
            fin = os.path.join(td, "in.json")
            fout = os.path.join(td, "out.json")
            with open(fin, "w", encoding="utf-8") as f:
                json.dump(self.req(), f, ensure_ascii=False)
            env = dict(os.environ, PYTHONUTF8="1")
            p = subprocess.run(
                [sys.executable,
                 os.path.join(os.path.dirname(__file__),
                              "facades_engine.py"), fin, fout],
                capture_output=True, env=env)
            self.assertEqual(p.returncode, 0, msg=p.stderr.decode("utf-8",
                                                                  "replace"))
            with open(fout, "r", encoding="utf-8") as f:
                res = json.load(f)
            self.assertTrue(res["ok"])
            self.assertEqual(res["summary"]["count"], 2)

    def test_merge_mode(self):
        # 2 участка (у A два окна, у B дверь) -> одна зона, суммы
        res = fe.run(dict(self.req(), merge=True))
        self.assertTrue(res["ok"], msg=str(res))
        self.assertEqual(res["summary"]["count"], 1)
        z = res["zones"][0]
        self.assertTrue(z["merged"])
        self.assertEqual(z["part_count"], 2)
        self.assertEqual(z["zone_id"], "Ф-1")
        self.assertEqual(sorted(z["outer_ids"]), ["A", "B"])
        rep = z["report"]
        self.assertAlmostEqual(rep["area_outer_m2"], 36.0 + 18.0, places=9)
        self.assertAlmostEqual(rep["area_net_m2"], 31.5 + 16.11, places=9)
        self.assertEqual(rep["openings_count"], 3)
        self.assertEqual(len(z["dims"]), 2)
        # общий bbox
        self.assertEqual(z["bbox"], [0, 0, 26000, 3000])
        # части в zones_full со сгруппированными id
        ids = [zd["id"] for zd in res["zones_full"]]
        self.assertEqual(sorted(ids), ["Ф-1.1", "Ф-1.2"])
        for zd in res["zones_full"]:
            self.assertEqual(zd["meta"]["group"], "Ф-1")

    def test_dims_rect_with_door(self):
        # дверь на низу разрывает нижнюю кромку: цепочка 5000|900|6100 + высота
        z = fz.load_zone({
            "schema": fz.SCHEMA, "id": "Z", "units": "mm",
            "outer": {"pts": rect(0, 0, 12000, 3000)},
            "openings": [opening("D", rect(5000, 0, 900, 2100),
                                 kind="door")]})
        d = fz.zone_dims(z)
        self.assertEqual(d["height"],
                         {"x": 0.0, "y0": 0.0, "y1": 3000.0})
        self.assertIsNotNone(d["bottom"])
        self.assertEqual(d["bottom"]["xs"], [0.0, 5000.0, 5900.0, 12000.0])

    def test_dims_rect_plain_no_bottom(self):
        # низ без разрывов: горизонтальных размеров нет
        z = fz.load_zone({
            "schema": fz.SCHEMA, "id": "Z", "units": "mm",
            "outer": {"pts": rect(0, 0, 12000, 3000)},
            "openings": [opening("W", rect(1000, 900, 1500, 1500))]})
        d = fz.zone_dims(z)
        self.assertIsNone(d["bottom"])

    def test_cli_broken_input_writes_out(self):
        with tempfile.TemporaryDirectory() as td:
            fin = os.path.join(td, "in.json")
            fout = os.path.join(td, "out.json")
            with open(fin, "w", encoding="utf-8") as f:
                f.write("{broken json")
            p = subprocess.run(
                [sys.executable,
                 os.path.join(os.path.dirname(__file__),
                              "facades_engine.py"), fin, fout],
                capture_output=True)
            self.assertNotEqual(p.returncode, 0)
            with open(fout, "r", encoding="utf-8") as f:
                res = json.load(f)
            self.assertFalse(res["ok"])
            self.assertIn("ошибка", res["error"])


class TestOpCladding(unittest.TestCase):
    """op="cladding" (этап 2, ATCLAD) — раскладка по зонам и контурам."""

    def _req(self, **kw):
        base = {"op": "cladding", "tile": {"w": 600, "h": 600},
                "gap": {"v": 10, "h": 10}, "datum": 0.0, "mode": "edge"}
        base.update(kw)
        return base

    def test_zone_simple(self):
        # зона 3040×1210 без проёмов — 5×2 целых, метка зоны на вставках
        z = zone_dict(rect(0, 0, 3040, 1210), zid="Ф-1")
        res = fe.run(self._req(zones=[{"zone_id": "Ф-1", "zone": z}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"],
                         {"tiles": 10, "full": 10, "cut": 0, "zones": 1})
        self.assertTrue(all(t["zone"] == "Ф-1" for t in res["inserts"]))
        self.assertEqual(res["per_zone"][0]["zone_id"], "Ф-1")

    def test_zone_units_m(self):
        # зона в метрах масштабируется в мм
        z = zone_dict(rect(0, 0, 3.04, 1.21), zid="Ф-2", units="m")
        res = fe.run(self._req(zones=[{"zone_id": "Ф-2", "zone": z}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["tiles"], 10)

    def test_zone_with_window_openings_mode(self):
        # окно в зоне → в режиме «от проёмов» швы от границ окна (В2)
        z = zone_dict(rect(0, 0, 3000, 600),
                      [opening("W1", rect(200, 0, 600, 600)),
                       opening("W2", rect(2200, 0, 600, 600))],
                      zid="Ф-3")
        res = fe.run(self._req(mode="openings",
                               zones=[{"zone_id": "Ф-3", "zone": z}]))
        self.assertTrue(res["ok"])
        row = sorted((t["x"], t["w"]) for t in res["inserts"])
        self.assertEqual(row, [(0.0, 200.0), (800.0, 390.0),
                               (1200.0, 600.0), (1810.0, 390.0),
                               (2800.0, 200.0)])

    def test_zone_with_arc_skipped(self):
        # дуга в проёме — зона пропускается с внятной note
        z = zone_dict(rect(0, 0, 3040, 1210),
                      [opening("A", rect(500, 400, 600, 600),
                               bulges=[0.5, 0, 0, 0])],
                      zid="Ф-4")
        ok_z = zone_dict(rect(5000, 0, 3040, 1210), zid="Ф-5")
        res = fe.run(self._req(zones=[{"zone_id": "Ф-4", "zone": z},
                                      {"zone_id": "Ф-5", "zone": ok_z}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 1)
        self.assertTrue(any("Ф-4" in n and "дуг" in n for n in res["notes"]))

    def test_raw_contours_nesting(self):
        # голые полилинии: окно = контур в контуре, вложенность сама;
        # outer_id проброшен для метки ATCLAD на полилинии
        res = fe.run(self._req(contours=[
            {"id": "AAA", "pts": rect(0, 0, 3040, 1820)},
            {"id": "BBB", "pts": rect(1220, 610, 610, 600)}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 1)
        self.assertEqual(res["per_zone"][0]["outer_id"], "AAA")
        # окно сидит в модульной сетке (610×600 от [1220,610]) — вычет
        # ровно одного камня: 15 целых - 1 = 14 (C3b движка)
        self.assertEqual(res["summary"]["tiles"], 14)
        self.assertEqual(res["summary"]["cut"], 0)

    def test_zones_and_contours_together(self):
        z = zone_dict(rect(0, 0, 1220, 600), zid="Ф-6")
        res = fe.run(self._req(
            zones=[{"zone_id": "Ф-6", "zone": z}],
            contours=[{"id": "CCC", "pts": rect(5000, 0, 1220, 600)}]))
        self.assertTrue(res["ok"])
        self.assertEqual(res["summary"]["zones"], 2)
        self.assertEqual(res["summary"]["tiles"], 4)

    def test_min_cut_default_150(self):
        # В4: дефолт 150 живёт и через CLI-обёртку
        z = zone_dict(rect(0, 0, 700, 600), zid="Ф-7")
        res = fe.run(self._req(zones=[{"zone_id": "Ф-7", "zone": z}]))
        self.assertEqual(res["summary"]["tiles"], 1)
        self.assertTrue(any("min_cut" in n for n in res["notes"]))
        res = fe.run(self._req(zones=[{"zone_id": "Ф-7", "zone": z}],
                               min_cut=50))
        self.assertEqual(res["summary"]["tiles"], 2)

    def test_empty_request(self):
        res = fe.run({"op": "cladding", "tile": {"w": 600, "h": 600}})
        self.assertFalse(res["ok"])
        self.assertIn("нет пригодных", res["error"])

    def test_unknown_op_mentions_cladding(self):
        res = fe.run({"op": "wat"})
        self.assertFalse(res["ok"])
        self.assertIn("cladding", res["error"])


if __name__ == "__main__":
    unittest.main()
