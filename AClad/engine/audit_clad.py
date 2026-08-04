# -*- coding: utf-8 -*-
"""Краш-аудит cladding_plan: пересечения, выход за зону, швы."""
import os
import sys, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from cladding_plan import cladding_plan

def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]

def boxes(holes):
    return [(min(p[0] for p in h), min(p[1] for p in h),
             max(p[0] for p in h), max(p[1] for p in h)) for h in holes]

T = 0.5

def check(tag, req, p):
    errs = []
    if not p.get("ok"):
        return ["%s: ОТКАЗ: %s" % (tag, p.get("error"))]
    ins = p["inserts"]
    hb = []
    x0 = y0 = 1e18; x1 = y1 = -1e18
    for c in req.get("contours") or []:
        hb += boxes(c.get("holes") or [])
        for px, py in c["outer"]:
            x0 = min(x0, px); y0 = min(y0, py)
            x1 = max(x1, px); y1 = max(y1, py)
    cells = [(i["x"], i["y"], i["x"] + i["w"], i["y"] + i["h"])
             for i in ins]
    # C1: камень в границах контура (bbox) и не в проёме
    for a in cells:
        if a[0] < x0 - T or a[2] > x1 + T or a[1] < y0 - T or \
           a[3] > y1 + T:
            errs.append("%s: КАМЕНЬ ЗА КОНТУРОМ %s" % (tag, a))
        for b in hb:
            ix = min(a[2], b[2]) - max(a[0], b[0])
            iy = min(a[3], b[3]) - max(a[1], b[1])
            if ix > T and iy > T:
                errs.append("%s: КАМЕНЬ В ПРОЁМЕ %s ∩ %s" % (tag, a, b))
    # C2: камни не пересекаются между собой
    cells.sort()
    for i in range(len(cells)):
        for j in range(i + 1, min(i + 40, len(cells))):
            a, b = cells[i], cells[j]
            if b[0] >= a[2] - T:
                break
            iy = min(a[3], b[3]) - max(a[1], b[1])
            ix = min(a[2], b[2]) - max(a[0], b[0])
            if ix > T and iy > T:
                errs.append("%s: ПЕРЕСЕЧЕНИЕ КАМНЕЙ %s ∩ %s"
                            % (tag, a, b))
    # C3: подрезка уже min_cut по ширине не ставится (В4)
    mc = float(req.get("min_cut", 150.0))
    for i in ins:
        if i["w"] < mc - T:
            errs.append("%s: КУСОК УЖЕ min_cut: w=%.0f" % (tag, i["w"]))
    return errs

SC = []
SC.append(("глухая", {"contours": [{"outer": rect(0, 0, 5400, 3000)}],
                      "tile": {"w": 600, "h": 600},
                      "gap": {"v": 8, "h": 8}}))
SC.append(("окно в центре", {"contours": [{"outer": rect(0, 0, 5400, 3000),
    "holes": [rect(2000, 900, 3400, 2100)]}],
    "tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8}}))
SC.append(("окно-простенок узкий", {"contours": [{"outer": rect(0, 0, 3000, 3000),
    "holes": [rect(200, 900, 1300, 2100), rect(1500, 900, 2800, 2100)]}],
    "tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8}}))
SC.append(("руст юзера", {"contours": [{"outer": rect(0, 0, 4000, 3000)}],
    "tile": {"w": 600, "h": 600}, "gap": {"v": 30, "h": 8},
    "vjoints": [1234.0], "hjoints": [1500.0]}))
random.seed(30826)
for i in range(120):
    w = random.randrange(1500, 12000, 300)
    h = random.randrange(1200, 9000, 300)
    holes = []
    for k in range(random.randrange(0, 3)):
        ww = random.randrange(500, 2400, 100)
        wh = random.randrange(500, 2100, 100)
        wx = random.randrange(0, max(1, w - ww), 50)
        wy = random.randrange(0, max(1, h - wh), 50)
        holes.append(rect(wx, wy, wx + ww, wy + wh))
    tw = random.choice((300, 600, 1200))
    th = random.choice((300, 600))
    SC.append(("fuzz%d %dx%d t%dx%d o%d" % (i, w, h, tw, th, len(holes)),
        {"contours": [{"outer": rect(0, 0, w, h), "holes": holes}],
         "tile": {"w": tw, "h": th},
         "gap": {"v": random.choice((6, 8, 10)),
                 "h": random.choice((6, 8, 10))}}))
allerrs = []; crash = 0
for tag, req in SC:
    try:
        p = cladding_plan(req)
    except Exception as e:
        crash += 1; allerrs.append("%s: КРАШ %r" % (tag, e)); continue
    allerrs += check(tag, req, p)
print("сценариев:", len(SC), "нарушений:", len(allerrs), "крашей:", crash)
import collections
kinds = collections.Counter(
    e.split(":")[1].strip().split(" ")[0] for e in allerrs)
for k, v in kinds.most_common():
    print("%5d  %s" % (v, k))
open("/tmp/audit_clad_errs.txt", "w").write("\n".join(allerrs))
