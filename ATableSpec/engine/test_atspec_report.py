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
assert find(b32r, "Р01")[2] == "17_06_01" and find(b32r, "Р01")[3] == 541   # 540.5 -> 541 (округление)

# ───────── выражения: арифметика / Iff / конкатенация / Count ─────────
o = Obj({"layer": "RF-стойки", "attributes": {"ИМЯ": "С6", "ПРОФ": "17_07_04", "ДЛИНА": "4165.00"}})
assert evaluate('=Object.«Длина»-150', o, [o], 1) == 4015.0
assert evaluate('=Iff(Object.«Длина»>3000, «длинная», «короткая»)', o, [o], 1) == "длинная"
assert evaluate('="арт-"+Object.«ИМЯ»', o, [o], 1) == "арт-С6"
assert evaluate('=Count', o, [o, o, o], 1) == 3

# ───────── заполнения: Ширина/Высота из РАЗМЕР_ЗАП ("ШхВ", разделитель — кир. Х) ─────────
z = Obj({"layer": "RF-заполнения", "attributes": {"МАРКИРОВКА": "ст2", "РАЗМЕР_ЗАП": "670Х1170"}})
assert evaluate("=Object.«Ширина»", z, [z], 1) == 670, evaluate("=Object.«Ширина»", z, [z], 1)
assert evaluate("=Object.«Высота»", z, [z], 1) == 1170
# спецификация заполнений: марка / ширина / высота / кол-во (=row 1-based)
_zrep = run_report(
    [{"layer": "RF-заполнения", "attributes": {"МАРКИРОВКА": "ст2", "РАЗМЕР_ЗАП": "670Х1170"}},
     {"layer": "RF-заполнения", "attributes": {"МАРКИРОВКА": "ст2", "РАЗМЕР_ЗАП": "670Х1170"}},
     {"layer": "RF-заполнения", "attributes": {"МАРКИРОВКА": "ст1", "РАЗМЕР_ЗАП": "510Х1170"}}],
    {"templates": [{"filter": [{"field": "Слой", "op": "=", "value": "RF-заполнения"}],
                    "columns": ["=row", "=Object.«МАРКИРОВКА»", "=Object.«Ширина»",
                                "=Object.«Высота»", "=Count"],
                    "group_by": 1, "sort_by": (1, "asc")}]})
_st2 = next(r for r in _zrep["rows"] if r[1] == "ст2")
assert _st2[2] == 670 and _st2[3] == 1170 and _st2[4] == 2, _st2

# ───────── нормализация имени поля: динам. параметр с иным регистром/пробелом ─────────
_d1 = Obj({"layer": "RF-доборники", "attributes": {"Длина ": "580.50", "ПРОФ": "01_40_07"}})
assert evaluate("=Object.«Длина»", _d1, [_d1], 1) == 580.5, "хвостовой пробел в имени параметра"
_d2 = Obj({"layer": "x", "attributes": {"ширина": "670", "высота": "1170"}})
assert evaluate("=Object.«Ширина»", _d2, [_d2], 1) == 670, "нижний регистр имени параметра"
assert evaluate("=Object.«Высота»", _d2, [_d2], 1) == 1170

# округление дробной длины динам. параметра до целого в строке отчёта (B)
_drep = run_report(
    [{"layer": "RF-доборники", "attributes": {"ПРИВЯЗКА": "Р01", "Длина": "3494.9997"}}],
    {"templates": [{"filter": [{"field": "Слой", "op": "=", "value": "RF-доборники"}],
                    "columns": ["=row", "=Object.«ПРИВЯЗКА»", "=Object.«Длина»", "=Count"]}]})
assert _drep["rows"][0][2] == 3495, _drep["rows"][0]

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

# п.7: фильтр «=»/«≠» по числовому полю — численно, формат не мешает (баг фильтра по длине)
#      длина-атрибут «3495.00» → float → str «3495.0»; раньше «3495.0»=="3495.00" → ноль строк
_st = Obj({"attributes": {"ИМЯ": "С01", "ДЛИНА": "3495.00"}})
assert _passes(_st, [{"field": "Длина", "op": "=", "value": "3495.00"}]) is True   # формат выпадушки
assert _passes(_st, [{"field": "Длина", "op": "=", "value": "3495"}])    is True   # целое (как в таблице)
assert _passes(_st, [{"field": "Длина", "op": "=", "value": "3495,00"}]) is True   # запятая-десятичная
assert _passes(_st, [{"field": "Длина", "op": "=", "value": "3494"}])    is False  # другая длина — мимо
assert _passes(_st, [{"field": "Длина", "op": "≠", "value": "3000"}])    is True
# артикул (не число) — строгое строковое сравнение, в число не схлопывать
_pr = Obj({"attributes": {"ПРОФ": "01_03_06"}})
assert _passes(_pr, [{"field": "ПРОФ", "op": "=", "value": "01_03_06"}]) is True
assert _passes(_pr, [{"field": "ПРОФ", "op": "=", "value": "010306"}])   is False

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

# ───────── Фаза 2: несколько секций в одной таблице (стойки + ригеля) ─────────
def _sec(title, layer, cols, header):
    return {"section_title": title, "header": header, "hide_header": False,
            "header_merges": [], "columns": cols,
            "filter": [{"field": "Слой", "op": "=", "value": layer}],
            "group_by": 1, "sort_by": (1, "asc")}

_cols6 = ["=row", "=Object.«ИМЯ»", "=Object.Name", "=Object.«Длина»", "=Count", "=«шт.»"]
_hdr6 = ["№", "Наим", "Арт", "Длина", "Кол", "Ед"]
_secrep = run_report(st32 + rg32, {"title": "СПЕЦИФИКАЦИЯ", "scale": 100, "hide_title": False,
    "sections": [_sec("Стойки", "RF-стойки", _cols6, _hdr6),
                 _sec("Ригеля", "RF-ригеля", _cols6, _hdr6)]})
assert "sections" in _secrep and len(_secrep["sections"]) == 2, list(_secrep.keys())
_s0, _s1 = _secrep["sections"]
assert _s0["title"] == "Стойки" and _s1["title"] == "Ригеля"
# источник = фильтр секции: в секции 0 только стойки, в секции 1 только ригеля
assert len(_s0["rows"]) == 3 and all(r[1].startswith("С") for r in _s0["rows"]), _s0["rows"]
assert len(_s1["rows"]) == 3 and all(r[1].startswith("Р") for r in _s1["rows"]), _s1["rows"]
# нумерация =row внутри секции своя (каждая стартует с 1)
assert [r[0] for r in _s0["rows"]] == [1, 2, 3] and [r[0] for r in _s1["rows"]] == [1, 2, 3]
# плоско = конкатенация секций; поля секции пробрасываются
assert _secrep["rows"] == _s0["rows"] + _s1["rows"]
assert _s0["hide_header"] is False and _s0["header"][0] == "№"

# секции с РАЗНЫМ числом столбцов (ширина итоговой таблицы = макс по секциям)
_difrep = run_report(st32 + rg32, {"sections": [
    _sec("A", "RF-стойки", ["=row", "=Object.«ИМЯ»"], ["№", "Наим"]),
    _sec("B", "RF-ригеля", ["=row", "=Object.«ИМЯ»", "=Object.Name", "=Object.«Длина»"], ["№", "Наим", "Арт", "Длина"])]})
assert len(_difrep["sections"][0]["rows"][0]) == 2 and len(_difrep["sections"][1]["rows"][0]) == 4

# обратная совместимость: старый формат (templates[] + общая шапка) -> одна секция, та же раскладка
_oldrep = run_report(st13, {"title": "T", "header": _hdr6, "templates": [base_tmpl("RF-стойки")]})
assert len(_oldrep["sections"]) == 1 and _oldrep["sections"][0]["title"] == ""
assert _oldrep["sections"][0]["header"][1] == "Наим" and _oldrep["sections"][0]["rows"] == _oldrep["rows"]
print("SECTIONS: многосекционный отчёт (стойки+ригеля, разные столбцы) + обратная совместимость. OK")

# ───────── Заполнения: площадь (2 знака, запятая, half-up) + строка ИТОГ ─────────
_zfill = []
for mk, w, h, n in [("13/3/1", 1125, 275, 2), ("13/3/2", 1125, 1900, 2),
                    ("13/3/3", 1045, 275, 2), ("13/3/4", 1045, 2550, 2)]:
    for _ in range(n):
        _zfill.append({"layer": "RF-заполнения",
                       "attributes": {"МАРКИРОВКА": mk, "РАЗМЕР_ЗАП": f"{w}Х{h}"}})
_zsec = {"section_title": "", "header": [], "hide_header": False, "header_merges": [],
         "columns": ["=row", "=Object.«МАРКИРОВКА»", "=Object.«Ширина»", "=Object.«Высота»",
                     "=Count", "=Object.«Ширина»*Object.«Высота»*Count/1000000"],
         "filter": [{"field": "Слой", "op": "=", "value": "RF-заполнения"}],
         "group_by": 1, "sort_by": (1, "asc"), "total_row": True}
_zr = run_report(_zfill, {"sections": [_zsec]})["sections"][0]["rows"]
_bym = {r[1]: r for r in _zr if isinstance(r[1], str) and r[1].startswith("13/3/")}
assert _bym["13/3/1"][5] == "0,62", _bym["13/3/1"]          # 0.61875 -> 0,62
assert _bym["13/3/2"][5] == "4,28", _bym["13/3/2"]          # 4.275 half-up -> 4,28 (не 4,27)
assert _bym["13/3/3"][5] == "0,57" and _bym["13/3/4"][5] == "5,33", (_bym["13/3/3"], _bym["13/3/4"])
_tot = _zr[-1]
assert _tot[0] == "сумма", _tot                            # метка в первом столбце
assert _tot[4] == 8, _tot                                  # Σ количества — целое
assert _tot[5] == "10,8", _tot                             # Σ площади — запятая, хвостовой 0 срезан
assert _tot[2] == "" and _tot[3] == "", _tot               # под Ш/В — пусто (нет Count)
print("AREA+TOTAL: площадь (2 знака, запятая, half-up) + строка ИТОГ. OK")

# ───────── Col(n): площадь по фактическим (в т.ч. правленым) столбцам Ш/В ─────────
# В этой фикстуре нет столбца «Тип»: Ширина = столбец 3, Высота = 4 (1-based).
# (1) area=Col(3)*Col(4) даёт ТОТ ЖЕ результат, что прямой Object.«Ширина»*«Высота».
_zsec_col = dict(_zsec); _zsec_col["columns"] = list(_zsec["columns"])
_zsec_col["columns"][5] = "=Col(3)*Col(4)*Count/1000000"
_zc = run_report(_zfill, {"sections": [_zsec_col]})["sections"][0]["rows"]
_cbym = {r[1]: r for r in _zc if isinstance(r[1], str) and r[1].startswith("13/3/")}
assert _cbym["13/3/1"][5] == "0,62" and _cbym["13/3/2"][5] == "4,28", _cbym
assert _zc[-1][5] == "10,8", _zc[-1]                       # Σ площади через Col — как через Object
# (2) правка столбца Ширина (Object.«Ширина»-25) -> площадь идёт по ПРАВЛЕНОЙ ширине.
_zsec_ed = dict(_zsec); _zsec_ed["columns"] = list(_zsec["columns"])
_zsec_ed["columns"][2] = "=Object.«Ширина»-25"            # столбец Ширина (1-based 3)
_zsec_ed["columns"][5] = "=Col(3)*Col(4)*Count/1000000"
_ze = run_report(_zfill, {"sections": [_zsec_ed]})["sections"][0]["rows"]
_ebym = {r[1]: r for r in _ze if isinstance(r[1], str) and r[1].startswith("13/3/")}
assert _ebym["13/3/1"][2] == 1100, _ebym["13/3/1"]        # ширина показана правленой (1125-25)
assert _ebym["13/3/1"][5] == "0,61", _ebym["13/3/1"]      # 1100*275*2/1e6=0,605 -> 0,61 (по правленой Ш)
# Col без контекста / вне диапазона -> пусто (не роняет таблицу)
assert evaluate("=Col(1)", z, [z], 1) is None and evaluate("=Col(99)", z, [z], 1) is None
print("COL: площадь по столбцам Ш/В (= Object) + следует правке ширины. OK")

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
