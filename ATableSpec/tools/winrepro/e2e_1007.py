#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# E2E краш-тест 10.07 на живых DXF (atspec-testdata): смоук всех файлов,
# сквозная нумерация, спецификация и раскрой штапиков из def'ов ЖИВОЙ ФОРМЫ
# (/tmp/atspec_{spec,cut}_def.json от tools/winrepro/CutDump.cs),
# сценарий «терморазрыва нет» (кейс Алексея), заполнения с ИТОГ.
import os, sys, json, glob, warnings
warnings.filterwarnings("ignore")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, "/home/claude/AutoCAD/ATableSpec/engine")
from atspec_report import run_report
import ezdxf
from ezdxf import bbox as _bb

DXF = "/home/claude/atspec-testdata/dxf"
FAILS = []
def rep(ok, tag, msg):
    print(("[OK] " if ok else "[FAIL] ") + tag + ": " + msg)
    if not ok: FAILS.append(tag)

SKIP = {"TEXT", "MTEXT", "ATTRIB", "ATTDEF", "DIMENSION", "HATCH"}
def gbox(ins):
    xs, ys = [], []
    for ve in ins.virtual_entities():
        if ve.dxftype() in SKIP: continue
        try:
            ext = _bb.extents([ve], fast=True)
            if ext.has_data:
                xs += [ext.extmin.x, ext.extmax.x]; ys += [ext.extmin.y, ext.extmax.y]
        except Exception: pass
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None

def recs(path, with_gab=False):
    doc = ezdxf.readfile(path)
    rr = []
    for e in doc.modelspace().query("INSERT"):
        at = {a.dxf.tag: a.dxf.text for a in e.attribs}
        if with_gab:
            b = gbox(e)
            if b:
                at.update({"ГАБ_X0": str(b[0]), "ГАБ_Y0": str(b[1]),
                           "ГАБ_X1": str(b[2]), "ГАБ_Y1": str(b[3])})
        rr.append({"name": e.dxf.name, "layer": e.dxf.layer, "attributes": at})
    return rr

def sec(title, lay, total=False):
    return {"section_title": title, "header": ["№", "М", "Длина", "К"],
            "columns": ["=row", "=Object.«МАРКИРОВКА»", "=Object.«Длина»", "=Count"],
            "filter": [{"field": "Слой", "op": "=", "value": lay}],
            "group_by": 1, "sort_by": [1, "asc"], "total_row": total}

# ── SMOKE: все 5 файлов, спецификация стоек не падает и даёт строки ──
for path in sorted(glob.glob(DXF + "/*.dxf")):
    name = os.path.basename(path)
    rr = recs(path)
    out = run_report(rr, {"sections": [sec("", "RF-стойки")]})
    rows = out["sections"][0]["rows"] if out["sections"] else []
    rep(len(rows) > 0, "SMOKE", "%s: стоек-групп %d (блоков всего %d)" % (name, len(rows), len(rr)))

# ── NUM: сквозная нумерация на живом чертеже ──
rrp = recs(DXF + "/Проба_штапики_2.dxf")
o = run_report(rrp, {"sections": [sec("", "RF-стойки"), sec("", "RF-ригеля")]})["sections"]
last1, first2 = o[0]["rows"][-1][0], o[1]["rows"][0][0]
rep(first2 == last1 + 1, "NUM1",
    "без заголовка нумерация сквозная: стойки 1..%s -> ригеля с %s" % (last1, first2))
o2 = run_report(rrp, {"sections": [sec("", "RF-стойки"), sec("Ригеля", "RF-ригеля")]})["sections"]
rep(o2[1]["rows"][0][0] == 1, "NUM2", "с заголовком — свой отсчёт с 1 (got %s)" % o2[1]["rows"][0][0])

# ── SPEC: спецификация штапиков из ЖИВОЙ ФОРМЫ на живом чертеже ──
spec = json.load(open("/tmp/atspec_spec_def.json"))
rrg = recs(DXF + "/Проба_штапики_2.dxf", with_gab=True)
so = run_report(rrg, spec)["sections"]
rep(len(so) == 4 and all(s["rows"] for s in so), "SPEC1",
    "4 секции штапиков, все непустые: строки " + str([len(s["rows"]) for s in so]))
rep(all(s["rows"][0][0] == 1 for s in so), "SPEC2",
    "у каждой секции заголовок есть -> нумерация с 1 (регресс)")
li = next(i for i, h in enumerate(so[3]["header"]) if str(h).lower().startswith("длина"))
sizes = sorted({r[li] for r in so[3]["rows"]})
rep(sizes == [57, 247], "SPEC3", "м/э зоны разрезных = %s (эталон СПДС 57/247)" % sizes)

# ── CUT: раскрой «Взять с табл.» из живой формы на живом чертеже ──
cut = json.load(open("/tmp/atspec_cut_def.json"))
flt_has_auto = any(f.get("auto") for s in cut["sections"] for f in s.get("filter", []))
co = run_report(rrg, cut)["sections"]
rep(len(co) == 4 and all(s["rows"] for s in co), "CUT1",
    "4 секции раскроя, все непустые: строки " + str([len(s["rows"]) for s in co]))
rep(all(s["header"][0] == "17_01_04 8 6000" for s in co) and
    [s["hide_header"] for s in co] == [False, True, True, True], "CUT2",
    "одна видимая шапка «17_01_04 8 6000», продолжения скрыты " +
    str([s["hide_header"] for s in co]))
rep(flt_has_auto, "CUT3", "def несёт auto-фильтры — движок исполняет их штатно (прогон прошёл)")
mono_ok = all(all(s["rows"][i][0] <= s["rows"][i + 1][0] for i in range(len(s["rows"]) - 1))
              for s in co)
rep(mono_ok, "CUT4", "группы длин по возрастанию в каждой под-секции: " +
    str([[r[0] for r in s["rows"]] for s in co]))
tot_cut = sum(r[1] for s in co for r in s["rows"])
tot_spec = sum(r[4] for s in so for r in s["rows"] if isinstance(r[4], (int, float)))
rep(tot_cut == tot_spec and tot_cut > 0, "CUT5",
    "суммарное кол-во раскроя == спецификации: %s == %s" % (tot_cut, tot_spec))

# ── NOBREAK: кейс Алексея — терморазрыва на чертеже нет ──
import copy
nb = copy.deepcopy(cut); nb["beads"]["layer"] = "НЕТ-ТАКОГО-СЛОЯ"
no = run_report(rrg, nb)["sections"]
rep(len(no) == 2 and [s["hide_header"] for s in no] == [False, True], "NOBRK1",
    "раскрой: пустые терморазрыв-секции исчезли, осталось %d (шапка одна)" % len(no))
nbs = copy.deepcopy(spec); nbs["beads"]["layer"] = "НЕТ-ТАКОГО-СЛОЯ"
ns = run_report(rrg, nbs)["sections"]
titles = [s["title"] for s in ns]
rep(len(ns) == 2 and all("терморазрыв" not in t or "без" in t for t in titles), "NOBRK2",
    "спецификация: заголовков «с терморазрывом» в таблице нет: " + str(titles))

# ── NOBREAK-ФОРМА (10.07-2): построитель сам не сеет пустые терморазрыв-секции ──
specN = json.load(open("/tmp/atspec_spec_nobrk_def.json"))
tN = [str(s.get("section_title", "")) for s in specN["sections"]]
rep(len(specN["sections"]) == 2 and all("без терморазрыва" in t for t in tN), "NOBRK3",
    "пресет без стыков: def формы несёт 2 секции «...без терморазрыва»: " + str(tN))
sN = run_report(rrg, specN)["sections"]
rep(len(sN) == 2 and all(s["rows"] for s in sN), "NOBRK4",
    "spec_nobrk на живом DXF: 2 секции, обе непустые: " + str([len(s["rows"]) for s in sN]))
cutN = json.load(open("/tmp/atspec_cut_nobrk_def.json"))
rep(len(cutN["sections"]) == 2, "NOBRK5",
    "«Взять с табл.» из ПОЛНОЙ спецификации без стыков: разрезные пропущены, секций %d"
    % len(cutN["sections"]))
cN = run_report(rrg, cutN)["sections"]
totN = sum(r[1] for s in cN for r in s["rows"])
li2 = next(i for i, h in enumerate(sN[0]["header"]) if "колич" in str(h).lower())
totSN = sum(r[li2] for s in sN for r in s["rows"] if isinstance(r[li2], (int, float)))
rep(len(cN) == 2 and cN[0]["header"][0] == "17_01_04 8 6000" and
    [s["hide_header"] for s in cN] == [False, True] and totN == totSN and totN > 0, "NOBRK6",
    "cut_nobrk: одна шапка «17_01_04 8 6000», Σ количеств == спецификации: %s == %s"
    % (totN, totSN))

# ── FILL: Заполнения.dxf — площадь + строка ИТОГ ──
rf = recs(DXF + "/Заполнения.dxf")
fo = run_report(rf, {"sections": [{
    "section_title": "", "header": ["№", "Марка", "Ширина", "Высота", "Колич.", "Площадь"],
    "columns": ["=row", "=Object.«МАРКИРОВКА»", "=Object.«Ширина»", "=Object.«Высота»",
                "=Count", "=Col(3)*Col(4)*Count/1000000"],
    "filter": [{"field": "Слой", "op": "=", "value": "RF-заполнения"}],
    "group_by": 1, "sort_by": [1, "asc"], "total_row": True}]})["sections"][0]["rows"]
rep(len(fo) >= 2 and fo[-1][0] == "сумма" and str(fo[-1][4]).isdigit(), "FILL",
    "заполнения: %d строк, ИТОГ кол-во=%s, площадь=%s" % (len(fo) - 1, fo[-1][4], fo[-1][5]))

print("\n── ИТОГ ──")
print("ПРОВАЛОВ: %d %s" % (len(FAILS), FAILS) if FAILS else "ВСЕ E2E-ПРОВЕРКИ ПРОЙДЕНЫ")
sys.exit(1 if FAILS else 0)
