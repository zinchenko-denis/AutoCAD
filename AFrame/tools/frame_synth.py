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
     rails_fixed) дают те же кляммеры, что полная (23.09b);
 F17 межэтажная (Герман 30.09, ответ на (а) PDF №29; АТР «Вектор-1», тип 4): у каждой боковой
     грани проёма, за которой стена, — вертикаль вдоль откоса: в полосе до 150 мм от грани (простенок
     уже 200 — в любом месте простенка), по высоте от перекрытия под проёмом до перекрытия над ним
     (нет перекрытия — до низа/верха стены), стыки — не шире 12 мм.
Плитка (Герман 29.09, ответ по №27) — отдельный проход со своим генератором
случайных чисел (прежние сценарии не сдвигаются), те же F1–F15 плюс:
 T1  вертикальные направляющие в [край+100, край−100] (кроме оконных у граней),
     соседние оси не дальше max(шаг, угловой шаг)+50;
 T2  хлысты: куски прогона стыкуются встык, каждый не длиннее хлыста; стык — на
     вертикальном профиле через высоту шины (Герман 29.09j, 9в), хлыст самый длинный:
     дальше в пределах хлыста профиля нет (кроме последнего в прогоне);
 T3  стартовая = горизонтальные кромки низа стены минус проёмы, концевая —
     кромки верха минус проёмы (оракул shapely, ±1 мм на прогон);
 T4  рядовая на отметке ряда = ширина стены на этой отметке минус проёмы
     (ряды дальше 20 мм от кромок низа/верха);
 T5  кляммеров нет; T6 сводка шин сходится со списком.
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
REFUSED = Counter()  # explicit structural refusals, never counted as built facades
DRAWING_ARRAYS = ("rails", "hrails", "brackets", "clamps", "fittings", "per_zone")


def safe_refusal(req, res):
    """Recognise addressed, evidence-bearing refusals; arbitrary errors fail.

    This checks the public contract, independently of frame_topology's helpers.
    Deterministic positive controls below additionally prevent deny-all from
    passing a random suite dominated by short/window-cut members.
    """
    if res.get("ok") is not False or not res.get("error") or res.get("calc_report") or \
            any(res.get(k) for k in DRAWING_ARRAYS):
        return False
    code = res.get("error_code")
    counts = res.get("unsupported_counts") or {}
    if code == "E_UNSUPPORTED_SHINA":
        return (req.get("cladding") in ("concrete", "clinker")
                and set(counts) == {"pieces", "joints"}
                and all(isinstance(n, int) and n >= 0 for n in counts.values())
                and sum(counts.values()) > 0)
    if req.get("calc") is None or req.get("parts") == "clamps":
        return False
    if code == "E_CALC_NOT_PASSED":
        # A missing preset, invalid input or arbitrary exception is not a
        # structural refusal of a valid role.
        return ("ни один шаг до " in res["error"]
                or "нет единого профиля, проходящего в рядовой и угловой зонах" in res["error"])
    sub = req.get("sub_type", "vertical")
    unsupported = res.get("unsupported") or []
    model = res.get("static_model") or {}
    if code == "E_CALC_TOPOLOGY_UNSUPPORTED" and sub == "interfloor":
        # Existing NSP free-end refusal takes precedence over the model guard.
        if counts != {"nsp_pieces": len(unsupported)} or not unsupported:
            return False
        for member in unsupported:
            supports = member.get("support_y") or []
            reason = member.get("reason")
            if reason == "fewer_than_two_supports" and len(supports) < 2:
                continue
            if reason == "unsupported_free_end" and len(supports) >= 2 and \
                    max(member.get("bottom_free", 0), member.get("top_free", 0)) > member.get("match_tolerance", math.inf):
                continue
            return False
        return True
    if model.get("status") != "not_verified" or model.get("scheme") != sub or \
            model.get("pieces_merged") is not False or model.get("fixed_sliding") != "not_modeled" or \
            model.get("splice_continuity") != "not_modeled":
        return False
    screening = model.get("geometric_screening") or {}
    if screening.get("status") != "refused" or screening.get("reasons") != unsupported or not unsupported:
        return False
    if code == "E_CALC_MODEL_UNCONFIRMED":
        return (sub == "interfloor" and unsupported == [{"reason": "interfloor_model_unconfirmed"}]
                and counts == {"vertical_members": 0} and bool(model.get("members")))
    if code != "E_CALC_TOPOLOGY_UNSUPPORTED" or sub not in ("vertical", "ortho"):
        return False
    members = {m.get("index"): m for m in model.get("members") or []}
    if counts != {"vertical_members": len({r.get("member_index") for r in unsupported})}:
        return False
    for issue in unsupported:
        member = members.get(issue.get("member_index"))
        if member is None:
            return False
        supports = member.get("support_y") or []
        spans = len(supports) - 1 if supports else 0
        actual = "multi" if spans >= 4 else str(spans)
        if supports != sorted(set(supports)) or member.get("support_count") != len(supports) or \
                issue.get("support_count") != len(supports) or issue.get("span_count") != spans or \
                issue.get("actual_span_class") != actual:
            return False
        reason = issue.get("reason")
        if reason == "insufficient_supports" and len(supports) < 2:
            continue
        if reason == "single_span_unsupported" and len(supports) == 2:
            continue
        if reason == "span_class_mismatch" and spans >= 2 and \
                issue.get("coefficient_span_class") in ("2", "3", "multi") and \
                actual != issue["coefficient_span_class"]:
            continue
        if reason == "support_interval_exceeds_calculated_span" and sub == "ortho" and spans >= 2 and \
                max(b - a for a, b in zip(supports, supports[1:])) > issue.get("coefficient_span", math.inf) + 0.5:
            continue
        return False
    return True


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


TILE_SUBS = ["vertical_l", "interfloor", "ortho"]


def make_tile_scenario(rng, fam, sub, idx):
    """Тот же сценарий, облицовка — плитка: направляющие заданным шагом,
    ряды шин — сетка клинкера/бетона (модуль 75–92), хлыст 2500."""
    sc = make_scenario(rng, fam, sub, idx)
    req = sc["req"]
    step = float(rng.choice([400, 500, 600, 700]))
    scc = float(rng.choice([0, 0, 300, 400]))
    y0, y1 = Polygon(sc["outer"]).bounds[1], Polygon(sc["outer"]).bounds[3]
    pitch = rng.choice([75.0, 88.0, 92.0])
    ph = rng.uniform(0, pitch)
    rows = [round(y0 + ph + k * pitch, 3) for k in range(1, int((y1 - y0) / pitch) + 1)
            if y0 < y0 + ph + k * pitch < y1]
    req.update({"cladding": rng.choice(["clinker", "concrete"]), "tile_step_x": step,
                "tile_step_x_corner": scc, "tile_whip": 2500.0, "parts": "frame",
                "tile_rail_brand": "ШК-%d" % idx, "rows_y": rows})
    sc["tile"] = {"step": step, "sc": scc, "whip": 2500.0, "rows": rows}
    sc["id"] = "tile-" + sc["id"]
    return sc


def _hedges_oracle(wall):
    """Горизонтальные кромки стены: (LineString, 'низ'|'верх') по shapely."""
    out = []
    cs = list(wall.exterior.coords)
    for (xa, ya), (xb, yb) in zip(cs, cs[1:]):
        if abs(yb - ya) > 0.5 or abs(xb - xa) <= 1e-6:
            continue
        xm, ym = (xa + xb) / 2.0, (ya + yb) / 2.0
        up = wall.contains(Point(xm, ym + 2.0))
        dn = wall.contains(Point(xm, ym - 2.0))
        if up != dn:
            out.append((LineString([(xa, ym), (xb, ym)]), "низ" if up else "верх"))
    return out


def check_tile(sc, res):
    """T1–T6 для плитки → список (код, текст)."""
    bad = []
    t = sc["tile"]
    wall, ops = _geom(sc)
    x0, y0, x1, y1 = wall.bounds
    rails, hr = res["rails"], res.get("hrails") or []
    boxes = [Polygon(h).bounds for h in sc["holes"]]
    win_x = set()
    for bx0, _by0, bx1, _by1 in boxes:
        win_x |= {round(bx0 - 100.0, 1), round(bx1 + 100.0, 1)}
    axes = sorted({round(r["x"], 1) for r in rails})
    grid = [a for a in axes if a not in win_x]
    if sc["sub"] == "interfloor":
        # 30.09b: НСП вдоль откоса в узком простенке — посередине простенка (может быть ближе 100 к краю)
        grid = [a for a in grid if not any(bx0 - 200.0 - T <= a < bx0 or bx1 < a <= bx1 + 200.0 + T
                                           for bx0, _by0, bx1, _by1 in boxes)]
    for a in grid:
        if a < x0 + 100.0 - T or a > x1 - 100.0 + T:
            bad.append(("T1", "ось x=%.0f ближе 100 мм к краю зоны" % a))
            break
    lim = max(t["step"], t["sc"]) + 50.0 + T
    for a, b in zip(axes, axes[1:]):
        if b - a > lim and wall.intersection(LineString([((a + b) / 2.0, y0 - 1), ((a + b) / 2.0, y1 + 1)])).length > T:
            bad.append(("T1", "между осями %.0f и %.0f пролёт %.0f > %.0f" % (a, b, b - a, lim)))
            break
    sh = [h for h in hr if h["kind"].startswith("шина")]
    runs = defaultdict(list)
    for h in sh:
        runs[(h["kind"], h["run"])].append(h)
    for key, ps in runs.items():
        ps.sort(key=lambda h: h["x0"])
        for a, b in zip(ps, ps[1:]):
            if abs(a["x1"] - b["x0"]) > T or abs(a["y"] - b["y"]) > T:
                bad.append(("T2", "прогон %s: куски не встык (%.0f / %.0f)" % (key, a["x1"], b["x0"])))
                break
        if any(h["len"] > t["whip"] + T for h in ps):
            bad.append(("T2", "прогон %s: кусок длиннее хлыста %.0f" % (key, t["whip"])))
        yy, sa, sb = ps[0]["y"], ps[0]["x0"], ps[-1]["x1"]
        sup = sorted({round(r["x"], 1) for r in rails
                      if r["y0"] - 20.0 - 1e-6 <= yy <= r["y1"] + 20.0 + 1e-6 and sa + T < r["x"] < sb - T})
        prev = sa
        for h in ps[1:]:
            j = h["x0"]
            # досягаемость — строго по длине хлыста (2500,2 мм — уже длиннее хлыста)
            reach = [x for x in sup if prev + 300.0 - 1e-6 <= x <= prev + t["whip"] + 1e-6]
            on = any(abs(j - x) <= T for x in sup)
            if reach and not on:
                bad.append(("T2", "прогон %s: стык %.0f не на направляющей (в пределах хлыста есть %s)"
                            % (key, j, reach[-3:])))
                break
            longer = [x for x in reach if x > j + T and x < sup[-1] - T]
            if on and longer:
                bad.append(("T2", "прогон %s: стык %.0f, а дальше в пределах хлыста направляющая %.0f"
                            % (key, j, longer[-1])))
                break
            prev = j
    ops_u = unary_union([Polygon([(a, b), (c, b), (c, d), (a, d)]) for a, b, c, d in boxes]) if boxes else None
    exp = {"низ": 0.0, "верх": 0.0}
    for ln, side in _hedges_oracle(wall):
        exp[side] += (ln.difference(ops_u) if ops_u is not None else ln).length
    # 29.09l (Герман, 9б): стартовая — и над каждым проёмом, концевая — и под ним, по ширине
    # проёма там, где за кромкой стена (пробой в 2 мм за кромкой, мимо других проёмов)
    for bx0, by0, bx1, by1 in boxes:
        for yy, side in ((by1 + 2.0, "низ"), (by0 - 2.0, "верх")):
            ln = LineString([(bx0, yy), (bx1, yy)]).intersection(wall)
            exp[side] += (ln.difference(ops_u) if ops_u is not None else ln).length
    got_s = sum(h["len"] for h in sh if h["kind"] == "шина стартовая")
    got_e = sum(h["len"] for h in sh if h["kind"] == "шина концевая")
    # 29.09q: шина без направляющей удлиняется (Герман) — по кромкам это нижняя граница
    if got_s < exp["низ"] - T * (1 + len(runs)):
        bad.append(("T3", "стартовая %.0f мм, по кромкам низа %.0f" % (got_s, exp["низ"])))
    if got_e < exp["верх"] - T * (1 + len(runs)):
        bad.append(("T3", "концевая %.0f мм, по кромкам верха %.0f" % (got_e, exp["верх"])))
    # T7 (29.09q, Герман: «если шина не попадает ни на одну направляющую — удлинить»): кусок без
    # направляющей под ним допустим, только если и в его пролёте стены на этой высоте их нет
    for key, ps in runs.items():
        yy, sa, sb = ps[0]["y"], ps[0]["x0"], ps[-1]["x1"]
        sup = [r["x"] for r in rails if r["y0"] - 20.0 - 1e-6 <= yy <= r["y1"] + 20.0 + 1e-6]
        if any(sa - T <= x <= sb + T for x in sup):
            continue
        pr = 2.0 if key[0] == "шина стартовая" else (-2.0 if key[0] == "шина концевая" else 0.0)
        ln = LineString([(x0 - 1, yy + pr), (x1 + 1, yy + pr)]).intersection(wall)
        for b in boxes:
            if b[1] + 1e-6 < yy + pr < b[3] - 1e-6:
                ln = ln.difference(Polygon([(b[0], b[1]), (b[2], b[1]), (b[2], b[3]), (b[0], b[3])]))
        xm = (sa + sb) / 2.0
        # путь к направляющей занят шиной той же высоты (±20 мм), которой рядовая не перекрывает
        # (стартовая/концевая) — удлинять некуда, это не нарушение
        others = [h for h in sh if abs(h["y"] - yy) < 20.0 and (h["kind"], h["run"]) != key and
                  (key[0] != "шина рядовая" or h["kind"] != "шина рядовая")]

        def _free(a, b):
            return not any(h["x0"] < b - T and h["x1"] > a + T for h in others)
        for g in getattr(ln, "geoms", [ln]):
            if g.is_empty:
                continue
            gx = [c[0] for c in g.coords]
            if not (min(gx) - T <= xm <= max(gx) + T):
                continue
            lft = [x for x in sup if min(gx) - T <= x < sa - T]
            rgt = [x for x in sup if sb + T < x <= max(gx) + T]
            if (lft and _free(max(lft), sa)) or (rgt and _free(sb, min(rgt))):
                bad.append(("T7", "прогон %s y=%.0f: под шиной нет направляющей, а в пролёте стены есть — не "
                            "удлинена" % (key, yy)))
                break
    ey = [ln.coords[0][1] for ln, _s in _hedges_oracle(wall)] + \
        [v for b in boxes for v in (b[1], b[3])]      # 29.09l: у кромок проёмов — свои шины
    for yy in t["rows"]:
        if any(abs(yy - e) < 20.0 + T for e in ey):
            continue
        ln = LineString([(x0 - 1, yy), (x1 + 1, yy)]).intersection(wall)
        if ops_u is not None:
            ln = ln.difference(ops_u)
        got = sum(h["len"] for h in sh if h["kind"] == "шина рядовая" and abs(h["y"] - yy) <= T)
        # 29.09l: прогон короче 5 мм — шум контура / острие фронтона, шиной не считается
        parts = list(getattr(ln, "geoms", [ln]))
        exp_len = sum(g.length for g in parts if g.length >= 5.0)
        if abs(got - exp_len) > 2 * T:
            bad.append(("T4", "ряд y=%.0f: шин %.0f мм, стены без проёмов %.0f" % (yy, got, exp_len)))
            break
    if res["clamps"]:
        bad.append(("T5", "у плитки %d кляммеров" % len(res["clamps"])))
    sm = res["summary"]
    if sm.get("shina_pieces") != len(sh) or abs(sm.get("shina_lm", -1) - sum(h["len"] for h in sh) / 1000.0) > 0.011 or \
            sm.get("hguides") != len(hr) - len(sh):
        bad.append(("T6", "сводка шин не сходится со списком"))
    return bad


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
            # 23.09n (Герман): кусок ≤ 300 мм без ГП допустим, длиннее — ГП обязателен
            if r["len"] <= 300.0 + T:
                continue
            if not any(h["x0"] - T <= r["x"] <= h["x1"] + T and r["y0"] - T <= h["y"] <= r["y1"] + T for h in hr):
                bad.append(("F7", "вертикаль x=%.0f [%.0f..%.0f] не опирается ни на один ГП" % (r["x"], r["y0"], r["y1"])))
        y0 = Polygon(sc["outer"]).bounds[1]
        tops = {round(h[3] + 100.0, 1) for h in (Polygon(q).bounds for q in sc["holes"])}
        # 23.09n (Герман): доп. ГП у верха — на 300 ниже верха висевшего куска
        tops |= {round(r["y1"] - 300.0, 1) for r in rails}
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
    if sc["sub"] == "interfloor":
        bad += _check_flanks(sc, res, wall)
    return bad


def _check_flanks(sc, res, wall):
    """F17: вертикали межэтажной вдоль боковых откосов (оракул — shapely, отметки из запроса)."""
    bad = []
    req = sc["req"]
    x0, y0, x1, y1 = wall.bounds
    floors = sorted(float(f) for f in (req.get("floors_y") or []))
    if not floors and float(req.get("floor_step") or 0) > 0:
        fs, f = float(req["floor_step"]), y0 + float(req["floor_step"])
        while f < y1 - 1e-6:
            floors.append(f)
            f += fs
    if not floors:
        return bad                               # межэтажная без отметок — контур пропущен
    boxes = [Polygon(h).bounds for h in sc["holes"]]
    by_x = defaultdict(list)
    for r in res["rails"]:
        by_x[round(r["x"], 3)].append((r["y0"], r["y1"]))
    for bi, (bx0, by0, bx1, by1) in enumerate(boxes):
        ym = (by0 + by1) / 2.0
        others = [Polygon([(b[0], b[1]), (b[2], b[1]), (b[2], b[3]), (b[0], b[3])])
                  for k, b in enumerate(boxes) if k != bi]
        for sd, edge in ((-1.0, bx0), (1.0, bx1)):
            pr = Point(edge + 2.0 * sd, ym)
            if not wall.contains(pr) or any(o.contains(pr) for o in others):
                continue                         # за гранью не стена
            ray = LineString([(edge, ym), (edge + sd * 1e7, ym)]).intersection(wall)
            for o in others:
                ray = ray.difference(o)
            far = None
            for g in getattr(ray, "geoms", [ray]):
                if g.is_empty:
                    continue
                gx = [c[0] for c in g.coords]
                if min(gx) - T <= edge <= max(gx) + T:
                    far = max(gx) if sd > 0 else min(gx)
            if far is None:
                continue
            wp = abs(far - edge)
            if wp < 60.0 - T:
                continue                         # уже самого узкого С-профиля (60) — не помещается
            lo_b, hi_b = sorted((edge, far) if wp <= 200.0 + T else (edge, edge + sd * 150.0))
            lo_n = max([f for f in floors if f <= by0 + T], default=None)
            hi_n = min([f for f in floors if f >= by1 - T], default=None)
            good = False
            for x, iv in by_x.items():
                if not (lo_b - T <= x <= hi_b + T):
                    continue
                col = LineString([(x, y0 - 1.0), (x, y1 + 1.0)]).intersection(wall)
                seg = None
                for g in getattr(col, "geoms", [col]):
                    gy = [c[1] for c in g.coords] if not g.is_empty else []
                    if gy and min(gy) - T <= ym <= max(gy) + T:
                        seg = (min(gy), max(gy))
                if seg is None:
                    continue
                need_lo = seg[0] if lo_n is None else max(lo_n, seg[0])
                need_hi = seg[1] if hi_n is None else min(hi_n, seg[1])
                need = [(need_lo, need_hi)]
                for ob in boxes:                   # чужие проёмы на этой оси (и ось на их грани) — не нужно
                    if ob[0] - T < x < ob[2] + T:
                        need = [(a, b) for a0, b0 in need for a, b in ((a0, min(b0, ob[1])), (max(a0, ob[3]), b0))
                                if b - a > T]
                have = sorted(iv)
                cov = []
                for a, b in have:
                    if cov and a <= cov[-1][1] + 12.0:
                        cov[-1][1] = max(cov[-1][1], b)
                    else:
                        cov.append([a, b])
                if all(any(c0 - 12.0 <= a and b <= c1 + 12.0 for c0, c1 in cov) for a, b in need):
                    good = True
                    break
            if not good:
                bad.append(("F17", "межэтажная: у %s грани проёма x=%.0f [%.0f..%.0f] нет вертикали вдоль "
                            "откоса от перекрытия до перекрытия" % ("левой" if sd < 0 else "правой",
                                                                    edge, by0, by1)))
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
        if res.get("error_code") == "E_UNSUPPORTED_SHINA" and sc.get("tile"):
            counts = res.get("unsupported_counts") or {}
            valid = bool(res.get("error")) and sum(counts.values()) > 0 and \
                    not any(res.get(k) for k in ("rails", "hrails", "brackets", "clamps", "fittings"))
            reordered = _reordered(sc["req"], rng)
            for q in (sc["req"], _shifted(sc["req"], *SHIFT), reordered):
                check = fre.run(json.loads(json.dumps(q)))
                valid = valid and not check.get("ok") and check.get("error_code") == "E_UNSUPPORTED_SHINA" and \
                        check.get("unsupported_counts") == counts and \
                        not any(check.get(k) for k in ("rails", "hrails", "brackets", "clamps", "fittings"))
            if valid:
                REFUSED[sc["sub"]] += 1
                return [], dt, 0
            return [("F0", "неустойчивый или непустой отказ по опорам шин")], dt, 0
        return [("F0", "движок отказал: %s" % res.get("error"))], dt, 0
    step_max = None
    su = res.get("system_used") or {}
    if sc["sub"] == "vertical":
        step_max = max(float(su.get("bracket_step") or 0), float(su.get("bracket_step_corner") or 0)) or None
    viol += check_place(sc, res, step_max)
    if sc.get("tile"):
        viol += check_tile(sc, res)
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
    if q5["rails_fixed"] and not sc.get("tile"):      # у плитки кляммеров нет
        r5 = fre.run(q5)
        xs = [p[0] for p in sc["outer"]]
        a = Counter((round(c["x"], 3), round(c["y"], 3), c["kind"], c.get("orient")) for c in res["clamps"]
                    if min(xs) - 1e-6 <= c["x"] <= max(xs) + 1e-6)
        b = Counter((round(c["x"], 3), round(c["y"], 3), c["kind"], c.get("orient"))
                    for c in (r5.get("clamps") or []))
        if not r5.get("ok") or a != b:
            viol.append(("F16", "только кляммеры по направляющим ≠ полной расстановке (%s)"
                         % (sum(((a - b) + (b - a)).values()) if r5.get("ok") else r5.get("error"))))
        q5["rails_fixed"] = [{k: r[k] for k in ("x", "y0", "y1", "clamp_role") if k in r}
                             for r in res["rails"]]
        tagged = fre.run(q5)
        ctag = Counter((round(c["x"], 3), round(c["y"], 3), c["kind"], c.get("orient"))
                       for c in (tagged.get("clamps") or []))
        if not tagged.get("ok") or a != ctag:
            viol.append(("F16", "только кляммеры по направляющим с сохранённой ролью ≠ полной расстановке"))
    n = len(res["rails"]) + len(res["brackets"]) + len(res["clamps"]) + len(res.get("hrails") or [])
    return viol, dt, n


def check_static_contract():
    """Fixed geometry with known supports, separate from random refusals.

    Test dimensions are synthetic examples, not engineering design limits.
    All accepted calculation results must still declare an unverified model.
    """
    bad, outcomes = [], Counter()
    base = dict(op="frame", zones=[], joints_x=[300, 900, 1500, 2100],
                rows_y=[600, 1200, 1800, 2400], floors_y=[], floor_step=0,
                calc=dict(wind_region="II", terrain="B", height=30,
                          q_clad=20, offset=200, na_max=3000))
    for sub in ("vertical", "ortho"):
        for height, reason in ((3000, None), (6000, None), (140, "insufficient_supports"),
                               (600, "insufficient_supports"), (1000, "single_span_unsupported"),
                               (1800, "span_class_mismatch")):
            req = dict(base, sub_type=sub, system="Вектор-1" if sub == "vertical" else "Ортогональная",
                       contours=[dict(id="O", pts=rect(0, 0, 2400, height))])
            res = fre.run(json.loads(json.dumps(req)))
            tag = "%s/%s" % (sub, height)
            if reason is None:
                model = res.get("calc_report", {}).get("static_model") or {}
                if not res.get("ok") or not res.get("rails") or not res.get("brackets") or \
                        model.get("status") != "not_verified" or model.get("pieces_merged") is not False or \
                        model.get("geometric_screening", {}).get("status") != "passed":
                    bad.append(("S1", "%s: обязательный положительный контроль не построен: %s" % (tag, res.get("error"))))
                    continue
                # Count actual supports on the emitted pieces independently
                # of the screening inventory; joints must not merge pieces.
                for member, rail in zip(model.get("members") or [], res["rails"]):
                    if sub == "vertical":
                        ys = {b["y"] for b in res["brackets"] if abs(b["x"] - rail["x"]) <= 0.5
                              and rail["y0"] - 0.5 <= b["y"] <= rail["y1"] + 0.5}
                    else:
                        ys = {h["y"] for h in res["hrails"] if h["kind"] == "ГП-40-40"
                              and h["x0"] - 0.5 <= rail["x"] <= h["x1"] + 0.5
                              and rail["y0"] - 0.5 <= h["y"] <= rail["y1"] + 0.5}
                    if member.get("support_y") != sorted(ys) or len(ys) < 3:
                        bad.append(("S1", "%s: опоры отчёта не совпадают с выданными элементами" % tag))
                if len(model.get("members") or []) != len(res["rails"]):
                    bad.append(("S1", "%s: стыки объединили отдельные расчётные куски" % tag))
                outcomes["successful_calc/" + sub] += 1
            else:
                actual = {u.get("reason") for u in res.get("unsupported") or []}
                if not safe_refusal(req, res) or res.get("error_code") != "E_CALC_TOPOLOGY_UNSUPPORTED" or actual != {reason}:
                    bad.append(("S2", "%s: ожидался точный отказ %s, получен %s/%s" %
                                (tag, reason, res.get("error_code"), sorted(str(r) for r in actual))))
                else:
                    outcomes["safe_refusal/" + sub + "/" + reason] += 1
    req = dict(base, sub_type="interfloor", system="Межэтажная", floors_y=[0, 1500, 3000],
               contours=[dict(id="O", pts=rect(0, 0, 2400, 3000))])
    res = fre.run(req)
    if not safe_refusal(req, res) or res.get("error_code") != "E_CALC_MODEL_UNCONFIRMED":
        bad.append(("S2", "межэтажная с геометрическими опорами: нет адресного отказа неподтверждённой модели"))
    else:
        outcomes["safe_refusal/interfloor/model_unconfirmed"] += 1
    manual = dict(req)
    manual.pop("calc")
    result = fre.run(manual)
    if not result.get("ok") or not result.get("rails") or not result.get("hrails") or result.get("calc_report"):
        bad.append(("S1", "межэтажная вручную: обязательный положительный контроль не построен"))
    else:
        outcomes["successful_manual/interfloor"] += 1
        clamps = fre.run(dict(req, parts="clamps", rails_fixed=result["rails"]))
        key = lambda c: (c["x"], c["y"], c["kind"], c.get("orient"))
        if not clamps.get("ok") or Counter(map(key, clamps.get("clamps") or [])) != Counter(map(key, result["clamps"])) or \
                any(clamps.get(k) for k in ("rails", "hrails", "brackets", "fittings")):
            bad.append(("S1", "межэтажная: повтор только кляммеров изменил результат ручной расстановки"))
        else:
            outcomes["successful_clamps/interfloor"] += 1
    return bad, outcomes


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
    REFUSED.clear()
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=2309)
    ap.add_argument("--n", type=int, default=24, help="сценариев на пару (форма, подсистема)")
    ap.add_argument("--dump", default=None)
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--allow", default="",
                    help="коды известных открытых вопросов через запятую (напр. F7,F16): "
                         "печатаются, но код выхода 0 (для CI; не скрывать — отчёт в логе)")
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
    # плитка (Герман 29.09): свой генератор — прежние сценарии не сдвигаются
    rng_t = random.Random(a.seed + 2909)
    tstats, tfirst, ttotal = Counter(), {}, 0
    for fam in fams:
        for sub in TILE_SUBS:
            for i in range(n):
                sc = make_tile_scenario(rng_t, fam, sub, i)
                viol, dt, ne = run_scenario(sc, rng_t)
                ttotal += 1
                elems += ne
                slow.append((dt, sc["id"], ne))
                for c in Counter(c for c, _ in viol):
                    tstats[c] += 1
                    stats["плитка"][c] += 1
                    if c not in tfirst:
                        tfirst[c] = (sc, [m for cc, m in viol if cc == c][:3])
    print("ПЛИТКА: сценариев %d, нарушений %s" % (ttotal, dict(tstats) or "нет"))
    print("  построено %d; безопасно ОТКЛОНЕНО (не выдано): %d %s" %
          (ttotal - sum(REFUSED.values()), sum(REFUSED.values()), dict(REFUSED)))
    for c, (sc, msgs) in sorted(tfirst.items()):
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
    sv, so = check_static_contract()
    print("СТАТИЧЕСКИЙ КОНТРАКТ: нарушений %d" % len(sv))
    for outcome, count in sorted(so.items()):
        print("  [RESULT] %s: %d" % (outcome, count))
    for c, m in sv:
        print("  · %s %s" % (c, m))
    allow = {c.strip() for c in a.allow.split(",") if c.strip()}
    hard = sum(n for v in stats.values() for c, n in v.items() if c not in allow) + len(cv) + len(sv)
    known = sum(n for v in stats.values() for c, n in v.items() if c in allow)
    if known:
        print("ИЗВЕСТНЫЕ открытые вопросы (--allow %s): %d сценариев — см. выше" % (",".join(sorted(allow)), known))
    return 1 if hard else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
