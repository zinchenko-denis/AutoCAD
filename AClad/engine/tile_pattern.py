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

УНИВЕРСАЛЬНАЯ РАЗБЕЖКА (23.09, заявка «шахматный порядок для любой
облицовки»: керамогранит любых форматов, фиброцемент, камень, клинкер).
Новые ключи запроса (все необязательны; без них — прежнее поведение
один в один, 91 юнит 09.09 — регресс):
- "bond": {"kind", "value", "units", "dir", "sequence"} — смещение рядов
  без образца: "none" — шов в шов; "alternate" — через ряд (0, s, 0,
  s …); "step" — лесенкой (ряд выше сдвинут на s относительно ряда
  ниже, накопительно); "sequence" — своя последовательность сдвигов
  рядов (повторяется; приводится к 0 у базового ряда); "pattern" —
  сдвиги из образца (как 09.09). value/sequence — доли МОДУЛЯ (плитка
  + шов: 1/2 = стык ряда ровно по центру плитки нижнего ряда) при
  units="frac" или мм при units="mm"; dir "+" — вправо (для столбцов
  — вверх), "-" — влево/вниз;
- "axis": "rows" (ряды горизонтальные, смещение по X) | "cols"
  (столбцы вертикальные, смещение по Y) — «cols» считается той же
  машиной в транспонированной плоскости (x↔y), результат
  транспонируется обратно;
- "anchor": {"h": L|C|R, "v": B|C|T, "center": tile|joint, "ref":
  bbox|wall, "point"?} — где стоит ЦЕЛАЯ плитка базового ряда: угол/
  середина габарита зоны (ref=bbox) или основной стены (ref=wall),
  либо общая точка (point) — общий горизонт/вертикаль всех зон;
  center: при C — плитка по оси или шов по оси;
- "gap_around": true — руст вокруг проёмов со всех сторон (ТЗ Германа
  1.5/П4 для кассет): проёмы расширяются на gap.v слева/справа и gap.h
  сверху/снизу, облицовка к проёму не прилипает;
- "shaped": "keep" (Г-куски фигурными, как 09.09) | "split" (на
  прямоугольники по линиям полос) | "split_joint" (на прямоугольники с
  рустом по продолжению грани проёма: у линии разреза более короткая
  полоса укорачивается на руст);
- "warn_cut": порог малой подрезки, мм — кусок, РЕЗАНЫЙ по ширине
  (высоте) уже порога, помечается small (предупреждение, не удаление);
- выход дополняется: pieces[].small/split, per_zone[].joints_x/rows_y
  (мост к ATFRAME: оси вертикальных швов = оси стоек, центры
  горизонтальных — кляммеры; считаются по фактическим стыкам кусков),
  summary.bond (разобранное смещение + текст), summary.small/trimmed.

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
                    # 23.09 (синтетика): интервалы, касающиеся друг друга
                    # (горизонтальный шов смежных контуров одной плоскости),
                    # сливаются — иначе подрезанная плитка через шов делилась
                    # на два куска (компоненты связности соединяют полосы
                    # только по X); для столбцов так выглядит ЛЮБОЙ
                    # вертикальный шов стен (транспонирование)
                    if ivs and abs(ivs[-1][1] - ys[m]) <= EPS:
                        ivs[-1] = (ivs[-1][0], ys[m + 1])
                    else:
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


def _sub_iv(ivs, a, b):
    """Y-интервалы ivs (отсортированные, непересекающиеся) минус [a, b]."""
    out = []
    for ya, yb in ivs:
        if yb <= a + EPS or ya >= b - EPS:
            out.append((ya, yb))
            continue
        if ya < a - EPS:
            out.append((ya, a))
        if yb > b + EPS:
            out.append((b, yb))
    return out


def region_subtract(region, cutters):
    """Новая зона = region минус объединение прямоугольников cutters
    [(x0, y0, x1, y1)] — полосы перестраиваются по X всех границ;
    используется для «руста вокруг проёмов» (проёмы, расширенные на
    руст, вычитаются из зоны без дыр)."""
    if not cutters or not region.xs:
        return region
    x_lo, x_hi = region.xs[0], region.xs[-1]
    xs = set(region.xs)
    for cx0, _cy0, cx1, _cy1 in cutters:
        for v in (cx0, cx1):
            if x_lo + EPS < v < x_hi - EPS:
                xs.add(v)
    xs = sorted(xs)
    new = OrthoRegion.__new__(OrthoRegion)
    new.xs = xs
    new.slabs = []
    new.area = 0.0
    for k in range(len(xs) - 1):
        xa, xb = xs[k], xs[k + 1]
        xm = 0.5 * (xa + xb)
        kb = bisect_right(region.xs, xm) - 1
        ivs = list(region.slabs[kb][2]) if 0 <= kb < len(region.slabs) else []
        for cx0, cy0, cx1, cy1 in cutters:
            if cx0 <= xa + EPS and cx1 >= xb - EPS:
                ivs = _sub_iv(ivs, cy0, cy1)
        ivs = [(a, b) for a, b in ivs if b - a > EPS]
        new.slabs.append((xa, xb, ivs))
        new.area += (xb - xa) * sum(b - a for a, b in ivs)
    new.bounds = region.bounds
    return new


def build_region(outers, holes, ex=0.0, ey=0.0):
    """Зона для раскладки: внешние контуры (чёт-нечёт — смежные контуры
    одной плоскости стыкуются по ребру) МИНУС объединение проёмов,
    расширенных на ex по X и ey по Y (руст вокруг проёмов; 0 — без
    руста). Вычитание, а не чёт-нечёт (23.09, синтетика): проём,
    вылезающий за контур стены, или два перекрывающихся проёма при
    чёт-нечёт давали облицовку СНАРУЖИ стены / внутри наложения
    проёмов; на корректных контурах результат тот же, что 09.09."""
    if not holes:
        return OrthoRegion.from_rings(outers, [])
    base = OrthoRegion.from_rings(outers, [])
    cutters = []
    for hring in holes:
        if len(hring) < 3:
            continue
        hr = OrthoRegion(hring)
        for x0, x1, ivs in hr.slabs:
            for ya, yb in ivs:
                cutters.append((x0 - ex, ya - ey, x1 + ex, yb + ey))
    return region_subtract(base, cutters)


def _snap_slot(a, b, bands, gap):
    """Принудительный руст [a, b], кликнутый у грани проёма (центр в
    пределах руста от центра полосы шва грани), совпадает со швом грани
    — иначе рядом легли бы два руста (двойной шов 1.5·руста; тот же
    урок, что у ATCLAD 23.07)."""
    c = 0.5 * (a + b)
    best = None
    for ba, bb in bands:
        d = abs(c - 0.5 * (ba + bb))
        if d <= gap + EPS and (best is None or d < best[0]):
            best = (d, ba, bb)
    return (best[1], best[2]) if best else (a, b)


def _sub_bounds(reg):
    """Фактический габарит зоны после вычитания (region_subtract хранит
    габарит исходной зоны — для участка он шире, раскладка перебирала бы
    лишние плитки)."""
    xs, ys = [], []
    for x0, x1, ivs in reg.slabs:
        if ivs:
            xs += [x0, x1]
            ys += [ivs[0][0], ivs[-1][1]]
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


def forced_cells(region, R, B, tw, th, s0, vslots, hslots, hole_boxes,
                 gv, gh, notes, zone_id):
    """ПРИНУДИТЕЛЬНЫЕ РУСТЫ (Герман 23.09: «как в ATCLAD», ТЗ 2.5/2.6).

    vslots — X-интервалы вертикальных рустов, hslots — Y-интервалы
    горизонтальных (рабочая плоскость; для столбцов транспонированы).
    Руст — граница участка: зона режется рустами на прямоугольные
    ячейки, руст не облицовывается. «Опорная» ячейка — та, где стоит
    базовая целая плитка (правило «Отсчёт» или общая точка); в ней фаза
    прежняя. В ячейках ЗА рустом раскладка начинается заново ОТ РУСТА,
    обращённого к опорной ячейке: целая плитка базового ряда вплотную к
    русту, подрезка приходит к дальней стороне. Для отсчёта «снизу» это
    ровно Г3 ATCLAD: выше горизонтального руста — ряды стандартной
    высоты, ниже — подрезка к русту.

    → None, если ни один руст не проходит через зону; иначе список
    (участок-зона, R, B) для layout_zone."""
    bx0, by0, bx1, by1 = region.bounds

    def prep(slots, lo, hi, bands, gap, what):
        out = []
        for a, b in slots:
            a, b = _snap_slot(a, b, bands, gap)
            if not (lo + EPS < a and b < hi - EPS):
                if a < hi and b > lo:
                    notes.append("%s: %s руст %.0f–%.0f у края зоны — не ставится"
                                 % (zone_id, what, a, b))
                continue
            if out and a <= out[-1][1] + EPS:
                out[-1] = (out[-1][0], max(out[-1][1], b))
            else:
                out.append((a, b))
        return out

    vb = [(hx0 - gv, hx0) for hx0, _y0, _x1, _y1 in hole_boxes] + \
         [(hx1, hx1 + gv) for _x0, _y0, hx1, _y1 in hole_boxes]
    hb = [(hy0 - gh, hy0) for _x0, hy0, _x1, _y1 in hole_boxes] + \
         [(hy1, hy1 + gh) for _x0, _y0, _x1, hy1 in hole_boxes]
    vs = prep(sorted(vslots), bx0, bx1, vb, gv, "вертикальный")
    hs = prep(sorted(hslots), by0, by1, hb, gh, "горизонтальный")
    if not vs and not hs:
        return None
    xc = [(bx0, vs[0][0])] if vs else [(bx0, bx1)]
    for k in range(len(vs)):
        xc.append((vs[k][1], vs[k + 1][0] if k + 1 < len(vs) else bx1))
    yc = [(by0, hs[0][0])] if hs else [(by0, by1)]
    for k in range(len(hs)):
        yc.append((hs[k][1], hs[k + 1][0] if k + 1 < len(hs) else by1))

    def pick(cells, v):
        for k, (a, b) in enumerate(cells):
            if a - EPS <= v <= b + EPS:
                return k
        return min(range(len(cells)),
                   key=lambda k: min(abs(v - cells[k][0]), abs(v - cells[k][1])))

    ia = pick(xc, R + s0 - 0.5 * tw)      # центр базовой целой плитки
    ja = pick(yc, B + 0.5 * th)
    big = 1e9
    out = []
    for ix, (cx0, cx1) in enumerate(xc):
        for iy, (cy0, cy1) in enumerate(yc):
            if cx1 - cx0 <= EPS or cy1 - cy0 <= EPS:
                continue
            sub = region_subtract(region, [(-big, -big, cx0, big), (cx1, -big, big, big),
                                           (-big, -big, big, cy0), (-big, cy1, big, big)])
            if sub.area <= EPS:
                continue
            bb = _sub_bounds(sub)
            if bb is None:
                continue
            sub.bounds = bb
            Rc = R if ix == ia else (cx0 + tw - s0 if ix > ia else cx1 - s0)
            Bc = B if iy == ja else (cy0 if iy > ja else cy1 - th)
            out.append((sub, Rc, Bc))
    notes.append("%s: принудительные русты — вертикальных %d, горизонтальных %d "
                 "(участков %d)" % (zone_id, len(vs), len(hs), len(out)))
    return out


def _split_rects(comp, joint=0.0):
    """Г/П-кусок (компонента прямоугольников полос) → прямоугольники.
    Соседние полосы с одинаковым Y-диапазоном склеиваются, линии разреза
    — вертикальные границы полос (в плоскости ряда: высота куска в
    пределах ряда не дробится). joint > 0 — у каждой линии разреза
    более КОРОТКАЯ из двух соседних полос укорачивается на joint (руст
    продолжает грань проёма/уступа; при равной высоте — правая)."""
    rs = sorted([list(r) for r in comp],
                key=lambda r: (round(r[1], 6), round(r[3], 6), r[0]))
    merged = []
    for r in rs:
        if merged:
            m = merged[-1]
            if abs(m[1] - r[1]) < EPS and abs(m[3] - r[3]) < EPS and \
               abs(m[2] - r[0]) < EPS:
                m[2] = r[2]
                continue
        merged.append(r)
    if joint <= EPS or len(merged) < 2:
        return [tuple(r) for r in merged]
    n = len(merged)
    trim_l = [0.0] * n
    trim_r = [0.0] * n
    for a in range(n):
        A = merged[a]
        for b in range(n):
            if a == b:
                continue
            Bq = merged[b]
            if abs(A[2] - Bq[0]) < EPS and \
               min(A[3], Bq[3]) - max(A[1], Bq[1]) > EPS:
                ha, hb = A[3] - A[1], Bq[3] - Bq[1]
                if ha < hb - EPS:
                    trim_r[a] = joint
                else:
                    trim_l[b] = joint
    out = []
    for k, r in enumerate(merged):
        x0, x1 = r[0] + trim_l[k], r[2] - trim_r[k]
        if x1 - x0 > EPS:
            out.append((x0, r[1], x1, r[3]))
    return out


def _small_flag(w, h, tile_w, tile_h, warn):
    """Малая подрезка: кусок РЕЗАН по ширине (высоте) и этот размер уже
    порога warn. Целый размер плитки меньше порога — не подрезка (кирпич
    82 мм при пороге 100 — не «малый»)."""
    if warn <= 0:
        return False
    cw = w < tile_w - 0.05
    ch = h < tile_h - 0.05
    return (cw and w < warn - 1e-9) or (ch and h < warn - 1e-9)


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


def zone_edges(outers, ref="bbox"):
    """Кромки зоны {"L","R","B","T"}: габарит (ref="bbox") или основная
    стена (ref="wall": доминирующие по суммарной длине рёбра наружной
    кромки; R/B — ровно datum_wall 09.09, L/T — зеркально)."""
    if outers and not isinstance(outers[0][0], (tuple, list)):
        outers = [outers]
    xs = [p[0] for o in outers for p in o]
    ys = [p[1] for o in outers for p in o]
    E = {"L": min(xs), "R": max(xs), "B": min(ys), "T": max(ys)}
    if ref != "wall":
        return E
    E["R"], E["B"] = datum_wall(outers)
    vsegs, hsegs = [], []
    for outer in outers:
        pts = outer if signed_area(outer) > 0 else outer[::-1]
        for (x1, y1), (x2, y2) in _edges(pts):
            dx, dy = x2 - x1, y2 - y1
            if abs(dx) < EPS and abs(dy) > EPS:
                vsegs.append((round(x1, 1), min(y1, y2), max(y1, y2),
                              1 if dy > 0 else -1))
            elif abs(dy) < EPS and abs(dx) > EPS:
                hsegs.append((round(y1, 1), min(x1, x2), max(x1, x2),
                              1 if dx > 0 else -1))
    left, top = {}, {}
    for x, lo, hi, d in _cancel(vsegs):
        if d < 0:
            left[x] = left.get(x, 0.0) + (hi - lo)
    for y, lo, hi, d in _cancel(hsegs):
        if d < 0:
            top[y] = top.get(y, 0.0) + (hi - lo)
    if left:
        best = max(left.values())
        kx = min(x for x, v in left.items() if v >= best - 1e-6)
        E["L"] = min((x for x in xs if abs(x - kx) <= 0.06),
                     key=lambda x: abs(x - kx), default=kx)
    if top:
        best = max(top.values())
        ky = max(y for y, v in top.items() if v >= best - 1e-6)
        E["T"] = min((y for y in ys if abs(y - ky) <= 0.06),
                     key=lambda y: abs(y - ky), default=ky)
    return E


def anchor_rb(outers, anchor, w, h, gv, gh):
    """anchor → (R, B): правый край базовой плитки и низ базового ряда
    (та же пара, что датум 09.09). Целая плитка базового ряда стоит в
    выбранном углу/середине габарита (стены) или в общей точке."""
    E = zone_edges(outers, str(anchor.get("ref") or "bbox"))
    pt = anchor.get("point")
    hx = str(anchor.get("h") or "R").upper()
    vy = str(anchor.get("v") or "B").upper()
    center = str(anchor.get("center") or "tile")
    px = py = None
    if pt:
        px, py = float(pt["x"]), float(pt["y"])
    if hx == "L":
        R = (px if pt else E["L"]) + w
    elif hx == "C":
        xc = px if pt else 0.5 * (E["L"] + E["R"])
        R = xc + 0.5 * w if center != "joint" else xc - 0.5 * gv
    else:
        R = px if pt else E["R"]
    if vy == "T":
        B = (py if pt else E["T"]) - h
    elif vy == "C":
        yc = py if pt else 0.5 * (E["B"] + E["T"])
        B = yc - 0.5 * h if center != "joint" else yc + 0.5 * gh
    else:
        B = py if pt else E["B"]
    return R, B


_TR = {"L": "B", "C": "C", "R": "T", "B": "L", "T": "R"}


def transpose_anchor(a):
    """Привязка в транспонированной плоскости (x↔y) для столбцов."""
    out = dict(a)
    out["h"] = _TR[str(a.get("v") or "B").upper()]
    out["v"] = _TR[str(a.get("h") or "R").upper()]
    if a.get("point"):
        out["point"] = {"x": float(a["point"]["y"]), "y": float(a["point"]["x"])}
    return out


def anchor_color_origin(anchor, nc, nr):
    """Какой элемент образца стоит в базовой плитке (i=0, j=0): справа —
    правый нижний (как 09.09), слева — левый, сверху — верхний ряд."""
    if not anchor:
        return nc - 1, 0
    hx = str(anchor.get("h") or "R").upper()
    vy = str(anchor.get("v") or "B").upper()
    c0 = 0 if hx == "L" else (nc // 2 if hx == "C" else nc - 1)
    r0 = nr - 1 if vy == "T" else (nr // 2 if vy == "C" else 0)
    return c0, r0


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


# ────────────────────────────────────────────── смещение рядов (bond)

BOND_KINDS = ("pattern", "none", "alternate", "step", "sequence")
_NEG_DIRS = ("-", "left", "down", "влево", "вниз", "l", "d")


def _parse_num(v):
    """Число из JSON/строки: 0.5, "1/3", "0,25", "200"."""
    if isinstance(v, bool):
        raise ValueError("не число: %r" % (v,))
    if isinstance(v, (int, float)):
        return float(v)
    t = str(v).strip().replace(",", ".").replace(" ", "")
    if t.lower().endswith("мм"):
        t = t[:-2]
    if "/" in t:
        a, b = t.split("/", 1)
        if float(b) == 0:
            raise ValueError("деление на ноль: %r" % (v,))
        return float(a) / float(b)
    return float(t)


def _frac(x):
    """Дробная часть в [0, 1); почти 0 и почти 1 → 0 (численный шум)."""
    f = x - math.floor(x)
    if f < 1e-9 or f > 1.0 - 1e-9:
        return 0.0
    return f


def frac_text(f):
    """0.5 → "1/2", 0.3333 → "1/3", 0.37 → "0.370"."""
    if abs(f) < 1e-9:
        return "0"
    for q in range(1, 13):
        p = int(round(f * q))
        if p and abs(p / float(q) - f) < 1e-6:
            g = math.gcd(p, q)
            return "%d/%d" % (p // g, q // g) if q // g > 1 else str(p // g)
    return "%.3f" % f


def resolve_bond(spec, shifts, module, r0=0, normalize=False):
    """Смещение рядов → (shift_of(j) → доля модуля [0, 1), info).

    spec None/kind="pattern" — сдвиги образца shifts[j mod NS] (как 09.09;
    normalize — привести к 0 у базового ряда r0, чтобы целая плитка
    стояла в точке отсчёта); иначе — см. докстринг модуля. module — шаг
    вдоль ряда (w + gap.v; для столбцов — h + gap.h) для перевода мм."""
    ns = len(shifts)
    notes = []
    if not spec or str(spec.get("kind") or "pattern") == "pattern":
        if normalize:
            base = shifts[r0 % ns]

            def fn(j):
                return _frac(shifts[(r0 + j) % ns] - base)
        else:
            def fn(j):
                return shifts[j % ns]
        info = {"kind": "pattern", "period": ns}
        return fn, _bond_finish(fn, info, module, notes)
    kind = str(spec.get("kind"))
    if kind not in BOND_KINDS:
        raise ValueError("неизвестный вид смещения %r (ожидается %s)"
                         % (kind, "|".join(BOND_KINDS)))
    d = spec.get("dir", "+")
    sign = -1.0 if (str(d).strip().lower() in _NEG_DIRS or
                    (isinstance(d, (int, float)) and not isinstance(d, bool)
                     and d < 0)) else 1.0
    units = str(spec.get("units") or "frac").lower()
    if units not in ("frac", "mm"):
        raise ValueError("единицы смещения: frac|mm (получено %r)" % units)

    def conv(v):
        x = _parse_num(v)
        return x / module if units == "mm" else x

    info = {"kind": kind, "dir": "+" if sign > 0 else "-", "units": units}
    if kind == "none":
        def fn(j):
            return 0.0
        info["period"] = 1
    elif kind in ("alternate", "step"):
        f = conv(spec.get("value", 0.5))
        if f < 0:
            f, sign = -f, -sign
            info["dir"] = "+" if sign > 0 else "-"
        if f >= 1.0 - 1e-9:
            notes.append("смещение не меньше модуля — берётся остаток "
                         "(%s модуля)" % frac_text(_frac(f)))
        f = _frac(f)
        if f <= 1e-9:
            notes.append("смещение 0 — ряды шов в шов")
        info["f"] = f
        if kind == "alternate":
            def fn(j):
                return 0.0 if j % 2 == 0 else _frac(sign * f)
            info["period"] = 2 if f > 1e-9 else 1
        else:
            def fn(j):
                return _frac(sign * j * f)
            period = None
            for p in range(1, 1001):
                if _frac(p * f + 1e-7) < 2e-7:
                    period = p
                    break
            info["period"] = period
    else:
        raw = spec.get("sequence") or []
        if isinstance(raw, str):
            raw = [t for t in raw.replace(",", ";").split(";") if t.strip()]
        seq = [conv(v) for v in raw]
        if not seq:
            raise ValueError("пустая последовательность смещений")
        if abs(_frac(seq[0])) > 1e-9:
            notes.append("первое смещение последовательности приведено к 0 "
                         "— базовый ряд начинается целой плиткой в точке "
                         "отсчёта")
        base = seq[0]
        seq = [_frac(v - base) for v in seq]
        n = len(seq)

        def fn(j):
            return _frac(sign * seq[j % n])
        info["sequence"] = seq
        info["period"] = n
    return fn, _bond_finish(fn, info, module, notes)


def _bond_finish(fn, info, module, notes):
    per = info.get("period") or 200
    js = range(min(per, 200))
    info["shifts"] = [round(fn(j), 6) for j in js][:24]
    ph = sorted(set(round(fn(j), 6) for j in js))
    # 0.999999 и 0 — одна фаза
    phases = []
    for v in ph:
        if v > 1 - 1e-6:
            v = 0.0
        if not any(abs(v - q) < 1e-6 for q in phases):
            phases.append(v)
    info["phases"] = sorted(phases)
    info["module"] = module
    info["notes"] = notes
    return info


def bond_text(info, axis="rows"):
    """Человеческое описание смещения для сводки/отчёта."""
    k = info.get("kind")
    cols = axis == "cols"
    what = "столбец правее" if cols else "ряд выше"
    ways = ("вверх", "вниз") if cols else ("вправо", "влево")
    way = ways[0] if info.get("dir", "+") == "+" else ways[1]
    mod = info.get("module") or 0.0
    per = info.get("period")
    per_t = ("период %d %s" % (per, "столбца" if cols and per in (2, 3, 4)
                               else "столбцов" if cols else
                               "ряда" if per in (2, 3, 4) else "рядов")
             if per else "рисунок не повторяется в пределах 1000 рядов")
    if k == "none":
        return "без смещения — швы в линию (шов в шов)"
    if k in ("alternate", "step"):
        f = info.get("f", 0.0)
        if f <= 1e-9:
            return "без смещения — швы в линию (шов в шов)"
        amt = "%s модуля (%.0f мм)" % (frac_text(f), f * mod)
        if k == "alternate":
            return "через ряд: каждый второй %s сдвинут %s на %s; %s" % (
                "столбец" if cols else "ряд", way, amt, per_t)
        return "лесенкой: каждый %s сдвинут %s на %s; %s" % (what, way, amt,
                                                             per_t)
    if k == "sequence":
        seq = info.get("sequence") or []
        return "своя последовательность (%s): %s модуля = %s мм; %s" % (
            way, "; ".join(frac_text(v) for v in seq),
            "; ".join("%.0f" % (v * mod) for v in seq), per_t)
    sh = info.get("shifts") or []
    return "по образцу: сдвиги рядов %s модуля; %s" % (
        "; ".join(frac_text(v) for v in sh[:8]) + (" …" if len(sh) > 8 else ""),
        per_t)


# ────────────────────────────────────────────── раскладка одной зоны


def layout_zone(region, R, B, tile_w, tile_h, gap_v, gap_h, pattern,
                min_piece=MIN_PIECE_MM, tiny_mode="absorb", shift_of=None,
                shaped="keep", warn_cut=0.0, color_col0=None, color_row0=0,
                stats=None):
    """Плитки одной зоны. Возвращает (pieces, absorbed):
    pieces — {"i","j","type","full","x","y","w","h","rect","tiny","area","rings"?}
    (rings — только у фигурных кусков; прямоугольные — x,y,w,h);
    absorbed — {"count","area"}: полоски тоньше min_piece, поглощённые
    рустами (tiny_mode="absorb") — в pieces их нет.

    23.09: shift_of(j) — сдвиг ряда j в долях модуля (дефолт —
    pattern.row_shifts[j mod NS], как 09.09); shaped — Г-куски "keep" |
    "split" | "split_joint" (см. _split_rects); warn_cut — флаг small
    (малая подрезка); color_col0/row0 — элемент образца в базовой
    плитке; stats — dict для {"trimmed": {"count","area"}} (площадь,
    снятая рустом при разрезе Г-кусков)."""
    MX = tile_w + gap_v
    MY = tile_h + gap_h
    rows = pattern["rows"]
    shifts = pattern.get("row_shifts") or [0.0, 0.5]
    nr = len(rows)
    nc = len(rows[0])
    ns = len(shifts)
    if shift_of is None:
        def shift_of(j):
            return shifts[j % ns]
    c0 = (nc - 1) if color_col0 is None else int(color_col0)
    trimmed = {"count": 0, "area": 0.0}
    if stats is not None:
        stats["trimmed"] = trimmed
    joint_split = gap_v if shaped == "split_joint" else 0.0
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
        s = shift_of(j) * MX
        row_pat = rows[(color_row0 + j) % nr]
        for i in range(i_min, i_max + 1):
            x1 = R - i * MX + s
            x0 = x1 - tile_w
            if x1 <= minx + EPS or x0 >= maxx - EPS:
                continue
            rects = region.clip_rect(x0, y0, x1, y1)
            if not rects:
                continue
            ttype = row_pat[(c0 - i) % nc]
            covered = sum((c - a) * (d - b) for a, b, c, d in rects)
            if abs(covered - full_area) <= AREA_TOL * full_area:
                pieces.append({"i": i, "j": j, "type": ttype, "full": True,
                               "x": x0, "y": y0, "w": tile_w, "h": tile_h,
                               "rect": True, "tiny": False, "area": full_area,
                               "small": False})
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
                if not is_rect and shaped in ("split", "split_joint"):
                    kept = 0.0
                    parts = _split_rects(comp, joint_split)
                    for px0, py0, px1, py1 in parts:
                        pw, ph = px1 - px0, py1 - py0
                        pa = pw * ph
                        if min(pw, ph) < NOISE_MM or pa < 1.0:
                            continue
                        kept += pa
                        ptiny = min(pw, ph) < min_piece
                        if ptiny and tiny_mode == "absorb":
                            absorbed["count"] += 1
                            absorbed["area"] += pa
                            continue
                        pieces.append({
                            "i": i, "j": j, "type": ttype, "full": False,
                            "x": px0, "y": py0, "w": pw, "h": ph,
                            "rect": True, "tiny": ptiny, "area": pa,
                            "split": True,
                            "small": _small_flag(pw, ph, tile_w, tile_h,
                                                 warn_cut)})
                    if area - kept > 1e-6:
                        trimmed["count"] += 1
                        trimmed["area"] += area - kept
                    continue
                if is_rect:
                    small = _small_flag(w, h, tile_w, tile_h, warn_cut)
                else:
                    small = any(_small_flag(r[2] - r[0], r[3] - r[1], tile_w,
                                            tile_h, warn_cut)
                                for r in _split_rects(comp, 0.0))
                piece = {"i": i, "j": j, "type": ttype, "full": False,
                         "x": bx0, "y": by0, "w": w, "h": h,
                         "rect": is_rect, "tiny": tiny, "area": area,
                         "small": small}
                if not is_rect:
                    # 23.09: 6 знаков (было 3) — кромки Г-куска совпадают с
                    # соседями точно (при нулевом русте 3 знака давали
                    # микроперекрытия 0.0003 мм — синтетика, I2)
                    piece["rings"] = [[[round(x, 6), round(y, 6)] for x, y in ring]
                                      for ring in union_outline(comp)]
                pieces.append(piece)
    return pieces, absorbed


def classify(piece, tile_w, tile_h, half_w, axis="rows"):
    """Вид куска для ведомости подрезки (axis="cols": половинка — по
    высоте, half_w тогда — «половинная» высота)."""
    if piece["full"]:
        return "целая"
    if not piece["rect"]:
        return "фигурная"
    w, h = piece["w"], piece["h"]
    if axis == "cols":
        if abs(w - tile_w) < 0.05 and half_w is not None and \
           abs(h - half_w) < 0.05:
            return "половинка"
    elif abs(h - tile_h) < 0.05 and half_w is not None and abs(w - half_w) < 0.05:
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
    """Касаются ли контуры a и b по ребру: общий вертикальный или
    горизонтальный отрезок длиной > tol при расстоянии между рёбрами
    ≤ tol И интерьеры по РАЗНЫЕ стороны шва (правая кромка одного —
    левая другого, верх одного — низ другого). 23.09 (синтетика):
    прежняя проверка без сторон считала «швом» и два нижних ребра на
    одной линии у ПЕРЕКРЫВАЮЩИХСЯ зон — они сливались, чёт-нечёт делал
    из наложения дыру. Возвращает список (axis, ca, cb): координаты
    стыкующихся рёбер, чтобы свести их к общему значению."""
    seams = []

    def oriented(ring):
        pts = ring if signed_area(ring) > 0 else ring[::-1]
        out = []
        for (x1, y1), (x2, y2) in _edges(pts):
            if abs(x1 - x2) < EPS and abs(y1 - y2) > EPS:
                # вверх (CCW) — правая кромка (+1), вниз — левая (−1)
                out.append(("v", x1, min(y1, y2), max(y1, y2), 1 if y2 > y1 else -1))
            elif abs(y1 - y2) < EPS and abs(x1 - x2) > EPS:
                # вправо — низ (+1), влево — верх (−1)
                out.append(("h", y1, min(x1, x2), max(x1, x2), 1 if x2 > x1 else -1))
        return out

    ea, eb = oriented(a), oriented(b)
    for ka, ca, lo_a, hi_a, sa in ea:
        for kb, cb, lo_b, hi_b, sb in eb:
            if ka != kb or sa != -sb or abs(ca - cb) > tol:
                continue
            if min(hi_a, hi_b) - max(lo_a, lo_b) > tol:
                seams.append(("x" if ka == "v" else "y", ca, cb))
    return seams


def overlap_notes(zones):
    """Перекрывающиеся зоны (облицовка ляжет дважды) — замечание.
    zones: [{"id","outers","holes"}] после объединения смежных."""
    out = []
    regs = []
    for z in zones:
        r = OrthoRegion.from_rings(z["outers"], [])
        regs.append((z["id"], r))
    for a in range(len(regs)):
        ida, ra = regs[a]
        ax0, ay0, ax1, ay1 = ra.bounds
        for b in range(a + 1, len(regs)):
            idb, rb = regs[b]
            bx0, by0, bx1, by1 = rb.bounds
            if min(ax1, bx1) - max(ax0, bx0) <= EPS or \
               min(ay1, by1) - max(ay0, by0) <= EPS:
                continue
            ov = 0.0
            for x0, x1, ivs in rb.slabs:
                for ya, yb in ivs:
                    ov += sum((c - p) * (d - q) for p, q, c, d in
                              ra.clip_rect(x0, ya, x1, yb))
            if ov > 1000.0:
                out.append("зоны %s и %s перекрываются (%.3f м²) — облицовка "
                           "ляжет дважды; проверьте контуры" % (ida, idb, ov / 1e6))
    return out


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


# ────────────────────────────────────────────── мост к ATFRAME

def bridge_axes(pieces, gap_v, gap_h, holes=(), gap_around=False, tol=0.05):
    """Оси вертикальных швов (joints_x — оси стоек подсистемы) и центры
    горизонтальных (rows_y — кляммеры) по ФАКТИЧЕСКИМ стыкам кусков:
    правая кромка + руст = левая кромка соседа с перекрытием по Y (и
    так же по вертикали) — как cladding_plan для ATCLAD. При разбежке
    это объединение осей всех рядов. С рустом вокруг проёмов — ещё оси
    боковых швов проёмов (анкерные швы, как в ATCLAD)."""
    by_x, by_y = {}, {}
    for p in pieces:
        by_x.setdefault(int(round(p["x"] * 10)), []).append(p)
        by_y.setdefault(int(round(p["y"] * 10)), []).append(p)
    jx, ry = set(), set()
    for p in pieces:
        xr = p["x"] + p["w"]
        k = int(round((xr + gap_v) * 10))
        hit = False
        for kk in (k - 1, k, k + 1):
            for q in by_x.get(kk, ()):
                if q is p:
                    continue
                if abs(q["x"] - (xr + gap_v)) <= tol and \
                   min(p["y"] + p["h"], q["y"] + q["h"]) - \
                   max(p["y"], q["y"]) > tol:
                    hit = True
                    break
            if hit:
                break
        if hit:
            jx.add(round(xr + 0.5 * gap_v, 2))
        yt = p["y"] + p["h"]
        k = int(round((yt + gap_h) * 10))
        hit = False
        for kk in (k - 1, k, k + 1):
            for q in by_y.get(kk, ()):
                if q is p:
                    continue
                if abs(q["y"] - (yt + gap_h)) <= tol and \
                   min(p["x"] + p["w"], q["x"] + q["w"]) - \
                   max(p["x"], q["x"]) > tol:
                    hit = True
                    break
            if hit:
                break
        if hit:
            ry.add(round(yt + 0.5 * gap_h, 2))
    if gap_around:
        for h in holes:
            if len(h) < 3:
                continue
            hx = [pt[0] for pt in h]
            jx.add(round(min(hx) - 0.5 * gap_v, 2))
            jx.add(round(max(hx) + 0.5 * gap_v, 2))
    return sorted(jx), sorted(ry)


def _tr_pts(pts):
    return [(float(p[1]), float(p[0])) for p in pts]


def _untranspose_piece(p):
    """Кусок из транспонированной плоскости (столбцы) обратно: x↔y,
    w↔h, кольца — с обратным порядком (снова против часовой)."""
    q = dict(p)
    q["x"], q["y"], q["w"], q["h"] = p["y"], p["x"], p["h"], p["w"]
    if "rings" in p:
        q["rings"] = [[[pt[1], pt[0]] for pt in ring][::-1]
                      for ring in p["rings"]]
    q["axis"] = "cols"
    return q


# ────────────────────────────────────────────── раскладка по зонам

def tile_pattern(req):
    """Раскладка по списку контуров (уже сгруппированных: outer+holes).

    req = {"tile": {"w","h"}, "gap": {"v","h"},
           "pattern": {"rows": [[type,...],...], "row_shifts": [...]},
           "datum": {"mode","x"?,"y"?}, "min_piece"?, "tiny_mode"?,
           "kerf"?, "merge_touching"?, "merge_tol"?, "ortho_tol"?,
           "axis"?, "bond"?, "anchor"?, "gap_around"?, "shaped"?,
           "warn_cut"?,                                  (23.09)
           "vjoints"?: [x, ...] — оси принудительных вертикальных рустов,
           "hjoints"?: [y, ...] — низ принудительных горизонтальных (23.09b),
           "contours": [{"id", "outer", "holes", "datum"?}, ...]}
    → {"ok", "pieces": [...+"zone"], "per_zone": [...], "summary", "notes"}."""
    notes = []
    try:
        tile_w = float(req["tile"]["w"])
        tile_h = float(req["tile"]["h"])
        gap = req.get("gap") or {}          # clad_engine передаёт None, если ключа нет
        gap_v = float(gap.get("v", 0.0))
        gap_h = float(gap.get("h", gap_v))
    except (KeyError, TypeError, ValueError, AttributeError):
        return {"ok": False, "error": "нет размеров плитки tile{w,h}/gap{v,h}",
                "notes": notes}
    if tile_w <= 0 or tile_h <= 0 or gap_v < 0 or gap_h < 0:
        return {"ok": False, "error": "размеры плитки/русты вне диапазона",
                "notes": notes}
    axis = str(req.get("axis") or "rows").lower()
    if axis not in ("rows", "cols"):
        return {"ok": False, "error": "axis: rows|cols (получено %r)" % axis,
                "notes": notes}
    cols = axis == "cols"
    new_api = bool(req.get("bond") or req.get("anchor") or cols)
    pattern = req.get("pattern")
    if not pattern or not pattern.get("rows"):
        pattern = default_pattern(req.get("types") or ["T1"])
        if not req.get("bond"):
            notes.append("образец не задан — разбежка полплиты одним типом")
    rows = pattern["rows"]
    nc = len(rows[0])
    if any(len(r) != nc for r in rows):
        return {"ok": False, "error": "ряды раппорта разной длины", "notes": notes}
    shifts = pattern.get("row_shifts")
    if not shifts:
        shifts = [0.0 if j % 2 == 0 else 0.5 for j in range(len(rows))]
        pattern = dict(pattern, row_shifts=shifts)
    try:
        min_piece = float(req.get("min_piece", MIN_PIECE_MM))
        kerf = float(req.get("kerf", KERF_MM))
        ortho_tol = float(req.get("ortho_tol", 5.0))
        merge_tol = float(req.get("merge_tol", MERGE_TOL))
        warn_cut = float(req.get("warn_cut") or 0.0)
    except (TypeError, ValueError):
        return {"ok": False, "error": "порог/пропил/допуск — не число",
                "notes": notes}
    tiny_mode = str(req.get("tiny_mode") or "absorb")
    merge = req.get("merge_touching", True)
    datum_spec = req.get("datum") or {"mode": DEFAULT_DATUM}
    shaped = str(req.get("shaped") or "keep")
    if shaped not in ("keep", "split", "split_joint"):
        return {"ok": False, "error": "shaped: keep|split|split_joint "
                "(получено %r)" % shaped, "notes": notes}
    gap_around = bool(req.get("gap_around", False))
    anchor = req.get("anchor") or None
    if anchor is not None:
        if not isinstance(anchor, dict) or \
           str(anchor.get("h") or "R").upper() not in ("L", "C", "R") or \
           str(anchor.get("v") or "B").upper() not in ("B", "C", "T"):
            return {"ok": False, "error": "anchor: h=L|C|R, v=B|C|T",
                    "notes": notes}
    # 23.09b (Герман): ПРИНУДИТЕЛЬНЫЕ русты, как в ATCLAD — vjoints: ось
    # вертикального руста (ТЗ 2.5), hjoints: НИЗ горизонтального (2.6/Г3)
    try:
        fvx = sorted(set(float(v) for v in (req.get("vjoints") or [])))
        fhy = sorted(set(float(v) for v in (req.get("hjoints") or [])))
    except (TypeError, ValueError):
        return {"ok": False, "error": "vjoints/hjoints — списки чисел", "notes": notes}
    # рабочая плоскость: для столбцов — транспонированная (x↔y)
    if cols:
        tw, th, gvw, ghw = tile_h, tile_w, gap_h, gap_v
    else:
        tw, th, gvw, ghw = tile_w, tile_h, gap_v, gap_h
    MXw = tw + gvw
    wanchor = transpose_anchor(anchor) if (cols and anchor) else anchor
    # слоты рустов в РЕАЛЬНОЙ плоскости → рабочая (столбцы: x↔y)
    rv = [(x - 0.5 * gap_v, x + 0.5 * gap_v) for x in fvx]
    rh = [(y, y + gap_h) for y in fhy]
    vslots_w, hslots_w = (rh, rv) if cols else (rv, rh)
    c0, r0 = anchor_color_origin(wanchor, nc, len(rows))
    try:
        shift_of, binfo = resolve_bond(req.get("bond"), shifts, MXw, r0,
                                       normalize=anchor is not None)
    except (ValueError, TypeError, ZeroDivisionError) as e:
        return {"ok": False, "error": "смещение рядов: %s" % e, "notes": notes}
    notes.extend(binfo.pop("notes", []))
    binfo["text"] = bond_text(binfo, axis)
    if new_api:
        nzp = [f for f in binfo["phases"] if f > 1e-9]
        half = (tw - min(nzp) * MXw) if nzp else None
    else:
        nz = [abs(sh) for sh in shifts if abs(sh) > 1e-9]
        half = (tile_w - min(nz) * (tile_w + gap_v)) if nz else None

    prepped = []
    zone_datum = {}
    for ci, c in enumerate(req.get("contours") or []):
        zone_id = str(c.get("id", ci + 1))
        cc = c
        if cols:
            cc = {"id": zone_id, "outer": _tr_pts(c.get("outer") or []),
                  "holes": [_tr_pts(h) for h in c.get("holes") or []]}
        pr = _prep_contour(cc, ortho_tol, notes, zone_id)
        if pr is None:
            continue
        outer, holes = pr
        prepped.append({"id": zone_id, "outer": outer, "holes": holes})
        if c.get("datum"):
            dd = dict(c["datum"])
            if cols:
                dd["x"], dd["y"] = dd.get("y"), dd.get("x")
            zone_datum[zone_id] = dd
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

    notes.extend(overlap_notes(zones))
    pieces_all, per_zone = [], []
    for z in zones:
        zone_id = z["id"]
        ex = gvw if gap_around else 0.0
        ey = ghw if gap_around else 0.0
        region = build_region(z["outers"], z["holes"], ex, ey)
        dspec = None
        for m in z["members"]:
            if m in zone_datum:
                dspec = zone_datum[m]
                break
        if anchor and dspec is None:
            R, B = anchor_rb(z["outers"], wanchor, tw, th, gvw, ghw)
        else:
            R, B = datum_for(z["outers"], dspec or datum_spec)
        st = {}
        cells = None
        if vslots_w or hslots_w:
            hbw = []
            for hring in z["holes"]:
                if len(hring) >= 3:
                    hbw.append((min(p[0] for p in hring), min(p[1] for p in hring),
                                max(p[0] for p in hring), max(p[1] for p in hring)))
            cells = forced_cells(region, R, B, tw, th, shift_of(0) * MXw,
                                 vslots_w, hslots_w, hbw, gvw, ghw, notes, zone_id)
        if cells is None:
            pcs, absorbed = layout_zone(region, R, B, tw, th, gvw, ghw,
                                        pattern, min_piece, tiny_mode, shift_of,
                                        shaped, warn_cut, c0, r0, st)
            trimmed = st.get("trimmed") or {"count": 0, "area": 0.0}
            zone_area = region.area
        else:
            pcs, absorbed = [], {"count": 0, "area": 0.0}
            trimmed = {"count": 0, "area": 0.0}
            zone_area = 0.0
            for sub, Rc, Bc in cells:
                stc = {}
                p_c, a_c = layout_zone(sub, Rc, Bc, tw, th, gvw, ghw, pattern,
                                       min_piece, tiny_mode, shift_of, shaped,
                                       warn_cut, c0, r0, stc)
                pcs.extend(p_c)
                absorbed["count"] += a_c["count"]
                absorbed["area"] += a_c["area"]
                tc = stc.get("trimmed") or {"count": 0, "area": 0.0}
                trimmed["count"] += tc["count"]
                trimmed["area"] += tc["area"]
                zone_area += sub.area
        holes_real = z["holes"]
        if cols:
            pcs = [_untranspose_piece(p) for p in pcs]
            holes_real = [_tr_pts(h) for h in z["holes"]]
            datum_real = {"x": B + th, "y": R - tw}
            origin = {"x": B, "y": R - tw}
        else:
            datum_real = {"x": R, "y": B}
            origin = {"x": R - tw, "y": B}
        by_type = {}
        for p in pcs:
            p["zone"] = zone_id
            d = by_type.setdefault(p["type"], {"full": 0, "cut": 0, "tiny": 0,
                                               "half": 0, "area": 0.0,
                                               "area_cut": 0.0, "kinds": {},
                                               "small": 0})
            d["area"] += p["area"]
            if p["full"]:
                d["full"] += 1
            else:
                d["cut"] += 1
                d["area_cut"] += p["area"]
                if p["tiny"]:
                    d["tiny"] += 1
                if p.get("small"):
                    d["small"] += 1
                k = classify(p, tile_w, tile_h, half, axis)
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
        n_small = sum(d["small"] for d in by_type.values())
        jx, ry = bridge_axes(pcs, gap_v, gap_h, holes_real, gap_around)
        per_zone.append({
            "zone_id": zone_id, "members": z["members"],
            "datum": datum_real, "origin": origin,
            "area_zone": zone_area, "area_tiles": sum(p["area"] for p in pcs),
            "full": n_full, "cut": n_cut,
            "tiny": sum(d["tiny"] for d in by_type.values()),
            "small": n_small, "trimmed": trimmed,
            "absorbed": absorbed,
            "blanks_cut": sum(d["blanks_cut"] for d in by_type.values()),
            "by_type": by_type,
            "joints_x": jx, "rows_y": ry,
            "forced": {"vjoints": [x for x in fvx], "hjoints": [y for y in fhy]}
            if cells is not None else None,
        })
        if absorbed["count"]:
            notes.append("%s: полосок тоньше %g мм — %d (%.3f м²) поглощены рустами, "
                         "в раскладку не идут"
                         % (zone_id, min_piece, absorbed["count"], absorbed["area"] / 1e6))
        if n_small:
            notes.append("%s: малая подрезка (резаный размер < %g мм) — %d шт"
                         % (zone_id, warn_cut, n_small))
        if trimmed["count"]:
            notes.append("%s: Г-кусков разрезано рустом по продолжению грани — %d"
                         % (zone_id, trimmed["count"]))

    totals = {}
    for pz in per_zone:
        for t, d in pz["by_type"].items():
            tt = totals.setdefault(t, {"full": 0, "cut": 0, "tiny": 0, "half": 0,
                                       "area": 0.0, "area_cut": 0.0, "kinds": {},
                                       "blanks_cut": 0, "blanks_by_kind": {},
                                       "small": 0})
            for k in ("full", "cut", "tiny", "half", "area", "area_cut",
                      "blanks_cut", "small"):
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
    trimmed_tot = {"count": sum(pz["trimmed"]["count"] for pz in per_zone),
                   "area": sum(pz["trimmed"]["area"] for pz in per_zone)}
    n_ax = len(binfo["phases"]) if not cols else 1
    if new_api and not cols and n_ax >= 2:
        notes.append("вертикальные швы при разбежке — %d оси на модуль %.0f мм "
                     "(шаг ≈ %.0f мм); для НВФ это оси стоек подсистемы "
                     "(ATFRAME берёт их из метки раскладки)"
                     % (n_ax, MXw, MXw / n_ax))
    if new_api and cols and len(binfo["phases"]) >= 2:
        notes.append("разбежка столбцов: горизонтальные швы соседних столбцов "
                     "на разных отметках (%d фазы на модуль %.0f мм)"
                     % (len(binfo["phases"]), MXw))
    summary = {
        "zones": len(per_zone),
        "full": sum(t["full"] for t in totals.values()),
        "cut": sum(t["cut"] for t in totals.values()),
        "tiny": sum(t["tiny"] for t in totals.values()),
        "small": sum(t["small"] for t in totals.values()),
        "absorbed": absorbed_tot,
        "trimmed": trimmed_tot,
        "blanks_cut": sum(t["blanks_cut"] for t in totals.values()),
        "tiles_total": sum(t["tiles_total"] for t in totals.values()),
        "tiles_by_area": sum(t["tiles_by_area"] for t in totals.values()),
        "waste_area": sum(t["waste_area"] for t in totals.values()),
        "area_zones": sum(pz["area_zone"] for pz in per_zone),
        "area_tiles": sum(pz["area_tiles"] for pz in per_zone),
        "by_type": totals,
        "tile": {"w": tile_w, "h": tile_h}, "gap": {"v": gap_v, "h": gap_h},
        "module": {"x": tile_w + gap_v, "y": tile_h + gap_h},
        "half_w": half, "kerf": kerf, "min_piece": min_piece,
        "tiny_mode": tiny_mode, "datum_mode": str(datum_spec.get("mode") or DEFAULT_DATUM),
        "pattern": {"rows": rows,
                    "row_shifts": binfo["shifts"] if new_api else shifts},
        "axis": axis, "bond": binfo, "anchor": anchor,
        "gap_around": gap_around, "shaped": shaped, "warn_cut": warn_cut,
        "joint_axes_per_module": n_ax,
    }
    summary["blanks"] = summary["tiles_total"]
    ab = summary["area_blanks"] = summary["tiles_total"] * tile_area
    summary["waste_pct"] = 100.0 * summary["waste_area"] / ab if ab > 0 else 0.0
    return {"ok": True, "pieces": pieces_all, "per_zone": per_zone,
            "summary": summary, "notes": notes}
