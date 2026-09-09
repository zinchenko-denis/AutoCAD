# -*- coding: utf-8 -*-
"""Раскладка мелкоштучной облицовки (плитка/кирпич) с перевязкой и
раскраской по образцу-раппорту (модуль AClad, op="tile_pattern").

Откуда: задание Германа 09.09 — бетонная плитка 290×82, руст 7,
шахматный порядок со смещением рядов на полплиты, раскладка снизу
вверх и справа налево, цвета по типовой схеме 6×10 (три цвета на
участок, два участка — терракотовый и бежевый, всего 6 типов).
Прототип на shapely/ezdxf сделан в сессии 09.09 (claude.ai-проект,
«Раскладка_плитки_290x82_0909.md»); здесь — тот же алгоритм на
чистом stdlib (движок замораживается PyInstaller — ezdxf/shapely
в движок не тащить), сверенный с прототипом до штуки на живом
эталоне (atspec-testdata/dxf/facades/tiles290).

Модель:
- контур зоны — ортогональный полигон с дырами-проёмами (как в
  cladding_plan; почти ортогональные рёбра выпрямляются
  _ortho_snap, наклонные — note и пропуск зоны);
- сетка: модуль MX = w + gap.v по X, MY = h + gap.h по Y; ряд j
  занимает [B + j·MY, B + j·MY + h]; плитка i ряда j занимает
  [R − i·MX − w + s_j, R − i·MX + s_j], где (R, B) — датум зоны
  (правый нижний угол: целая плитка справа внизу, подрезка слева и
  сверху), s_j = row_shifts[j mod NR]·MX — смещение ряда из образца
  (полплиты: 0, +0.5, 0, +0.5 …). i считается от правой кромки
  (i = 0 — крайняя правая плитка), j — от датума вверх; ниже датума
  (j < 0) и правее (i < 0) сетка тоже определена — так выступы под
  основной стеной и правее её получают ту же сетку;
- цвет плитки (i, j) = rows[j mod NR][(NC − 1) − (i mod NC)], где
  rows — ряды образца снизу вверх, плитки в ряду слева направо (как
  нарисовано). Проверено: прямоугольник 6×10 у датума воспроизводит
  образец один в один, включая относительный сдвиг рядов;
- датум (mode): "wall" — правый нижний угол ОСНОВНОЙ стены
  (доминирующее правое вертикальное и нижнее горизонтальное ребро
  внешнего контура по суммарной длине; целые плитки садятся на
  стену, подрезка уходит на выступы/«ножки»); "bbox" — угол
  габарита (Xmax, Ymin), буквальное «справа налево, снизу вверх»;
  "point" — заданная точка (общий датум нескольких зон / клик
  пользователя). x/y можно задать поверх режима (общий горизонт);
- клиппинг: зона режется на вертикальные полосы по всем X вершин
  (slab decomposition), в полосе покрытие — Y-интервалы по чёт-нечет
  горизонтальных рёбер; плитка ∩ зона = набор прямоугольников по
  полосам; целая — если они покрывают всю плитку; иначе куски =
  компоненты связности прямоугольников (соседние полосы с общим
  Y-перекрытием), контур куска — объединение прямоугольников
  (сокращение внутренних рёбер). Куски получаются прямоугольные
  (подрезка по ширине/высоте) или фигурные (Г-образные у углов
  проёмов);
- полоски: кусок с меньшим габаритом < min_piece (дефолт 10 мм)
  ПОГЛОЩАЕТСЯ рустами — в раскладку не идёт (ответ Германа 09.09,
  tiny_mode="absorb"; tiny_mode="layer" — прежнее поведение: кусок
  остаётся с флагом tiny под отдельный слой). < 0.1 мм — численный
  мусор, отбрасывается всегда;
- раскрой (ответ Германа 09.09 — «как учтены обрезки и отходы»):
  заготовки под подрезку считаются НЕ «плитка на кусок», а укладкой
  кусков в целые плитки (nest_pieces): полосы полной высоты — по
  ширине, полосы полной ширины — по высоте, first-fit-decreasing с
  пропилом kerf (дефолт 3 мм); фигурные (Г у угла окна) — плитка на
  кусок. Отходы = площадь заготовок − площадь кусков, отдельной
  строкой в сводке;
- смежные контуры одной плоскости (ответ Германа 09.09 на В2: стены
  Б4–Б7 — не независимые) — outer-контуры, касающиеся по ребру,
  объединяются в одну зону с общей сеткой (merge_touching, дефолт
  True; допуск стыка merge_tol 1 мм);
- датум по умолчанию — "bbox" (ответ Германа 09.09 на В1: как у GPT —
  правый-низ габарита зоны, буквальное «справа налево, снизу вверх»).

Только stdlib.
"""
from bisect import bisect_left, bisect_right
import math

from cladding_plan import _closed, _edges, _is_ortho, _ortho_err, \
    _ortho_snap, EPS

NOISE_MM = 0.1        # кусок тоньше — численный мусор (ребро зоны на линии сетки)
MIN_PIECE_MM = 10.0   # дефолт порога «полоски» (поглощаются рустами)
AREA_TOL = 1e-6       # относительный допуск «плитка покрыта целиком»
QUANT_TOL = 0.1       # мм: кластеризация координат после _ortho_snap
MERGE_TOL = 1.0       # мм: допуск стыка смежных контуров одной плоскости
KERF_MM = 3.0         # мм: пропил при раскрое подрезки
DEFAULT_DATUM = "bbox"  # ответ Германа 09.09 (В1)


# ────────────────────────────────────────────── геометрия: ортогональная зона

def signed_area(pts):
    a = 0.0
    n = len(pts)
    for k in range(n):
        x1, y1 = pts[k]
        x2, y2 = pts[(k + 1) % n]
        a += x1 * y2 - x2 * y1
    return a / 2.0


class OrthoRegion(object):
    """Ортогональный полигон с дырами → вертикальные полосы (slabs).

    slabs[k] = (x0, x1, [(ya, yb), ...]) — полоса между соседними X
    вершин и Y-интервалы её покрытия зоной (чёт-нечет по
    горизонтальным рёбрам всех колец, накрывающим полосу)."""

    def __init__(self, outer, holes=()):
        self._init_rings([list(outer)], [list(h) for h in holes])

    @classmethod
    def from_rings(cls, outers, holes=()):
        """Зона из нескольких внешних контуров (объединённая плоскость)
        — чёт-нечёт по горизонтальным рёбрам всех колец; контуры не
        должны перекрываться (стыки — по общему ребру)."""
        self = cls.__new__(cls)
        self._init_rings([list(o) for o in outers], [list(h) for h in holes])
        return self

    def _init_rings(self, outers, holes):
        rings = outers + holes
        xs = sorted(set(x for ring in rings for x, _y in ring))
        self.xs = xs
        hedges = []
        for ring in rings:
            for (x1, y1), (x2, y2) in _edges(ring):
                if abs(y1 - y2) < EPS and abs(x1 - x2) > EPS:
                    hedges.append((y1, min(x1, x2), max(x1, x2)))
        per = [[] for _ in range(max(len(xs) - 1, 0))]
        for y, xa, xb in hedges:
            ka = bisect_left(xs, xa)
            kb = bisect_left(xs, xb)
            for k in range(ka, kb):
                per[k].append(y)
        self.slabs = []
        self.area = 0.0
        for k in range(len(xs) - 1):
            ys = sorted(per[k])
            ivs = []
            for m in range(0, len(ys) - 1, 2):
                if ys[m + 1] - ys[m] > EPS:
                    ivs.append((ys[m], ys[m + 1]))
                    self.area += (xs[k + 1] - xs[k]) * (ys[m + 1] - ys[m])
            self.slabs.append((xs[k], xs[k + 1], ivs))
        bx0, bx1 = (xs[0], xs[-1]) if xs else (0.0, 0.0)
        ally = [y for o in outers for _x, y in o]
        self.bounds = (bx0, min(ally), bx1, max(ally))

    def clip_rect(self, tx0, ty0, tx1, ty1):
        """Прямоугольники покрытия плитки [tx0,tx1]×[ty0,ty1] по полосам."""
        xs = self.xs
        ka = max(bisect_right(xs, tx0) - 1, 0)
        kb = min(bisect_left(xs, tx1), len(self.slabs))
        out = []
        for k in range(ka, kb):
            x0, x1, ivs = self.slabs[k]
            cx0 = x0 if x0 > tx0 else tx0
            cx1 = x1 if x1 < tx1 else tx1
            if cx1 - cx0 <= EPS:
                continue
            for ya, yb in ivs:
                if yb <= ty0 + EPS:
                    continue
                if ya >= ty1 - EPS:
                    break
                cy0 = ya if ya > ty0 else ty0
                cy1 = yb if yb < ty1 else ty1
                if cy1 - cy0 > EPS:
                    out.append((cx0, cy0, cx1, cy1))
        return out


def _components(rects):
    """Компоненты связности прямоугольников: соседние по X (общая
    граница полосы) с положительным перекрытием по Y."""
    n = len(rects)
    parent = list(range(n))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for a in range(n):
        ax0, ay0, ax1, ay1 = rects[a]
        for b in range(a + 1, n):
            bx0, by0, bx1, by1 = rects[b]
            if (abs(ax1 - bx0) < EPS or abs(bx1 - ax0) < EPS) and \
               min(ay1, by1) - max(ay0, by0) > EPS:
                ra, rb = find(a), find(b)
                if ra != rb:
                    parent[ra] = rb
    groups = {}
    for a in range(n):
        groups.setdefault(find(a), []).append(rects[a])
    return list(groups.values())


def _cancel(segs):
    """segs: [(c, a, b, dir)] — отрезки на общей координате c от a до b
    (a < b) с направлением ±1. Внутренние рёбра встречаются дважды с
    противоположным знаком и сокращаются; возвращает элементарные
    направленные рёбра [(c, a, b, dir)] границы."""
    by_c = {}
    for c, a, b, d in segs:
        by_c.setdefault(c, []).append((a, b, d))
    out = []
    for c, lst in by_c.items():
        cuts = sorted(set([a for a, _b, _d in lst] + [b for _a, b, _d in lst]))
        for m in range(len(cuts) - 1):
            lo, hi = cuts[m], cuts[m + 1]
            if hi - lo <= EPS:
                continue
            net = 0
            for a, b, d in lst:
                if a <= lo + EPS and b >= hi - EPS:
                    net += d
            if net > 0:
                out.append((c, lo, hi, 1))
            elif net < 0:
                out.append((c, lo, hi, -1))
    return out


def union_outline(rects):
    """Контур объединения непересекающихся прямоугольников (компонента
    связности одной плитки) → кольца [[(x,y),...], ...]: внешнее
    против часовой, дыры — по часовой; коллинеарные вершины убраны."""
    if len(rects) == 1:
        x0, y0, x1, y1 = rects[0]
        return [[(x0, y0), (x1, y0), (x1, y1), (x0, y1)]]
    vsegs, hsegs = [], []
    for x0, y0, x1, y1 in rects:
        hsegs.append((y0, x0, x1, 1))     # низ: слева направо
        vsegs.append((x1, y0, y1, 1))     # правая: вверх
        hsegs.append((y1, x0, x1, -1))    # верх: справа налево
        vsegs.append((x0, y0, y1, -1))    # левая: вниз
    edges = []
    for x, lo, hi, d in _cancel(vsegs):
        edges.append(((x, lo), (x, hi)) if d > 0 else ((x, hi), (x, lo)))
    for y, lo, hi, d in _cancel(hsegs):
        edges.append(((lo, y), (hi, y)) if d > 0 else ((hi, y), (lo, y)))
    start = {}
    for e in edges:
        start.setdefault(e[0], []).append(e)
    rings = []
    used = set()
    for e0 in edges:
        if id(e0) in used:
            continue
        ring = [e0[0]]
        cur = e0
        used.add(id(cur))
        guard = 0
        while True:
            nxt_pt = cur[1]
            if nxt_pt == ring[0]:
                break
            ring.append(nxt_pt)
            cands = [e for e in start.get(nxt_pt, []) if id(e) not in used]
            if not cands:
                break
            # в точке-«перешейке» два выхода: берём поворот, продолжающий
            # обход (первый доступный — контур остаётся замкнутым)
            cur = cands[0]
            used.add(id(cur))
            guard += 1
            if guard > 100000:
                break
        # убрать коллинеарные вершины
        clean = []
        n = len(ring)
        for k in range(n):
            p0, p1, p2 = ring[k - 1], ring[k], ring[(k + 1) % n]
            if (abs(p0[0] - p1[0]) < EPS and abs(p1[0] - p2[0]) < EPS) or \
               (abs(p0[1] - p1[1]) < EPS and abs(p1[1] - p2[1]) < EPS):
                continue
            clean.append(p1)
        if len(clean) >= 3:
            rings.append(clean)
    rings.sort(key=lambda r: -abs(signed_area(r)))
    return rings


# ────────────────────────────────────────────── датум и образец

def datum_wall(outers):
    """Правый нижний угол основной стены: доминирующее (по суммарной
    длине) правое вертикальное ребро и нижнее горизонтальное ребро
    ВНЕШНЕЙ границы. outers — один или несколько внешних контуров
    (объединённая зона): внутренние швы между смежными контурами
    взаимно сокращаются (_cancel), в счёт идёт только наружная кромка.
    Значения — точные координаты рёбер."""
    if outers and not isinstance(outers[0][0], (tuple, list)):
        outers = [outers]
    vsegs, hsegs = [], []
    for outer in outers:
        pts = outer if signed_area(outer) > 0 else outer[::-1]   # CCW: интерьер слева
        for (x1, y1), (x2, y2) in _edges(pts):
            dx, dy = x2 - x1, y2 - y1
            if abs(dx) < EPS and abs(dy) > EPS:
                # вверх → интерьер слева → правая кромка (+1); вниз → левая (−1)
                vsegs.append((round(x1, 1), min(y1, y2), max(y1, y2), 1 if dy > 0 else -1))
            elif abs(dy) < EPS and abs(dx) > EPS:
                # вправо → интерьер сверху → низ (+1); влево → верх (−1)
                hsegs.append((round(y1, 1), min(x1, x2), max(x1, x2), 1 if dx > 0 else -1))
    right, bottom = {}, {}
    for x, lo, hi, d in _cancel(vsegs):
        if d > 0:
            right[x] = right.get(x, 0.0) + (hi - lo)
    for y, lo, hi, d in _cancel(hsegs):
        if d > 0:
            bottom[y] = bottom.get(y, 0.0) + (hi - lo)
    xs = [p[0] for o in outers for p in o]
    ys = [p[1] for o in outers for p in o]
    if right:
        kx = max(right.items(), key=lambda kv: kv[1])[0]
        R = min((x for x in xs if abs(x - kx) <= 0.06), key=lambda x: abs(x - kx), default=kx)
    else:
        R = max(xs)
    if bottom:
        ky = max(bottom.items(), key=lambda kv: kv[1])[0]
        B = min((y for y in ys if abs(y - ky) <= 0.06), key=lambda y: abs(y - ky), default=ky)
    else:
        B = min(ys)
    return R, B


def datum_for(outers, spec):
    """spec: {"mode": "wall"|"bbox"|"point", "x"?, "y"?} → (R, B).
    outers — внешние контуры зоны (несколько — объединённая зона)."""
    spec = spec or {}
    if outers and not isinstance(outers[0][0], (tuple, list)):
        outers = [outers]
    mode = str(spec.get("mode") or DEFAULT_DATUM)
    xs = [p[0] for o in outers for p in o]
    ys = [p[1] for o in outers for p in o]
    if mode == "wall":
        R, B = datum_wall(outers)
    elif mode == "point":
        R, B = float(spec.get("x", max(xs))), float(spec.get("y", min(ys)))
    else:
        R, B = max(xs), min(ys)
    if spec.get("x") is not None and mode != "point":
        R = float(spec["x"])
    if spec.get("y") is not None and mode != "point":
        B = float(spec["y"])
    return R, B


def pattern_from_sample(rects, row_tol=None):
    """Образец из чертежа → раппорт.

    rects: [{"x0","y0","x1","y1","type"}] — прямоугольники плиток образца
    с типом (цвет/марка) — их собирает вызывающая сторона (C#: контуры
    + заливка/слой/блок; tools/tile_pattern_dxf.py — то же по DXF).
    Возвращает {"rows": [[type,...] снизу вверх, слева направо],
    "row_shifts": [доля модуля, ...] (сдвиг ряда относительно нижнего,
    в долях шага плиток образца; +вправо), "cols", "nrows",
    "module_x", "module_y", "tile_w", "tile_h"} или бросает ValueError
    с человеческим текстом."""
    if not rects:
        raise ValueError("образец пуст")
    hs = sorted((r["y1"] - r["y0"]) for r in rects)
    ws = sorted((r["x1"] - r["x0"]) for r in rects)
    tile_h = hs[len(hs) // 2]
    tile_w = ws[len(ws) // 2]
    if row_tol is None:
        row_tol = tile_h * 0.4
    rows = []
    for r in sorted(rects, key=lambda q: q["y0"]):
        if rows and abs(rows[-1][0]["y0"] - r["y0"]) <= row_tol:
            rows[-1].append(r)
        else:
            rows.append([r])
    for row in rows:
        row.sort(key=lambda q: q["x0"])
    ncols = len(rows[0])
    if any(len(row) != ncols for row in rows):
        raise ValueError("в рядах образца разное число плиток: %s"
                         % [len(r) for r in rows])
    if ncols < 1 or len(rows) < 1:
        raise ValueError("образец: нет плиток")
    if ncols >= 2:
        pitches = [row[k + 1]["x0"] - row[k]["x0"]
                   for row in rows for k in range(ncols - 1)]
        module_x = sum(pitches) / len(pitches)
    else:
        module_x = tile_w
    if len(rows) >= 2:
        module_y = sum(rows[k + 1][0]["y0"] - rows[k][0]["y0"]
                       for k in range(len(rows) - 1)) / (len(rows) - 1)
    else:
        module_y = tile_h
    x00 = rows[0][0]["x0"]
    shifts = []
    for row in rows:
        s = (row[0]["x0"] - x00) / module_x
        # сдвиг ряда — доля модуля вправо, приводим к [0, 1)
        # (−0.5 и +0.5 дают одну и ту же модульную сетку → 0.5)
        s -= math.floor(s)
        shifts.append(round(s, 3))
    return {
        "rows": [[str(r["type"]) for r in row] for row in rows],
        "row_shifts": shifts,
        "cols": ncols, "nrows": len(rows),
        "module_x": module_x, "module_y": module_y,
        "tile_w": tile_w, "tile_h": tile_h,
    }


def default_pattern(types, nrows=2, shift=0.5):
    """Раппорт «в разбежку без раскраски»: один тип, ряды через полплиты."""
    t = str(types[0]) if types else "T1"
    return {"rows": [[t]] * nrows,
            "row_shifts": [0.0 if j % 2 == 0 else shift for j in range(nrows)],
            "cols": 1, "nrows": nrows}


# ────────────────────────────────────────────── раскладка одной зоны


def layout_zone(region, R, B, tile_w, tile_h, gap_v, gap_h, pattern,
                min_piece=MIN_PIECE_MM, tiny_mode="absorb"):
    """Плитки одной зоны. Возвращает (pieces, absorbed):
    pieces — {"i","j","type","full","x","y","w","h","rect","tiny","area","rings"?}
    (rings — только у фигурных кусков; прямоугольные — x,y,w,h);
    absorbed — {"count","area"}: полоски тоньше min_piece, поглощённые
    рустами (tiny_mode="absorb") — в pieces их нет."""
    MX = tile_w + gap_v
    MY = tile_h + gap_h
    rows = pattern["rows"]
    shifts = pattern.get("row_shifts") or [0.0, 0.5]
    nr = len(rows)
    nc = len(rows[0])
    ns = len(shifts)
    minx, miny, maxx, maxy = region.bounds
    i_min = int(math.floor((R - maxx) / MX)) - 2
    i_max = int(math.ceil((R - minx) / MX)) + 2
    j_min = int(math.floor((miny - B) / MY)) - 1
    j_max = int(math.ceil((maxy - B) / MY)) + 1
    full_area = tile_w * tile_h
    pieces = []
    absorbed = {"count": 0, "area": 0.0}
    for j in range(j_min, j_max + 1):
        y0 = B + j * MY
        y1 = y0 + tile_h
        if y1 <= miny + EPS or y0 >= maxy - EPS:
            continue
        s = shifts[j % ns] * MX
        row_pat = rows[j % nr]
        for i in range(i_min, i_max + 1):
            x1 = R - i * MX + s
            x0 = x1 - tile_w
            if x1 <= minx + EPS or x0 >= maxx - EPS:
                continue
            rects = region.clip_rect(x0, y0, x1, y1)
            if not rects:
                continue
            ttype = row_pat[(nc - 1) - (i % nc)]
            covered = sum((c - a) * (d - b) for a, b, c, d in rects)
            if abs(covered - full_area) <= AREA_TOL * full_area:
                pieces.append({"i": i, "j": j, "type": ttype, "full": True,
                               "x": x0, "y": y0, "w": tile_w, "h": tile_h,
                               "rect": True, "tiny": False, "area": full_area})
                continue
            for comp in _components(rects):
                bx0 = min(r[0] for r in comp)
                by0 = min(r[1] for r in comp)
                bx1 = max(r[2] for r in comp)
                by1 = max(r[3] for r in comp)
                area = sum((c - a) * (d - b) for a, b, c, d in comp)
                w, h = bx1 - bx0, by1 - by0
                if min(w, h) < NOISE_MM or area < 1.0:
                    continue
                tiny = min(w, h) < min_piece
                if tiny and tiny_mode == "absorb":
                    absorbed["count"] += 1
                    absorbed["area"] += area
                    continue
                is_rect = len(comp) == 1 or abs(area - w * h) <= AREA_TOL * w * h
                piece = {"i": i, "j": j, "type": ttype, "full": False,
                         "x": bx0, "y": by0, "w": w, "h": h,
                         "rect": is_rect, "tiny": tiny, "area": area}
                if not is_rect:
                    piece["rings"] = [[[round(x, 3), round(y, 3)] for x, y in ring]
                                      for ring in union_outline(comp)]
                pieces.append(piece)
    return pieces, absorbed


def classify(piece, tile_w, tile_h, half_w):
    """Вид куска для ведомости подрезки."""
    if piece["full"]:
        return "целая"
    if not piece["rect"]:
        return "фигурная"
    w, h = piece["w"], piece["h"]
    if abs(h - tile_h) < 0.05 and half_w is not None and abs(w - half_w) < 0.05:
        return "половинка"
    if abs(h - tile_h) < 0.05:
        return "рез по ширине"
    if abs(w - tile_w) < 0.05:
        return "рез по высоте"
    return "рез по ширине и высоте"


# ────────────────────────────────────────────── раскрой подрезки

def _ffd(sizes, capacity, kerf):
    """First-fit-decreasing: сколько заготовок длины capacity нужно,
    чтобы нарезать куски sizes с пропилом kerf между соседними."""
    bins = []
    for sz in sorted(sizes, reverse=True):
        placed = False
        for b in bins:
            need = sz + (kerf if b["n"] else 0.0)
            if b["used"] + need <= capacity + EPS:
                b["used"] += need
                b["n"] += 1
                placed = True
                break
        if not placed:
            bins.append({"used": sz, "n": 1})
    return len(bins)


def nest_pieces(pieces, tile_w, tile_h, kerf=KERF_MM):
    """Раскрой кусков подрезки из целых плиток (ответ Германа 09.09 —
    «как учтены обрезки»). Возвращает {"blanks", "by_kind": {...},
    "pieces": n, "area": Σплощадь кусков}.

    Правила: полоса полной высоты (рез только по ширине) — укладка по
    ширине плитки; полоса полной ширины (рез по высоте) — укладка по
    высоте; кусок, резаный в обоих направлениях, — укладывается как
    полоса полной высоты (консервативно: второй рез не переиспользуем);
    фигурный (Г у угла проёма) — плитка на кусок."""
    widths, heights, shaped = [], [], 0
    area = 0.0
    for p in pieces:
        if p["full"]:
            continue
        area += p["area"]
        if not p["rect"]:
            shaped += 1
        elif abs(p["h"] - tile_h) < 0.05:
            widths.append(p["w"])
        elif abs(p["w"] - tile_w) < 0.05:
            heights.append(p["h"])
        else:
            widths.append(p["w"])
    b_w = _ffd(widths, tile_w, kerf)
    b_h = _ffd(heights, tile_h, kerf)
    return {"blanks": b_w + b_h + shaped,
            "by_kind": {"по ширине": b_w, "по высоте": b_h, "фигурные": shaped},
            "pieces": len(widths) + len(heights) + shaped, "area": area}


def _cluster_map(vals, tol):
    """Кластеры близких координат (разрыв ≤ tol) → значение = среднее
    кластера. Возвращает dict{исходное: представитель}. Убирает
    остаточную невязку после _ortho_snap (сотые мм), чтобы почти
    вертикальные/горизонтальные рёбра стали ТОЧНО орто (иначе slab-
    декомпозиция считает их наклонными)."""
    if not vals:
        return {}
    sv = sorted(set(vals))
    groups = [[sv[0]]]
    for v in sv[1:]:
        if v - groups[-1][-1] <= tol:
            groups[-1].append(v)
        else:
            groups.append([v])
    m = {}
    for g in groups:
        rep = sum(g) / len(g)
        for v in g:
            m[v] = rep
    return m


def _quantize(rings, tol):
    """Свести близкие X и близкие Y всех колец к общим значениям."""
    xs = [p[0] for ring in rings for p in ring]
    ys = [p[1] for ring in rings for p in ring]
    mx = _cluster_map(xs, tol)
    my = _cluster_map(ys, tol)
    out = []
    for ring in rings:
        q = []
        for x, y in ring:
            q.append((mx[x], my[y]))
        # убрать дубли-соседи, возникшие от квантования
        dedup = []
        for p in q:
            if not dedup or abs(dedup[-1][0] - p[0]) > EPS or \
               abs(dedup[-1][1] - p[1]) > EPS:
                dedup.append(p)
        if len(dedup) >= 2 and abs(dedup[0][0] - dedup[-1][0]) <= EPS and \
           abs(dedup[0][1] - dedup[-1][1]) <= EPS:
            dedup = dedup[:-1]
        out.append(dedup)
    return out


def _prep_contour(contour, ortho_tol, notes, zone_id):
    """Замкнуть, выпрямить почти ортогональные рёбра, свести близкие
    координаты, проверить ортогональность."""
    outer = _closed(contour["outer"])
    holes = [_closed(h) for h in contour.get("holes") or []]
    if len(outer) < 3:
        notes.append("%s: контур < 3 вершин — пропуск" % zone_id)
        return None
    moved = 0.0
    rings = [outer] + [h for h in holes if len(h) >= 3]
    snapped = []
    for r in rings:
        if not _is_ortho(r):
            r, mv = _ortho_snap(r, ortho_tol)
            moved = max(moved, mv)
        snapped.append(r)
    # свести остаточную невязку (сотые мм) к точным орто-координатам;
    # порог кластеризации — доля допуска, чтобы не слить реальные грани
    snapped = _quantize(snapped, QUANT_TOL)
    bad = [p for p in snapped if len(p) < 3 or not _is_ortho(p)]
    if bad:
        worst = max((_ortho_err(p) for p in bad if len(p) >= 3), default=0.0)
        notes.append("%s: контур не ортогонален (остаточное отклонение "
                     "%.2f мм при допуске %.1f мм) — раскладка не "
                     "выполняется, зона пропущена"
                     % (zone_id, worst, ortho_tol))
        return None
    outer2 = snapped[0]
    fixed = snapped[1:]
    if moved > ortho_tol * 0.5:
        notes.append("%s: рёбра выпрямлены (макс. поправка %.2f мм)"
                     % (zone_id, moved))
    if abs(signed_area(outer2)) < 1.0:
        notes.append("%s: нулевая площадь — пропуск" % zone_id)
        return None
    return outer2, fixed




# ────────────────────────────────────────────── объединение смежных контуров

def _touch(a, b, tol):
    """Касаются ли контуры a и b по ребру (общий вертикальный или
    горизонтальный отрезок длиной > tol при расстоянии между рёбрами
    ≤ tol). Возвращает список (axis, ca, cb): координаты стыкующихся
    рёбер, чтобы свести их к общему значению."""
    seams = []
    ea = [((x1, y1), (x2, y2)) for (x1, y1), (x2, y2) in _edges(a)]
    eb = [((x1, y1), (x2, y2)) for (x1, y1), (x2, y2) in _edges(b)]
    for (ax1, ay1), (ax2, ay2) in ea:
        av = abs(ax1 - ax2) < EPS
        ah = abs(ay1 - ay2) < EPS
        if not (av or ah):
            continue
        for (bx1, by1), (bx2, by2) in eb:
            if av and abs(bx1 - bx2) < EPS and abs(ax1 - bx1) <= tol:
                ov = min(max(ay1, ay2), max(by1, by2)) - max(min(ay1, ay2), min(by1, by2))
                if ov > tol:
                    seams.append(("x", ax1, bx1))
            elif ah and abs(by1 - by2) < EPS and abs(ay1 - by1) <= tol:
                ov = min(max(ax1, ax2), max(bx1, bx2)) - max(min(ax1, ax2), min(bx1, bx2))
                if ov > tol:
                    seams.append(("y", ay1, by1))
    return seams


def merge_touching(zones, tol=MERGE_TOL):
    """zones: [{"id", "outer", "holes"}] (уже выпрямленные) → группы
    смежных контуров объединены в одну зону: {"id": "A+B", "outers":
    [...], "holes": [...], "members": [ids]}. Координаты стыков сведены
    к общему значению (иначе slab-декомпозиция оставит щель)."""
    n = len(zones)
    parent = list(range(n))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    seams_all = []
    for a in range(n):
        for b in range(a + 1, n):
            sm = _touch(zones[a]["outer"], zones[b]["outer"], tol)
            if sm:
                seams_all.extend(sm)
                ra, rb = find(a), find(b)
                if ra != rb:
                    parent[ra] = rb
    groups = {}
    for a in range(n):
        groups.setdefault(find(a), []).append(a)
    # карта координат стыков → общее значение (среднее по кластеру)
    fix = {"x": {}, "y": {}}
    for axis in ("x", "y"):
        vals = sorted(set(v for ax, ca, cb in seams_all if ax == axis for v in (ca, cb)))
        m = _cluster_map(vals, tol)
        fix[axis] = m

    def fx(ring):
        return [(fix["x"].get(x, x), fix["y"].get(y, y)) for x, y in ring]

    out = []
    for _root, idx in sorted(groups.items(), key=lambda kv: min(kv[1])):
        members = [zones[k] for k in sorted(idx)]
        if len(members) == 1:
            z = members[0]
            out.append({"id": z["id"], "outers": [z["outer"]],
                        "holes": list(z["holes"]), "members": [z["id"]]})
            continue
        out.append({
            "id": "+".join(z["id"] for z in members),
            "outers": [fx(z["outer"]) for z in members],
            "holes": [fx(h) for z in members for h in z["holes"]],
            "members": [z["id"] for z in members],
        })
    return out


# ────────────────────────────────────────────── раскладка по зонам

def tile_pattern(req):
    """Раскладка по списку контуров (уже сгруппированных: outer+holes).

    req = {"tile": {"w","h"}, "gap": {"v","h"},
           "pattern": {"rows": [[type,...],...], "row_shifts": [...]},
           "datum": {"mode","x"?,"y"?}, "min_piece"?, "tiny_mode"?,
           "kerf"?, "merge_touching"?, "merge_tol"?, "ortho_tol"?,
           "contours": [{"id", "outer", "holes", "datum"?}, ...]}
    → {"ok", "pieces": [...+"zone"], "per_zone": [...], "summary", "notes"}."""
    notes = []
    try:
        tile_w = float(req["tile"]["w"])
        tile_h = float(req["tile"]["h"])
        gap_v = float(req.get("gap", {}).get("v", 0.0))
        gap_h = float(req.get("gap", {}).get("h", gap_v))
    except (KeyError, TypeError, ValueError):
        return {"ok": False, "error": "нет размеров плитки tile{w,h}/gap{v,h}",
                "notes": notes}
    if tile_w <= 0 or tile_h <= 0 or gap_v < 0 or gap_h < 0:
        return {"ok": False, "error": "размеры плитки/русты вне диапазона",
                "notes": notes}
    pattern = req.get("pattern")
    if not pattern or not pattern.get("rows"):
        pattern = default_pattern(req.get("types") or ["T1"])
        notes.append("образец не задан — разбежка полплиты одним типом")
    rows = pattern["rows"]
    nc = len(rows[0])
    if any(len(r) != nc for r in rows):
        return {"ok": False, "error": "ряды раппорта разной длины", "notes": notes}
    shifts = pattern.get("row_shifts")
    if not shifts:
        shifts = [0.0 if j % 2 == 0 else 0.5 for j in range(len(rows))]
        pattern = dict(pattern, row_shifts=shifts)
    min_piece = float(req.get("min_piece", MIN_PIECE_MM))
    tiny_mode = str(req.get("tiny_mode") or "absorb")
    kerf = float(req.get("kerf", KERF_MM))
    ortho_tol = float(req.get("ortho_tol", 5.0))
    merge = req.get("merge_touching", True)
    merge_tol = float(req.get("merge_tol", MERGE_TOL))
    datum_spec = req.get("datum") or {"mode": DEFAULT_DATUM}
    MX = tile_w + gap_v
    half_w = None
    nz = [abs(sh) for sh in shifts if abs(sh) > 1e-9]
    if nz:
        half_w = tile_w - min(nz) * MX

    prepped = []
    zone_datum = {}
    for ci, c in enumerate(req.get("contours") or []):
        zone_id = str(c.get("id", ci + 1))
        pr = _prep_contour(c, ortho_tol, notes, zone_id)
        if pr is None:
            continue
        outer, holes = pr
        prepped.append({"id": zone_id, "outer": outer, "holes": holes})
        if c.get("datum"):
            zone_datum[zone_id] = c["datum"]
    if not prepped:
        return {"ok": False, "error": "нет пригодных зон/контуров для раскладки",
                "notes": notes}
    if merge:
        zones = merge_touching(prepped, merge_tol)
        for z in zones:
            if len(z["members"]) > 1:
                notes.append("смежные контуры %s объединены в одну плоскость "
                             "(общая сетка)" % ", ".join(z["members"]))
    else:
        zones = [{"id": z["id"], "outers": [z["outer"]], "holes": z["holes"],
                  "members": [z["id"]]} for z in prepped]

    pieces_all, per_zone = [], []
    for z in zones:
        zone_id = z["id"]
        region = OrthoRegion.from_rings(z["outers"], z["holes"])
        dspec = datum_spec
        for m in z["members"]:
            if m in zone_datum:
                dspec = zone_datum[m]
                break
        R, B = datum_for(z["outers"], dspec)
        pcs, absorbed = layout_zone(region, R, B, tile_w, tile_h, gap_v, gap_h,
                                    pattern, min_piece, tiny_mode)
        by_type = {}
        for p in pcs:
            p["zone"] = zone_id
            d = by_type.setdefault(p["type"], {"full": 0, "cut": 0, "tiny": 0,
                                               "half": 0, "area": 0.0,
                                               "area_cut": 0.0, "kinds": {}})
            d["area"] += p["area"]
            if p["full"]:
                d["full"] += 1
            else:
                d["cut"] += 1
                d["area_cut"] += p["area"]
                if p["tiny"]:
                    d["tiny"] += 1
                k = classify(p, tile_w, tile_h, half_w)
                if k == "половинка":
                    d["half"] += 1
                d["kinds"][k] = d["kinds"].get(k, 0) + 1
        # раскрой подрезки — по зоне и типу (режут по фасадам)
        for t, d in by_type.items():
            nest = nest_pieces([p for p in pcs if p["type"] == t], tile_w, tile_h, kerf)
            d["blanks_cut"] = nest["blanks"]
            d["blanks_by_kind"] = nest["by_kind"]
            d["tiles_total"] = d["full"] + nest["blanks"]
            d["area_blanks"] = d["tiles_total"] * tile_w * tile_h
            d["waste_area"] = d["area_blanks"] - d["area"]
        pieces_all.extend(pcs)
        n_full = sum(d["full"] for d in by_type.values())
        n_cut = sum(d["cut"] for d in by_type.values())
        per_zone.append({
            "zone_id": zone_id, "members": z["members"],
            "datum": {"x": R, "y": B},
            "area_zone": region.area, "area_tiles": sum(p["area"] for p in pcs),
            "full": n_full, "cut": n_cut,
            "tiny": sum(d["tiny"] for d in by_type.values()),
            "absorbed": absorbed,
            "blanks_cut": sum(d["blanks_cut"] for d in by_type.values()),
            "by_type": by_type,
        })
        if absorbed["count"]:
            notes.append("%s: полосок тоньше %g мм — %d (%.3f м²) поглощены рустами, "
                         "в раскладку не идут"
                         % (zone_id, min_piece, absorbed["count"], absorbed["area"] / 1e6))

    totals = {}
    for pz in per_zone:
        for t, d in pz["by_type"].items():
            tt = totals.setdefault(t, {"full": 0, "cut": 0, "tiny": 0, "half": 0,
                                       "area": 0.0, "area_cut": 0.0, "kinds": {},
                                       "blanks_cut": 0, "blanks_by_kind": {}})
            for k in ("full", "cut", "tiny", "half", "area", "area_cut", "blanks_cut"):
                tt[k] += d[k]
            for k, v in d["kinds"].items():
                tt["kinds"][k] = tt["kinds"].get(k, 0) + v
            for k, v in d["blanks_by_kind"].items():
                tt["blanks_by_kind"][k] = tt["blanks_by_kind"].get(k, 0) + v
    tile_area = tile_w * tile_h
    for t, tt in totals.items():
        tt["tiles_total"] = tt["full"] + tt["blanks_cut"]
        tt["area_blanks"] = tt["tiles_total"] * tile_area
        tt["waste_area"] = tt["area_blanks"] - tt["area"]
        tt["waste_pct"] = (100.0 * tt["waste_area"] / tt["area_blanks"]
                           if tt["area_blanks"] > 0 else 0.0)
        tt["tiles_by_area"] = int(math.ceil(tt["area"] / tile_area))
        # совместимость: прежнее поле blanks = tiles_total
        tt["blanks"] = tt["tiles_total"]
    absorbed_tot = {"count": sum(pz["absorbed"]["count"] for pz in per_zone),
                    "area": sum(pz["absorbed"]["area"] for pz in per_zone)}
    summary = {
        "zones": len(per_zone),
        "full": sum(t["full"] for t in totals.values()),
        "cut": sum(t["cut"] for t in totals.values()),
        "tiny": sum(t["tiny"] for t in totals.values()),
        "absorbed": absorbed_tot,
        "blanks_cut": sum(t["blanks_cut"] for t in totals.values()),
        "tiles_total": sum(t["tiles_total"] for t in totals.values()),
        "tiles_by_area": sum(t["tiles_by_area"] for t in totals.values()),
        "waste_area": sum(t["waste_area"] for t in totals.values()),
        "area_zones": sum(pz["area_zone"] for pz in per_zone),
        "area_tiles": sum(pz["area_tiles"] for pz in per_zone),
        "by_type": totals,
        "tile": {"w": tile_w, "h": tile_h}, "gap": {"v": gap_v, "h": gap_h},
        "module": {"x": MX, "y": tile_h + gap_h},
        "half_w": half_w, "kerf": kerf, "min_piece": min_piece,
        "tiny_mode": tiny_mode, "datum_mode": str(datum_spec.get("mode") or DEFAULT_DATUM),
        "pattern": {"rows": rows, "row_shifts": shifts},
    }
    summary["blanks"] = summary["tiles_total"]
    ab = summary["area_blanks"] = summary["tiles_total"] * tile_area
    summary["waste_pct"] = 100.0 * summary["waste_area"] / ab if ab > 0 else 0.0
    return {"ok": True, "pieces": pieces_all, "per_zone": per_zone,
            "summary": summary, "notes": notes}
