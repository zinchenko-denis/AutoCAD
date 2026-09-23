# -*- coding: utf-8 -*-
"""Синтетика плана витража ABlockGen (vitrage_plan.build_plan, ревью 23.09)
против независимого геометрического оракула (shapely).

Сценарии: проём 800..8000 × 800..6000 мм, тело стойки/ригеля из боевых
профилей, шаг стоек или число пролётов, 0..3 ригеля, изредка ярусы стоек с
зазором (терморазрыв), fold заполнения.

Инварианты:
 V1 заполнения внутри проёма;
 V2 заполнения не перекрываются;
 V3 заполнения не заходят на тела стоек и ригелей;
 V4 баланс площади: заполнения + тела стоек + тела ригелей = проём (без
    ярусов с зазором и без отброшенных узких ячеек — они в ноте);
 V5 РАЗМЕР_ЗАП = «округл(Ш)+fold Х округл(В)+fold», Х кириллическая,
    и совпадает с dyn Ширина/Высота;
 V6 ДЛИНА ригеля = осевому шагу пролёта, ДЛИНА стойки = длине яруса;
    атрибут ДЛИНА = dyn Длина;
 V7 сдвиг чертежа сдвигает только координаты (марки, размеры те же);
 V8 детерминизм; V9 сводка = числу вставок по видам;
 V10 узкая ячейка по высоте (< min_fill) выброшена — с нотой (по ширине нота есть);
 V11 одинаковый свет заполнения (до 0.001 мм) → одинаковые РАЗМЕР_ЗАП и марка;
 V12 одинаковая длина ригеля (до 0.001 мм) → одна марка.

Запуск: PYTHONUTF8=1 python3 ABlockGen/tools/vitrage_synth.py [--seed N] [--n N]
"""
import argparse
import json
import os
import random
import sys
from collections import Counter

from shapely.geometry import box
from shapely.ops import unary_union

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "engine")))
import vitrage_plan as vp  # noqa: E402

T = 0.05   # мм
SHIFT = (47621.3, 24070.3)


def scenario(rng):
    W = rng.randrange(800, 8000, 5) + rng.choice([0, 0.4, 0.7])
    H = rng.randrange(800, 6000, 5)
    bw = rng.choice([50.0, 54.4, 60.0, 76.0])
    rw = rng.choice([45.6, 50.0, 60.0])
    grid = {}
    if rng.random() < 0.6:
        grid["step_x"] = rng.randrange(400, 1600, 5)
    else:
        grid["n_cols"] = rng.randrange(1, 8)
    k = rng.randrange(0, 4)
    grid["rail_y"] = sorted(rng.sample(range(300, int(H) - 300, 10), k)) if H > 700 + 20 * k else []
    tiers = None
    if rng.random() < 0.2 and H > 2000:
        gap = rng.choice([0, 10, 20])
        a = rng.randrange(800, int(H) - 800, 10)
        tiers = [a, H - a - gap]
        grid["tiers"], grid["tier_gap"] = tiers, gap
    x0, y0 = rng.uniform(-5000, 5000), rng.uniform(-2000, 2000)
    req = {"op": "plan", "opening": {"x0": x0, "y0": y0, "x1": x0 + W, "y1": y0 + H}, "grid": grid,
           "blocks": {"stand": {"name": "RF-стойка", "body_w": bw, "rot": 0},
                      "rigel": {"name": "RF-ригель", "body_w": rw, "rot": 270},
                      "fill": {"name": "RF-заполнение", "fold": rng.choice([15, 20])}},
           "marks": {"stand": "С{n}", "rigel": "Р{n}", "fill": "Сп{n}"}}
    return req


def check(req, viol, tag):
    try:
        p = vp.build_plan(json.loads(json.dumps(req)))
    except ValueError as e:
        return "отказ: %s" % e
    op = req["opening"]
    x0, y0, x1, y1 = op["x0"], op["y0"], op["x1"], op["y1"]
    bw = req["blocks"]["stand"]["body_w"]
    rw = req["blocks"]["rigel"]["body_w"]
    fold = req["blocks"]["fill"]["fold"]
    opening = box(x0, y0, x1, y1)
    st = [i for i in p["inserts"] if i["kind"] == "stand"]
    rg = [i for i in p["inserts"] if i["kind"] == "rigel"]
    fl = [i for i in p["inserts"] if i["kind"] == "fill"]
    axes = sorted({round(s["x"], 6) for s in st})
    stand_b = [box(s["x"] - bw / 2, s["y"], s["x"] + bw / 2, s["y"] + s["dyn"]["Длина"]) for s in st]
    rig_b = []
    for r in rg:
        L = r["dyn"]["Длина"]
        rig_b.append(box(r["x"] + bw / 2, r["y"] - rw / 2, r["x"] + L - bw / 2, r["y"] + rw / 2))
        nxt = [a for a in axes if a > r["x"] + T]
        if not nxt or abs((min(nxt) - r["x"]) - L) > T:
            viol.append(("V6", "%s ригель x=%.1f: ДЛИНА %.2f ≠ шагу осей %.2f" % (tag, r["x"], L,
                                                                              (min(nxt) - r["x"]) if nxt else -1)))
        if abs(float(r["attrs"]["ДЛИНА"]) - L) > 0.006:
            viol.append(("V6", "%s ригель: атрибут ДЛИНА %s ≠ dyn %.2f" % (tag, r["attrs"]["ДЛИНА"], L)))
    fill_b = [box(f["x"], f["y"], f["x"] + f["dyn"]["Ширина"], f["y"] + f["dyn"]["Высота"]) for f in fl]
    for f, b in zip(fl, fill_b):
        if not opening.buffer(T).contains(b):
            viol.append(("V1", "%s заполнение (%.1f, %.1f) вне проёма" % (tag, f["x"], f["y"])))
        w, h = f["dyn"]["Ширина"], f["dyn"]["Высота"]
        want = "%dХ%d" % (vp._rnd(w) + fold, vp._rnd(h) + fold)
        if f["attrs"]["РАЗМЕР_ЗАП"] != want or "\u0425" not in f["attrs"]["РАЗМЕР_ЗАП"]:
            viol.append(("V5", "%s РАЗМЕР_ЗАП %r, ожидали %r" % (tag, f["attrs"]["РАЗМЕР_ЗАП"], want)))
    for i in range(len(fill_b)):
        for j in range(i + 1, len(fill_b)):
            if fill_b[i].intersection(fill_b[j]).area > T:
                viol.append(("V2", "%s заполнения %d и %d перекрываются" % (tag, i, j)))
    frame = unary_union(stand_b + rig_b) if (stand_b or rig_b) else None
    if frame is not None:
        for f, b in zip(fl, fill_b):
            if b.intersection(frame).area > 1.0:
                viol.append(("V3", "%s заполнение (%.1f, %.1f) на теле профиля (%.1f мм²)"
                             % (tag, f["x"], f["y"], b.intersection(frame).area)))
                break
    min_fill = 50.0
    rails = sorted(y0 + r for r in req["grid"].get("rail_y") or [])
    lo = [y0] + [r + rw / 2 for r in rails]
    hi = [r - rw / 2 for r in rails] + [y1]
    thin = [h - l for l, h in zip(lo, hi) if 0 < h - l < min_fill]
    if thin and not any("пропущ" in n and "свет" in n and "ряд" in n for n in p.get("notes") or []):
        viol.append(("V10", "%s ячейка высотой %.1f мм (< %g) выброшена без ноты" % (tag, thin[0], min_fill)))
    spans = [b - a for a, b in zip(axes, axes[1:])]
    dropped = bool(thin) or any(sp - bw < min_fill for sp in spans)
    if not req["grid"].get("tiers") and not dropped and frame is not None:
        total = unary_union(fill_b + [frame]).area
        if abs(total - opening.area) > 1.0 + 1e-7 * opening.area:
            viol.append(("V4", "%s баланс: заполнения+профили %.1f мм², проём %.1f (Δ %.1f)"
                         % (tag, total, opening.area, total - opening.area)))
    by_size = {}
    for f in fl:
        k = (round(f["dyn"]["Ширина"], 3), round(f["dyn"]["Высота"], 3))
        v = (f["attrs"]["РАЗМЕР_ЗАП"], f["attrs"]["МАРКИРОВКА"])
        if k in by_size and by_size[k] != v:
            viol.append(("V11", "%s одинаковый свет %.3f×%.3f: %s/%s и %s/%s" % (tag, k[0], k[1], by_size[k][0],
                                                                             by_size[k][1], v[0], v[1])))
            break
        by_size.setdefault(k, v)
    by_len = {}
    for r in rg:
        k = round(r["dyn"]["Длина"], 3)
        if k in by_len and by_len[k] != r["attrs"]["ИМЯ"]:
            viol.append(("V12", "%s ригели длиной %.3f: марки %s и %s" % (tag, k, by_len[k], r["attrs"]["ИМЯ"])))
            break
        by_len.setdefault(k, r["attrs"]["ИМЯ"])
    s = p["summary"]
    if (s["stands"], s["rigels"], s["fills"]) != (len(st), len(rg), len(fl)):
        viol.append(("V9", "%s сводка %s ≠ вставкам %s" % (tag, (s["stands"], s["rigels"], s["fills"]),
                                                           (len(st), len(rg), len(fl)))))
    for s_ in st:
        if abs(float(s_["attrs"]["ДЛИНА"]) - s_["dyn"]["Длина"]) > 0.006:
            viol.append(("V6", "%s стойка: атрибут ДЛИНА ≠ dyn" % tag))
    # V7/V8
    q = json.loads(json.dumps(req))
    for k, d in (("x0", SHIFT[0]), ("x1", SHIFT[0]), ("y0", SHIFT[1]), ("y1", SHIFT[1])):
        q["opening"][k] += d
    p2 = vp.build_plan(q)

    def canon(pp, dx=0.0, dy=0.0):
        # порядок вставок — как выдал движок; координаты — с допуском, марки/размеры — строго
        return [(i["kind"], i["x"] - dx, i["y"] - dy, json.dumps(i["attrs"], sort_keys=True, ensure_ascii=False),
                 tuple(round(v, 3) for _, v in sorted(i["dyn"].items()))) for i in pp["inserts"]]
    a, b = canon(p), canon(p2, *SHIFT)
    bad_pairs = [(u, v) for u, v in zip(a, b)
                 if u[0] != v[0] or abs(u[1] - v[1]) > 0.01 or abs(u[2] - v[2]) > 0.01 or u[3] != v[3]]
    if bad_pairs or len(a) != len(b):
        u, v = bad_pairs[0] if bad_pairs else (None, None)
        viol.append(("V7", "%s сдвиг проёма меняет план (%d вставок), напр. %s → %s"
                     % (tag, len(bad_pairs), u and u[3], v and v[3])))
    if canon(vp.build_plan(json.loads(json.dumps(req)))) != a:
        viol.append(("V8", "%s повторный прогон дал другой план" % tag))
    return None


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=2309)
    ap.add_argument("--n", type=int, default=1500)
    a = ap.parse_args(argv)
    rng = random.Random(a.seed)
    viol, refused, ins = [], Counter(), 0
    for i in range(a.n):
        req = scenario(rng)
        r = check(req, viol, "#%d" % i)
        if r:
            refused[r.split(":")[1].strip()[:40]] += 1
    by = Counter(c for c, _ in viol)
    print("ВИТРАЖИ: сценариев %d, отказов движка %d, нарушений %d %s"
          % (a.n, sum(refused.values()), len(viol), dict(sorted(by.items()))))
    seen = set()
    for c, m in viol:
        if c not in seen:
            seen.add(c)
            print("  · %s %s" % (c, m[:320]))
    for r, n in refused.most_common(4):
        print("  [INFO] отказ ×%d: %s" % (n, r))
    return 1 if viol else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
