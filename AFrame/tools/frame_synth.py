# -*- coding: utf-8 -*-
"""Синтетические испытания AFrame (ревью 23.09): расстановка подсистемы
(frame_engine → frame_plan) и расчёт (frame_calc) против НЕЗАВИСИМОГО
оракула — геометрия стены и проёмов в shapely, правила Германа
словами из ТЗ, физика нагрузок.

Сценарии: прямоугольник с окнами (в т.ч. дверь в пол, окно у края,
окна вплотную), Г-образная, ступенчатая, П-образная стена, фронтон
(скаты — неортогональные рёбра); подсистемы вертикальная (хлысты и с
отметками), межэтажная (отметки и шаг этажа), ортогональная. Оси стоек и
ряды — из сетки плитки (модуль и фаза случайны), как их дала бы
раскладка. Путь — как в C#: голые полилинии → frame_engine.run.

Инварианты расстановки:
 F1  направляющая лежит на стене (не выходит за контур);
 F2  направляющая не идёт сквозь проём;
 F3  кронштейн на стене и не в проёме;
 F4  кляммер на стене (допуск 60 мм по торцам) и не в проёме;
 F5  горизонтальный профиль на стене и не сквозь проём;
 F6  кронштейн держит профиль (лежит на стойке или на ГП);
 F7  каждая стойка держится: вертикальная — ≥1 кронштейн на куске,
     ортогональная — кусок пересекает ≥1 ГП;
 F8  шаг кронштейнов на стойке ≤ шага системы (+1 мм), вертикальная;
 F9  куски на одной оси не перекрываются;
 F10 сдвиг всего чертежа (47 621.3; 24 070.3) сдвигает ответ и только;
 F11 порядок и направление обхода вершин не влияют;
 F12 детерминизм;
 F13 сводка согласована со списками (штуки, погонаж);
 F14 ортогональная: ряды кронштейнов = низ+300+600·k (правило Германа:
     5 кронштейнов на 3 м — не откатывать) + доп. ряд над окнами;
 F15 числа конечные, длины > 0;
 F16 «только кляммеры» по направляющим полной расстановки (parts=clamps,
     rails_fixed) дают те же кляммеры, что полная (23.09b).
Инварианты расчёта (frame_calc):
 K1  пиковая ветровая не убывает с высотой; местность A ≥ B ≥ C;
 K2  каждая проверка не убывает с нагрузкой (w0, высота, вес облицовки)
     при том же шаге;
 K3  подобранный шаг не растёт с нагрузкой;
 K4  если шаг режет анкер, смена профиля шаг не увеличивает
     (решение «не откатывать»: лимитирует анкер, не профиль);
 K5  (INFO) «прошедшие шаги» — сплошной отрезок снизу: если нет,
     подбор «максимальный прошедший» выбирает шаг, при котором меньший
     шаг не проходит (скачок констант неразрезной балки по числу пролётов);
 K6  (INFO) ветер ниже 5 м — формула без нижней отсечки высоты.

Запуск (из корня репо):
  PYTHONUTF8=1 python3 AFrame/tools/frame_synth.py [--seed N] [--n N] [--dump DIR] [--quick]
Выход ≠ 0 при нарушениях F*/K1–K4.
"""
import argparse
import ast
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
import frame_engine as fre  # noqa: E402
import frame_calc as fc     # noqa: E402

T = 1.0        # мм, геометрический допуск
T_CLAMP = 60.0  # мм, кляммеры у торцов кусков (как R3 краш-аудита)
SHIFT = (47621.3, 24070.3)
SYSTEMS = {"vertical": "Standart", "interfloor": "Межэтажная", "ortho": "Ортогональная"}


# ── сценарии ─────────────────────────────────────────────────────────
def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def _windows(rng, outer_poly, n, forbid_touch=False):
    """Окна/двери внутри стены: не пересекаются между собой (касание —
    можно), лежат в полигоне стены (касание границы — можно)."""
    out = []
    x0, y0, x1, y1 = outer_poly.bounds
    tries = 0
    while len(out) < n and tries < 200:
        tries += 1
        w = rng.randrange(600, 2400, 50)
        h = rng.randrange(600, 2400, 50)
        kind = rng.random()
        if kind < 0.15 and not forbid_touch:       # дверь в пол
            wx = rng.randrange(int(x0), int(max(x0 + 1, x1 - w)), 10)
            wy = y0
        elif kind < 0.25 and not forbid_touch:     # окно у бокового края
            wx = x0 if rng.random() < 0.5 else x1 - w
            wy = rng.randrange(int(y0) + 300, int(max(y0 + 301, y1 - h - 300)), 10)
        else:
            wx = rng.randrange(int(x0), int(max(x0 + 1, x1 - w)), 10)
            wy = rng.randrange(int(y0) + 300, int(max(y0 + 301, y1 - h - 300)), 10)
        if wy > y0 + 1e-6 and wy < y0 + 300 - 1e-6:
            continue          # полоска 1..299 под окном — нереалистично
        cand = Polygon(rect(wx, wy, wx + w, wy + h))
        if not outer_poly.buffer(1e-6).contains(cand):
            continue
        if any(cand.intersection(Polygon(o)).area > 1e-6 for o in out):
            continue
        out.append(rect(wx, wy, wx + w, wy + h))
    return out


def make_outer(rng, fam):
    W = rng.randrange(4200, 18000, 100)
    H = rng.randrange(4000, 15000, 100)
    if fam == "rect":
        return rect(0, 0, W, H)
    if fam == "L":           # Г: слева низ, справа во всю высоту
        a = rng.randrange(1500, W - 1200, 100)
        b = rng.randrange(1500, H - 1200, 100)
        return [[0, 0], [W, 0], [W, H], [a, H], [a, b], [0, b]]
    if fam == "step":        # ступени вверх вправо
        k = rng.randrange(2, 4)
        xs = sorted(rng.sample(range(1200, W - 1200, 100), k - 1))
        hs = sorted(rng.sample(range(2400, H, 100), k - 1)) + [H]
        pts = [[0, 0], [W, 0], [W, hs[-1]]]
        bnd = xs[::-1]
        for i, xb in enumerate(bnd):
            pts += [[xb, hs[-1 - i]], [xb, hs[-2 - i]]]
        pts += [[0, hs[0]]]
        return pts
    if fam == "U":           # П: вырез сверху по центру
        a = rng.randrange(1200, W // 2 - 300, 100)
        c = rng.randrange(W // 2 + 300, W - 1200, 100)
        b = rng.randrange(1500, H - 1200, 100)
        return [[0, 0], [W, 0], [W, H], [c, H], [c, b], [a, b], [a, H], [0, H]]
    if fam == "gable":       # фронтон
        eave = rng.randrange(2400, H - 1200, 100)
        return [[0, 0], [W, 0], [W, eave], [W / 2.0, H], [0, eave]]
    raise ValueError(fam)


def make_scenario(rng, fam, sub, idx):
    outer = make_outer(rng, fam)
    op = Polygon(outer)
    holes = _windows(rng, op, rng.randrange(0, 5))
    x0, y0, x1, y1 = op.bounds
    mod_w = rng.choice([600, 900, 1200, 1500])
    gap_v = rng.choice([8, 10, 12])
    mod_h = rng.choice([600, 900, 1200])
    fx = rng.uniform(0, mod_w)
    joints = [round(x0 + fx + k * (mod_w + gap_v) + gap_v / 2.0, 3)
              for k in range(-1, int((x1 - x0) / (mod_w + gap_v)) + 2)
              if x0 < x0 + fx + k * (mod_w + gap_v) + gap_v / 2.0 < x1]
    rows = [round(y0 + k * (mod_h + gap_v), 3)
            for k in range(1, int((y1 - y0) / (mod_h + gap_v)) + 1)]
    floors, floor_step = [], 0.0
    if sub in ("vertical_f", "interfloor"):
        fh = rng.choice([2800, 3000, 3300])
        floors = [round(y0 + fh * k, 3) for k in range(1, int((y1 - y0) / fh) + 1)
                  if y0 + fh * k < y1 - 300]
    if sub == "interfloor_step":
        floor_step = float(rng.choice([2800, 3000, 3300]))
    real_sub = {"vertical_l": "vertical", "vertical_f": "vertical",
                "interfloor": "interfloor", "interfloor_step": "interfloor",
                "ortho": "ortho"}[sub]
    req = {"op": "frame", "sub_type": real_sub, "system": SYSTEMS[real_sub],
           "zones": [], "contours": [{"id": "O", "pts": outer}] +
           [{"id": "H%d" % i, "pts": h} for i, h in enumerate(holes)],
           "joints_x": joints, "rows_y": rows, "floors_y": floors,
           "floor_step": floor_step, "rail_profile": "", "nsp_type": "НСП-1"}
    if rng.random() < 0.3:
        req["corners_x"] = [x0] if rng.random() < 0.5 else [x0, x1]
    return {"id": "%s-%s-%03d" % (fam, sub, idx), "fam": fam, "sub": real_sub,
            "outer": outer, "holes": holes, "req": req}


# ── оракул ───────────────────────────────────────────────────────────
def _geom(sc):
    wall = Polygon(sc["outer"])
    ops = [Polygon(h) for h in sc["holes"]]
    return wall, unary_union(ops) if ops else None


def _vseg(r):
    return LineString([(r["x"], r["y0"]), (r["x"], r["y1"])])


def _hseg(h):
    return LineString([(h["x0"], h["y"]), (h["x1"], h["y"])])


def check_place(sc, res, step_max):
    """Нарушения F1–F9, F13–F15 → список (код, текст)."""
    bad = []
    wall, ops = _geom(sc)
    wall_t = wall.buffer(T, join_style=2)
    wall_c = wall.buffer(T_CLAMP, join_style=2)
    ops_in = ops.buffer(-T, join_style=2) if ops is not None else None
    rails, hr = res["rails"], res.get("hrails") or []
    br, cl = res["brackets"], res["clamps"]
    for r in rails:
        s = _vseg(r)
        out = s.difference(wall_t).length
        if out > T:
            bad.append(("F1", "стойка x=%.0f [%.0f..%.0f] вне стены на %.0f мм" % (r["x"], r["y0"], r["y1"], out)))
        if ops_in is not None and s.intersection(ops_in).length > T:
            bad.append(("F2", "стойка x=%.0f [%.0f..%.0f] сквозь проём" % (r["x"], r["y0"], r["y1"])))
        if not (r["len"] > 0 and all(math.isfinite(r[k]) for k in ("x", "y0", "y1", "len"))):
            bad.append(("F15", "стойка с длиной %r" % r["len"]))
    for h in hr:
        s = _hseg(h)
        out = s.difference(wall_t).length
        if out > T:
            bad.append(("F5", "ГП y=%.0f [%.0f..%.0f] вне стены на %.0f мм" % (h["y"], h["x0"], h["x1"], out)))
        if ops_in is not None and s.intersection(ops_in).length > T:
            bad.append(("F5", "ГП y=%.0f [%.0f..%.0f] сквозь проём" % (h["y"], h["x0"], h["x1"])))
    for b in br:
        p = Point(b["x"], b["y"])
        if not wall_t.contains(p):
            bad.append(("F3", "кронштейн (%.0f, %.0f) вне стены" % (b["x"], b["y"])))
        elif ops_in is not None and ops_in.contains(p):
            bad.append(("F3", "кронштейн (%.0f, %.0f) в проёме" % (b["x"], b["y"])))
        on_v = any(abs(b["x"] - r["x"]) <= T and r["y0"] - T <= b["y"] <= r["y1"] + T for r in rails)
        on_h = any(abs(b["y"] - h["y"]) <= T and h["x0"] - T <= b["x"] <= h["x1"] + T for h in hr)
        if not (on_v or on_h):
            bad.append(("F6", "кронштейн (%.0f, %.0f) ничего не держит" % (b["x"], b["y"])))
    for c in cl:
        p = Point(c["x"], c["y"])
        if not wall_c.contains(p):
            bad.append(("F4", "кляммер (%.0f, %.0f) вне стены" % (c["x"], c["y"])))
        elif ops_in is not None and ops.buffer(-T_CLAMP, join_style=2).contains(p):
            bad.append(("F4", "кляммер (%.0f, %.0f) в проёме" % (c["x"], c["y"])))
    by_x = defaultdict(list)
    for r in rails:
        by_x[round(r["x"], 1)].append(r)
    for x, rr in by_x.items():
        rr.sort(key=lambda r: r["y0"])
        for a, b2 in zip(rr, rr[1:]):
            if b2["y0"] < a["y1"] - T:
                bad.append(("F9", "куски на оси x=%.0f перекрываются [%.0f..%.0f]∩[%.0f..%.0f]"
                            % (x, a["y0"], a["y1"], b2["y0"], b2["y1"])))
    if sc["sub"] == "vertical":
        for r in rails:
            ys = sorted(b["y"] for b in br if abs(b["x"] - r["x"]) <= T and r["y0"] - T <= b["y"] <= r["y1"] + T)
            if not ys:
                bad.append(("F7", "стойка x=%.0f [%.0f..%.0f] без кронштейна" % (r["x"], r["y0"], r["y1"])))
            for a, b2 in zip(ys, ys[1:]):
                if step_max and b2 - a > step_max + T:
                    bad.append(("F8", "шаг %.0f > %.0f на стойке x=%.0f" % (b2 - a, step_max, r["x"])))
                    break
    if sc["sub"] == "ortho":
        for r in rails:
            if r["len"] < 200.0:
                continue
            if not any(h["x0"] - T <= r["x"] <= h["x1"] + T and r["y0"] - T <= h["y"] <= r["y1"] + T for h in hr):
                bad.append(("F7", "вертикаль x=%.0f [%.0f..%.0f] не опирается ни на один ГП" % (r["x"], r["y0"], r["y1"])))
        y0 = Polygon(sc["outer"]).bounds[1]
        tops = {round(h[3] + 100.0, 1) for h in (Polygon(q).bounds for q in sc["holes"])}
        for b in br:
            k = (b["y"] - y0 - 300.0) / 600.0
            if abs(k - round(k)) * 600.0 > T and round(b["y"], 1) not in tops:
                bad.append(("F14", "ряд кронштейнов y=%.0f не на сетке низ+300+600·k" % b["y"]))
                break
    s = res["summary"]
    lm = sum(r["len"] for r in rails) / 1000.0
    if s["rails"] != len(rails) or abs(s["rails_lm"] - lm) > 0.011 or \
            s["brackets_main"] + s["brackets_row"] != len(br) or \
            s["clamps_start"] + s["clamps_row"] + s["clamps_side"] + s["clamps_combo"] != len(cl):
        bad.append(("F13", "сводка не сходится со списками"))
    return bad


def _canon(res, dx=0.0, dy=0.0):
    """Ответ как мультимножество кортежей (для F10–F12)."""
    def r1(v):
        return round(float(v), 3)
    out = []
    for r in res["rails"]:
        out.append(("R", r1(r["x"] - dx), r1(r["y0"] - dy), r1(r["y1"] - dy), r.get("kind")))
    for h in res.get("hrails") or []:
        out.append(("H", r1(h["y"] - dy), r1(h["x0"] - dx), r1(h["x1"] - dx), h.get("kind")))
    for b in res["brackets"]:
        out.append(("B", r1(b["x"] - dx), r1(b["y"] - dy), b.get("kind")))
    for c in res["clamps"]:
        out.append(("C", r1(c["x"] - dx), r1(c["y"] - dy), c.get("kind"), c.get("orient")))
    for f in res.get("fittings") or []:
        out.append(("F", r1(f["x"] - dx), r1(f["y"] - dy), f.get("kind")))
    return Counter(out)


def _diff(a, b, tol=0.05):
    """Число элементов без пары: тип и марки совпадают, координаты — в
    пределах tol (округление до 0.1 на границе .x5 давало ложные расхождения)."""
    def split(t):
        return tuple(v for v in t if not isinstance(v, float)), [v for v in t if isinstance(v, float)]
    pool = defaultdict(list)
    for t, n in b.items():
        k, v = split(t)
        pool[k] += [v] * n
    miss = 0
    for t, n in a.items():
        k, v = split(t)
        for _ in range(n):
            cand = pool.get(k) or []
            j = next((i for i, w in enumerate(cand) if all(abs(p - q) <= tol for p, q in zip(v, w))), None)
            if j is None:
                miss += 1
            else:
                cand.pop(j)
    return miss + sum(len(v) for v in pool.values())


def _shifted(req, dx, dy):
    q = json.loads(json.dumps(req))
    for c in q["contours"]:
        c["pts"] = [[p[0] + dx, p[1] + dy] for p in c["pts"]]
    q["joints_x"] = [x + dx for x in q["joints_x"]]
    q["rows_y"] = [y + dy for y in q["rows_y"]]
    q["floors_y"] = [y + dy for y in q["floors_y"]]
    if "corners_x" in q:
        q["corners_x"] = [x + dx for x in q["corners_x"]]
    return q


def _reordered(req, rng):
    q = json.loads(json.dumps(req))
    for c in q["contours"]:
        pts = c["pts"][::-1]
        k = rng.randrange(len(pts))
        c["pts"] = pts[k:] + pts[:k]
    return q


def run_scenario(sc, rng):
    viol = []
    t0 = time.time()
    res = fre.run(json.loads(json.dumps(sc["req"])))
    dt = time.time() - t0
    if not res.get("ok"):
        return [("F0", "движок отказал: %s" % res.get("error"))], dt, 0
    step_max = None
    su = res.get("system_used") or {}
    if sc["sub"] == "vertical":
        step_max = max(float(su.get("bracket_step") or 0), float(su.get("bracket_step_corner") or 0)) or None
    viol += check_place(sc, res, step_max)
    base = _canon(res)
    r2 = fre.run(_shifted(sc["req"], *SHIFT))
    if not r2.get("ok") or _diff(base, _canon(r2, *SHIFT)) > 0:
        viol.append(("F10", "сдвиг чертежа меняет ответ (расхождений %s)"
                     % (_diff(base, _canon(r2, *SHIFT)) if r2.get("ok") else r2.get("error"))))
    r3 = fre.run(_reordered(sc["req"], rng))
    if not r3.get("ok") or _diff(base, _canon(r3)) > 0:
        viol.append(("F11", "обход вершин меняет ответ (расхождений %s)"
                     % (_diff(base, _canon(r3)) if r3.get("ok") else r3.get("error"))))
    r4 = fre.run(json.loads(json.dumps(sc["req"])))
    if _diff(_canon(r4), base, 0.0) > 0:
        viol.append(("F12", "повторный прогон дал другой ответ"))
    q5 = json.loads(json.dumps(sc["req"]))
    q5["parts"] = "clamps"
    q5["rails_fixed"] = [{"x": r["x"], "y0": r["y0"], "y1": r["y1"]} for r in res["rails"]]
    if q5["rails_fixed"]:
        r5 = fre.run(q5)
        xs = [p[0] for p in sc["outer"]]
        a = Counter((round(c["x"], 3), round(c["y"], 3), c["kind"], c.get("orient")) for c in res["clamps"]
                    if min(xs) - 1e-6 <= c["x"] <= max(xs) + 1e-6)
        b = Counter((round(c["x"], 3), round(c["y"], 3), c["kind"], c.get("orient"))
                    for c in (r5.get("clamps") or []))
        if not r5.get("ok") or a != b:
            viol.append(("F16", "только кляммеры по направляющим ≠ полной расстановке (%s)"
                         % (sum(((a - b) + (b - a)).values()) if r5.get("ok") else r5.get("error"))))
    n = len(res["rails"]) + len(res["brackets"]) + len(res["clamps"]) + len(res.get("hrails") or [])
    return viol, dt, n


# ── расчёт ───────────────────────────────────────────────────────────
def calc_presets():
    """Шесть боевых входов из test_frame_calc.py (NAME = dict(...))."""
    src = open(os.path.join(ENGINE, "test_frame_calc.py"), encoding="utf-8").read()
    out = {}
    for node in ast.parse(src).body:
        if isinstance(node, ast.Assign) and isinstance(node.value, ast.Call) and \
                getattr(node.value.func, "id", "") == "dict" and len(node.targets) == 1:
            try:
                out[node.targets[0].id] = {kw.arg: ast.literal_eval(kw.value) for kw in node.value.keywords}
            except (ValueError, AttributeError):
                pass
    return {k: v for k, v in out.items() if "scheme" in v}


def check_calc(rng, n_rand):
    viol, info = [], []
    # K1
    for terr in ("A", "B", "C"):
        prev = None
        for h in [5, 7.5, 10, 15, 20, 30, 40, 60, 80, 100, 150]:
            w = fc.wind_peak(30.0, terr, h)[0]
            if prev is not None and w < prev - 1e-9:
                viol.append(("K1", "ветер убывает с высотой: %s h=%s" % (terr, h)))
            prev = w
    for h in [5, 10, 20, 40, 80]:
        wa, wb, wc = (fc.wind_peak(30.0, t, h)[0] for t in "ABC")
        if not (wa >= wb >= wc):
            viol.append(("K1", "местность: A %.1f, B %.1f, C %.1f при h=%s" % (wa, wb, wc, h)))
    # K6: ниже 5 м
    w5 = fc.wind_peak(30.0, "B", 5)[0]
    w2 = fc.wind_peak(30.0, "B", 2)[0]
    info.append(("K6", "ветер B при h=2 м %.1f кг/м² против h=5 м %.1f (%.0f%%): нижней отсечки высоты нет"
                 % (w2, w5, 100.0 * (w2 / w5 - 1))))
    presets = calc_presets()
    cands = list(range(200, 1501, 50))
    k5 = []
    for name, base in presets.items():
        for _ in range(n_rand):
            inp = dict(base)
            inp["height"] = round(rng.uniform(5, 100), 1)
            inp["q_clad"] = round(rng.uniform(10, 120), 1)
            if "w0" in inp:
                inp["w0"] = rng.choice([17, 23, 30, 38, 48, 60, 73])
            zone = rng.choice(["row", "corner"])
            step = rng.choice(cands[:13])
            ch = fc.calc_chain(inp, step, zone)
            for key, mul in (("w0", 1.3), ("height", 1.5), ("q_clad", 1.4)):
                if key not in inp:
                    continue
                inp2 = dict(inp)
                inp2[key] = inp[key] * mul
                ch2 = fc.calc_chain(inp2, step, zone)
                for c1, c2 in zip(ch["checks"], ch2["checks"]):
                    if c2["value"] < c1["value"] - 0.11:
                        viol.append(("K2", "%s: %s убывает при росте %s (%s → %s) шаг %s"
                                     % (name, c1["name"], key, c1["value"], c2["value"], step)))
                s1 = fc.pick_step(inp, zone)[0]
                s2 = fc.pick_step(inp2, zone)[0]
                if s1 is not None and s2 is not None and s2 > s1:
                    viol.append(("K3", "%s: шаг вырос %s → %s при росте %s" % (name, s1, s2, key)))
            # K4
            s, _, _ = fc.pick_step(inp, zone)
            bind = fc.binding_check(inp, zone, s) if s else None
            if bind and bind[0] == "анкер":
                for prof in fc.PROFILES:
                    s_p = fc.pick_step(dict(inp, profile=prof), zone)[0]
                    if s_p is not None and s_p > s:
                        viol.append(("K4", "%s: шаг режет анкер (%s), а профиль %s дал %s > %s"
                                     % (name, s, prof, s_p, s)))
                        break
            # K5
            passed = [c for c in cands if fc.calc_chain(dict(inp, max_step=1500), c, zone)["passed"]]
            if passed:
                gaps = [c for c in cands if c < max(passed) and c not in passed]
                if gaps:
                    k5.append((name, zone, max(passed), gaps[:3]))
    if k5:
        ex = k5[0]
        info.append(("K5", "в %d случаях из %d «прошедшие шаги» не сплошные: напр. %s/%s — "
                     "максимальный прошедший %s, а %s не проходят"
                     % (len(k5), len(presets) * n_rand, ex[0], ex[1], ex[2], ex[3])))
    return viol, info


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=2309)
    ap.add_argument("--n", type=int, default=24, help="сценариев на пару (форма, подсистема)")
    ap.add_argument("--dump", default=None)
    ap.add_argument("--quick", action="store_true")
    a = ap.parse_args(argv)
    rng = random.Random(a.seed)
    n = 4 if a.quick else a.n
    fams = ["rect", "L", "step", "U", "gable"]
    subs = ["vertical_l", "vertical_f", "interfloor", "interfloor_step", "ortho"]
    stats = defaultdict(Counter)
    first = {}
    total, elems, t_all = 0, 0, time.time()
    slow = []
    for fam in fams:
        for sub in subs:
            for i in range(n):
                sc = make_scenario(rng, fam, sub, i)
                viol, dt, ne = run_scenario(sc, rng)
                total += 1
                elems += ne
                slow.append((dt, sc["id"], ne))
                codes = Counter(c for c, _ in viol)
                for c in codes:
                    stats[fam][c] += 1
                    if (fam, c) not in first:
                        first[(fam, c)] = (sc, [m for cc, m in viol if cc == c][:3])
    print("РАССТАНОВКА: сценариев %d, элементов %d, время %.1f с" % (total, elems, time.time() - t_all))
    per = n * len(subs)
    codes_all = sorted({c for f in stats for c in stats[f]})
    print("  нарушения (число сценариев из %d на форму):" % per)
    print("  %-7s %s" % ("форма", "  ".join("%-4s" % c for c in codes_all) or "—"))
    for fam in fams:
        print("  %-7s %s" % (fam, "  ".join("%-4d" % stats[fam][c] for c in codes_all)))
    for (fam, c), (sc, msgs) in sorted(first.items()):
        print("  · %s %s: %s" % (c, sc["id"], "; ".join(msgs)))
        if a.dump:
            os.makedirs(a.dump, exist_ok=True)
            with open(os.path.join(a.dump, "%s_%s.json" % (c, sc["id"])), "w", encoding="utf-8") as f:
                json.dump(sc["req"], f, ensure_ascii=False)
    slow.sort(reverse=True)
    print("  самые долгие: %s" % ", ".join("%s %.2fс/%d" % (s[1], s[0], s[2]) for s in slow[:3]))
    cv, ci = check_calc(rng, 3 if a.quick else 12)
    print("РАСЧЁТ: нарушений %d" % len(cv))
    for c, m in cv[:8]:
        print("  · %s %s" % (c, m))
    for c, m in ci:
        print("  [INFO] %s %s" % (c, m))
    hard = sum(sum(v.values()) for v in stats.values()) + len(cv)
    return 1 if hard else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
