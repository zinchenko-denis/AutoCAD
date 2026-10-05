"""Торцы у окна: исходная ось до проёма, выступ 50/50 — только боковой стойке."""
import copy
import unittest
from collections import Counter

from frame_engine import op_frame
from frame_plan import frame_plan


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def request(sub="vertical", calculate=False):
    result = dict(op="frame", system=dict(name="Вектор-1", mid_rail_over=None),
                  sub_type=sub, parts="frame", cladding="porcelain", corners_x=[],
                  contours=[dict(id="wall", pts=rect(0, 0, 3000, 5000)),
                            dict(id="window", pts=rect(1000, 2000, 2000, 3500))],
                  joints_x=[996, 1500, 2004])
    if calculate:
        result["calc"] = dict(wind_region="II", terrain="B", height=30, q_clad=25,
                              offset=230, na_max=3000, gamma_clad=1.1)
    return result


class WindowEndTests(unittest.TestCase):
    def rail_spans(self, result, x):
        return sorted((r["y0"], r["y1"]) for r in result["rails"]
                      if abs(r["x"] - x) < 1e-4)

    def check_window(self, result):
        self.assertTrue(result["ok"], result.get("error"))
        # Отступ 100 и выступ 50/50 относятся к отдельной боковой стойке.
        for x in (900, 2100):
            self.assertEqual(self.rail_spans(result, x), [(1950, 3550)])
        # Крайние оси рустов должны доходить до той же грани, что и средняя.
        for x in (996, 1500, 2004):
            self.assertEqual(self.rail_spans(result, x), [(0, 2000), (3500, 5000)])

    def test_manual_both_jambs_reach_sill_and_head(self):
        for exact in (False, True):
            result = op_frame(dict(request(), exact_step=exact))
            self.check_window(result)
            for x in (996, 2004):
                supports = [b["y"] for b in result["brackets"] if b["x"] == x]
                self.assertIn(1700, supports)  # 300 от подоконника, а не 350.
                self.assertIn(3800, supports)  # 300 от верха окна, а не 350.

    def test_calculated_layout_keeps_same_ends(self):
        result = op_frame(request(calculate=True))
        self.check_window(result)
        self.assertTrue(result["calc_report"]["static_model"]["members"])
        for x in (996, 2004):
            supports = [b["y"] for b in result["brackets"] if b["x"] == x]
            self.assertIn(1700, supports)
            self.assertIn(3800, supports)

    def test_orthogonal_uses_same_window_boundary(self):
        result = op_frame(request("ortho"))
        self.check_window(result)
        self.assertTrue(all(r["kind"] == "Z-профиль" for r in result["rails"]
                            if r["x"] in (900, 2100)))

    def test_translated_fractional_geometry_preserves_ends(self):
        for sub in ("vertical", "ortho"):
            req = request(sub)
            dx, dy = 12800.125, -4620.375
            req["joints_x"] = [x + dx for x in req["joints_x"]]
            for contour in req["contours"]:
                contour["pts"] = [[x + dx, y + dy] for x, y in contour["pts"]]
            result = op_frame(req)
            self.assertTrue(result["ok"], result.get("error"))
            for x in (996, 1500, 2004):
                self.assertEqual(self.rail_spans(result, x + dx),
                                 [(dy, 2000 + dy), (3500 + dy, 5000 + dy)])

    def test_fractional_sill_reproduces_video_class(self):
        # Синтетический аналог, не восстановленный DWG. В видео измерен
        # торец 2101.1599; положение окна здесь задано явно на 50 выше.
        req = request()
        req["contours"][1]["pts"] = rect(1000, 2151.1599, 2000, 3500)
        result = op_frame(req)
        self.assertTrue(result["ok"], result.get("error"))
        for x in (996, 1500, 2004):
            self.assertEqual(self.rail_spans(result, x)[0], (0, 2151.1599))
        for x in (900, 2100):
            self.assertEqual(self.rail_spans(result, x), [(2101.1599, 3550)])

    def test_head_near_wall_top_does_not_erase_regular_piece(self):
        for sub in ("vertical", "ortho"):
            req = request(sub)
            req["contours"][0]["pts"] = rect(0, 0, 3000, 3510)
            result = op_frame(req)
            self.assertTrue(result["ok"], result.get("error"))
            for x in (996, 1500, 2004):
                self.assertEqual(self.rail_spans(result, x), [(0, 2000), (3500, 3510)])
            for x in (900, 2100):
                self.assertEqual(self.rail_spans(result, x), [(1950, 3510)])

    def test_neighbouring_openings_still_clip_shifted_rails(self):
        req = request()
        req["contours"].append(dict(id="neighbour", pts=rect(400, 500, 920, 2300)))
        result = op_frame(req)
        self.assertTrue(result["ok"], result.get("error"))
        for rail in result["rails"]:
            for contour in req["contours"][1:]:
                xs, ys = zip(*contour["pts"])
                if min(xs) < rail["x"] < max(xs):
                    self.assertTrue(rail["y1"] <= min(ys) or rail["y0"] >= max(ys))
        for x in {r["x"] for r in result["rails"]}:
            spans = self.rail_spans(result, x)
            self.assertTrue(all(a[1] <= b[0] for a, b in zip(spans, spans[1:])))
        # Слева ось 996 попадает также в полосу смещения соседнего окна;
        # её нижний торец доходит до его низа 500. Справа сосед не влияет.
        self.assertEqual(self.rail_spans(result, 996), [(0, 500), (3500, 5000)])
        self.assertEqual(self.rail_spans(result, 2004), [(0, 2000), (3500, 5000)])

    def test_clamps_only_roundtrip_keeps_window_roles(self):
        req = request()
        req["parts"] = "all"
        req["rows_y"] = [600, 1200, 1800, 2400, 3000, 3600, 4200, 4800]
        full = op_frame(req)
        self.check_window(full)
        # frame_plan takes already grouped contours; the command reaches this
        # same path after loading its stored rail geometry.
        grouped = copy.deepcopy(req)
        grouped["contours"] = [dict(outer=req["contours"][0]["pts"],
                                    holes=[req["contours"][1]["pts"]])]
        grouped.update(parts="clamps", rails_fixed=full["rails"])
        only = frame_plan(grouped)
        self.assertTrue(only["ok"], only.get("error"))
        key = lambda items: Counter((c["x"], c["y"], c["kind"], c.get("orient")) for c in items)
        self.assertEqual(key(full["clamps"]), key(only["clamps"]))


if __name__ == "__main__":
    unittest.main()
