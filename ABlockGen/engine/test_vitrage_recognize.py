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
ok(all(r["rot"] == 270 and r["layer"] == "RF-ригеля" and
       r["attrs"]["ПРОФ"] == "F50.02.03" for r in rg), "R5: контракт ригелей")
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
ok(len(built) >= len(STRIPS) - 3, f"R11: собрано полос {len(built)}")
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
                rec = (bbx, at, e.dxf.layer)
                if e.dxf.layer == "RF-стойки": fact_st.append(rec)
                elif e.dxf.layer == "RF-ригеля": fact_rg.append(rec)

        # с rigel_top (Э3-A) план обязан воспроизвести эталон ЦЕЛИКОМ:
        # верхний ряд — Р31/Р33 в свету, нижние — осевые. Точный матч ВСЕХ.
        plan = recognize({"strips": strips, "panels": panels,
                          "blocks": {"stand": {"name": "S"},     # body_w — автовывод
                                     "rigel": {"name": "R"},
                                     "rigel_top": {"name": "T"}}})
        st = sorted((i for i in plan["inserts"] if i["kind"] == "stand"),
                    key=lambda z: z["x"])
        rg = [i for i in plan["inserts"] if i["kind"] in ("rigel", "rigel_top")]
        fact_st.sort(key=lambda z: z[0][0])
        assert len(st) == len(fact_st) == 4, \
            f"D2: стоек план {len(st)} / факт {len(fact_st)}"
        # смещение зон — по первой стойке (центр bbox эталона = ось)
        D = (fact_st[0][0][0] + fact_st[0][0][2]) / 2 - st[0]["x"]
        d = 0
        for s, (fb, fa, _) in zip(st, fact_st):
            assert near(s["x"] + D, (fb[0] + fb[2]) / 2, 0.5), \
                f"D2: ось {s['x'] + D:.2f} vs {(fb[0]+fb[2])/2:.2f}"
            assert near(s["y"], fb[1], 0.5), f"D2: низ стойки {s['y']} vs {fb[1]}"
            assert fa["ДЛИНА"] == s["attrs"]["ДЛИНА"], \
                f"D2: ДЛИНА {fa['ДЛИНА']} != {s['attrs']['ДЛИНА']}"
            d += 3
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
            matched += 2
        assert len(rg) == len(fact_rg) == 11, \
            f"D2: ригелей план {len(rg)} / факт {len(fact_rg)}"
        d += matched
        print(f"vitrage_recognize D2-эталон: {d} сверок OK — план с rigel_top "
              f"воспроизводит ручной каркас Алексея ПОЛНОСТЬЮ (11 ригелей "
              f"включая верхние в свету, байт-в-байт по ДЛИНА)")
    else:
        print("vitrage_recognize D2: ezdxf нет — пропуск")
else:
    print("vitrage_recognize D2: Проба_3.dxf недоступен — пропуск")
