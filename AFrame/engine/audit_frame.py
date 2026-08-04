# -*- coding: utf-8 -*-
"""Краш-аудит frame_plan: геометрические инварианты выхода."""
import os
import sys, itertools, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from frame_plan import frame_plan

T = 1.0  # гео-допуск, мм

def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]

def boxes(holes):
    return [(min(p[0] for p in h), min(p[1] for p in h),
             max(p[0] for p in h), max(p[1] for p in h)) for h in holes]

def check(tag, req, p):
    errs = []
    if not p.get("ok"):
        return ["%s: ДВИЖОК ОТКАЗАЛ: %s" % (tag, p.get("error"))]
    sub = req.get("sub_type") or "vertical"
    rails = p["rails"]; hr = p.get("hrails") or []
    br = p["brackets"]; cl = p["clamps"]
    hb = []
    for c in req.get("contours") or []:
        hb += boxes(c.get("holes") or [])
    # R1: кусок направляющей не пересекает окно (bbox, по своей оси)
    for r in rails:
        for bx0, by0, bx1, by1 in hb:
            if bx0 + T < r["x"] < bx1 - T and \
               r["y0"] < by1 - T and r["y1"] > by0 + T:
                errs.append("%s: СТОЙКА СКВОЗЬ ОКНО x=%.0f [%.0f..%.0f]"
                            % (tag, r["x"], r["y0"], r["y1"]))
    # R2: кронштейн лежит на направляющей (verticals) или на ГП (ortho)
    for b in br:
        on_v = any(abs(b["x"] - r["x"]) <= T and
                   r["y0"] - T <= b["y"] <= r["y1"] + T for r in rails)
        on_h = any(abs(b["y"] - h["y"]) <= T and
                   h["x0"] - T <= b["x"] <= h["x1"] + T for h in hr)
        if not (on_v or on_h):
            errs.append("%s: КРОНШТЕЙН В ВОЗДУХЕ (%.0f, %.0f) %s"
                        % (tag, b["x"], b["y"], b["kind"]))
    # R3: кляммер на стойке (ось rails, Y внутри куска с допуском зазора)
    for c in cl:
        on_v = any(abs(c["x"] - r["x"]) <= T and
                   r["y0"] - 60 <= c["y"] <= r["y1"] + 60 for r in rails)
        if not on_v:
            errs.append("%s: КЛЯММЕР В ВОЗДУХЕ (%.0f, %.0f) %s"
                        % (tag, c["x"], c["y"], c["kind"]))
    # R4: дубли кляммеров в точке
    pts = {}
    for c in cl:
        k = (round(c["x"]), round(c["y"]))
        if k in pts:
            errs.append("%s: ДУБЛЬ КЛЯММЕРА в %s (%s+%s)"
                        % (tag, k, pts[k], c["kind"]))
        pts[k] = c["kind"]
    # R4b (04.08): дубли КРОНШТЕЙНОВ в точке — раньше чекера не было,
    # а на полигоне Германа их набралось 64 (смещённая оконная стойка
    # легла на собственную ось руста, стоящую ровно в edge_offset)
    ptb = {}
    for b in br:
        k = (round(b["x"]), round(b["y"]))
        if k in ptb:
            errs.append("%s: ДУБЛЬ КРОНШТЕЙНА в %s (%s+%s)"
                        % (tag, k, ptb[k], b["kind"]))
        ptb[k] = b["kind"]
    # R5: шаги кронштейнов на куске ≤ заявленного (вертикальная), крайние ~300
    if sub == "vertical":
        su = p.get("system_used") or {}
        step_max = max(float(su.get("bracket_step") or 800),
                       float(su.get("bracket_step_corner") or 800))
        for r in rails:
            ys = sorted(b["y"] for b in br
                        if abs(b["x"] - r["x"]) <= T and
                        r["y0"] - T <= b["y"] <= r["y1"] + T)
            if not ys and r["y1"] - r["y0"] > 600 + T:
                errs.append("%s: НАПРАВЛЯЮЩАЯ БЕЗ КРОНШТЕЙНОВ x=%.0f "
                            "len=%.0f" % (tag, r["x"], r["y1"] - r["y0"]))
            for a, b2 in zip(ys, ys[1:]):
                if b2 - a > step_max + T:
                    errs.append("%s: ШАГ %.0f > %.0f на x=%.0f"
                                % (tag, b2 - a, step_max, r["x"]))
    # R7: куски направляющих на одной оси не перекрываются
    import collections as _c
    byx = _c.defaultdict(list)
    for r in rails:
        byx[round(r["x"], 1)].append((r["y0"], r["y1"]))
    for xx, lst in byx.items():
        lst.sort()
        for a2, b2 in zip(lst, lst[1:]):
            if b2[0] < a2[1] - T:
                errs.append("%s: НАЛОЖЕНИЕ КУСКОВ x=%s %s-%s" %
                            (tag, xx, a2, b2))
    # R6: знаки в границах контура (bbox) с запасом выступов
    for c in req.get("contours") or []:
        pass
    return errs

SC = []
# ── сценарии: базовые и краевые ──
J6 = [300 + 600 * i for i in range(9)]   # оси 300..5100
R6 = [600.0 * i for i in range(11)]
for sub, sysn in (("vertical", "Вектор-1"), ("interfloor", "Межэтажная"),
                  ("ortho", "Ортогональная")):
    SC.append(("%s глухая" % sub, {"system": sysn, "sub_type": sub,
        "contours": [{"outer": rect(0, 0, 5400, 6000)}],
        "joints_x": list(J6), "rows_y": list(R6), "floors_y": [3000, 6000]}))
    SC.append(("%s окно в центре" % sub, {"system": sysn, "sub_type": sub,
        "contours": [{"outer": rect(0, 0, 5400, 6000),
                      "holes": [rect(2000, 1500, 3400, 2900)]}],
        "joints_x": list(J6), "rows_y": list(R6), "floors_y": [3000, 6000]}))
    SC.append(("%s окно у края" % sub, {"system": sysn, "sub_type": sub,
        "contours": [{"outer": rect(0, 0, 5400, 6000),
                      "holes": [rect(50, 1500, 1100, 2900)]}],
        "joints_x": list(J6), "rows_y": list(R6), "floors_y": [3000, 6000]}))
    SC.append(("%s два окна рядом" % sub, {"system": sysn, "sub_type": sub,
        "contours": [{"outer": rect(0, 0, 5400, 6000),
                      "holes": [rect(1200, 1500, 2400, 2900),
                                rect(2700, 1500, 3900, 2900)]}],
        "joints_x": list(J6), "rows_y": list(R6), "floors_y": [3000, 6000]}))
    SC.append(("%s окно через перекрытие" % sub, {"system": sysn,
        "sub_type": sub,
        "contours": [{"outer": rect(0, 0, 5400, 6000),
                      "holes": [rect(2000, 2400, 3400, 3600)]}],
        "joints_x": list(J6), "rows_y": list(R6), "floors_y": [3000, 6000]}))
    SC.append(("%s узкая зона" % sub, {"system": sysn, "sub_type": sub,
        "contours": [{"outer": rect(0, 0, 700, 9000)}],
        "joints_x": [350], "rows_y": list(R6), "floors_y": [3000, 6000, 9000]}))
    SC.append(("%s окно почти во всю зону" % sub, {"system": sysn,
        "sub_type": sub,
        "contours": [{"outer": rect(0, 0, 3000, 3000),
                      "holes": [rect(150, 150, 2850, 2850)]}],
        "joints_x": [600, 1200, 1800, 2400], "rows_y": list(R6),
        "floors_y": [3000]}))

random.seed(20260803)
for i in range(120):
    w = random.randrange(1800, 12000, 300)
    h = random.randrange(2400, 15000, 300)
    holes = []
    for k in range(random.randrange(0, 3)):
        ww = random.randrange(600, 2400, 100)
        wh = random.randrange(600, 2400, 100)
        wx = random.randrange(0, max(1, w - ww), 50)
        wy = random.randrange(0, max(1, h - wh), 50)
        holes.append(rect(wx, wy, wx + ww, wy + wh))
    joints = [x for x in range(300, w, 600)]
    rows = [600.0 * k for k in range(0, h // 600 + 1)]
    floors = [3000.0 * k for k in range(1, h // 3000 + 1)]
    sub, sysn = random.choice((("vertical", "Вектор-1"),
                               ("interfloor", "Межэтажная"),
                               ("ortho", "Ортогональная")))
    SC.append(("fuzz%d %s %dx%d o%d" % (i, sub, w, h, len(holes)),
               {"system": sysn, "sub_type": sub,
                "contours": [{"outer": rect(0, 0, w, h), "holes": holes}],
                "joints_x": joints, "rows_y": rows, "floors_y": floors}))

# ── 04.08 (D-сверка полигона): ПАРЫ БЛИЗКИХ ОСЕЙ У ГРАНИ ПРОЁМА ──
# Пропуск прежних 141: ось руста ровно в edge_offset (100) от грани —
# она же цель смещения соседней оси, стоящей ближе. Дают одну X.
# На полигоне такие пары идут с шагом 96 мм (16 осей из 39).
for _d in (4.0, 40.0, 96.0, 99.0):
    for _sub, _sys in (("vertical", "Вектор-1"), ("vertical", "Standart")):
        SC.append(("пара осей у грани Δ=%.0f %s" % (_d, _sys),
                   {"system": _sys, "sub_type": _sub,
                    "contours": [{"outer": rect(0, 0, 4000, 6000),
                                  "holes": [rect(1000, 500, 2800, 2700),
                                            rect(1000, 3500, 2800,
                                                 5700)]}],
                    "joints_x": [900.0, 1000.0 - _d, 2800.0 + _d,
                                 2900.0, 3500.0],
                    "rows_y": [600.0 * i for i in range(11)],
                    "floors_y": [3000.0]}))

allerrs = []
crash = 0
for tag, req in SC:
    try:
        p = frame_plan(req)
    except Exception as e:
        allerrs.append("%s: ИСКЛЮЧЕНИЕ %r" % (tag, e)); crash += 1
        continue
    allerrs += check(tag, req, p)
print("сценариев:", len(SC), "нарушений:", len(allerrs), "крашей:", crash)
import collections
kinds = collections.Counter(e.split(":")[1].strip().split(" (")[0].split(" x=")[0].split(" в ")[0] for e in allerrs)
for k, v in kinds.most_common():
    print("%5d  %s" % (v, k))
with open("/tmp/audit_errs.txt", "w") as f:
    f.write("\n".join(allerrs))
