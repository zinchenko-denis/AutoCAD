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

print("frame_plan: %d проверок OK" % _n)
