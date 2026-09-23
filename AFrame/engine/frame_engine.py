# -*- coding: utf-8 -*-
"""frame_engine — CLI движка AFrame (паттерн clad_engine: замороженный
exe, JSON через файлы): `frame_engine.exe in.json out.json`.

op="frame" — расстановка подсистемы (команда ATFRAME, бандл AFrame).

Вход (собирает C#-команда ATFRAME из метки ATCLAD + диалога):
{
  "op": "frame",
  "system": "Standart" | {"name": "...", …переопределения…},
  "zones":    [{"zone_id", "zone": {facade_zone/1}, "joints_x"?, "rows_y"?}, ...],
  "contours": [{"id", "pts", "bulges"?, "joints_x"?, "rows_y"?}, ...],   // голые
  // joints_x/rows_y у зоны/контура — СВОИ оси (метка раскладки этой зоны),
  // иначе общие joints_x/rows_y запроса (ручные оси «шагом/точками»)
  "joints_x": [x, ...],   // оси стоек (из метки ATCLAD)
  "floors_y": [y, ...],   // отметки перекрытий (диалог)
  "rows_y":   [y, ...],   // центры горизонтальных швов (метка ATCLAD)
  "parts"?: "all" | "frame" | "clamps",       // 23.09b: что раскладывать
  "rails_fixed"?: [{x, y0, y1}, ...]  // только кляммеры — по этим направляющим
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


_ARC_EPS = 1e-9


def _zone_to_contour(zd, notes, zone_id):
    """facade_zone/1 → {outer, holes} (как в clad_engine).

    23.09 (ревью): движок ждал zone.contour.pts — формат, которого нет ни в
    ATFZONE, ни в _fzones.json (там zone.outer.pts и openings[].poly.pts):
    зоны этапа 1 молча выпадали с нотой «меньше 3 вершин», подсистема
    строилась только по голым полилиниям. Прежний вид contour{pts} тоже
    принимаем (старые запросы/тесты). Единицы mm|m; дуги — пропуск с нотой
    (как у раскладки: кассеты/плитка по дугам не кладутся)."""
    units = zd.get("units") or "mm"
    k = {"mm": 1.0, "m": 1000.0}.get(units)
    if k is None:
        notes.append("%s: неизвестные единицы %r — пропуск" % (zone_id, units))
        return None
    src = zd.get("outer") or zd.get("contour") or {}
    bulges = list((src.get("bulges") or []))
    for op in zd.get("openings") or []:
        bulges += list(((op.get("poly") or op.get("contour") or {}).get("bulges")) or [])
    try:
        if any(abs(float(b)) > _ARC_EPS for b in bulges):
            notes.append("%s: контур с дугами — подсистема не строится, зона "
                         "пропущена" % zone_id)
            return None
    except (TypeError, ValueError):
        pass
    outer = _pts(src, notes, zone_id)
    if outer is None:
        return None
    holes = []
    for op in zd.get("openings") or []:
        h = _pts(op.get("poly") or op.get("contour") or op, notes,
                 "%s/проём" % zone_id)
        if h is not None:
            holes.append(h)
    if k != 1.0:
        outer = [(x * k, y * k) for x, y in outer]
        holes = [[(x * k, y * k) for x, y in h] for h in holes]
    return {"outer": outer, "holes": holes}


def _bbox(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def _pt_in_poly(poly, x, y):
    """Чёт-нечет лучом вправо (как cladding_plan._pt_in_poly)."""
    n = len(poly)
    inside = False
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            xi = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
            if xi > x:
                inside = not inside
    return inside


def _seg_pt_dist(p, a, b):
    ax, ay = a
    bx, by = b
    dx, dy = bx - ax, by - ay
    L2 = dx * dx + dy * dy
    if L2 <= 1e-18:
        return ((p[0] - ax) ** 2 + (p[1] - ay) ** 2) ** 0.5
    t = max(0.0, min(1.0, ((p[0] - ax) * dx + (p[1] - ay) * dy) / L2))
    return ((p[0] - ax - t * dx) ** 2 + (p[1] - ay - t * dy) ** 2) ** 0.5


def _on_edge(poly, p, tol):
    n = len(poly)
    return any(_seg_pt_dist(p, poly[i], poly[(i + 1) % n]) <= tol for i in range(n))


def _pip_ray(poly, x, y):
    n, inside = len(poly), False
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            if x1 + (y - y1) * (x2 - x1) / (y2 - y1) > x:
                inside = not inside
    return inside


def _contains_poly(outer, inner, tol=0.5):
    """inner ЦЕЛИКОМ внутри outer (граница — можно): вершины и середины
    подотрезков рёбер inner, разбитых точками касания/пересечения с outer,
    внутри или на границе outer.

    24.09 (независимая рецензия): вложенность определялась по центру
    габарита и среднему вершин — у невыпуклого контура (П, U, С) эти точки
    лежат в ВЫЕМКЕ, и отдельная зона, стоящая в выемке, «поглощала» его как
    проём: U-полоса 1,72 м² вокруг прямоугольника 5,28 м² — раскладка 5,28
    из 7,00 без предупреждения."""
    n, m = len(inner), len(outer)
    for p in inner:
        if not (_pip_ray(outer, p[0], p[1]) or _on_edge(outer, p, tol)):
            return False
    for i in range(n):
        a1, a2 = inner[i], inner[(i + 1) % n]
        dx, dy = a2[0] - a1[0], a2[1] - a1[1]
        L2 = dx * dx + dy * dy
        if L2 <= 1e-18:
            continue
        ts = [0.0, 1.0]
        for j in range(m):
            b1, b2 = outer[j], outer[(j + 1) % m]
            for q in (b1, b2):
                t = ((q[0] - a1[0]) * dx + (q[1] - a1[1]) * dy) / L2
                if 0.0 < t < 1.0 and _seg_pt_dist(q, a1, a2) <= tol:
                    ts.append(t)
            ex, ey = b2[0] - b1[0], b2[1] - b1[1]
            den = dx * ey - dy * ex
            if abs(den) > 1e-12:
                t = ((b1[0] - a1[0]) * ey - (b1[1] - a1[1]) * ex) / den
                u = ((b1[0] - a1[0]) * dy - (b1[1] - a1[1]) * dx) / den
                if 0.0 < t < 1.0 and -1e-9 <= u <= 1.0 + 1e-9:
                    ts.append(t)
        ts.sort()
        for k in range(len(ts) - 1):
            if ts[k + 1] - ts[k] <= 1e-12:
                continue
            tm = 0.5 * (ts[k] + ts[k + 1])
            q = (a1[0] + dx * tm, a1[1] + dy * tm)
            if not (_pip_ray(outer, q[0], q[1]) or _on_edge(outer, q, tol)):
                return False
    return True


def _group_contours(raw, notes):
    """Голые контуры: внешние + дыры по вложенности.

    23.09 (ревью): раньше — по вложенности ГАБАРИТОВ, и отдельная зона в
    «кармане» Г-образной стены считалась проёмом этой стены (без
    подсистемы), а ATCLAD/ATTILE её раскладывали. С 24.09 (независимая
    рецензия) — ПОЛНАЯ вложенность (_contains_poly), как clad_engine:
    проба по центру габарита у П/U-контура попадала в выемку. Вложенность
    глубже проёма — нота-пропуск. Контур несёт свои оси (joints_x/rows_y)
    — они переходят к зоне."""
    parsed = []   # (cid, pts, area, probe, src)
    for i, c in enumerate(raw):
        cid = str(c.get("id") or ("c%d" % (i + 1)))
        pts = _pts(c, notes, "контур %s" % cid)
        if pts is None:
            continue
        try:
            arc = any(abs(float(b)) > _ARC_EPS for b in (c.get("bulges") or []))
        except (TypeError, ValueError):
            arc = False
        if arc:
            # 24.09 (рецензия): дуга голой полилинии превращалась в хорду без
            # предупреждения; путь через зону ATFZONE такие отклонял
            notes.append("контур %s: дуги не поддерживаются — пропуск" % cid)
            continue
        if len(pts) >= 2 and abs(pts[0][0] - pts[-1][0]) <= 0.5 and \
           abs(pts[0][1] - pts[-1][1]) <= 0.5:
            pts = pts[:-1]
        if len(pts) < 3:
            notes.append("контур %s: меньше 3 вершин — пропуск" % cid)
            continue
        area = abs(sum(pts[j][0] * pts[(j + 1) % len(pts)][1] -
                       pts[(j + 1) % len(pts)][0] * pts[j][1]
                       for j in range(len(pts)))) / 2.0
        xs = [q[0] for q in pts]
        ys = [q[1] for q in pts]
        probe = [((min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0),
                 (sum(xs) / len(xs), sum(ys) / len(ys))]
        parsed.append((cid, pts, area, probe, c))
    n = len(parsed)
    cont = [-1] * n
    for i in range(n):
        best = -1
        for j in range(n):
            if i == j or parsed[j][2] <= parsed[i][2]:
                continue
            if _contains_poly(parsed[j][1], parsed[i][1]):
                if best < 0 or parsed[j][2] < parsed[best][2]:
                    best = j
        cont[i] = best

    def depth(i):
        d, cur = 0, cont[i]
        while cur >= 0 and d <= n:
            d += 1
            cur = cont[cur]
        return d

    tops, kids = [], {}
    for i in range(n):
        d = depth(i)
        if d == 0:
            tops.append(i)
        elif d == 1:
            kids.setdefault(cont[i], []).append(i)
        else:
            notes.append("контур %s: вложен глубже проёма — пропуск" % parsed[i][0])
    out = []
    for t in tops:
        cid, pts, _a, _pr, src = parsed[t]
        item = {"outer": pts, "holes": [parsed[k][1] for k in kids.get(t, [])]}
        for key in ("joints_x", "rows_y"):
            if src.get(key):
                item[key] = src.get(key)
        out.append((cid, item))
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
            for key in ("joints_x", "rows_y"):
                if zrec.get(key):
                    c[key] = zrec.get(key)
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
            "corners_x": req.get("corners_x"),
            # 01.08: выбор профиля (видимость динблока) — ГРАБЛЯ-13:
            # новые поля req пробрасывать в base ЯВНО
            "rail_profile": req.get("rail_profile"),
            "nsp_type": req.get("nsp_type"),
            # 04.08 (Герман п.1): ручной шаг ставится буквально
            "exact_step": req.get("exact_step"),
            # 04.08 (Герман п.2): стойки угловых/краевых зон
            "rail_step_corner": req.get("rail_step_corner"),
            "rail_step_main": req.get("rail_step_main"),
            "edge_rail_off": req.get("edge_rail_off"),
            "calc": req.get("calc"),
            # 23.09b (Герман): что раскладывать + существующие направляющие
            "parts": req.get("parts"),
            "rails_fixed": req.get("rails_fixed")}
    rails, brackets, clamps, per_zone = [], [], [], []
    hrails, fittings = [], []
    system_used, calc_report = None, None
    calc_reports = []
    for zone_id, contour in items:
        creq = dict(base)
        # 23.09 (ревью): оси швов — СВОИ у каждой зоны (из её метки
        # раскладки); общий список прогона давал в зоне стойки по швам
        # соседней зоны (цоколь под этажом: стоек 14 → 29)
        for key in ("joints_x", "rows_y"):
            if contour.get(key):
                creq[key] = contour.pop(key)
        creq["contours"] = [contour]
        res = fp.frame_plan(creq)
        if not res.get("ok"):
            return {"ok": False, "error": res.get("error"),
                    "notes": notes}
        system_used = res.get("system_used") or system_used
        if res.get("calc_report"):
            # 24.09 (рецензия): в ответе оставался отчёт ПОСЛЕДНЕЙ зоны, хотя
            # у зон свои оси и шаги — теперь все, верхний — самый жёсткий
            calc_reports.append({"zone_id": zone_id, "report": res["calc_report"]})
            def _smin(r):
                st = r.get("steps") or {}
                vals = [float(v) for v in (st.values() if isinstance(st, dict) else st)
                        if isinstance(v, (int, float))]
                return min(vals) if vals else float("inf")
            if calc_report is None or _smin(res["calc_report"]) < _smin(calc_report):
                calc_report = res["calc_report"]
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
        "zones": len(per_zone),
        "parts": str(req.get("parts") or "all")}
    out = {
        "ok": True, "rails": rails, "hrails": hrails,
        "brackets": brackets, "clamps": clamps,
        "fittings": fittings, "per_zone": per_zone, "notes": notes,
        "system_used": system_used, "summary": summary}
    if calc_report is not None:
        out["calc_report"] = calc_report
        out["calc_reports"] = calc_reports
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
