# -*- coding: utf-8 -*-
"""D-сверка полигона Германа (poligon_0208) против нашего движка.

Запуск руками (не юнит — нужен DWG-дамп из atspec-testdata):

    PYTHONUTF8=1 python3 dsverka_poligon.py [путь/к/poligon_tree.json]

Дамп готовится `tools/dwg_probe_tree.py` (aspose-cad, грабля-22):
он разворачивает *Model_Space РЕКУРСИВНО в мировые координаты и
кладёт inserts/polys/lines. ВАЖНО: `ci.entities` даёт лишь 99
сущностей — вся геометрия лежит в блоке `*Model_Space`
(`block_entities['*Model_Space']`), обходить надо ЕГО.

── Чего сверка НЕ может (осознанное ограничение) ────────────────────
Слои `_01_ПС_*` — это КОНВЕНЦИЯ ГЕРМАНА (снята с боевого DWG
Ленпроспекта 24.07, STAGE3_FRAME.md «наши слои этапа 3 — ЭТИ»), и
наш ATFRAME пишет в НИХ ЖЕ. Поэтому по слою отличить нашу генерацию
от его руки НЕЛЬЗЯ. Достоверно наши — только блоки `AFRAME_*`
(EnsureFitBlock создаёт их сам, образцом не подменяются): в полигоне
это 122 фитинга межэтажного участка. Кронштейны/направляющие
вставляются ОБРАЗЦОМ пользователя, поэтому носят имена Германа
(«Кронш КР1-85-150») и неотличимы от ручных по имени.
Кляммеров на `_01_ПС_КЛЯММЕРЫ` в полигоне НЕТ ВООБЩЕ (0), хотя наш
движок на этих зонах даёт 566..790 штук — то есть полного прогона
ATFRAME в файле не было. Отсюда рабочая гипотеза: подсистема
полигона — РУЧНАЯ, а совпадения ниже = совпадения ПРАВИЛА, не
копия нашего вывода. Гипотезу подтвердить у Германа (В-ае).
"""
import os
import sys
import json
import collections

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from frame_plan import frame_plan

# границы участков полигона (подписи в модели)
X_RYAD = 15211.0
X_ORT = 41645.0
WIN_W, WIN_H = 1800.0, 2200.0   # проёмы полигона
TOL = 1.0


def bbox(pts):
    xs = [a for a, b in pts]
    ys = [b for a, b in pts]
    return min(xs), min(ys), max(xs), max(ys)


def load(path):
    T = json.load(open(path, encoding="utf-8"))
    return T["inserts"], [p for p in T["polys"] if p["par"] == "MS"]


def zone_of(x):
    return "Рядовая" if x < X_RYAD else \
           ("Межэтажная" if x < X_ORT else "Ортогональная")


def windows(polys, keep):
    """Проёмы участка = замкнутые прямоугольники WIN_W x WIN_H."""
    out = []
    for p in polys:
        x0, y0, x1, y1 = bbox(p["pts"])
        if not keep(x0):
            continue
        if abs((x1 - x0) - WIN_W) < TOL and abs((y1 - y0) - WIN_H) < TOL:
            out.append((x0, y0, x1, y1))
    return sorted(out)


def outer_of(polys, keep, fallback):
    """Контур зоны = самая большая замкнутая полилиния участка."""
    cand = [p for p in polys if keep(bbox(p["pts"])[0])]
    big = None
    for p in cand:
        x0, y0, x1, y1 = bbox(p["pts"])
        if abs((x1 - x0) - WIN_W) < TOL and abs((y1 - y0) - WIN_H) < TOL:
            continue
        a = (x1 - x0) * (y1 - y0)
        if big is None or a > big[0]:
            big = (a, (x0, y0, x1, y1))
    return big[1] if big else fallback


def cnt(items, kx="x", ky="y"):
    return set((round(i[kx], 1), round(i[ky], 1)) for i in items)


def steps(vals):
    v = sorted(set(round(a, 1) for a in vals))
    return collections.Counter(round(b - a) for a, b in zip(v, v[1:]))


def report(tag, ours, his, note=""):
    o, h = len(ours), len(his)
    d = (o - h) / h * 100.0 if h else float("nan")
    print("  %-16s наше=%-5d его=%-5d  Δ=%+6.1f%%   %s"
          % (tag, o, h, d, note))


def run(path):
    ins, polys = load(path)
    ins = [i for i in ins if i["x"] > -20000]     # отбросить легенду слева
    print("вставок: %d, полилиний ModelSpace: %d\n" % (len(ins), len(polys)))

    ours_marker = [i for i in ins if i["n"].startswith("AFRAME_")]
    print("ДОСТОВЕРНО НАШИ (блоки AFRAME_*): %d — %s"
          % (len(ours_marker),
             dict(collections.Counter(i["n"] for i in ours_marker))))
    print("кляммеров на слое _01_ПС_КЛЯММЕРЫ: %d\n"
          % sum(1 for i in ins if i["lay"] == "_01_ПС_КЛЯММЕРЫ"))

    # ── РЯДОВАЯ (вертикальная схема) ────────────────────────────
    print("=== РЯДОВАЯ (sub_type=vertical, Вектор-1) ===")
    keep = lambda x: x < X_RYAD
    obl = [i for i in ins if i["lay"] == "Облицовка 4" and keep(i["x"])]
    krh = [i for i in ins if i["lay"] == "_-_4_Кронштейны" and keep(i["x"])]
    rah = [i for i in ins
           if i["lay"] == "_01_ПС_НАПРАВЛЯЮЩИЕ" and keep(i["x"])]
    klh = [i for i in ins if i["lay"] == "!5_Кляммеры" and keep(i["x"])]
    wins = windows(polys, keep)
    xs = [i["x"] for i in obl + krh]
    ys = [i["y"] for i in obl + krh]
    zx0, zy0, zx1, zy1 = min(xs), min(ys), max(xs), max(ys)
    jx = sorted(set(round(i["x"], 1) for i in rah))
    ry = sorted(set(round(i["y"], 1) for i in obl))
    print("  зона (%.0f, %.0f)-(%.0f, %.0f); проёмов=%d; осей стоек=%d"
          % (zx0, zy0, zx1, zy1, len(wins), len(jx)))
    # ФАКТ Э1: кронштейны строго на осях направляющих
    kx = set(round(i["x"], 1) for i in krh)
    print("  [ФАКТ] X кронштейнов ⊆ X направляющих: %d из %d"
          % (len(kx & set(jx)), len(kx)))
    print("  [ФАКТ] шаги осей стоек:", steps(jx).most_common(4))
    col = collections.defaultdict(list)
    for i in krh:
        col[round(i["x"], 1)].append(round(i["y"], 1))
    full = max(col.values(), key=len)
    print("  [ФАКТ] цепочка кронштейнов длинной колонки:", sorted(full))
    print("  [ФАКТ] её шаги:", steps(full).most_common(4))

    p = frame_plan({"op": "frame", "system": "Вектор-1",
                    "sub_type": "vertical",
                    "contours": [{"outer": [[zx0, zy0], [zx1, zy0],
                                            [zx1, zy1], [zx0, zy1]],
                                  "holes": [[[a, b], [c, b], [c, d], [a, d]]
                                            for a, b, c, d in wins]}],
                    "joints_x": jx, "floors_y": [], "floor_step": 3000.0,
                    "rows_y": ry})
    if not p.get("ok"):
        print("  ДВИЖОК ОТКАЗАЛ:", p.get("error"))
    else:
        report("кронштейны", p["brackets"], krh)
        report("направляющие", p["rails"], rah)
        report("кляммеры", p["clamps"], klh)
        same = cnt(p["brackets"]) & cnt(krh)
        print("  позиций кронштейнов совпало: %d" % len(same))
        ocol = collections.defaultdict(list)
        for b in p["brackets"]:
            ocol[round(b["x"], 1)].append(round(b["y"], 1))
        if ocol:
            k = max(ocol, key=lambda z: len(ocol[z]))
            print("  наша цепочка (X=%.1f):" % k, sorted(ocol[k])[:12])

    # ── ОРТОГОНАЛЬНАЯ ───────────────────────────────────────────
    print("\n=== ОРТОГОНАЛЬНАЯ (sub_type=ortho) ===")
    keep = lambda x: x > X_ORT
    obl = [i for i in ins if i["lay"] == "Облицовка 4" and keep(i["x"])]
    krh = [i for i in ins if i["n"].startswith("Кронш") and keep(i["x"])]
    klh = [i for i in ins if i["lay"] == "!5_Кляммеры" and keep(i["x"])]
    rah = [i for i in ins if i["lay"] in ("!3_Направляющие",
                                          "_01_ПС_НАПРАВЛЯЮЩИЕ")
           and keep(i["x"])]
    wins = windows(polys, keep)
    zx0, zy0, zx1, zy1 = outer_of(polys, keep, (0, 0, 1, 1))
    jx = sorted(set(round(i["x"], 1) for i in obl))
    ry = sorted(set(round(i["y"], 1) for i in obl))
    print("  зона (%.0f, %.0f)-(%.0f, %.0f); проёмов=%d"
          % (zx0, zy0, zx1, zy1, len(wins)))
    hy = sorted(set(round(i["y"], 1) for i in krh))
    print("  [ФАКТ] Y-уровни кронштейнов: старт %.1f (= низ+%.0f), шаги %s"
          % (hy[0], hy[0] - zy0, steps(hy).most_common(3)))
    # доп. кронштейны у граней проёмов — на каком отступе
    hx = sorted(set(round(i["x"], 1) for i in krh))
    offs = collections.Counter()
    for wx0, wy0, wx1, wy1 in wins:
        for x in hx:
            for edge in (wx0, wx1):
                if abs(abs(x - edge) - 100.0) < TOL:
                    offs[100] += 1
    print("  [ФАКТ] колонок ровно в 100 мм от грани проёма: %d" % offs[100])
    print("  [ФАКТ] шаги колонок:", steps(hx).most_common(5))

    p = frame_plan({"op": "frame", "system": "Ортогональная",
                    "sub_type": "ortho",
                    "contours": [{"outer": [[zx0, zy0], [zx1, zy0],
                                            [zx1, zy1], [zx0, zy1]],
                                  "holes": [[[a, b], [c, b], [c, d], [a, d]]
                                            for a, b, c, d in wins]}],
                    "joints_x": jx, "floors_y": [], "floor_step": 3000.0,
                    "rows_y": ry})
    if not p.get("ok"):
        print("  ДВИЖОК ОТКАЗАЛ:", p.get("error"))
    else:
        report("кронштейны", p["brackets"], krh)
        report("направляющие", p["rails"], rah)
        report("кляммеры", p["clamps"], klh)
        oy = sorted(set(round(b["y"], 1) for b in p["brackets"]))
        print("  наши Y-уровни: старт %.1f, шаги %s"
              % (oy[0], steps(oy).most_common(3)))
        ox = sorted(set(round(b["x"], 1) for b in p["brackets"]))
        print("  колонок: наше=%d его=%d, совпало X=%d"
              % (len(ox), len(hx), len(set(ox) & set(hx))))
        print("  ЕГО типы кляммеров:",
              collections.Counter(i["n"] for i in klh).most_common())
        print("  НАШИ типы кляммеров:",
              collections.Counter(c.get("kind")
                                  for c in p["clamps"]).most_common())


if __name__ == "__main__":
    d = sys.argv[1] if len(sys.argv) > 1 else \
        os.path.expanduser("~/poligon_tree.json")
    run(d)
