# -*- coding: utf-8 -*-
"""frame_engine — CLI движка AFrame (паттерн clad_engine: замороженный
exe, JSON через файлы): `frame_engine.exe in.json out.json`.

op="frame" — расстановка подсистемы (команда ATFRAME, бандл AFrame).

Вход (собирает C#-команда ATFRAME из метки ATCLAD + диалога):
{
  "op": "frame",
  "system": "Standart" | {"name": "...", …переопределения…},
  "zones":    [{"zone_id", "zone": {facade_zone/1}}, ...],
  "contours": [{"id", "pts", "bulges"?}, ...],   // голые полилинии
  "joints_x": [x, ...],   // оси стоек (из метки ATCLAD)
  "floors_y": [y, ...],   // отметки перекрытий (диалог)
  "rows_y":   [y, ...]    // центры горизонтальных швов (метка ATCLAD)
}

Выход: {"ok", rails, brackets, clamps, per_zone, summary, notes,
        system_used}. Конверсия зон facade_zone/1 и группировка голых
контуров по вложенности — те же правила, что у clad_engine
(реализация продублирована сознательно: модули развязаны)."""
import io
import json
import sys

import frame_plan as fp

EPS = 1e-6


def _pts(obj, notes, tag):
    pts = obj.get("pts") or obj.get("points") or []
    out = []
    for p in pts:
        try:
            out.append((float(p[0]), float(p[1])))
        except (TypeError, ValueError, IndexError):
            notes.append("%s: битая вершина — пропуск" % tag)
            return None
    if len(out) < 3:
        notes.append("%s: меньше 3 вершин — пропуск" % tag)
        return None
    return out


def _zone_to_contour(zd, notes, zone_id):
    """facade_zone/1 → {outer, holes} (как в clad_engine)."""
    cont = zd.get("contour") or {}
    outer = _pts(cont, notes, zone_id)
    if outer is None:
        return None
    holes = []
    for op in zd.get("openings") or []:
        h = _pts(op.get("contour") or op, notes,
                 "%s/проём" % zone_id)
        if h is not None:
            holes.append(h)
    return {"outer": outer, "holes": holes}


def _bbox(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def _inside(inner, outer):
    """bbox-вложенность + центр bbox внутри bbox (глубина ≤1 —
    упрощение как у clad_engine для голых контуров)."""
    ix0, iy0, ix1, iy1 = _bbox(inner)
    ox0, oy0, ox1, oy1 = _bbox(outer)
    return (ix0 >= ox0 - EPS and iy0 >= oy0 - EPS and
            ix1 <= ox1 + EPS and iy1 <= oy1 + EPS and
            (ix1 - ix0) * (iy1 - iy0) <
            (ox1 - ox0) * (oy1 - oy0) - EPS)


def _group_contours(raw, notes):
    """Голые контуры: внешние + дыры по bbox-вложенности."""
    polys = []
    for i, c in enumerate(raw):
        cid = str(c.get("id") or ("c%d" % (i + 1)))
        pts = _pts(c, notes, "контур %s" % cid)
        if pts is None:
            continue
        polys.append((cid, pts))
    outers = []
    for cid, pts in polys:
        if not any(_inside(pts, opts) for ocid, opts in polys
                   if ocid != cid):
            outers.append((cid, pts))
    out = []
    for cid, pts in outers:
        holes = [hp for hcid, hp in polys
                 if hcid != cid and _inside(hp, pts)]
        out.append((cid, {"outer": pts, "holes": holes}))
    return out


def op_frame(req):
    notes = []
    items = []
    for zrec in req.get("zones") or []:
        if not isinstance(zrec, dict):
            continue
        zone_id = str(zrec.get("zone_id") or "?")
        zd = zrec.get("zone")
        if not isinstance(zd, dict):
            notes.append("%s: нет геометрии зоны — пропуск" % zone_id)
            continue
        c = _zone_to_contour(zd, notes, zone_id)
        if c is not None:
            items.append((zone_id, c))
    for cid, c in _group_contours(req.get("contours") or [], notes):
        items.append(("контур %s" % cid, c))
    if not items:
        return {"ok": False,
                "error": "нет пригодных зон/контуров", "notes": notes}

    base = {"system": req.get("system"),
            "sub_type": req.get("sub_type"),
            "joints_x": req.get("joints_x"),
            "floors_y": req.get("floors_y"),
            "rows_y": req.get("rows_y"),
            "floor_step": req.get("floor_step"),
            "calc": req.get("calc")}
    rails, brackets, clamps, per_zone = [], [], [], []
    hrails, fittings = [], []
    system_used, calc_report = None, None
    for zone_id, contour in items:
        creq = dict(base)
        creq["contours"] = [contour]
        res = fp.frame_plan(creq)
        if not res.get("ok"):
            return {"ok": False, "error": res.get("error"),
                    "notes": notes}
        system_used = res.get("system_used") or system_used
        calc_report = res.get("calc_report") or calc_report
        for coll, dst in ((res["rails"], rails),
                          (res["brackets"], brackets),
                          (res["clamps"], clamps),
                          (res.get("hrails") or [], hrails),
                          (res.get("fittings") or [], fittings)):
            for t in coll:
                t = dict(t)
                t["zone"] = zone_id
                dst.append(t)
        for n in res["notes"]:
            notes.append("%s: %s" % (zone_id, n))
        s = res["summary"]
        per_zone.append({"zone_id": zone_id, "rails": s["rails"],
                         "rails_lm": s["rails_lm"],
                         "brackets_main": s["brackets_main"],
                         "brackets_row": s["brackets_row"]})
    lm = sum(r["len"] for r in rails) / 1000.0
    hlm = sum(r["len"] for r in hrails) / 1000.0
    stock = float((system_used or {}).get("rail_stock") or 0.0)
    import math
    summary = {
        "rails": len(rails), "rails_lm": round(lm, 2),
        "hrails": len(hrails), "hrails_lm": round(hlm, 2),
        "fittings": len(fittings),
        "rail_stock_est": (int(math.ceil(lm * 1000.0 / stock))
                           if stock > EPS else None),
        "brackets_main": sum(1 for b in brackets
                             if b["kind"] == "несущий"),
        "brackets_row": sum(1 for b in brackets
                            if b["kind"] == "рядовой"),
        "clamps_start": sum(1 for c in clamps
                            if c["kind"] == "стартовый"),
        "clamps_row": sum(1 for c in clamps
                          if c["kind"] == "рядовой"),
        "clamps_side": sum(1 for c in clamps
                           if c["kind"] == "боковой"),
        "clamps_combo": sum(1 for c in clamps
                            if c["kind"] == "комбинированный"),
        "zones": len(per_zone)}
    out = {
        "ok": True, "rails": rails, "hrails": hrails,
        "brackets": brackets, "clamps": clamps,
        "fittings": fittings, "per_zone": per_zone, "notes": notes,
        "system_used": system_used, "summary": summary}
    if calc_report is not None:
        out["calc_report"] = calc_report
        summary["calc_steps"] = calc_report["steps"]
    return out


def run(req):
    op = (req or {}).get("op")
    if op == "frame":
        return op_frame(req)
    return {"ok": False, "error": "неизвестный op: %r" % (op,)}


def main(argv):
    if len(argv) != 3:
        print("usage: frame_engine in.json out.json")
        return 2
    try:
        with io.open(argv[1], "r", encoding="utf-8") as f:
            req = json.load(f)
    except (OSError, ValueError) as e:
        res = {"ok": False, "error": "вход не прочитан: %s" % e}
    else:
        try:
            res = run(req)
        except Exception as e:                     # noqa: BLE001
            res = {"ok": False,
                   "error": "%s: %s" % (type(e).__name__, e)}
    with io.open(argv[2], "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False)
    return 0 if res.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
