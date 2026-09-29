# -*- coding: utf-8 -*-
"""Картинка и числа к PDF №27 (29.09c, ответ Германа по плитке): настоящий прогон
ATFRAME под клинкер на эталоне 290×82 (AClad/tools/testdata/tiles290). Ряды шин —
центры горизонтальных швов из раскладки ATTILE (движок AClad, как в чертеже),
подсистема — frame_engine; три типа подсистемы, шины трёх видов, хлысты 2500.
Пишет build27/tile_frame_290.png и печатает итоги по типам.
Запуск (из корня репо): PYTHONUTF8=1 python3 AFrame/docs/make_pics27.py
Нужны ezdxf, matplotlib (инструмент разработки, в сборку не входит)."""
import json
import os
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "AClad", "tools"))
sys.path.insert(0, os.path.join(ROOT, "AFrame", "engine"))
import ezdxf                          # noqa: E402
import tile_pattern_dxf as tpd        # noqa: E402
import frame_engine as fe             # noqa: E402

FIX = os.path.join(ROOT, "AClad", "tools", "testdata", "tiles290", "tiles290_input.dxf")
OUT = os.path.join(HERE, "build27", "tile_frame_290.png")


def attile_rows():
    doc = ezdxf.readfile(FIX)
    msp = doc.modelspace()
    sample, _colors = tpd.read_sample(msp, doc, None, "SAMPLE_")
    polys = tpd.closed_polys(msp, ["ZONE_TERRACOTTA", "ZONE_BEIGE"])
    contours = [{"id": "P%04d" % i, "pts": p["pts"]} for i, p in enumerate(polys)]
    req = {"op": "tile_pattern", "tile": {"w": 290.0, "h": 82.0}, "gap": {"v": 7.0, "h": 7.0},
           "sample": sample or None, "datum": {"mode": "bbox"}, "min_piece": 10.0, "kerf": 3.0,
           "merge_touching": True, "contours": contours}
    with tempfile.TemporaryDirectory() as d:
        ip, op = os.path.join(d, "in.json"), os.path.join(d, "out.json")
        with open(ip, "w", encoding="utf-8") as f:
            json.dump(req, f)
        subprocess.call([sys.executable, os.path.join(ROOT, "AClad", "engine", "clad_engine.py"), ip, op])
        with open(op, encoding="utf-8") as f:
            res = json.load(f)
    assert res.get("ok"), res.get("error")
    by = {}
    for pz in res["per_zone"]:
        # плоскость ATTILE (слитые касающиеся контуры) — ряды всем её членам
        mem = pz.get("members") or [str(pz.get("zone_id") or "").replace("контур ", "")]
        for oid in mem:
            oid = str(oid).replace("контур ", "")
            if oid:
                by[oid] = pz.get("rows_y") or []
    for c in contours:
        if c["id"] in by:
            c["rows_y"] = by[c["id"]]
    return contours, res["summary"]


def frame(contours, sub, **kw):
    req = {"op": "frame", "sub_type": sub, "parts": "frame", "cladding": "clinker",
           "tile_step_x": 600.0, "tile_whip": 2500.0, "tile_rail_brand": "ШК (пример)",
           "system": {"vertical": "Вектор-1", "interfloor": "Межэтажная", "ortho": "Ортогональная"}[sub],
           "contours": json.loads(json.dumps(contours)), "floors_y": [], "joints_x": [], "rows_y": [],
           "calc": {"wind_region": "II", "terrain": "B", "height": 30, "q_clad": 45, "offset": 230,
                    "na_max": 3000}}
    req.update(kw)
    t0 = time.time()
    res = fe.run(req)
    return res, time.time() - t0


def main():
    contours, tsum = attile_rows()
    print("эталон 290×82: контуров %d, плиток ATTILE %s" % (len(contours), tsum.get("tiles_total")))
    runs = {}
    for sub, kw in (("vertical", {}), ("interfloor", {"floor_step": 3000.0}), ("ortho", {})):
        res, dt = frame(contours, sub, **kw)
        assert res.get("ok"), res.get("error")
        s = res["summary"]
        runs[sub] = res
        print("%-10s: вертикальных %d (%.0f м.п.), гориз. направляющих %d (%.0f м.п.), кронштейнов %d, "
              "шины: старт %.0f м.п., рядовые %.0f м.п., конец %.0f м.п. = хлыстов %d; шаги %s; %.1f с"
              % (sub, s["rails"], s["rails_lm"], s["hguides"], s["hguides_lm"],
                 s["brackets_main"] + s["brackets_row"], s["shina_start_lm"], s["shina_row_lm"],
                 s["shina_end_lm"], s["shina_pieces"], s.get("calc_steps"), dt))
    draw(contours, runs["vertical"])


def draw(contours, res):
    """Одна зона эталона — с наибольшим числом окон (как в черновике №27), и крупно
    у её окна: только элементы этой зоны."""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.patches import Rectangle
    groups = fe._group_contours(json.loads(json.dumps(contours)), [])
    gid, grp = max(groups, key=lambda g: len(g[1]["holes"]))
    zone = "контур %s" % gid
    outer, holes = grp["outer"], grp["holes"]
    rails = [r for r in res["rails"] if r["zone"] == zone]
    hrs = [h for h in res["hrails"] if h["zone"] == zone]
    brs = [b for b in res["brackets"] if b["zone"] == zone]
    X0, X1 = min(p[0] for p in outer), max(p[0] for p in outer)
    Y0, Y1 = min(p[1] for p in outer), max(p[1] for p in outer)
    hb = sorted(((min(p[0] for p in h), min(p[1] for p in h), max(p[0] for p in h), max(p[1] for p in h))
                 for h in holes), key=lambda t: (t[1], t[0]))
    wx0, wy0, wx1, wy1 = hb[0]
    zx0, zx1 = max(X0 - 150, wx0 - 1500), wx1 + 1500
    zy0, zy1 = max(Y0 - 150, wy0 - 1100), wy1 + 700
    col = {"шина стартовая": "#1a9850", "шина рядовая": "#9a9a9a", "шина концевая": "#d73027"}
    fig, axs = plt.subplots(1, 2, figsize=(16, 7.6), gridspec_kw={"width_ratios": [1.45, 1]})
    for k, ax in enumerate(axs):
        for poly in [outer] + holes:
            q = list(poly) + [poly[0]]
            ax.plot([p[0] for p in q], [p[1] for p in q], color="black", lw=0.7 if k == 0 else 1.4)
        for h in hrs:
            if k == 1 and not (h["x1"] > zx0 and h["x0"] < zx1 and zy0 - 60 < h["y"] < zy1 + 60):
                continue
            c = col.get(h["kind"], "#4575b4")
            row = h["kind"] == "шина рядовая"
            ax.plot([h["x0"], h["x1"]], [h["y"], h["y"]], color=c, solid_capstyle="butt",
                    lw=(0.15 if row else 1.4) if k == 0 else (0.7 if row else 2.6))
            if k == 1 and h["x0"] > zx0:
                ax.plot([h["x0"], h["x0"]], [h["y"] - 22, h["y"] + 22], color="black", lw=1.1)
        for r in rails:
            if k == 1 and not (zx0 < r["x"] < zx1):
                continue
            ax.plot([r["x"], r["x"]], [r["y0"], r["y1"]], color="#4575b4", lw=0.45 if k == 0 else 2.6)
        if k == 1:
            pts = [(b["x"], b["y"]) for b in brs if zx0 < b["x"] < zx1 and zy0 < b["y"] < zy1]
            ax.plot([p[0] for p in pts], [p[1] for p in pts], "s", color="#313695", ms=5)
            ax.set_xlim(zx0, zx1)
            ax.set_ylim(zy0, zy1)
            ax.set_title("крупно у окна: синие — вертикальные направляющие шагом 600 и кронштейны;\n"
                         "серые — рядовые шины по центрам швов; зелёная — стартовая по низу зоны;\n"
                         "чёрные чёрточки — начало хлыста (2500 от левого края прогона)", fontsize=9.5)
        else:
            ax.add_patch(Rectangle((zx0, zy0), zx1 - zx0, zy1 - zy0, fill=False, ec="#f46d43", lw=1.8))
            ax.set_xlim(X0 - 400, X1 + 400)
            ax.set_ylim(Y0 - 400, Y1 + 400)
            ax.set_title("зона эталона 290×82 (%d окон): вертикальная под клинкер;\n"
                         "зелёная — стартовая шина, красная — концевая, серые — рядовые" % len(holes), fontsize=10)
        ax.set_aspect("equal")
        ax.set_xticks([])
        ax.set_yticks([])
    fig.tight_layout()
    fig.savefig(OUT, dpi=130)
    print("картинка:", OUT, "— зона", zone, "окон", len(holes))


if __name__ == "__main__":
    main()
