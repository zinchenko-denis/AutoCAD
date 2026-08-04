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
# УТОЧНЕНО 30.07 п.10: к оси 610 добавились КРАЕВЫЕ профили в 100 мм
# от краёв замкнутой области (углы и края захватки — правило Германа)
ok(sorted({r["x"] for r in p["rails"]}) == [100.0, 610.0, 1120.0] and
   sorted(set((r["y0"], r["y1"]) for r in p["rails"])) ==
   [(0.0, 2995.0), (3005.0, 6000.0)] and
   all(r["kind"] == "НСП" for r in p["rails"]),
   "FR4c: НСП-куски со стыком-зазором на отметке + краевые оси "
   "100/1120 (%s)" % sorted({r["x"] for r in p["rails"]}))
ok([f["x"] for f in p["fittings"]] == [100.0, 610.0, 1120.0] and
   all(near(f["y"], 3000.0) and f["kind"] == "вставка"
       for f in p["fittings"]),
   "FR4d: вставка на стыке КАЖДОГО НСП с НГП (%s)" % p["fittings"])
ok(sorted(round(b["x"], 1) for b in p["brackets"]) ==
   [150.0, 610.0, 1070.0],
   "FR4e: краевые профили НЕ трогают кронштейны НГП (150/610/1070)")

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
# УТОЧНЕНО 30.07 п.10: +2 краевых профиля (100 и 5900) → +2 вставки
ok(sum(1 for f in p["fittings"] if f["kind"] == "скоба С1") == 2 and
   sum(1 for f in p["fittings"] if f["kind"] == "вставка") == 6,
   "FR11e: 2 скобы С1 + 6 вставок (НСП×перекрытие, с краевыми)")
ok(sorted(f["x"] for f in p["fittings"] if f["kind"] == "вставка") ==
   [100.0, 600.0, 1800.0, 4200.0, 5400.0, 5900.0],
   "FR11f: краевые межэтажные профили 100 и 5900 (п.10)")

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
z = sorted([r for r in p["rails"] if r["kind"] == "Z-профиль"],
           key=lambda r: r["x"])
# УТОЧНЕНО 02.08 (замечание №1): Z у ОБЕИХ граней окна всегда —
# раньше правая грань (2100) оставалась без профиля, если рядом не
# было оси сетки, и доп. кронштейны п.9 висели в воздухе
ok(len(z) == 2 and near(z[0]["x"], 900) and near(z[1]["x"], 2100) and
   all(near(r["y0"], 1950) and near(r["y1"], 3550) for r in z),
   "FR13: Z-профили у окна 900 И 2100, [1950..3550] (%s)" % z)

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

# ── FR-C (этап 4): шаги ПО РАСЧЁТУ (frame_calc) вместо справочника ──
# C1: республиканская-кейс (h=41.2, вынос 230, анкер 1880, ШП-60-20-20)
# → их выводы 800/450; в угловой зоне кронштейны чаще
CALC_RESP = {"wind_region": "II", "terrain": "B", "height": 41.2,
             "q_clad": 25, "offset": 230, "na_max": 1880,
             "profile": "ШП-60-20-20-1,2", "q_rails": 1.21}
p = frame_plan({"system": "Вектор-1", "contours":
                [{"outer": rect(0, 0, 5000, 6000)}],
                "joints_x": [i * 608.0 + 304 for i in range(8)],
                "floors_y": [3000], "calc": CALC_RESP})
ok(p["ok"], "FR-C1: ok")
ok(p["summary"]["calc_steps"] == {"main": 800, "corner": 450},
   "FR-C1: шаги по расчёту 800/450 — как выводы республиканской (%s)"
   % p["summary"].get("calc_steps"))
ok(any("ПО РАСЧЁТУ" in n for n in p["notes"]), "FR-C1: note о расчёте")
ok(p["calc_report"]["row"]["passed"] and
   p["calc_report"]["corner"]["passed"] and
   len(p["calc_report"]["row"]["checks"]) >= 7,
   "FR-C1: calc_report с цепочкой проверок")
mid = [b for b in p["brackets"] if 2000 < b["x"] < 3000]
ys = sorted(set(round(b["y"]) for b in mid))
dl = [ys[i + 1] - ys[i] for i in range(len(ys) - 1)]
ok(dl and max(dl) <= 800 + 1, "FR-C1: шаг кронштейнов в поле <=800")

# C2: расчёт не проходит (анкер 300 Н) → честный отказ
p = frame_plan({"system": "Вектор-1", "contours":
                [{"outer": rect(0, 0, 3000, 3000)}],
                "joints_x": [608, 1216],
                "calc": dict(CALC_RESP, na_max=300)})
ok(not p["ok"] and "не проходит" in p["error"],
   "FR-C2: анкер 300 Н — отказ с подсказкой (%s)" % p.get("error"))

# C3: неполные исходные → отказ с именем поля
p = frame_plan({"system": "Вектор-1", "contours":
                [{"outer": rect(0, 0, 3000, 3000)}],
                "joints_x": [608], "calc": {"terrain": "B"}})
ok(not p["ok"] and "height" in p["error"],
   "FR-C3: нет высоты — отказ (%s)" % p.get("error"))

# C4: РЕГРЕСС — без calc шаги из справочника (Вектор-1: 1200/800)
p = frame_plan({"system": "Вектор-1", "contours":
                [{"outer": rect(0, 0, 5000, 6000)}],
                "joints_x": [i * 608.0 + 304 for i in range(8)],
                "floors_y": [3000]})
ok(p["ok"] and "calc_report" not in p and
   "calc_steps" not in p["summary"],
   "FR-C4: без calc — прежнее поведение")

# C5: межэтажная Нижнекаменская-кейс (61.2 м, кассеты 8) →
# гориз. шаг 350 (ограничитель — удлинитель УК-85, как их верхний
# диапазон 45..65 м)
p = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                "contours": [{"outer": rect(0, 0, 5000, 9000)}],
                "joints_x": [i * 800.0 + 400 for i in range(6)],
                "floor_step": 2930,
                "calc": {"wind_region": "II", "terrain": "B",
                         "height": 61.2, "q_clad": 8,
                         "gamma_clad": 1.05, "offset": 280,
                         "na_max": 3960, "b_corner": 450}})
ok(p["ok"], "FR-C5: ok (%s)" % p.get("error"))
ok(p["summary"]["calc_steps"]["main"] == 350,
   "FR-C5: межэтажная 61.2 м — гориз. шаг ПО РАСЧЁТУ 350 (как "
   "Нижнекаменская 45..65 м) (%s)" % p["summary"].get("calc_steps"))
xs = sorted(b["x"] for b in p["brackets"]
            if abs(b["y"] - 2930) < 1 and 1500 < b["x"] < 3500)
dxs = [xs[i + 1] - xs[i] for i in range(len(xs) - 1)]
ok(dxs and max(dxs) <= 350 + 1,
   "FR-C5: кронштейны по перекрытию с шагом <=350")

# C6: ортогональная Новгород-кейс (район I, анкер 1280, вынос 260)
p = frame_plan({"system": "Ортогональная", "sub_type": "ortho",
                "contours": [{"outer": rect(0, 0, 3000, 2400)}],
                "joints_x": [608, 1216, 1824, 2432],
                "calc": {"wind_region": "I", "terrain": "B",
                         "height": 10, "q_clad": 25, "offset": 260,
                         "na_max": 1280, "e3": 12, "e4": 25,
                         "v_step": 400, "max_step": 600}})
ok(p["ok"], "FR-C6: ok (%s)" % p.get("error"))
ok(p["summary"]["calc_steps"] == {"main": 600, "corner": 600},
   "FR-C6: ортогональная — 600/600 (конструктивный max 600, как "
   "выводы Новгород-260) (%s)" % p["summary"].get("calc_steps"))


# ── FR-G (фидбэк Германа 27.07): угловые зоны от УКАЗАННЫХ углов ──
# G1: угол только слева (corners_x=[0]) — справа шаг рядовой
p = frame_plan({"system": "Вектор-1", "contours":
                [{"outer": rect(0, 0, 6000, 3000)}],
                "joints_x": [304 + i * 608.0 for i in range(10)],
                "floors_y": [], "corners_x": [0.0]})
ok(p["ok"], "FR-G1: ok")
def _steps_at(p, x):
    ys = sorted(b["y"] for b in p["brackets"] if abs(b["x"] - x) < 1)
    return [ys[i + 1] - ys[i] for i in range(len(ys) - 1)]
left = _steps_at(p, 304.0)          # в угловой полосе 1500
right = _steps_at(p, 304 + 9 * 608.0)   # далеко от угла
ok(left and max(left) <= 800 + 1,
   "FR-G1: слева (угол) шаг <=800 (%s)" % max(left or [0]))
ok(right and max(right) > 800 + 1,
   "FR-G1: справа шаг рядовой 1200 (%s)" % max(right or [0]))

# G2: corners_x=[] — угловых зон нет вовсе
p = frame_plan({"system": "Вектор-1", "contours":
                [{"outer": rect(0, 0, 6000, 3000)}],
                "joints_x": [304 + i * 608.0 for i in range(10)],
                "floors_y": [], "corners_x": []})
ok(p["ok"] and max(_steps_at(p, 304.0)) > 800 + 1,
   "FR-G2: corners_x=[] — весь фасад рядовой")

# G3: межэтажная + расчёт БЕЗ b_corner — авто 450 + note
p = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                "contours": [{"outer": rect(0, 0, 5000, 9000)}],
                "joints_x": [i * 800.0 + 400 for i in range(6)],
                "floor_step": 2930,
                "calc": {"wind_region": "II", "terrain": "B",
                         "height": 61.2, "q_clad": 8,
                         "gamma_clad": 1.05, "offset": 280,
                         "na_max": 3960}})
ok(p["ok"], "FR-G3: ok (%s)" % p.get("error"))
ok(p["calc_report"]["inputs"]["b_corner"] == 450.0 and
   any("принят 450" in n for n in p["notes"]),
   "FR-G3: b_corner авто 450 + note (%s)" %
   p["calc_report"]["inputs"].get("b_corner"))

# G4: межэтажная + углы + оси чаще в углу — b_corner из осей
jx = [200, 650, 1100, 1550] + [2400 + i * 800.0 for i in range(4)]
p = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                "contours": [{"outer": rect(0, 0, 6000, 9000)}],
                "joints_x": jx, "floor_step": 2930,
                "corners_x": [0.0],
                "calc": {"wind_region": "II", "terrain": "B",
                         "height": 61.2, "q_clad": 8,
                         "gamma_clad": 1.05, "offset": 280,
                         "na_max": 3960}})
ok(p["ok"], "FR-G4: ok (%s)" % p.get("error"))
ok(abs(p["calc_report"]["inputs"]["b_corner"] - 450.0) < 1 and
   any("из осей раскладки" in n for n in p["notes"]),
   "FR-G4: b_corner из осей в угловой полосе = 450 (%s)" %
   p["calc_report"]["inputs"].get("b_corner"))

# ── FR-H (фидбэк Германа 30.07, ответы по сборке №10) ──
# H1/H2: ЗАДВОЕНИЕ КЛЯММЕРОВ. Причина найдена прогоном: верх откоса
# проёма лежит чуть НИЖЕ ближайшего шва раскладки → стартовый садится
# на кромку, рядовой на шов в нескольких см выше («и стартовый стоит,
# и рядовой»). Слияние ближе CLAMP_MERGE, приоритет у стартового.
_rows = [605.0 * i for i in range(0, 12)]


def _win_case(top, sub="interfloor", sysname="Межэтажная"):
    return frame_plan({"system": sysname, "sub_type": sub,
                       "contours": [{"outer": rect(0, 0, 3000, 6000),
                                     "holes": [rect(1000, 2000, 2000, top)]}],
                       "joints_x": [500, 1500, 2500],
                       "floors_y": [3000], "rows_y": _rows})


def _tight_pairs(p, tol=100.0):
    by = {}
    for c in p["clamps"]:
        by.setdefault(round(c["x"], 1), []).append(c["y"])
    n = 0
    for ys in by.values():
        ys.sort()
        n += sum(1 for a, b in zip(ys, ys[1:]) if b - a < tol - 1e-9)
    return n


for _sub, _sys in (("interfloor", "Межэтажная"), ("vertical", "Вектор-1"),
                   ("ortho", "Вектор-1")):
    for _top in (3500.0, 3600.0, 3630.0):
        _p = _win_case(_top, _sub, _sys)
        ok(_tight_pairs(_p) == 0,
           "FR-H1: %s верх окна %.0f — задвоенные кляммеры (%d пар)" %
           (_sub, _top, _tight_pairs(_p)))

# H2: при слиянии выживает СТАРТОВЫЙ (кромка откоса), рядовой на шве
# в 30 мм выше исчезает; общее число падает ровно на слитые
p_a = _win_case(3630.0)      # кромка совпала со швом — эталон
p_b = _win_case(3600.0)      # кромка на 30 мм ниже шва
c15b = [c for c in p_b["clamps"] if near(c["x"], 1500)]
ok(any(near(c["y"], 3600.0) and c["kind"] == "стартовый" for c in c15b),
   "FR-H2: стартовый остался на кромке откоса 3600")
ok(not any(near(c["y"], 3630.0) for c in c15b),
   "FR-H2: рядовой на шве 3630 слит со стартовым")
ok(len(p_b["clamps"]) == len(p_a["clamps"]),
   "FR-H2: после слияния столько же, сколько при совпавшей кромке "
   "(%d vs %d)" % (len(p_b["clamps"]), len(p_a["clamps"])))

# H3: БОКОВОЙ только в пределах высоты окна (п.4). Вертикальная:
# смещённая стойка грань−100, окно по высоте 2000..3500
p_v = frame_plan({"system": "Вектор-1",
                  "contours": [{"outer": rect(0, 0, 3000, 6000),
                                "holes": [rect(1000, 2000, 2000, 3500)]}],
                  "joints_x": [950, 1500, 2050],
                  "floors_y": [3000], "rows_y": _rows})
side_cl = [c for c in p_v["clamps"] if c["kind"] == "боковой"]
# диапазон = высота окна + выступ 50/50 (низ смещённой стойки — отлив,
# там боковой по ТЗ 26.07; п.4 30.07 ограничивает сверху и снизу окном)
ok(side_cl and all(1950.0 - 1e-6 <= c["y"] <= 3550.0 + 1e-6
                   for c in side_cl),
   "FR-H3: боковые кляммеры только в высоту окна+выступ (%s)" %
   sorted({round(c["y"]) for c in side_cl}))
ok(not any(c["kind"] == "боковой" and c["y"] > 3550.0
           for c in p_v["clamps"]),
   "FR-H3: выше окна боковых нет — обычные рядовые")
ok(all(any(near(c["x"], e) for e in (900.0, 2100.0)) for c in side_cl),
   "FR-H3: боковые — на смещённых оконных стойках грань∓100")

# H4 (п.11): ОРТОГОНАЛЬНАЯ — вертикальный ШП режется по стандартным
# 3000, стык ПОСЕРЕДИНЕ между кронштейнами сетки (300 + 600k), свес
# крайнего кронштейна ≤300. Раньше клали одним куском на всю высоту —
# динблок столько не растягивался («профилей выше первого этажа нет»).
p_o = frame_plan({"system": "Вектор-1", "sub_type": "ortho",
                  "contours": [{"outer": rect(0, 0, 3000, 15000)}],
                  "joints_x": [500, 1500, 2500],
                  "rows_y": [605.0 * i for i in range(25)]})
shp = sorted((r for r in p_o["rails"] if near(r["x"], 1500)),
             key=lambda r: r["y0"])
ok(len(shp) == 5 and all(r["len"] <= 3000.0 + 1e-6 for r in shp),
   "FR-H4: ШП режется по 3000 (%d кусков, max %.0f)" %
   (len(shp), max(r["len"] for r in shp)))
_joints = [round((shp[i]["y1"] + shp[i + 1]["y0"]) / 2.0)
           for i in range(len(shp) - 1)]
ok(_joints == [3000, 6000, 9000, 12000],
   "FR-H4: стыки посередине между кронштейнами 2700/3300 (%s)" % _joints)
_br = sorted(b["y"] for b in p_o["brackets"] if near(b["x"], 1500))
ok(_br and near(_br[0], 300.0) and
   all(near(b - a, 600.0) for a, b in zip(_br, _br[1:])),
   "FR-H4: сетка кронштейнов 300 + 600k не тронута (%s…)" % _br[:4])

# H5 (п.3): короткая направляющая (под окном / над откосом) НЕ режется
# отметкой перекрытия — «терморазрыв здесь не нужен, она меньше 3 м»
p_s = frame_plan({"system": "Вектор-1",
                  "contours": [{"outer": rect(0, 0, 3000, 6000),
                                "holes": [rect(1000, 2500, 2000, 4000)]}],
                  "joints_x": [500, 1500, 2500],
                  "floors_y": [1500, 3000],
                  "rows_y": [605.0 * i for i in range(11)]})
low = [r for r in p_s["rails"]
       if near(r["x"], 1500) and r["y1"] <= 2500.0 + 1e-6]
ok(len(low) == 1 and near(low[0]["y0"], 0.0) and near(low[0]["y1"], 2500.0),
   "FR-H5: подоконный кусок 2500 мм — ОДНА направляющая без стыка "
   "на отметке 1500 (%s)" % [(r["y0"], r["y1"]) for r in low])
tall = [r for r in p_s["rails"] if near(r["x"], 500)]
ok(len(tall) > 1,
   "FR-H5: сплошная стойка 6000 по-прежнему режется отметками (%d)" %
   len(tall))

# H6 (п.9): ОРТОГОНАЛЬНАЯ — доп. кронштейны у граней проёма, если
# сетка отстоит больше 300; сбоку в пределах ВЫСОТЫ окна, сверху — в
# пределах его ШИРИНЫ, все на 100 мм от грани.
p_w = frame_plan({"system": "Вектор-1", "sub_type": "ortho",
                  "contours": [{"outer": rect(0, 0, 6000, 6000),
                                "holes": [rect(3000, 1000, 4000, 2950)]}],
                  "joints_x": [600.0 * i for i in range(11)],
                  "rows_y": [600.0 * i for i in range(11)]})
_side = sorted(b["y"] for b in p_w["brackets"] if near(b["x"], 2900.0))
ok(_side == [1500.0, 2100.0, 2700.0],
   "FR-H6: доп. кронштейны слева на 100 от грани, в высоту окна (%s)" %
   _side)
_top = sorted(b["x"] for b in p_w["brackets"] if near(b["y"], 3050.0))
ok(_top == [3500.0],
   "FR-H6: доп. кронштейн над откосом на 100, в ширину окна (%s)" %
   _top)
_rside = sorted(b["y"] for b in p_w["brackets"] if near(b["x"], 4100.0))
ok(_rside == [1500.0, 2100.0, 2700.0],
   "FR-H6: доп. кронштейны справа на 100 от грани, в высоту окна (%s)" %
   _rside)
ok(any("доп. кронштейнов у проёмов: 7" in n for n in p_w["notes"]),
   "FR-H6: в сводке 7 добавленных = 3 слева + 3 справа + 1 сверху (%s)"
   % p_w["notes"])
ok(not any(b["y"] > 2950.0 and near(b["x"], 2900.0)
           for b in p_w["brackets"]),
   "FR-H6: выше окна доп. боковых нет — только в его высоту")

# H7 (п.7): СП-60-40 крепится к БЛИЖАЙШИМ осям — ось, совпавшая с
# гранью проёма, раньше отбрасывалась и СП тянулся до следующей
p_sp = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                   "contours": [{"outer": rect(0, 0, 6000, 6100),
                                 "holes": [rect(1800, 2000, 4200, 3500)]}],
                   "joints_x": [600, 1800, 3000, 4200, 5400],
                   "floors_y": [3000]})
_sp = [h for h in p_sp["hrails"] if h["kind"] == "СП-60-40"]
ok(len(_sp) == 1 and near(_sp[0]["x0"], 1800.0) and
   near(_sp[0]["x1"], 4200.0),
   "FR-H7: СП между осями НА гранях окна, не до следующих (%s)" %
   [(h["x0"], h["x1"]) for h in _sp])

# ── FR-P (письмо Германа 01.08, п.2): марка профиля в rails[] для
#    состояния ВИДИМОСТИ динблока; В-ш закрыт списками допустимых ──

# P1: межэтажная — НСП по умолчанию НСП-1, ШП-куски и hrails с марками
p_p = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                  "contours": [{"outer": rect(0, 0, 1220, 6000)}],
                  "joints_x": [610], "floors_y": [3000]})
ok(all(r["profile"] == "НСП-1" for r in p_p["rails"]
       if r["kind"] == "НСП") and
   all(h["profile"] == h["kind"] for h in p_p["hrails"]),
   "FR-P1: межэтажная — НСП-1 по умолчанию, hrails несут kind "
   "(%s)" % sorted({r["profile"] for r in p_p["rails"]}))

# P2: nsp_type=НСП-2 подхватывается
p_p2 = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                   "nsp_type": "НСП-2",
                   "contours": [{"outer": rect(0, 0, 1220, 6000)}],
                   "joints_x": [610], "floors_y": [3000]})
ok(all(r["profile"] == "НСП-2" for r in p_p2["rails"]
       if r["kind"] == "НСП"),
   "FR-P2: nsp_type=НСП-2 → марка НСП-2 у межэтажных стоек")

# P3: ортогональная — ШП-60-20 и ZП-40-20 (написание Германа)
p_p3 = frame_plan({"system": "Ортогональная", "sub_type": "ortho",
                   "contours": [{"outer": rect(0, 0, 1800, 3600),
                                 "holes": [rect(500, 1200, 1300,
                                                2400)]}],
                   "joints_x": [300, 900, 1500], "floors_y": [3000]})
_zk = {r["kind"]: r["profile"] for r in p_p3["rails"]}
ok(_zk.get("ШП-60-20") == "ШП-60-20" and
   _zk.get("Z-профиль") == "ZП-40-20",
   "FR-P3: орто — ШП-60-20/ZП-40-20 (%s)" % _zk)

# P4: вертикальная без расчёта — профиль из пресета системы
# (Вектор-1: calc.profile = ГП-40-40-1,2; у Standart пресета нет —
# там остаётся нейтральная «направляющая»)
p_p4 = frame_plan({"system": "Вектор-1", "sub_type": "vertical",
                   "contours": [{"outer": rect(0, 0, 1200, 3000)}],
                   "joints_x": [600], "floors_y": [3000]})
ok(all(r["profile"] == "ГП-40-40-1,2" for r in p_p4["rails"]),
   "FR-P4: вертикальная без калка — марка из пресета системы (%s)"
   % sorted({r["profile"] for r in p_p4["rails"]}))

# P5: явный выбор ГП-60-40 — видимость ГП-60-40, расчёт по ГП-40-40
# (в запас) + note; кандидаты подбора сужены
_CALC = dict(wind_region="IV", terrain="B", height=30.0, q_clad=50.0,
             offset=150.0, na_max=6000.0)
p_p5 = frame_plan({"system": "Вектор-1", "sub_type": "vertical",
                   "rail_profile": "ГП-60-40", "calc": dict(_CALC),
                   "contours": [{"outer": rect(0, 0, 1200, 3000)}],
                   "joints_x": [600], "floors_y": [3000]})
ok(all(r["profile"] == "ГП-60-40" for r in p_p5["rails"]) and
   any("ГП-60-40" in n for n in p_p5["notes"]) and
   p_p5["calc_report"]["profile"]["row"] == "ГП-40-40-1,2",
   "FR-P5: ГП-60-40 — видимость своя, расчёт по ГП-40-40 в запас "
   "(%s)" % p_p5["calc_report"]["profile"])

# P6: авто-подбор вертикальной гуляет ТОЛЬКО по допустимым (В-ш)
p_p6 = frame_plan({"system": "Вектор-1", "sub_type": "vertical",
                   "calc": dict(_CALC),
                   "contours": [{"outer": rect(0, 0, 1200, 3000)}],
                   "joints_x": [600], "floors_y": [3000]})
_vars = {v["profile"] for v in
         p_p6["calc_report"]["variants"]["row"]} \
    if isinstance(p_p6["calc_report"].get("variants"), dict) \
    else {v["profile"] for v in p_p6["calc_report"]["variants"]}
ok(_vars <= {"ГП-40-40-1,2", "ШП-60-20-1,2", "ШП-60-20-20-1,2"},
   "FR-P6: кандидаты вертикальной ограничены В-ш (%s)" % _vars)
ok(all(r["profile"] == p_p6["calc_report"]["profile"]["row"]
       for r in p_p6["rails"]),
   "FR-P6b: марка направляющих = подобранному профилю")

# ── FR-A (краш-аудит 03.08): стойки не идут сквозь соседние окна;
#    верхние доп. кронштейны орто держат перемычку ГП ──
p_a1 = frame_plan({"system": "Вектор-1", "sub_type": "vertical",
                   "contours": [{"outer": rect(0, 0, 4000, 4000),
                                 "holes": [rect(300, 400, 1250, 1400),
                                           rect(1300, 300, 2500,
                                                1500)]}],
                   "joints_x": [600, 1800, 3000], "floors_y": [3000]})
_bad = [r for r in p_a1["rails"]
        for bx0, by0, bx1, by1 in ((300, 400, 1250, 1400),
                                   (1300, 300, 2500, 1500))
        if bx0 + 1 < r["x"] < bx1 - 1 and
        r["y0"] < by1 - 1 and r["y1"] > by0 + 1]
ok(not _bad,
   "FR-A1: стойка одного окна не идёт сквозь соседнее (%s)" % _bad)

p_a2 = frame_plan({"system": "Вектор-1", "sub_type": "ortho",
                   "contours": [{"outer": rect(0, 0, 6000, 6000),
                                 "holes": [rect(3000, 1000, 4000,
                                                2950)]}],
                   "joints_x": [600.0 * i for i in range(11)],
                   "rows_y": [600.0 * i for i in range(11)]})
_per = [h for h in p_a2["hrails"] if near(h["y"], 3050.0)]
ok(len(_per) == 1 and near(_per[0]["x0"], 2900.0) and
   near(_per[0]["x1"], 4100.0),
   "FR-A2: перемычка ГП над окном 2900..4100 на отметке 3050 (%s)"
   % _per)
_tb = [b for b in p_a2["brackets"] if near(b["y"], 3050.0)]
ok(len(_tb) > 0 and all(2899.0 <= b["x"] <= 4101.0 for b in _tb),
   "FR-A2b: верхние доп. кронштейны лежат на перемычке (%s)"
   % sorted(round(b["x"]) for b in _tb))

# A3: ось руста ровно в edge_off (100) от грани окна → гарантированная
# стойка не дублирует её (наложения кусков на оси)
p_a3 = frame_plan({"system": "Вектор-1", "sub_type": "vertical",
                   "contours": [{"outer": rect(0, 0, 5400, 6600),
                                 "holes": [rect(1400, 1200, 2600,
                                                2400)]}],
                   "joints_x": [300 + 600 * i for i in range(9)],
                   "floors_y": [3300, 6600]})
import collections as _cc
_byx = _cc.defaultdict(list)
for r in p_a3["rails"]:
    _byx[round(r["x"], 1)].append((r["y0"], r["y1"]))
_ov = 0
for _xx, _lst in _byx.items():
    _lst.sort()
    for _q1, _q2 in zip(_lst, _lst[1:]):
        if _q2[0] < _q1[1] - 0.5:
            _ov += 1
ok(_ov == 0,
   "FR-A3: наложений кусков нет при русте ровно в 100 от грани (%d)"
   % _ov)

# A4: ПЕРЕСЕКАЮЩИЕСЯ окна — стойки не идут сквозь проёмы и без дублей
p_a4 = frame_plan({"system": "Вектор-1", "sub_type": "vertical",
                   "contours": [{"outer": rect(0, 0, 4800, 5400),
                                 "holes": [rect(0, 650, 2000, 2850),
                                           rect(600, 1900, 2800,
                                                3200)]}],
                   "joints_x": [300 + 600 * i for i in range(8)],
                   "floors_y": [3000]})
_bad4 = [r for r in p_a4["rails"]
         for bb in ((0, 650, 2000, 2850), (600, 1900, 2800, 3200))
         if bb[0] + 1 < r["x"] < bb[2] - 1 and
         r["y0"] < bb[3] - 1 and r["y1"] > bb[1] + 1]
ok(not _bad4,
   "FR-A4: пересекающиеся окна — стойки не в проёмах (%s)" % _bad4)


# A5/A6 (04.08, D-сверка полигона): ось руста стоит РОВНО в edge_offset
# от грани проёма — она же цель смещения соседней оси, стоящей ближе
# грани. Раньше собственный кусок и смещённый ложились на одну X: два
# профиля в одном месте и дубли кронштейнов (на полигоне 64 шт).
# Проверяем ОБЕ стороны окна: порядком обхода осей это не лечится.
def _dubl(pp):
    import collections as _cc
    cb = _cc.Counter((round(b["x"], 1), round(b["y"], 1))
                     for b in pp["brackets"])
    cr = _cc.Counter((round(r["x"], 1), round(r["y0"], 1),
                      round(r["y1"], 1)) for r in pp["rails"])
    ov = 0
    byx = {}
    for r in pp["rails"]:
        byx.setdefault(round(r["x"], 1), []).append((r["y0"], r["y1"]))
    for lst in byx.values():
        lst.sort()
        ov += sum(1 for a, b in zip(lst, lst[1:]) if b[0] < a[1] - 1)
    return (sum(1 for v in cb.values() if v > 1),
            sum(1 for v in cr.values() if v > 1), ov)


for _tag, _jx in (("слева", [900.0, 996.0]),
                  ("справа", [2804.0, 2900.0]),
                  ("с обеих", [900.0, 996.0, 2804.0, 2900.0])):
    _pa5 = frame_plan({"system": "Standart", "sub_type": "vertical",
                       "contours": [{"outer": rect(0, 0, 4000, 3000),
                                     "holes": [rect(1000, 500,
                                                    2800, 2700)]}],
                       "joints_x": list(_jx), "floors_y": []})
    _db, _dr, _ov = _dubl(_pa5)
    ok(_db == 0 and _dr == 0 and _ov == 0,
       "FR-A5 (%s): руст ровно в 100 от грани — ни дублей, ни "
       "наложений (кронш=%d, кусков=%d, наложений=%d)"
       % (_tag, _db, _dr, _ov))

# A6: смещённый кусок не съедает собственную ось целиком — профиль на
# высоте окна остаётся ровно один и покрывает окно + выступ 50/50
_pa6 = frame_plan({"system": "Standart", "sub_type": "vertical",
                   "contours": [{"outer": rect(0, 0, 4000, 3000),
                                 "holes": [rect(1000, 500, 2800,
                                                2700)]}],
                   "joints_x": [900.0, 996.0], "floors_y": []})
_at900 = [r for r in _pa6["rails"] if abs(r["x"] - 900.0) < 1]
_cover = [r for r in _at900 if r["y0"] <= 450 + 1 and r["y1"] >= 2750 - 1]
ok(len(_cover) == 1,
   "FR-A6: на оси 900 ровно один профиль перекрывает высоту окна (%d)"
   % len(_cover))

print("frame_plan: %d проверок OK" % _n)
