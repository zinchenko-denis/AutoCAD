# -*- coding: utf-8 -*-
"""Синтетика движка отчётов ATSPEC (atspec_report.run_report, ревью 23.09)
против независимого оракула: свой разбор значений и фильтров, своя
группировка по НАПЕЧАТАННОМУ значению, свои суммы.

Блоки: марки (кириллица, изредка латинские двойники «С»/«C», хвостовые
пробелы), длины в разных записях («1500», «1500,0», «1 500», «1500.00»,
double-хвосты динблоков «1499.99999999998»), слои. Определения: фильтр по
слою/марке/длине (=, ≠, >, <, ≥, ≤, содержит, не содержит, списки values),
столбцы №/Марка/Длина/Кол./Сумма длин, группа по марке или длине, сортировка
по столбцу группы, строка ИТОГ.

Инварианты:
 A1 одна позиция — одна строка: в столбце группы нет двух строк с одинаковым
    напечатанным значением (пробелы по краям не видны);
 A2 Σ «Кол.» = числу блоков, прошедших фильтр (оракул);
 A3 № идут 1..n подряд; A4 порядок строк — по возрастанию/убыванию столбца
    группы (натуральный: С2 < С10);
 A5 состав: множество марок/длин в таблице = оракулу;
 A6 «Сумма длин» строки = Σ длин её блоков (округление к целому);
 A7 ИТОГ = Σ напечатанных «Кол.»;
 A8 перестановка блоков на входе не меняет таблицу;
 A9 ни одной ячейки «#ВЫРАЖ?» и ни одного исключения.
INFO: латинские двойники марок (С/C, Р/P, …) — выглядят одинаково, считаются
раздельно; движок об этом не предупреждает.

Запуск: PYTHONUTF8=1 python3 ATableSpec/tools/report_synth.py [--seed N] [--n N]
Выход ≠ 0 при нарушениях.
"""
import argparse
import json
import math
import os
import random
import re
import sys
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "engine")))
from atspec_report import run_report  # noqa: E402

HOMO = str.maketrans("СРАВЕКМНОТХ", "CPABEKMHOTX")   # кириллица → латиница-двойник


def num(s):
    """Оракул числа: чистое число с «,»/«.» и пробелами-разрядами, иначе None."""
    t = str(s).strip().replace("\u00a0", "").replace(" ", "").replace(",", ".")
    return float(t) if re.fullmatch(r"[+-]?\d+(\.\d+)?", t) else None


def pint(x):
    """Печать целого как в таблице: половины от нуля."""
    return int(math.floor(x + 0.5)) if x >= 0 else -int(math.floor(-x + 0.5))


NOISE = {"tails": True, "spaces": True, "homo": True}


def length_text(rng, v):
    k = rng.random()
    if not NOISE["tails"] and k >= 0.8:
        k = 0.0
    if k < 0.45:
        return "%d" % v
    if k < 0.6:
        return "%d,0" % v
    if k < 0.7:
        return "{:,}".format(v).replace(",", " ")
    if k < 0.8:
        return "%.2f" % v
    if k < 0.9:
        return repr(v - 2e-11 * v)          # double-хвост динблока: 1499.99999999998
    return repr(v + 3e-12 * v)


def make_blocks(rng, n):
    marks = ["С%d" % i for i in range(1, 16)] + ["Р%d" % i for i in range(1, 9)] + ["СП%02d" % i for i in range(1, 5)]
    lens = [rng.randrange(300, 6000, 5) for _ in range(12)]
    out = []
    for _ in range(n):
        m = rng.choice(marks)
        if NOISE["homo"] and rng.random() < 0.03:
            m = m.translate(HOMO)            # латинский двойник
        if NOISE["spaces"] and rng.random() < 0.05:
            m = m + " "                      # хвостовой пробел атрибута
        v = rng.choice(lens)
        out.append({"name": "Профиль", "layer": rng.choice(["RF-стойки", "RF-ригели", "0"]),
                    "attributes": {"ИМЯ": m, "Длина": length_text(rng, v)}})
    return out


def make_def(rng):
    by = rng.choice(["mark", "len"])
    cols = ["=row", "=Object.«ИМЯ»", "=Object.«Длина»", "=Count", "=Sum(Object.«Длина»)"]
    gi = 1 if by == "mark" else 2
    flt = []
    k = rng.random()
    if k < 0.3:
        flt.append({"field": "Слой", "op": "=", "value": rng.choice(["RF-стойки", "RF-ригели"])})
    elif k < 0.45:
        flt.append({"field": "Слой", "op": "=", "values": ["RF-стойки", "RF-ригели"]})
    elif k < 0.6:
        flt.append({"field": "Длина", "op": rng.choice([">", "<", "≥", "≤"]),
                    "value": str(rng.randrange(1000, 5000, 100))})
    elif k < 0.75:
        flt.append({"field": "ИМЯ", "op": rng.choice(["содержит", "не содержит"]), "value": rng.choice(["С", "Р", "П"])})
    elif k < 0.85:
        flt.append({"field": "Длина", "op": rng.choice(["=", "≠"]), "value": "1500"})
    return {"title": "T", "sections": [{"section_title": "Спецификация", "header": ["№", "Марка", "Длина", "Кол.", "Σ"],
                                        "columns": cols, "group_by": gi,
                                        "sort_by": [gi, rng.choice(["asc", "desc"])],
                                        "filter": flt, "total_row": rng.random() < 0.5}]}, gi


def o_pass(b, flt):
    for f in flt:
        fld, op = f["field"], f.get("op", "=")
        if fld == "Слой":
            ls = b["layer"]
        else:
            ls = str(b["attributes"].get(fld, "")).strip()
        vv = [v for v in (f.get("values") or []) if str(v).strip()] or [f.get("value", "")]
        vv = [str(v).strip() for v in vv]

        def one(v):
            a, c = num(ls), num(v)
            if op in ("=", "≠"):
                eq = (abs(a - c) <= 1e-6 * max(1.0, abs(a), abs(c))) if (a is not None and c is not None) \
                    else ls.lower() == v.lower()
                return eq if op == "=" else not eq
            if op in ("содержит", "не содержит"):
                h = v.lower() in ls.lower()
                return h if op == "содержит" else not h
            if a is None or c is None:
                return False
            return {">": a > c, "<": a < c, "≥": a >= c, "≤": a <= c}[op]
        if op in (">", "<", "≥", "≤"):
            ok = one(vv[0])                      # списки у диапазонов — первое значение
        elif op in ("≠", "не содержит"):
            ok = all(one(v) for v in vv)         # «ни одно из»
        else:
            ok = any(one(v) for v in vv)         # «любое из»
        if not ok:
            return False
    return True


def printed_key(v):
    if isinstance(v, (int, float)):
        return ("n", pint(float(v)))
    s = str(v).strip()
    n = num(s)
    return ("n", pint(n)) if n is not None else ("s", s)


def natkey(v):
    if isinstance(v, (int, float)):
        return (0, (float(v),))
    s = str(v)
    return (1, tuple((0, int(p)) if p.isdigit() else (1, p) for p in re.split(r"(\d+)", s) if p))


def check(rng, idx, viol, info):
    blocks = make_blocks(rng, rng.randrange(5, 300))
    d, gi = make_def(rng)
    sec = d["sections"][0]
    try:
        rep = run_report(json.loads(json.dumps(blocks)), json.loads(json.dumps(d)))
    except Exception as e:  # noqa: BLE001
        viol.append(("A9", "#%d исключение %r" % (idx, e)))
        return
    rows = rep["sections"][0]["rows"] if rep["sections"] else []
    total = None
    if sec["total_row"] and rows and not isinstance(rows[-1][0], int):
        total, rows = rows[-1], rows[:-1]
    passed = [b for b in blocks if o_pass(b, sec["filter"])]
    tag = "#%d" % idx
    if any(c == "#ВЫРАЖ?" for r in rows for c in r):
        viol.append(("A9", "%s ячейка #ВЫРАЖ?" % tag))
    keys = [printed_key(str(r[gi]).replace(", разн.", "")) for r in rows]
    dup = [k for k, c in Counter(keys).items() if c > 1]
    if dup:
        viol.append(("A1", "%s группа по %s: одинаковые строки %s" % (tag, "марке" if gi == 1 else "длине",
                                                                     [k[1] for k in dup[:3]])))
    cnt = sum(int(r[3]) for r in rows)
    if cnt != len(passed):
        viol.append(("A2", "%s Σ Кол. %d, прошло фильтр %d (фильтр %s)" % (tag, cnt, len(passed), sec["filter"])))
    if [r[0] for r in rows] != list(range(1, len(rows) + 1)):
        viol.append(("A3", "%s нумерация %s" % (tag, [r[0] for r in rows][:8])))
    ks = [natkey(r[gi]) for r in rows]
    want = sorted(ks, reverse=sec["sort_by"][1] == "desc")
    if ks != want:
        viol.append(("A4", "%s порядок строк не по столбцу группы" % tag))
    o_keys = Counter()
    for b in passed:
        v = b["attributes"]["ИМЯ"] if gi == 1 else num(b["attributes"]["Длина"])
        o_keys[printed_key(v)] += 1
    if set(o_keys) != set(keys):
        viol.append(("A5", "%s состав групп: лишние %s, нет %s" % (tag, sorted(set(keys) - set(o_keys))[:3],
                                                                  sorted(set(o_keys) - set(keys))[:3])))
    # A6: сумма длин по группе (оракул по напечатанному ключу группы)
    sums = defaultdict(float)
    for b in passed:
        v = b["attributes"]["ИМЯ"] if gi == 1 else num(b["attributes"]["Длина"])
        sums[printed_key(v)] += num(b["attributes"]["Длина"])
    if not dup:
        for r, k in zip(rows, keys):
            if isinstance(r[4], (int, float)) and abs(r[4] - pint(sums[k])) > 1:
                viol.append(("A6", "%s Σ длин %s: %s против %s" % (tag, k[1], r[4], pint(sums[k]))))
                break
    if total is not None and int(total[3]) != cnt:
        viol.append(("A7", "%s ИТОГ %s ≠ Σ %d" % (tag, total[3], cnt)))
    sh = list(blocks)
    rng.shuffle(sh)
    rep2 = run_report(json.loads(json.dumps(sh)), json.loads(json.dumps(d)))
    rows2 = rep2["sections"][0]["rows"] if rep2["sections"] else []
    base = [r[1:] for r in (rows + ([total] if total else []))]
    if not dup and [r[1:] for r in rows2] != base:
        dif = next(((x, y) for x, y in zip([r[1:] for r in rows2], base) if x != y), None)
        viol.append(("A8", "%s перестановка блоков меняет таблицу: %s → %s" % (tag, dif[1] if dif else "", dif[0] if dif else "")))
    marks = {b["attributes"]["ИМЯ"].strip() for b in passed}
    homo = [m for m in marks if m != m.translate(HOMO) and m.translate(HOMO) in marks] + \
        [m for m in marks if re.search("[A-Z]", m)]
    if homo and gi == 1:
        info.append(homo[0])


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=2309)
    ap.add_argument("--n", type=int, default=600)
    a = ap.parse_args(argv)
    hard = 0
    for mode, noise in (("чистые данные", dict(tails=False, spaces=False, homo=False)),
                        ("только пробелы в марках", dict(tails=False, spaces=True, homo=False)),
                        ("только double-хвосты длин", dict(tails=True, spaces=False, homo=False)),
                        ("всё вместе + латинские двойники", dict(tails=True, spaces=True, homo=True))):
        NOISE.update(noise)
        rng = random.Random(a.seed)
        viol, info = [], []
        for i in range(a.n):
            check(rng, i, viol, info)
        hard += len(viol)
        by = Counter(c for c, _ in viol)
        print("ОТЧЁТЫ [%s]: сценариев %d, с нарушениями %d %s" % (mode, a.n, len({m.split()[0] for _, m in viol}),
                                                                 dict(sorted(by.items()))))
        report(viol, info)
    return 1 if hard else 0


def report(viol, info):
    seen = set()
    for c, m in viol:
        if c not in seen:
            seen.add(c)
            print("  · %s %s" % (c, m[:300]))
    if info:
        print("  [INFO] латинские двойники марок в %d таблицах (напр. %r): печатаются одинаково, считаются "
              "отдельно — предупреждения нет" % (len(info), info[0]))


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
