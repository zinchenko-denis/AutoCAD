# -*- coding: utf-8 -*-
"""Анализ КЛЯММЕРОВ полигона Германа (В-аа) + стыков направляющих (В-ад).

Запуск руками (нужен дамп из atspec-testdata):

    PYTHONUTF8=1 python3 analiz_klu_poligon.py [путь/к/poligon_tree.json]

Итог анализа 07.08a (числа ниже воспроизводятся этим скриптом):

── В-аа: правило типов кляммеров, выведенное из полигона ────────────
Кляммер стоит на КАЖДОМ пересечении оси направляющей с горизонтальным
швом облицовки (это у нас уже так); ТИП определяется положением оси
относительно ПЛИТ (на рядовой плиты крупноформатные ЦЕЛЬНЫЕ разной
ширины: 1122/1200/1072/896/843/608/314 — карта *U-блоков, никаких
скрытых рустов внутри):
  1. ось на вертикальном шве, плиты с ОБЕИХ сторон → РЯДОВОЙ
     (вставки КЛР/КЛС у Германа стоят на −56 от оси шва — база блока,
     совпадает с нашим CenterOffsetX 04.08h);
  2. ось в ПОЛЕ плиты (середина широкой плиты, оконная ось ±100,
     шов подрезки у откоса — сбоку проём, край зоны ±100) →
     УГЛОВОЙ КЛУ-1.1 rot=270. «Пары вдоль рустов» из наблюдения
     02.08 — это пары СОСЕДНИХ рядов: руст у грани проёма (−4) +
     оконная ось (−100) в 96 мм друг от друга;
  3. первый ряд снизу зоны и первый над проёмом → СТАРТОВЫЙ (у нас ✓);
  4. первый шов над стыком направляющих → КОМБИНИРОВАННЫЙ, ровно на
     оси шва (у нас ✓; на орто-сверке у нас 0 комбов только потому,
     что floors_y=[] не давал стыков);
  5. НОВОЕ: держатели ВЕРХНИХ кромок, УГЛОВОЙ rot=0:
     верх зоны — на каждом вертикальном шве верхнего ряда + края
     (орто: 27 = 25 швов + 2 края); верх подоконного ряда (низ
     проёма) — на швах в створе проёма, вкл. грани (орто: 4 X × 20
     низов проёмов = 80);
  6. НОВОЕ: ряды угловых у откосов идут ПО ВСЕЙ ВЫСОТЕ зоны (профиль
     на шве подрезки у грани — сквозной), не только в диапазоне
     проёма: орто 8 столбиков × 20 + 2 края × 21 = 202.
Проверка на орто: 202 + 80 + 27 = 309 КЛУ ✓ (бьётся точно).
Наш недобор 566 против 737 — п.5 (80+27), сквозные столбики п.6 и
отсутствие стыков (комбы 61).
ОТКРЫТОЕ (спросить с подтверждением правила): на рядовой в створе
окон на серединах плит у низов проёмов стоят ПАРЫ КЛУ rot=270 c
шагом 188 мм по Y (второй знак — что держит?); точная Y-посадка
верхнекромочных (39424.7 при верхе ряда 39336.3+600).

── В-ад: стыки направляющих и кронштейны — правило полигона ─────────
В ПОЛЕ направляющие Германа — хлысты 3000 с зазором 10 ОТ НИЗА ЗОНЫ
(низы кусков 24070.3 + 3010n), БЕЗ привязки к перекрытиям; кронштейны
на каждом куске «300 от торцов + равномерно ≤800» (буква ТЗ 26.07).
Его цепочка 24370.3 → 25170.3 → 25970.3 → 26770.3 → 27380.3 → ... =
(низ хлыста+300, +800×3 = верх хлыста−300), затем через стык 610 =
300+10+300 ✓. «Пара −300/+310 у стыка» — это ТОРЦЫ соседних хлыстов,
отдельного правила пары нет. На оконных осях (±100) куски идут ВСЮ
высоту со стыками на «низ проёма − 50» (низы кусков 24470.3+3000n),
т.е. оконная направляющая НЕ [низ−50, верх+50], а сквозная. На осях
в створе окна — куски между проёмами (низы 24070.3, 26720.3+3000n =
от верха проёма). Вопрос Герману: в боевых проектах с реальными
отметками перекрытий стык направляющих и несущий кронштейн садятся
на отметку (В15, Ленпроспект), или всегда хлысты от низа как на
полигоне? Что тогда «несущий» в ведомости?
"""
import os
import sys
import json
import collections

X_RYAD = 15211.0
X_ORT = 41645.0
WIN_W, WIN_H = 1800.0, 2200.0
TOL = 1.0


def bbox(pts):
    xs = [a for a, b in pts]
    ys = [b for a, b in pts]
    return min(xs), min(ys), max(xs), max(ys)


def zone_of(x):
    return "RYAD" if x < X_RYAD else ("MEZH" if x < X_ORT else "ORTO")


def steps(vals):
    v = sorted(set(round(a, 1) for a in vals))
    return collections.Counter(round(b - a) for a, b in zip(v, v[1:]))


def run(path):
    T = json.load(open(path, encoding="utf-8"))
    ins = [i for i in T["inserts"] if i["x"] > -20000]
    polys = [p for p in T["polys"] if p["par"] == "MS"]
    kl = [i for i in ins if i["lay"] == "!5_Кляммеры"]

    print("=== Кляммеры по участкам ===")
    for z in ("RYAD", "MEZH", "ORTO"):
        zz = [i for i in kl if zone_of(i["x"]) == z]
        print(" %s %d %s" % (z, len(zz), dict(
            collections.Counter(i["n"].split("!")[0] for i in zz))))

    # ── ОРТО: семейства КЛУ ─────────────────────────────────────
    wins = []
    for p in polys:
        x0, y0, x1, y1 = bbox(p["pts"])
        if x0 > X_ORT and abs((x1 - x0) - WIN_W) < TOL \
                and abs((y1 - y0) - WIN_H) < TOL:
            wins.append((round(x0, 1), round(y0, 1),
                         round(x1, 1), round(y1, 1)))
    wl = sorted(set(w[0] for w in wins))
    wr = sorted(set(w[2] for w in wins))
    klu = [i for i in kl if zone_of(i["x"]) == "ORTO" and "КЛУ" in i["n"]]
    r270 = [i for i in klu if round(i["r"]) % 360 == 270]
    r0 = [i for i in klu if round(i["r"]) % 360 == 0]
    top = [i for i in r0 if i["y"] > 39000]
    sub = [i for i in r0 if i["y"] <= 39000]
    print("\n=== ОРТО: КЛУ %d = столбики rot=270 %d + подоконные %d "
          "+ верхняя кромка %d ===" % (len(klu), len(r270),
                                       len(sub), len(top)))
    bx = collections.Counter(round(i["x"], 1) for i in r270)
    print(" столбики (X: n):")
    for x in sorted(bx):
        near = ""
        for g in wl:
            if abs(x - g) < 60:
                near = "лев. грань проёмов %.1f" % g
        for g in wr:
            if abs(x - g) < 60:
                near = "прав. грань проёмов %.1f" % g
        print("   X=%.1f n=%d  %s" % (x, bx[x], near or "край зоны"))
    print(" подоконные Y-уровни:", sorted(set(round(i["y"], 1)
                                              for i in sub)))
    print(" верхняя кромка: %d позиций на Y=%s" %
          (len(top), sorted(set(round(i["y"], 1) for i in top))))

    # ── РЯДОВАЯ: оси × типы ─────────────────────────────────────
    print("\n=== РЯДОВАЯ: типы кляммеров по осям направляющих ===")
    rah = [i for i in ins
           if i["lay"] == "_01_ПС_НАПРАВЛЯЮЩИЕ" and i["x"] < X_RYAD]
    rx = sorted(set(round(i["x"], 1) for i in rah))
    obl = [i for i in ins
           if i["lay"] == "Облицовка 4" and i["x"] < X_RYAD]
    seams = set(round(i["x"] - 4.0, 1) for i in obl)
    winsR = []
    for p in polys:
        x0, y0, x1, y1 = bbox(p["pts"])
        if x0 < X_RYAD and abs((x1 - x0) - WIN_W) < TOL \
                and abs((y1 - y0) - WIN_H) < TOL:
            winsR.append((x0, y0, x1, y1))
    gl = sorted(set(round(w[0], 1) for w in winsR))
    gr = sorted(set(round(w[2], 1) for w in winsR))

    def axis_kind(x):
        for g in gl:
            if abs(x - (g - 100)) < 2:
                return "ОКОННАЯ-Л"
        for g in gr:
            if abs(x - (g + 100)) < 2:
                return "ОКОННАЯ-П"
        if any(abs(x - s) < 2 for s in seams):
            return "ШОВ(руст)"
        return "поле плиты/край"

    tab = collections.defaultdict(collections.Counter)
    klR = [i for i in kl if i["x"] < X_RYAD]
    for i in klR:
        ax = min(rx, key=lambda a: abs(i["x"] - a))
        short = ("КЛУ" if "КЛУ" in i["n"] else
                 "КЛР" if "КЛР-1-н" in i["n"] else
                 "КЛС" if "КЛС" in i["n"] else "КОМБ")
        tab[ax][(short, round(i["r"]) % 360)] += 1
    kinds = collections.defaultdict(collections.Counter)
    for ax in rx:
        for k, n in tab[ax].items():
            kinds[axis_kind(ax)]["%s/%d" % k] += n
    for kind in sorted(kinds):
        print(" %-16s %s" % (kind, dict(kinds[kind])))

    # ── Стыки направляющих (В-ад) ───────────────────────────────
    print("\n=== РЯДОВАЯ: низы кусков направляющих (В-ад) ===")
    byx = collections.defaultdict(list)
    for i in rah:
        byx[round(i["x"], 1)].append(round(i["y"], 1))
    in_stvor = lambda x: any(a - 105 < x < b + 105
                             for a, b in zip(gl, gr))
    pole = [x for x in byx
            if axis_kind(x) == "поле плиты/край" and not in_stvor(x)]
    ys = sorted(byx[pole[0]]) if pole else []
    print(" поле (X=%.1f): низы кусков %s — хлысты 3000, зазор 10, "
          "от низа зоны" % (pole[0], ys))
    stv = [x for x in byx
           if axis_kind(x) == "поле плиты/край" and in_stvor(x)]
    ys3 = sorted(byx[stv[0]]) if stv else []
    print(" в створе окна (X=%.1f): низы кусков %s — куски между "
          "проёмами (от верха проёма)" % (stv[0], ys3))
    okn = [x for x in byx if axis_kind(x).startswith("ОКОННАЯ")]
    ys2 = sorted(byx[okn[0]]) if okn else []
    print(" оконная (X=%.1f): низы кусков %s — стыки на «низ "
          "проёма − 50», сквозная" % (okn[0], ys2))
    krh = [i for i in ins if i["lay"] == "_-_4_Кронштейны"
           and i["x"] < X_RYAD]
    col = collections.defaultdict(list)
    for i in krh:
        col[round(i["x"], 1)].append(round(i["y"], 1))
    full = max(col.values(), key=len)
    print(" цепочка кронштейнов длинной колонки:", sorted(full))
    print("   = на каждом хлысте (низ+300, шаг ≤800, верх−300); "
          "через стык 610 = 300+10+300")


if __name__ == "__main__":
    d = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.expanduser("~"),
        "atspec-testdata/dxf/facades/poligon_0208/poligon_tree.json")
    run(d)
