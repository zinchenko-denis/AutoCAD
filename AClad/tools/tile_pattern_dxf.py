#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""tile_pattern_dxf.py — обкатка движка мелкоштучной раскладки (op=
tile_pattern) БЕЗ AutoCAD: читает DXF, зовёт clad_engine, пишет
DXF + xlsx со спецификацией. Повторяет то, что делает C#-команда
ATTILE, — для проверки алгоритма и раскладок Германа на живых файлах.

Это ИНСТРУМЕНТ РАЗРАБОТКИ (ezdxf/openpyxl), в замороженный движок НЕ
входит. Установка: pip install ezdxf openpyxl.

Вход (DXF):
  · зоны  — замкнутые LWPOLYLINE на слоях --zone-layers (вложенная
    полилиния = проём; без --zone-layers берутся все замкнутые);
  · образец — замкнутые LWPOLYLINE или HATCH на слоях --sample-layers,
    тип плитки = слой, цвет = true_color/ACI слоя.

Пример (эталон 290×82, фикстура в репозитории):
  python tile_pattern_dxf.py testdata/tiles290/tiles290_input.dxf \\
      --zone-layers ZONE_TERRACOTTA ZONE_BEIGE \\
      --sample-layers-prefix SAMPLE_ \\
      --tile 290x82 --gap 7 \\
      --out out.dxf --xlsx out.xlsx
"""
import argparse
import json
import os
import subprocess
import sys

try:
    import ezdxf
except ImportError:
    sys.exit("нужен ezdxf: pip install ezdxf")

HERE = os.path.dirname(os.path.abspath(__file__))
ENGINE = os.path.normpath(os.path.join(HERE, "..", "engine", "clad_engine.py"))


def closed_polys(msp, layers=None):
    out = []
    for e in msp.query("LWPOLYLINE"):
        if layers and e.dxf.layer not in layers:
            continue
        pts = [(float(x), float(y)) for x, y, *_ in e.get_points("xyb")]
        if len(pts) < 3:
            continue
        out.append({"layer": e.dxf.layer, "pts": pts, "closed": bool(e.closed)})
    return out


def layer_rgb(doc, name):
    try:
        lay = doc.layers.get(name)
    except Exception:
        return None, 7
    aci = int(lay.dxf.color) if lay.dxf.hasattr("color") else 7
    tc = None
    if lay.dxf.hasattr("true_color") and lay.dxf.true_color is not None:
        tc = int(lay.dxf.true_color) & 0xFFFFFF
    return tc, abs(aci) if aci else 7


def entity_bbox(e):
    t = e.dxftype()
    if t == "LWPOLYLINE":
        xs = [p[0] for p in e.get_points("xy")]
        ys = [p[1] for p in e.get_points("xy")]
    elif t == "HATCH":
        xs, ys = [], []
        for path in e.paths:
            for v in getattr(path, "vertices", []):
                xs.append(v[0]); ys.append(v[1])
    else:
        return None
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def read_sample(msp, doc, layers=None, prefix=None):
    sample, colors = [], {}
    for t in ("LWPOLYLINE", "HATCH"):
        for e in msp.query(t):
            lay = e.dxf.layer
            if layers and lay not in layers:
                continue
            if prefix and not lay.startswith(prefix):
                continue
            bb = entity_bbox(e)
            if bb is None or bb[2] - bb[0] < 1 or bb[3] - bb[1] < 1:
                continue
            sample.append({"x0": bb[0], "y0": bb[1], "x1": bb[2], "y1": bb[3],
                           "type": lay})
            if lay not in colors:
                colors[lay] = layer_rgb(doc, lay)
    return sample, colors


def run_engine(req):
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        ip = os.path.join(d, "in.json"); op = os.path.join(d, "out.json")
        with open(ip, "w", encoding="utf-8") as f:
            json.dump(req, f)
        subprocess.call([sys.executable, ENGINE, ip, op])
        with open(op, encoding="utf-8") as f:
            return json.load(f)


def write_dxf(res, colors, path, tile_w, tile_h):
    doc = ezdxf.new("R2018", setup=True)
    doc.header["$INSUNITS"] = 4
    msp = doc.modelspace()
    types = sorted({p["type"] for p in res["pieces"]})
    for t in types:
        tc, aci = colors.get(t, (None, 7))
        for suf in ("", "_ПОДРЕЗКА", "_ПОЛОСКИ"):
            lname = ("Плитка_" + t + suf)[:255]
            lay = doc.layers.add(lname) if lname not in doc.layers \
                else doc.layers.get(lname)
            if tc is not None:
                lay.rgb = ((tc >> 16) & 255, (tc >> 8) & 255, tc & 255)
            else:
                lay.dxf.color = aci
        bname = "Плитка_" + t
        blk = doc.blocks.new(bname)
        pts = [(0, 0), (tile_w, 0), (tile_w, tile_h), (0, tile_h)]
        blk.add_lwpolyline(pts, close=True, dxfattribs={"layer": "0", "color": 0})
        h = blk.add_hatch(color=0, dxfattribs={"layer": "0"})
        h.paths.add_polyline_path(pts, is_closed=True)
    for p in res["pieces"]:
        t = p["type"]
        if p["full"]:
            msp.add_blockref("Плитка_" + t, (p["x"], p["y"]),
                             dxfattribs={"layer": "Плитка_" + t})
        else:
            lay = ("Плитка_" + t + ("_ПОЛОСКИ" if p.get("tiny") else "_ПОДРЕЗКА"))[:255]
            rings = p.get("rings") or [[[p["x"], p["y"]], [p["x"] + p["w"], p["y"]],
                                        [p["x"] + p["w"], p["y"] + p["h"]],
                                        [p["x"], p["y"] + p["h"]]]]
            for ring in rings:
                msp.add_lwpolyline(ring, close=True, dxfattribs={"layer": lay})
                hh = msp.add_hatch(color=256, dxfattribs={"layer": lay})
                hh.paths.add_polyline_path(ring, is_closed=True)
                break
    doc.saveas(path)


def write_xlsx(res, path):
    try:
        import openpyxl
    except ImportError:
        print("openpyxl не установлен — xlsx пропущен")
        return
    from openpyxl.styles import Font
    s = res["summary"]
    wb = openpyxl.Workbook(); ws = wb.active; ws.title = "По типам"
    ws.append(["Спецификация плитки %gx%g, руст %g/%g" %
               (s["tile"]["w"], s["tile"]["h"], s["gap"]["v"], s["gap"]["h"])])
    ws["A1"].font = Font(bold=True, size=13)
    ws.append([])
    hdr = ["Тип", "Целых, шт", "Кусков подрезки, шт", "Заготовок на подрезку, шт",
           "ИТОГО плиток, шт", "Минимум по площади, шт", "Плитка нетто, м²",
           "Отходы раскроя, м²", "Отходы, %"]
    ws.append(hdr)
    for c in range(1, len(hdr) + 1):
        ws.cell(row=3, column=c).font = Font(bold=True)
    for t, d in sorted(s["by_type"].items()):
        ws.append([t, d["full"], d["cut"], d["blanks_cut"], d["tiles_total"], d["tiles_by_area"],
                   round(d["area"] / 1e6, 2), round(d["waste_area"] / 1e6, 2), round(d["waste_pct"], 1)])
    ws.append(["Σ", s["full"], s["cut"], s["blanks_cut"], s["tiles_total"], s["tiles_by_area"],
               round(s["area_tiles"] / 1e6, 2), round(s["waste_area"] / 1e6, 2), round(s["waste_pct"], 1)])
    ws.append([])
    ws.append(["Заготовки на подрезку — раскрой кусков из целых плиток (полосы полной высоты — по ширине, "
               "полной ширины — по высоте, пропил %g мм; фигурные у углов проёмов — плитка на кусок). "
               "Полоски тоньше %g мм (%d шт., %.2f м²) поглощены рустами и в раскладку не входят. "
               "Запас на бой/брак не включён." % (s["kerf"], s["min_piece"], s["absorbed"]["count"],
                                                   s["absorbed"]["area"] / 1e6)])
    ws2 = wb.create_sheet("По зонам")
    ws2.append(["Зона", "Датум X", "Датум Y", "S зоны, м²", "S плитки, м²",
                "Целых", "Кусков подрезки", "Заготовок на подрезку", "Полосок поглощено"])
    for pz in res["per_zone"]:
        ws2.append([pz["zone_id"], round(pz["datum"]["x"], 1), round(pz["datum"]["y"], 1),
                    round(pz["area_zone"] / 1e6, 2), round(pz["area_tiles"] / 1e6, 2),
                    pz["full"], pz["cut"], pz["blanks_cut"], pz["absorbed"]["count"]])
    wb.save(path)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dxf")
    ap.add_argument("--zone-layers", nargs="*")
    ap.add_argument("--sample-layers", nargs="*")
    ap.add_argument("--sample-layers-prefix")
    ap.add_argument("--tile", default="290x82", help="ШxВ, мм")
    ap.add_argument("--gap", default="7", help="руст, мм (или В,Г)")
    ap.add_argument("--datum", default="bbox", choices=["bbox", "wall"],
                    help="bbox — угол габарита (ответ Германа 09.09), wall — угол стены")
    ap.add_argument("--min-piece", type=float, default=10.0,
                    help="полоски тоньше поглощаются рустами")
    ap.add_argument("--kerf", type=float, default=3.0, help="пропил раскроя, мм")
    ap.add_argument("--no-merge", action="store_true",
                    help="не объединять смежные контуры в одну плоскость")
    ap.add_argument("--out", default="tiles_out.dxf")
    ap.add_argument("--xlsx", default="tiles_out.xlsx")
    a = ap.parse_args()

    tw, th = (float(v) for v in a.tile.lower().split("x"))
    gv = gh = float(a.gap.split(",")[0])
    if "," in a.gap:
        gh = float(a.gap.split(",")[1])

    doc = ezdxf.readfile(a.dxf)
    msp = doc.modelspace()
    sample, colors = read_sample(msp, doc, a.sample_layers, a.sample_layers_prefix)
    zone_polys = closed_polys(msp, a.zone_layers)
    if not zone_polys:
        sys.exit("зон не найдено (проверьте --zone-layers)")
    # плоские замкнутые полилинии — движок сам группирует вложенность
    # (внешний контур + проёмы), как это делает C#-команда ATTILE
    contours = [{"id": "P%04d" % i, "pts": p["pts"]}
                for i, p in enumerate(zone_polys)]
    print("контуров: %d, образец: %d плиток, типов: %d" %
          (len(contours), len(sample), len(colors)))

    req = {"op": "tile_pattern", "tile": {"w": tw, "h": th},
           "gap": {"v": gv, "h": gh},
           "sample": sample or None, "datum": {"mode": a.datum},
           "min_piece": a.min_piece, "kerf": a.kerf,
           "merge_touching": not a.no_merge, "contours": contours}

    res = run_engine(req)
    if not res.get("ok"):
        print("движок отказал:", res.get("error"))
        for n in res.get("notes", []):
            print("  ·", n)
        sys.exit(1)
    s = res["summary"]
    print("целых %d | кусков подрезки %d | полосок поглощено %d | заготовок на подрезку %d" %
          (s["full"], s["cut"], s["absorbed"]["count"], s["blanks_cut"]))
    print("ПЛИТОК ВСЕГО %d (по площади минимум %d) | плитка нетто %.1f м² | отходы раскроя %.1f м² = %.1f %% (пропил %g мм)" %
          (s["tiles_total"], s["tiles_by_area"], s["area_tiles"] / 1e6,
           s["waste_area"] / 1e6, s["waste_pct"], s["kerf"]))
    for n in res.get("notes", [])[:20]:
        print("  ·", n)
    write_dxf(res, colors, a.out, tw, th)
    write_xlsx(res, a.xlsx)
    print("записано:", a.out, "и", a.xlsx)


if __name__ == "__main__":
    main()
