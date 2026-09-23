#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Юниты facade_zones.py. Запуск: PYTHONUTF8=1 python3 -m unittest -v"""

import json
import math
import os
import unittest

import facade_zones as fz


def rect(x0, y0, w, h):
    return [[x0, y0], [x0 + w, y0], [x0 + w, y0 + h], [x0, y0 + h]]


def zone_dict(outer_pts, openings=(), bulges=None, units="mm", **kw):
    d = {"schema": fz.SCHEMA, "id": kw.pop("zid", "Z-01"),
         "units": units,
         "outer": {"pts": outer_pts}}
    if bulges is not None:
        d["outer"]["bulges"] = bulges
    d["openings"] = list(openings)
    d.update(kw)
    return d


def opening(oid, pts, kind="window", bulges=None):
    o = {"id": oid, "kind": kind, "poly": {"pts": pts}}
    if bulges is not None:
        o["poly"]["bulges"] = bulges
    return o


def codes(issues):
    return [i.code for i in issues]


class TestRectBasics(unittest.TestCase):
    def test_plain_rect_area_perimeter(self):
        z = fz.load_zone(zone_dict(rect(0, 0, 12000, 3000)))
        issues = fz.validate_zone(z)
        self.assertFalse(fz._has_errors(issues))
        rep = fz.zone_report(z, issues)
        self.assertAlmostEqual(rep["area_outer_m2"], 36.0, places=9)
        self.assertAlmostEqual(rep["area_net_m2"], 36.0, places=9)
        self.assertAlmostEqual(rep["perimeter_outer_m"], 30.0, places=9)
        self.assertEqual(rep["openings_count"], 0)

    def test_units_m(self):
        z = fz.load_zone(zone_dict(rect(0, 0, 12, 3), units="m"))
        rep = fz.zone_report(z)
        self.assertAlmostEqual(rep["area_outer_m2"], 36.0, places=9)
        self.assertAlmostEqual(rep["perimeter_outer_m"], 30.0, places=9)

    def test_cw_input_normalized(self):
        pts = rect(0, 0, 1000, 1000)[::-1]  # CW
        z = fz.load_zone(zone_dict(pts))
        self.assertGreater(z.outer.signed_area(), 0)
        rep = fz.zone_report(z)
        self.assertAlmostEqual(rep["area_outer_m2"], 1.0, places=9)

    def test_explicit_closing_point_dropped(self):
        pts = rect(0, 0, 1000, 1000) + [[0, 0]]
        z = fz.load_zone(zone_dict(pts))
        self.assertEqual(z.outer.n(), 4)
        self.assertAlmostEqual(abs(z.outer.signed_area()), 1e6, places=3)

    def test_autoclose_small_gap(self):
        pts = rect(0, 0, 1000, 1000) + [[0.3, 0.0]]  # хвост в 0.3 мм от начала
        z = fz.load_zone(zone_dict(pts))
        issues = fz.validate_zone(z)
        self.assertIn("W_AUTOCLOSED", codes(issues))
        self.assertFalse(fz._has_errors(issues))
        self.assertEqual(z.outer.n(), 4)

    def test_dup_points_cleaned(self):
        pts = [[0, 0], [0, 0], [1000, 0], [1000, 1000], [1000, 1000], [0, 1000]]
        z = fz.load_zone(zone_dict(pts))
        issues = fz.validate_zone(z)
        self.assertIn("W_DUP_POINTS", codes(issues))
        rep = fz.zone_report(z, issues)
        self.assertAlmostEqual(rep["area_outer_m2"], 1.0, places=9)


class TestOpenings(unittest.TestCase):
    def test_window_deduction(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("W-01", rect(1500, 900, 1500, 1500))]))
        rep = fz.zone_report(z)
        self.assertAlmostEqual(rep["openings_total_m2"], 2.25, places=9)
        self.assertAlmostEqual(rep["area_net_m2"], 33.75, places=9)
        self.assertAlmostEqual(rep["openings_perimeter_total_m"], 6.0, places=9)

    def test_door_touching_bottom_edge_valid(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("D-01", rect(5000, 0, 900, 2100), kind="door")]))
        issues = fz.validate_zone(z)
        self.assertFalse(fz._has_errors(issues), msg=str(issues))
        rep = fz.zone_report(z, issues)
        self.assertAlmostEqual(rep["area_net_m2"], 36.0 - 1.89, places=9)

    def test_opening_partially_outside(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("W-01", rect(11500, 900, 1500, 1500))]))
        self.assertIn("E_OPENING_OUTSIDE", codes(fz.validate_zone(z)))

    def test_opening_fully_outside(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("W-01", rect(20000, 900, 1500, 1500))]))
        self.assertIn("E_OPENING_OUTSIDE", codes(fz.validate_zone(z)))

    def test_openings_overlap(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("W-01", rect(1000, 900, 1500, 1500)),
             opening("W-02", rect(2000, 900, 1500, 1500))]))
        self.assertIn("E_OPENINGS_OVERLAP", codes(fz.validate_zone(z)))

    def test_openings_nested(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("W-01", rect(1000, 500, 2000, 2000)),
             opening("W-02", rect(1500, 1000, 500, 500))]))
        self.assertIn("E_OPENINGS_OVERLAP", codes(fz.validate_zone(z)))

    def test_openings_side_by_side_touch_ok(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("W-01", rect(1000, 900, 1500, 1500)),
             opening("W-02", rect(2500, 900, 1500, 1500))]))
        issues = fz.validate_zone(z)
        self.assertFalse(fz._has_errors(issues), msg=str(issues))

    def test_tiny_opening_warns(self):
        z = fz.load_zone(zone_dict(
            rect(0, 0, 12000, 3000),
            [opening("X", rect(100, 100, 50, 50))]))
        issues = fz.validate_zone(z)
        self.assertIn("W_TINY_OPENING", codes(issues))
        self.assertFalse(fz._has_errors(issues))

    def test_opening_self_intersect(self):
        bow = [[1000, 1000], [2000, 2000], [2000, 1000], [1000, 2000]]
        z = fz.load_zone(zone_dict(rect(0, 0, 12000, 3000),
                                   [opening("W-01", bow)]))
        self.assertIn("E_SELF_INTERSECT", codes(fz.validate_zone(z)))


class TestOuterDefects(unittest.TestCase):
    def test_self_intersect_bowtie(self):
        bow = [[0, 0], [1000, 1000], [1000, 0], [0, 1000]]
        z = fz.load_zone(zone_dict(bow))
        self.assertIn("E_SELF_INTERSECT", codes(fz.validate_zone(z)))

    def test_zero_area(self):
        z = fz.load_zone(zone_dict([[0, 0], [1000, 0], [2000, 0]]))
        self.assertIn("E_ZERO_AREA", codes(fz.validate_zone(z)))

    def test_report_raises_on_errors(self):
        bow = [[0, 0], [1000, 1000], [1000, 0], [0, 1000]]
        z = fz.load_zone(zone_dict(bow))
        with self.assertRaises(ValueError):
            fz.zone_report(z)


class TestBulge(unittest.TestCase):
    def test_arc_end_matches_p2(self):
        for b in (1.0, -1.0, 0.4142, -0.25, 2.0):
            p1, p2 = (0.0, 0.0), (2000.0, 500.0)
            cx, cy, R, a1, delta = fz._arc_params(p1, p2, b)
            ex = cx + R * math.cos(a1 + delta)
            ey = cy + R * math.sin(a1 + delta)
            self.assertAlmostEqual(ex, p2[0], places=6, msg="b=%s" % b)
            self.assertAlmostEqual(ey, p2[1], places=6, msg="b=%s" % b)

    def test_arc_points_on_radius(self):
        p1, p2, b = (0.0, 0.0), (2000.0, 0.0), 1.0
        cx, cy, R, _, _ = fz._arc_params(p1, p2, b)
        for p in fz._arc_points(p1, p2, b):
            self.assertAlmostEqual(fz._dist(p, (cx, cy)), R, places=6)

    def test_semicircle_area_analytic_vs_polygonized(self):
        # «арка»: прямоугольник + полукруг на верхнем сегменте
        pts = [[0, 0], [2000, 0], [2000, 1000], [0, 1000]]
        z = fz.load_zone(zone_dict(pts, bulges=[0, 0, 1.0, 0]))
        a_analytic = abs(z.outer.signed_area())
        poly = z.outer.polygonized()
        sh = 0.0
        n = len(poly)
        for i in range(n):
            x1, y1 = poly[i]
            x2, y2 = poly[(i + 1) % n]
            sh += x1 * y2 - x2 * y1
        a_poly = abs(sh) / 2.0
        self.assertLess(abs(a_analytic - a_poly) / a_analytic, 1e-3)

    def test_arched_zone_area_perimeter(self):
        # прямоугольник 2×1 м + полукруг R=1 м поверх: S = 2 + pi/2, P = 4 + pi
        pts = [[0, 0], [2000, 0], [2000, 1000], [0, 1000]]
        z = fz.load_zone(zone_dict(pts, bulges=[0, 0, 1.0, 0]))
        issues = fz.validate_zone(z)
        self.assertFalse(fz._has_errors(issues), msg=str(issues))
        rep = fz.zone_report(z, issues)
        self.assertAlmostEqual(rep["area_outer_m2"], 2.0 + math.pi / 2.0,
                               places=4)
        self.assertAlmostEqual(rep["perimeter_outer_m"], 4.0 + math.pi,
                               places=4)

    def test_bulge_sign_consistency_negative(self):
        # та же арка отрицательным bulge на обратном направлении сегмента
        pts = [[0, 0], [2000, 0], [2000, 1000], [0, 1000]]
        z_pos = fz.load_zone(zone_dict(pts, bulges=[0, 0, 1.0, 0]))
        # в реверснутом контуре арка — на сегменте (0,1000)->(2000,1000):
        # направление +x, выпуклость вверх = слева = отрицательный bulge
        z_neg = fz.load_zone(zone_dict(pts[::-1], bulges=[-1.0, 0, 0, 0]))
        # (после нормализации CW->CCW контуры эквивалентны)
        self.assertAlmostEqual(abs(z_pos.outer.signed_area()),
                               abs(z_neg.outer.signed_area()), places=3)
        self.assertAlmostEqual(z_pos.outer.perimeter(),
                               z_neg.outer.perimeter(), places=6)

    def test_reverse_preserves_geometry(self):
        pts = [[0, 0], [2000, 0], [2000, 1000], [0, 1000]]
        z = fz.load_zone(zone_dict(pts, bulges=[0, 0, 1.0, 0]))
        a0 = z.outer.signed_area()
        p0 = z.outer.perimeter()
        z.outer.reverse()
        self.assertAlmostEqual(z.outer.signed_area(), -a0, places=6)
        self.assertAlmostEqual(z.outer.perimeter(), p0, places=9)
        # полигонизация после reverse остаётся на той же дуге
        z.outer.reverse()
        self.assertAlmostEqual(z.outer.signed_area(), a0, places=6)

    def test_arched_window_deduction(self):
        # окно с полукруглым верхом внутри большой зоны
        zpts = rect(0, 0, 12000, 4000)
        wpts = [[1000, 900], [2000, 900], [2000, 1900], [1000, 1900]]
        z = fz.load_zone(zone_dict(
            zpts, [opening("W-arc", wpts, bulges=[0, 0, 1.0, 0])]))
        issues = fz.validate_zone(z)
        self.assertFalse(fz._has_errors(issues), msg=str(issues))
        rep = fz.zone_report(z, issues)
        want = 1.0 + math.pi * 0.25 / 2.0  # 1 м² + полукруг R=0.5
        self.assertAlmostEqual(rep["openings_total_m2"], want, places=4)


class TestFormatErrors(unittest.TestCase):
    def test_bad_schema(self):
        d = zone_dict(rect(0, 0, 1000, 1000))
        d["schema"] = "nope/9"
        with self.assertRaises(fz.ZoneFormatError):
            fz.load_zone(d)

    def test_no_id(self):
        d = zone_dict(rect(0, 0, 1000, 1000))
        d["id"] = ""
        with self.assertRaises(fz.ZoneFormatError):
            fz.load_zone(d)

    def test_dup_opening_id(self):
        d = zone_dict(rect(0, 0, 5000, 3000),
                      [opening("W", rect(100, 100, 500, 500)),
                       opening("W", rect(1000, 100, 500, 500))])
        with self.assertRaises(fz.ZoneFormatError):
            fz.load_zone(d)

    def test_bad_bulges_len(self):
        d = zone_dict(rect(0, 0, 1000, 1000), bulges=[0, 0])
        with self.assertRaises(fz.ZoneFormatError):
            fz.load_zone(d)

    def test_too_few_points(self):
        with self.assertRaises(fz.ZoneFormatError):
            fz.load_zone(zone_dict([[0, 0], [1000, 0]]))

    def test_bad_system(self):
        d = zone_dict(rect(0, 0, 1000, 1000), system="ventfas")
        with self.assertRaises(fz.ZoneFormatError):
            fz.load_zone(d)

    def test_bad_units(self):
        d = zone_dict(rect(0, 0, 1000, 1000), units="cm")
        with self.assertRaises(fz.ZoneFormatError):
            fz.load_zone(d)


class TestListAndExample(unittest.TestCase):
    def test_load_zones_list(self):
        d1 = zone_dict(rect(0, 0, 1000, 1000), zid="A")
        d2 = zone_dict(rect(0, 0, 2000, 1000), zid="B")
        zs = fz.load_zones([d1, d2])
        self.assertEqual([z.id for z in zs], ["A", "B"])

    def test_example_file(self):
        path = os.path.join(os.path.dirname(__file__), "..", "examples",
                            "zone_example.json")
        zs = fz.load_zones(path)
        self.assertEqual(len(zs), 1)
        z = zs[0]
        issues = fz.validate_zone(z)
        self.assertFalse(fz._has_errors(issues), msg=str(issues))
        rep = fz.zone_report(z, issues)
        self.assertAlmostEqual(rep["area_outer_m2"], 36.0, places=9)
        self.assertAlmostEqual(rep["openings_total_m2"], 6.39, places=9)
        self.assertAlmostEqual(rep["area_net_m2"], 29.61, places=9)
        self.assertAlmostEqual(rep["openings_perimeter_total_m"], 18.0,
                               places=9)
        txt = fz.report_text(rep)
        self.assertIn("НЕТТО: 29.610", txt)

    def test_report_json_roundtrip(self):
        z = fz.load_zone(zone_dict(rect(0, 0, 12000, 3000),
                                   [opening("W", rect(1000, 900, 1500, 1500))]))
        rep = fz.zone_report(z)
        s = json.dumps(rep, ensure_ascii=False)
        back = json.loads(s)
        self.assertEqual(back["schema"], fz.REPORT_SCHEMA)
        self.assertAlmostEqual(back["area_net_m2"], 33.75, places=9)



class TestReview2309(unittest.TestCase):
    """23.09 (ревью): фантом-зона, дуга на 45°, зона в метрах."""

    def test_opening_crossing_wall_edge_is_error_not_zone(self):
        cs = [{"id": "W", "pts": rect(0, 0, 8000, 5000)},
              {"id": "O", "pts": rect(-50, 1000, 1500, 1500)}]
        zds, issues = fz.build_zones_from_contours(cs)
        self.assertEqual(len(zds), 1, "окно с нахлёстом не должно стать отдельной зоной")
        self.assertIn("E_CONTOUR_CROSSES", codes(issues))
        self.assertEqual(zds[0]["meta"]["outer_contour_id"], "W")

    def test_adjacent_walls_touching_are_fine(self):
        cs = [{"id": "A", "pts": rect(0, 0, 4000, 3000)}, {"id": "B", "pts": rect(4000, 0, 3000, 3000)}]
        zds, issues = fz.build_zones_from_contours(cs)
        self.assertEqual(len(zds), 2)
        self.assertNotIn("E_CONTOUR_CROSSES", codes(issues))

    def _round_window_edges(self, dx, dy):
        r = 500.0
        cx, cy = 3000.0 + dx, 1500.0 + dy
        pts = [[cx - r, cy], [cx, cy - r], [cx + r, cy], [cx, cy + r]]
        b = math.tan(math.pi / 8)
        z = fz.load_zone(zone_dict(rect(dx, dy, 6000, 3000),
                                   [opening("O", pts, bulges=[b, b, b, b])]))
        rep = fz.zone_report(z)
        return rep["sills_total_m"], rep["jambs_total_m"]

    def test_round_window_edges_do_not_depend_on_position(self):
        a = self._round_window_edges(0.0, 0.0)
        for dx, dy in ((612345.5, 24070.3), (13.7, 0.1), (-4096.25, 777.77)):
            b = self._round_window_edges(dx, dy)
            self.assertAlmostEqual(a[0], b[0], places=6)
            self.assertAlmostEqual(a[1], b[1], places=6)
        # четверть окружности снизу — отлив (по 45° с каждой стороны)
        self.assertAlmostEqual(a[0], math.pi * 0.5 / 2, delta=0.003)

    def test_zone_in_metres_equals_millimetres(self):
        small = rect(1000, 1000, 400, 400)            # проём 0.4 × 0.4 м
        zmm = fz.load_zone(zone_dict(rect(0, 0, 6000, 3000), [opening("O", small)]))
        zm = fz.load_zone(zone_dict([[x / 1000.0, y / 1000.0] for x, y in rect(0, 0, 6000, 3000)],
                                    [opening("O", [[x / 1000.0, y / 1000.0] for x, y in small])],
                                    units="m"))
        a, b = fz.zone_report(zmm), fz.zone_report(zm)
        for key in ("area_net_m2", "openings_total_m2", "sills_total_m", "jambs_total_m"):
            self.assertAlmostEqual(a[key], b[key], places=9, msg=key)



class TestIndependentReview2409(unittest.TestCase):
    """24.09: находки независимой рецензии (совпадающие/наложенные контуры,
    проём в наружной выемке, частичная кромка на границе, NaN, далёкие
    координаты)."""

    def zones(self, cs):
        return fz.build_zones_from_contours(cs)

    def net(self, zd):
        return fz.zone_report(fz.load_zone(zd))["area_net_m2"]

    def test_duplicate_openings_counted_once(self):
        cs = [{"id": "W", "pts": rect(0, 0, 5000, 5000)}] + \
             [{"id": "O%d" % k, "pts": rect(1000, 1000, 3000, 3000)} for k in range(3)]
        zds, issues = self.zones(cs)
        self.assertEqual(len(zds), 1)
        self.assertAlmostEqual(self.net(zds[0]), 16.0, places=9)
        self.assertEqual(codes(issues).count("W_DUPLICATE_CONTOUR"), 2)

    def test_duplicate_outer_counted_once(self):
        zds, issues = self.zones([{"id": "A", "pts": rect(0, 0, 5000, 5000)},
                                  {"id": "B", "pts": list(reversed(rect(0, 0, 5000, 5000)))}])
        self.assertEqual(len(zds), 1)
        self.assertIn("W_DUPLICATE_CONTOUR", codes(issues))

    def test_aligned_overlap_of_outers_is_error(self):
        zds, issues = self.zones([{"id": "A", "pts": rect(0, 0, 5000, 5000)},
                                  {"id": "B", "pts": rect(4000, 0, 5000, 5000)}])
        self.assertEqual(len(zds), 1)
        self.assertIn("E_CONTOURS_OVERLAP", codes(issues))

    def test_adjacent_zones_sharing_edge_are_fine(self):
        zds, issues = self.zones([{"id": "A", "pts": rect(0, 0, 5000, 5000)},
                                  {"id": "B", "pts": rect(5000, 0, 5000, 5000)},
                                  {"id": "C", "pts": rect(0, 5000, 10000, 3000)}])
        self.assertEqual(len(zds), 3)
        self.assertEqual([c for c in codes(issues) if c.startswith("E_")], [])

    def test_opening_in_external_notch_is_not_subtracted(self):
        U = [[0, 0], [6000, 0], [6000, 6000], [4000, 6000], [4000, 2000], [2000, 2000],
             [2000, 6000], [0, 6000]]
        zds, issues = self.zones([{"id": "W", "pts": U}, {"id": "O", "pts": rect(2000, 2000, 2000, 3000)}])
        by = {z["meta"]["outer_contour_id"]: z for z in zds}
        self.assertAlmostEqual(self.net(by["W"]), 28.0, places=9)
        self.assertEqual(by["W"]["openings"], [])

    def test_real_opening_in_u_wall_still_subtracted(self):
        U = [[0, 0], [6000, 0], [6000, 6000], [4000, 6000], [4000, 2000], [2000, 2000],
             [2000, 6000], [0, 6000]]
        zds, issues = self.zones([{"id": "W", "pts": U}, {"id": "O", "pts": rect(500, 1000, 1000, 1500)}])
        self.assertEqual(len(zds), 1)
        self.assertAlmostEqual(self.net(zds[0]), 26.5, places=9)

    def test_overlapping_openings_in_zone_json_is_error(self):
        z = fz.load_zone(zone_dict(rect(0, 0, 5000, 5000),
                                   [opening("O1", rect(1000, 1000, 2000, 2000)),
                                    opening("O2", rect(2000, 1000, 2000, 2000))]))
        self.assertIn("E_OPENINGS_OVERLAP", codes(fz.validate_zone(z)))

    def test_partial_boundary_edge_split(self):
        L = [[0, 0], [9000, 0], [9000, 6000], [6000, 6000], [6000, 3000], [0, 3000]]
        zds, _ = self.zones([{"id": "W", "pts": L}, {"id": "O", "pts": rect(6000, 2000, 1000, 2000)}])
        rep = fz.zone_report(fz.load_zone(zds[0]))
        self.assertAlmostEqual(rep["jambs_total_m"], 4.0, places=6)

    def test_nan_rejected(self):
        zds, issues = self.zones([{"id": "W", "pts": [[0, 0], [5000, 0], [5000, float("nan")], [0, 5000]]}])
        self.assertEqual(zds, [])
        self.assertIn("E_BAD_CONTOUR", codes(issues))
        with self.assertRaises(fz.ZoneFormatError):
            fz.load_zone(zone_dict([[0, 0], [5, 0], [5, float("inf")], [0, 5]]))

    def test_far_coordinates_keep_precision(self):
        for off in (0.0, 1e9, 1e10, 1e11):
            zds, _ = self.zones([{"id": "W", "pts": rect(off, off, 5000, 5000)},
                                 {"id": "O", "pts": rect(off + 1000, off + 1000, 1234, 1567)}])
            self.assertEqual(len(zds), 1, off)
            self.assertAlmostEqual(self.net(zds[0]), 25.0 - 1.234 * 1.567, places=5, msg=off)

    def test_label_anchor_units_consistent(self):
        b = 0.37
        zmm = fz.load_zone(zone_dict([[0, 0], [6000, 0], [6000, 3000], [0, 3000]],
                                     bulges=[0, 0, b, 0]))
        zm = fz.load_zone(zone_dict([[0, 0], [6, 0], [6, 3], [0, 3]], bulges=[0, 0, b, 0], units="m"))
        la = fz.label_anchor(zmm.outer.polygonized(fz.CHORD_TOL), fz.GEO_TOL)
        lb = fz.label_anchor(zm.outer.polygonized(fz.CHORD_TOL / 1000.0), fz.GEO_TOL / 1000.0)
        self.assertAlmostEqual(la[0], lb[0] * 1000.0, delta=1.0)
        self.assertAlmostEqual(la[1], lb[1] * 1000.0, delta=1.0)


if __name__ == "__main__":
    unittest.main()
