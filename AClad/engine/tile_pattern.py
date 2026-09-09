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
- полоски: кусок с меньшим габаритом < min_piece (дефолт 10 мм) —
  флаг tiny (отдельный слой в чертеже, в счёт плиток не входит: на
  объекте поглощается рустами); < 0.1 мм — численный мусор, кусок
  отбрасывается; заготовки: 1 плитка на кусок, половинки полной
  высоты шириной w − |s|·MX парами (2 из одной плитки).

Только stdlib.
"""
from bisect import bisect_left, bisect_right
import math

from cladding_plan import _closed, _edges, _is_ortho, _ortho_err, \
    _ortho_snap, EPS

NOISE_MM = 0.1        # кусок тоньше — численный мусор (ребро зоны на линии сетки)
MIN_PIECE_MM = 10.0   # дефолт порога «полоски»
AREA_TOL = 1e-6       # относительный допуск «плитка покрыта целиком»


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
        rings = [list(outer)] + [list(h) for h in holes]
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
        ally = [y for _x, y in outer]
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

def datum_wall(outer):
    """Правый нижний угол основной стены: доминирующее (по суммарной
    длине) правое вертикальное ребро и нижнее горизонтальное ребро
    внешнего контура. Значения — точные координаты рёбер."""
    pts = outer if signed_area(outer) > 0 else outer[::-1]   # CCW: интерьер слева
    # бакеты по 0.1 мм: рёбра одной стены после _ortho_snap могут
    # различаться на сотые мм и НЕ должны попадать в разные бакеты
    # (иначе длинная нижняя грань стены дробится и проигрывает «ножке»).
    right, bottom = {}, {}
    for (x1, y1), (x2, y2) in _edges(pts):
        dx, dy = x2 - x1, y2 - y1
        if abs(dx) < EPS and dy > 0:            # вверх → интерьер слева → правая кромка
            k = round(x1, 1)
            tot, ex = right.get(k, (0.0, x1))
            right[k] = (tot + dy, ex)
        elif abs(dy) < EPS and dx > 0:          # вправо → интерьер сверху → низ
            k = round(y1, 1)
            tot, ex = bottom.get(k, (0.0, y1))
            bottom[k] = (tot + dx, ex)
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    R = max(right.values(), key=lambda v: v[0])[1] if right else max(xs)
    B = max(bottom.values(), key=lambda v: v[0])[1] if bottom else min(ys)
    return R, B


def datum_for(outer, spec):
    """spec: {"mode": "wall"|"bbox"|"point", "x"?, "y"?} → (R, B)."""
    spec = spec or {}
    mode = str(spec.get("mode") or "wall")
    xs = [p[0] for p in outer]
    ys = [p[1] for p in outer]
    if mode == "bbox":
        R, B = max(xs), min(ys)
    elif mode == "point":
        R, B = float(spec.get("x", max(xs))), float(spec.get("y", min(ys)))
    else:
        R, B = datum_wall(outer)
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
                min_piece=MIN_PIECE_MM):
    """Плитки одной зоны. Возвращает список кусков:
    {"i","j","type","full","x","y","w","h","rect","tiny","area","rings"?}
    (rings — только у фигурных кусков; для прямоугольных x,y,w,h)."""
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
                is_rect = len(comp) == 1 or abs(area - w * h) <= AREA_TOL * w * h
                piece = {"i": i, "j": j, "type": ttype, "full": False,
                         "x": bx0, "y": by0, "w": w, "h": h,
                         "rect": is_rect, "tiny": min(w, h) < min_piece,
                         "area": area}
                if not is_rect:
                    piece["rings"] = [[[round(x, 3), round(y, 3)] for x, y in ring]
                                      for ring in union_outline(comp)]
                pieces.append(piece)
    return pieces


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
    snapped = _quantize(snapped, max(ortho_tol * 0.2, 0.05))
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


def tile_pattern(req):
    """Раскладка по списку контуров (уже сгруппированных: outer+holes).

    req = {"tile": {"w","h"}, "gap": {"v","h"},
           "pattern": {"rows": [[type,...],...], "row_shifts": [...]},
           "datum": {"mode","x"?,"y"?}, "min_piece"?, "ortho_tol"?,
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
    ortho_tol = float(req.get("ortho_tol", 5.0))
    datum_spec = req.get("datum") or {"mode": "wall"}
    MX = tile_w + gap_v
    half_w = None
    nz = [abs(s) for s in shifts if abs(s) > 1e-9]
    if nz:
        half_w = tile_w - min(nz) * MX

    pieces_all, per_zone = [], []
    for ci, c in enumerate(req.get("contours") or []):
        zone_id = str(c.get("id", ci + 1))
        prepped = _prep_contour(c, ortho_tol, notes, zone_id)
        if prepped is None:
            continue
        outer, holes = prepped
        region = OrthoRegion(outer, holes)
        R, B = datum_for(outer, c.get("datum") or datum_spec)
        pcs = layout_zone(region, R, B, tile_w, tile_h, gap_v, gap_h,
                          pattern, min_piece)
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
        pieces_all.extend(pcs)
        n_full = sum(d["full"] for d in by_type.values())
        n_cut = sum(d["cut"] for d in by_type.values())
        n_tiny = sum(d["tiny"] for d in by_type.values())
        per_zone.append({
            "zone_id": zone_id, "datum": {"x": R, "y": B},
            "area_zone": region.area, "area_tiles": sum(p["area"] for p in pcs),
            "full": n_full, "cut": n_cut, "tiny": n_tiny,
            "by_type": by_type,
        })
        if n_tiny:
            notes.append("%s: полосок тоньше %g мм — %d (слой полосок, в счёт не идут)"
                         % (zone_id, min_piece, n_tiny))

    if not per_zone:
        return {"ok": False, "error": "нет пригодных зон/контуров для раскладки",
                "notes": notes}

    totals = {}
    for pz in per_zone:
        for t, d in pz["by_type"].items():
            tt = totals.setdefault(t, {"full": 0, "cut": 0, "tiny": 0, "half": 0,
                                       "area": 0.0, "area_cut": 0.0, "kinds": {}})
            for k in ("full", "cut", "tiny", "half", "area", "area_cut"):
                tt[k] += d[k]
            for k, v in d["kinds"].items():
                tt["kinds"][k] = tt["kinds"].get(k, 0) + v
    for t, tt in totals.items():
        tt["blanks"] = tt["full"] + (tt["cut"] - tt["tiny"] - tt["half"]) + \
            int(math.ceil(tt["half"] / 2.0))
    summary = {
        "zones": len(per_zone),
        "full": sum(t["full"] for t in totals.values()),
        "cut": sum(t["cut"] for t in totals.values()),
        "tiny": sum(t["tiny"] for t in totals.values()),
        "blanks": sum(t["blanks"] for t in totals.values()),
        "area_zones": sum(pz["area_zone"] for pz in per_zone),
        "area_tiles": sum(pz["area_tiles"] for pz in per_zone),
        "by_type": totals,
        "tile": {"w": tile_w, "h": tile_h}, "gap": {"v": gap_v, "h": gap_h},
        "module": {"x": MX, "y": tile_h + gap_h},
        "half_w": half_w,
        "pattern": {"rows": rows, "row_shifts": shifts},
    }
    return {"ok": True, "pieces": pieces_all, "per_zone": per_zone,
            "summary": summary, "notes": notes}
