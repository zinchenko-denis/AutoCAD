# -*- coding: utf-8 -*-
"""Юниты vitrage_recognize. Синтетика — литералы полос из «Проба 3.dxf»
(АР-витраж: 4 вертикали сегментами, 5 отметок, дверь в центральном пролёте),
плюс опциональная сверка с ЗЕЛЁНЫМ эталоном живого файла (每 вставка нашего
плана должна существовать в конструкции, построенной Алексеем вручную).

Числа файла (разбор 13.07): X-полосы 27058..27133 (75, крайняя) / 27528..27578 /
29238..29288 / 29683..29758 (75, крайняя); Y-сегменты 250+50+2400+900+300+285 =
4185 от 31239.8; отметки 31489.8 (порог двери) / 31539.8 / 33939.8 / 34839.8 /
35139.8; дверь 27653..29163 × 31514.8..33839.8; оси стоек 27108 / 27553 /
29263 / 29708 (крайние: тело 50 изнутри 75-полосы); шаги 445/1710/445.
"""
import os
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

from vitrage_recognize import (recognize, classify_strips, chain_verticals,
                               stand_axis, group_rails, covered,
                               strips_from_segments)

PASS = 0


def ok(cond, msg):
    global PASS
    assert cond, msg
    PASS += 1


def near(a, b, tol=0.05):
    return abs(a - b) <= tol


def bb(x0, y0, x1, y1):
    return {"x0": x0, "y0": y0, "x1": x1, "y1": y1}


# ── синтетика «Проба 3»: полосы АР ──
YSEG = [(31239.8, 31489.8), (31489.8, 31539.8), (31539.8, 33939.8),
        (33939.8, 34839.8), (34839.8, 35139.8), (35139.8, 35424.8)]
XCOL = [(27058.0, 27133.0), (27528.0, 27578.0),
        (29238.0, 29288.0), (29683.0, 29758.0)]
STRIPS = []
for (x0, x1) in XCOL:
    for (y0, y1) in YSEG:
        STRIPS.append(bb(x0, y0, x1, y1))
RAILS_SRC = [
    (31489.8, [(27578.0, 29238.0)]),                       # порог (центр)
    (31539.8, [(27133.0, 27528.0), (29288.0, 29683.0)]),   # низ крайних
    (33939.8, [(27133.0, 27528.0), (27578.0, 29238.0), (29288.0, 29683.0)]),
    (34839.8, [(27133.0, 27528.0), (27578.0, 29238.0), (29288.0, 29683.0)]),
    (35139.8, [(27133.0, 27528.0), (27578.0, 29238.0), (29288.0, 29683.0)]),
]
for y_ax, spans in RAILS_SRC:
    for (x0, x1) in spans:
        STRIPS.append(bb(x0, y_ax - 25.0, x1, y_ax + 25.0))
# мусор, который обязан отфильтроваться: значки 100×100, стрелка 850×250
STRIPS += [bb(27280.5, 32689.8, 27380.5, 32789.8),
           bb(29435.5, 34339.8, 29535.5, 34439.8),
           bb(30506.8, 31514.8, 31356.8, 31764.8)]
PANELS = [
    {"name": "ETL_Панель_витража_Дверь_Двупольная_Алюминиевая_ГОСТ23747_2015 - "
             "1500х2100мм_со створкой 1050мм__Пр-9248103-В_ут_-10",
     "x0": 27653.0, "y0": 31514.8, "x1": 29163.0, "y1": 33839.8},
    {"name": "ETL_Панель_витража_Створка - Глухая-8978012-В_ут_-10",
     "x0": 27280.5, "y0": 34339.8, "x1": 27380.5, "y1": 34439.8},
]
REQ = {
    "strips": STRIPS, "panels": PANELS,
    "blocks": {"stand": {"name": "F50.01.07-осн", "body_w": 50, "prof": "F50.01.07"},
               "rigel": {"name": "F50.02.03-осн", "prof": "F50.02.03"}},
}

# ── R1: классификация ──
v, h, c = classify_strips(STRIPS, 40, 120)
ok(len(v) == 4 * 5, f"R1: вертикалей {len(v)} != 20")   # 6 сегментов/колонку, 1 из них кубик
ok(len(c) == 6, f"R1: кубиков {len(c)} != 6")           # 4 стыковых 50×50 + 2 значка 100×100
ok(len(h) == 12, f"R1: горизонталей {len(h)} != 12")    # 1+2+3+3+3; стрелка 850×250 отпала

# ── R2: цепочки вертикалей ──
ch = chain_verticals(v, c)
ok(len(ch) == 4, f"R2: цепочек {len(ch)} != 4")
for cc in ch:
    ok(near(cc["y0"], 31239.8) and near(cc["y1"], 35424.8) and not cc["gaps"],
       f"R2: цепочка {cc}")

# ── R3: оси (правило крайних: тело изнутри) ──
ax = [stand_axis(cc, 50, i == 0, i == 3) for i, cc in enumerate(ch)]
for got, exp in zip(ax, (27108.0, 27553.0, 29263.0, 29708.0)):
    ok(near(got, exp), f"R3: ось {got:.2f} != {exp}")

# ── R4: отметки ──
rails = group_rails(h)
ok([round(r["y"], 1) for r in rails] == [31489.8, 31539.8, 33939.8, 34839.8, 35139.8],
   f"R4: отметки {[r['y'] for r in rails]}")
ok(covered(rails[0]["spans"], 27578 + 0, 29238 - 0, 0.5) and
   not covered(rails[0]["spans"], 27133, 27528, 0.5), "R4: покрытие порога")

# ── R5: полный прогон ──
plan = recognize(REQ)
ok(plan["ok"], "R5: план не собрался")
st = [i for i in plan["inserts"] if i["kind"] == "stand"]
rg = [i for i in plan["inserts"] if i["kind"] == "rigel"]
ok(len(st) == 4, f"R5: стоек {len(st)} != 4")
ok(all(near(s["y"], 31239.8) and s["dyn"]["Длина"] == 4185.0 and
       s["attrs"]["ДЛИНА"] == "4185.00" and s["attrs"]["ПРОФ"] == "F50.01.07" and
       s["rot"] == 0 and s["layer"] == "RF-стойки" for s in st), "R5: контракт стоек")
ok({s["attrs"]["ИМЯ"] for s in st} == {"С1"}, "R5: одна марка (типоразмер один)")
ok(len(rg) == 11, f"R5: ригелей {len(rg)} != 11 (2 нижних крайних + 3×3)")
# порог под дверью пропущен
ok(not any(near(r["y"], 31489.8) for r in rg), "R5: порог двери должен быть пропущен")
# нижние крайних пролётов
low = sorted((r for r in rg if near(r["y"], 31539.8)), key=lambda r: r["x"])
ok(len(low) == 2 and near(low[0]["x"], 27108) and near(low[1]["x"], 29263) and
   all(r["dyn"]["Длина"] == 445.0 and r["attrs"]["ДЛИНА"] == "445.00" for r in low),
   f"R5: нижние ригели {low}")
# средний пролёт: длина = осевой шаг 1710
mid = [r for r in rg if near(r["x"], 27553)]
ok(len(mid) == 3 and all(r["dyn"]["Длина"] == 1710.0 for r in mid), "R5: центр 1710")
ok(all(r["rot"] == 0 and r["layer"] == "RF-ригеля" and
       r["attrs"]["ПРОФ"] == "F50.02.03" for r in rg),
   "R5: контракт ригелей (rot=0 — дефолт с 14.07, поворот приходит от образца)")
# марки по типоразмерам: 445 и 1710 → две марки
ok(len({r["attrs"]["ИМЯ"] for r in rg}) == 2, "R5: марки ригелей")
ok(plan["summary"]["stands"] == 4 and plan["summary"]["rigels"] == 11, "R5: summary")
ok(any("двер" in n for n in plan["notes"]), "R5: note про дверь")

# ── R6: ошибки входа ──
def must_fail(req, tag):
    try:
        recognize(req)
    except ValueError:
        ok(True, tag); return
    ok(False, tag + ": ValueError не поднят")

must_fail({"strips": [], "blocks": REQ["blocks"]}, "R6: пустые strips")
must_fail({"strips": STRIPS, "blocks": {"stand": {"name": "s"}}}, "R6: без ригеля")
must_fail({"strips": [bb(0, 0, 50, 3000)], "blocks": REQ["blocks"]},
          "R6: одна вертикаль")

# ── R7: крайняя полоса ровно 50 (без зазора) → ось по центру ──
ax7 = stand_axis({"x0": 100.0, "x1": 150.0, "y0": 0, "y1": 100}, 50, True, False)
ok(near(ax7, 125.0), f"R7: {ax7}")

# ── R8: автовывод тела профиля из ширин полос (body_w не задан) ──
req8 = {"strips": STRIPS, "panels": PANELS,
        "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"}}}
p8 = recognize(req8)
st8 = sorted((i for i in p8["inserts"] if i["kind"] == "stand"), key=lambda z: z["x"])
ok(near(st8[0]["x"], 27108.0) and near(st8[-1]["x"], 29708.0),
   f"R8: оси при автотеле {st8[0]['x']}, {st8[-1]['x']}")
ok(any("тело профиля" in n and "50.0" in n for n in p8["notes"]), f"R8: note {p8['notes']}")

# ── R9: Э3-A — опциональный ВЕРХНИЙ ригель в свету (эталон: Р31/Р33) ──
req9 = {"strips": STRIPS, "panels": PANELS,
        "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"},
                   "rigel_top": {"name": "T", "prof": "F50.01.07"}}}
p9 = recognize(req9)
tops = [i for i in p9["inserts"] if i["kind"] == "rigel_top"]
regs = [i for i in p9["inserts"] if i["kind"] == "rigel"]
ok(len(tops) == 3 and len(regs) == 8, f"R9: top {len(tops)} / обычных {len(regs)}")
ok(all(near(t["y"], 35139.8) and t["block"] == "T" and
       t["attrs"]["ПРОФ"] == "F50.01.07" for t in tops), "R9: верхняя отметка блоком T")
t1 = sorted(tops, key=lambda z: z["x"])
ok(near(t1[0]["x"], 27133.0) and t1[0]["attrs"]["ДЛИНА"] == "395.00" and
   near(t1[1]["x"], 27578.0) and t1[1]["attrs"]["ДЛИНА"] == "1660.00",
   f"R9: в свету {t1[0]['x']}/{t1[0]['attrs']['ДЛИНА']}, {t1[1]['x']}/{t1[1]['attrs']['ДЛИНА']}")
ok(not any(near(r["y"], 35139.8) for r in regs), "R9: обычных на верхней отметке нет")
ok(any("верхняя отметка" in n for n in p9["notes"]), "R9: note")

# ── R10: rigel_top без горизонталей — не падает ──
p10r = recognize({"strips": [bb(0, 0, 50, 3000), bb(700, 0, 750, 3000)],
                  "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"},
                             "rigel_top": {"name": "T"}}})
ok(p10r["ok"] and p10r["summary"]["rigels"] == 0, "R10: только стойки")

# ── R11: Э3-E — «АР голыми линиями»: полосы разобраны на 4 стороны ──
SEGS = []
for s in STRIPS[:len(STRIPS) - 3]:      # без значков/стрелки — они не полосы
    x0, y0, x1, y1 = s["x0"], s["y0"], s["x1"], s["y1"]
    SEGS += [{"x0": x0, "y0": y0, "x1": x0, "y1": y1},   # левая грань
             {"x0": x1, "y0": y0, "x1": x1, "y1": y1},   # правая
             {"x0": x0, "y0": y0, "x1": x1, "y1": y0},   # нижний торец
             {"x0": x0, "y0": y1, "x1": x1, "y1": y1}]   # верхний
built = strips_from_segments(SEGS, 40, 120)
# 15.07 (union-сборка): сегменты колонны сливаются в ОДНУ полосу на цепочку —
# полос меньше, чем в STRIPS-литерале, но покрытие то же: 4 колонны + отметки
ok(len(built) >= 16, f"R11: собрано полос {len(built)}")
p11 = recognize({"strips": [], "segments": SEGS, "panels": PANELS,
                 "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"}}})
st11 = sorted((i for i in p11["inserts"] if i["kind"] == "stand"), key=lambda z: z["x"])
rg11 = [i for i in p11["inserts"] if i["kind"] == "rigel"]
ok(len(st11) == 4 and len(rg11) == 11,
   f"R11: из линий стоек {len(st11)}, ригелей {len(rg11)}")
for got, exp in zip((s["x"] for s in st11), (27108.0, 27553.0, 29263.0, 29708.0)):
    ok(near(got, exp, 0.5), f"R11: ось {got} != {exp}")
ok(all(s["attrs"]["ДЛИНА"] == "4185.00" for s in st11), "R11: длины стоек")
ok(not any(near(r["y"], 31489.8) for r in rg11), "R11: порог пропущен и на линиях")
ok(any("полос собрано из отрезков" in n for n in p11["notes"]), "R11: note")

# ── R12: rot с образца (фикс 14.07 — ригели Алексея легли с 270 вместо 0) ──
req12 = {"strips": STRIPS, "panels": PANELS,
         "blocks": {"stand": {"name": "S", "rot": 0},
                    "rigel": {"name": "R", "rot": 0},
                    "rigel_top": {"name": "T", "rot": 270}}}
p12 = recognize(req12)
ok(all(i["rot"] == 0 for i in p12["inserts"] if i["kind"] in ("stand", "rigel")),
   "R12: стойки/ригели rot=0")
ok(all(i["rot"] == 270 for i in p12["inserts"] if i["kind"] == "rigel_top"),
   "R12: верхние rot=270 (вертикальное определение)")

# ── R13: dims — вертикальная цепочка ФАКТИЧЕСКИХ ригелей (без порога) ──
dm13 = p12["dims"]
ok(len(dm13) == 4, f"R13: dims {len(dm13)} != 4")
v13 = [d for d in dm13 if d["dir"] == "v" and len(d["pts"]) > 2][0]
segs = [round(b - a, 1) for a, b in zip(v13["pts"], v13["pts"][1:])]
ok(segs == [300.0, 2400.0, 900.0, 300.0, 285.0],
   f"R13: цепочка от габарита по осям {segs} (порога 250 нет)")
h13 = [d for d in dm13 if d["dir"] == "h" and len(d["pts"]) > 2][0]
ok([round(b - a, 1) for a, b in zip(h13["pts"], h13["pts"][1:])] == [445.0, 1710.0, 445.0],
   "R13: межосевые стоек 445/1710/445")
o13 = [d for d in dm13 if d["dir"] == "h" and len(d["pts"]) == 2][0]
ok(near(o13["pts"][1] - o13["pts"][0], 2650.0), f"R13: габарит ширины {o13}")

# ── R14: «Образец 2» (15.07, АР-импровизация №2, Revit-вставки): рамки
#    обрамления, импосты 150/200 (wmax 210), body_w с образца, оси крайних
#    по внутренней грани, клэмп обвязки к рамке, дверь на подставке ──
V14 = [(0, 0, 200, 2450), (0, 2450, 200, 3470),          # левый 200
       (865, 0, 935, 2450), (865, 2450, 935, 3470),      # внутр. 70
       (2515, 0, 2585, 2450), (2515, 2450, 2585, 3470),  # внутр. 70
       (2950, 0, 3100, 2450), (2950, 2450, 3100, 3470)]  # правый 150
H14 = [(200, 0, 865, 200), (2585, 0, 2950, 200),         # низ 200
       (935, 0, 2515, 150),                              # низ под дверью 150
       (200, 2415, 865, 2485), (935, 2415, 2515, 2485),
       (2585, 2415, 2950, 2485),                         # середина 70
       (200, 3370, 865, 3470), (935, 3370, 2515, 3470),
       (2585, 3370, 2950, 3470)]                         # верх 100
S14 = [bb(*b) for b in V14 + H14]
P14 = [{"name": "ADSK_Двери_Витражная_Двупольная_Ал",    # «Двери» ≠ «дверь»!
        "x0": 935, "y0": 150, "x1": 2515, "y1": 2415}]
FR14 = []                                                 # рамки обрамления
for x0, y0, x1, y1 in ((0, 0, 3100, 3470), (160, 136, 3010, 3470)):
    FR14 += [{"x0": x0, "y0": y0, "x1": x0, "y1": y1},
             {"x0": x1, "y0": y0, "x1": x1, "y1": y1},
             {"x0": x0, "y0": y0, "x1": x1, "y1": y0},
             {"x0": x0, "y0": y1, "x1": x1, "y1": y1}]
FR14.append({"x0": 3010, "y0": 136, "x1": 3300, "y1": 136})   # высотная отметка
p14 = recognize({"strips": S14, "segments": FR14, "panels": P14,
                 "blocks": {"stand": {"name": "S", "body_w": 50.0},
                            "rigel": {"name": "R"}}})
st14 = sorted((i for i in p14["inserts"] if i["kind"] == "stand"),
              key=lambda z: z["x"])
ok(len(st14) == 4, f"R14: стоек {len(st14)} != 4")
for got, exp in zip((s["x"] for s in st14), (175.0, 900.0, 2550.0, 2975.0)):
    ok(near(got, exp), f"R14: ось {got} != {exp} (грань∓25 у крайних, центры внутр.)")
ok(all(s["y"] == 136.0 and s["attrs"]["ДЛИНА"] == "3334.00" for s in st14),
   "R14: стойки по внутренней рамке (y=136, 3334), не по графике (3470)")
rg14 = [i for i in p14["inserts"] if i["kind"] == "rigel"]
ys14 = sorted({round(r["y"], 1) for r in rg14})
ok(ys14 == [161.0, 2450.0, 3420.0],
   f"R14: отметки {ys14} (низ 161 = рамка+25 — клэмп; 2450/3420 — оси полос)")
low14 = sorted(r["x"] for r in rg14 if near(r["y"], 161.0))
ok(low14 == [175.0, 2550.0],
   f"R14: низ {low14} — только пролёты 1/3 (дверь на подставке глушит порог)")
ok(len(rg14) == 8, f"R14: ригелей {len(rg14)} != 8")
ok(any("обрамление АР" in n for n in p14["notes"]) and
   any("обвязка вне обрамления" in n for n in p14["notes"]), "R14: notes рамки")

# ── R15: «Образец 1» (15.07, АР-импровизация №1, голые линии): union
#    дроблёных контуров, фантомы дверной коробки, хвосты выносок, габарит
#    стоек сквозь обвязку, равноширинные крайние → оси по центрам ──
S15 = []
def _seg15(x0, y0, x1, y1): S15.append({"x0": x0, "y0": y0, "x1": x1, "y1": y1})
_seg15(0, -75, 0, 2925)                     # левая грань-линия с «ушками»
_seg15(100, 80, 100, 2100); _seg15(100, 2180, 100, 2750)   # правая грань дробно
_seg15(1670, 80, 1670, 2100); _seg15(1670, 2180, 1670, 2750)
_seg15(1770, 80, 1770, 2100); _seg15(1770, 2180, 1770, 2750)
_seg15(3200, 80, 3200, 2100); _seg15(3200, 2180, 3200, 2750)
_seg15(3300, -75, 3300, 2925)               # правая крайняя полная
_seg15(0, 0, 3300, 0); _seg15(0, 2850, 3300, 2850)         # низ/верх габарита
_seg15(100, 80, 1670, 80)                                  # верх нижнего ригеля
_seg15(100, 2100, 1670, 2100); _seg15(1770, 2100, 3200, 2100)
_seg15(100, 2180, 1670, 2180); _seg15(1770, 2180, 3200, 2180)
_seg15(100, 2750, 1670, 2750); _seg15(1770, 2750, 3200, 2750)
_seg15(2300, 150, 2300, 2000); _seg15(2350, 150, 2350, 2000)  # дверная коробка
_seg15(0, -600, 0, -110); _seg15(100, -600, 100, -110)        # выноски вниз
                                    # (зазор до «ушек» −75, как в Образце 1)
p15 = recognize({"strips": [], "segments": S15,
                 "blocks": {"stand": {"name": "S", "body_w": 50.0},
                            "rigel": {"name": "R"}}})
st15 = sorted((i for i in p15["inserts"] if i["kind"] == "stand"),
              key=lambda z: z["x"])
ok(len(st15) == 3, f"R15: стоек {len(st15)} != 3 (коробка 50 мм — не стойка)")
for got, exp in zip((s["x"] for s in st15), (50.0, 1720.0, 3250.0)):
    ok(near(got, exp), f"R15: ось {got} != {exp} (равноширинные крайние → центр)")
ok(all(s["y"] == 0.0 and s["attrs"]["ДЛИНА"] == "2850.00" for s in st15),
   f"R15: стойки 0..2850 сквозь обвязку (факт: {st15[0]['y']}, "
   f"{st15[0]['attrs']['ДЛИНА']})")
rg15 = [i for i in p15["inserts"] if i["kind"] == "rigel"]
ys15 = sorted({round(r["y"], 1) for r in rg15})
ok(ys15 == [40.0, 2140.0, 2800.0], f"R15: отметки {ys15} — оси пар линий")
ok(len([r for r in rg15 if near(r["y"], 40.0)]) == 1,
   "R15: нижний ригель только в пролёте 1 (в пролёте 2 линии порога нет)")
ok(any("отброшена" in n for n in p15["notes"]), "R15: note про фантом коробки")
ok(any("хвосты выносок" in n for n in p15["notes"]), "R15: note про выноски")

print(f"vitrage_recognize: {PASS} проверок OK")

# ── D2: живой файл «Проба 3» — план обязан совпасть с ручной конструкцией ──
CANDIDATES = ["/home/claude/Проба_3.dxf",
              "/home/claude/atspec-testdata/dxf/ar/Проба_3.dxf"]
DXF = next((p for p in CANDIDATES if os.path.exists(p)), None)
if DXF:
    try:
        import ezdxf
    except ImportError:
        ezdxf = None
    if ezdxf is not None:
        doc = ezdxf.readfile(DXF)
        msp = doc.modelspace()

        def bbox_ins(e, depth=0):
            blk = doc.blocks.get(e.dxf.name)
            if blk is None: return None
            m = e.matrix44()
            xs, ys = [], []
            for x in blk:
                t = x.dxftype()
                if t == "LWPOLYLINE":
                    for p in x.get_points("xy"):
                        w = m.transform((p[0], p[1], 0)); xs.append(w[0]); ys.append(w[1])
                elif t == "LINE":
                    for p in (x.dxf.start, x.dxf.end):
                        w = m.transform(p); xs.append(w[0]); ys.append(w[1])
                elif t == "INSERT" and depth < 3:
                    sub = bbox_ins(x, depth + 1)
                    if sub:
                        for q in ((sub[0], sub[1]), (sub[2], sub[3])):
                            w = m.transform((q[0], q[1], 0)); xs.append(w[0]); ys.append(w[1])
            if not xs: return None
            return (min(xs), min(ys), max(xs), max(ys))

        RED = (25599, 29504, 32040, 36572)
        GRN = (34590, 29504, 41032, 36572)

        def in_zone(bbx, z):
            cx, cy = (bbx[0] + bbx[2]) / 2, (bbx[1] + bbx[3]) / 2
            return z[0] - 1 <= cx <= z[2] + 1 and z[1] - 1 <= cy <= z[3] + 1

        strips, panels, fact_st, fact_rg = [], [], [], []
        for e in msp.query("INSERT"):
            bbx = bbox_ins(e)
            if bbx is None: continue
            if in_zone(bbx, RED):
                strips.append({"x0": bbx[0], "y0": bbx[1], "x1": bbx[2], "y1": bbx[3]})
                panels.append({"name": e.dxf.name, "x0": bbx[0], "y0": bbx[1],
                               "x1": bbx[2], "y1": bbx[3]})
            elif in_zone(bbx, GRN):
                at = {a.dxf.tag: a.dxf.text for a in e.attribs}
                rec = (bbx, at, e.dxf.layer, round(e.dxf.rotation, 1) % 360)
                if e.dxf.layer == "RF-стойки": fact_st.append(rec)
                elif e.dxf.layer == "RF-ригеля": fact_rg.append(rec)

        # с rigel_top (Э3-A) план обязан воспроизвести эталон ЦЕЛИКОМ:
        # верхний ряд — Р31/Р33 в свету rot=270, нижние — осевые rot=0
        # (повороты — факт эталона Алексея, 14.07). Точный матч ВСЕХ.
        plan = recognize({"strips": strips, "panels": panels,
                          "blocks": {"stand": {"name": "S", "rot": 0},
                                     "rigel": {"name": "R", "rot": 0},
                                     "rigel_top": {"name": "T", "rot": 270}}})
        st = sorted((i for i in plan["inserts"] if i["kind"] == "stand"),
                    key=lambda z: z["x"])
        rg = [i for i in plan["inserts"] if i["kind"] in ("rigel", "rigel_top")]
        fact_st.sort(key=lambda z: z[0][0])
        assert len(st) == len(fact_st) == 4, \
            f"D2: стоек план {len(st)} / факт {len(fact_st)}"
        # смещение зон — по первой стойке (центр bbox эталона = ось)
        D = (fact_st[0][0][0] + fact_st[0][0][2]) / 2 - st[0]["x"]
        d = 0
        for s, (fb, fa, _, frot) in zip(st, fact_st):
            assert near(s["x"] + D, (fb[0] + fb[2]) / 2, 0.5), \
                f"D2: ось {s['x'] + D:.2f} vs {(fb[0]+fb[2])/2:.2f}"
            assert near(s["y"], fb[1], 0.5), f"D2: низ стойки {s['y']} vs {fb[1]}"
            assert fa["ДЛИНА"] == s["attrs"]["ДЛИНА"], \
                f"D2: ДЛИНА {fa['ДЛИНА']} != {s['attrs']['ДЛИНА']}"
            assert s["rot"] == frot, f"D2: rot стойки {s['rot']} != {frot}"
            d += 4
        matched = 0
        for r in rg:
            # и осевые (x = ось левой стойки), и верхние в свету (x = ось+25):
            # у эталона bbox x0 ригеля == нашей вставке в обоих случаях
            hit = [f for f in fact_rg
                   if abs(f[0][0] - (r["x"] + D)) <= 0.5 and
                   abs((f[0][1] + f[0][3]) / 2 - r["y"]) <= 0.5]
            assert len(hit) == 1, f"D2: ригель {r['x']:.1f},{r['y']:.1f} не найден в эталоне"
            assert hit[0][1]["ДЛИНА"] == r["attrs"]["ДЛИНА"], \
                f"D2: ДЛИНА {hit[0][1]['ДЛИНА']} != {r['attrs']['ДЛИНА']} ({r['kind']})"
            assert r["rot"] == hit[0][3], \
                f"D2: rot {r['rot']} != {hit[0][3]} ({r['kind']} {r['attrs']['ДЛИНА']})"
            matched += 3
        assert len(rg) == len(fact_rg) == 11, \
            f"D2: ригелей план {len(rg)} / факт {len(fact_rg)}"
        d += matched
        print(f"vitrage_recognize D2-эталон: {d} сверок OK — план с rigel_top "
              f"воспроизводит ручной каркас Алексея ПОЛНОСТЬЮ (позиции, ДЛИНА "
              f"байт-в-байт И ПОВОРОТЫ: ригели 0, верхние 270, как в его эталоне)")
    else:
        print("vitrage_recognize D2: ezdxf нет — пропуск")
else:
    print("vitrage_recognize D2: Проба_3.dxf недоступен — пропуск")

# ── D3: живые «Образец 1/2» (15.07, АР-импровизации других архитекторов) —
#    план сверяется с ручным RF-эталоном Алексея из ТОГО ЖЕ файла.
#    Образец 1 — полное совпадение; Образец 2 — всё, кроме ВЕРХНЕЙ обвязки:
#    её Алексей прижал к верху рамки (−25 от нашей оси полосы) — конвенция
#    открыта (вопрос ему), фиксируем расхождение ТОЧНО −25. ──
D3_DIRS = ["/home/claude/atspec-testdata/dxf/ar", "/home/claude/ar_samples"]


def _d3_run(path, allow_top_dy):
    import ezdxf
    from ezdxf import bbox as ezbbox
    skip = {"TEXT", "MTEXT", "ATTDEF", "DIMENSION", "HATCH"}
    doc = ezdxf.readfile(path)
    strips, panels, segments, f_st, f_rg = [], [], [], [], []
    for e in doc.modelspace():
        t = e.dxftype()
        if t == "INSERT":
            if e.dxf.layer.startswith("RF-"):
                at = {a.dxf.tag: a.dxf.text for a in e.attribs}
                rec = (e.dxf.insert.x, e.dxf.insert.y, at.get("ДЛИНА"))
                (f_st if e.dxf.layer == "RF-стойки" else f_rg).append(rec)
                continue
            pts = [ezbbox.extents([v], fast=True) for v in e.virtual_entities()
                   if v.dxftype() not in skip]
            pts = [b for b in pts if b.has_data]
            if not pts:
                continue
            bx = {"x0": min(b.extmin.x for b in pts),
                  "y0": min(b.extmin.y for b in pts),
                  "x1": max(b.extmax.x for b in pts),
                  "y1": max(b.extmax.y for b in pts)}
            strips.append(dict(bx))
            p = dict(bx); p["name"] = e.dxf.name; panels.append(p)
        elif t == "LINE":
            segments.append({"x0": e.dxf.start.x, "y0": e.dxf.start.y,
                             "x1": e.dxf.end.x, "y1": e.dxf.end.y})
    plan = recognize({"strips": strips, "segments": segments, "panels": panels,
                      "blocks": {"stand": {"name": "S", "rot": 0,
                                           "body_w": 50.0},
                                 "rigel": {"name": "R", "rot": 0}}})
    st = sorted(((i["x"], i["y"], i["attrs"]["ДЛИНА"])
                 for i in plan["inserts"] if i["kind"] == "stand"))
    rg = sorted(((i["x"], i["y"], i["attrs"]["ДЛИНА"])
                 for i in plan["inserts"] if i["kind"].startswith("rigel")),
                key=lambda z: (round(z[1], 1), z[0]))
    f_st.sort(); f_rg.sort(key=lambda z: (round(z[1], 1), z[0]))
    assert len(st) == len(f_st), f"D3 {path}: стоек {len(st)}/{len(f_st)}"
    assert len(rg) == len(f_rg), f"D3 {path}: ригелей {len(rg)}/{len(f_rg)}"
    D = f_st[0][0] - st[0][0]
    n = 0
    for a, b in zip(st, f_st):
        assert abs(a[0] + D - b[0]) <= 0.5 and abs(a[1] - b[1]) <= 0.5 \
            and a[2] == b[2], f"D3 {path}: стойка {a} vs {b}"
        n += 3
    top_y = max(round(a[1], 1) for a in rg)
    for a, b in zip(rg, f_rg):
        dy = b[1] - a[1]
        y_ok = (abs(dy) <= 0.5 or
                (allow_top_dy and round(a[1], 1) == top_y and near(dy, 25.0)))
        assert abs(a[0] + D - b[0]) <= 0.5 and y_ok and a[2] == b[2], \
            f"D3 {path}: ригель {a} vs {b} (dy={dy:+.1f})"
        n += 3
    return n


_d3_total = 0
_d3_files = 0
try:
    import ezdxf  # noqa: F401
    _have_ezdxf = True
except ImportError:
    _have_ezdxf = False
if _have_ezdxf:
    for _i, _top in ((1, False), (2, True)):
        _p = next((os.path.join(d, "Образец_%d.dxf" % _i) for d in D3_DIRS
                   if os.path.exists(os.path.join(d, "Образец_%d.dxf" % _i))),
                  None)
        if _p:
            _d3_total += _d3_run(_p, _top)
            _d3_files += 1
    if _d3_files:
        print("vitrage_recognize D3-эталоны: %d сверок OK по %d образцам "
              "(Образец 1 — полный матч; Образец 2 — верхняя обвязка "
              "эталона на −25, конвенция открыта)" % (_d3_total, _d3_files))
    else:
        print("vitrage_recognize D3: Образцы недоступны — пропуск")
else:
    print("vitrage_recognize D3: ezdxf нет — пропуск")
