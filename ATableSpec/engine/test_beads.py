# -*- coding: utf-8 -*-
"""Штапики (ТЗ 07.07): препроцессор стыков стоек _beads_expand.

Часть 1 — СИНТЕТИКА (всегда, в т.ч. на CI): интервальная математика стыков,
клоны-«половины», фолбэки, прогон через run_report с 4-секционным def пресета.

Часть 2 — ЭТАЛОННЫЙ DXF (Проба_штапики_2.dxf, приватный atspec-testdata):
если файл найден рядом/в uploads И ezdxf доступен — полная сверка с ручной
таблицей Алексея: 30 заполнений Эм1 со стыком, перехлёсты 247/57, зазор 10.
Нет файла/ezdxf — часть пропускается (CI основного repo файла не имеет).

Запуск: PYTHONUTF8=1 python3 test_beads.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from atspec_report import _beads_expand, run_report, _BEADS_CUT_LAYER  # noqa: E402

FAIL = 0


def rep(ok, tag, msg):
    global FAIL
    print(f"[{'OK' if ok else 'BUG'}] {tag}: {msg}")
    if not ok:
        FAIL += 1


def B(layer, x0, y0, x1, y1, **attrs):
    a = {"ГАБ_X0": str(x0), "ГАБ_Y0": str(y0), "ГАБ_X1": str(x1), "ГАБ_Y1": str(y1)}
    a.update({k: str(v) for k, v in attrs.items()})
    return {"name": "blk", "layer": layer, "attributes": a}


# ── Часть 1: синтетика ──────────────────────────────────────────────────────
# Две стойки в колонке x[0..100] с зазором 1000..1010; зеркальная колонка справа
# x[600..700] (дубль Y-интервалов — merged должен схлопнуть).
recs = [
    B("RF-стойки", 0, 0, 100, 1000), B("RF-стойки", 0, 1010, 100, 2400),
    B("RF-стойки", 600, 0, 700, 1000), B("RF-стойки", 600, 1010, 700, 2400),
    # заполнение на стыке: y 800..1300 → низ 1000-800=200, верх 1300-1010=290
    B("RF-заполнения", 80, 800, 620, 1300, МАРКИРОВКА="Эм1"),
    # заполнение без стыка (целиком в нижней стойке)
    B("RF-заполнения", 80, 100, 620, 900, МАРКИРОВКА="Сп1"),
    # заполнение далеко по X (нет прилегающих стоек) → стыка нет
    B("RF-заполнения", 2000, 800, 2600, 1300, МАРКИРОВКА="Сп9"),
    # запись без габарита → консервативный фолбэк ШТ_СТЫК=0
    {"name": "blk", "layer": "RF-заполнения", "attributes": {"МАРКИРОВКА": "Бг1"}},
]
out = _beads_expand([dict(r, attributes=dict(r["attributes"])) for r in recs],
                    {"layer": "RF-стойки"})

by_mark = {}
for r in out:
    if r["layer"] == "RF-заполнения":
        by_mark[r["attributes"].get("МАРКИРОВКА")] = r["attributes"]
halves = [r for r in out if r["layer"] == _BEADS_CUT_LAYER]

a = by_mark["Эм1"]
rep(a.get("ШТ_СТЫК") == "1" and a.get("ШТ_НИЗ") == "200"
    and a.get("ШТ_ВЕРХ") == "290" and a.get("ШТ_ЗАЗОР") == "10",
    "B1", f"стык на Эм1: {dict((k, a.get(k)) for k in ('ШТ_СТЫК','ШТ_НИЗ','ШТ_ВЕРХ','ШТ_ЗАЗОР'))}")
rep(by_mark["Сп1"].get("ШТ_СТЫК") == "0", "B2",
    f"Сп1 без стыка: ШТ_СТЫК={by_mark['Сп1'].get('ШТ_СТЫК')!r}")
rep(by_mark["Сп9"].get("ШТ_СТЫК") == "0", "B3",
    f"Сп9 вне колонок: ШТ_СТЫК={by_mark['Сп9'].get('ШТ_СТЫК')!r}")
rep(by_mark["Бг1"].get("ШТ_СТЫК") == "0", "B4",
    f"без габарита → 0: ШТ_СТЫК={by_mark['Бг1'].get('ШТ_СТЫК')!r}")
rep(len(halves) == 2 and sorted(h["attributes"]["ШТ_РАЗМЕР"] for h in halves) == ["200", "290"]
    and {h["attributes"]["ШТ_ЧАСТЬ"] for h in halves} == {"низ", "верх"}
    and all(h["attributes"].get("МАРКИРОВКА") == "Эм1" for h in halves),
    "B5", f"половины: {[ (h['attributes']['ШТ_ЧАСТЬ'], h['attributes']['ШТ_РАЗМЕР']) for h in halves ]}")

# стойки не трогаются, beads=None — no-op
rep(all("ШТ_СТЫК" not in r["attributes"] for r in out if r["layer"] == "RF-стойки"),
    "B6", "стойки без ШТ-полей")
noop = _beads_expand([dict(r, attributes=dict(r["attributes"])) for r in recs], None)
rep(len(noop) == len(recs) and all("ШТ_СТЫК" not in r["attributes"] for r in noop),
    "B7", "beads=None → записи нетронуты")

# ── run_report с 4-секционным def (как пресет «Штапики») ──────────────────
COLS = ["=row", "=Object.«МАРКИРОВКА»", "Артикул", None, "=Count*2", "шт."]


def sec(title, length_expr, flt, grp=3):
    cols = list(COLS)
    cols[3] = length_expr
    return {"section_title": title, "header": ["№", "Наименование", "Артикул", "Длина", "Колич.", "Ед."],
            "columns": cols, "filter": flt, "group_by": grp, "sort_by": [grp, "asc"]}


DEF = {"title": "Спецификация штапиков", "beads": {"layer": "RF-стойки"}, "sections": [
    sec("Горизонтальный штапик в зонах без терморазрыва", "=Object.«Ширина»",
        [{"field": "Слой", "op": "=", "value": "RF-заполнения"},
         {"field": "ШТ_СТЫК", "op": "=", "value": "0"}]),
    sec("Вертикальный штапик в зонах без терморазрыва", "=Object.«Высота»",
        [{"field": "Слой", "op": "=", "value": "RF-заполнения"},
         {"field": "ШТ_СТЫК", "op": "=", "value": "0"}]),
    sec("Горизонтальный штапик в зонах с терморазрывом", "=Object.«Ширина»",
        [{"field": "Слой", "op": "=", "value": "RF-заполнения"},
         {"field": "ШТ_СТЫК", "op": "=", "value": "1"}]),
    sec("Вертикальный штапик в зонах с терморазрывом", "=Object.«ШТ_РАЗМЕР»",
        [{"field": "Слой", "op": "=", "value": _BEADS_CUT_LAYER}]),
]}

# размеры для Ширина/Высота — через РАЗМЕР_ЗАП (как в живых чертежах)
recs2 = [dict(r, attributes=dict(r["attributes"])) for r in recs]
for r in recs2:
    m = r["attributes"].get("МАРКИРОВКА")
    if m == "Эм1":
        r["attributes"]["РАЗМЕР_ЗАП"] = "651Х490"
    elif m in ("Сп1", "Сп9"):
        r["attributes"]["РАЗМЕР_ЗАП"] = "651Х790"
res = run_report(recs2, DEF)
S = res["sections"]
rep(len(S) == 4, "B8", f"секций: {len(S)}")
# секция 4: две строки-«половины», сортировка по возрастанию размера, Count*2
s4 = S[3]["rows"]
rep(len(s4) == 2 and s4[0][3] == 200 and s4[1][3] == 290
    and s4[0][4] == 2 and s4[1][4] == 2 and s4[0][0] == 1 and s4[1][0] == 2,
    "B9", f"верт. с терморазрывом: {s4}")
# секция 3: гориз. с терморазрывом — один Эм1, Ширина 651, ×2
s3 = S[2]["rows"]
rep(len(s3) == 1 and s3[0][3] == 651 and s3[0][4] == 2, "B10",
    f"гориз. с терморазрывом: {s3}")
# секции 1–2: без терморазрыва — Сп1+Сп9+Бг1(фолбэк), группы по длине
s1, s2 = S[0]["rows"], S[1]["rows"]
rep(any(r[3] == 651 and r[4] == 4 for r in s1), "B11",
    f"гориз. без терморазрыва (651 x2 заполнения → 4 шт): {s1}")
rep(any(r[3] == 790 and r[4] == 4 for r in s2), "B12",
    f"верт. без терморазрыва (790 → 4 шт): {s2}")
# «первое, разн.» в Наименовании смешанной группы (Сп1+Сп9 в одной длине)
rep(any(str(r[1]).endswith(", разн.") for r in s1), "B13",
    f"наименование смешанной группы: {[r[1] for r in s1]}")

# ── Часть 2: эталонный DXF ─────────────────────────────────────────────────
CAND = ["/mnt/user-data/uploads/Проба_штапики_2.dxf",
        os.path.join(os.path.dirname(os.path.abspath(__file__)),
                     "..", "..", "..", "atspec-testdata", "dxf", "Проба_штапики_2.dxf")]
path = next((p for p in CAND if os.path.exists(p)), None)
if path is None:
    print("[SKIP] DXF-эталон не найден — часть 2 пропущена (норма для CI)")
else:
    try:
        import ezdxf
        from ezdxf import bbox as _bb
    except Exception:
        ezdxf = None
        print("[SKIP] ezdxf недоступен — часть 2 пропущена")
    if ezdxf is not None:
        SKIP = {"TEXT", "MTEXT", "ATTRIB", "ATTDEF", "DIMENSION", "HATCH"}

        def gbox(ins):
            xs, ys = [], []
            for ve in ins.virtual_entities():
                if ve.dxftype() in SKIP:
                    continue
                try:
                    ext = _bb.extents([ve], fast=True)
                    if ext.has_data:
                        xs += [ext.extmin.x, ext.extmax.x]
                        ys += [ext.extmin.y, ext.extmax.y]
                except Exception:
                    pass
            return (min(xs), min(ys), max(xs), max(ys)) if xs else None

        import warnings
        warnings.filterwarnings("ignore")
        doc = ezdxf.readfile(path)
        rr = []
        for e in doc.modelspace().query("INSERT"):
            lay = e.dxf.layer
            if lay not in ("RF-заполнения", "RF-стойки"):
                continue
            b = gbox(e)
            if not b:
                continue
            at = {a.dxf.tag: a.dxf.text for a in e.attribs}
            at.update({"ГАБ_X0": str(b[0]), "ГАБ_Y0": str(b[1]),
                       "ГАБ_X1": str(b[2]), "ГАБ_Y1": str(b[3])})
            rr.append({"name": "blk", "layer": lay, "attributes": at})
        out2 = _beads_expand(rr, {"layer": "RF-стойки"})
        em = [r for r in out2 if r["layer"] == "RF-заполнения"
              and r["attributes"].get("МАРКИРОВКА") == "Эм1"]
        ok = (len(em) == 30
              and all(r["attributes"].get("ШТ_СТЫК") == "1" for r in em)
              and all(r["attributes"].get("ШТ_НИЗ") == "247" for r in em)
              and all(r["attributes"].get("ШТ_ВЕРХ") == "57" for r in em)
              and all(r["attributes"].get("ШТ_ЗАЗОР") == "10" for r in em))
        rep(ok, "D1", f"Эм1: n={len(em)}, "
            f"низ={sorted({r['attributes'].get('ШТ_НИЗ') for r in em})}, "
            f"верх={sorted({r['attributes'].get('ШТ_ВЕРХ') for r in em})}, "
            f"зазор={sorted({r['attributes'].get('ШТ_ЗАЗОР') for r in em})} "
            f"(эталон таблицы Алексея: 247 / 57 / 10)")
        others = [r for r in out2 if r["layer"] == "RF-заполнения"
                  and r["attributes"].get("МАРКИРОВКА") != "Эм1"]
        rep(all(r["attributes"].get("ШТ_СТЫК") == "0" for r in others), "D2",
            f"прочие заполнения без стыка: {len(others)} шт")
        halves2 = [r for r in out2 if r["layer"] == _BEADS_CUT_LAYER]
        rep(len(halves2) == 60 and
            sorted({r["attributes"]["ШТ_РАЗМЕР"] for r in halves2}) == ["247", "57"],
            "D3", f"половин: {len(halves2)} (ожидание 60 = 30×2)")

print()
if FAIL:
    print(f"BEADS: ПРОВАЛОВ {FAIL}")
    sys.exit(1)
print("BEADS: все ассерты прошли.")
