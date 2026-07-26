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


# ── FR1: прямоугольник 3040×6000, 4 стойки, перекрытие 3000,
#    Вектор-1 (1200/800, угловая зона 608, старт 300, зазор 10) ──
p = frame_plan({"system": "Вектор-1",
                "contours": [{"outer": rect(0, 0, 3040, 6000)}],
                "joints_x": [608, 1216, 1824, 2432],
                "floors_y": [3000]})
ok(p["ok"], "FR1: ok")
ok(p["summary"]["rails"] == 8 and
   all(near(r["len"], 2995) for r in p["rails"]),
   "FR1: 8 направляющих по 2995 (стык 10 на отметке) (%s)"
   % p["summary"])
ok(p["summary"]["brackets_main"] == 4 and
   all(near(b["y"], 3000) for b in p["brackets"]
       if b["kind"] == "несущий"),
   "FR1: несущий на каждой стойке по центру отметки (В15)")
mid = sorted(b["y"] for b in p["brackets"]
             if b["kind"] == "рядовой" and near(b["x"], 1216))
ok(mid == [300.0, 1200.0, 2100.0, 4000.0, 5000.0],
   "FR1: рядовая стойка — старт 300, равномерно ≤1200 (В16) (%s)" % mid)
cor = sorted(b["y"] for b in p["brackets"]
             if b["kind"] == "рядовой" and near(b["x"], 608))
ok(cor == [300.0, 975.0, 1650.0, 2325.0, 3750.0, 4500.0, 5250.0],
   "FR1: угловая стойка — шаг ≤800 (В17: зона 608) (%s)" % cor)
ok(p["summary"]["brackets_row"] == 2 * 5 + 2 * 7,
   "FR1: рядовых всего 24 (%s)" % p["summary"])
ok(p["summary"]["rail_stock_est"] == 4,
   "FR1: 23.96 м.п. → 4 хлыста по 6000")

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
b15 = sorted(b["y"] for b in p["brackets"] if near(b["x"], 1500))
ok(b15 == [300.0, 1150.0, 3800.0, 4900.0],
   "FR2: рядовые сегментов — 300+равномерно, над окном свой старт "
   "(%s)" % b15)
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
ok(len([c for c in c5 if c["kind"] == "рядовой"]) == 9 and
   len([c for c in c5 if c["kind"] == "стартовый"]) == 1,
   "FR3: сплошная стойка — 1 стартовый + все 9 швов")

# ── FR4: межэтажная — только несущие на отметках, рядовых нет ──
p = frame_plan({"system": "Межэтажная",
                "contours": [{"outer": rect(0, 0, 1220, 6000)}],
                "joints_x": [610], "floors_y": [3000]})
ok(p["summary"]["brackets_row"] == 0 and
   p["summary"]["brackets_main"] == 1,
   "FR4: межэтажная без рядовых (%s)" % p["summary"])

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
                "joints_x": [996]})
r996 = sorted((r["y0"], r["y1"]) for r in p["rails"]
              if near(r["x"], 996))
r900 = sorted((r["y0"], r["y1"]) for r in p["rails"]
              if near(r["x"], 900))
ok(r996 == [(0.0, 2000.0), (3500.0, 5000.0)] and
   r900 == [(2000.0, 3500.0)],
   "FR7: стойка у грани окна смещена на высоте окна (996→900) "
   "(%s / %s)" % (r996, r900))
b900 = sorted(b["y"] for b in p["brackets"] if near(b["x"], 900))
ok(b900 == [2300.0, 2900.0],
   "FR7b: кронштейны смещённого куска — 300 от низа куска + "
   "равномерно (%s)" % b900)
ok(not any(near(b["x"], 996) and 2000 < b["y"] < 3500
           for b in p["brackets"]),
   "FR7c: на оси руста в высоте окна кронштейнов нет")

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
