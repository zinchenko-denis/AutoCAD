#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""facade_zones.py — движок зон фасадов (блок A, общий для НВФ и СФТК).

Формат входа: docs/ZONE_FORMAT.md (schema facade_zone/1).
ТОЛЬКО stdlib (движок замораживается PyInstaller'ом, как atspec/abg).

API:
    zone = load_zone(dict_or_path)        # парс + нормализация (бросает ZoneFormatError)
    issues = validate_zone(zone)          # [Issue], errors + warnings
    report = zone_report(zone)            # dict facade_zone_report/1 (бросает при errors)

CLI:
    python3 facade_zones.py validate file.json
    python3 facade_zones.py report file.json [-o out.json]

Конвенция bulge: как LWPOLYLINE (b = tan(theta/4), 0 = прямая). Знаковая
сторона выпуклости закреплена самосогласованной парой аналитика<->полигонизация
(юниты); абсолютную сторону сверить по первому живому DXF с дугой — см.
ZONE_FORMAT.md.
"""

import json
import math
import sys

SCHEMA = "facade_zone/1"
REPORT_SCHEMA = "facade_zone_report/1"

EPS = 1e-9          # чистая математика
DUP_TOL = 1e-6      # мм: строгий дубль вершин
GEO_TOL = 0.5       # мм: замыкание, касания, глубина «настоящего» пересечения
CHORD_TOL = 0.5     # мм: стрелка при полигонизации дуг
MIN_ARC_STEPS = 8   # минимум сегментов на дугу
TINY_OPENING_M2 = 0.01  # м2: проём мельче — предупреждение (мусор?)

_UNIT_TO_MM = {"mm": 1.0, "m": 1000.0}


class ZoneFormatError(Exception):
    """Файл/словарь не соответствует facade_zone/1 структурно."""


class Issue(object):
    __slots__ = ("code", "level", "where", "msg")

    def __init__(self, code, level, where, msg):
        self.code = code      # E_* / W_*
        self.level = level    # "error" | "warning"
        self.where = where    # "outer" | "opening:<id>" | "zone"
        self.msg = msg

    def as_dict(self):
        return {"code": self.code, "level": self.level,
                "where": self.where, "msg": self.msg}

    def __repr__(self):
        return "%s[%s@%s] %s" % (self.level, self.code, self.where, self.msg)


def _err(code, where, msg):
    return Issue(code, "error", where, msg)


def _warn(code, where, msg):
    return Issue(code, "warning", where, msg)


# ---------------------------------------------------------------- геометрия

def _dist(a, b):
    return math.hypot(b[0] - a[0], b[1] - a[1])


def _arc_params(p1, p2, b):
    """Параметры дуги сегмента (p1 -> p2, bulge=b).

    Возвращает (cx, cy, R, a1, delta) или None для прямой/вырожденной.
    delta = 4*atan(b) со знаком; конец дуги = центр + R*e^{i(a1+delta)}.
    """
    if abs(b) < EPS:
        return None
    c = _dist(p1, p2)
    if c < DUP_TOL:
        return None
    theta = 4.0 * math.atan(abs(b))
    R = c * (1.0 + b * b) / (4.0 * abs(b))
    s = abs(b) * c / 2.0                      # сагитта
    mx = (p1[0] + p2[0]) / 2.0
    my = (p1[1] + p2[1]) / 2.0
    ux = (p2[0] - p1[0]) / c
    uy = (p2[1] - p1[1]) / c
    nx, ny = -uy, ux                          # левая нормаль к хорде
    # конвенция: b>0 -> вершина дуги СПРАВА от хорды (CCW вокруг центра);
    # центр при theta<pi на противоположной (левой) стороне: h = R - s > 0,
    # при theta>pi — на той же: h = R - s < 0. Универсально h = sign(b)*(R-s).
    h = (1.0 if b > 0 else -1.0) * (R - s)    # смещение центра вдоль нормали
    cx, cy = mx + nx * h, my + ny * h
    a1 = math.atan2(p1[1] - cy, p1[0] - cx)
    delta = theta if b > 0 else -theta
    return (cx, cy, R, a1, delta)


def _arc_len(p1, p2, b):
    ap = _arc_params(p1, p2, b)
    if ap is None:
        return _dist(p1, p2)
    _, _, R, _, delta = ap
    return R * abs(delta)


def _arc_segment_area(p1, p2, b):
    """Знаковая добавка площади дуги над хордой (к shoelace)."""
    ap = _arc_params(p1, p2, b)
    if ap is None:
        return 0.0
    _, _, R, _, delta = ap
    t = abs(delta)
    return math.copysign(0.5 * R * R * (t - math.sin(t)), b)


def _arc_points(p1, p2, b, chord_tol=CHORD_TOL):
    """Промежуточные точки дуги (без p1 и p2)."""
    ap = _arc_params(p1, p2, b)
    if ap is None:
        return []
    cx, cy, R, a1, delta = ap
    if R > chord_tol:
        step = 2.0 * math.acos(max(-1.0, min(1.0, 1.0 - chord_tol / R)))
    else:
        step = abs(delta)
    n = max(MIN_ARC_STEPS, int(math.ceil(abs(delta) / max(step, 1e-6))))
    out = []
    for i in range(1, n):
        a = a1 + delta * (float(i) / n)
        out.append((cx + R * math.cos(a), cy + R * math.sin(a)))
    return out


class Poly(object):
    """Замкнутая полилиния: pts открытым списком, bulges[i] — сегмент i->i+1
    (последний — замыкающий). Нормализована: без подряд-дублей."""

    __slots__ = ("pts", "bulges", "warnings")

    def __init__(self, pts, bulges=None):
        self.warnings = []
        pts = [(float(p[0]), float(p[1])) for p in pts]
        if bulges is None:
            bulges = [0.0] * len(pts)
        bulges = [float(b) for b in bulges]
        if len(bulges) != len(pts):
            raise ZoneFormatError("len(bulges) != len(pts)")
        # явное замыкание последней-первой -> убрать хвост
        if len(pts) >= 2 and _dist(pts[0], pts[-1]) <= DUP_TOL:
            pts = pts[:-1]
            bulges = bulges[:-1]
        # подряд-дубли
        cp, cb, dropped = [], [], 0
        for p, b in zip(pts, bulges):
            if cp and _dist(cp[-1], p) <= DUP_TOL:
                dropped += 1
                if abs(b) > EPS and abs(cb[-1]) < EPS:
                    cb[-1] = b
                continue
            cp.append(p)
            cb.append(b)
        if dropped:
            self.warnings.append(dropped)
        self.pts = cp
        self.bulges = cb

    def n(self):
        return len(self.pts)

    def segments(self):
        n = len(self.pts)
        for i in range(n):
            yield self.pts[i], self.pts[(i + 1) % n], self.bulges[i]

    def gap(self):
        """Зазор «замыкания» исходных данных всегда 0 (замыкаем неявно)."""
        return 0.0

    def signed_area(self):
        # 24.09 (независимая рецензия): shoelace от ЛОКАЛЬНОГО начала — при
        # координатах ~1e10 мм прямое произведение теряло значащие цифры
        # (проём 1234×1567 на 1e11 мм становился нулевым)
        ox, oy = self.pts[0] if self.pts else (0.0, 0.0)
        a = 0.0
        for p1, p2, b in self.segments():
            a += ((p1[0] - ox) * (p2[1] - oy) - (p2[0] - ox) * (p1[1] - oy))
            if abs(b) > EPS:
                a += 2.0 * _arc_segment_area(p1, p2, b)
        return a / 2.0

    def perimeter(self):
        return sum(_arc_len(p1, p2, b) for p1, p2, b in self.segments())

    def reverse(self):
        n = len(self.pts)
        self.pts = self.pts[::-1]
        self.bulges = [-self.bulges[(n - 2 - j) % n] for j in range(n)]

    def polygonized(self, chord_tol=CHORD_TOL):
        out = []
        for p1, p2, b in self.segments():
            out.append(p1)
            if abs(b) > EPS:
                out.extend(_arc_points(p1, p2, b, chord_tol))
        return out

    def bbox(self):
        xs = [p[0] for p in self.polygonized()]
        ys = [p[1] for p in self.polygonized()]
        return (min(xs), min(ys), max(xs), max(ys))


# ------------------------------------------------- предикаты на полигонизации

def _cross(o, a, b):
    return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])


def _point_seg_dist(p, a, b):
    ax, ay = b[0] - a[0], b[1] - a[1]
    L2 = ax * ax + ay * ay
    if L2 < EPS:
        return _dist(p, a)
    t = ((p[0] - a[0]) * ax + (p[1] - a[1]) * ay) / L2
    t = max(0.0, min(1.0, t))
    return _dist(p, (a[0] + t * ax, a[1] + t * ay))


def _seg_relation(p1, p2, p3, p4, tol=GEO_TOL):
    """'proper' — пересечение глубже tol с обеих сторон; 'touch' — касание
    в пределах tol; None — не взаимодействуют."""
    L1 = _dist(p1, p2)
    L2 = _dist(p3, p4)
    if L1 < EPS or L2 < EPS:
        return None
    # знаковые расстояния концов до противоположной прямой
    d1 = _cross(p3, p4, p1) / L2
    d2 = _cross(p3, p4, p2) / L2
    d3 = _cross(p1, p2, p3) / L1
    d4 = _cross(p1, p2, p4) / L1
    if ((d1 > tol and d2 < -tol) or (d1 < -tol and d2 > tol)) and \
       ((d3 > tol and d4 < -tol) or (d3 < -tol and d4 > tol)):
        return "proper"
    for (p, a, b) in ((p1, p3, p4), (p2, p3, p4), (p3, p1, p2), (p4, p1, p2)):
        if _point_seg_dist(p, a, b) <= tol:
            return "touch"
    return None


def _pip(pt, poly_pts):
    """Точка в полигоне (ray casting); граница НЕ учитывается — проверять
    отдельно через _on_boundary."""
    x, y = pt
    inside = False
    n = len(poly_pts)
    for i in range(n):
        x1, y1 = poly_pts[i]
        x2, y2 = poly_pts[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            xin = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
            if xin > x:
                inside = not inside
    return inside


def _on_boundary(pt, poly_pts, tol=GEO_TOL):
    n = len(poly_pts)
    for i in range(n):
        if _point_seg_dist(pt, poly_pts[i], poly_pts[(i + 1) % n]) <= tol:
            return True
    return False


def _self_intersections(poly_pts, tol=GEO_TOL):
    """Пары несмежных сегментов: список ('proper'|'touch')."""
    n = len(poly_pts)
    out = []
    for i in range(n):
        a1 = poly_pts[i]
        a2 = poly_pts[(i + 1) % n]
        for j in range(i + 1, n):
            if j == i or (j + 1) % n == i or (i + 1) % n == j:
                continue  # смежные и сам с собой
            b1 = poly_pts[j]
            b2 = poly_pts[(j + 1) % n]
            r = _seg_relation(a1, a2, b1, b2, tol)
            if r:
                out.append(r)
    return out


def _probe_points(pts):
    """Вершины + середины рёбер: сэмплы для тестов включения (ловит
    «скользящее» перекрытие прямоугольников одинаковой высоты, где все
    пересечения рёбер — концевые касания)."""
    n = len(pts)
    out = list(pts)
    for i in range(n):
        a, b = pts[i], pts[(i + 1) % n]
        out.append(((a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0))
    return out


def _seg_params(a1, a2, poly_pts, tol=GEO_TOL):
    """Параметры t∈[0,1] на отрезке a1→a2, где его касается/пересекает
    граница poly (вершины контура на отрезке, пересечения рёбер, концы
    коллинеарных наложений) — для проверки ПОДОТРЕЗКОВ, а не одних вершин."""
    dx, dy = a2[0] - a1[0], a2[1] - a1[1]
    L2 = dx * dx + dy * dy
    ts = {0.0, 1.0}
    if L2 < EPS * EPS:
        return [0.0, 1.0]
    n = len(poly_pts)
    for j in range(n):
        b1, b2 = poly_pts[j], poly_pts[(j + 1) % n]
        for q in (b1, b2):
            t = ((q[0] - a1[0]) * dx + (q[1] - a1[1]) * dy) / L2
            if 0.0 < t < 1.0 and _point_seg_dist(q, a1, a2) <= tol:
                ts.add(t)
        ex, ey = b2[0] - b1[0], b2[1] - b1[1]
        den = dx * ey - dy * ex
        if abs(den) > EPS:
            t = ((b1[0] - a1[0]) * ey - (b1[1] - a1[1]) * ex) / den
            u = ((b1[0] - a1[0]) * dy - (b1[1] - a1[1]) * dx) / den
            if 0.0 < t < 1.0 and -EPS <= u <= 1.0 + EPS:
                ts.add(t)
    return sorted(ts)


def _signed_area_pts(pts):
    ox, oy = pts[0]
    a = 0.0
    for i in range(len(pts)):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % len(pts)]
        a += (x1 - ox) * (y2 - oy) - (x2 - ox) * (y1 - oy)
    return a / 2.0


def _interiors_overlap(apts, bpts, tol=GEO_TOL):
    """Пересекаются ли ВНУТРЕННОСТИ двух контуров (площадью, а не касанием).

    24.09 (независимая рецензия): совпадающие окна и «выровненный» нахлёст
    (общие верх/низ) _polys_relation отдаёт как «touch» — пробные точки
    лежат на границе. Проверка: каждый подотрезок ребра A (по точкам
    касания с B) сдвигаем внутрь A на 2·tol — точка строго внутри B
    значит общая площадь; и наоборот."""
    def probe(P, Q):
        sgn = 1.0 if _signed_area_pts(P) > 0 else -1.0
        n = len(P)
        for i in range(n):
            a1, a2 = P[i], P[(i + 1) % n]
            L = _dist(a1, a2)
            if L < 4.0 * tol:
                continue
            nx, ny = -(a2[1] - a1[1]) / L * sgn, (a2[0] - a1[0]) / L * sgn
            ts = _seg_params(a1, a2, Q, tol)
            for k in range(len(ts) - 1):
                if (ts[k + 1] - ts[k]) * L < 4.0 * tol:
                    continue
                tm = 0.5 * (ts[k] + ts[k + 1])
                mx, my = a1[0] + (a2[0] - a1[0]) * tm, a1[1] + (a2[1] - a1[1]) * tm
                q = (mx + nx * 2.0 * tol, my + ny * 2.0 * tol)
                if _pip(q, P) and _pip(q, Q) and not _on_boundary(q, Q, 0.5 * tol):
                    return True
        return False
    return probe(apts, bpts) or probe(bpts, apts)


def _same_contour(apts, bpts, tol=GEO_TOL):
    """Тот же контур (дубль обводки): те же вершины с точностью tol, в любом
    порядке обхода и с любой начальной вершины."""
    if len(apts) != len(bpts) or not apts:
        return False
    n = len(apts)
    for rev in (False, True):
        B = bpts[::-1] if rev else bpts
        for sh in range(n):
            if all(_dist(apts[i], B[(i + sh) % n]) <= tol for i in range(n)):
                return True
    return False


def _polys_relation(apts, bpts, tol=GEO_TOL):
    """Отношение контуров A и B: 'cross' | 'a_in_b' | 'b_in_a' | 'touch' | 'apart'."""
    touch = False
    for i in range(len(apts)):
        a1, a2 = apts[i], apts[(i + 1) % len(apts)]
        for j in range(len(bpts)):
            r = _seg_relation(a1, a2, bpts[j], bpts[(j + 1) % len(bpts)], tol)
            if r == "proper":
                return "cross"
            if r == "touch":
                touch = True
    a_in = any(_pip(p, bpts) and not _on_boundary(p, bpts, tol)
               for p in _probe_points(apts))
    b_in = any(_pip(p, apts) and not _on_boundary(p, apts, tol)
               for p in _probe_points(bpts))
    if a_in:
        return "a_in_b"
    if b_in:
        return "b_in_a"
    return "touch" if touch else "apart"


def _contains(outer_pts, inner_pts, tol=GEO_TOL):
    """inner целиком внутри outer (граница — можно): без proper-пересечений,
    все вершины И середины всех подотрезков рёбер (разбитых точками касания
    с outer) внутри или на границе.

    24.09 (независимая рецензия): проверялись только вершины — проём в
    наружной ВЫЕМКЕ П-стены с вершинами на её стенках принимался «внутри»,
    и из стены вычитались 6 м² пустоты (28 → 22 м²)."""
    for i in range(len(inner_pts)):
        a1 = inner_pts[i]
        a2 = inner_pts[(i + 1) % len(inner_pts)]
        for j in range(len(outer_pts)):
            if _seg_relation(a1, a2, outer_pts[j],
                             outer_pts[(j + 1) % len(outer_pts)], tol) == "proper":
                return False
    for p in inner_pts:
        if not (_pip(p, outer_pts) or _on_boundary(p, outer_pts, tol)):
            return False
    for i in range(len(inner_pts)):
        a1 = inner_pts[i]
        a2 = inner_pts[(i + 1) % len(inner_pts)]
        ts = _seg_params(a1, a2, outer_pts, tol)
        for k in range(len(ts) - 1):
            tm = 0.5 * (ts[k] + ts[k + 1])
            m = (a1[0] + (a2[0] - a1[0]) * tm, a1[1] + (a2[1] - a1[1]) * tm)
            if not (_pip(m, outer_pts) or _on_boundary(m, outer_pts, tol)):
                return False
    return True


# ------------------------------------------------------------------- модель

class Opening(object):
    __slots__ = ("id", "kind", "poly")

    def __init__(self, oid, kind, poly):
        self.id = oid
        self.kind = kind
        self.poly = poly


class Zone(object):
    __slots__ = ("id", "name", "facade", "floors", "base", "system",
                 "units", "outer", "openings", "source", "meta",
                 "norm_warnings")

    def to_mm(self):
        return _UNIT_TO_MM[self.units]


def _parse_poly(obj, where, warnings_sink=None, k=1.0):
    if not isinstance(obj, dict) or "pts" not in obj:
        raise ZoneFormatError("%s: ожидается объект {pts: [[x,y],...]}" % where)
    pts = obj["pts"]
    if not isinstance(pts, list) or len(pts) < 3:
        raise ZoneFormatError("%s: pts — минимум 3 вершины" % where)
    for p in pts:
        if (not isinstance(p, (list, tuple)) or len(p) != 2 or
                not all(isinstance(v, (int, float)) and not isinstance(v, bool)
                        for v in p)):
            raise ZoneFormatError("%s: вершина не [x,y]" % where)
        # 24.09 (рецензия): NaN/бесконечность проходили и давали NaN-площадь
        if not all(math.isfinite(v) for v in p):
            raise ZoneFormatError("%s: вершина с нечисловой координатой (NaN/∞)" % where)
    bulges = obj.get("bulges")
    if bulges is not None:
        if not isinstance(bulges, list) or len(bulges) != len(pts) or \
                not all(isinstance(v, (int, float)) and math.isfinite(v) for v in bulges):
            raise ZoneFormatError("%s: bulges — конечные числа, длина == len(pts)" % where)
    # хвост с микрозазором к первой точке (недоведённая обводка ≤ GEO_TOL):
    # сливаем с первой вершиной, чтобы не плодить микросегмент замыкания
    p0, pl = pts[0], pts[-1]
    # 23.09 (ревью): зазор хвоста — в мм (k — единицы зоны → мм); раньше
    # сравнивался с 0.5 в единицах зоны, у зоны в МЕТРАХ это 0.5 м — проём
    # со стороной ≤0.5 м терял вершину
    d = math.hypot(pl[0] - p0[0], pl[1] - p0[1]) * k
    if DUP_TOL < d <= GEO_TOL:
        pts = pts[:-1]
        if bulges is not None:
            bulges = bulges[:-1]
        if warnings_sink is not None:
            warnings_sink.append(_warn(
                "W_AUTOCLOSED", where,
                "хвост обводки в %.3f мм от начала — замкнуто автоматически" % d))
    return Poly(pts, bulges)


def load_zone(src):
    """src: путь к JSON, dict или уже-разобранный объект зоны."""
    if isinstance(src, str):
        with open(src, "r", encoding="utf-8") as f:
            data = json.load(f)
    else:
        data = src
    if isinstance(data, list):
        raise ZoneFormatError("файл несёт список зон — используйте load_zones()")
    if not isinstance(data, dict):
        raise ZoneFormatError("ожидается JSON-объект зоны")
    if data.get("schema") != SCHEMA:
        raise ZoneFormatError("schema != %s" % SCHEMA)
    if not data.get("id"):
        raise ZoneFormatError("нет id зоны")
    units = data.get("units", "mm")
    if units not in _UNIT_TO_MM:
        raise ZoneFormatError("units: только mm|m")

    z = Zone.__new__(Zone)
    z.id = str(data["id"])
    z.name = data.get("name", "")
    z.facade = data.get("facade", "")
    z.floors = str(data.get("floors", ""))
    z.base = data.get("base", "")
    z.system = data.get("system")
    if z.system not in (None, "нвф", "сфтк"):
        raise ZoneFormatError("system: нвф|сфтк|null")
    z.units = units
    z.source = data.get("source", {})
    z.meta = data.get("meta", {})
    z.norm_warnings = []

    ku = _UNIT_TO_MM[units]
    z.outer = _parse_poly(data.get("outer"), "outer", z.norm_warnings, ku)
    if z.outer.warnings:
        z.norm_warnings.append(
            _warn("W_DUP_POINTS", "outer",
                  "удалены подряд-дубли вершин: %d" % z.outer.warnings[0]))

    z.openings = []
    seen = set()
    for i, o in enumerate(data.get("openings", []) or []):
        if not isinstance(o, dict) or not o.get("id") or "poly" not in o:
            raise ZoneFormatError("openings[%d]: нужны id и poly" % i)
        oid = str(o["id"])
        if oid in seen:
            raise ZoneFormatError("openings: дубль id %s" % oid)
        seen.add(oid)
        where = "opening:%s" % oid
        poly = _parse_poly(o["poly"], where, z.norm_warnings, ku)
        if poly.warnings:
            z.norm_warnings.append(
                _warn("W_DUP_POINTS", where,
                      "удалены подряд-дубли вершин: %d" % poly.warnings[0]))
        z.openings.append(Opening(oid, str(o.get("kind", "window")), poly))

    # нормализация ориентации (все контуры CCW, площадь +)
    for name, poly in [("outer", z.outer)] + \
                      [("opening:%s" % o.id, o.poly) for o in z.openings]:
        a = poly.signed_area()
        if a < 0:
            poly.reverse()
    return z


def load_zones(src):
    """Файл со списком зон (или одной) -> [Zone]."""
    if isinstance(src, str):
        with open(src, "r", encoding="utf-8") as f:
            data = json.load(f)
    else:
        data = src
    if isinstance(data, dict):
        return [load_zone(data)]
    return [load_zone(d) for d in data]


# --------------------------------------------------------------- валидация

def validate_zone(zone):
    issues = list(zone.norm_warnings)
    k = zone.to_mm()
    tol = GEO_TOL  # допуски в мм; координаты приводим к мм

    def mm(pts):
        return [(p[0] * k, p[1] * k) for p in pts]

    # --- outer (самопересечение проверяем ДО площади: у «бабочки»
    # знаковая площадь ~0 и E_ZERO_AREA прятал бы настоящую причину)
    op = zone.outer
    if op.n() < 3:
        issues.append(_err("E_TOO_FEW_POINTS", "outer",
                           "меньше 3 вершин после нормализации"))
        return issues
    outer_pts = mm(op.polygonized(CHORD_TOL / k))
    rels = _self_intersections(outer_pts, tol)
    if "proper" in rels:
        issues.append(_err("E_SELF_INTERSECT", "outer",
                           "самопересечение внешнего контура"))
        return issues
    if "touch" in rels:
        issues.append(_warn("W_SELF_TOUCH", "outer",
                            "самокасание внешнего контура — проверьте обводку"))
    area_mm2 = abs(op.signed_area()) * k * k
    if area_mm2 <= tol * tol:
        issues.append(_err("E_ZERO_AREA", "outer", "вырожденный контур"))
        return issues

    # --- openings
    polys = []
    for o in zone.openings:
        where = "opening:%s" % o.id
        if o.poly.n() < 3:
            issues.append(_err("E_TOO_FEW_POINTS", where, "меньше 3 вершин"))
            continue
        pts = mm(o.poly.polygonized(CHORD_TOL / k))
        if "proper" in _self_intersections(pts, tol):
            issues.append(_err("E_SELF_INTERSECT", where, "самопересечение"))
            continue
        a_m2 = abs(o.poly.signed_area()) * k * k / 1e6
        if a_m2 <= 0.0:
            issues.append(_err("E_ZERO_AREA", where, "вырожденный проём"))
            continue
        if a_m2 < TINY_OPENING_M2:
            issues.append(_warn("W_TINY_OPENING", where,
                                "площадь %.4f м² — мусор обводки?" % a_m2))
        if not _contains(outer_pts, pts, tol):
            issues.append(_err("E_OPENING_OUTSIDE", where,
                               "проём выходит за контур зоны"))
            continue
        polys.append((o, pts))

    for i in range(len(polys)):
        for j in range(i + 1, len(polys)):
            rel = _polys_relation(polys[i][1], polys[j][1], tol)
            if rel in ("cross", "a_in_b", "b_in_a"):
                issues.append(_err(
                    "E_OPENINGS_OVERLAP",
                    "opening:%s" % polys[i][0].id,
                    "пересекается/вложен с opening:%s" % polys[j][0].id))
            elif rel == "touch" and _interiors_overlap(polys[i][1], polys[j][1], tol):
                # 24.09 (рецензия): совпадающие/выровненно наложенные окна
                # вычитались дважды (стена 25 м², два одинаковых окна по
                # 9 м² — нетто 7; три — минус 2)
                same = _same_contour(polys[i][1], polys[j][1], tol)
                issues.append(_err(
                    "E_OPENINGS_OVERLAP",
                    "opening:%s" % polys[i][0].id,
                    ("повторяет opening:%s (две одинаковые обводки)" if same
                     else "накладывается на opening:%s") % polys[j][0].id))
    # последняя страховка: проёмы не больше зоны (без «max(0, …)»)
    if polys and not _has_errors(issues):
        k2 = zone.to_mm() ** 2
        a_out = abs(zone.outer.signed_area()) * k2
        a_ops = sum(abs(o.poly.signed_area()) for o, _p in polys) * k2
        if a_ops > a_out * (1.0 + 1e-9) + 1e-6:
            issues.append(_err("E_NEGATIVE_NET", "outer",
                               "проёмы (%.3f м²) больше площади зоны (%.3f м²)"
                               % (a_ops / 1e6, a_out / 1e6)))
    return issues


def _has_errors(issues):
    return any(i.level == "error" for i in issues)


# ------------------------------------------------- кромки проёмов (отлив/откос)

def _opening_edges_mm(zone, opening, outer_pts_mm, tol=GEO_TOL):
    """Разложить периметр проёма по типам кромок (все длины в мм).

    bottom — низ проёма (отлив), top — верх (откос+отсечка), sides — бока
    (откос+отсечка), boundary — кромки, лежащие НА границе зоны (дверь до
    низа зоны: порог без отлива).

    Классификация по направлению обхода CCW-полигона проёма: интерьер
    слева, поэтому сегмент вправо (|dy|<=|dx|, dx>0) — нижняя кромка,
    влево — верхняя, остальное — бока. Дуги классифицируются мини-хордами
    полигонизации (арочный верх уходит в top/sides по фактическим наклонам).
    """
    k = zone.to_mm()
    out = {"bottom": 0.0, "top": 0.0, "sides": 0.0, "boundary": 0.0}
    for p1, p2, b in opening.poly.segments():
        if abs(b) > EPS:
            chain = [p1] + _arc_points(p1, p2, b, CHORD_TOL / k) + [p2]
        else:
            chain = [p1, p2]
        for i in range(len(chain) - 1):
          a0 = (chain[i][0] * k, chain[i][1] * k)
          c0 = (chain[i + 1][0] * k, chain[i + 1][1] * k)
          # 24.09 (рецензия): сторона окна, ЧАСТИЧНО лежащая на краю зоны,
          # целиком шла в откос — теперь делится по точкам касания: часть
          # на границе — «граница» (как целый сегмент на границе раньше)
          # 23.09n (Герман, ответ по сборке №24): бок окна, ЧАСТИЧНО лежащий на
          # краю зоны, — «всё равно считать откосом»: бок не делим, он откос
          # целиком; бок, целиком лежащий на краю, — по-прежнему «граница».
          # Верх и низ делятся по точкам касания, как с 24.09.
          ddx, ddy = abs(c0[0] - a0[0]), abs(c0[1] - a0[1])
          if ddy > ddx + 1e-7 * max(ddx, ddy):
              ts = [0.0, 1.0]
          else:
              ts = _seg_params(a0, c0, outer_pts_mm, tol)
          for kk in range(len(ts) - 1):
            a = (a0[0] + (c0[0] - a0[0]) * ts[kk], a0[1] + (c0[1] - a0[1]) * ts[kk])
            c = (a0[0] + (c0[0] - a0[0]) * ts[kk + 1], a0[1] + (c0[1] - a0[1]) * ts[kk + 1])
            L = _dist(a, c)
            if L < EPS:
                continue
            mid = ((a[0] + c[0]) / 2.0, (a[1] + c[1]) / 2.0)
            if (_on_boundary(a, outer_pts_mm, tol) and
                    _on_boundary(c, outer_pts_mm, tol) and
                    _on_boundary(mid, outer_pts_mm, tol)):
                out["boundary"] += L
                continue
            dx, dy = c[0] - a[0], c[1] - a[1]
            hv = "bottom" if dx > 0 else "top"
            # 23.09 (ревью): хорда дуги РОВНО под 45° (у круглых/арочных окон
            # середина четверти) падала то в низ/верх, то в бок в зависимости
            # от положения окна на чертеже (шум double) — отлив/откос круглого
            # окна «гулял» до 17 см при сдвиге чертежа. Ничья — пополам.
            if abs(abs(dy) - abs(dx)) <= 1e-7 * L:
                out[hv] += 0.5 * L
                out["sides"] += 0.5 * L
            elif abs(dy) < abs(dx):
                out[hv] += L
            else:
                out["sides"] += L
    return out


def _centroid(pts):
    """Центроид полигона (по вершинам полигонизации)."""
    a2 = 0.0
    cx = cy = 0.0
    n = len(pts)
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % n]
        cr = x1 * y2 - x2 * y1
        a2 += cr
        cx += (x1 + x2) * cr
        cy += (y1 + y2) * cr
    if abs(a2) < EPS:
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        return (sum(xs) / n, sum(ys) / n)
    return (cx / (3.0 * a2), cy / (3.0 * a2))


def label_anchor(pts, tol=GEO_TOL):
    """Точка марки зоны — ПРАВЫЙ ВЕРХНИЙ угол ОБЛАСТИ (фидбэк Германа
    21.07 п.1, было — центроид): вершина полигонизации с максимальным
    Y, при равных (GEO_TOL) — с максимальным X. Именно угол контура, а
    не bbox: у Г-образной зоны с вырезом сверху-справа марка сядет на
    правый конец верхней кромки, а не повиснет в пустоте."""
    y_max = max(p[1] for p in pts)
    return (max(p[0] for p in pts if p[1] >= y_max - tol), y_max)


# ------------------------------------------------------------------- отчёт

def zone_report(zone, issues=None):
    """Площади/периметры зоны в м²/м. issues можно передать готовые
    (после validate_zone), иначе посчитаются заново. При errors — ValueError."""
    if issues is None:
        issues = validate_zone(zone)
    if _has_errors(issues):
        raise ValueError("зона %s не прошла валидацию: %s" %
                         (zone.id, "; ".join(str(i) for i in issues
                                             if i.level == "error")))
    k = zone.to_mm()
    a_out = abs(zone.outer.signed_area()) * k * k / 1e6
    p_out = zone.outer.perimeter() * k / 1e3
    outer_pts_mm = [(p[0] * k, p[1] * k) for p in zone.outer.polygonized(CHORD_TOL / k)]
    ops = []
    a_ops = 0.0
    p_ops = 0.0
    sills = jambs = on_bnd = 0.0
    for o in zone.openings:
        a = abs(o.poly.signed_area()) * k * k / 1e6
        p = o.poly.perimeter() * k / 1e3
        a_ops += a
        p_ops += p
        e = _opening_edges_mm(zone, o, outer_pts_mm)
        edges = {"bottom_m": e["bottom"] / 1e3, "top_m": e["top"] / 1e3,
                 "sides_m": e["sides"] / 1e3,
                 "on_boundary_m": e["boundary"] / 1e3}
        sills += edges["bottom_m"]
        jambs += edges["top_m"] + edges["sides_m"]
        on_bnd += edges["on_boundary_m"]
        ops.append({"id": o.id, "kind": o.kind,
                    "area_m2": a, "perimeter_m": p, "edges": edges})
    return {
        "schema": REPORT_SCHEMA,
        "zone_id": zone.id,
        "name": zone.name,
        "facade": zone.facade,
        "floors": zone.floors,
        "system": zone.system,
        "area_outer_m2": a_out,
        "openings_total_m2": a_ops,
        "area_net_m2": a_out - a_ops,
        "perimeter_outer_m": p_out,
        "openings_count": len(ops),
        "openings": ops,
        "openings_perimeter_total_m": p_ops,
        "sills_total_m": sills,
        "jambs_total_m": jambs,
        "on_boundary_total_m": on_bnd,
        "warnings": [i.as_dict() for i in issues if i.level == "warning"],
    }


def report_text(rep):
    """Человекочитаемая сводка (командная строка AutoCAD/консоль)."""
    L = []
    head = "Зона %s" % rep["zone_id"]
    if rep.get("name"):
        head += " — %s" % rep["name"]
    L.append(head)
    L.append("  брутто: %.3f м²; проёмы (%d): %.3f м²; НЕТТО: %.3f м²" %
             (rep["area_outer_m2"], rep["openings_count"],
              rep["openings_total_m2"], rep["area_net_m2"]))
    L.append("  периметр контура: %.3f м; периметры проёмов всего: %.3f м" %
             (rep["perimeter_outer_m"], rep["openings_perimeter_total_m"]))
    L.append("  отливы (низ проёмов): %.3f м; откосы (верх+бока): %.3f м%s" %
             (rep["sills_total_m"], rep["jambs_total_m"],
              ("; кромок на границе зоны: %.3f м" % rep["on_boundary_total_m"])
              if rep.get("on_boundary_total_m") else ""))
    for o in rep["openings"]:
        L.append("    %s [%s]: %.3f м², %.3f м" %
                 (o["id"], o["kind"], o["area_m2"], o["perimeter_m"]))
    for w in rep["warnings"]:
        L.append("  ! %s: %s" % (w["where"], w["msg"]))
    return "\n".join(L)


# ----------------------------------- размеры зоны (фидбэк Германа №2, п.4)

def zone_dims(zone):
    """Данные для линейных размеров зоны (образец Германа «Проба 4»):

    - высота зоны — вертикальный размер слева по bbox;
    - цепочка по НИЖНЕЙ границе — только если на ней есть разрывы
      (вершины нижних сегментов контура + границы проёмов, касающихся
      низа); плюс общий габарит ширины. Низ без разрывов — горизонтальных
      размеров нет (правило снято с его файла: 10 высот + цепочка 5 + 1
      габарит только у зоны с дверями).

    Возвращает {"height": {...}, "bottom": {...}|None} в единицах зоны.
    """
    k = zone.to_mm()
    tol = GEO_TOL / k
    pts = zone.outer.polygonized(CHORD_TOL / k)
    xs_all = [p[0] for p in pts]
    ys_all = [p[1] for p in pts]
    x0, x1 = min(xs_all), max(xs_all)
    y0, y1 = min(ys_all), max(ys_all)

    cuts = set([x0, x1])
    n = len(pts)
    for i in range(n):
        a, b = pts[i], pts[(i + 1) % n]
        if abs(a[1] - y0) <= tol and abs(b[1] - y0) <= tol:
            cuts.add(a[0])
            cuts.add(b[0])
    for o in zone.openings:
        opts = o.poly.polygonized(CHORD_TOL / k)
        m = len(opts)
        for i in range(m):
            a, b = opts[i], opts[(i + 1) % m]
            if abs(a[1] - y0) <= tol and abs(b[1] - y0) <= tol:
                cuts.add(a[0])
                cuts.add(b[0])

    thr = max(tol, 1.0 / k)   # дедуп: 1 мм в единицах зоны
    xs = []
    for x in sorted(cuts):
        if not xs or x - xs[-1] > thr:
            xs.append(x)
    bottom = {"y": y0, "xs": xs} if len(xs) >= 3 else None
    return {"height": {"x": x0, "y0": y0, "y1": y1}, "bottom": bottom}


# ------------------------------------ группировка контуров (ручной режим)

def build_zones_from_contours(contours, cladding="", zone_prefix="Z-",
                              start_index=1, units="mm", source=None):
    """Плоский список контуров (из C#-выбора) -> зоны по вложенности.

    contours: [{"id": любое, "pts": [[x,y],...], "bulges": [...]?}, ...]
    Top-level контуры = зоны; вложенные 1-го уровня = проёмы; глубже —
    ошибка E_NESTED_DEEP (контур пропускается).

    Возвращает (zone_dicts, issues): zone_dicts — список dict facade_zone/1
    (нумерация zone_prefix+N от start_index, в порядке убывания площади),
    issues — глобальные проблемы разбора контуров (Issue).
    """
    if units not in _UNIT_TO_MM:
        raise ZoneFormatError("units: только mm|m")
    k = _UNIT_TO_MM[units]
    issues = []
    parsed = []  # (cid, Poly, pts_mm, area_mm2)
    for i, c in enumerate(contours):
        cid = str(c.get("id", i))
        where = "contour:%s" % cid
        try:
            pts = c["pts"]
            if not isinstance(pts, list) or len(pts) < 3:
                raise ZoneFormatError("минимум 3 вершины")
            # 24.09 (рецензия): только конечные числа (NaN давал NaN-площадь)
            for q in pts:
                if (not isinstance(q, (list, tuple)) or len(q) != 2 or
                        not all(isinstance(v, (int, float)) and not isinstance(v, bool)
                                and math.isfinite(v) for v in q)):
                    raise ZoneFormatError("вершина не [x,y] из конечных чисел")
            if c.get("bulges") is not None and not all(
                    isinstance(v, (int, float)) and math.isfinite(v) for v in c.get("bulges")):
                raise ZoneFormatError("bulges — конечные числа")
            # хвост с микрозазором к первой точке — слить
            p0, pl = pts[0], pts[-1]
            d = math.hypot((pl[0] - p0[0]) * k, (pl[1] - p0[1]) * k)
            bulges = c.get("bulges")
            if DUP_TOL < d <= GEO_TOL:
                pts = pts[:-1]
                if bulges is not None:
                    bulges = bulges[:-1]
            poly = Poly(pts, bulges)
        except (ZoneFormatError, KeyError, TypeError, ValueError) as e:
            issues.append(_err("E_BAD_CONTOUR", where, str(e)))
            continue
        if poly.n() < 3:
            issues.append(_err("E_BAD_CONTOUR", where,
                               "меньше 3 вершин после нормализации"))
            continue
        # нулевую/вырожденную площадь здесь НЕ отсеиваем: пусть контур станет
        # зоной и провалит полную валидацию с внятным диагнозом
        # (E_SELF_INTERSECT для «бабочки», E_ZERO_AREA для коллинеарного) —
        # ошибка уйдёт в failed, а не потеряется среди issues разбора
        area = abs(poly.signed_area()) * k * k
        pts_mm = [(p[0] * k, p[1] * k) for p in poly.polygonized(CHORD_TOL / k)]
        # 24.09 (независимая рецензия): одна и та же обводка дважды (копия
        # поверх, двойной клик) — окно вычиталось дважды, стена считалась
        # дважды. Точный повтор — не учитывается, с предупреждением.
        dup = next((q for q in parsed if abs(q[3] - area) <= GEO_TOL * GEO_TOL + 1e-9 * area
                    and _same_contour(q[2], pts_mm)), None)
        if dup is not None:
            issues.append(_warn("W_DUPLICATE_CONTOUR", where,
                                "повторяет contour:%s (две одинаковые обводки) — повтор "
                                "не учтён" % dup[0]))
            continue
        parsed.append((cid, poly, pts_mm, area))

    # минимальный по площади контейнер для каждого контура
    n = len(parsed)
    container = [-1] * n
    for i in range(n):
        best = -1
        for j in range(n):
            if i == j or parsed[j][3] <= parsed[i][3]:
                continue
            if _contains(parsed[j][2], parsed[i][2]):
                if best < 0 or parsed[j][3] < parsed[best][3]:
                    best = j
        container[i] = best

    def depth(i):
        d, cur = 0, container[i]
        while cur >= 0 and d <= n:
            d += 1
            cur = container[cur]
        return d

    zones = []   # индексы top-level
    kids = {}
    # 23.09 (ревью): контур, ПЕРЕСЕКАЮЩИЙ край другого (проём, нарисованный
    # с нахлёстом за стену), не вложен ни в кого — раньше он молча
    # становился ОТДЕЛЬНОЙ ЗОНОЙ с площадью окна, а из стены не вычитался
    # (стена 8×5 м: 40.000 м² вместо 37.825 + «зона» 2.25). Теперь — ошибка
    # с именами контуров, меньший из пары в зоны не идёт.
    tops0 = [i for i in range(n) if depth(i) == 0]
    bb0 = {}
    for i in tops0:
        xs0 = [p[0] for p in parsed[i][2]]
        ys0 = [p[1] for p in parsed[i][2]]
        bb0[i] = (min(xs0), min(ys0), max(xs0), max(ys0))
    crossed = set()
    for ia in range(len(tops0)):
        for ib in range(ia + 1, len(tops0)):
            i, j = tops0[ia], tops0[ib]
            a0, b0 = bb0[i], bb0[j]
            if a0[2] < b0[0] - GEO_TOL or b0[2] < a0[0] - GEO_TOL or \
               a0[3] < b0[1] - GEO_TOL or b0[3] < a0[1] - GEO_TOL:
                continue
            rel = _polys_relation(parsed[i][2], parsed[j][2])
            if rel != "cross" and not _interiors_overlap(parsed[i][2], parsed[j][2]):
                continue
            small, big = (i, j) if parsed[i][3] <= parsed[j][3] else (j, i)
            if small in crossed:
                continue
            crossed.add(small)
            if rel == "cross":
                issues.append(_err(
                    "E_CONTOUR_CROSSES", "contour:%s" % parsed[small][0],
                    "контур пересекает край контура %s — проём с нахлёстом за стену? "
                    "Не учтён (ни зоной, ни проёмом): поправьте обводку — проём "
                    "целиком внутри стены или по её краю" % parsed[big][0]))
            else:
                # 24.09 (рецензия): выровненный нахлёст двух стен (общие верх
                # и низ) не ловился — две зоны считали одну площадь дважды
                issues.append(_err(
                    "E_CONTOURS_OVERLAP", "contour:%s" % parsed[small][0],
                    "контур накладывается на контур %s (общая площадь) — одна "
                    "площадь посчиталась бы дважды. Не учтён: поправьте обводку"
                    % parsed[big][0]))
    for i in range(n):
        d = depth(i)
        if i in crossed:
            continue
        if d == 0:
            zones.append(i)
        elif d == 1:
            kids.setdefault(container[i], []).append(i)
        else:
            issues.append(_err(
                "E_NESTED_DEEP", "contour:%s" % parsed[i][0],
                "контур вложен глубже проёма (уровень %d) — пропущен" % d))

    # нумерация по правилу Германа (фидбэк 19.07): СНИЗУ ВВЕРХ и СПРАВА
    # НАЛЕВО. Ряды кластеризуются по minY с допуском 25% медианной высоты
    # зон (этажные пояса ловятся, миллиметровый разброс внутри ряда — нет);
    # внутри ряда — по убыванию maxX.
    def _bb(i):
        xs = [p[0] for p in parsed[i][2]]
        ys = [p[1] for p in parsed[i][2]]
        return (min(xs), min(ys), max(xs), max(ys))

    bbs = {i: _bb(i) for i in zones}
    if zones:
        heights = sorted(bbs[i][3] - bbs[i][1] for i in zones)
        med_h = heights[len(heights) // 2]
        tol_row = max(1.0, 0.25 * med_h)
        by_y = sorted(zones, key=lambda i: bbs[i][1])
        rows, row, base = [], [], None
        for i in by_y:
            if base is None or bbs[i][1] - base <= tol_row:
                row.append(i)
                if base is None:
                    base = bbs[i][1]
            else:
                rows.append(row)
                row, base = [i], bbs[i][1]
        if row:
            rows.append(row)
        zones = [i for r in rows
                 for i in sorted(r, key=lambda i: -bbs[i][2])]

    zone_dicts = []
    num = start_index
    for zi in zones:
        cid, poly, _, _ = parsed[zi]
        bb = bbs[zi]
        zd = {
            "schema": SCHEMA,
            "id": "%s%d" % (zone_prefix, num),
            "name": cladding,
            "cladding": cladding,
            "system": None,
            "units": units,
            "outer": {"pts": [[p[0], p[1]] for p in poly.pts],
                      "bulges": list(poly.bulges)},
            "openings": [],
            "source": source or {"method": "manual"},
            "meta": {"outer_contour_id": cid,
                     "bbox": [bb[0] / k, bb[1] / k, bb[2] / k, bb[3] / k]},
        }
        for oi in kids.get(zi, []):
            ocid, opoly, _, _ = parsed[oi]
            zd["openings"].append({
                "id": ocid, "kind": "window",
                "poly": {"pts": [[p[0], p[1]] for p in opoly.pts],
                         "bulges": list(opoly.bulges)}})
        zone_dicts.append(zd)
        num += 1
    return zone_dicts, issues


# --------------------------------------------------------------------- CLI

def main(argv):
    if len(argv) < 2 or argv[0] not in ("validate", "report"):
        sys.stderr.write(__doc__)
        return 2
    op, path = argv[0], argv[1]
    out_path = None
    if "-o" in argv:
        out_path = argv[argv.index("-o") + 1]
    try:
        zones = load_zones(path)
    except (ZoneFormatError, ValueError, OSError) as e:
        sys.stderr.write("ФОРМАТ: %s\n" % e)
        return 1
    rc = 0
    reports = []
    for z in zones:
        issues = validate_zone(z)
        for i in issues:
            sys.stderr.write("%s\n" % i)
        if _has_errors(issues):
            rc = 1
            continue
        if op == "report":
            rep = zone_report(z, issues)
            reports.append(rep)
            print(report_text(rep))
        else:
            print("Зона %s: OK (%d предупреждений)" %
                  (z.id, sum(1 for i in issues if i.level == "warning")))
    if op == "report" and out_path and reports:
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(reports if len(reports) > 1 else reports[0],
                      f, ensure_ascii=False, indent=2)
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
