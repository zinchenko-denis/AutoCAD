# -*- coding: utf-8 -*-
"""Юниты этапа 4: frame_calc. Запуск: PYTHONUTF8=1 python3
test_frame_calc.py. ВСЕ эталонные числа — из 13 боевых статрасчётов
«Вектор фасад» (atspec-testdata/docs/facades_calc/, METHOD_CALC.md),
сверены руками 27.07. FC-обозначения кейсов:
  FC1 ветровая; FC2 монолит/кирпич тип-1; FC3 Нижнекаменская тип-4;
  FC4 Искра тип-5; FC5 республиканская (пограничный кронштейн 1-1 и
  подбор шага 450); FC6 Новгород-260 ортогональная; FC7 Регенбоген
  АКП местность A; FC8 pick_step/report."""
import sys
from frame_calc import (wind_peak, ice_load, spans_const, calc_chain,
                        pick_step, report, WIND_REGIONS)

_n = 0


def ok(cond, msg):
    global _n
    _n += 1
    if not cond:
        print("FAIL:", msg)
        sys.exit(1)


def near(a, b, tol):
    return abs(a - b) <= tol


def rel(a, b, r=0.01):
    return abs(a - b) <= abs(b) * r


def chk(chain, name):
    for c in chain["checks"]:
        if c["name"].startswith(name):
            return c
    raise KeyError(name)


# ── FC1: пиковая ветровая (их стр. 4; допуск 0.5 кг/м² — их округления k/ζ)
for (w0, t, h), (er, ec) in [
        ((30, "B", 6), (58.1, 106.4)),        # монолит/кирпич
        ((30, "B", 61.2), (117.5, 215.4)),    # Нижнекаменская
        ((30, "B", 57), (114.9, 210.7)),      # Искра/ФЦП
        ((30, "B", 41.2), (103.8, 190.3)),    # республиканская
        ((30, "A", 28), (113.3, 207.8)),      # Регенбоген (тип A)
        ((23, "B", 10), (51.7, 94.9))]:       # Новгород (район I)
    r, c = wind_peak(w0, t, h)
    ok(near(r, er, 0.5) and near(c, ec, 0.6),
       "FC1: ветровая (%s,%s,%s): %.1f/%.1f vs %s/%s" % (w0, t, h, r, c, er, ec))
ok(near(ice_load("II"), 7.6, 0.25), "FC1: гололёд район II = 7.6")
ok(WIND_REGIONS["II"] == 30 and WIND_REGIONS["I"] == 23, "FC1: w0 районов")

# ── FC2: тип-1 монолит КГ (Ольгинская 2026) — вся цепочка при 800
MONO = dict(scheme="vertical", w0=30, terrain="B", height=6,
            q_clad=25, gamma_clad=1.1, q_rails=0.745, offset=170,
            na_max=3000, bracket="КР2-70", extender="УК-70-1,2",
            profile="ГП-40-40-1,2", b_row=608, b_corner=608,
            rail_len=3000)
row = calc_chain(MONO, 800, "row")
cor = calc_chain(MONO, 800, "corner")
ok(near(row["n_p"], 14.0, 0.2) and near(row["n_w"], 31.1, 0.3),
   "FC2: N_п=14.0 N_в=31.1 (%s/%s)" % (row["n_p"], row["n_w"]))
ok(near(cor["n_w"], 57.0, 0.5), "FC2: N_в угл=57.0 (%s)" % cor["n_w"])
ok(rel(chk(row, "анкер")["value"], 104.0) and
   near(chk(row, "анкер")["limit"], 306.1, 0.1),
   "FC2: анкер ряд 104.0<=306.1 (%s)" % chk(row, "анкер")["value"])
ok(rel(chk(cor, "анкер")["value"], 140.0), "FC2: анкер угл 140.0")
ok(rel(chk(row, "кронштейн 1-1")["value"], 1157) and
   rel(chk(cor, "кронштейн 1-1")["value"], 2024),
   "FC2: кронштейн 1-1 1157/2024 (%s/%s)" % (
       chk(row, "кронштейн 1-1")["value"], chk(cor, "кронштейн 1-1")["value"]))
ok(rel(chk(row, "кронштейн 2-2")["value"], 232) and
   rel(chk(cor, "кронштейн 2-2")["value"], 425),
   "FC2: кронштейн 2-2 232/425")
ok(rel(chk(row, "удлинитель")["value"], 604.9) and
   rel(chk(cor, "удлинитель")["value"], 1060.1),
   "FC2: удлинитель 604.9/1060.1 (%s/%s)" % (
       chk(row, "удлинитель")["value"], chk(cor, "удлинитель")["value"]))
ok(rel(chk(row, "профиль σ")["value"], 452.9) and
   rel(chk(cor, "профиль σ")["value"], 817.7),
   "FC2: профиль σ 452.9/817.7 (3-пролётная 0.100) (%s/%s)" % (
       chk(row, "профиль σ")["value"], chk(cor, "профиль σ")["value"]))
ok(near(chk(row, "профиль f")["value"], 0.2, 0.05) and
   near(chk(cor, "профиль f")["value"], 0.4, 0.05) and
   near(chk(row, "профиль f")["limit"], 5.3, 0.1),
   "FC2: прогиб 0.2/0.4<=5.3")
ok(rel(chk(row, "заклёпки срез")["value"], 17.0, 0.02) and
   rel(chk(cor, "заклёпки срез")["value"], 29.3, 0.02) and
   near(chk(row, "заклёпки срез")["limit"], 253.06, 0.1),
   "FC2: заклёпки срез 17.0/29.3<=253.06")
ok(rel(chk(row, "заклёпки смятие")["value"], 338.0, 0.02) and
   rel(chk(cor, "заклёпки смятие")["value"], 581.8, 0.02),
   "FC2: смятие 338.0/581.8")
ok(row["passed"] and cor["passed"], "FC2: 800 проходит обе зоны")
kir = calc_chain(dict(MONO, na_max=2400), 800, "row")
ok(near(chk(kir, "анкер")["limit"], 244.9, 0.1) and kir["passed"],
   "FC2: кирпич — лимит анкера 244.9 (na_max=2400)")

# ── FC3: тип-4 Нижнекаменская (Вектор-5 кассеты, 61.2 м) — 350/200
NIZH = dict(scheme="interfloor", w0=30, terrain="B", height=61.2,
            q_clad=8, gamma_clad=1.05, q_rails=2.31 + 1.4, offset=280,
            na_max=3960, bracket="КР1-85", extender="УК-85-1,2",
            profile="НСП-95-70-1,2", b_row=800, b_corner=450,
            rail_len=2930)
row = calc_chain(NIZH, 350, "row")
cor = calc_chain(NIZH, 200, "corner")
ok(near(row["n_p"], 20.0, 0.2) and near(cor["n_p"], 16.3, 0.2),
   "FC3: N_п 20.0/16.3 (%s/%s)" % (row["n_p"], cor["n_p"]))
ok(near(row["n_w"], 136.4, 1.0) and near(cor["n_w"], 142.9, 1.0),
   "FC3: N_в 136.4/142.9 многопролётная 1.132 (%s/%s)" % (
       row["n_w"], cor["n_w"]))
ok(rel(chk(row, "анкер")["value"], 281.2) and
   rel(chk(cor, "анкер")["value"], 277.6) and
   near(chk(row, "анкер")["limit"], 404.1, 0.2),
   "FC3: анкер 281.2/277.6<=404.1 (%s/%s)" % (
       chk(row, "анкер")["value"], chk(cor, "анкер")["value"]))
ok(rel(chk(row, "кронштейн 1-1")["value"], 1401) and
   rel(chk(cor, "кронштейн 1-1")["value"], 1421),
   "FC3: кронштейн 1-1 1401/1421 (%s/%s)" % (
       chk(row, "кронштейн 1-1")["value"], chk(cor, "кронштейн 1-1")["value"]))
ok(rel(chk(row, "кронштейн 2-2")["value"], 257) and
   rel(chk(cor, "кронштейн 2-2")["value"], 270),
   "FC3: кронштейн 2-2 257/270")
ok(rel(chk(row, "удлинитель")["value"], 2117.1) and
   rel(chk(cor, "удлинитель")["value"], 2203.6),
   "FC3: удлинитель 2117.1/2203.6 (%s/%s)" % (
       chk(row, "удлинитель")["value"], chk(cor, "удлинитель")["value"]))
ok(rel(chk(row, "профиль σ")["value"], 2080.9) and
   rel(chk(cor, "профиль σ")["value"], 2145.2),
   "FC3: профиль σ 2080.9/2145.2 (0.106 multi) (%s/%s)" % (
       chk(row, "профиль σ")["value"], chk(cor, "профиль σ")["value"]))
ok(near(chk(row, "профиль f")["value"], 7.9, 0.15) and
   near(chk(cor, "профиль f")["value"], 8.2, 0.15) and
   near(chk(row, "профиль f")["limit"], 19.5, 0.1),
   "FC3: прогиб 7.9/8.2<=19.5 (%s/%s)" % (
       chk(row, "профиль f")["value"], chk(cor, "профиль f")["value"]))
ok(rel(chk(row, "заклёпки срез")["value"], 68.9) and
   rel(chk(cor, "заклёпки срез")["value"], 72.1),
   "FC3: заклёпки 68.9/72.1")
ok(rel(chk(row, "заклёпки смятие")["value"], 1367.7) and
   rel(chk(cor, "заклёпки смятие")["value"], 1431.5),
   "FC3: смятие 1367.7/1431.5")
ok(row["passed"] and cor["passed"], "FC3: 350/200 проходят")

# ── FC4: тип-5 Искра (без удлинителя, КП-125, 4 заклёпки) — 500/270
ISKRA = dict(scheme="interfloor_direct", w0=30, terrain="B", height=57,
             q_clad=16, gamma_clad=1.1, q_rails=1.97, offset=120,
             na_max=4000, bracket="КП-125", extender=None,
             profile="НСП-69-60-1,2", b_row=500, b_corner=270,
             rail_len=3000, n_rivets=4)
row = calc_chain(ISKRA, 500, "row")
cor = calc_chain(ISKRA, 270, "corner")
ok(near(row["n_p"], 32.6, 0.3) and near(cor["n_p"], 20.5, 0.3),
   "FC4: N_п 32.6/20.5 (%s/%s)" % (row["n_p"], cor["n_p"]))
ok(near(row["n_w"], 195.1, 1.5) and near(cor["n_w"], 193.2, 1.5),
   "FC4: N_в 195.1/193.2 (%s/%s)" % (row["n_w"], cor["n_w"]))
ok(rel(chk(row, "анкер")["value"], 231.8) and
   rel(chk(cor, "анкер")["value"], 216.2) and
   near(chk(row, "анкер")["limit"], 408.2, 0.2),
   "FC4: анкер 231.8/216.2<=408.2 (e3=0) (%s/%s)" % (
       chk(row, "анкер")["value"], chk(cor, "анкер")["value"]))
ok(rel(chk(row, "кронштейн 1-1")["value"], 78, 0.03) and
   rel(chk(cor, "кронштейн 1-1")["value"], 64, 0.03),
   "FC4: кронштейн 1-1 78/64 (%s/%s)" % (
       chk(row, "кронштейн 1-1")["value"], chk(cor, "кронштейн 1-1")["value"]))
ok(rel(chk(row, "кронштейн 2-2")["value"], 2145) and
   rel(chk(cor, "кронштейн 2-2")["value"], 2123),
   "FC4: кронштейн 2-2 2145/2123 (e6=27!) (%s/%s)" % (
       chk(row, "кронштейн 2-2")["value"], chk(cor, "кронштейн 2-2")["value"]))
ok(len([c for c in row["checks"] if c["name"].startswith("удлин")]) == 0,
   "FC4: удлинителя нет (тип-5)")
ok(rel(chk(row, "профиль σ")["value"], 1609.3) and
   rel(chk(cor, "профиль σ")["value"], 1588.4),
   "FC4: профиль σ 1609.3/1588.4 (%s/%s)" % (
       chk(row, "профиль σ")["value"], chk(cor, "профиль σ")["value"]))
ok(near(chk(row, "профиль f")["value"], 8.1, 0.15) and
   near(chk(cor, "профиль f")["value"], 8.0, 0.15) and
   near(chk(row, "профиль f")["limit"], 20.0, 0.1),
   "FC4: прогиб 8.1/8.0<=20.0")
ok(rel(chk(row, "заклёпки срез")["value"], 49.5, 0.02) and
   rel(chk(cor, "заклёпки срез")["value"], 49.0, 0.02),
   "FC4: заклёпки n=4: 49.5/49.0")
ok(rel(chk(row, "заклёпки смятие")["value"], 981.2, 0.02) and
   rel(chk(cor, "заклёпки смятие")["value"], 971.7, 0.02),
   "FC4: смятие 981.2/971.7")
ok(row["passed"] and cor["passed"], "FC4: 500/270 проходят")

# ── FC5: республиканская (тип-1, вынос 230, h=41.2) — пограничный
#    кронштейн 1-1 (1997/2039 из 2250) и ПОДБОР шага угловой = 450
RESP = dict(scheme="vertical", w0=30, terrain="B", height=41.2,
            q_clad=25, gamma_clad=1.1, q_rails=1.21, offset=230,
            na_max=1880, bracket="КР2-70", extender="УК-70-1,2",
            profile="ШП-60-20-20-1,2", b_row=600, b_corner=600,
            rail_len=3000)
row = calc_chain(RESP, 800, "row")
cor = calc_chain(RESP, 450, "corner")
ok(near(row["n_p"], 14.2, 0.2) and near(cor["n_p"], 8.0, 0.15),
   "FC5: N_п 14.2/8.0 (%s/%s)" % (row["n_p"], cor["n_p"]))
ok(near(row["n_w"], 54.8, 0.4), "FC5: N_в ряд 800→3 пролёта→1.10: 54.8"
   " (%s)" % row["n_w"])
ok(near(cor["n_w"], 58.2, 0.5), "FC5: N_в угл 450→6 пролётов→1.132: 58.2"
   " (%s)" % cor["n_w"])
ok(rel(chk(row, "анкер")["value"], 160.4) and
   rel(chk(cor, "анкер")["value"], 128.3) and
   near(chk(row, "анкер")["limit"], 191.8, 0.2),
   "FC5: анкер 160.4/128.3<=191.8 (%s/%s)" % (
       chk(row, "анкер")["value"], chk(cor, "анкер")["value"]))
ok(rel(chk(row, "кронштейн 1-1")["value"], 1997) and
   rel(chk(cor, "кронштейн 1-1")["value"], 2039),
   "FC5: кронштейн 1-1 1997/2039 — почти упор (%s/%s)" % (
       chk(row, "кронштейн 1-1")["value"], chk(cor, "кронштейн 1-1")["value"]))
ok(rel(chk(row, "профиль σ")["value"], 427.7, 0.02),
   "FC5: профиль σ ряд 427.7 (%s)" % chk(row, "профиль σ")["value"])
ok(near(chk(row, "профиль f")["value"], 0.6, 0.1), "FC5: прогиб ряд 0.6")
step, chain, log = pick_step(RESP, "corner")
ok(step == 450, "FC5: ПОДБОР угловой = 450 (500 валится по кронштейну"
   " 1-1) (%s)" % step)
c500 = [c for c in log if c["step"] == 500][0]
ok(not c500["passed"] and
   not chk(c500, "кронштейн 1-1")["ok"],
   "FC5: при 500 кронштейн 1-1 %.0f>2250" % chk(c500, "кронштейн 1-1")["value"])
step_r, _, _ = pick_step(RESP, "row")
ok(step_r == 800, "FC5: подбор рядовой = 800 (конструктивный max)"
   " (%s)" % step_r)

# ── FC6: Новгород-260 (ортогональная, район I, слабый анкер 1280 Н)
NOVG = dict(scheme="ortho", w0=23, terrain="B", height=10,
            q_clad=25, gamma_clad=1.1, q_rails=0.73 + 0.745, offset=260,
            na_max=1280, bracket="КР2-70", e3=12.0, e4=25.0,
            extender="УК-70-1,2", profile="ЗП-40-20-1,2",
            b_row=608, b_corner=608, v_step=400, rail_len=600)
row = calc_chain(NOVG, 600, "row")
cor = calc_chain(NOVG, 600, "corner")
ok(near(row["n_p"], 7.3, 0.15) and near(row["n_w"], 15.5, 0.2),
   "FC6: N_п=7.3 N_в=15.5 (2 пролёта→1.25) (%s/%s)" % (
       row["n_p"], row["n_w"]))
# Их эксель в УГЛОВОЙ зоне непоследовательно взял 3-пролётные
# константы (N_в=25.0, анкер 86.5, кронштейн 934, удлинитель 471.2);
# floor-правило даёт 2 пролёта → 1.25 → консервативнее. Эталоны
# угловой ниже пересчитаны руками по последовательной логике.
ok(near(cor["n_w"], 28.5, 0.3),
   "FC6: N_в угл 28.5 по floor-правилу (их непослед. 25.0) (%s)" %
   cor["n_w"])
ok(rel(chk(row, "анкер")["value"], 72.4, 0.02) and
   rel(chk(cor, "анкер")["value"], 91.2, 0.02) and
   near(chk(row, "анкер")["limit"], 130.6, 0.2),
   "FC6: анкер 72.4/91.2<=130.6 (e3/e4=12/25; их угол 86.5) (%s/%s)" % (
       chk(row, "анкер")["value"], chk(cor, "анкер")["value"]))
ok(rel(chk(row, "кронштейн 1-1")["value"], 615, 0.02) and
   rel(chk(cor, "кронштейн 1-1")["value"], 1047.6, 0.02),
   "FC6: кронштейн 1-1 615/1047.6 (их угол 934) (%s/%s)" % (
       chk(row, "кронштейн 1-1")["value"], chk(cor, "кронштейн 1-1")["value"]))
ok(rel(chk(row, "удлинитель")["value"], 303.8, 0.02) and
   rel(chk(cor, "удлинитель")["value"], 531.3, 0.02),
   "FC6: удлинитель 303.8/531.3 (их угол 471.2) (%s/%s)" % (
       chk(row, "удлинитель")["value"], chk(cor, "удлинитель")["value"]))
ok(rel(chk(row, "профиль σ")["value"], 134.9, 0.02),
   "FC6: профиль ЗП σ=134.9 (0.125 двухпролётная) (%s)" %
   chk(row, "профиль σ")["value"])
ok(chk(row, "профиль f")["value"] <= 0.1 and
   near(chk(row, "профиль f")["limit"], 2.7, 0.1),
   "FC6: прогиб ~0.0<=2.7")
ok(rel(chk(row, "заклёпки срез")["value"], 8.6, 0.03) and
   rel(chk(row, "заклёпки смятие")["value"], 170.5, 0.03),
   "FC6: заклёпки 8.6; смятие 170.5")
ok(row["passed"] and cor["passed"], "FC6: 600/600 проходят")

# ── FC7: Регенбоген АКП тип-1 (местность A, ШП-60-20) — профиль при 800
REG = dict(scheme="vertical", w0=30, terrain="A", height=28,
           q_clad=7.2, gamma_clad=1.2, q_rails=1.22, offset=400,
           na_max=4000, bracket="КР1-85", extender="УК-85-1,2",
           profile="ШП-60-20-1,2", b_row=900, b_corner=900,
           rail_len=3000)
row = calc_chain(REG, 800, "row")
ok(rel(chk(row, "профиль σ")["value"], 698.4, 0.02),
   "FC7: профиль σ=698.4 (тип A: k10=1.0 ζ10=0.76 α=0.15) (%s)" %
   chk(row, "профиль σ")["value"])
ok(near(chk(row, "профиль f")["value"], 0.9, 0.1), "FC7: прогиб 0.9")

# ── FC8: report() — обе зоны сразу; spans_const-правило
ok(spans_const("vertical", 3000, 800) == (0.100, 1.10, 0.00675),
   "FC8: 3000/800→3 пролёта")
ok(spans_const("vertical", 3000, 1200) == (0.125, 1.25, 0.0052),
   "FC8: 3000/1200→2 пролёта (Бугры/ФЦП-ШП)")
ok(spans_const("vertical", 3000, 450) == (0.106, 1.132, 0.0063),
   "FC8: 3000/450→multi")
ok(spans_const("interfloor", 2930, 2930) == (0.106, 1.132, 0.0063),
   "FC8: interfloor всегда multi")
ok(spans_const("ortho", 600, 400) == (0.125, 1.25, 0.0052),
   "FC8: ortho 600/400→max(2,1)=2")
rep = report(RESP, candidates=[400, 450, 500, 600, 700, 800])
ok(rep["row"]["step"] == 800 and rep["corner"]["step"] == 450,
   "FC8: report республиканской: 800/450 — КАК В ИХ ВЫВОДАХ (%s/%s)" % (
       rep["row"]["step"], rep["corner"]["step"]))
rep2 = report(NIZH, candidates=[200, 250, 300, 350, 400, 450, 500])
ok(rep2["row"]["step"] >= 350 and rep2["corner"]["step"] >= 200,
   "FC8: Нижнекаменская: >=350/>=200 на верхнем диапазоне (%s/%s)" % (
       rep2["row"]["step"], rep2["corner"]["step"]))

print("frame_calc: %d проверок OK" % _n)
