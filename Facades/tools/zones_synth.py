# -*- coding: utf-8 -*-
"""Синтетические испытания движка зон ATFZONE (facade_zones /
facades_engine, ревью 23.09) против НЕЗАВИСИМОГО оракула на shapely.

Оракул не использует функций движка: дуги (bulge) — своя формула
центра/радиуса и мелкая дискретизация (≥720 хорд на полный круг),
площади/периметры/разность — shapely, кромки проёмов — пробой точками по
сторонам мелкого отрезка (а не направлением обхода, как движок).

Сценарии: стены — прямоугольник, Г, ступени, П, фронтон (скаты), арочный
верх, случайный звёздный многоугольник; проёмы — окна, двери в пол, окна
у бокового края, арочные, круглые, трапеции; обход CW/CCW, случайная
первая вершина, дубли вершин, хвост-дубль и микрозазор хвоста ≤0.5 мм,
единицы мм|м, сдвиг чертежа. Плюс плохие входы: «бабочка», проём за
контуром, наложение проёмов, контур внутри проёма.

Инварианты:
 Z1 площадь брутто; Z2 периметр; Z3 нетто = брутто − проёмы (разность
    shapely — ловит и наложения, пропущенные валидацией);
 Z4 отливы (низ проёма); Z5 откосы (верх+бока); Z6 порог (кромка на
    границе зоны); Z7 площадь и периметр каждого проёма;
 Z8 валидация: ошибки движка = ожидаемые (и нет лишних);
 Z9 CLI facades_engine (как C#: плоский список контуров) = прямой вызов;
    merge=true — одна зона, суммы = сумме частей;
 Z10 обход вершин, Z11 единицы м↔мм, Z12 сдвиг — отчёт не меняется;
 Z13 группировка: зоны = контуры верхнего уровня (shapely), проёмы —
    вложенные 1-го уровня, глубже — E_NESTED_DEEP;
 Z14 нумерация «снизу вверх, справа налево» (ряды по низу зоны).

Запуск (из корня): PYTHONUTF8=1 python3 Facades/tools/zones_synth.py
  [--seed N] [--n N] [--quick] [--dump DIR]. Выход ≠ 0 при нарушениях.
"""
import argparse
import json
import math
import os
import random
import sys
import time
from collections import Counter, defaultdict

from shapely.geometry import LineString, Point, Polygon
from shapely.ops import unary_union

HERE = os.path.dirname(os.path.abspath(__file__))
ENGINE = os.path.normpath(os.path.join(HERE, "..", "engine"))
sys.path.insert(0, ENGINE)
import facade_zones as fz      # noqa: E402
import facades_engine as fe    # noqa: E402

SCEN, ARC_OPEN, INFO, UNITS_ARC, SHIFT_ARC, UNITS = {}, [], [], [], [], []
TOL_A = 2e-4      # м²
TOL_L = 2e-3      # м
REL = 2e-4


# ── оракул дуг ───────────────────────────────────────────────────────
def arc_points(p1, p2, b, per_rad=115.0):
    """Точки дуги p1→p2 с bulge b (tan(θ/4), + = против часовой), БЕЗ p1."""
    th = 4.0 * math.atan(b)
    (x1, y1), (x2, y2) = p1, p2
    dx, dy = x2 - x1, y2 - y1
    c = math.hypot(dx, dy)
    mx, my = (x1 + x2) / 2.0, (y1 + y2) / 2.0
    d = (c / 2.0) / math.tan(th / 2.0)
    cx, cy = mx - dy / c * d, my + dx / c * d
    r = math.hypot(x1 - cx, y1 - cy)
    a1 = math.atan2(y1 - cy, x1 - cx)
    n = max(16, int(abs(th) * per_rad))
    return [(cx + r * math.cos(a1 + th * k / n), cy + r * math.sin(a1 + th * k / n))
            for k in range(1, n + 1)]


def ring(pts, bulges=None):
    """Замкнутый контур с дугами → плотный список точек (без повтора первой)."""
    out = []
    n = len(pts)
    for i in range(n):
        p1, p2 = tuple(pts[i]), tuple(pts[(i + 1) % n])
        out.append(p1)
        b = (bulges or [0.0] * n)[i] if bulges else 0.0
        if abs(b) > 1e-12 and math.hypot(p2[0] - p1[0], p2[1] - p1[1]) > 1e-9:
            out += arc_points(p1, p2, b)[:-1]
    return out


def poly_of(c):
    return Polygon(ring(c["pts"], c.get("bulges")))


def edge_classes(op_ring, outer_poly, delta=0.05):
    """Кромки проёма: пробой точками по сторонам каждого отрезка."""
    op_poly = Polygon(op_ring)
    # «на границе» = в пределах 0.5 мм (допуск касаний движка GEO_TOL) и на длине > 20 мм:
    # наклонная кромка, касающаяся границы углом, даёт лишь миллиметры — это не порог
    bnd_b = outer_poly.exterior.buffer(0.5)
    out = Counter()
    n = len(op_ring)
    for i in range(n):
        a, c = op_ring[i], op_ring[(i + 1) % n]
        L = math.hypot(c[0] - a[0], c[1] - a[1])
        if L < 1e-9:
            continue
        m = ((a[0] + c[0]) / 2.0, (a[1] + c[1]) / 2.0)
        # часть кромки, лежащая НА границе зоны (не «всё или ничего» по концам)
        on_b = LineString([a, c]).intersection(bnd_b).length
        if on_b > 20.0:
            out["boundary"] += min(on_b, L)
            L -= min(on_b, L)
            if L <= 1.0:
                continue
        dx, dy = c[0] - a[0], c[1] - a[1]
        if abs(dy) <= abs(dx):
            up_in = op_poly.contains(Point(m[0], m[1] + delta))
            out["bottom" if up_in else "top"] += L
        else:
            out["sides"] += L
    return out


def oracle(zone_c, opening_cs, unit_k=1.0):
    """Отчёт оракула в м²/м для зоны (контуры в мм·unit_k)."""
    s = unit_k / 1000.0
    wall = poly_of(zone_c)
    ops = [poly_of(o) for o in opening_cs]
    rep = {"area_outer_m2": wall.area * s * s, "perimeter_outer_m": wall.exterior.length * s,
           "openings_total_m2": sum(o.area for o in ops) * s * s,
           "openings_perimeter_total_m": sum(o.exterior.length for o in ops) * s,
           "area_net_m2": wall.difference(unary_union(ops)).area * s * s if ops else wall.area * s * s,
           "sills_total_m": 0.0, "jambs_total_m": 0.0, "on_boundary_total_m": 0.0, "per": []}
    for o, oc in zip(ops, opening_cs):
        e = edge_classes(ring(oc["pts"], oc.get("bulges")), wall)
        rep["sills_total_m"] += e["bottom"] * s
        rep["jambs_total_m"] += (e["top"] + e["sides"]) * s
        rep["on_boundary_total_m"] += e["boundary"] * s
        rep["per"].append((o.area * s * s, o.exterior.length * s))
    return rep


# ── сценарии ─────────────────────────────────────────────────────────
def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def outer_shape(rng, fam):
    W = rng.randrange(4000, 20000, 10)
    H = rng.randrange(3000, 16000, 10)
    if fam == "rect":
        return {"pts": rect(0, 0, W, H)}
    if fam == "L":
        a, b = rng.randrange(1500, W - 1500, 10), rng.randrange(1500, H - 1200, 10)
        return {"pts": [[0, 0], [W, 0], [W, H], [a, H], [a, b], [0, b]]}
    if fam == "step":
        a, b = rng.randrange(1500, W // 2, 10), rng.randrange(W // 2 + 300, W - 1200, 10)
        h1, h2 = rng.randrange(1500, H // 2, 10), rng.randrange(H // 2 + 200, H - 300, 10)
        return {"pts": [[0, 0], [W, 0], [W, H], [b, H], [b, h2], [a, h2], [a, h1], [0, h1]]}
    if fam == "U":
        a, c = rng.randrange(1200, W // 2 - 300, 10), rng.randrange(W // 2 + 300, W - 1200, 10)
        b = rng.randrange(1500, H - 1200, 10)
        return {"pts": [[0, 0], [W, 0], [W, H], [c, H], [c, b], [a, b], [a, H], [0, H]]}
    if fam == "gable":
        e = rng.randrange(2000, H - 1000, 10)
        return {"pts": [[0, 0], [W, 0], [W, e], [W / 2.0, H], [0, e]]}
    if fam == "arch":           # арочный верх: дуга на верхнем ребре (обход против часовой)
        e = rng.randrange(2000, H - 500, 10)
        b = rng.uniform(0.05, 0.6)
        return {"pts": [[0, 0], [W, 0], [W, e], [0, e]], "bulges": [0, 0, b, 0]}
    if fam == "star":
        cx, cy = W / 2.0, H / 2.0
        k = rng.randrange(5, 12)
        angs = sorted(rng.uniform(0, 2 * math.pi) for _ in range(k))
        return {"pts": [[round(cx + math.cos(a) * W / 2 * rng.uniform(0.45, 1.0), 3),
                         round(cy + math.sin(a) * H / 2 * rng.uniform(0.45, 1.0), 3)] for a in angs]}
    raise ValueError(fam)


def openings_for(rng, wall, fam):
    x0, y0, x1, y1 = wall.bounds
    out, polys = [], []
    for _ in range(rng.randrange(0, 6) * 3):
        if len(out) >= 5:
            break
        w, h = rng.randrange(400, 2600, 10), rng.randrange(400, 2600, 10)
        kind = rng.random()
        wx = rng.uniform(x0, max(x0 + 1, x1 - w))
        wy = rng.uniform(y0, max(y0 + 1, y1 - h))
        if kind < 0.15:
            wy = y0                                            # дверь в пол
        elif kind < 0.25:
            wx = x0 if rng.random() < 0.5 else x1 - w          # у бокового края
        wx, wy = round(wx, 1), round(wy, 1)
        shape = rng.random()
        if shape < 0.55:
            c = {"pts": rect(wx, wy, wx + w, wy + h)}
        elif shape < 0.72:                                     # арочное окно
            c = {"pts": rect(wx, wy, wx + w, wy + h), "bulges": [0, 0, rng.uniform(0.1, 1.0), 0]}
        elif shape < 0.85:                                     # круглое
            r = min(w, h) / 2.0
            c = {"pts": [[wx, wy + r], [wx + 2 * r, wy + r]], "bulges": [1.0, 1.0]}
        else:                                                  # трапеция
            d = rng.uniform(0.05, 0.3) * w
            c = {"pts": [[wx, wy], [wx + w, wy], [wx + w - d, wy + h], [wx + d, wy + h]]}
        if len(c["pts"]) == 2:          # круг: две полуокружности — движок хочет ≥3 вершин
            (ax, ay), (bx, by) = c["pts"]
            r = (bx - ax) / 2.0
            c = {"pts": [[ax, ay], [ax + r, ay - r], [bx, by], [ax + r, ay + r]],
                 "bulges": [math.tan(math.pi / 8)] * 4}
        p = poly_of(c)
        if not p.is_valid or not wall.buffer(-1.0).contains(p.buffer(-2.0)):
            continue
        if not wall.buffer(0.01).contains(p):
            continue
        if any(p.intersection(q).area > 1.0 for q in polys):
            continue
        out.append(c)
        polys.append(p)
    return out


def perturb(rng, c, reverse=None, start=None, dup=False, tail=None):
    """Обход, первая вершина, дубли, хвост — не должны менять ответ."""
    pts = [list(p) for p in c["pts"]]
    bul = list(c.get("bulges") or [0.0] * len(pts))
    rev = rng.random() < 0.5 if reverse is None else reverse
    if rev:   # обратный обход: вершины назад, bulge ребра i→i+1 = −bulge ребра i+1→i
        n = len(pts)
        pts2 = [pts[(-i) % n] for i in range(n)]
        bul2 = [-bul[(-i - 1) % n] for i in range(n)]
        pts, bul = pts2, bul2
    k = rng.randrange(len(pts)) if start is None else start
    pts, bul = pts[k:] + pts[:k], bul[k:] + bul[:k]
    if dup and len(pts) > 3:
        j = rng.randrange(len(pts))
        pts.insert(j + 1, list(pts[j]))
        bul.insert(j, 0.0)       # ребро j→дубль нулевое; дуга остаётся на дубль→j+1
    if tail == "dup":
        pts.append(list(pts[0]))
        bul.append(0.0)
    elif tail == "gap":
        pts.append([pts[0][0] + 0.3, pts[0][1] - 0.2])
        bul.append(0.0)
    out = {"pts": pts}
    if any(abs(b) > 1e-12 for b in bul):
        out["bulges"] = bul
    return out


# ── проверки ─────────────────────────────────────────────────────────
def near(a, b, tol):
    return abs(a - b) <= tol + REL * max(abs(a), abs(b))


def cmp_report(tag, eng, ora, bad, arc_tol=0.0):
    """arc_tol — допуск на классификацию ДУГ (движок делит дугу хордами со
    стрелкой 0.5 мм и относит хорду к низу/верху/боку целиком: на переходе
    45° ошибка до хорды). Для прямых кромок допуск — миллиметры."""
    for key, code, tol in (("area_outer_m2", "Z1", TOL_A), ("perimeter_outer_m", "Z2", TOL_L),
                           ("area_net_m2", "Z3", TOL_A), ("sills_total_m", "Z4", TOL_L),
                           ("jambs_total_m", "Z5", TOL_L), ("on_boundary_total_m", "Z6", TOL_L),
                           ("openings_total_m2", "Z7", TOL_A),
                           ("openings_perimeter_total_m", "Z7", TOL_L)):
        if arc_tol and code in ("Z4", "Z5", "Z6"):
            continue            # дуги: классификация хордами — см. INFO «дуги»
        if not near(eng[key], ora[key], tol):
            bad.append((code, "%s: %s движок %.4f, оракул %.4f" % (tag, key, eng[key], ora[key])))


def engine_report(zone_c, ops, units="mm", k=1.0):
    contours = [dict(zone_c, id="Z")] + [dict(o, id="O%d" % i) for i, o in enumerate(ops)]
    if k != 1.0:
        contours = [dict(c, pts=[[p[0] * k, p[1] * k] for p in c["pts"]]) for c in contours]
    zds, issues = fz.build_zones_from_contours(contours, units=units)
    if len(zds) != 1:
        return None, ["зон %d" % len(zds)] + [str(i) for i in issues]
    z = fz.load_zone(zds[0])
    iss = fz.validate_zone(z)
    errs = [i.code for i in iss if i.level == "error"]
    if errs:
        return None, errs
    return fz.zone_report(z, iss), []


def run_good(rng, fam, idx, bad):
    zc = outer_shape(rng, fam)
    wall = poly_of(zc)
    if not wall.is_valid:
        return 0
    ops = openings_for(rng, wall, fam)
    tag = "%s-%03d" % (fam, idx)
    SCEN[tag] = {"zone": zc, "openings": ops}
    ora = oracle(zc, ops)
    eng, err = engine_report(zc, ops)
    if eng is None:
        bad.append(("Z8", "%s: движок отверг корректную зону: %s" % (tag, err)))
        return len(ops)
    arc_tol = any(o.get("bulges") for o in ops + [zc])
    cmp_report(tag, eng, ora, bad, arc_tol)
    for i, ((a, p), oe) in enumerate(zip(ora["per"], eng["openings"])):
        if ops[i].get("bulges"):
            wall_i = poly_of(zc)
            e = edge_classes(ring(ops[i]["pts"], ops[i].get("bulges")), wall_i)
            ARC_OPEN.append(max(abs(oe["edges"]["bottom_m"] - e["bottom"] / 1000.0),
                                abs(oe["edges"]["top_m"] + oe["edges"]["sides_m"]
                                    - (e["top"] + e["sides"]) / 1000.0)))
        if not near(oe["area_m2"], a, TOL_A) or not near(oe["perimeter_m"], p, TOL_L):
            bad.append(("Z7", "%s: проём %d площадь %.4f/%.4f периметр %.4f/%.4f"
                        % (tag, i, oe["area_m2"], a, oe["perimeter_m"], p)))
    # Z10: обход/первая вершина/дубль/хвост
    zc2 = perturb(rng, zc, dup=rng.random() < 0.3, tail=rng.choice([None, "dup", "gap"]))
    ops2 = [perturb(rng, o) for o in ops]
    e2, err2 = engine_report(zc2, ops2)
    if e2 is None:
        bad.append(("Z10", "%s: после перестановки вершин отказ %s" % (tag, err2)))
    else:
        for key in ("area_outer_m2", "area_net_m2", "perimeter_outer_m", "sills_total_m",
                    "jambs_total_m", "on_boundary_total_m"):
            if not near(e2[key], eng[key], 1e-6):
                bad.append(("Z10", "%s: %s %.6f → %.6f при другом обходе" % (tag, key, eng[key], e2[key])))
                break
    # Z11: метры
    # Z11 — только INFO: C# шлёт мм, «м» есть лишь в формате зоны (латентно)
    e3, _ = engine_report(zc, ops, units="m", k=0.001)
    if e3 is None:
        UNITS.append((None, tag))
    elif not near(e3["area_net_m2"], eng["area_net_m2"], 1e-6):
        UNITS.append((abs(e3["area_net_m2"] - eng["area_net_m2"]), tag))
    elif arc_tol and any(abs(e3[k] - eng[k]) > 1e-6 for k in ("sills_total_m", "jambs_total_m")):
        UNITS_ARC.append(max(abs(e3[k] - eng[k]) for k in ("sills_total_m", "jambs_total_m")))
    # Z12: сдвиг чертежа
    dx, dy = 612345.5, 24070.3
    zs = dict(zc, pts=[[p[0] + dx, p[1] + dy] for p in zc["pts"]])
    os_ = [dict(o, pts=[[p[0] + dx, p[1] + dy] for p in o["pts"]]) for o in ops]
    e4, err4 = engine_report(zs, os_)
    if e4 is None or any(not near(e4[k], eng[k], 1e-6) for k in ("area_net_m2", "perimeter_outer_m")) or \
            (not arc_tol and any(not near(e4[k], eng[k], 1e-6) for k in ("sills_total_m", "jambs_total_m",
                                                                         "on_boundary_total_m"))):
        bad.append(("Z12", "%s: сдвиг чертежа меняет отчёт (%s)" % (tag, err4 or "числа")))
    elif any(abs(e4[k] - eng[k]) > 1e-6 for k in ("sills_total_m", "jambs_total_m")):
        SHIFT_ARC.append(max(abs(e4[k] - eng[k]) for k in ("sills_total_m", "jambs_total_m")))
    return len(ops)


def run_bad(rng, idx, bad):
    """Плохие входы: ожидаемые коды ошибок."""
    W, H = 8000, 5000
    cases = []
    cases.append(("бабочка", {"pts": [[0, 0], [W, H], [W, 0], [0, H]]}, [], "E_SELF_INTERSECT"))
    x = rng.randrange(-800, -100, 10)
    cases.append(("проём за контуром", {"pts": rect(0, 0, W, H)},
                  [{"pts": rect(x, 1000, x + 1500, 2500)}], None))
    cases.append(("наложение проёмов", {"pts": rect(0, 0, W, H)},
                  [{"pts": rect(1000, 1000, 3000, 3000)}, {"pts": rect(2500, 2000, 4500, 4000)}],
                  "E_OPENINGS_OVERLAP"))
    for name, zc, ops, code in cases:
        contours = [dict(zc, id="Z")] + [dict(o, id="O%d" % i) for i, o in enumerate(ops)]
        zds, issues = fz.build_zones_from_contours(contours)
        codes = {i.code for i in issues}
        for zd in zds:
            try:
                z = fz.load_zone(zd)
                codes |= {i.code for i in fz.validate_zone(z) if i.level == "error"}
            except fz.ZoneFormatError as e:
                codes.add("E_BAD_FORMAT:%s" % e)
        if name == "проём за контуром":
            # пересекающий границу контур — не вложен: движок делает из него ВТОРУЮ ЗОНУ
            if len(zds) != 1 and not codes:
                bad.append(("Z8", "проём, пересекающий край стены, молча стал отдельной зоной "
                                  "(зон %d, ошибок нет): площадь стены посчитана без вычета" % len(zds)))
            continue
        if code not in codes:
            bad.append(("Z8", "%s: ожидали %s, получили %s" % (name, code, sorted(codes) or "без ошибок")))
    # частичная кромка на границе: сторона окна наполовину по краю «кармана» Г-стены
    L = {"pts": [[0, 0], [9000, 0], [9000, 6000], [6000, 6000], [6000, 3000], [0, 3000]]}
    ops = [{"pts": rect(6000, 2000, 7000, 4000)}]
    eng, _ = engine_report(L, ops)
    ora = oracle(L, ops)
    if idx == 0 and eng and not near(eng["on_boundary_total_m"], ora["on_boundary_total_m"], TOL_L):
        INFO.append("частичная кромка на границе зоны (бок окна 2 м, из них 1 м по краю Г): движок "
                    "откос %.1f / порог %.1f, по длине на границе — откос %.1f / порог %.1f"
                    % (eng["jambs_total_m"], eng["on_boundary_total_m"], ora["jambs_total_m"],
                       ora["on_boundary_total_m"]))
    # Z13: контур внутри проёма
    contours = [{"id": "Z", "pts": rect(0, 0, W, H)}, {"id": "O", "pts": rect(1000, 1000, 5000, 4000)},
                {"id": "I", "pts": rect(2000, 2000, 3000, 3000)}]
    zds, issues = fz.build_zones_from_contours(contours)
    if "E_NESTED_DEEP" not in {i.code for i in issues}:
        bad.append(("Z13", "контур внутри проёма: нет E_NESTED_DEEP"))


def run_cli(rng, bad):
    """Z9/Z13/Z14: несколько зон одним плоским списком, как шлёт C#."""
    zones, contours = [], []
    for r in range(3):
        for c in range(3):
            x0, y0 = c * 10000 + rng.uniform(-20, 20), r * 3500 + rng.uniform(-30, 30)
            zc = {"pts": rect(round(x0, 1), round(y0, 1), round(x0 + 9000, 1), round(y0 + 3200, 1))}
            ops = [{"pts": rect(round(x0 + 1000 + 3000 * k, 1), round(y0 + 900, 1),
                                round(x0 + 2400 + 3000 * k, 1), round(y0 + 2400, 1))} for k in range(rng.randrange(0, 3))]
            zones.append((zc, ops, (r, c)))
    rng.shuffle(zones)
    for i, (zc, ops, rc) in enumerate(zones):
        contours.append(dict(zc, id="Z%d" % i))
        contours += [dict(o, id="Z%dO%d" % (i, j)) for j, o in enumerate(ops)]
    res = fe.run(json.loads(json.dumps({"op": "zones", "contours": contours, "units": "mm",
                                        "zone_prefix": "Ф-", "start_index": 1})))
    if not res.get("ok") or len(res["zones"]) != 9:
        bad.append(("Z9", "CLI: зон %s, ошибка %s" % (len(res.get("zones") or []), res.get("error"))))
        return
    net = sum(oracle(zc, ops)["area_net_m2"] for zc, ops, _ in zones)
    if not near(res["summary"]["area_net_total_m2"], net, TOL_A * 9):
        bad.append(("Z9", "CLI: сумма нетто %.4f, оракул %.4f" % (res["summary"]["area_net_total_m2"], net)))
    # Z14: Ф-1 — нижний ряд, правая колонка; дальше справа налево, потом ряд выше
    order = []
    by_id = {z["zone_id"]: z for z in res["zones"]}
    for n in range(1, 10):
        z = by_id["Ф-%d" % n]
        bb = z["bbox"]
        order.append((round(bb[1] / 3500), round(bb[0] / 10000)))
    want = [(r, c) for r in range(3) for c in (2, 1, 0)]
    if order != want:
        bad.append(("Z14", "нумерация: %s вместо %s" % (order, want)))
    merged = fe.run(json.loads(json.dumps({"op": "zones", "contours": contours, "units": "mm",
                                           "merge": True})))
    if not merged.get("ok") or len(merged["zones"]) != 1 or \
            not near(merged["zones"][0]["report"]["area_net_m2"], net, TOL_A * 9):
        bad.append(("Z9", "merge=true: зон %s, нетто %s" % (len(merged.get("zones") or []),
                                                            merged.get("summary", {}).get("area_net_total_m2"))))


def selftest():
    """Оракул сам: полукруг r=1000 → πr²/2, дуга πr."""
    c = {"pts": [[-1000, 0], [1000, 0]], "bulges": [0, 1.0]}
    p = Polygon(ring(c["pts"], c["bulges"]))
    assert abs(p.area - math.pi * 1e6 / 2) / (math.pi * 1e6 / 2) < 1e-4, p.area
    assert abs(p.exterior.length - (math.pi * 1000 + 2000)) < 0.5, p.exterior.length
    assert p.bounds[3] > 990, "дуга с bulge>0 должна идти вверх (против часовой)"


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=2309)
    ap.add_argument("--n", type=int, default=120)
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--dump", default=None)
    a = ap.parse_args(argv)
    selftest()
    rng = random.Random(a.seed)
    n = 10 if a.quick else a.n
    fams = ["rect", "L", "step", "U", "gable", "arch", "star"]
    stats = defaultdict(Counter)
    first = {}
    t0 = time.time()
    total_ops = 0
    for fam in fams:
        for i in range(n):
            bad = []
            total_ops += run_good(rng, fam, i, bad)
            for code, msg in bad:
                stats[fam][code] += 1
                first.setdefault((fam, code), msg)
    bad = []
    for i in range(5):
        run_bad(rng, i, bad)
    run_cli(rng, bad)
    for code, msg in bad:
        stats["входы/CLI"][code] += 1
        first.setdefault(("входы/CLI", code), msg)
    print("ЗОНЫ: сценариев %d (+плохие входы и CLI), проёмов %d, время %.1f с"
          % (n * len(fams), total_ops, time.time() - t0))
    codes = sorted({c for f in stats for c in stats[f]})
    if not codes:
        print("  нарушений нет")
    else:
        print("  %-10s %s" % ("форма", "  ".join("%-4s" % c for c in codes)))
        for fam in fams + ["входы/CLI"]:
            if fam in stats:
                print("  %-10s %s" % (fam, "  ".join("%-4d" % stats[fam][c] for c in codes)))
        for (fam, code), msg in sorted(first.items()):
            print("  · %s %s" % (code, msg))
            tag = msg.split(":")[0]
            if a.dump and tag in SCEN:
                os.makedirs(a.dump, exist_ok=True)
                with open(os.path.join(a.dump, "%s_%s.json" % (code, tag)), "w", encoding="utf-8") as f:
                    json.dump(SCEN[tag], f, ensure_ascii=False)
    for m in INFO:
        print("  [INFO] %s" % m)
    if UNITS:
        dn = [d for d, _ in UNITS if d is not None]
        print("  [INFO] зона в МЕТРАХ: у %d зон из %d нетто другое (макс Δ %.3f м², напр. %s) — load_zone "
              "сравнивает хвост обводки с 0.5 в единицах зоны (0.5 м): проём со стороной ≤0.5 м теряет "
              "вершину. C# шлёт мм — латентно" % (len(UNITS), len(SCEN), max(dn) if dn else 0, UNITS[0][1]))
    if SHIFT_ARC:
        print("  [INFO] дуги: сдвиг чертежа меняет раздел отлив/откос у %d зон с дугами (макс %.0f мм) — "
              "хорда ровно на 45° падает то в низ, то в бок" % (len(SHIFT_ARC), max(SHIFT_ARC) * 1000))
    if UNITS_ARC:
        print("  [INFO] дуги: зона в метрах делит дугу хордами со стрелкой 0.5 М (допуск в единицах зоны) — "
              "отлив/откос меняются у %d зон (макс %.0f мм); C# шлёт мм" % (len(UNITS_ARC), max(UNITS_ARC) * 1000))
    if ARC_OPEN:
        ARC_OPEN.sort()
        print("  [INFO] дуги: расхождение отлив/откос на проём с дугой — медиана %.1f мм, макс %.1f мм "
              "(проёмов с дугой %d)" % (ARC_OPEN[len(ARC_OPEN) // 2] * 1000, ARC_OPEN[-1] * 1000, len(ARC_OPEN)))
    return 1 if codes else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
