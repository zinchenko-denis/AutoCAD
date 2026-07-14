# -*- coding: utf-8 -*-
"""Юниты vitrage_plan. Самодостаточны (числа эталона Проба_штапики_2 зашиты
литералами), плюс опциональная сверка с живым DXF, если он доступен
(/home/claude/atspec-testdata/dxf/ — как D-серия в ATableSpec/test_beads).

Эталонные числа (разбор 13.07):
  проём X 19301.1..21470.5 (2169.4 = 3×705 + 54.4), низ Y 34739.5,
  ярусы стоек 2715 и 2990, терморазрыв 10, оси ригелей от низа 45/595/955
  (низ яруса) и 2445/2805 (м/э зона), тело стойки 54.4, тело ригеля 45.6,
  заполнения: свет+15 → 666Х519 / 666Х329 / 666Х1459 (Х — U+0425).
"""
import json
import os
import sys
import tempfile

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

from vitrage_plan import (build_plan, stand_axes, stand_tiers, cell_rows,
                          size_attr, fmt_len, main, X_CYR)

PASS = 0


def ok(cond, msg):
    global PASS
    assert cond, msg
    PASS += 1


def near(a, b, tol=0.05):
    return abs(a - b) <= tol


# ── U1: оси стоек эталона ──
axes, notes = stand_axes(19301.1, 21470.5, 54.4, step_x=705)
ok(len(axes) == 4, f"U1: осей {len(axes)} != 4")
for got, exp in zip(axes, (19328.3, 20033.3, 20738.3, 21443.3)):
    ok(near(got, exp), f"U1: ось {got:.2f} != {exp}")
ok(notes == [], f"U1: неожиданные notes {notes}")

# ── U2: ярусы эталона ──
segs, notes = stand_tiers(34739.5, 5715.0, [2715, 2990], 10)
ok(segs[0] == (34739.5, 2715.0) and near(segs[1][0], 37464.5) and segs[1][1] == 2990.0,
   f"U2: сегменты {segs}")
ok(notes == [], f"U2: notes {notes}")

# ── U3: РАЗМЕР_ЗАП — формула свет+15 и кириллическая Х ──
ok(size_attr(650.6, 504.4, 15) == "666Х519", "U3: Сп1")
ok(size_attr(650.6, 314.4, 15) == "666Х329", "U3: Вр1/Эм1")
ok(size_attr(650.6, 1444.4, 15) == "666Х1459", "U3: Сп4")
ok(size_attr(650.6, 704.4, 15) == "666Х719", "U3: Сп2")
sep = size_attr(1, 1, 0)
x = [c for c in sep if not c.isdigit()][0]
ok(ord(x) == 0x425, f"U3: разделитель U+{ord(x):04X} != U+0425")
ok(X_CYR == chr(0x425), "U3: X_CYR не кириллическая")

# ── U4: формат ДЛИНА ──
ok(fmt_len(2715) == "2715.00" and fmt_len(705) == "705.00", "U4: fmt_len")

# ── U5: полный план эталонного стека ──
REQ = {
    "opening": {"x0": 19301.1, "y0": 34739.5, "x1": 21470.5, "y1": 40454.5},
    "grid": {"step_x": 705, "rail_y": [45, 595, 955, 2445, 2805],
             "tiers": [2715, 2990], "tier_gap": 10},
    "blocks": {"stand": {"name": "17_07_24", "body_w": 54.4},
               # 17_06_01 рисован ВЕРТИКАЛЬНО → его вставки в чертеже с rot=270;
               # с 14.07 rot приходит от C# (мода вставок / образец), не хардкод
               "rigel": {"name": "17_06_01", "body_w": 45.6, "rot": 270},
               "fill": {"name": "СТП", "fold": 15}},
}
plan = build_plan(REQ)
ok(plan["ok"], "U5: план не собрался")
st = [i for i in plan["inserts"] if i["kind"] == "stand"]
rg = [i for i in plan["inserts"] if i["kind"] == "rigel"]
fl = [i for i in plan["inserts"] if i["kind"] == "fill"]
ok(len(st) == 8, f"U5: стоек {len(st)} != 8")
ok(len(rg) == 15, f"U5: ригелей {len(rg)} != 15")
low = [s for s in st if near(s["y"], 34739.5)]
ok(len(low) == 4 and all(s["dyn"]["Длина"] == 2715.0 and s["attrs"]["ДЛИНА"] == "2715.00"
                         for s in low), "U5: нижний ярус стоек")
up = [s for s in st if near(s["y"], 37464.5)]
ok(len(up) == 4 and all(s["dyn"]["Длина"] == 2990.0 for s in up), "U5: верхний ярус")
ok(all(s["rot"] == 0 and s["layer"] == "RF-стойки" and s["block"] == "17_07_24"
       for s in st), "U5: контракт стойки")
# марки-типоразмеры: 2715 → С1, 2990 → С2
ok({s["attrs"]["ИМЯ"] for s in low} == {"С1"} and
   {s["attrs"]["ИМЯ"] for s in up} == {"С2"}, "U5: марки стоек по типоразмеру")
# ригель первой отметки первого пролёта
r0 = [r for r in rg if near(r["y"], 34784.5) and near(r["x"], 19328.3)]
ok(len(r0) == 1, "U5: ригель (45, пролёт 1) не найден")
r0 = r0[0]
ok(r0["rot"] == 270 and r0["dyn"]["Длина"] == 705.0 and
   r0["attrs"]["ДЛИНА"] == "705.00" and r0["attrs"]["ИМЯ"] == "Р1" and
   r0["layer"] == "RF-ригеля", f"U5: контракт ригеля {r0}")
# заполнения: ряд 45..595 → 666Х519, вставка (19355.5, 34807.3)
f0 = [f for f in fl if near(f["y"], 34807.3, 0.1) and near(f["x"], 19355.5, 0.1)]
ok(len(f0) == 1, "U5: заполнение ряда 1 пролёта 1 не найдено")
f0 = f0[0]
ok(f0["attrs"]["РАЗМЕР_ЗАП"] == "666Х519" and
   near(f0["dyn"]["Ширина"], 650.6) and near(f0["dyn"]["Высота"], 504.4) and
   f0["layer"] == "RF-заполнения", f"U5: контракт заполнения {f0}")
sizes = sorted({f["attrs"]["РАЗМЕР_ЗАП"] for f in fl})
ok("666Х519" in sizes and "666Х329" in sizes and "666Х1459" in sizes,
   f"U5: набор размеров {sizes}")
# ряд-щель ниже отметки 45 (свет 22.2 < 50) не генерится
ok(not any(f["y"] < 34784.5 for f in fl), "U5: щель у низа не должна заполняться")
# одинаковый размер → одна марка
m329 = {f["attrs"]["МАРКИРОВКА"] for f in fl if f["attrs"]["РАЗМЕР_ЗАП"] == "666Х329"}
ok(len(m329) == 1, f"U5: марки дублируются {m329}")
# сводка согласована
ok(plan["summary"]["stands"] == 8 and plan["summary"]["rigels"] == 15 and
   plan["summary"]["fills"] == len(fl), "U5: summary")

# ── U6: остаточный пролёт + note ──
axes6, notes6 = stand_axes(0, 2100, 50, step_x=705)
ok(near(axes6[0], 25) and near(axes6[-1], 2075), "U6: крайние оси")
ok(near(axes6[-1] - axes6[-2], 640), f"U6: остаточный {axes6[-1]-axes6[-2]:.1f}")
ok(len(notes6) == 1 and "остаточный" in notes6[0], f"U6: note {notes6}")

# ── U7: n_cols равными долями ──
axes7, _ = stand_axes(0, 2054, 54, n_cols=4)
ok(len(axes7) == 5 and near(axes7[1] - axes7[0], 500), f"U7: {axes7}")

# ── U8: ошибки входа ──
def must_fail(req, tag):
    try:
        build_plan(req)
    except ValueError:
        ok(True, tag)
        return
    ok(False, f"{tag}: ValueError не поднят")

must_fail({"opening": {"x0": 0, "y0": 0, "x1": 1000, "y1": 1000},
           "grid": {"step_x": 705}, "blocks": {}}, "U8: без stand.name")
must_fail({"opening": {"x0": 0, "y0": 0, "x1": 1000, "y1": 1000},
           "grid": {"step_x": 705, "rail_y": [1500]},
           "blocks": {"stand": {"name": "s"}, "rigel": {"name": "r"}}},
          "U8: отметка вне проёма")
must_fail({"opening": {"x0": 0, "y0": 0, "x1": 0, "y1": 1000},
           "grid": {"step_x": 705}, "blocks": {"stand": {"name": "s"}}},
          "U8: вырожденный проём")
must_fail({"opening": {"x0": 0, "y0": 0, "x1": 1000, "y1": 1000},
           "grid": {"step_x": 705, "rail_y": [500]},
           "blocks": {"stand": {"name": "s"}}}, "U8: отметки без rigel.name")
must_fail({"opening": {"x0": 0, "y0": 0, "x1": 1000, "y1": 1000},
           "blocks": {"stand": {"name": "s"}}}, "U8: ни step_x, ни n_cols")

# ── U9: cell_rows — щели отсекаются ──
rows9 = cell_rows(0, 1000, [45, 595], 45.6, 50)
ok(len(rows9) == 2, f"U9: рядов {len(rows9)} != 2 (щель 22.2 внизу отсечена)")
ok(near(rows9[0][0], 67.8) and near(rows9[0][1], 504.4), f"U9: ряд1 {rows9[0]}")

# ── U10: нормализация перевёрнутых точек ──
p10 = build_plan({"opening": {"x0": 2169.4, "y0": 2715, "x1": 0, "y1": 0},
                  "grid": {"step_x": 705},
                  "blocks": {"stand": {"name": "s", "body_w": 54.4}}})
ok(p10["ok"] and p10["opening"]["w"] == 2169.4, "U10: swap точек")

# ── U11: CLI smoke (файл → файл, ошибка → exit 1) ──
tmp = tempfile.mkdtemp()
rq = os.path.join(tmp, "req.json")
out = os.path.join(tmp, "plan.json")
with open(rq, "w", encoding="utf-8") as f:
    json.dump(REQ, f, ensure_ascii=False)
rc = main(["vitrage_plan.py", rq, out])
ok(rc == 0, "U11: rc != 0")
with open(out, encoding="utf-8") as f:
    ok(json.load(f)["summary"]["stands"] == 8, "U11: out.json")
with open(rq, "w", encoding="utf-8") as f:
    json.dump({"opening": {"x0": 0, "y0": 0, "x1": 10, "y1": 10}}, f)
rc = main(["vitrage_plan.py", rq, out])
ok(rc == 1, "U11: ошибка входа должна давать rc 1")

# ── U12: rot — с чертежа/образца, дефолт 0 (фикс 14.07: не хардкодить 270) ──
p12 = build_plan({"opening": {"x0": 0, "y0": 0, "x1": 2050, "y1": 3000},
                  "grid": {"step_x": 1000, "rail_y": [300]},
                  "blocks": {"stand": {"name": "s", "body_w": 50},
                             "rigel": {"name": "r"}}})
ok(all(i["rot"] == 0 for i in p12["inserts"]), "U12: дефолт rot=0 для всех")
ok(build_plan({"opening": {"x0": 0, "y0": 0, "x1": 2050, "y1": 3000},
               "grid": {"step_x": 1000, "rail_y": [300]},
               "blocks": {"stand": {"name": "s", "body_w": 50, "rot": 90},
                          "rigel": {"name": "r", "rot": 270}}}
              )["inserts"][0]["rot"] == 90, "U12: stand.rot прокинут")

# ── U13: dims — габариты + межосевые цепочки (числа эталона) ──
dm = plan["dims"]
ok(len(dm) == 4, f"U13: dims {len(dm)} != 4")
h_chain = [d for d in dm if d["dir"] == "h" and len(d["pts"]) > 2][0]
segs13 = [round(b - a, 1) for a, b in zip(h_chain["pts"], h_chain["pts"][1:])]
ok(segs13 == [705.0, 705.0, 705.0], f"U13: межосевые стоек {segs13}")
h_ovr = [d for d in dm if d["dir"] == "h" and len(d["pts"]) == 2][0]
ok(near(h_ovr["pts"][1] - h_ovr["pts"][0], 2169.4), f"U13: габарит ширины {h_ovr}")
v_chain = [d for d in dm if d["dir"] == "v" and len(d["pts"]) > 2][0]
ok(near(v_chain["pts"][0], 34739.5) and near(v_chain["pts"][-1], 40454.5) and
   len(v_chain["pts"]) == 7, f"U13: верт. цепочка от габарита до габарита {v_chain['pts']}")
v_ovr = [d for d in dm if d["dir"] == "v" and len(d["pts"]) == 2][0]
ok(near(v_ovr["pts"][1] - v_ovr["pts"][0], 5715.0), "U13: габарит высоты")
ok(h_chain["line"] < 34739.5 and v_chain["ref"] > 21400, "U13: линии снизу/справа")
p13 = build_plan({**REQ, "params": {"dims": False}})
ok(p13["dims"] == [], "U13: dims отключаемы")

print(f"vitrage_plan: {PASS} проверок OK")

# ── D: опциональная сверка с живым эталоном ──
DXF = "/home/claude/atspec-testdata/dxf/Проба_штапики_2.dxf"
if os.path.exists(DXF):
    try:
        import ezdxf
    except ImportError:
        ezdxf = None
    if ezdxf is not None:
        doc = ezdxf.readfile(DXF)
        msp = doc.modelspace()
        Y_LO, Y_HI = 34700, 40500     # эталонный стек
        X_LO, X_HI = 19300, 21500

        def picks(layer):
            res = []
            for e in msp.query("INSERT"):
                if e.dxf.layer != layer:
                    continue
                p = e.dxf.insert
                if X_LO < p.x < X_HI and Y_LO < p.y < Y_HI:
                    res.append((round(p.x, 1), round(p.y, 1),
                                {a.dxf.tag: a.dxf.text for a in e.attribs}))
            return res

        d = 0
        # план — синтетический фрагмент (2 нижних яруса), реальный витраж выше:
        # сверка субсетом — каждая вставка ПЛАНА обязана существовать в факте.
        fact_st = picks("RF-стойки")
        assert len(fact_st) >= 8, f"D: стоек в эталоне {len(fact_st)}"
        for s in st:
            hit = [f for f in fact_st
                   if abs(f[0] - s["x"]) <= 0.5 and abs(f[1] - s["y"]) <= 0.5]
            assert len(hit) == 1, f"D: стойка плана {s['x']},{s['y']} не найдена"
            assert hit[0][2]["ДЛИНА"] == s["attrs"]["ДЛИНА"], \
                f"D: ДЛИНА {hit[0][2]['ДЛИНА']} != {s['attrs']['ДЛИНА']}"
            d += 1
        # ригели нижних отметок: позиция плана существует в факте
        fact_rg = picks("RF-ригеля")
        for r in rg:
            hit = [f for f in fact_rg
                   if abs(f[0] - r["x"]) <= 0.5 and abs(f[1] - r["y"]) <= 0.5]
            assert len(hit) == 1, f"D: ригель плана {r['x']},{r['y']} не найден"
            assert hit[0][2]["ДЛИНА"] == r["attrs"]["ДЛИНА"]
            d += 1
        # заполнения: по позиции — РАЗМЕР_ЗАП байт-в-байт. Верхняя ячейка
        # фрагмента открыта до края синтетического проёма, у реального витража
        # там ригели 11 ярусов — сверяем только ячейки НИЖЕ последней отметки.
        fact_fl = picks("RF-заполнения")
        top_rail = 34739.5 + 2805
        fl_cmp = [f for f in fl if f["y"] + f["dyn"]["Высота"] <= top_rail + 0.5]
        matched = 0
        for f in fl_cmp:
            hit = [g for g in fact_fl
                   if abs(g[0] - f["x"]) <= 0.5 and abs(g[1] - f["y"]) <= 0.5]
            if hit:
                assert hit[0][2]["РАЗМЕР_ЗАП"] == f["attrs"]["РАЗМЕР_ЗАП"], \
                    f"D: РАЗМЕР_ЗАП {hit[0][2]['РАЗМЕР_ЗАП']} != {f['attrs']['РАЗМЕР_ЗАП']}"
                matched += 1
                d += 1
        assert matched >= 10, f"D: совпало заполнений только {matched}"
        print(f"vitrage_plan D-эталон: {d} сверок OK "
              f"(стойки 8, ригели {len(rg)}, заполнения {matched})")
    else:
        print("vitrage_plan D-эталон: ezdxf нет — пропуск")
else:
    print("vitrage_plan D-эталон: DXF недоступен — пропуск")
