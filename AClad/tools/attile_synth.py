# -*- coding: utf-8 -*-
"""Синтетические испытания универсальной разбежки (команда ATTILE,
движок AClad/engine/tile_pattern.py) против НЕЗАВИСИМОГО оракула на
shapely.

Оракул не использует ни одной функции движка:
- сетка — своя формула (индекс плитки слева направо от базовой, сдвиг
  ряда — своя реализация смещений none/alternate/step/sequence;
  столбцы — прямо в реальной плоскости, без транспонирования);
- зона — shapely: объединение внешних контуров минус проёмы, при русте
  вокруг проёмов — анизотропный ортогональный отступ (масштаб по Y →
  квадратный mitre-буфер → обратно);
- плитка ∩ зона — shapely.intersection.

Инварианты на каждый сценарий:
 I1  куски лежат внутри зоны (с учётом руста вокруг проёмов);
 I2  куски не перекрываются;
 I3  каждый кусок — внутри ровно одной плитки сетки оракула (фаза
     рядов/столбцов, привязка, модуль);
 I4  множество ЦЕЛЫХ плиток совпадает с оракулом (по положению);
 I5  сохранение площади: Σ(плитка ∩ зона) = Σ кусков + поглощённые
     полоски + снятое рустом при разрезе Г + численный шум;
 I6  куски разных плиток не ближе min(руст) друг к другу;
 I7  в точке отсчёта целая плитка (если угол свободен);
 I8  флаг малой подрезки пересчитан независимо;
 I9  сводка согласована (целые/куски/итого/раскрой ≥ нижней границы
     по площади/отходы ≥ 0);
 I10 оси мостa ATFRAME — подмножество осей швов оракула;
 I11 детерминизм и независимость от порядка/направления вершин;
 I12 путь CLI clad_engine с C#-форматом контуров (каждая полилиния —
     отдельный контур) даёт то же, что прямой вызов.

Запуск (из корня репо): PYTHONUTF8=1 python3 AClad/tools/attile_synth.py
  [--quick] [--seed N] [--render DIR] [--dump DIR]
Нужны shapely (+ matplotlib для --render). Выход ≠ 0 при нарушениях.
"""
import argparse
import json
import math
import os
import random
import subprocess
import sys
import tempfile
import time

from shapely import affinity
from shapely.geometry import Polygon, box
from shapely.ops import unary_union
from shapely.strtree import STRtree

HERE = os.path.dirname(os.path.abspath(__file__))
ENGINE = os.path.normpath(os.path.join(HERE, "..", "engine"))
sys.path.insert(0, ENGINE)
import tile_pattern as tp  # noqa: E402

FORMATS = [(600, 600), (600, 1200), (1200, 600), (300, 600), (600, 300),
           (1200, 1200), (800, 1600), (1200, 2400), (450, 900),
           (290, 82), (250, 65), (240, 71), (1200, 3000), (190, 3600)]
KINDS = ["none", "alternate", "step", "sequence"]
FRACS = ["1/2", "1/3", "1/4", "1/5", "2/3", "3/4", "0.37"]


# ───────────────────────────────────────────────────────── оракул

def _num(v):
    if isinstance(v, (int, float)):
        return float(v)
    t = str(v).replace(",", ".")
    if "/" in t:
        a, b = t.split("/")
        return float(a) / float(b)
    return float(t)


def _frac(x):
    f = x - math.floor(x)
    return 0.0 if (f < 1e-9 or f > 1 - 1e-9) else f


def o_shift(bond, module):
    """Независимая реализация сдвига ряда j (доля модуля)."""
    kind = bond["kind"]
    sign = -1.0 if bond.get("dir") == "-" else 1.0

    def conv(v):
        return _num(v) / module if bond.get("units") == "mm" else _num(v)
    if kind == "none":
        return lambda j: 0.0
    if kind == "alternate":
        f = conv(bond["value"])
        return lambda j: 0.0 if j % 2 == 0 else _frac(sign * f)
    if kind == "step":
        f = conv(bond["value"])
        return lambda j: _frac(sign * j * f)
    seq = [conv(v) for v in bond["sequence"]]
    base = seq[0]
    return lambda j: _frac(sign * (seq[j % len(seq)] - base))


def o_base(bounds, anchor, w, h, gv, gh):
    """Левый-нижний угол базовой плитки: целая в выбранной точке."""
    minx, miny, maxx, maxy = bounds
    pt = anchor.get("point")
    xL = xR = xc = pt["x"] if pt else None
    yB = yT = yc = pt["y"] if pt else None
    if not pt:
        xL, xR, xc = minx, maxx, 0.5 * (minx + maxx)
        yB, yT, yc = miny, maxy, 0.5 * (miny + maxy)
    joint = anchor.get("center") == "joint"
    hx, vy = anchor.get("h", "R"), anchor.get("v", "B")
    bx = xL if hx == "L" else (xR - w if hx == "R" else
                               (xc + gv / 2.0 if joint else xc - w / 2.0))
    by = yB if vy == "B" else (yT - h if vy == "T" else
                               (yc + gh / 2.0 if joint else yc - h / 2.0))
    return bx, by


def o_tiles(bounds, bx, by, w, h, gv, gh, shift, axis):
    """Плитки сетки, задевающие габарит: (x0, y0, x1, y1, курс)."""
    MX, MY = w + gv, h + gh
    minx, miny, maxx, maxy = bounds
    out = []
    if axis == "rows":
        for j in range(int(math.floor((miny - by) / MY)) - 1,
                       int(math.ceil((maxy - by) / MY)) + 2):
            y0 = by + j * MY
            if y0 + h <= miny or y0 >= maxy:
                continue
            s = shift(j) * MX
            for k in range(int(math.floor((minx - bx - s) / MX)) - 1,
                           int(math.ceil((maxx - bx - s) / MX)) + 2):
                x0 = bx + k * MX + s
                if x0 + w <= minx or x0 >= maxx:
                    continue
                out.append((x0, y0, x0 + w, y0 + h, j))
    else:
        for j in range(int(math.floor((minx - bx) / MX)) - 1,
                       int(math.ceil((maxx - bx) / MX)) + 2):
            x0 = bx + j * MX
            if x0 + w <= minx or x0 >= maxx:
                continue
            s = shift(j) * MY
            for k in range(int(math.floor((miny - by - s) / MY)) - 1,
                           int(math.ceil((maxy - by - s) / MY)) + 2):
                y0 = by + k * MY + s
                if y0 + h <= miny or y0 >= maxy:
                    continue
                out.append((x0, y0, x0 + w, y0 + h, j))
    return out


def o_expand(poly, ex, ey):
    """Ортогональный отступ полигона на ex по X и ey по Y."""
    if ex <= 0 and ey <= 0:
        return poly
    k = ex / ey
    p2 = affinity.scale(poly, 1.0, k, origin=(0, 0))
    e2 = p2.buffer(ex, join_style=2, mitre_limit=10.0)
    return affinity.scale(e2, 1.0, 1.0 / k, origin=(0, 0))


def o_zone(outers, holes, gap_around, gv, gh):
    O = unary_union([Polygon(o) for o in outers])
    H = [Polygon(hh) for hh in holes]
    if gap_around and H:
        H = [o_expand(hp, gv, gh) for hp in H]
    return O.difference(unary_union(H)) if H else O


def piece_geom(p):
    if p.get("rings"):
        return Polygon(p["rings"][0], p["rings"][1:])
    return box(p["x"], p["y"], p["x"] + p["w"], p["y"] + p["h"])


def o_small(p, W, H, warn):
    if warn <= 0 or p["full"] or not p["rect"]:
        return None
    cw = p["w"] < W - 0.05
    ch = p["h"] < H - 0.05
    return bool((cw and p["w"] < warn - 1e-9) or (ch and p["h"] < warn - 1e-9))


# ───────────────────────────────────────────────────────── сценарии

def R(x0, y0, w, h):
    return [[x0, y0], [x0 + w, y0], [x0 + w, y0 + h], [x0, y0 + h]]


def rand_params(rng, fmt=None):
    W, H = fmt or rng.choice(FORMATS)
    gv = rng.choice([0, 3, 5, 6, 7, 8, 10, 12])
    gh = gv if rng.random() < 0.7 else rng.choice([0, 4, 8, 10, 15])
    kind = rng.choice(KINDS)
    bond = {"kind": kind, "dir": rng.choice(["+", "-"])}
    if kind in ("alternate", "step"):
        if rng.random() < 0.3:
            mod = (W + gv) if True else 0
            bond.update(value=rng.choice([100, 200, 250, 300, int(mod / 2), 700]),
                        units="mm")
        else:
            bond.update(value=rng.choice(FRACS), units="frac")
    elif kind == "sequence":
        if rng.random() < 0.3:
            bond.update(sequence=[0, rng.choice([150, 200, 250]),
                                  rng.choice([400, 450])], units="mm")
        else:
            bond.update(sequence=rng.choice([["0", "1/2", "1/4", "3/4"],
                                             ["0", "1/3", "2/3"],
                                             ["1/4", "3/4"], ["0"]]),
                        units="frac")
    anchor = {"h": rng.choice("LCR"), "v": rng.choice("BCT"),
              "center": rng.choice(["tile", "joint"])}
    axis = rng.choice(["rows", "cols"])
    gap_around = rng.random() < 0.6 and gv > 0 and gh > 0
    shaped = rng.choice(["keep", "split", "split_joint"])
    return {"tile": {"w": W, "h": H}, "gap": {"v": gv, "h": gh}, "types": ["T"],
            "axis": axis, "bond": bond, "anchor": anchor,
            "gap_around": gap_around, "shaped": shaped,
            "min_piece": rng.choice([0, 5, 10, 20]),
            "warn_cut": rng.choice([0, 100, 150]),
            "kerf": rng.choice([0, 3]), "merge_touching": True}


def windows(rng, x0, y0, x1, y1, n, margin=60):
    """n непересекающихся окон внутри [x0,x1]×[y0,y1] (целые мм)."""
    out = []
    tries = 0
    while len(out) < n and tries < 400:
        tries += 1
        w = rng.randint(300, 2400)
        h = rng.randint(300, 2600)
        if x1 - x0 - 2 * margin < w or y1 - y0 - 2 * margin < h:
            continue
        ax = rng.randint(x0 + margin, x1 - margin - w)
        ay = rng.randint(y0 + margin, y1 - margin - h)
        cand = (ax, ay, ax + w, ay + h)
        if all(cand[2] + 2 <= o[0] or o[2] + 2 <= cand[0] or
               cand[3] + 2 <= o[1] or o[3] + 2 <= cand[1] for o in out):
            out.append(cand)
    return [R(a, b, c - a, d - b) for a, b, c, d in out]


def fam_rect(rng):
    q = rand_params(rng)
    W, H = rng.randint(200, 14000), rng.randint(200, 12000)
    q["contours"] = [{"id": "R", "outer": R(0, 0, W, H)}]
    return q


def fam_facade(rng):
    q = rand_params(rng)
    W, H = rng.randint(4000, 24000), rng.randint(3000, 15000)
    q["contours"] = [{"id": "F", "outer": R(0, 0, W, H),
                      "holes": windows(rng, 0, 0, W, H, rng.randint(1, 14))}]
    return q


def fam_grid(rng):
    """Фасад с регулярной сеткой окон (этажи × оси) и узкими простенками."""
    q = rand_params(rng)
    nx, ny = rng.randint(2, 6), rng.randint(2, 5)
    ww, wh = rng.randint(900, 1800), rng.randint(1200, 2000)
    pier = rng.choice([30, 150, 400, 900, 1200])
    fl = wh + rng.randint(900, 1600)
    W = nx * (ww + pier) + pier
    H = ny * fl + 400
    holes = [R(pier + i * (ww + pier), 400 + j * fl + 300, ww, wh)
             for i in range(nx) for j in range(ny)]
    q["contours"] = [{"id": "G", "outer": R(0, 0, W, H), "holes": holes}]
    return q


def fam_shape(rng):
    """Ортогональный многоугольник: объединение прямоугольников + окна."""
    q = rand_params(rng)
    rects = [box(0, 0, rng.randint(3000, 9000), rng.randint(3000, 9000))]
    for _ in range(rng.randint(1, 4)):
        x, y = rng.randint(-2000, 8000), rng.randint(-2000, 8000)
        rects.append(box(x, y, x + rng.randint(1000, 6000), y + rng.randint(1000, 6000)))
    U = unary_union(rects)
    if U.geom_type != "Polygon" or U.interiors:
        return fam_facade(rng)
    outer = [[int(round(x)), int(round(y))] for x, y in list(U.exterior.coords)[:-1]]
    minx, miny, maxx, maxy = U.bounds
    holes = [hh for hh in windows(rng, int(minx), int(miny), int(maxx), int(maxy),
                                  rng.randint(0, 6))
             if Polygon(hh).buffer(40).within(U)]
    q["contours"] = [{"id": "S", "outer": outer, "holes": holes}]
    return q


def fam_tiny(rng):
    q = rand_params(rng)
    W, H = q["tile"]["w"], q["tile"]["h"]
    q["contours"] = [{"id": "T", "outer": R(0, 0, max(15, int(W * rng.uniform(0.05, 1.6))),
                                            max(15, int(H * rng.uniform(0.05, 1.6))))}]
    return q


def fam_adjacent(rng):
    """Смежные стены одной плоскости (точные швы) — объединение зон."""
    q = rand_params(rng)
    x = 0
    cs = []
    H0 = rng.randint(3000, 9000)
    for k in range(rng.randint(2, 4)):
        w = rng.randint(1500, 6000)
        h = H0 if rng.random() < 0.6 else rng.randint(2000, 9000)
        cs.append({"id": "A%d" % k, "outer": R(x, 0, w, h),
                   "holes": windows(rng, x, 0, x + w, h, rng.randint(0, 3))})
        x += w
    q["contours"] = cs
    return q


def fam_edgewin(rng):
    """Окно ближе руста к кромке стены (руст вокруг проёма срезает полосу)."""
    q = rand_params(rng)
    q["gap_around"] = q["gap"]["v"] > 0 and q["gap"]["h"] > 0
    W, H = rng.randint(3000, 8000), rng.randint(3000, 8000)
    d = rng.choice([1, 3, 5, 9, 15])
    q["contours"] = [{"id": "E", "outer": R(0, 0, W, H),
                      "holes": [R(d, rng.randint(300, 1000), 1200, 1500),
                                R(W - d - 900, H - d - 1100, 900, 1100)]}]
    return q


def fam_point(rng):
    """Общая точка отсчёта (в зоне или вне её) для нескольких зон."""
    q = fam_adjacent(rng)
    q["merge_touching"] = rng.random() < 0.5
    q["anchor"]["point"] = {"x": rng.randint(-3000, 9000), "y": rng.randint(-2000, 5000)}
    return q


FAMILIES = [("rect", fam_rect), ("facade", fam_facade), ("grid", fam_grid),
            ("shape", fam_shape), ("tiny", fam_tiny), ("adjacent", fam_adjacent),
            ("edgewin", fam_edgewin), ("point", fam_point)]


def gallery():
    """Структурный перебор: все привязки × все смещения × оси × Г-режимы
    на типовом фасаде 12×9 м с сеткой окон, керамогранит 600×1200."""
    holes = [R(900 + i * 2700, 900 + j * 2700, 1500, 1800) for i in range(4) for j in range(3)]
    base_c = [{"id": "Г", "outer": R(0, 0, 11700, 8900), "holes": holes}]
    bonds = [{"kind": "none"},
             {"kind": "alternate", "value": "1/2", "dir": "+"},
             {"kind": "alternate", "value": "1/3", "dir": "-"},
             {"kind": "step", "value": "1/3", "dir": "+"},
             {"kind": "step", "value": 200, "units": "mm", "dir": "-"},
             {"kind": "sequence", "sequence": ["0", "1/2", "1/4", "3/4"], "dir": "+"}]
    out = []
    for axis in ("rows", "cols"):
        for b in bonds:
            for hx in "LCR":
                for vy in "BCT":
                    for center in (("tile", "joint") if "C" in (hx, vy) else ("tile",)):
                        for shaped in ("keep", "split_joint"):
                            out.append({"tile": {"w": 600, "h": 1200},
                                        "gap": {"v": 8, "h": 8}, "types": ["КГ"],
                                        "axis": axis, "bond": dict(b),
                                        "anchor": {"h": hx, "v": vy, "center": center},
                                        "gap_around": True, "shaped": shaped,
                                        "min_piece": 10, "warn_cut": 150, "kerf": 3,
                                        "contours": base_c})
    return out


# ───────────────────────────────────────────────────────── проверка

def check(req, full_checks=True):
    """→ (ошибки[], stats{}) для одного запроса."""
    errs = []
    t0 = time.time()
    res = tp.tile_pattern(req)
    dt = time.time() - t0
    st = {"t": dt}
    if not res.get("ok"):
        return ["движок отказал: %s" % res.get("error")], st
    W, H = float(req["tile"]["w"]), float(req["tile"]["h"])
    gv, gh = float(req["gap"]["v"]), float(req["gap"]["h"])
    axis = req.get("axis", "rows")
    pcs = res["pieces"]
    st["pieces"] = len(pcs)
    # зоны движка (после объединения) → оракул по каждой
    mod = (H + gh) if axis == "cols" else (W + gv)
    shift = o_shift(req["bond"], mod)
    cont = {c["id"]: c for c in req["contours"]}
    geoms = [piece_geom(p) for p in pcs]
    tiles_all, inter_total, full_o = [], 0.0, set()
    Zs = []
    for pz in res["per_zone"]:
        mem = [cont[m] for m in pz["members"]]
        outers = [m["outer"] for m in mem]
        holes = [hh for m in mem for hh in (m.get("holes") or [])]
        Z = o_zone(outers, holes, req.get("gap_around"), gv, gh)
        Zs.append(Z)
        bounds = unary_union([Polygon(o) for o in outers]).bounds
        bx, by = o_base(bounds, req["anchor"], W, H, gv, gh)
        tiles = o_tiles(bounds, bx, by, W, H, gv, gh, shift, axis)
        for (x0, y0, x1, y1, j) in tiles:
            tb = box(x0, y0, x1, y1)
            it = tb.intersection(Z)
            a = it.area
            if a <= 0:
                continue
            # численный шум движка (компоненты тоньше 0.1 мм / < 1 мм²)
            parts = [it] if it.geom_type == "Polygon" else list(getattr(it, "geoms", []))
            for pp in parts:
                if pp.geom_type != "Polygon":
                    continue
                bx0, by0, bx1, by1 = pp.bounds
                if min(bx1 - bx0, by1 - by0) < tp.NOISE_MM or pp.area < 1.0:
                    a -= pp.area
            inter_total += a
            tiles_all.append((tb, pz["zone_id"]))
            if tb.difference(Z).area < 1e-6 * W * H:
                full_o.add((pz["zone_id"], round(x0, 3), round(y0, 3)))
        # I7: целая в точке отсчёта (если угол свободен)
        base_box = box(bx, by, bx + W, by + H)
        if base_box.difference(Z).area < 1e-6 * W * H:
            if not any(p["full"] and p["zone"] == pz["zone_id"] and
                       abs(p["x"] - bx) < 1e-6 and abs(p["y"] - by) < 1e-6 for p in pcs):
                errs.append("I7 %s: нет целой в точке отсчёта (%.1f, %.1f)"
                            % (pz["zone_id"], bx, by))
    Zu = unary_union(Zs)
    # I1: внутри зоны
    for p, g in zip(pcs, geoms):
        # кольца Г-кусков округлены движком до 0.001 мм — допуск по периметру
        rtol = (1e-3 * g.length if p.get("rings") else 0.0) + 1e-3
        out = g.difference(Zu).area
        if out > rtol:
            errs.append("I1: кусок вне зоны на %.4f мм² %s" % (out, (p["x"], p["y"], p["w"], p["h"])))
            break
        if abs(g.area - p["area"]) > rtol:
            errs.append("I1b: площадь куска %.3f ≠ контуру %.3f" % (p["area"], g.area))
            break
    # I3: каждый кусок — в одной плитке оракула
    tboxes = [t[0] for t in tiles_all]
    tree = STRtree(tboxes)
    owner = []
    for p, g in zip(pcs, geoms):
        cand = [k for k in tree.query(g) if tiles_all[k][1] == p["zone"] and
                tboxes[k].buffer(2e-3).contains(g)]
        if len(cand) != 1:
            errs.append("I3: кусок %s не в одной плитке сетки (кандидатов %d)"
                        % ((round(p["x"], 2), round(p["y"], 2), round(p["w"], 2), round(p["h"], 2)),
                           len(cand)))
            owner.append(-1)
            if len([e for e in errs if e.startswith("I3")]) > 3:
                break
        else:
            owner.append(cand[0])
    # I2: без перекрытий; I6: зазор между кусками разных плиток
    gmin = min(gv, gh)
    tr2 = STRtree(geoms)
    n_ov = 0
    lim = len(pcs) if (full_checks and len(pcs) <= 6000) else 0
    for a in range(lim):
        for b in tr2.query(geoms[a].buffer(max(gmin - 1e-6, 0.0))):
            if b <= a:
                continue
            ia = geoms[a].intersection(geoms[b]).area
            if ia > 1e-3:
                n_ov += 1
            rt = 2e-3 if (pcs[a].get("rings") or pcs[b].get("rings")) else 1e-6
            # граница НЕобъединённых зон — жёсткий рез (плитка режется по
            # кромке каждой зоны), руст между зонами не обязателен
            if gmin > 0 and len(owner) > b and owner[a] != owner[b] and owner[a] >= 0 \
               and pcs[a]["zone"] == pcs[b]["zone"] \
               and geoms[a].distance(geoms[b]) < gmin - rt:
                errs.append("I6: куски разных плиток ближе руста (%.4f < %g)"
                            % (geoms[a].distance(geoms[b]), gmin))
                lim = 0
                break
    if n_ov:
        errs.append("I2: перекрытий кусков %d" % n_ov)
    # I4: целые
    full_e = set((p["zone"], round(p["x"], 3), round(p["y"], 3)) for p in pcs if p["full"])
    if full_e != full_o:
        errs.append("I4: целые не совпали: лишних %d, недостаёт %d (пример лишней %s, недостающей %s)"
                    % (len(full_e - full_o), len(full_o - full_e),
                       next(iter(full_e - full_o), None), next(iter(full_o - full_e), None)))
    # I5: сохранение площади
    s = res["summary"]
    acc = sum(p["area"] for p in pcs) + s["absorbed"]["area"] + s["trimmed"]["area"]
    tol = 1.0 + 1e-9 * inter_total + 0.05 * len(tiles_all) * 1.0e-3
    if abs(acc - inter_total) > max(tol, 2.0):
        errs.append("I5: площадь: оракул %.2f, движок %.2f (Δ %.3f мм²)"
                    % (inter_total, acc, acc - inter_total))
    # I8: малая подрезка
    wc = float(req.get("warn_cut") or 0)
    for p in pcs:
        o = o_small(p, W, H, wc)
        if o is not None and bool(p.get("small")) != o:
            errs.append("I8: флаг малой подрезки %s ≠ оракул %s (%s×%s)" % (p.get("small"), o, p["w"], p["h"]))
            break
    # I9: сводка
    nf = sum(1 for p in pcs if p["full"])
    if s["full"] != nf or s["cut"] != len(pcs) - nf:
        errs.append("I9: сводка целых/кусков не сходится")
    if s["tiles_total"] != s["full"] + s["blanks_cut"]:
        errs.append("I9: итого ≠ целые + заготовки")
    cut_area = sum(p["area"] for p in pcs if not p["full"])
    if s["blanks_cut"] + 1e-9 < math.floor(cut_area / (W * H) + 1e-9):
        errs.append("I9: заготовок меньше нижней границы по площади")
    if s["waste_area"] < -1e-6:
        errs.append("I9: отрицательные отходы")
    if abs(s["area_tiles"] - sum(p["area"] for p in pcs)) > 1e-3:
        errs.append("I9: area_tiles ≠ Σ кусков")
    # I10: оси моста ⊆ швов оракула: центр шва у кромки плитки сетки
    #      (x0 − gv/2, x1 + gv/2; y — так же), при русте вокруг проёма —
    #      оси боковых швов проёмов; для Г-разрезов — в пределах руста от
    #      граней контура/проёмов (там проходит линия разреза)
    import bisect
    for pz in res["per_zone"]:
        mine = [t[0] for t in tiles_all if t[1] == pz["zone_id"]]
        ax_x = sorted(set([round(b.bounds[0] - gv / 2.0, 3) for b in mine] +
                          [round(b.bounds[2] + gv / 2.0, 3) for b in mine]))
        ax_y = sorted(set([round(b.bounds[1] - gh / 2.0, 3) for b in mine] +
                          [round(b.bounds[3] + gh / 2.0, 3) for b in mine]))
        mem = [cont[m] for m in pz["members"]]
        vx = [pt[0] for m in mem for ring in [m["outer"]] + list(m.get("holes") or []) for pt in ring]
        vy = [pt[1] for m in mem for ring in [m["outer"]] + list(m.get("holes") or []) for pt in ring]
        split = req.get("shaped") in ("split", "split_joint")

        def near(v, arr, tol):
            k = bisect.bisect_left(arr, v - tol)
            return k < len(arr) and arr[k] <= v + tol
        vxs, vys = sorted(vx), sorted(vy)
        for x in pz["joints_x"]:
            ok_ = near(x, ax_x, 0.05)
            if not ok_ and req.get("gap_around"):
                ok_ = near(x - gv / 2.0, vxs, 0.05) or near(x + gv / 2.0, vxs, 0.05)
            if not ok_ and split:
                ok_ = near(x, vxs, gv + 0.05)
            if not ok_:
                errs.append("I10: вертикальная ось %.2f не шов сетки/проёма" % x)
                break
        for y in pz["rows_y"]:
            ok_ = near(y, ax_y, 0.05) or (split and near(y, vys, gh + 0.05))
            if not ok_:
                errs.append("I10: горизонтальная ось %.2f не шов сетки" % y)
                break
    st["full"] = s["full"]
    st["cut"] = s["cut"]
    return errs, st


def invariance(req):
    """I11: детерминизм и порядок/направление вершин."""
    a = tp.tile_pattern(req)
    b = tp.tile_pattern(json.loads(json.dumps(req)))
    if json.dumps(a, sort_keys=True) != json.dumps(b, sort_keys=True):
        return ["I11: недетерминированный результат"]
    q = json.loads(json.dumps(req))
    for c in q["contours"]:
        c["outer"] = c["outer"][::-1]
        c["outer"] = c["outer"][2:] + c["outer"][:2]
        c["holes"] = [hh[::-1] for hh in c.get("holes") or []]
    b = tp.tile_pattern(q)
    ka = sorted((round(p["x"], 3), round(p["y"], 3), round(p["w"], 3), round(p["h"], 3)) for p in a["pieces"])
    kb = sorted((round(p["x"], 3), round(p["y"], 3), round(p["w"], 3), round(p["h"], 3)) for p in b["pieces"])
    return [] if ka == kb else ["I11: результат зависит от порядка/направления вершин"]


def cli_same(req):
    """I12: C#-формат (каждая полилиния — отдельный контур, op через CLI)."""
    pay = {k: v for k, v in req.items() if k != "contours"}
    pay["op"] = "tile_pattern"
    pay["contours"] = []
    n = 0
    for c in req["contours"]:
        for ring in [c["outer"]] + list(c.get("holes") or []):
            n += 1
            pay["contours"].append({"id": "H%d" % n, "pts": ring,
                                    "bulges": [0.0] * len(ring)})
    d = tempfile.mkdtemp()
    fi, fo = os.path.join(d, "in.json"), os.path.join(d, "out.json")
    json.dump(pay, open(fi, "w", encoding="utf-8"), ensure_ascii=False)
    rc = subprocess.call([sys.executable, os.path.join(ENGINE, "clad_engine.py"), fi, fo])
    if rc != 0:
        return ["I12: CLI код %d" % rc]
    out = json.load(open(fo, encoding="utf-8"))
    a = tp.tile_pattern(req)
    if not out.get("ok"):
        return ["I12: CLI отказал: %s" % out.get("error")]
    ka = sorted((round(p["x"], 3), round(p["y"], 3), round(p["w"], 3), round(p["h"], 3)) for p in a["pieces"])
    kb = sorted((round(p["x"], 3), round(p["y"], 3), round(p["w"], 3), round(p["h"], 3)) for p in out["pieces"])
    return [] if ka == kb else ["I12: CLI ≠ прямой вызов (%d vs %d кусков)" % (len(kb), len(ka))]


# ───────────────────────────────────────────────────────── рендер

def render(req, res, path, title=None):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.patches import Polygon as MP
    fig, ax = plt.subplots(figsize=(7.5, 6.0), dpi=110)
    for c in req["contours"]:
        o = c["outer"] + [c["outer"][0]]
        ax.plot([p[0] for p in o], [p[1] for p in o], color="#222", lw=1.2, zorder=5)
        for hh in c.get("holes") or []:
            ax.add_patch(MP(hh, closed=True, fc="#dfe6ee", ec="#445", lw=0.8, zorder=4))
    for p in res["pieces"]:
        ring = p["rings"][0] if p.get("rings") else \
            [[p["x"], p["y"]], [p["x"] + p["w"], p["y"]],
             [p["x"] + p["w"], p["y"] + p["h"]], [p["x"], p["y"] + p["h"]]]
        fc = "#e9d8b8" if p["full"] else "#c9a46a"
        ec = "#b3261e" if p.get("small") else "#7a6a55"
        lw = 1.3 if p.get("small") else 0.35
        ax.add_patch(MP(ring, closed=True, fc=fc, ec=ec, lw=lw, zorder=3))
    for pz in res["per_zone"]:
        o = pz.get("origin")
        if o:
            ax.plot([o["x"]], [o["y"]], "o", color="#b3261e", ms=5, zorder=6)
    ax.set_aspect("equal")
    ax.autoscale()
    ax.set_xticks([])
    ax.set_yticks([])
    for sp in ax.spines.values():
        sp.set_visible(False)
    if title:
        ax.set_title(title, fontsize=9)
    fig.tight_layout()
    fig.savefig(path)
    plt.close(fig)


# ───────────────────────────────────────────────────────── прогон

def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--seed", type=int, default=2309)
    ap.add_argument("--render", default=None)
    ap.add_argument("--dump", default=None)
    a = ap.parse_args(argv)
    rng = random.Random(a.seed)
    per_fam = 12 if a.quick else 45
    cases = []
    for name, fn in FAMILIES:
        for k in range(per_fam):
            cases.append(("%s-%02d" % (name, k), fn(rng)))
    for k, q in enumerate(gallery()):
        if a.quick and k % 6:
            continue
        cases.append(("gallery-%03d" % k, q))
    bad, times, npieces = [], [], 0
    t_all = time.time()
    for name, q in cases:
        try:
            errs, st = check(q, full_checks=not a.quick)
            if name.startswith(("rect", "facade", "shape")) and int(name[-2:]) % 5 == 0:
                errs += invariance(q)
            if name.startswith(("facade", "adjacent", "grid")) and int(name[-2:]) % 9 == 0:
                errs += cli_same(q)
        except Exception as e:     # падение движка/оракула — тоже провал
            import traceback
            errs, st = ["ИСКЛЮЧЕНИЕ: %s" % traceback.format_exc(limit=3)], {"t": 0}
        times.append((st.get("t", 0), name, st.get("pieces", 0)))
        npieces += st.get("pieces", 0)
        if errs:
            bad.append((name, errs, q))
            print("FAIL %s: %s" % (name, " | ".join(errs[:3])))
            if a.dump:
                os.makedirs(a.dump, exist_ok=True)
                json.dump(q, open(os.path.join(a.dump, name + ".json"), "w",
                                  encoding="utf-8"), ensure_ascii=False, indent=1)
    times.sort(reverse=True)
    kinds = {}
    for _n, es, _q in bad:
        for e in es:
            kinds[e.split(":")[0]] = kinds.get(e.split(":")[0], 0) + 1
    if kinds:
        print("по инвариантам:", kinds)
    print("сценариев: %d, кусков всего: %d, провалов: %d, время %.1f с; "
          "самые долгие: %s" % (len(cases), npieces, len(bad), time.time() - t_all,
                                ", ".join("%s %.2fс/%d" % (n, t, k) for t, n, k in times[:3])))
    if a.render:
        os.makedirs(a.render, exist_ok=True)
        for name, q in cases[:0]:
            pass
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
