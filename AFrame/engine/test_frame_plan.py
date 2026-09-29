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

# ── FR8 (переписан 07.08a, ответ Германа В-ад): floors пуст —
#    ХЛЫСТЫ rail_std от низа с зазором МЕЖДУ (полигон: низы
#    24070.3+3010n), последний обрезается; кронштейны все одного
#    типа (несущих нет), floor_step вертикальной игнорируется ──
p = frame_plan({"system": "Standart",
                "contours": [{"outer": rect(0, 0, 1220, 6100)}],
                "joints_x": [610], "floor_step": 3000})
r = sorted((x["y0"], x["y1"]) for x in p["rails"])
ok(r == [(0.0, 3000.0), (3010.0, 6010.0), (6020.0, 6100.0)],
   "FR8: хлысты 3000 от низа, зазор 10 между, обрезок (%s)" % r)
ok(p["summary"]["brackets_main"] == 0 and
   p["summary"]["brackets_row"] > 0 and
   not any("автоматически" in n for n in p["notes"]),
   "FR8b: без отметок несущих нет — все кронштейны одного типа")
bys = sorted(b["y"] for b in p["brackets"])
ok(bys[0] == 300.0 and 2700.0 in bys and 3310.0 in bys,
   "FR8c: кронштейны 300 от торцов хлыстов (через стык 610=300+10+"
   "300, как на полигоне) (%s)" % bys[:6])

# ── FR9: направляющая длиннее хлыста (floors заданы, пролёт между
#    отметками больше stock) — предупреждение ──
p = frame_plan({"system": "Standart",
                "contours": [{"outer": rect(0, 0, 1220, 13000)}],
                "joints_x": [610], "floors_y": [6500]})
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
side_v = [c for c in p_v["clamps"]
          if c["kind"] == "боковой" and c.get("orient") == "v"]
side_h = [c for c in p_v["clamps"]
          if c["kind"] == "боковой" and c.get("orient") == "h"]
# ВЕРТИКАЛЬНЫЕ боковые — только в высоту окна+выступ 50/50, на
# смещённых оконных стойках (п.4 30.07); ГОРИЗОНТАЛЬНЫЕ (07.08,
# ответ 1 Германа) — под отливом (верх куска в створе = низ окна)
# и последним рядом по высоте (верх зоны)
ok(side_v and all(1950.0 - 1e-6 <= c["y"] <= 3550.0 + 1e-6
                  for c in side_v),
   "FR-H3: вертикальные боковые только в высоту окна+выступ (%s)" %
   sorted({round(c["y"]) for c in side_v}))
ok(all(any(near(c["x"], e) for e in (900.0, 2100.0)) for c in side_v),
   "FR-H3: вертикальные боковые — на смещённых стойках грань∓100")
ok(any(near(c["y"], 2000.0) and near(c["x"], 1500.0)
       for c in side_h),
   "FR-H3b: боковой ПОД ОТЛИВОМ на створной оси (верх куска = низ "
   "окна), горизонтальный")
ok(any(near(c["y"], 6000.0) for c in side_h),
   "FR-H3b: боковой ПОСЛЕДНИМ РЯДОМ по высоте (верх зоны)")
ok(all(near(c["y"], 2000.0) or near(c["y"], 6000.0)
       for c in side_h),
   "FR-H3b: горизонтальные боковые только отлив/верх (%s)" %
   sorted({round(c["y"]) for c in side_h}))

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

# ── A7 (04.08, Герман п.1): ручной шаг ставится БУКВАЛЬНО ──
# «задал 800 — программа ставит 798»: В16 размазывал остаток по всем
# пролётам, теперь при exact_step короче только последний пролёт.
_pa7 = frame_plan({"system": "Standart", "sub_type": "vertical",
                   "exact_step": True,
                   "contours": [{"outer": rect(0, 0, 610, 9000)}],
                   "joints_x": [305], "floors_y": [3000, 6000]})
_ys = sorted(round(b["y"], 3) for b in _pa7["brackets"])
_d = [round(b - a, 3) for a, b in zip(_ys, _ys[1:])]
# шаги внутри куска: 800 у всех, кроме доборного и межкускового
_full = [v for v in _d if abs(v - 800.0) < 0.01]
ok(len(_full) >= 4 and all(v <= 800.0 + 0.01 for v in _d),
   "A7: ручной шаг 800 ставится ровно (шаги %s)" % _d[:6])
_pa7b = frame_plan({"system": "Standart", "sub_type": "vertical",
                    "contours": [{"outer": rect(0, 0, 610, 9000)}],
                    "joints_x": [305], "floors_y": [3000, 6000]})
_ysb = sorted(round(b["y"], 3) for b in _pa7b["brackets"])
_db = [round(b - a, 3) for a, b in zip(_ysb, _ysb[1:])]
ok(not any(abs(v - 800.0) < 0.01 for v in _db),
   "A7b: без exact_step прежнее поведение В16 (шаги %s)" % _db[:4])

# ── A8 (04.08, Герман п.4): ортогональная ставит промежуточный
#    профиль посередине пролёта шире 650, как вертикальная ──
_A8 = {"contours": [{"outer": rect(0, 0, 2440, 3000)}],
       "joints_x": [0, 1220, 2440], "floors_y": []}
_o = frame_plan(dict(_A8, system="Ортогональная", sub_type="ortho"))
_v = frame_plan(dict(_A8, system="Вектор-1", sub_type="vertical"))
_ox = sorted(set(round(r["x"], 1) for r in _o["rails"]))
_vx = sorted(set(round(r["x"], 1) for r in _v["rails"]))
ok(_ox == _vx and 610.0 in _ox and 1830.0 in _ox,
   "A8: ортогональная — средний профиль в пролёте 1220 (%s)" % _ox)

# ── A9/A10 (04.08, Герман п.2): стойки угловых и краевых зон ──
# «программа не дорисовывает профиль и кронштейны в угловых и краевых
# зонах»: стойки шли только по осям рустов, у внешнего угла и у конца
# облицовки рустов нет — там было пусто.
_A9 = {"system": "Вектор-1", "sub_type": "vertical",
       "contours": [{"outer": rect(0, 0, 6000, 3000)}],
       "joints_x": [2000, 3000, 4000], "floors_y": []}
_p9 = frame_plan(dict(_A9))
_x9 = sorted(set(round(r["x"], 1) for r in _p9["rails"]))
ok(100.0 not in _x9 and 5900.0 not in _x9,
   "A9: без указанных углов поведение прежнее (%s)" % _x9)

_p9c = frame_plan(dict(_A9, corners_x=[0.0], rail_step_corner=600.0))
_x9c = sorted(set(round(r["x"], 1) for r in _p9c["rails"]))
ok(100.0 in _x9c and 700.0 in _x9c and 1300.0 in _x9c,
   "A9b: угловая зона от угла — 100, далее шагом 600 (%s)" % _x9c[:5])
ok(all(v <= 1500.0 + 100.0 + 1 for v in (100.0, 700.0, 1300.0)) and
   1900.0 not in _x9c,
   "A9c: стойки угловой зоны не выходят за её ширину 1500 (%s)" % _x9c)

# краевая зона: конец облицовки без указанного угла — последняя
# стойка в 100 мм от границы области
ok(5900.0 in _x9c, "A10: краевая зона — стойка в 100 от границы (%s)"
   % _x9c[-3:])
_p10 = frame_plan(dict(_A9, corners_x=[]))
_x10 = sorted(set(round(r["x"], 1) for r in _p10["rails"]))
ok(100.0 in _x10 and 5900.0 in _x10,
   "A10b: углов нет — обе границы краевые (%s)" % _x10)

# на добавленных осях есть и кронштейны (жалоба была про «профиль И
# кронштейны»), а в угловой зоне шаг — угловой
_br = [b for b in _p9c["brackets"] if abs(b["x"] - 100.0) < 1]
ok(len(_br) > 0, "A10c: на угловой стойке есть кронштейны (%d)"
   % len(_br))

# ── K-серия (07.08a, ответы Германа): типы кляммеров по положению
#    оси (В-аа), угловая зона внутрь (В-аз) ──
# Плита шире 650 → mid-ось в поле плиты: кляммеры на ней БОКОВЫЕ
# вертикальные («если плитка больше 600 — боковой по середине»);
# на рустах — рядовые; числа руками: русты 0/1130, mid 565,
# ряды на 600+608k, хлысты 3000+10 → стык на 3000, первый шов
# выше — 3032: на русте комбинированный, на mid ПУСТО (полигон)
_rowsK = [600.0 + 608.0 * k for k in range(9)]      # 600..5464
_pK = frame_plan({"system": "Вектор-1",
                  "contours": [{"outer": rect(0, 0, 1130, 5600)}],
                  "joints_x": [0, 1130], "rows_y": _rowsK})
_mid = [c for c in _pK["clamps"] if abs(c["x"] - 565.0) < 1]
_seam = [c for c in _pK["clamps"] if abs(c["x"]) < 1]
ok(_mid and all(c["kind"] in ("боковой", "стартовый")
                for c in _mid),
   "K1: на mid-оси плиты 1130 кляммеры боковые (кроме стартового "
   "низа) (%s)" % sorted({c["kind"] for c in _mid}))
ok(all(c.get("orient") == "v" for c in _mid
       if c["kind"] == "боковой" and c["y"] < 5600.0 - 1),
   "K1b: боковые середины плиты — вертикальные (кроме верхней "
   "кромки)")
ok(any(c["kind"] == "рядовой" for c in _seam),
   "K2: на оси-русте — рядовые (плиты с обеих сторон)")
_seamY = {round(c["y"]): c["kind"] for c in _seam}
ok(_seamY.get(3032) == "комбинированный",
   "K3: первый шов над стыком хлыста (3000) на русте — "
   "комбинированный (%s)" % _seamY.get(3032))
_midY = {round(c["y"]) for c in _mid}
ok(3032 not in _midY,
   "K3b: на mid-оси ряд над стыком ПУСТ (пропуски 1216 полигона) "
   "(%s)" % sorted(_midY)[:8])
ok({round(c["y"]) for c in _pK["clamps"]
    if c.get("orient") == "h" and c["kind"] == "боковой"} == {5600},
   "K4: горизонтальный боковой — последний ряд по высоте (верх "
   "зоны)")

# все кронштейны без floors — одного типа (В-ад: «везде монолит»)
ok(_pK["summary"]["brackets_main"] == 0,
   "K5: хлыстовый режим — несущих нет, все кронштейны одного типа")

# угловая зона ВНУТРЬ (В-аз): угол на левом краю — стойки только
# вправо от угла; интервал зоны [угол, угол+1500]
_pK2 = frame_plan({"system": "Вектор-1",
                   "contours": [{"outer": rect(0, 0, 6000, 3000)}],
                   "joints_x": [2000, 3000, 4000],
                   "corners_x": [0.0], "rail_step_corner": 600.0})
_xK2 = sorted(set(round(r["x"], 1) for r in _pK2["rails"]))
ok(100.0 in _xK2 and 700.0 in _xK2,
   "K6: угол слева — стойки зоны вправо от угла (%s)" % _xK2[:4])
_pK3 = frame_plan({"system": "Вектор-1",
                   "contours": [{"outer": rect(0, 0, 6000, 3000)}],
                   "joints_x": [200, 3000, 5800],
                   "corners_x": [6000.0], "rail_step_corner": 600.0})
_xK3 = sorted(set(round(r["x"], 1) for r in _pK3["rails"]))
ok(5900.0 in _xK3 and 5300.0 in _xK3 and
   not any(v > 5900.0 + 1 for v in _xK3),
   "K6b: угол справа — стойки зоны влево (внутрь) (%s)" % _xK3[-4:])

# ── CL1–CL7 (23.09b, Герман): «что раскладывать» — подсистема и
#    кляммеры / только подсистема / только кляммеры по СУЩЕСТВУЮЩИМ
#    направляющим и текущей облицовке ──
from collections import Counter as _Cn
import subprocess as _sp, tempfile as _tf
_rq = {"system": "Standart", "sub_type": "vertical",
       "contours": [{"outer": rect(0, 0, 7200, 6000), "holes": [rect(2000, 900, 3400, 2400)]}],
       "joints_x": [305.0 + 610 * k for k in range(12)], "rows_y": [605.0 * k for k in range(1, 10)],
       "floors_y": [3000.0]}
_full = frame_plan(json.loads(json.dumps(_rq)))
_fr = frame_plan(dict(json.loads(json.dumps(_rq)), parts="frame"))
ok(_fr["ok"] and not _fr["clamps"] and _fr["rails"] == _full["rails"] and _fr["brackets"] == _full["brackets"],
   "CL1: только подсистема — направляющие и кронштейны те же, кляммеров нет")
def _ck(res):
    return _Cn((round(c["x"], 3), round(c["y"], 3), c["kind"], c.get("orient")) for c in res["clamps"])
_rf = [{"x": r["x"], "y0": r["y0"], "y1": r["y1"]} for r in _full["rails"]]
_cl = frame_plan(dict(json.loads(json.dumps(_rq)), parts="clamps", rails_fixed=_rf))
ok(_cl["ok"] and not _cl["rails"] and not _cl["brackets"] and _ck(_cl) == _ck(_full),
   "CL2: только кляммеры по направляющим полной расстановки = кляммеры полной (%d/%d)"
   % (len(_cl["clamps"]), len(_full["clamps"])))
for _st, _sn in (("interfloor", "Межэтажная"), ("ortho", "Ортогональная")):
    _q = dict(json.loads(json.dumps(_rq)), sub_type=_st, system=_sn, floor_step=3000.0)
    _f2 = frame_plan(json.loads(json.dumps(_q)))
    _c2 = frame_plan(dict(_q, parts="clamps", rails_fixed=[{"x": r["x"], "y0": r["y0"], "y1": r["y1"]}
                                                             for r in _f2["rails"]]))
    ok(_ck(_c2) == _ck(_f2), "CL3: %s — только кляммеры = полной (%d/%d)" % (_st, len(_c2["clamps"]), len(_f2["clamps"])))
# направляющие стоят НЕ по швам текущей облицовки (облицовку переложили) —
# кляммеры садятся на существующие оси, тип — по текущим швам
_moved = [dict(r, x=r["x"] + 150.0) for r in _rf if 3000 < r["x"] < 7000]
_cm = frame_plan(dict(json.loads(json.dumps(_rq)), parts="clamps", rails_fixed=_moved))
ok(_cm["ok"] and _cm["clamps"] and {round(c["x"], 3) for c in _cm["clamps"]} <= {round(r["x"], 3) for r in _moved},
   "CL4: кляммеры только на существующих направляющих")
ok(all(c["kind"] in ("боковой", "стартовый", "комбинированный") for c in _cm["clamps"]),
   "CL4b: ось вне шва текущей облицовки — рядовых нет (поле плиты)")
_e = frame_plan(dict(json.loads(json.dumps(_rq)), parts="clamps", rails_fixed=[]))
ok(_e["ok"] is False and "rails_fixed" in _e["error"], "CL5: пустой список направляющих — отказ текстом")
ok(frame_plan(dict(json.loads(json.dumps(_rq)), parts="всё"))["ok"] is False, "CL5b: неизвестный parts — отказ")
_v = frame_plan(dict(json.loads(json.dumps(_rq)), parts="clamps"))
ok(_v["ok"] and _v["clamps"] and not _v["rails"] and any("РАСЧЁТНЫМ" in n for n in _v["notes"]),
   "CL6: только кляммеры без направляющих — по расчётным осям, с нотой")
_d = _tf.mkdtemp()
_rq2 = {"op": "frame", "sub_type": "vertical", "system": "Standart",
        "contours": [{"id": "A", "pts": rect(0, 0, 7200, 6000)}, {"id": "W", "pts": rect(2000, 900, 3400, 2400)}],
        "joints_x": _rq["joints_x"], "rows_y": _rq["rows_y"], "floors_y": [3000.0],
        "parts": "clamps", "rails_fixed": _rf}
json.dump(_rq2, open(os.path.join(_d, "in.json"), "w", encoding="utf-8"))
_sp.call([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), "frame_engine.py"),
          os.path.join(_d, "in.json"), os.path.join(_d, "out.json")])
_o = json.load(open(os.path.join(_d, "out.json"), encoding="utf-8"))
ok(_o["ok"] and _o["summary"]["parts"] == "clamps" and _o["summary"]["rails"] == 0 and
   len(_o["clamps"]) == len(_full["clamps"]), "CL7: CLI frame_engine пробрасывает parts/rails_fixed")

# ── PG1–PG4 (23.09, ревью): стена НЕ прямоугольник — всё по контуру,
#    а не по габариту; оконная стойка у края зоны; СП мимо соседнего окна ──
def _pip(poly, x, y, tol=1.0):
    """Точка внутри/на границе полигона (свой луч, stdlib)."""
    n, ins = len(poly), False
    for i in range(n):
        (xa, ya), (xb, yb) = poly[i], poly[(i + 1) % n]
        # на ребре
        if min(xa, xb) - tol <= x <= max(xa, xb) + tol and min(ya, yb) - tol <= y <= max(ya, yb) + tol:
            dx, dy = xb - xa, yb - ya
            L = (dx * dx + dy * dy) ** 0.5
            if L > 0 and abs(dx * (y - ya) - dy * (x - xa)) / L <= tol:
                return True
        if (ya > y) != (yb > y) and x < xa + (y - ya) * (xb - xa) / (yb - ya):
            ins = not ins
    return ins
_L = [[0, 0], [9000, 0], [9000, 6000], [6000, 6000], [6000, 3000], [0, 3000]]
for _st, _sn, _fl in (("vertical", "Standart", []), ("interfloor", "Межэтажная", [3000.0]),
                      ("ortho", "Ортогональная", [])):
    _p = frame_plan({"system": _sn, "sub_type": _st, "contours": [{"outer": _L}],
                     "joints_x": [305.0 + 610 * k for k in range(15)],
                     "rows_y": [605.0 * k for k in range(1, 10)], "floors_y": _fl})
    _out = [r for r in _p["rails"] if not (_pip(_L, r["x"], r["y0"]) and _pip(_L, r["x"], r["y1"]))]
    _outb = [b for b in _p["brackets"] if not _pip(_L, b["x"], b["y"])]
    _outc = [c for c in _p["clamps"] if not _pip(_L, c["x"], c["y"], 60.0)]
    _outh = [h for h in _p.get("hrails") or [] if not (_pip(_L, h["x0"], h["y"]) and _pip(_L, h["x1"], h["y"]))]
    ok(_p["ok"] and not _out and not _outb and not _outc and not _outh,
       "PG1: Г-стена, %s — ничего за контуром (стоек %d, кронштейнов %d, кляммеров %d, ГП %d вне)"
       % (_st, len(_out), len(_outb), len(_outc), len(_outh)))
# фронтон: стойки у края доходят до ската, не до конька
_G = [[0, 0], [10000, 0], [10000, 3000], [5000, 6000], [0, 3000]]
_p = frame_plan({"system": "Standart", "sub_type": "vertical", "contours": [{"outer": _G}],
                 "joints_x": [305.0 + 610 * k for k in range(16)], "rows_y": [605.0 * k for k in range(1, 10)]})
_top = max(r["y1"] for r in _p["rails"] if abs(r["x"] - 305.0) < 1e-6)
ok(abs(_top - (3000 + 305.0 * 3000 / 5000)) < 1e-6, "PG2: фронтон — стойка x=305 до ската (%.1f)" % _top)
# окно в 90 мм от бокового края: оконная стойка (грань + 100) — не за стеной
_p = frame_plan({"system": "Standart", "sub_type": "vertical",
                 "contours": [{"outer": rect(0, 0, 12700, 6000), "holes": [rect(11210, 1800, 12610, 3700)]}],
                 "joints_x": [305.0 + 610 * k for k in range(21)], "rows_y": [605.0 * k for k in range(1, 10)]})
ok(all(r["x"] <= 12700 + 1e-6 for r in _p["rails"]) and all(c["x"] <= 12700 + 1e-6 for c in _p["clamps"]),
   "PG3: окно у края зоны — оконная стойка за стену не выходит")
# межэтажная: СП-60-40 под окном не проходит сквозь соседнее окно / вдоль окна под ним
_p = frame_plan({"system": "Межэтажная", "sub_type": "interfloor",
                 "contours": [{"outer": rect(0, 0, 5000, 5200),
                               "holes": [rect(2560, 1100, 3660, 2250), rect(2510, 450, 4160, 1100),
                                         rect(3370, 1950, 4120, 4200), rect(2180, 1060, 3080, 2210)]}],
                 "joints_x": [305.0 + 1508 * k for k in range(4)], "rows_y": [605.0 * k for k in range(1, 8)],
                 "floors_y": [3000.0]})
_holes = [(2560, 1100, 3660, 2250), (2510, 450, 4160, 1100), (3370, 1950, 4120, 4200), (2180, 1060, 3080, 2210)]
_bad = [h for h in _p["hrails"] if h["kind"] == "СП-60-40" and
        any(hx0 + 1 < min(h["x1"], hx1) - max(h["x0"], hx0) + hx0 and hy0 + 1 < h["y"] < hy1 - 1 and
            min(h["x1"], hx1) - max(h["x0"], hx0) > 1 for hx0, hy0, hx1, hy1 in _holes)]
ok(_p["ok"] and not _bad, "PG4: СП-60-40 не проходит сквозь соседние окна (%s)" % _bad[:1])

# ── ZF1–ZF6 (23.09, ревью): frame_engine — зоны этапа 1 (facade_zone/1),
#    группировка голых контуров точкой в полигоне, СВОИ оси у зоны ──
import frame_engine as _fe
_zone = {"schema": "facade_zone/1", "id": "Ф-1", "units": "mm",
         "outer": {"pts": rect(0, 0, 6000, 3000), "bulges": [0, 0, 0, 0]},
         "openings": [{"id": "W", "kind": "window", "poly": {"pts": rect(2000, 900, 3400, 2400),
                                                             "bulges": [0, 0, 0, 0]}}],
         "meta": {"outer_contour_id": "A"}}
_base = {"op": "frame", "sub_type": "vertical", "system": "Standart",
         "joints_x": [305.0 + 610 * k for k in range(10)], "rows_y": [605.0 * k for k in range(1, 5)]}
_r = _fe.run(dict(json.loads(json.dumps(_base)), zones=[{"zone_id": "Ф-1", "zone": _zone}]))
ok(_r["ok"] and _r["summary"]["rails"] > 0 and {t["zone"] for t in _r["rails"]} == {"Ф-1"},
   "ZF1: зона _fzones.json (outer/openings[].poly) читается — подсистема и ЗАХВАТКА «Ф-1»")
ok(not any(2000 + 1 < t["x"] < 3400 - 1 and t["y0"] < 2400 - 1 and t["y1"] > 900 + 1 for t in _r["rails"]),
   "ZF1b: проём зоны учтён (стойки не сквозь окно)")
_old = _fe.run(dict(json.loads(json.dumps(_base)), zones=[{"zone_id": "Ф-1", "zone": {
    "contour": {"pts": rect(0, 0, 6000, 3000)}, "openings": [{"contour": {"pts": rect(2000, 900, 3400, 2400)}}]}}]))
ok(_old["ok"] and _old["summary"]["rails"] == _r["summary"]["rails"], "ZF2: прежний вид contour{pts} тоже принимается")
_zm = json.loads(json.dumps(_zone))
_zm["units"] = "m"
_zm["outer"]["pts"] = [[x / 1000.0, y / 1000.0] for x, y in _zm["outer"]["pts"]]
_zm["openings"][0]["poly"]["pts"] = [[x / 1000.0, y / 1000.0] for x, y in _zm["openings"][0]["poly"]["pts"]]
_rm = _fe.run(dict(json.loads(json.dumps(_base)), zones=[{"zone_id": "Ф-1", "zone": _zm}]))
ok(_rm["ok"] and _rm["summary"]["rails"] == _r["summary"]["rails"], "ZF3: зона в метрах = в миллиметрах")
_za = json.loads(json.dumps(_zone))
_za["outer"]["bulges"] = [0, 0, 0.3, 0]
_ra = _fe.run(dict(json.loads(json.dumps(_base)), zones=[{"zone_id": "Ф-1", "zone": _za}]))
ok(_ra["ok"] is False and any("дугами" in n for n in _ra["notes"]), "ZF4: зона с дугами — нота, не падение")
_Lc = [[0, 0], [9000, 0], [9000, 6000], [6000, 6000], [6000, 3000], [0, 3000]]
_rg = _fe.run(dict(json.loads(json.dumps(_base)), joints_x=[305.0 + 610 * k for k in range(15)],
                   contours=[{"id": "L", "pts": _Lc}, {"id": "P", "pts": rect(1000, 3500, 5000, 5500)}]))
ok(_rg["ok"] and {t["zone"] for t in _rg["rails"]} == {"контур L", "контур P"},
   "ZF5: зона в «кармане» Г-стены — отдельная зона (не проём), подсистема в обеих")
_jz = [305.0 + 610 * k for k in range(10)]
_jz2 = [610.0 + 1220 * k for k in range(5)]
_rj = _fe.run(dict(json.loads(json.dumps(_base)), joints_x=sorted(_jz + _jz2), zones=[
    {"zone_id": "Ф-1", "zone": _zone, "joints_x": _jz, "rows_y": _base["rows_y"]}]))
ok(_rj["ok"] and {round(t["x"], 1) for t in _rj["rails"]} >= {round(x, 1) for x in _jz if 0 < x < 6000}
   and not ({round(t["x"], 1) for t in _rj["rails"]} & ({round(x, 1) for x in _jz2} - {round(x, 1) for x in _jz})),
   "ZF6: свои оси зоны важнее общего списка (чужих швов нет)")

# ── IR1–IR9 (24.09, независимая рецензия): расчёт по фактической
#    грузовой ширине, треугольник, дуги голого контура, «только
#    кляммеры» по контуру и мимо проёмов, отчёт по каждой зоне, полная
#    вложенность контуров ──
_ir = {"op": "frame", "sub_type": "vertical", "system": "Вектор-1", "corners_x": [0, 3100],
       "contours": [{"id": "A", "pts": rect(0, 0, 3100, 3000)}],
       "joints_x": [100, 200, 300, 400, 1600, 2800], "rows_y": [600.0 * k for k in range(1, 5)],
       "calc": {"wind_region": "II", "terrain": "B", "height": 30, "q_clad": 25, "offset": 230,
                "na_max": 3000}}
_r1 = _fe.run(json.loads(json.dumps(_ir)))
_st = _r1["calc_report"]["steps"]
ok(_r1["ok"] and any("грузовая ширина для расчёта 600" in n for n in _r1["notes"]),
   "IR1: грузовая ширина — по итоговым осям (600), не медиана исходных (100)")
_b2200 = sorted(b["y"] for b in _r1["brackets"] if abs(b["x"] - 2200) < 1e-6)
ok(_st["corner"] <= 500 and all(_b2200[i + 1] - _b2200[i] <= _st["corner"] + 1e-6
                                for i in range(len(_b2200) - 1)),
   "IR1b: у достроенной стойки X=2200 шаг кронштейнов в пределах расчётного угловой зоны (%s)" % _b2200)
_reg = dict(json.loads(json.dumps(_ir)), joints_x=[305.0 + 610 * k for k in range(5)])
_r2 = _fe.run(_reg)
ok(_r2["ok"] and not any("грузовая ширина для расчёта" in n for n in _r2["notes"]),
   "IR2: регулярная сетка — ширина прежняя (нота не нужна)")
_tri = _fe.run({"op": "frame", "sub_type": "vertical", "system": "Standart",
                "contours": [{"id": "T", "pts": [[0, 0], [6000, 0], [3000, 6000]]}],
                "joints_x": [305.0 + 610 * k for k in range(10)], "rows_y": [605.0 * k for k in range(1, 10)]})
ok(_tri["ok"] and _tri["rails"] and all(_pip([[0, 0], [6000, 0], [3000, 6000]], t["x"], t["y1"])
                                        for t in _tri["rails"]),
   "IR3: треугольный фасад — подсистема есть, стойки до скатов (%d)" % len(_tri["rails"]))
_arc = _fe.run({"op": "frame", "sub_type": "vertical", "system": "Standart",
                "contours": [{"id": "A", "pts": rect(0, 0, 6000, 3000), "bulges": [0, 0, 1, 0]}],
                "joints_x": [305.0 + 610 * k for k in range(10)], "rows_y": [605.0 * k for k in range(1, 5)]})
ok(any("дуги не поддерживаются" in n for n in _arc["notes"]) and not _arc.get("rails"),
   "IR4: голый контур с дугой — нота, не молчаливая хорда")
_Lr = [[0, 0], [6000, 0], [6000, 6000], [3000, 6000], [3000, 2000], [0, 2000]]
_cf = _fe.run({"op": "frame", "sub_type": "vertical", "system": "Standart",
               "contours": [{"id": "L", "pts": _Lr}, {"id": "W", "pts": rect(3600, 3000, 4800, 4500)}],
               "parts": "clamps", "rails_fixed": [{"x": 1000, "y0": 0, "y1": 6000}, {"x": 4200, "y0": 0, "y1": 6000}],
               "joints_x": [1000.0, 4200.0], "rows_y": [600.0 * k for k in range(1, 11)]})
ok(_cf["ok"] and not [c for c in _cf["clamps"] if abs(c["x"] - 1000) < 1e-6 and c["y"] > 2000 + 60],
   "IR5: только кляммеры — существующая стойка по контуру стены (не в «пустоте» Г)")
ok(not [c for c in _cf["clamps"] if abs(c["x"] - 4200) < 1e-6 and 3000 + 60 < c["y"] < 4500 - 60],
   "IR5b: только кляммеры — не внутри окна")
_mz = _fe.run({"op": "frame", "sub_type": "vertical", "system": "Вектор-1", "contours": [],
               "zones": [{"zone_id": "Ф-1", "zone": {"units": "mm", "outer": {"pts": rect(0, 0, 6000, 3000)}},
                          "joints_x": [305.0 + 610 * k for k in range(10)]},
                         {"zone_id": "Ф-2", "zone": {"units": "mm", "outer": {"pts": rect(0, 4000, 3100, 7000)}},
                          "joints_x": [100, 200, 300, 400, 1600, 2800]}],
               "rows_y": [600.0 * k for k in range(1, 12)], "corners_x": [0, 3100, 6000],
               "calc": {"wind_region": "II", "terrain": "B", "height": 30, "q_clad": 25, "offset": 230,
                        "na_max": 3000}})
ok(_mz["ok"] and [c["zone_id"] for c in _mz.get("calc_reports") or []] == ["Ф-1", "Ф-2"],
   "IR6: расчётный отчёт по каждой зоне (calc_reports), не только последней")
_U = [[0, 0], [2600, 0], [2600, 2400], [2500, 2400], [2500, 100], [100, 100], [100, 2400], [0, 2400]]
_g = _fe.run({"op": "frame", "sub_type": "vertical", "system": "Standart",
              "contours": [{"id": "U", "pts": _U}, {"id": "I", "pts": rect(300, 300, 2300, 2200)}],
              "joints_x": [305.0 + 610 * k for k in range(5)], "rows_y": [605.0 * k for k in range(1, 4)]})
ok(_g["ok"] and {t["zone"] for t in _g["rails"]} == {"контур U", "контур I"},
   "IR7: U-полоса вокруг отдельной зоны — обе зоны (не «проём»)")
_Uw = [[0, 0], [9000, 0], [9000, 6000], [6000, 6000], [6000, 3000], [3000, 3000], [3000, 6000], [0, 6000]]
_Uo = [[500, 500], [8500, 500], [8500, 2500], [7000, 2500], [7000, 1500], [2000, 1500], [2000, 2500], [500, 2500]]
_h = _fe.run({"op": "frame", "sub_type": "vertical", "system": "Standart",
              "contours": [{"id": "W", "pts": _Uw}, {"id": "O", "pts": _Uo}],
              "joints_x": [305.0 + 610 * k for k in range(15)], "rows_y": [605.0 * k for k in range(1, 10)]})
ok(_h["ok"] and {t["zone"] for t in _h["rails"]} == {"контур W"},
   "IR8: П-проём внутри П-стены (проба габарита в выемке стены) — проём, а не стена")
ok(not any(2000 + 1 < t["x"] < 7000 - 1 and t["y0"] < 1500 - 1 and t["y1"] > 500 + 1 and
           not (t["y1"] <= 500 + 1 or t["y0"] >= 1500 - 1) for t in _h["rails"]),
   "IR8b: стойки не сквозь П-проём")

# ── FR-G (23.09n, ответ Германа по №24): кусок вертикали выше последнего ГП
#    длиннее 300 мм — добавить ГП у верха; кусок ≤ 300 — как есть. Случай из
#    синтетики: ШП над окном режется от верха окна, последний кусок висит ──
def _fr_g(win_top):
    return frame_plan({"system": "Ортогональная", "sub_type": "ortho",
                       "contours": [{"outer": rect(0, 0, 4000, 6700),
                                     "holes": [rect(1500, 1000, 2500, win_top)]}],
                       "joints_x": [600, 1200, 1800, 2400, 3000, 3600]})
pg1 = _fr_g(3305)          # куски над окном: 3305–6300 и 6310–6700 (390 мм, без ГП)
top1 = [r for r in pg1["rails"] if abs(r["x"] - 1800) < 1 and r["y1"] > 6600]
ok(top1 and any(abs(h["y"] - 6400) < 1 and h["x0"] - 1 <= 1800 <= h["x1"] + 1 for h in pg1["hrails"]),
   "FR-G1: кусок 390 мм над последним ГП — доп. ГП на 300 ниже верха (6400)")
ok(len([b for b in pg1["brackets"] if abs(b["y"] - 6400) < 1]) == 7 and
   any("доп. ГП у верха" in n for n in pg1.get("notes", [])),
   "FR-G1b: у доп. ГП кронштейны по 7 колонкам сетки и замечание")
pg2 = _fr_g(3400)          # верхний кусок 6405–6700 = 295 мм
ok(not any(abs(h["y"] - 6400) < 60 for h in pg2["hrails"]) and
   not any("доп. ГП у верха" in n for n in pg2.get("notes", [])),
   "FR-G2: кусок ≤ 300 мм — ГП не добавляется")

# ── FR-T (Денис 26.09; Герман 29.09 — ответ по №27): облицовка — бетонная/
#    клинкерная плитка. Вертикальные направляющие НЕ по швам, а заданным шагом
#    по горизонтали от края зоны (100); тип подсистемы — любой из трёх;
#    кронштейны — по расчёту или вручную, как у всех; шины трёх видов
#    (стартовая по низу и над проёмами, рядовые по центрам швов, концевая по
#    верху и под проёмами — Герман 29.09j, 9б), хлысты до 2500 со стыком на
#    направляющей (9в); кляммеров нет ──
tsys = {"name": "Standart", "bracket_step": 600, "bracket_step_corner": 600}
treq = {"system": tsys, "exact_step": True, "cladding": "clinker", "tile_step_x": 600,
        "contours": [{"outer": rect(0, 0, 3000, 2400),
                      "holes": [rect(1000, 800, 1600, 2000)]}],
        "rows_y": [300 * k for k in range(1, 8)],
        "joints_x": [130 * k for k in range(1, 23)], "parts": "frame"}
pt = frame_plan(treq)
ok(pt["ok"], "FR-T1: ok (%s)" % pt.get("error"))
xs = sorted(set(r["x"] for r in pt["rails"]))
ok(xs == [100, 700, 900, 1300, 1700, 1900, 2500, 2900],
   "FR-T1: стойки шагом 600 от 100 + закрывающая 2900 + у граней окна 900/1700 (%s)" % xs)
ok(not any(abs(r["x"] - 130 * k) < 1 for r in pt["rails"] for k in (1, 2, 3, 4, 5)),
   "FR-T1: оси швов раскладки (шаг 130) игнорируются")
win = sorted((r["y0"], r["y1"]) for r in pt["rails"] if r["x"] in (900, 1700))
ok(win == [(750, 2050), (750, 2050)], "FR-T2: у окна куски на высоту окна + 50/50 (%s)" % win)
thr = [(r["y0"], r["y1"]) for r in pt["rails"] if r["x"] == 1300]
ok(sorted(thr) == [(0, 800), (2000, 2400)], "FR-T2: стойка через окно режется (%s)" % thr)
ok(len(pt["clamps"]) == 0, "FR-T3: кляммеров у плитки нет")


def _kinds(res):
    out = {}
    for h in res["hrails"]:
        out[h["kind"]] = out.get(h["kind"], 0) + 1
    return out


ok(_kinds(pt) == {"шина стартовая": 3, "шина рядовая": 14, "шина концевая": 3},
   "FR-T3: стартовая и концевая — по 2 хлыста зоны (2500+500) и по одной у окна; рядовых 14: "
   "3 сквозных ряда × 2 + 4 ряда окна × 2 куска (%s)" % _kinds(pt))
ok(sorted((h["x0"], h["x1"]) for h in pt["hrails"] if h["y"] == 0) == [(0, 2500), (2500, 3000)],
   "FR-T3: стартовая по низу зоны — стык на направляющей 2500 (от края 100 + 4×600)")
# 29.09l (Герман, 9б): «стартовая, концевая ставится также над окнами и под окнами»
ok([(h["x0"], h["x1"], h["kind"]) for h in pt["hrails"] if h["y"] == 2000] ==
   [(1000, 1600, "шина стартовая")] and
   [(h["x0"], h["x1"], h["kind"]) for h in pt["hrails"] if h["y"] == 800] ==
   [(1000, 1600, "шина концевая")],
   "FR-T3: над окном — стартовая, под окном — концевая, по ширине проёма")
ok(sorted((h["x0"], h["x1"]) for h in pt["hrails"] if h["y"] == 2400) == [(0, 2500), (2500, 3000)],
   "FR-T3: концевая по верху зоны")
ok(sorted((h["x0"], h["x1"]) for h in pt["hrails"] if h["y"] == 1200) == [(0, 1000), (1600, 3000)],
   "FR-T3: рядовая в ряду окна — до граней проёма")
b100 = sorted(b["y"] for b in pt["brackets"] if b["x"] == 100)
ok(b100 == [300, 900, 1500, 2100], "FR-T4: кронштейны шагом 600 буквально от 300 (%s)" % b100)
ok(any("вертикальных направляющих 6 шагом 600" in n for n in pt["notes"]) and
   any("хлыстов шины 20" in n and "стык на направляющей" in n for n in pt["notes"]),
   "FR-T4: итог в замечаниях (%s)" % pt["notes"])
# бетонная — то же; керамогранит — прежний алгоритм (стойки по швам, шин нет)
pc = frame_plan(dict(treq, cladding="concrete"))
ok(pc["ok"] and len(pc["hrails"]) == 20, "FR-T5: бетонная плитка — так же")
pp = frame_plan(dict(treq, cladding="porcelain", joints_x=[500, 1100, 2000, 2600], rows_y=[]))
ok(pp["ok"] and not any(h["kind"].startswith("шина") for h in pp["hrails"]) and
   {500, 2000} <= set(r["x"] for r in pp["rails"]),
   "FR-T5: керамогранит — стойки по швам, шин нет")
# ошибки: нет шага; «только кляммеры»; неизвестная облицовка; угловой шаг и хлыст
e1 = frame_plan(dict(treq, tile_step_x=0))
ok(not e1["ok"] and "шаг вертикальных направляющих" in e1["error"], "FR-T6: без шага — ошибка")
e2 = frame_plan(dict(treq, parts="clamps"))
ok(not e2["ok"] and "кляммеров нет" in e2["error"], "FR-T6: у плитки «только кляммеры» — ошибка")
e3 = frame_plan(dict(treq, cladding="wood"))
ok(not e3["ok"], "FR-T6: неизвестная облицовка — ошибка")
e4 = frame_plan(dict(treq, tile_step_x_corner=50))
ok(not e4["ok"] and "угловой зоне" in e4["error"], "FR-T6: угловой шаг 50 — ошибка")
e5 = frame_plan(dict(treq, tile_whip=100))
ok(not e5["ok"] and "хлыста" in e5["error"], "FR-T6: хлыст 100 мм — ошибка")
# без осей раскладки — у плитки не ошибка; узкая зона — одна стойка посередине
pn = frame_plan(dict(treq, joints_x=[], contours=[{"outer": rect(0, 0, 150, 2400)}]))
ok(pn["ok"] and sorted(set(r["x"] for r in pn["rails"])) == [75],
   "FR-T7: зона уже 200 мм — одна стойка посередине")
# 29.09c (Герман): у плитки кронштейны ПО РАСЧЁТУ; грузовая ширина = заданный шаг
pk = frame_plan(dict(treq, system="Вектор-1", exact_step=False,
                     calc={"wind_region": "II", "terrain": "B", "height": 30,
                           "q_clad": 40, "offset": 200, "na_max": 3000}))
ok(pk["ok"] and any("ПО РАСЧЁТУ" in n for n in pk["notes"]) and
   pk["calc_report"]["inputs"]["b_row"] == 600 and pk["calc_report"]["inputs"]["b_corner"] == 600,
   "FR-T7: у плитки расчёт применяется, грузовая ширина = шаг 600 (%s)" % pk.get("error"))
pk2 = frame_plan(dict(treq, system="Вектор-1", exact_step=False, tile_step_x_corner=400,
                      calc={"wind_region": "II", "terrain": "B", "height": 30,
                            "q_clad": 40, "offset": 200, "na_max": 3000}))
ok(pk2["ok"] and pk2["calc_report"]["inputs"]["b_corner"] == 400,
   "FR-T7: шаг направляющих угловой зоны уходит в расчёт угловой (b_corner 400)")

# зона без раскладки — ряды шагом швов от низа ЭТОЙ зоны + стартовая и концевая
pr = frame_plan(dict(treq, rows_y=[], tile_row_step=500,
                     contours=[{"outer": rect(0, 1000, 1200, 2600)}]))
ok(pr["ok"] and sorted(h["y"] for h in pr["hrails"]) == [1000, 1500, 2000, 2500, 2600],
   "FR-T8: без раскладки — рядовые шагом швов от низа зоны (%s)" % [h["y"] for h in pr["hrails"]])
ok([h["kind"] for h in sorted(pr["hrails"], key=lambda h: h["y"])] ==
   ["шина стартовая", "шина рядовая", "шина рядовая", "шина рядовая", "шина концевая"],
   "FR-T8: низ — стартовая, верх — концевая")

# FR-T9 (Герман 29.09j, 9а и 9в): стык хлыста — на направляющей; хлыст — самый длинный
# до 2500, кратный шагу (при шаге 600 — 2400); после окна — заново от грани проёма
pw = frame_plan(dict(treq, rows_y=[1000], contours=[{"outer": rect(0, 0, 6000, 2000),
                                                     "holes": [rect(1000, 500, 2000, 1500)]}]))
ok([(h["x0"], h["x1"]) for h in sorted(pw["hrails"], key=lambda h: h["x0"]) if h["y"] == 0] ==
   [(0, 2500), (2500, 4900), (4900, 6000)],
   "FR-T9: стартовая 6000 — стыки на направляющих 2500 и 4900: 2500 + 2400 + 1100")
ok(sorted((h["x0"], h["x1"]) for h in pw["hrails"] if h["y"] == 1000) ==
   [(0, 1000), (2000, 4300), (4300, 6000)],
   "FR-T9: рядовая за окном — заново от грани 2000, стык на направляющей 4300")
def _joints(res):
    """Стыки хлыстов: начала кусков прогона, кроме первого."""
    by = {}
    for h in res["hrails"]:
        by.setdefault(h["run"], []).append(h)
    return [h["x0"] for hs in by.values() for h in sorted(hs, key=lambda h: h["x0"])[1:]]


_px = set(r["x"] for r in pw["rails"])
ok(_joints(pw) and all(x in _px for x in _joints(pw)),
   "FR-T9: каждый стык хлыстов — на направляющей (%s)" % _joints(pw))
ok(len(set(h["run"] for h in pw["hrails"])) == 6,
   "FR-T9: прогонов 6 (старт, 2 рядовых, конец, над окном, под окном)")

# FR-T10 (Герман 29.09): у плитки все три типа. Межэтажная — вертикальные межэтажные
# заданным шагом (не по швам), НГП на перекрытиях, шины; без отметок — контур пропущен
# целиком, шин без направляющих нет. Ортогональная — сетка кронштейнов, ГП, вертикальные
# ШП заданным шагом, шины; в итоге шины и ГП — раздельно
pi = frame_plan(dict(treq, sub_type="interfloor", floors_y=[1200]))
ok(pi["ok"] and sorted(set(r["x"] for r in pi["rails"])) == [100, 700, 1300, 1900, 2500, 2900] and
   {r["kind"] for r in pi["rails"]} == {"НСП", "ШП-60-20"},
   "FR-T10: межэтажная — НСП шагом 600 от края, через окно — ШП (%s)"
   % sorted(set((r["x"], r["kind"]) for r in pi["rails"])))
ok(_kinds(pi).get("НГП") == 2 and _kinds(pi).get("шина рядовая") == 14 and
   all(b["kind"] == "несущий" and b["y"] == 1200 for b in pi["brackets"]),
   "FR-T10: межэтажная — НГП на перекрытии (окно режет), несущие на отметке, шины (%s)" % _kinds(pi))
pi0 = frame_plan(dict(treq, sub_type="interfloor", floors_y=[]))
ok(pi0["ok"] and not pi0["hrails"] and not pi0["rails"] and
   any("межэтажная без отметок" in n for n in pi0["notes"]) and
   not any("хлыстов" in n for n in pi0["notes"]),
   "FR-T10: межэтажная без отметок — ни направляющих, ни шин")
po = frame_plan(dict(treq, sub_type="ortho"))
ok(po["ok"] and sorted(set(r["x"] for r in po["rails"] if r["kind"] == "ШП-60-20")) ==
   [100, 700, 1300, 1900, 2500, 2900] and
   sorted(set(r["x"] for r in po["rails"] if r["kind"] == "Z-профиль")) == [900, 1700],
   "FR-T10: ортогональная — ШП шагом 600, Z у граней окна")
ok(sorted(set(h["y"] for h in po["hrails"] if h["kind"] == "ГП-40-40")) == [300, 900, 1500, 2100] and
   po["summary"]["hguides"] == 6 and po["summary"]["shina_pieces"] == 20 and
   near(po["summary"]["shina_lm"], 25.8) and po["summary"]["hrails"] == 26,
   "FR-T10: ортогональная — ГП по сетке 300+600, в итоге ГП 6 и шины 20 (25,8 м) раздельно (%s)"
   % {k: v for k, v in po["summary"].items() if k.startswith("shina") or k.startswith("hg")})

# FR-T11 (Герман 29.09, п.5): углы здания у плитки. Угол на левом краю, шаг направляющих в
# угловой зоне 400: от угла 100/500/900/1300, граница зоны 1500 — общая, дальше 600;
# кронштейны в угловой зоне — угловым шагом
pa = frame_plan(dict(treq, contours=[{"outer": rect(0, 0, 3000, 2400)}], corners_x=[0],
                     tile_step_x_corner=400,
                     system={"name": "Standart", "bracket_step": 600, "bracket_step_corner": 400}))
ok(pa["ok"] and sorted(set(r["x"] for r in pa["rails"])) == [100, 500, 900, 1300, 1500, 2100, 2700, 2900],
   "FR-T11: оси в угловой зоне шагом 400 от угла (%s)" % sorted(set(r["x"] for r in pa["rails"])))
ok(sorted(b["y"] for b in pa["brackets"] if b["x"] == 500) == [300, 700, 1100, 1500, 1900, 2100] and
   sorted(b["y"] for b in pa["brackets"] if b["x"] == 2100) == [300, 900, 1500, 2100],
   "FR-T11: кронштейны: в угловой зоне шагом 400, в рядовой — 600")
pa2 = frame_plan(dict(treq, contours=[{"outer": rect(0, 0, 3000, 2400)}], corners_x=[0]))
ok(sorted(set(r["x"] for r in pa2["rails"])) == [100, 700, 1300, 1900, 2500, 2900],
   "FR-T11: угловой шаг 0 — оси как в рядовой")

# FR-T12: Г-образная зона — стартовая на каждой ступени низа; ряд в 10 мм над ступенью на
# этой ступени не ставится (место стартовой); фронтон — концевой по наклону нет (Герман
# 29.09j, 9д: «концевая по наклону не нужна») и ноты о длине наклона больше нет
L = [[0, 0], [2000, 0], [2000, 1000], [3000, 1000], [3000, 3000], [0, 3000]]
pl = frame_plan(dict(treq, rows_y=[500, 1010, 1500], contours=[{"outer": L}]))
st = sorted((h["y"], h["x0"], h["x1"]) for h in pl["hrails"] if h["kind"] == "шина стартовая")
ok(st == [(0, 0, 2000), (1000, 2000, 3000)], "FR-T12: стартовые на обеих ступенях (%s)" % st)
ok(sorted((h["x0"], h["x1"]) for h in pl["hrails"] if h["y"] == 1010) == [(0, 2000)],
   "FR-T12: ряд 1010 над ступенью 1000 — только на нижней части")
G = [[0, 0], [4000, 0], [4000, 3000], [2000, 4000], [0, 3000]]
pg = frame_plan(dict(treq, rows_y=[3500], contours=[{"outer": G}]))
ok(pg["ok"] and not any(h["kind"] == "шина концевая" for h in pg["hrails"]) and
   not any("наклон" in n for n in pg["notes"]),
   "FR-T12: фронтон — концевой на наклоне нет, ноты нет (%s)" % pg["notes"])

# FR-T13: марка шины от проекта — в профиль каждого куска и в итог; хлыст из запроса
pb = frame_plan(dict(treq, tile_rail_brand="ШК-40", tile_whip=3000))
ok(all(h["profile"] == "ШК-40" for h in pb["hrails"] if h["kind"].startswith("шина")) and
   pb["summary"]["tile_rail_brand"] == "ШК-40" and pb["summary"]["tile_whip"] == 3000 and
   pb["summary"]["shina_start"] == 2 and near(pb["summary"]["shina_start_lm"], 3.6),
   "FR-T13: марка в профиле шин, хлыст 3000 — стартовая зоны одним куском + над окном (%s)"
   % pb["summary"])

# FR-T14 (Герман 29.09j, 9в): «если шаг 600 — хлыст 2400, стандартная 2500 — для шага 500».
# Прогон 8000 без окон: стыки на направляющих, внутренние хлысты = 2400 / 2500
for _st, _mid in ((600, 2400), (500, 2500)):
    _pz = frame_plan(dict(treq, tile_step_x=_st, rows_y=[], contours=[{"outer": rect(0, 0, 8000, 1000)}]))
    _ax = set(r["x"] for r in _pz["rails"])
    _bt = sorted((h for h in _pz["hrails"] if h["y"] == 0), key=lambda h: h["x0"])
    ok(_pz["ok"] and all(h["x0"] in _ax for h in _bt[1:]) and all(h["len"] <= 2500 for h in _bt) and
       [h["len"] for h in _bt[1:-1]] == [_mid] * (len(_bt) - 2) and len(_bt) >= 3,
       "FR-T14: шаг %d — стыки на направляющих, внутренние хлысты %d (%s)"
       % (_st, _mid, [(h["x0"], h["len"]) for h in _bt]))
# последняя направляющая прогона стыком не берётся: 2600 → 1900 + 700 (не 2500 + огрызок 100)
_p26 = frame_plan(dict(treq, rows_y=[], contours=[{"outer": rect(0, 0, 2600, 1000)}]))
ok(sorted(h["len"] for h in _p26["hrails"] if h["y"] == 0) == [700, 1900],
   "FR-T14: прогон 2600 — стык на 1900, у края огрызка на одной опоре нет (%s)"
   % sorted(h["len"] for h in _p26["hrails"] if h["y"] == 0))

# FR-T15 (9б): дверь в пол — стартовая над ней, концевой под ней нет (под ней не стена); окно
# под верхом зоны — концевая под ним, стартовой над ним нет
_pd = frame_plan(dict(treq, rows_y=[], contours=[{"outer": rect(0, 0, 4000, 3000),
                                                  "holes": [rect(1000, 0, 2000, 2100),
                                                            rect(2800, 1500, 3500, 3000)]}]))
_st15 = sorted((h["y"], h["x0"], h["x1"]) for h in _pd["hrails"] if h["kind"] == "шина стартовая")
_en15 = sorted((h["y"], h["x0"], h["x1"]) for h in _pd["hrails"] if h["kind"] == "шина концевая")
ok(_st15 == [(0, 0, 1000), (0, 2000, 4000), (2100, 1000, 2000)],
   "FR-T15: стартовая — низ зоны мимо двери и над дверью (%s)" % _st15)
ok((1500, 2800, 3500) in _en15 and not any(e[0] == 0 for e in _en15) and
   not any(e[0] == 3000 and e[1] < 3500 and e[2] > 2800 for e in _en15),
   "FR-T15: концевая — под окном у верха; над ним по верху зоны — нет (%s)" % _en15)

# FR-T16 (9в): направляющих в пределах хлыста нет (шаг больше хлыста) — стык без опоры,
# в замечаниях — счёт
_pa = frame_plan(dict(treq, tile_step_x=3000, rows_y=[], contours=[{"outer": rect(0, 0, 7000, 1000)}]))
ok(_pa["ok"] and any("без направляющей" in n for n in _pa["notes"]) and
   all(h["len"] >= 300 - 1e-6 for h in _pa["hrails"]),
   "FR-T16: шаг 3000 > хлыста — стыки без опоры в замечаниях, кусков короче 300 нет (%s)"
   % [n for n in _pa["notes"] if "шины" in n])

# FR-T17: две зоны рядом — стыки каждой только на её направляющих, номера прогонов сквозные
_p2 = frame_plan(dict(treq, rows_y=[], contours=[{"outer": rect(0, 0, 3000, 1000)},
                                                 {"outer": rect(3000, 0, 9000, 1000)}]))
_r2 = [(h["run"], h["x0"]) for h in _p2["hrails"]]
_ax2 = [set(r["x"] for r in _p2["rails"] if r["x"] < 3000), set(r["x"] for r in _p2["rails"] if r["x"] > 3000)]
ok(_p2["ok"] and len(set(r for r, _x in _r2)) == 4 and
   all(x in _ax2[0 if x < 3000 else 1] for x in _joints(_p2)) and
   sorted(_joints(_p2)) == [2500, 2500, 5500, 5500, 7900, 7900],
   "FR-T17: две зоны — 4 прогона со сквозными номерами, стыки на своих направляющих (%s)" % _r2)

# FR-T18 (29.09l, прогон на эталоне 290×82): контур с вершинами, разнесёнными на доли мм
# (шип стены шириной 0,001 мм), давал «шины» по 0,001 мм — в счёте хлыстов и на чертеже
_spk = [[0, 0], [3000, 0], [3000, 1000], [1500.001, 1000], [1500.001, 1200], [1500, 1200],
        [1500, 1000], [0, 1000]]
_ps = frame_plan(dict(treq, rows_y=[1100], contours=[{"outer": _spk}]))
ok(_ps["ok"] and all(h["len"] >= 5 for h in _ps["hrails"]) and
   not any(abs(h["y"] - 1100) < 1 or abs(h["y"] - 1200) < 1 for h in _ps["hrails"]),
   "FR-T18: шип контура 0,001 мм — шин-обрезков нет (%s)"
   % sorted((h["y"], h["len"]) for h in _ps["hrails"] if h["len"] < 5))

# FR-T19 (Герман 29.09n: «если шина не попадает ни на одну направляющую, то надо её удлинить»):
# окно 400 мм между осями 1300 и 1900 — над и под ним шины удлинены до ближайших направляющих: слева
# ось 1300 у окна смещена в 1250 (100 от грани), справа своя стойка 1850 не ставится — ось 1900 в 50 мм
_pn = frame_plan(dict(treq, contours=[{"outer": rect(0, 0, 3000, 2400), "holes": [rect(1350, 800, 1750, 2000)]}]))
_hd = [(h["x0"], h["x1"]) for h in _pn["hrails"] if h["y"] == 2000 and h["kind"] == "шина стартовая"]
_sl = [(h["x0"], h["x1"]) for h in _pn["hrails"] if h["y"] == 800 and h["kind"] == "шина концевая"]
ok(_hd == [(1250, 1900)] and _sl == [(1250, 1900)] and any("удлинено" in n for n in _pn["notes"]),
   "FR-T19: узкое окно — шины над/под ним удлинены до ближайших направляющих (%s / %s)" % (_hd, _sl))
_ax19 = set(r["x"] for r in _pn["rails"] if r["y0"] - 20 <= 2000 <= r["y1"] + 20)
ok({1250, 1900} <= _ax19 and not any(1250 < x < 1900 for x in _ax19),
   "FR-T19: концы удлинённой шины — на направляющих, между ними направляющих нет (%s)" % sorted(_ax19))
# межэтажная: оконных стоек нет — до ближайших межэтажных 1300 и 1900
_pi2 = frame_plan(dict(treq, sub_type="interfloor", floors_y=[1200],
                       contours=[{"outer": rect(0, 0, 3000, 2400), "holes": [rect(1350, 800, 1750, 2000)]}]))
_hd2 = [(h["x0"], h["x1"]) for h in _pi2["hrails"] if h["y"] == 2000 and h["kind"] == "шина стартовая"]
ok(_hd2 == [(1300, 1900)], "FR-T19: межэтажная — до ближайших направляющих 1300/1900 (%s)" % _hd2)
# FR-T20: простенок 500 мм в вырезах низа зоны — направляющих в нём нет, удлинить не до чего
_pier = [[0, 0], [1000, 0], [1000, 1000], [1350, 1000], [1350, 0], [1850, 0], [1850, 1000],
         [2200, 1000], [2200, 0], [3000, 0], [3000, 3000], [0, 3000]]
# 29.09t (Герман, ответ на 6е PDF №28: «вставить дополнительную направляющую в 100 мм, как у окна»):
# у краёв вырезов — направляющие 1450 и 1750 по высоте простенка (+50), с кронштейнами; шина на них
_pp = frame_plan(dict(treq, contours=[{"outer": _pier}]))
_st = [(h["x0"], h["x1"]) for h in _pp["hrails"] if h["y"] == 0 and 1300 < h["x0"] < 1900]
_nr = sorted((r["x"], r["y0"], r["y1"]) for r in _pp["rails"] if r["x"] in (900, 1450, 1750, 2300))
ok(_st == [(1350, 1850)] and not any("удлинить не до чего" in n for n in _pp["notes"]) and
   _nr == [(900, 0, 1050), (1450, 0, 1050), (1750, 0, 1050), (2300, 0, 1050)] and
   any("краёв вырезов" in n for n in _pp["notes"]),
   "FR-T20: у краёв вырезов — направляющие в 100 мм (900/1450/1750/2300), шина простенка на них (%s)" % _nr)
ok(sorted(b["y"] for b in _pp["brackets"] if b["x"] == 1450) == [300, 750],
   "FR-T20: на направляющей простенка 0–1050 — кронштейны по правилу «300 от торцов» (%s)"
   % sorted(b["y"] for b in _pp["brackets"] if b["x"] == 1450))

# FR-O-DUP (29.09, синтетика «в ролях»): ортогональная — две оси руста у граней соседних окон
# с узким простенком (швы раскладки 1164 и 1176) смещались в одну точку x=1260: два одинаковых
# Z-профиля в одном месте. Теперь куски сводятся по оси, как в вертикальной (04.08)
_wa = rect(410, 540, 1160, 2290)
_wb = rect(1180, 830, 1830, 2280)
_od = frame_plan({"system": "Ортогональная", "sub_type": "ortho",
                  "contours": [{"outer": rect(0, 0, 4000, 3000), "holes": [_wa, _wb]}],
                  "joints_x": [406, 785, 1164, 1176, 1505, 1834, 2241, 2849, 3256]})
_ov = []
for _i, _r in enumerate(_od["rails"]):
    for _q in _od["rails"][_i + 1:]:
        if abs(_r["x"] - _q["x"]) <= 1e-6 and min(_r["y1"], _q["y1"]) - max(_r["y0"], _q["y0"]) > 1e-6:
            _ov.append((_r["x"], _r["y0"], _r["y1"], _q["y0"], _q["y1"]))
ok(_od["ok"] and not _ov and any(abs(_r["x"] - 1260) < 1e-6 for _r in _od["rails"]),
   "FR-O-DUP: ортогональная — у граней соседних окон нет двух профилей в одном месте (%s)" % _ov[:3])

print("frame_plan: %d проверок OK" % _n)
