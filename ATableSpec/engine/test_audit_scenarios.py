# -*- coding: utf-8 -*-
"""Регресс-сценарии практики (аудит 05.07): эмуляция рабочего дня конструктора: витраж (стойки/ригели/крышки/заполнения),
пресеты формы, правки выражений руками, крайние случаи. Печать: [OK]/[BUG]/[EDGE]."""
import sys, io
sys.path.insert(0, __import__("os").path.dirname(__file__) or ".")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
from atspec_report import run_report, run_template, Obj, evaluate

def B(name, layer, **attrs):
    return {"name": name, "layer": layer, "attributes": {k.replace('_', ' ') if False else k: str(v) for k, v in attrs.items()}}

FINDINGS = []
def rep(tag, sid, msg):
    FINDINGS.append((tag, sid, msg))
    print(f"[{tag}] {sid}: {msg}")

# ── чертёж: витраж ──
stoiki = [
    B("dyn1", "RF-стойки", ИМЯ="С01", ПРОФ="КП45", Длина="3495.0000"),
    B("dyn1", "RF-стойки", ИМЯ="С02", ПРОФ="КП45", Длина="3495.0000"),
    B("dyn1", "RF-стойки", ИМЯ="С02", ПРОФ="КП45", Длина="3495.0000"),
    B("dyn1", "RF-стойки", ИМЯ="С03", ПРОФ="КП50", Длина="4120.5"),
]
rigeli = [
    B("dyn2", "RF-ригели", ИМЯ="Р-05", ПРОФ="КР40", Длина="1150"),
    B("dyn2", "RF-ригели", ИМЯ="Р-05", ПРОФ="КР40", Длина="1150"),
    B("dyn2", "RF-ригели", ИМЯ="Р-06", ПРОФ="КР40", Длина="980"),
]
zap = [
    B("gl", "RF-заполнения", МАРКИРОВКА="СП01", Visibility1="Стеклопакет", РАЗМЕР_ЗАП="750Х2050"),
    B("gl", "RF-заполнения", МАРКИРОВКА="СП01", Visibility1="Стеклопакет", РАЗМЕР_ЗАП="750Х2050"),
    B("gl", "RF-заполнения", МАРКИРОВКА="СП02", Visibility1="Стеклопакет", РАЗМЕР_ЗАП="900Х2050"),
]
ALL = stoiki + rigeli + zap

def sec_spec(layer, extra_cols=None):
    cols = ["=row", "=Object.«ИМЯ»", "=Object.«ПРОФ»", "=Object.«Длина»", "=Count", "=«шт.»"]
    return {"section_title": "", "hide_header": False,
            "header": ["№ п/п", "Наименование", "Артикул", "Длина, мм", "Колич.", "Ед. изм."],
            "columns": extra_cols or cols,
            "filter": [{"field": "Слой", "op": "=", "value": layer}],
            "group_by": 1, "sort_by": [1, "asc"], "total_row": False}

# S1 — базовая спецификация стоек
r = run_report(ALL, {"title": "Т", "sections": [sec_spec("RF-стойки")]})
rows = r["sections"][0]["rows"]
exp = [[1, 'С01', 'КП45', 3495, 1, 'шт.'], [2, 'С02', 'КП45', 3495, 2, 'шт.'], [3, 'С03', 'КП50', 4121, 1, 'шт.']]
rep("OK" if rows == exp else "BUG", "S1",
    f"спека стоек: группировка+count+sort → {rows if rows != exp else 'эталон'}"
    + ("" if rows == exp else f" ОЖИДАЛ {exp}"))

# S2 — сортировка марок без ведущих нулей
marks = [B("d", "L", ИМЯ=f"С{i}") for i in (1, 2, 10, 3)]
rws = run_template(marks, {"filter": [], "columns": ["=Object.«ИМЯ»"], "group_by": 0, "sort_by": (0, "asc")})
order = [x[0] for x in rws]
rep("OK" if order == ["С1", "С2", "С3", "С10"] else "BUG", "S2",
    f"сортировка марок С1,С2,С10,С3 → {order} (ожидание: натуральная С1,С2,С3,С10)")

# S3 — крышки из стоек: артикул-литерал, длина −150
sec = sec_spec("RF-стойки")
sec["columns"] = ["=row", "=Object.«ИМЯ»", "=«ВЯЗ 0.1»", "=Object.«Длина»-150", "=Count", "=«шт.»"]
rws = run_report(ALL, {"sections": [sec]})["sections"][0]["rows"]
ok = rws[0][2] == "ВЯЗ 0.1" and rws[0][3] == 3345 and rws[2][3] == 3971  # 4120.5-150=3970.5→3971
rep("OK" if ok else "BUG", "S3", f"крышки: литерал-артикул + Длина-150 → {rws[0][2]}, {rws[0][3]}, {rws[2][3]} (округление 3970.5→3971)")

# S4 — ригели −20 от габарита
sec = sec_spec("RF-ригели"); sec["columns"][3] = "=Object.«Длина»-20"
rws = run_report(ALL, {"sections": [sec]})["sections"][0]["rows"]
rep("OK" if rws[0][3] == 1130 and rws[1][3] == 960 else "BUG", "S4", f"ригели −20: {[x[3] for x in rws]}")

# S5 — раскрой: merged шапка, группировка по длине, числовая сортировка
cut = {"section_title": "", "hide_header": False, "header": ["Артикул 8 6000", ""],
       "header_merges": [[0, 1]], "columns": ["=Object.«Длина»", "=Count"],
       "filter": [{"field": "ПРОФ", "op": "=", "value": "КР40"}],
       "group_by": 0, "sort_by": [0, "desc"], "total_row": False}
rws = run_report(ALL, {"sections": [cut]})["sections"][0]["rows"]
rep("OK" if rws == [[1150, 2], [980, 1]] else "BUG", "S5", f"раскрой: {rws} (числовая сортировка длин desc)")

# S6 — заполнения: Ш/В из РАЗМЕР_ЗАП, площадь Col*Col*Count/1e6, ИТОГ
zsec = {"section_title": "", "hide_header": False,
        "header": ["№", "Тип", "Марка", "Ширина", "Высота", "Колич.", "Площадь, м²"],
        "columns": ["=row", "=Object.«Visibility1»", "=Object.«МАРКИРОВКА»",
                     "=Object.«Ширина»", "=Object.«Высота»", "=Count",
                     "=Col(4)*Col(5)*Count/1000000"],
        "filter": [{"field": "Слой", "op": "=", "value": "RF-заполнения"}],
        "group_by": 2, "sort_by": [2, "asc"], "total_row": True}
rws = run_report(ALL, {"sections": [zsec]})["sections"][0]["rows"]
# СП01: 750*2050*2/1e6=3.075 → «3,08»? 3.075 Decimal→3.08; СП02: 0.9*2.05=1.845→«1,85» (HALF_UP)
ok = rws[0][3] == 750 and rws[0][6] == "3,08" and rws[1][6] == "1,85"
tot = rws[-1]
ok_tot = tot[0] == "сумма" and tot[5] == 3 and tot[6] == "4,93"  # напечатанные 3,08+1,85=4,93
rep("OK" if ok and ok_tot else "BUG", "S6",
    f"заполнения: Ш={rws[0][3]}, S1={rws[0][6]}, S2={rws[1][6]}; ИТОГ={tot}")

# S7 — коллизия группировки: одна марка, разные размеры
zz = zap + [B("gl", "RF-заполнения", МАРКИРОВКА="СП01", Visibility1="Стеклопакет", РАЗМЕР_ЗАП="900Х2100")]
rws = run_template(zz, {k: zsec[k] for k in ("filter", "columns", "group_by", "sort_by")})
r0 = rws[0]
ok7 = r0[3] == "разн." and r0[4] == "разн." and r0[5] == 3 and r0[1] != "разн."
rep("OK" if ok7 else "BUG", "S7", f"СП01 с разными размерами: Ш={r0[3]!r}, В={r0[4]!r}, Тип={r0[1]!r}, n={r0[5]} (ожидание: «разн.» в Ш/В, тип однороден)")

# S8 — длина с пробелом/nbsp (ручной ATTRIB «3 495»)
b = Obj(B("d", "L", Длина="3 495"))
v = b.field("Длина")
flt_pass = run_template([B("d", "L", Длина="3 495")],
    {"filter": [{"field": "Длина", "op": "=", "value": "3495"}], "columns": ["=Object.«Длина»"], "group_by": None, "sort_by": None})
rep("OK" if v == 3495.0 and len(flt_pass) == 1 else "BUG", "S8",
    f"Длина='3 495' (пробел-разделитель): field→{v!r}, фильтр '=3495' нашёл {len(flt_pass)} строк (ожидание: 3495.0, 1 строка)")

# S9 — атрибута нет: Длина−20 на блоке без Длины
rws = run_template([B("d", "L", ИМЯ="Х")], {"filter": [], "columns": ["=Object.«Длина»-20"], "group_by": None, "sort_by": None})
rep("EDGE", "S9", f"нет атрибута Длина: ячейка '=Длина-20' → {rws[0][0]!r} (пусто МОЛЧА, без маркера ошибки)")

# S10 — =Object.Длина без «ёлочек» (привычка из СПДС)
rws = run_template(stoiki[:1], {"filter": [], "columns": ["=Object.Длина"], "group_by": None, "sort_by": None})
rep("OK" if rws[0][0] == 3495 else "BUG", "S10", f"=Object.Длина без «ёлочек» → {rws[0][0]!r} (ожидание: 3495, синтаксис СПДС)")

# S11 — незакрытая «ёлочка» в столбце группировки
try:
    rws = run_template(stoiki, {"filter": [], "columns": ["=Object.«ИМЯ"], "group_by": 0, "sort_by": None})
    ok11 = len(rws) == 1 and rws[0][0] == "#ВЫРАЖ?"
    rep("OK" if ok11 else "BUG", "S11", f"незакрытая «ёлочка» в group-столбце: не падает, ячейка={rws[0][0]!r}, групп={len(rws)} (None-ключ собрал всё в одну)")
except Exception as e:
    rep("BUG", "S11", f"незакрытая «ёлочка» в group-столбце РОНЯЕТ весь отчёт: {type(e).__name__}: {e}")

# S12 — group_by за пределами столбцов (правленый руками def)
try:
    rws = run_template(stoiki, {"filter": [], "columns": ["=row"], "group_by": 5, "sort_by": None})
    rep("OK" if len(rws) == 4 else "BUG", "S12", f"group_by=5 из 1 столбца → группировка снята, строк={len(rws)}")
except Exception as e:
    rep("BUG", "S12", f"group_by за пределами РОНЯЕТ отчёт: {type(e).__name__}")

# S13 — sort_by за пределами
try:
    rws = run_template(stoiki, {"filter": [], "columns": ["=row"], "group_by": None, "sort_by": (7, "asc")})
    rep("OK" if len(rws) == 4 else "BUG", "S13", f"sort_by=7 → сортировка пропущена, строк={len(rws)}")
except Exception as e:
    rep("BUG", "S13", f"sort_by за пределами РОНЯЕТ отчёт: {type(e).__name__}")

# S14 — contains, кириллица, регистр
rws = run_template(zap, {"filter": [{"field": "Visibility1", "op": "содержит", "value": "стекло"}],
                          "columns": ["=Count"], "group_by": None, "sort_by": None})
rep("OK" if len(rws) == 3 else "BUG", "S14", f"«содержит 'стекло'» по 'Стеклопакет' (регистр): {len(rws)} строк")

# S15 — числовой op на нечисловом поле
rws = run_template(stoiki + [B("d", "RF-стойки", ИМЯ="СХХ")],
    {"filter": [{"field": "Длина", "op": ">", "value": "1000"}], "columns": ["=Object.«ИМЯ»"], "group_by": None, "sort_by": None})
rep("OK" if len(rws) == 4 else "EDGE", "S15", f"фильтр Длина>1000, у одного блока Длины нет: {len(rws)} строк (безДлины отсеян молча)")

# S16 — ИТОГ: сумма raw vs сумма напечатанных
z3 = [B("g", "L", М="A", РАЗМЕР_ЗАП="615Х1000"), B("g", "L", М="Б", РАЗМЕР_ЗАП="615Х1000")]
rws = run_template(z3, {"filter": [], "columns": ["=Object.«М»", "=Object.«Ширина»*Object.«Высота»*Count/1000000"],
                         "group_by": 0, "sort_by": None, "total_row": True})
cells = [r[1] for r in rws]
rep("OK" if cells == ["0,62", "0,62", "1,24"] else "BUG", "S16",
    f"ячейки площади {cells[:-1]} → ИТОГ {cells[-1]} (ожидание: 1,24 = сумма НАПЕЧАТАННЫХ)")

# S17 — Count и Count()
rws = run_template(stoiki, {"filter": [], "columns": ["=Count", "=Count()"], "group_by": None, "sort_by": None})
rep("OK" if rws[0] == [1, 1] else "BUG", "S17", f"Count/Count(): {rws[0]}")

# S18 — нумерация после desc-сортировки
sec = sec_spec("RF-стойки"); sec["sort_by"] = [1, "desc"]
rws = run_report(ALL, {"sections": [sec]})["sections"][0]["rows"]
rep("OK" if [x[0] for x in rws] == [1, 2, 3] else "BUG", "S18", f"№п/п после desc: {[x[0] for x in rws]} ({[x[1] for x in rws]})")

# S19 — пустая выборка + ИТОГ
rws = run_template(ALL, {"filter": [{"field": "Слой", "op": "=", "value": "НЕТ-ТАКОГО"}],
                          "columns": zsec["columns"], "group_by": 2, "sort_by": None, "total_row": True})
rep("OK" if rws == [] else "BUG", "S19", f"пустая выборка с ИТОГ: rows={rws} (ожидание: пусто, без «сумма 0»)")

# S20 — Iff
rws = run_template(stoiki[:1], {"filter": [], "columns": ["=Iff(Object.«Длина»>3000,«длинная»,«короткая»)"], "group_by": None, "sort_by": None})
rep("OK" if rws[0][0] == "длинная" else "BUG", "S20", f"Iff: {rws[0][0]}")

# S21 — деление на ноль
rws = run_template(stoiki[:1], {"filter": [], "columns": ["=Object.«Длина»/0"], "group_by": None, "sort_by": None})
rep("OK" if rws[0][0] is None else "EDGE", "S21", f"деление на 0 → {rws[0][0]!r} (не роняет)")

# S22 — сравнение как значение ячейки
rws = run_template(stoiki[:1], {"filter": [], "columns": ["=Object.«ИМЯ»=«С01»"], "group_by": None, "sort_by": None})
rep("EDGE", "S22", f"'=ИМЯ=«С01»' в ячейке → {rws[0][0]!r} (bool англ. текстом)")

# S23 — total label съедает первый count-less столбец
sec = {"filter": [], "columns": ["=row", "=Object.«ИМЯ»", "=Count"], "group_by": 1, "sort_by": None, "total_row": True}
rws = run_template(stoiki, sec)
rep("OK", "S23", f"ИТОГ-строка: {rws[-1]} (label в первый столбец, счёт под Count)")

# S24 — фильтр по значению с точкой «0.1» vs «0,1»
bb = [B("d", "L", АРТ="0.1"), B("d", "L", АРТ="0,1")]
rws = run_template(bb, {"filter": [{"field": "АРТ", "op": "=", "value": "0.1"}], "columns": ["=Count"], "group_by": None, "sort_by": None})
rep("OK" if len(rws) == 2 else "EDGE", "S24", f"АРТ '0.1'/'0,1' против «=0.1»: {len(rws)} строк (числовое сведение)")

# S25 — «разн.» НЕ срабатывает на однородной группе (С02 ×2 идентичны)
sec = sec_spec("RF-стойки")
rws = run_report(ALL, {"sections": [sec]})["sections"][0]["rows"]
ok = all("разн." not in [str(c) for c in r] for r in rws)
rep("OK" if ok else "BUG", "S25", f"однородные группы без «разн.»: {rws[1]}")

# S26 — маркер не трогает литералы и валидные формулы
rws = run_template(stoiki[:1], {"filter": [], "columns": ["=«шт.»", "Примечание", "=Object.«Длина»-20"], "group_by": None, "sort_by": None})
rep("OK" if rws[0] == ["шт.", "Примечание", 3475] else "BUG", "S26", f"литералы/формулы без маркера: {rws[0]}")

# S27 — натуральная сортировка с дефисом и разной длиной чисел
mk = [B("d", "L", ИМЯ=x) for x in ("Р-10", "Р-05", "Р-6")]
rws = run_template(mk, {"filter": [], "columns": ["=Object.«ИМЯ»"], "group_by": 0, "sort_by": (0, "asc")})
order = [x[0] for x in rws]
rep("OK" if order == ["Р-05", "Р-6", "Р-10"] else "BUG", "S27", f"Р-05/Р-6/Р-10 → {order}")

# S28 — фильтр «=» с допуском: double-хвосты динпараметров (видео Алексея 06.07)
dd = [B("g", "L", Ширина="1499.9999999998"), B("g", "L", Ширина="749.5")]
r1 = run_template(dd, {"filter": [{"field": "Ширина", "op": "=", "value": "1500"}], "columns": ["=Count"], "group_by": None, "sort_by": None})
r2 = run_template(dd, {"filter": [{"field": "Ширина", "op": "=", "value": "750"}], "columns": ["=Count"], "group_by": None, "sort_by": None})
rep("OK" if len(r1) == 1 and len(r2) == 0 else "BUG", "S28",
    f"«=1500» ловит 1499.9999999998 ({len(r1)} стр), «=750» НЕ ловит 749.5 ({len(r2)} стр)")

# S29 — «Имя блока» — отдельное поле, не атрибут ИМЯ (список имён без ПРОФ)
bb = [{"name": "КП45", "layer": "RF-стойки", "attributes": {"ИМЯ": "С01"}}]
r1 = run_template(bb, {"filter": [{"field": "Имя блока", "op": "=", "value": "КП45"}], "columns": ["=Object.«ИМЯ»"], "group_by": None, "sort_by": None})
r2 = run_template(bb, {"filter": [{"field": "ИМЯ", "op": "=", "value": "КП45"}], "columns": ["=Count"], "group_by": None, "sort_by": None})
rep("OK" if len(r1) == 1 and r1[0][0] == "С01" and len(r2) == 0 else "BUG", "S29",
    f"поле «Имя блока»=КП45 находит блок (атрибут ИМЯ={r1[0][0] if r1 else '—'}); атрибут ИМЯ=КП45 не находит ({len(r2)} стр)")

# S30 — «Имя блока» в выражении столбца
bb = [{"name": "КП45", "layer": "L", "attributes": {"ИМЯ": "С01"}}]
rws = run_template(bb, {"filter": [], "columns": ["=Object.«Имя блока»", "=Object.«ИМЯ»"], "group_by": None, "sort_by": None})
rep("OK" if rws[0] == ["КП45", "С01"] else "BUG", "S30", f"столбцы Имя блока/ИМЯ → {rws[0]}")

# S31 — «≠» с допуском: хвостатое значение НЕ проходит ≠
dd = [B("g", "L", Ширина="1499.9999999998")]
rws = run_template(dd, {"filter": [{"field": "Ширина", "op": "≠", "value": "1500"}], "columns": ["=Count"], "group_by": None, "sort_by": None})
rep("OK" if len(rws) == 0 else "BUG", "S31", f"«≠1500» на 1499.9999999998 → {len(rws)} строк (допуск работает и в ≠)")

# S32 — Visibility1 как обычное поле блока
vv = [B("g", "L", Visibility1="Стеклопакет", МАРКИРОВКА="СП01")]
rws = run_template(vv, {"filter": [], "columns": ["=Object.«Visibility1»"], "group_by": None, "sort_by": None})
rep("OK" if rws[0][0] == "Стеклопакет" else "BUG", "S32", f"Visibility1 → {rws[0][0]!r}")

# S33 — зеркало ReportCommands.NumClean (C#): контракт числовой чистки формы.
# Логика ДОЛЖНА совпадать с C#: трогаем только строки с '.' или ','; парс с чисткой
# пробелов/nbsp и ','→'.'; почти-целое (1e-6 отн.) → целое; иначе — исходник как есть.
def num_clean(v):
    if not v or ('.' not in v and ',' not in v):
        return v
    t = v.replace("\u00a0", "").replace(" ", "").replace(",", ".")
    try:
        x = float(t)
    except ValueError:
        return v
    r = round(x)
    if abs(x - r) <= 1e-6 * max(1.0, abs(x)):
        return "%d" % r
    return v
cases = [("01", "01"), ("С01", "С01"), ("0,1", "0,1"), ("749.5", "749.5"),
         ("1499.9999999998", "1500"), ("1200.0000000002", "1200"),
         ("3495.0000", "3495"), ("3 495,00", "3495"), ("2050", "2050"), ("", "")]
bad = [(inp, num_clean(inp), exp) for inp, exp in cases if num_clean(inp) != exp]
rep("OK" if not bad else "BUG", "S33", f"NumClean-зеркало: {'все ' + str(len(cases)) + ' кейсов' if not bad else bad}")

print()
print("── СВОДКА ──")
for tag in ("BUG", "EDGE"):
    for t, sid, msg in FINDINGS:
        if t == tag:
            print(f"  [{t}] {sid}")
