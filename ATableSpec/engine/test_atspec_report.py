# -*- coding: utf-8 -*-
"""Тест ядра atspec_report. Самодостаточен (inline-фикстуры реальных значений),
плюс опциональный прогон на живых DXF В-13/В-32, если они доступны."""
import os
import sys
try:
    sys.stdout.reconfigure(encoding="utf-8")   # Windows CI: stdout по умолчанию cp1252
except Exception:
    pass
from atspec_report import run_report, evaluate, Obj


def stoiki(mults, proff, length_of):
    recs = []
    for mark, n in mults.items():
        for _ in range(n):
            recs.append({"name": "*U", "layer": "RF-стойки",
                         "attributes": {"ИМЯ": mark, "ПРОФ": proff,
                                        "ДЛИНА": f"{length_of(mark):.2f}"}})
    return recs


def base_tmpl(layer):
    return {"filter": [{"field": "Слой", "op": "=", "value": layer}],
            "columns": ["=row-1", "=Object.«ИМЯ»", "=Object.Name",
                        "=Object.«Длина»", "=Count", "=«шт.»"],
            "group_by": 1, "sort_by": (1, "asc")}


def kryshka_tmpl(layer, article, offset):
    return {"filter": [{"field": "Слой", "op": "=", "value": layer}],
            "columns": ["=row-1", "=Object.«ИМЯ»", f"=«{article}»",
                        f"=Object.«Длина»-{offset}", "=Count", "=«шт.»"],
            "group_by": 1, "sort_by": (1, "asc")}


def find(rep, mark):
    for r in rep["rows"]:
        if r[1] == mark:
            return r
    raise AssertionError(f"марка {mark} не найдена")


# ───────── В-13 (значения из живого файла) ─────────
m13 = {"С01": 1, "С02": 2, "С03": 2, "С04": 2, "С05": 1,
       "С06": 1, "С07": 1, "С08": 1, "С09": 1, "С10": 1, "С11": 1, "С12": 1, "С13": 1}
L13 = lambda m: 3495 if m in ("С01", "С02", "С03", "С04", "С05") else 4165
st13 = stoiki(m13, "01_03_06", L13)

b13 = run_report(st13, {"templates": [base_tmpl("RF-стойки")]})
assert find(b13, "С01")[2] == "01_03_06" and find(b13, "С01")[3] == 3495
assert find(b13, "С02")[4] == 2 and find(b13, "С06")[3] == 4165
assert find(b13, "С01")[5] == "шт."

k13 = run_report(st13, {"templates": [kryshka_tmpl("RF-стойки", "01 02 04", 150)]})
assert find(k13, "С01")[3] == 3345 and find(k13, "С06")[3] == 4015     # Длина-150
assert find(k13, "С01")[2] == "01 02 04"                               # литерал

both = run_report(st13, {"templates": [kryshka_tmpl("RF-стойки", "01 06 02", 150),
                                       kryshka_tmpl("RF-стойки", "01 05 01", 75)]})
assert len(both["rows"]) == 2 * len(m13)                               # крышка+прижим на марку

# ───────── В-32 (значения из живого файла) ─────────
st32 = stoiki({"С1": 18, "С2": 36, "С3": 18}, "17_07_04", lambda m: 2748)
rg32 = [{"name": "*U", "layer": "RF-ригеля",
         "attributes": {"ИМЯ": m, "ПРОФ": "17_06_01", "ДЛИНА": f"{l:.2f}"}}
        for m, l, n in [("Р01", 540.5, 54), ("Р01.2", 540.5, 36), ("Р02*", 700.5, 36)]
        for _ in range(n)]

b32s = run_report(st32, {"templates": [base_tmpl("RF-стойки")]})
assert find(b32s, "С1")[2] == "17_07_04" and find(b32s, "С1")[3] == 2748
assert find(b32s, "С2")[4] == 36
b32r = run_report(rg32, {"templates": [base_tmpl("RF-ригеля")]})
assert find(b32r, "Р01")[2] == "17_06_01" and find(b32r, "Р01")[3] == 540.5

# ───────── выражения: арифметика / Iff / конкатенация / Count ─────────
o = Obj({"layer": "RF-стойки", "attributes": {"ИМЯ": "С6", "ПРОФ": "17_07_04", "ДЛИНА": "4165.00"}})
assert evaluate('=Object.«Длина»-150', o, [o], 1) == 4015.0
assert evaluate('=Iff(Object.«Длина»>3000, «длинная», «короткая»)', o, [o], 1) == "длинная"
assert evaluate('="арт-"+Object.«ИМЯ»', o, [o], 1) == "арт-С6"
assert evaluate('=Count', o, [o, o, o], 1) == 3

# ───────── регресс по фиксам: нумерация / литерал / не содержит ─────────
# п.1: №п/п (=row-1) идёт по порядку ПОСЛЕ сортировки по наименованию
nums = [r[0] for r in b13["rows"]]
assert nums == list(range(0, len(b13["rows"]))), f"№п/п не по порядку: {nums}"
assert [r[1] for r in b13["rows"]] == sorted(r[1] for r in b13["rows"])

# п.4: ячейка без «=» — литерал; пустая — ""; кириллица без кавычек не роняет
assert evaluate("Примечание", o, [o], 1) == "Примечание"
assert evaluate("", o, [o], 1) == ""
_lit = run_report(st13, {"templates": [{
    "filter": [{"field": "Слой", "op": "=", "value": "RF-стойки"}],
    "columns": ["=row-1", "=Object.«ИМЯ»", "Примечание", ""],
    "group_by": 1, "sort_by": (1, "asc")}]})
assert find(_lit, "С01")[2] == "Примечание" and find(_lit, "С01")[3] == ""

# п.6: «не содержит» — обратное «содержит»
from atspec_report import _passes
_f = [{"field": "ИМЯ", "op": "не содержит", "value": "0"}]
assert _passes(Obj({"attributes": {"ИМЯ": "Р1"}}), _f) is True
assert _passes(Obj({"attributes": {"ИМЯ": "С01"}}), _f) is False

print("UNIT: все ассерты прошли (В-13, В-32, выражения).")

# ───────── контракт обмена: engine_json с action="report" ─────────
import dxf_spec  # noqa: E402
_payload = {"blocks": st13, "action": "report",
            "report": {"title": "Стойки", "header": ["№", "Наим", "Арт", "Длина", "Кол", "Ед"],
                       "templates": [base_tmpl("RF-стойки")]}}
_out = dxf_spec.engine_json(_payload, {})          # config={} — отчёт идёт по сырым записям
assert _out["ok"] and "report" in _out
assert any(r[1] == "С01" and r[3] == 3495 for r in _out["report"]["rows"])
print("CONTRACT: engine_json(action='report') отдаёт отчёт. OK")

# ───────── опциональный прогон на живых DXF ─────────
files = {"В-13": "/mnt/user-data/uploads/КМД_В-13_ИЗМ.dxf",
         "В-32": "/mnt/user-data/uploads/КМД_В32_2.dxf"}
present = {k: v for k, v in files.items() if os.path.exists(v)}
if present:
    import ezdxf
    def recs(path, layer):
        msp = ezdxf.readfile(path).modelspace()
        return [{"name": e.dxf.name, "layer": e.dxf.layer,
                 "attributes": {a.dxf.tag: (a.dxf.text or "") for a in e.attribs}}
                for e in msp.query("INSERT") if e.dxf.layer == layer]
    for name, path in present.items():
        rep = run_report(recs(path, "RF-стойки"), {"templates": [base_tmpl("RF-стойки")]})
        print(f"INTEGRATION {name}: стоек-марок {len(rep['rows'])}, "
              f"пример {rep['rows'][0][1]}/{rep['rows'][0][2]}/{rep['rows'][0][3]}")
else:
    print("INTEGRATION: живые DXF не примонтированы — пропуск (юнит-фикстуры покрывают логику).")
