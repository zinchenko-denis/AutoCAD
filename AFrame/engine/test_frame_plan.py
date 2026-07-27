# -*- coding: utf-8 -*-
"""Юниты AFrame Э1: frame_plan. Запуск: PYTHONUTF8=1
python3 test_frame_plan.py. Числа посчитаны руками (STAGE3_FRAME.md);
FR-D — факты боевого эталона frame_lenprospekt (skip без testdata)."""
import json
import os
import sys
from frame_plan import frame_plan

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


# ── FR1 (ТЗ Германа 26.07): кронштейны НА КАЖДУЮ направляющую —
#    300 от низа, 300 от верха, между ними равномерно ≤ шага;
#    угловая зона 1500; первый кронштейн направляющей, начавшейся
#    стыком на перекрытии, — несущий ──
p = frame_plan({"system": {"name": "Вектор-1", "mid_rail_over": None},
                "contours": [{"outer": rect(0, 0, 5000, 6000)}],
                "joints_x": [608, 2500, 4392],
                "floors_y": [3000]})
ok(p["ok"], "FR1: ok")
ok(p["summary"]["rails"] == 6 and
   all(near(r["len"], 2995) for r in p["rails"]),
   "FR1: 6 направляющих по 2995 (стык 10 на отметке) (%s)"
   % p["summary"])
mid = sorted((b["y"], b["kind"]) for b in p["brackets"]
             if near(b["x"], 2500))
ok([round(y, 1) for y, k in mid] ==
   [300.0, 1497.5, 2695.0, 3305.0, 4502.5, 5700.0],
   "FR1: рядовая стойка (шаг 1200) — 300…300 на каждую направляющую "
   "(%s)" % mid)
ok([k for y, k in mid] == ["рядовой"] * 3 +
   ["несущий", "рядовой", "рядовой"],
   "FR1b: несущий = первый кронштейн направляющей от стыка (В-е)")
cor = sorted(round(b["y"], 2) for b in p["brackets"]
             if near(b["x"], 608))
ok(cor == [300.0, 1098.33, 1896.67, 2695.0,
           3305.0, 4103.33, 4901.67, 5700.0],
   "FR1c: угловая стойка (зона 1500, шаг 800) — по 4 на направляющую "
   "как 300-800-800-800-300 (%s)" % cor)
ok(p["summary"]["rail_stock_est"] == 3,
   "FR1d: 17.97 м.п. → 3 хлыста")

# ── FR2: окно (1000,2000)-(2000,3500); стойка сквозь окно рвётся,
#    боковые сплошные; сегмент над окном стартует своим 300 ──
p = frame_plan({"system": "Вектор-1",
                "contours": [{"outer": rect(0, 0, 3000, 6000),
                              "holes": [rect(1000, 2000, 2000, 3500)]}],
                "joints_x": [500, 1500, 2500],
                "floors_y": [3000]})
r15 = sorted([(r["y0"], r["y1"]) for r in p["rails"]
              if near(r["x"], 1500)])
ok(r15 == [(0.0, 2000.0), (3500.0, 6000.0)],
   "FR2: стойка сквозь окно порвана по bbox окна (%s)" % r15)
ok(not any(near(b["x"], 1500) and b["kind"] == "несущий"
           for b in p["brackets"]),
   "FR2: несущего на 3000 у порванной стойки нет (отметка в окне)")
b15 = sorted(round(b["y"], 2) for b in p["brackets"]
             if near(b["x"], 1500))
ok(b15 == [300.0, 1000.0, 1700.0,
           3800.0, 4433.33, 5066.67, 5700.0],
   "FR2: 300…300 на каждый кусок, равномерно ≤800 (%s)" % b15)
r5 = sorted([(r["y0"], r["y1"]) for r in p["rails"] if near(r["x"], 500)])
ok(r5 == [(0.0, 2995.0), (3005.0, 6000.0)],
   "FR2: стойка сбоку окна сплошная со стыком на отметке (%s)" % r5)

# ── FR3: кляммеры по швам раскладки: стартовый на низе зоны И над
#    проёмом, рядовой на каждом шве сегмента ──
rows = [605.0 * i for i in range(1, 10)]
p = frame_plan({"system": "Вектор-1",
                "contours": [{"outer": rect(0, 0, 3000, 6000),
                              "holes": [rect(1000, 2000, 2000, 3500)]}],
                "joints_x": [500, 1500, 2500],
                "floors_y": [3000], "rows_y": rows})
c15 = [c for c in p["clamps"] if near(c["x"], 1500)]
st = sorted(c["y"] for c in c15 if c["kind"] == "стартовый")
ok(st == [0.0, 3500.0],
   "FR3: стартовые кляммеры — низ зоны и над окном (%s)" % st)
ok(sorted(c["y"] for c in c15 if c["kind"] == "рядовой") ==
   [605.0, 1210.0, 1815.0, 3630.0, 4235.0, 4840.0, 5445.0],
   "FR3: рядовые кляммеры по швам вне окна")
c5 = [c for c in p["clamps"] if near(c["x"], 500)]
ok(len([c for c in c5 if c["kind"] == "рядовой"]) == 8 and
   len([c for c in c5 if c["kind"] == "стартовый"]) == 1 and
   [c["y"] for c in c5 if c["kind"] == "комбинированный"] == [3025.0],
   "FR3: сплошная стойка — стартовый + 8 рядовых + КОМБИНИРОВАННЫЙ "
   "на первом шве после стыка (термошов, ТЗ 26.07)")

# ── FR4 (ТЗ 26.07 §2): МЕЖЭТАЖНАЯ — кронштейны по центру перекрытия
#    с шагом ПО ГОРИЗОНТАЛИ (все несущие), НГП горизонтальный,
#    НСП в рустах со вставками ──
p = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                "contours": [{"outer": rect(0, 0, 1220, 6000)}],
                "joints_x": [610], "floors_y": [3000]})
bx = sorted(round(b["x"], 1) for b in p["brackets"])
ok(bx == [150.0, 610.0, 1070.0] and
   all(near(b["y"], 3000) and b["kind"] == "несущий"
       for b in p["brackets"]),
   "FR4: кронштейны по перекрытию горизонтально 150/610/1070, все "
   "несущие (%s)" % bx)
ok([h["kind"] for h in p["hrails"]] == ["НГП"] and
   near(p["hrails"][0]["len"], 1220),
   "FR4b: НГП на всю ширину на отметке")
ok(sorted((r["y0"], r["y1"]) for r in p["rails"]) ==
   [(0.0, 2995.0), (3005.0, 6000.0)] and
   all(r["kind"] == "НСП" for r in p["rails"]),
   "FR4c: НСП-куски со стыком-зазором на отметке")
ok(p["fittings"] == [{"x": 610.0, "y": 3000.0, "kind": "вставка"}],
   "FR4d: вставка на стыке НСП с НГП (%s)" % p["fittings"])

# ── FR11 (межэтажная с окном): кронштейн в проёме пропускается,
#    НГП рвётся окном, куски над/под окном = ШП-60-20, СП-60-40 в
#    подоконной зоне со скобами С1 к крайним профилям ──
p = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                "contours": [{"outer": rect(0, 0, 6000, 6100),
                              "holes": [rect(2500, 2000, 3500, 3500)]}],
                "joints_x": [600, 1800, 3000, 4200, 5400],
                "floors_y": [3000]})
bx = sorted(round(b["x"], 1) for b in p["brackets"])
ok(bx == [150.0, 825.0, 1500.0, 2250.0, 3750.0, 4500.0, 5175.0,
          5850.0],
   "FR11: 9 позиций минус одна в окне = 8; углы 1500 чаще (%s)" % bx)
ngp = sorted((h["x0"], h["x1"]) for h in p["hrails"]
             if h["kind"] == "НГП")
ok(ngp == [(0.0, 2500.0), (3500.0, 6000.0)],
   "FR11b: НГП порван окном (%s)" % ngp)
shp = sorted((r["y0"], r["y1"]) for r in p["rails"]
             if r["kind"] == "ШП-60-20")
ok(shp == [(0.0, 2000.0), (3500.0, 6100.0)],
   "FR11c: куски над/под окном — ШП-60-20 (%s)" % shp)
sp = [h for h in p["hrails"] if h["kind"] == "СП-60-40"]
ok(len(sp) == 1 and near(sp[0]["x0"], 1800) and
   near(sp[0]["x1"], 4200) and near(sp[0]["y"], 2000),
   "FR11d: СП-60-40 под окном между крайними профилями")
ok(sum(1 for f in p["fittings"] if f["kind"] == "скоба С1") == 2 and
   sum(1 for f in p["fittings"] if f["kind"] == "вставка") == 4,
   "FR11e: 2 скобы С1 + 4 вставки (НСП×перекрытие)")

# ── FR12 (ТЗ 26.07 §3): ОРТОГОНАЛЬНАЯ — сетка кронштейнов 600×шаг,
#    ГП-40-40 на каждом ряду, ШП-60-20 по рустам ──
p = frame_plan({"system": "Ортогональная", "sub_type": "ortho",
                "contours": [{"outer": rect(0, 0, 4000, 3000)}],
                "joints_x": [600, 1200, 1800, 2400, 3000, 3600]})
ok(p["summary"]["brackets_row"] == 35 and
   p["summary"]["brackets_main"] == 0,
   "FR12: сетка 7×5 кронштейнов, все рядовые (%s)" % p["summary"])
ys12 = sorted({round(b["y"], 1) for b in p["brackets"]})
ok(ys12 == [300.0, 900.0, 1500.0, 2100.0, 2700.0],
   "FR12b: вертикальный шаг сетки 600 от 300 (%s)" % ys12)
ok(len([h for h in p["hrails"] if h["kind"] == "ГП-40-40"]) == 5,
   "FR12c: ГП-40-40 на каждом ряду сетки")
ok(len(p["rails"]) == 6 and
   all(r["kind"] == "ШП-60-20" for r in p["rails"]),
   "FR12d: 6 ШП по рустам, свес 300 ≤ 300 — без note")

# ── FR13 (ортогональная, окно): Z-профиль у окна со смещением 100 и
#    выступом 50 ──
p = frame_plan({"system": "Ортогональная", "sub_type": "ortho",
                "contours": [{"outer": rect(0, 0, 3000, 5000),
                              "holes": [rect(1000, 2000, 2000, 3500)]}],
                "joints_x": [996]})
z = [r for r in p["rails"] if r["kind"] == "Z-профиль"]
ok(len(z) == 1 and near(z[0]["x"], 900) and
   near(z[0]["y0"], 1950) and near(z[0]["y1"], 3550),
   "FR13: Z-профиль у окна 900, [1950..3550] (%s)" % z)

# ── FR5: система-переопределение поверх пресета ──
p = frame_plan({"system": {"name": "Вектор-1", "rail_gap": 8.0},
                "contours": [{"outer": rect(0, 0, 1220, 6000)}],
                "joints_x": [610], "floors_y": [3000]})
r = sorted([(x["y0"], x["y1"]) for x in p["rails"]])
ok(r == [(0.0, 2996.0), (3004.0, 6000.0)],
   "FR5: rail_gap переопределён (8) (%s)" % r)

# ── FR6: ошибки — нет осей / неизвестная система ──
p = frame_plan({"system": "Вектор-1",
                "contours": [{"outer": rect(0, 0, 610, 600)}]})
ok(not p["ok"] and "joints_x" in p["error"], "FR6: нет осей — отказ")
p = frame_plan({"system": "НЕТ-ТАКОЙ", "joints_x": [1],
                "contours": [{"outer": rect(0, 0, 610, 600)}]})
ok(not p["ok"], "FR6b: неизвестная система — отказ")

# ── FR7 (фидбэк Германа 26.07): ось у грани окна (996 при грани
#    1000) — на высоте окна кусок СМЕЩЁН на грань−100=900 вместе с
#    кронштейнами; под/над окном — по оси руста ──
p = frame_plan({"system": "Standart",
                "contours": [{"outer": rect(0, 0, 3000, 5000),
                              "holes": [rect(1000, 2000, 2000, 3500)]}],
                "joints_x": [996],
                "rows_y": [605.0 * i for i in range(1, 8)]})
r996 = sorted((r["y0"], r["y1"]) for r in p["rails"]
              if near(r["x"], 996))
r900 = sorted((r["y0"], r["y1"]) for r in p["rails"]
              if near(r["x"], 900))
ok(r996 == [(0.0, 1950.0), (3550.0, 5000.0)] and
   r900 == [(1950.0, 3550.0)],
   "FR7: оконная стойка 996→900 с выступом 50 за проём (длина = "
   "сторона окна + 100, ТЗ 26.07) (%s / %s)" % (r996, r900))
b900 = sorted(b["y"] for b in p["brackets"] if near(b["x"], 900))
ok(b900 == [2250.0, 2750.0, 3250.0],
   "FR7b: кронштейны оконной стойки — 300…300 по её длине (%s)"
   % b900)
ok(not any(near(b["x"], 996) and 1950 < b["y"] < 3550
           for b in p["brackets"]),
   "FR7c: на оси руста в высоте окна кронштейнов нет")
ok(all(c["kind"] == "боковой" for c in p["clamps"]
       if near(c["x"], 900)) and
   any(near(c["x"], 900) for c in p["clamps"]),
   "FR7d: кляммеры оконной стойки — БОКОВЫЕ (примыкание к окну/"
   "отливу)")

# ── FR8 (26.07): floors пуст + floor_step — перекрытия сами шагом
#    этажа: направляющие не «бесконечные» ──
p = frame_plan({"system": "Standart",
                "contours": [{"outer": rect(0, 0, 1220, 6100)}],
                "joints_x": [610], "floor_step": 3000})
r = sorted((x["y0"], x["y1"]) for x in p["rails"])
ok(r == [(0.0, 2995.0), (3005.0, 5995.0), (6005.0, 6100.0)],
   "FR8: автоперекрытия 3000/6000, стыки с зазором (%s)" % r)
ok(p["summary"]["brackets_main"] == 2 and
   any("автоматически шагом 3000" in n for n in p["notes"]),
   "FR8b: несущие на автоотметках + note")

# ── FR9: направляющая длиннее хлыста — предупреждение ──
p = frame_plan({"system": "Standart",
                "contours": [{"outer": rect(0, 0, 1220, 7000)}],
                "joints_x": [610]})
ok(any("хлыста" in n for n in p["notes"]),
   "FR9: note про хлыст 6000 (%s)" % p["notes"])

# ── FR10 (ТЗ 26.07): плита шире 600 — доп. направляющая по центру
#    (пролёт осей 1208 > 650 → ось на середине) ──
p = frame_plan({"system": "Standart",
                "contours": [{"outer": rect(0, 0, 2416, 3000)}],
                "joints_x": [604, 1812]})
xs10 = sorted({round(r["x"], 1) for r in p["rails"]})
ok(xs10 == [604.0, 1208.0, 1812.0],
   "FR10: доп. ось по центру широкой плиты (%s)" % xs10)

# ── FR-D: факты боевого эталона Ленпроспекта (skip без testdata) ──
ETALON = "/home/claude/atspec-testdata/dxf/facades/frame_lenprospekt/" \
         "frame_ps.json"
if os.path.exists(ETALON):
    et = json.load(open(ETALON, encoding="utf-8"))
    # кляммеры рядовые фрагмента — шаг 605 (плита 600 + шов 5)
    ry = sorted({c["y"] for c in et["frag_m1"]
                 if c["b"] == "КР-НС-1_рядовой"})
    base = [602.5, 1207.5, 1812.5, 2417.5]
    ok(all(any(near(y, b, 0.6) for y in ry) for b in base),
       "FR-D1: кляммеры эталона по шагу 605 (%s)" % ry[:6])
    # кронштейны фрагмента: первый ~300 от низа, есть цепочка ~950
    by = sorted({c["y"] for c in et["frag_m1"]
                 if c["la"] == "кронштейны"})
    ok(any(near(y, 300, 1) for y in by),
       "FR-D2: первый кронштейн эталона на ~300 (%s)" % by[:8])
    ok(any(near(b - a, 950, 60) for a in by for b in by if b > a),
       "FR-D3: в эталоне есть шаг кронштейнов ~950")
    # шаг стоек по X в модели: серия 600..606 (2 стойки на плиту 1205)
    xs = sorted(i["x"] for i in et["model_inserts"] if i["b"] == "м1*")
    dl = [xs[i + 1] - xs[i] for i in range(len(xs) - 1)]
    ok(sum(1 for d in dl if 600 - 3 <= d <= 606) >= 5,
       "FR-D4: шаг стоек эталона 602.5–605 подтверждён")
else:
    print("FR-D: эталон не найден — пропуск (нужен atspec-testdata)")

print("frame_plan: %d проверок OK" % _n)
