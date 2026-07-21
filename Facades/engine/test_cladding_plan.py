# -*- coding: utf-8 -*-
"""Юниты фазы 2 (облицовка): cladding_plan. Запуск: PYTHONUTF8=1
python3 test_cladding_plan.py. Геометрия проверяется руками посчитанными
раскладками (docs/CLADDING.md)."""
import sys
from cladding_plan import cladding_plan

_n = 0


def ok(cond, msg):
    global _n
    _n += 1
    if not cond:
        print("FAIL:", msg)
        sys.exit(1)


def near(a, b, t=1e-6):
    return abs(a - b) <= t


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def plan(**kw):
    # min_cut НЕ задаётся: тесты работают на боевом дефолте 150 (В4)
    base = {"tile": {"w": 600, "h": 600}, "gap": {"v": 10, "h": 10},
            "datum": 0.0, "mode": "edge"}
    base.update(kw)
    return cladding_plan(base)


# ── C1: прямоугольник 3040×1210, руст 10 — ровно 5×2 целых ──
p = plan(contours=[{"outer": rect(0, 0, 3040, 1210)}])
ok(p["ok"] and p["summary"]["tiles"] == 10 and p["summary"]["cut"] == 0,
   "C1: 5×2 целых (%s)" % p["summary"])
ok(sorted({t["x"] for t in p["inserts"]}) == [0, 610, 1220, 1830, 2440] and
   sorted({t["y"] for t in p["inserts"]}) == [0, 610],
   "C1: сетка от края и от датума")

# ── C2: датум 300 — ниже не кладём (note), верхний ряд подрезан ──
p = plan(contours=[{"outer": rect(0, 0, 610, 1210)}], datum=300.0)
ys = sorted({(t["y"], t["h"]) for t in p["inserts"]})
ok(ys == [(300.0, 600.0), (910.0, 300.0)],
   "C2: ряды от датума, верх подрезан (%s)" % ys)
ok(any("ниже отметки старта на 300" in n for n in p["notes"]),
   "C2: note про низ")

# ── C3: окно в среднем ряду (edge): примыкающие камни отступают от
#    окна на верт. руст (П4, 21.07) — подрезки 480/520, не 490/530 ──
p = plan(contours=[{"outer": rect(0, 0, 3040, 1820),
                    "holes": [rect(1100, 610, 1900, 1210)]}])
mid = sorted([t for t in p["inserts"] if near(t["y"], 610)],
             key=lambda t: t["x"])
ok(len(mid) == 4, "C3: средний ряд 4 камня (%d)" % len(mid))
ok(near(mid[1]["x"], 610) and near(mid[1]["w"], 480) and
   near(mid[2]["x"], 1910) and near(mid[2]["w"], 520),
   "C3: подрезки у окна 480/520 со швом (%s)"
   % [(t["x"], t["w"]) for t in mid])
ok(all(near(t["w"], 600) for t in p["inserts"] if not near(t["y"], 610)),
   "C3: крайние ряды целые")

# окно, выровненное ЛЕВОЙ гранью в сетку и по рядам: слева и по
# вертикали швы совпали с сеткой (целые), справа от окна камень
# столбца поджат швом окна → 590 (П4)
p = plan(contours=[{"outer": rect(0, 0, 3040, 1820),
                    "holes": [rect(1220, 610, 1830, 1210)]}])
ok(p["summary"]["cut"] == 1 and p["summary"]["tiles"] == 14,
   "C3b: окно в сетке — один поджатый шов справа (%s)" % p["summary"])
c3b = [t for t in p["inserts"] if near(t["y"], 610) and t["x"] > 1500]
ok(near(c3b[0]["x"], 1840) and near(c3b[0]["w"], 590),
   "C3b: камень справа от окна 1840/590 (%s)"
   % [(t["x"], t["w"]) for t in c3b])

# ── C4: «от проёмов» — двери во всю высоту ряда; камни отступают от
#    дверей на шов (П4); простенок между швами 1380: остаток 160 <
#    300 → В5-B: целая в середине, подрезки (160+600)/2=380 ──
holes4 = [rect(200, 0, 800, 600), rect(2200, 0, 2800, 600)]
p = plan(contours=[{"outer": rect(0, 0, 3000, 600), "holes": holes4}],
         mode="openings")
row = sorted(p["inserts"], key=lambda t: t["x"])
ok([(t["x"], t["w"]) for t in row] ==
   [(0.0, 190.0), (810.0, 380.0), (1200.0, 600.0), (1810.0, 380.0),
    (2810.0, 190.0)],
   "C4: швы вокруг дверей, целая в центре простенка (%s)"
   % [(t["x"], t["w"]) for t in row])

# ── C5 (В2+П4): ряд НАД проёмами — анкерные швы дверей действуют и
#    там (сквозные вертикальные русты); над дверью камень ровно в
#    пролёте между швами; раскладка симметрична ──
p = plan(contours=[{"outer": rect(0, 0, 3000, 1210), "holes": holes4}],
         mode="openings")
top = sorted([t for t in p["inserts"] if near(t["y"], 610)],
             key=lambda t: t["x"])
ok([(t["x"], t["w"]) for t in top] ==
   [(0.0, 190.0), (200.0, 600.0), (810.0, 380.0), (1200.0, 600.0),
    (1810.0, 380.0), (2200.0, 600.0), (2810.0, 190.0)],
   "C5: сквозные швы от граней дверей (%s)"
   % [(t["x"], t["w"]) for t in top])
ok([(t["x"], t["w"]) for t in top] ==
   [(3000.0 - t["x"] - t["w"], t["w"]) for t in reversed(top)],
   "C5: раскладка симметрична относительно середины фасада")
# швы между соседними камнями ряда — НЕ меньше руста (стыков впритык
# больше нет: П4 закрыл щель «камень-в-камень» на анкерных линиях)
gaps5 = [round(top[i + 1]["x"] - (top[i]["x"] + top[i]["w"]), 6)
         for i in range(len(top) - 1)]
ok(all(g >= 10 - 1e-6 for g in gaps5),
   "C5b: все стыки ряда ≥ верт. руста (%s)" % gaps5)

# ── C6: остаток меньше min_cut — камень не ставится, note ──
p = plan(contours=[{"outer": rect(0, 0, 615, 600)}])   # остаток 5 < 20
ok(p["summary"]["tiles"] == 1 and
   any("< min_cut" in n for n in p["notes"]),
   "C6: 5 мм не ставим (%s)" % p["summary"])

# ── C7: ступень контура НЕ кратная ряду — подрезка по высоте сама ──
step7 = [[0, 0], [2440, 0], [2440, 900], [1220, 900], [1220, 1210],
         [0, 1210]]
p = plan(contours=[{"outer": step7}])
r1 = [t for t in p["inserts"] if near(t["y"], 0)]
lo = [t for t in p["inserts"] if near(t["y"], 610) and near(t["h"], 290)]
hi = [t for t in p["inserts"] if near(t["y"], 900) and near(t["h"], 310)]
ok(len(r1) == 4 and all(near(t["h"], 600) for t in r1),
   "C7: нижний ряд 4 целых")
ok(len(lo) == 4 and len(hi) == 2,
   "C7: ряд 2 — подполосы 290 (вся ширина, y=610) и 310 (левая часть, "
   "y=900): %d/%d" % (len(lo), len(hi)))

# ── C8: неортогональный контур — пропуск с note ──
p = plan(contours=[{"outer": [[0, 0], [1000, 0], [1000, 800], [100, 900]]},
                   {"outer": rect(0, 0, 610, 600)}])
ok(p["summary"]["tiles"] == 1 and
   any("неортогональ" in n for n in p["notes"]),
   "C8: кривой контур пропущен, второй обработан")

# ── C9: два контура — ОБЩИЙ ГОРИЗОНТ (сетка рядов одна) ──
p = plan(contours=[{"outer": rect(0, 0, 610, 1210)},
                   {"outer": rect(5000, 305, 5610, 1210)}])
y2 = sorted({t["y"] for t in p["inserts"] if t["x"] >= 5000})
ok(y2 == [305.0, 610.0],
   "C9: контур 2 стартует подрезкой 305 и попадает в горизонт (%s)" % y2)
ok(near([t for t in p["inserts"]
         if t["x"] >= 5000 and near(t["y"], 305)][0]["h"], 295),
   "C9: стартовый камень контура 2 подрезан до 295")

# ── C10: нулевой руст — плотная укладка ──
p = plan(contours=[{"outer": rect(0, 0, 1800, 600)}], gap={"v": 0, "h": 0})
xs = sorted(t["x"] for t in p["inserts"])
ok(xs == [0.0, 600.0, 1200.0] and p["summary"]["cut"] == 0,
   "C10: руст 0 — стык в стык (%s)" % xs)

# ── C11: «от проёмов» без проёмов вовсе == «от края» интервала;
#    с origin.x — тоже совпадают (оба от точки привязки) ──
pa = plan(contours=[{"outer": rect(0, 0, 3040, 600)}], mode="openings")
pb = plan(contours=[{"outer": rect(0, 0, 3040, 600)}], mode="edge")
ok([(t["x"], t["w"]) for t in pa["inserts"]] ==
   [(t["x"], t["w"]) for t in pb["inserts"]],
   "C11: без проёмов режимы совпадают")
pa = plan(contours=[{"outer": rect(0, 0, 3040, 600)}], mode="openings",
          origin={"x": 305.0, "y": 0.0})
pb = plan(contours=[{"outer": rect(0, 0, 3040, 600)}], mode="edge",
          origin={"x": 305.0, "y": 0.0})
ok([(t["x"], t["w"]) for t in pa["inserts"]] ==
   [(t["x"], t["w"]) for t in pb["inserts"]],
   "C11b: и с точкой привязки совпадают")

# ── C13 (В4): дефолт min_cut = 150 — остаток 90 пропускается ──
p = plan(contours=[{"outer": rect(0, 0, 700, 600)}])
ok(p["summary"]["tiles"] == 1 and
   any("90 < min_cut" in n for n in p["notes"]),
   "C13: остаток 90 не ставится на дефолте 150 (%s)" % p["notes"])
p = plan(contours=[{"outer": rect(0, 0, 700, 600)}], min_cut=50)
ok(p["summary"]["tiles"] == 2,
   "C13b: явный min_cut=50 перекрывает дефолт")

# ── C14 (В5-A): пролёт между швами 1520, остаток 300 — один
#    подрезной в центре простенка ──
holes14 = [rect(0, 0, 400, 600), rect(1940, 0, 2340, 600)]
p = plan(contours=[{"outer": rect(0, 0, 2340, 600), "holes": holes14}],
         mode="openings")
row = sorted(p["inserts"], key=lambda t: t["x"])
ok([(t["x"], t["w"]) for t in row] ==
   [(410.0, 600.0), (1020.0, 300.0), (1330.0, 600.0)],
   "C14: остаток 300 — одним камнем в середине простенка (%s)"
   % [(t["x"], t["w"]) for t in row])

# ── C15 (В5-B, n=1): пролёт между швами 860 — целых 0 в центре, два
#    равных подрезных (250+600)/2=425 ──
holes15 = [rect(0, 0, 400, 600), rect(1280, 0, 1680, 600)]
p = plan(contours=[{"outer": rect(0, 0, 1680, 600), "holes": holes15}],
         mode="openings")
row = sorted(p["inserts"], key=lambda t: t["x"])
ok([(t["x"], t["w"]) for t in row] == [(410.0, 425.0), (845.0, 425.0)],
   "C15: простенок со швами — два равных подрезных по 425 (%s)"
   % [(t["x"], t["w"]) for t in row])

# ── C17 (П4 по вертикали): окно-«лента» в правой половине, не
#    выровнено по рядам — камни над/под окном отступают на гориз.
#    руст, соседний столбец (мимо окна) не трогается ──
p = plan(contours=[{"outer": rect(0, 0, 1220, 1210),
                    "holes": [rect(610, 500, 1220, 660)]}])
t17 = sorted([(t["x"], t["y"], t["w"], t["h"]) for t in p["inserts"]])
ok(t17 == [(0.0, 0.0, 600.0, 500.0), (0.0, 660.0, 600.0, 550.0),
           (610.0, 0.0, 600.0, 490.0), (610.0, 670.0, 600.0, 540.0)],
   "C17: отступ gh под и над окном только у столбца окна (%s)" % t17)

# ── C18 (П3): origin.x — вертикальный руст через точку: сетка
#    столбцов от x=305 (модульно, клип краями контура) ──
p = plan(contours=[{"outer": rect(0, 0, 3040, 1210)}],
         origin={"x": 305.0, "y": 0.0})
xs18 = sorted({(t["x"], t["w"]) for t in p["inserts"]})
ok(xs18 == [(0.0, 295.0), (305.0, 600.0), (915.0, 600.0),
            (1525.0, 600.0), (2135.0, 600.0), (2745.0, 295.0)],
   "C18: столбцы от точки верт. руста (%s)" % xs18)
ok(p["summary"]["tiles"] == 12 and p["summary"]["cut"] == 4,
   "C18: 2 ряда × (4 целых + 2 подрезки)")

# ── C19 (П3): origin.y — синоним датума (низ первого ряда) ──
pa = plan(contours=[{"outer": rect(0, 0, 610, 1210)}],
          origin={"y": 300.0})
pb = plan(contours=[{"outer": rect(0, 0, 610, 1210)}], datum=300.0)
ok([(t["y"], t["h"]) for t in pa["inserts"]] ==
   [(t["y"], t["h"]) for t in pb["inserts"]],
   "C19: origin.y эквивалентен datum")

# ── C16: агрегация одинаковых notes со счётчиком ──
p = plan(contours=[{"outer": rect(0, 0, 700, 1820)}])   # 3 ряда по 90
ok(any("90 < min_cut (×3)" in n for n in p["notes"]),
   "C16: одинаковые пропуски схлопнуты (×3) (%s)" % p["notes"])

# ── C12: окно уже camня в простенке — вставка min(rem, w) не шире w ──
p = plan(contours=[{"outer": rect(0, 0, 1400, 600),
                    "holes": [rect(100, 0, 200, 600),
                              [[1300, 0], [1350, 0], [1350, 600],
                               [1300, 600]]]}],
         mode="openings")
ok(all(t["w"] <= 600 + 1e-6 for t in p["inserts"]),
   "C12: ни один камень не шире 600")

print("cladding_plan: %d проверок OK" % _n)
