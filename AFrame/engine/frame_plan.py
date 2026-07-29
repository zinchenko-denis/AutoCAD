# -*- coding: utf-8 -*-
"""Расстановка подсистемы НВФ: направляющие (стойки) + кронштейны +
кляммеры (модуль AFrame, этап 3). Концепт — Facades/docs/STAGE3_FRAME.md.

Решения Дениса 24.07:
- порядок целевой «расчёт несущей способности → расстановка»; ПОКА шаги
  берутся из справочника систем (systems.json) и подтверждаются
  конструктором — модуль расчёта (этап 4) встанет ПЕРЕД этим движком;
- В16: рядовые кронштейны между несущими РАВНОМЕРНО («размазывая длину
  между перекрытиями на количество кронштейнов»);
- В18: зазор стыка направляющих — дефолт 10 (5–10 по системам);
- В17: угловая зона — типовой случай, ширина ≈ одной плитки; спецузлы
  углов (крест-накрест, короба жёсткости) НЕ моделируем.

Факты боевого DWG Ленпроспекта (эталон
atspec-testdata/dxf/facades/frame_lenprospekt/frame_ps.json):
стойки по осям с шагом 602.5–605; первый кронштейн ~300 от низа стойки,
дальше ~950 (3 шт на этаж ~3 м — сходится со словами Дениса); кляммеры:
стартовый на низе и над проёмами, рядовой на каждом горизонтальном
стыке (шаг 605), половинки на краях.

Модель (вход, JSON):
{
  "op": "frame",
  "system": "Standart" | {…переопределения…},  // имя из systems.json
  "contours": [{"outer": [[x,y]..], "holes": [[[x,y]..]..]}],
  "joints_x": [x, ...],   // ОСИ вертикальных рустов = оси стоек
                          // (из метки ATCLAD / кромок камней)
  "floors_y": [y, ...],   // отметки перекрытий (центр несущего — В15;
                          // стык направляющих центрован на отметке)
  "rows_y": [y, ...]      // низы горизонтальных швов раскладки —
                          // для кляммеров (опционально)
}

Выход: rails[{x,y0,y1,len}], brackets[{x,y,kind:"несущий"|"рядовой"}],
clamps[{x,y,kind:"стартовый"|"рядовой"}], summary, notes.

Стойка по оси jx рвётся проёмами, ПЕРЕКРЫВАЮЩИМИ jx по X (bbox).
Фидбэк Германа 26.07 (первый прогон ATFRAME):
- ось БЛИЖЕ edge_offset (100, «не ближе 100 мм от края — бетон
  колется») к грани проёма → на высоте проёма стойка идёт ОТДЕЛЬНЫМ
  куском, смещённым на ось «грань − edge_offset» (слева) /
  «грань + edge_offset» (справа); кронштейны и кляммеры куска — на
  смещённой оси; под/над проёмом — по оси руста;
- floors_y пуст, но задан floor_step → отметки перекрытий
  генерируются от низа контура шагом floor_step (этаж ~3000):
  направляющие не бывают «бесконечными»;
- направляющая длиннее rail_stock — note (хлыст 6000).
Сегменты стойки режутся отметками перекрытий: направляющая = кусок
между стыками (зазор rail_gap центрован на отметке). Кронштейны:
несущий на каждой отметке внутри сегмента; рядовые — равномерно в
промежутках (первый — bracket_start_offset от низа куска), шаг ≤
bracket_step (в угловой зоне — bracket_step_corner). Только stdlib
(движок замораживается PyInstaller).
"""
import json
import math
import os
import sys

EPS = 1e-6


def _closed(pts):
    p = [(float(a), float(b)) for a, b in pts]
    if len(p) >= 2 and abs(p[0][0] - p[-1][0]) < EPS and \
       abs(p[0][1] - p[-1][1]) < EPS:
        p = p[:-1]
    return p


def _bbox(poly):
    xs = [p[0] for p in poly]
    ys = [p[1] for p in poly]
    return min(xs), min(ys), max(xs), max(ys)


def _sub_y(spans, lo, hi):
    """Вычесть [lo, hi] из списка Y-интервалов (копия cladding_plan —
    модули развязаны сознательно, конвенция репо)."""
    out = []
    for a0, a1 in spans:
        c0, c1 = max(a0, lo), min(a1, hi)
        if c1 - c0 <= EPS:
            out.append((a0, a1))
            continue
        if c0 - a0 > EPS:
            out.append((a0, c0))
        if a1 - c1 > EPS:
            out.append((c1, a1))
    return out


def _systems_path(base_dir=None):
    """systems.json: рядом с exe (бандл, PyInstaller-onefile: __file__
    уходит в temp!), в _MEIPASS, рядом с .py (разработка)."""
    cands = []
    if base_dir:
        cands.append(base_dir)
    if getattr(sys, "frozen", False):
        cands.append(os.path.dirname(sys.executable))
        if hasattr(sys, "_MEIPASS"):
            cands.append(sys._MEIPASS)
    cands.append(os.path.dirname(os.path.abspath(__file__)))
    for d in cands:
        p = os.path.join(d, "systems.json")
        if os.path.exists(p):
            return p
    raise OSError("systems.json не найден рядом с движком (%s)"
                  % "; ".join(cands))


def load_system(name_or_dict, base_dir=None):
    """Система по имени из systems.json рядом с движком; dict —
    переопределение поверх имени в ключе "name" (или чистый dict)."""
    with open(_systems_path(base_dir), "r", encoding="utf-8") as f:
        allsys = json.load(f)["systems"]
    if isinstance(name_or_dict, dict):
        name = name_or_dict.get("name")
        base = dict(allsys.get(name, {})) if name else {}
        base.update({k: v for k, v in name_or_dict.items()
                     if k != "name"})
        base.setdefault("_name", name or "своя")
        return base
    if name_or_dict not in allsys:
        raise KeyError("система %r не найдена в systems.json"
                       % name_or_dict)
    s = dict(allsys[name_or_dict])
    s["_name"] = name_or_dict
    return s


def _span_points(a, b, step):
    """Точки от a до b ВКЛЮЧИТЕЛЬНО, равномерно с шагом ≤ step."""
    L = b - a
    if L <= EPS:
        return [a]
    k = max(1, int(math.ceil((L - EPS) / step)))
    return [a + L * j / k for j in range(k + 1)]


def _corner_ivals(x0, x1, czone, corners):
    """Интервалы угловых зон на [x0, x1]. corners=None — прежнее
    поведение (оба края контура); corners=[] — углов НЕТ (фидбэк
    Германа 27.07: угловая зона отсчитывается от УКАЗАННЫХ внешних
    углов здания, а не от краёв зоны); corners=[x..] — полосы czone
    в обе стороны от каждого угла."""
    if czone <= EPS:
        return []
    if corners is None:
        return [(x0, x0 + czone), (x1 - czone, x1)]
    out = []
    for c in corners:
        a, b = max(x0, c - czone), min(x1, c + czone)
        if b - a > EPS:
            out.append((a, b))
    out.sort()
    merged = []
    for a, b in out:
        if merged and a <= merged[-1][1] + EPS:
            merged[-1] = (merged[-1][0], max(merged[-1][1], b))
        else:
            merged.append((a, b))
    return merged


def _in_corner(x, x0, x1, czone, corners):
    return any(a - EPS <= x <= b + EPS
               for a, b in _corner_ivals(x0, x1, czone, corners))


def _hpos(x0, x1, margin, czone, step_main, step_corner,
          corners=None):
    """Горизонтальные позиции кронштейнов (межэтажная/ортогональная,
    ТЗ 26.07): от margin до margin от краёв; в угловых зонах шаг ≤
    step_corner, вне — ≤ step_main; равномерно по интервалам, границы
    зон — общие точки."""
    a, b = x0 + margin, x1 - margin
    if b - a <= EPS:
        return [(a + b) / 2.0]
    ivals = [(max(a, ia), min(b, ib))
             for ia, ib in _corner_ivals(x0, x1, czone, corners)
             if min(b, ib) - max(a, ia) > EPS]
    bounds = [a]
    for ia, ib in ivals:
        for v in (ia, ib):
            if a + EPS < v < b - EPS:
                bounds.append(v)
    bounds.append(b)
    bounds = sorted(set(bounds))
    pts = []
    for i in range(len(bounds) - 1):
        sa, sb = bounds[i], bounds[i + 1]
        mid = (sa + sb) / 2.0
        in_c = any(ia - EPS <= mid <= ib + EPS for ia, ib in ivals)
        seg = _span_points(sa, sb, step_corner if in_c else step_main)
        pts += seg if not pts else seg[1:]
    return pts


def _in_boxes(boxes, x, y):
    return any(bx0 - EPS < x < bx1 + EPS and by0 - EPS < y < by1 + EPS
               for bx0, by0, bx1, by1 in boxes)


def _rail_brackets(a, b, start_off, step):
    """Кронштейны ОДНОЙ направляющей [a, b] — ТЗ Германа 26.07:
    первый 300 от НИЗА, последний 300 от ВЕРХА, между ними равномерно
    с шагом ≤ расчётного (3000 → 300-800-800-800-300 = 4 шт);
    нестандартная длина — крайние 300/300, между ними ≤ шага;
    короче 2×300 — один кронштейн в центре."""
    L = b - a
    if L <= 2 * start_off + EPS:
        return [a + L / 2.0]
    lo, hi = a + start_off, b - start_off
    k = max(1, int(math.ceil((hi - lo - EPS) / step))) if step else 1
    return [lo + (hi - lo) * j / k for j in range(k + 1)]


def _piece_clamps(clamps, rows, s_lo, s_hi, s_x, side, fl_in):
    """Кляммеры куска стойки (ТЗ 26.07): боковой на оконных
    (смещённых/Z) стойках и их низе (примыкание к окну/отливу);
    стартовый на низе обычного куска (низ зоны / над откосом);
    рядовой на шве; КОМБИНИРОВАННЫЙ на первом шве выше каждого
    стыка-термошва."""
    if not rows:
        return
    clamps.append({"x": round(s_x, 4), "y": round(s_lo, 4),
                   "kind": "боковой" if side else "стартовый"})
    for ry in rows:
        if not (s_lo + EPS < ry < s_hi - EPS):
            continue
        kind = "боковой" if side else "рядовой"
        if not side and any(
                f < ry and not any(f < r2 < ry for r2 in rows)
                for f in fl_in):
            kind = "комбинированный"
        clamps.append({"x": round(s_x, 4), "y": round(ry, 4),
                       "kind": kind})


def _median_gap(vals, default):
    """Медиана интервалов сортированного списка (грузовая ширина из
    осей рустов / шаг перекрытий из отметок)."""
    if len(vals) < 2:
        return default
    iv = sorted(vals[i + 1] - vals[i] for i in range(len(vals) - 1))
    return iv[len(iv) // 2]


def _apply_calc(calc_req, system, sub, joints, floors, floor_step,
                corners=None):
    """Подбор шагов кронштейнов расчётом frame_calc (этап 4).

    Возвращает (report, err, (step_main, step_corner)). Пресет
    системы (systems.json "calc") перекрывается полями calc_req;
    обязательные от конструктора: terrain, height, q_clad, offset,
    na_max, wind_region|w0.
    """
    import frame_calc
    p = dict(system.get("calc") or {})
    p.update({k: v for k, v in calc_req.items() if v is not None})
    for f in ("terrain", "height", "q_clad", "offset", "na_max"):
        if p.get(f) in (None, ""):
            return None, "расчёт: не задано поле «%s»" % f, None
    if not p.get("w0") and not p.get("wind_region"):
        return None, "расчёт: не задан ветровой район", None
    if not p.get("bracket") or not p.get("profile"):
        return None, ("расчёт: для системы нет расчётного пресета "
                      "(кронштейн/профиль) — systems.json calc"), None
    b = float(p.get("b") or _median_gap(joints, 600.0))
    rail_len = float(p.get("rail_len") or floor_step or
                     _median_gap(floors, 3000.0))
    scheme = p.get("scheme") or \
        {"vertical": "vertical", "interfloor": "interfloor",
         "ortho": "ortho"}.get(sub, "vertical")
    inp = dict(scheme=scheme, terrain=str(p["terrain"]),
               height=float(p["height"]), q_clad=float(p["q_clad"]),
               gamma_clad=float(p.get("gamma_clad") or 1.1),
               q_rails=float(p.get("q_rails") or 0.0),
               offset=float(p["offset"]), na_max=float(p["na_max"]),
               bracket=str(p["bracket"]),
               extender=p.get("extender") or None,
               profile=p["profile"], b_row=b,
               b_corner=float(p.get("b_corner") or 0) or None,
               rail_len=rail_len,
               max_step=float(p.get("max_step") or 800.0),
               n_rivets=int(p.get("n_rivets") or 2))
    bc_note = None
    if not inp["b_corner"]:
        if scheme == "vertical":
            inp["b_corner"] = b
        else:
            bc = None
            czone = float(system.get("corner_zone") or 1500.0)
            if corners:
                cj = [j for j in joints
                      if any(abs(j - c) <= czone for c in corners)]
                if len(cj) >= 3:
                    iv = sorted(cj[i + 1] - cj[i]
                                for i in range(len(cj) - 1))
                    bc = iv[len(iv) // 2]
            if bc:
                inp["b_corner"] = bc
                bc_note = ("шаг направляющих в угловой зоне взят из "
                           "осей раскладки: %.0f" % bc)
            else:
                inp["b_corner"] = min(b, 450.0)
                bc_note = ("шаг направляющих в угловой зоне принят "
                           "%.0f (осей в угловой полосе нет) — "
                           "проверьте по раскладке"
                           % inp["b_corner"])
    if p.get("wind_region"):
        inp["wind_region"] = str(p["wind_region"])
    if p.get("w0"):
        inp["w0"] = float(p["w0"])
    if p.get("ice_region"):
        inp["ice_region"] = str(p["ice_region"])
    for k in ("e1", "e2", "e3", "e4", "ry"):
        if p.get(k) is not None:
            inp[k] = float(p[k])
    if scheme == "ortho":
        inp["v_step"] = float(p.get("v_step") or
                              system.get("ortho_v_step") or 600.0)
    try:
        rep = frame_calc.report(inp)
    except (KeyError, TypeError, ValueError) as e:
        return None, "расчёт: %s" % e, None
    for zone, name in (("row", "рядовой"), ("corner", "угловой")):
        if not rep[zone]["step"]:
            fails = []
            tried = rep[zone].get("tried") or []
            if tried:
                # падающие проверки на минимальном кандидате
                last = frame_calc.calc_chain(inp, tried[0][0],
                                             zone)
                fails = [c["name"] for c in last["checks"]
                         if not c["ok"]]
            hint = ("уменьшите шаг направляющих (b_corner) или "
                    "усильте профиль" if any("профиль" in f
                                             for f in fails)
                    else "усильте систему (кронштейн/профиль/анкер)")
            return None, ("расчёт: в %s зоне ни один шаг до %.0f мм "
                          "не проходит (%s) — %s"
                          % (name, inp["max_step"],
                             ", ".join(fails) or "все проверки",
                             hint)), None
    out = {"row": rep["row"]["chain"], "corner": rep["corner"]["chain"],
           "steps": {"main": rep["row"]["step"],
                     "corner": rep["corner"]["step"]},
           "scheme": scheme,
           "inputs": {k: inp[k] for k in
                      ("terrain", "height", "q_clad", "offset",
                       "na_max", "b_row", "b_corner", "rail_len",
                       "max_step")},
           "bc_note": bc_note}
    return out, None, (float(rep["row"]["step"]),
                       float(rep["corner"]["step"]))


def frame_plan(req):
    try:
        system = load_system(req.get("system") or "Standart",
                             req.get("_systems_dir"))
    except (KeyError, OSError, ValueError) as e:
        return {"ok": False, "error": str(e)}
    step_main = system.get("bracket_step")
    step_corner = system.get("bracket_step_corner")
    start_off = system.get("bracket_start_offset")
    corner_zone = float(system.get("corner_zone") or 0.0)
    gap = float(system.get("rail_gap") or 0.0)
    edge_off = float(system.get("edge_offset") or 0.0)
    overhang = float(system.get("edge_overhang") or 0.0)
    mid_over = system.get("mid_rail_over")
    mid_over = float(mid_over) if mid_over else None
    stock = float(system.get("rail_stock") or 0.0)
    floor_step = None
    try:
        fs = req.get("floor_step")
        if fs is not None and float(fs) > EPS:
            floor_step = float(fs)
    except (TypeError, ValueError):
        floor_step = None

    joints = []
    for x in req.get("joints_x") or []:
        try:
            joints.append(float(x))
        except (TypeError, ValueError):
            continue
    joints = sorted(set(joints))
    floors = []
    for y in req.get("floors_y") or []:
        try:
            floors.append(float(y))
        except (TypeError, ValueError):
            continue
    floors = sorted(set(floors))
    corners = None
    if req.get("corners_x") is not None:
        corners = []
        for x in req.get("corners_x") or []:
            try:
                corners.append(float(x))
            except (TypeError, ValueError):
                continue
        corners = sorted(set(corners))
    rows = []
    for y in req.get("rows_y") or []:
        try:
            rows.append(float(y))
        except (TypeError, ValueError):
            continue
    rows = sorted(set(rows))

    # тип подсистемы (ТЗ Германа 26.07): вертикальная (дефолт) /
    # межэтажная / ортогональная; комбинации — разными запусками
    sub = (req.get("sub_type") or "vertical").strip().lower()
    min_corner = float(system.get("min_from_corner") or 150.0)
    ortho_v = float(system.get("ortho_v_step") or 600.0)
    ortho_oh = float(system.get("ortho_max_overhang") or 300.0)

    notes, rails, brackets, clamps = [], [], [], []
    hrails, fittings = [], []
    if not joints:
        return {"ok": False,
                "error": "нет осей стоек (joints_x) — раскладка "
                         "ATCLAD не выбрана и оси не заданы"}

    # ── ЭТАП 4: шаги ПО РАСЧЁТУ (frame_calc), а не из справочника.
    # req["calc"] = исходные от конструктора (район/местность/высота/
    # вес облицовки/вынос/анкер); пресет системы systems.json["calc"]
    # даёт кронштейн/удлинитель/профиль/вес направляющих. Целевой
    # порядок Дениса 24.07: расчёт → шаги → расстановка.
    calc_rep = None
    if req.get("calc") is not None:
        calc_rep, cerr, csteps = _apply_calc(
            req.get("calc") or {}, system, sub, joints, floors,
            floor_step, corners)
        if cerr:
            return {"ok": False, "error": cerr}
        step_main, step_corner = csteps
        notes.append(
            "шаги кронштейнов ПО РАСЧЁТУ: рядовая %.0f / угловая %.0f "
            "(W_p=%.1f/%.1f кг/м²)"
            % (step_main, step_corner, calc_rep["row"]["w_p"],
               calc_rep["corner"]["w_p"]))
        if calc_rep.get("bc_note"):
            notes.append(calc_rep["bc_note"])

    for ci, c in enumerate(req.get("contours") or []):
        outer = _closed(c.get("outer") or [])
        if len(outer) < 4:
            notes.append("контур %d: меньше 4 вершин — пропуск"
                         % (ci + 1))
            continue
        holes = [_closed(h) for h in (c.get("holes") or [])]
        x0, y0, x1, y1 = _bbox(outer)
        hole_boxes = [_bbox(h) for h in holes]
        # отметки перекрытий: заданные, либо автогенерация шагом
        # этажа от низа контура (фидбэк Германа 26.07)
        floors_c = floors
        if not floors and floor_step:
            floors_c = []
            f = y0 + floor_step
            while f < y1 - EPS:
                floors_c.append(f)
                f += floor_step
            if floors_c:
                notes.append(
                    "контур %d: перекрытия автоматически шагом %.0f "
                    "(%d шт)" % (ci + 1, floor_step, len(floors_c)))

        # ── МЕЖЭТАЖНАЯ (ТЗ 26.07 §2) ──
        if sub == "interfloor":
            if not floors_c:
                notes.append("контур %d: межэтажная без отметок "
                             "перекрытий — задайте точки или шаг "
                             "этажа" % (ci + 1))
                continue
            step_m = float(step_main or 800.0)
            step_c = float(step_corner or step_m)
            for f in floors_c:
                # кронштейны по центру перекрытия, шаг по горизонтали
                for px in _hpos(x0, x1, min_corner, corner_zone,
                                step_m, step_c, corners):
                    if _in_boxes(hole_boxes, px, f):
                        continue          # точка попала в проём
                    brackets.append({"x": round(px, 4),
                                     "y": round(f, 4),
                                     "kind": "несущий"})
                # НГП — горизонтальный несущий профиль (рвётся окнами)
                segs = [(x0, x1)]
                for bx0, by0, bx1, by1 in hole_boxes:
                    if by0 - EPS < f < by1 + EPS:
                        segs = [(sa2, sb2) for sa, sb in segs
                                for sa2, sb2 in
                                (((sa, min(sb, bx0)),
                                  (max(sa, bx1), sb)))
                                if sb2 - sa2 > EPS]
                for sa, sb in segs:
                    if stock > EPS and sb - sa > stock + EPS:
                        notes.append("НГП на отм. %.0f длиной %.0f > "
                                     "хлыста %.0f" % (f, sb - sa,
                                                      stock))
                    hrails.append({"y": round(f, 4),
                                   "x0": round(sa, 4),
                                   "x1": round(sb, 4),
                                   "len": round(sb - sa, 4),
                                   "kind": "НГП"})
            # вертикальные профили по рустам (центр руста, БЕЗ
            # смещения у окон): между перекрытиями НСП; куски над/под
            # окнами — ШП-60-20
            for jx in joints:
                if jx < x0 - EPS or jx > x1 + EPS:
                    continue
                spans = [(y0, y1)]
                touch = []
                for bx0, by0, bx1, by1 in hole_boxes:
                    if bx0 - EPS < jx < bx1 + EPS:
                        spans = _sub_y(spans, by0, by1)
                        touch += [by0, by1]
                for s_lo, s_hi in spans:
                    if s_hi - s_lo <= EPS:
                        continue
                    is_shp = any(abs(s_lo - t) < 1 or abs(s_hi - t) < 1
                                 for t in touch)
                    fl_in = [f for f in floors_c
                             if s_lo + EPS < f < s_hi - EPS]
                    cuts = [s_lo] + fl_in + [s_hi]
                    for i in range(len(cuts) - 1):
                        a2 = cuts[i] + (gap / 2.0 if i > 0 else 0.0)
                        b2 = cuts[i + 1] - (gap / 2.0
                                            if i + 1 < len(cuts) - 1
                                            else 0.0)
                        if b2 - a2 <= EPS:
                            continue
                        rails.append({"x": round(jx, 4),
                                      "y0": round(a2, 4),
                                      "y1": round(b2, 4),
                                      "len": round(b2 - a2, 4),
                                      "kind": "ШП-60-20" if is_shp
                                      else "НСП"})
                    if not is_shp:
                        # вставки (ВС-300/В-70) на стыках НСП с НГП
                        for f in fl_in:
                            fittings.append({"x": round(jx, 4),
                                             "y": round(f, 4),
                                             "kind": "вставка"})
                    _piece_clamps(clamps, rows, s_lo, s_hi, jx,
                                  False, fl_in)
            # СП-60-40 в подоконной зоне на скобах С1 к крайним
            # межэтажным профилям
            jset = [j for j in joints if x0 - EPS <= j <= x1 + EPS]
            for bx0, by0, bx1, by1 in hole_boxes:
                left = [j for j in jset if j < bx0 - EPS]
                right = [j for j in jset if j > bx1 + EPS]
                if not left or not right:
                    continue
                la2, ra2 = max(left), min(right)
                hrails.append({"y": round(by0, 4),
                               "x0": round(la2, 4),
                               "x1": round(ra2, 4),
                               "len": round(ra2 - la2, 4),
                               "kind": "СП-60-40"})
                fittings.append({"x": round(la2, 4),
                                 "y": round(by0, 4),
                                 "kind": "скоба С1"})
                fittings.append({"x": round(ra2, 4),
                                 "y": round(by0, 4),
                                 "kind": "скоба С1"})
            continue

        # ── ОРТОГОНАЛЬНАЯ (ТЗ 26.07 §3) ──
        if sub == "ortho":
            step_m = float(step_main or 800.0)
            step_c = float(step_corner or step_m)
            xs_g = _hpos(x0, x1, min_corner, corner_zone,
                         step_m, step_c, corners)
            ys_g = []
            yy = y0 + float(start_off or 300.0)
            while yy < y1 - EPS:
                ys_g.append(yy)
                yy += ortho_v
            # сетка кронштейнов (кроме проёмов)
            for yy in ys_g:
                for px in xs_g:
                    if _in_boxes(hole_boxes, px, yy):
                        continue
                    brackets.append({"x": round(px, 4),
                                     "y": round(yy, 4),
                                     "kind": "рядовой"})
                # ГП-40-40 горизонтальный на каждом ряду сетки
                segs = [(x0, x1)]
                for bx0, by0, bx1, by1 in hole_boxes:
                    if by0 - EPS < yy < by1 + EPS:
                        segs = [(sa2, sb2) for sa, sb in segs
                                for sa2, sb2 in
                                (((sa, min(sb, bx0)),
                                  (max(sa, bx1), sb)))
                                if sb2 - sa2 > EPS]
                for sa, sb in segs:
                    hrails.append({"y": round(yy, 4),
                                   "x0": round(sa, 4),
                                   "x1": round(sb, 4),
                                   "len": round(sb - sa, 4),
                                   "kind": "ГП-40-40"})
            top_y = max(ys_g) if ys_g else y0
            # вертикальные ШП по рустам; у окон — Z-образные со
            # смещением 100 и выступом 50 (как вертикальная)
            for jx in joints:
                if jx < x0 - EPS or jx > x1 + EPS:
                    continue
                pieces = [(y0, y1, jx)]
                for bx0, by0, bx1, by1 in hole_boxes:
                    inside = bx0 - EPS < jx < bx1 + EPS
                    sx = None
                    if not inside and edge_off > EPS:
                        if jx <= bx0 + EPS and \
                                bx0 - jx < edge_off - EPS:
                            sx = bx0 - edge_off
                        elif jx >= bx1 - EPS and \
                                jx - bx1 < edge_off - EPS:
                            sx = bx1 + edge_off
                    if not inside and sx is None:
                        continue
                    sy0 = by0 - (overhang if sx is not None else 0.0)
                    sy1 = by1 + (overhang if sx is not None else 0.0)
                    nxt = []
                    for lo, hi, xe in pieces:
                        c0 = max(lo, sy0 if sx is not None else by0)
                        c1 = min(hi, sy1 if sx is not None else by1)
                        if c1 - c0 <= EPS or (xe != jx):
                            nxt.append((lo, hi, xe))
                            continue
                        if c0 - lo > EPS:
                            nxt.append((lo, c0, xe))
                        if not inside:
                            nxt.append((c0, c1, sx))
                        if hi - c1 > EPS:
                            nxt.append((c1, hi, xe))
                    pieces = nxt
                for s_lo, s_hi, s_x in pieces:
                    if s_hi - s_lo <= EPS:
                        continue
                    side = abs(s_x - jx) > EPS
                    if stock > EPS and s_hi - s_lo > stock + EPS:
                        notes.append("ШП X=%.0f длиной %.0f > хлыста "
                                     "%.0f" % (s_x, s_hi - s_lo,
                                               stock))
                    if not side and s_hi - top_y > ortho_oh + EPS:
                        notes.append("ШП X=%.0f: консольный свес "
                                     "%.0f > %.0f" %
                                     (s_x, s_hi - top_y, ortho_oh))
                    rails.append({"x": round(s_x, 4),
                                  "y0": round(s_lo, 4),
                                  "y1": round(s_hi, 4),
                                  "len": round(s_hi - s_lo, 4),
                                  "kind": "Z-профиль" if side
                                  else "ШП-60-20"})
                    _piece_clamps(clamps, rows, s_lo, s_hi, s_x,
                                  side, [])
            continue

        # ── ВЕРТИКАЛЬНАЯ (дефолт; ТЗ 26.07 §1) ──
        # доп. направляющая по центру плиты шире 600 (ТЗ 26.07):
        # пролёт между соседними осями больше порога → ось в середине
        jx_all = [j for j in joints if x0 - EPS <= j <= x1 + EPS]
        if mid_over and len(jx_all) >= 2:
            mids = []
            for i in range(len(jx_all) - 1):
                if jx_all[i + 1] - jx_all[i] > mid_over + EPS:
                    mids.append((jx_all[i] + jx_all[i + 1]) / 2.0)
            jx_all = sorted(jx_all + mids)

        for jx in jx_all:
            # угловая зона (В17: типовой случай — полоса у краёв зоны)
            in_corner = _in_corner(jx, x0, x1, corner_zone,
                                   corners)
            step = step_corner if in_corner else step_main

            # куски стойки (lo, hi, ось): проём, накрывающий ось по X,
            # ВЫРЕЗАЕТ диапазон; ось ближе edge_offset к грани проёма —
            # на высоте проёма кусок СМЕЩАЕТСЯ от грани (26.07: крепёж
            # не ближе 100 мм от края проёма)
            pieces = [(y0, y1, jx)]
            for bx0, by0, bx1, by1 in hole_boxes:
                inside = bx0 - EPS < jx < bx1 + EPS
                sx = None
                if not inside and edge_off > EPS:
                    if jx <= bx0 + EPS and bx0 - jx < edge_off - EPS:
                        sx = bx0 - edge_off      # слева от проёма
                    elif jx >= bx1 - EPS and jx - bx1 < edge_off - EPS:
                        sx = bx1 + edge_off      # справа от проёма
                if not inside and sx is None:
                    continue
                nxt = []
                # смещённый оконный кусок выступает за проём на
                # overhang сверху и снизу (ТЗ 26.07: длина = сторона
                # окна + 100, выступ 50/50)
                sy0 = by0 - (overhang if sx is not None else 0.0)
                sy1 = by1 + (overhang if sx is not None else 0.0)
                for lo, hi, xe in pieces:
                    c0, c1 = max(lo, sy0 if sx is not None else by0), \
                             min(hi, sy1 if sx is not None else by1)
                    if c1 - c0 <= EPS or (xe != jx):
                        # не пересекает по высоте / кусок уже смещён
                        nxt.append((lo, hi, xe))
                        continue
                    if c0 - lo > EPS:
                        nxt.append((lo, c0, xe))
                    if inside:
                        pass                     # вырез
                    else:
                        nxt.append((c0, c1, sx))
                    if hi - c1 > EPS:
                        nxt.append((c1, hi, xe))
                pieces = nxt

            for s_lo, s_hi, s_x in pieces:
                if s_hi - s_lo <= EPS:
                    continue
                side = abs(s_x - jx) > EPS       # оконный (смещённый)
                fl_in = [f for f in floors_c
                         if s_lo + EPS < f < s_hi - EPS]
                # направляющие: куски между стыками (стык центрован
                # на отметке перекрытия, зазор gap — В15/В18);
                # кронштейны — НА КАЖДУЮ направляющую: 300 от низа,
                # 300 от верха, между ними равномерно ≤ шага (ТЗ
                # Германа 26.07); первый кронштейн направляющей,
                # начавшейся стыком на перекрытии, — «несущий» (В-е)
                cuts = [s_lo] + fl_in + [s_hi]
                for i in range(len(cuts) - 1):
                    a = cuts[i] + (gap / 2.0 if i > 0 else 0.0)
                    b = cuts[i + 1] - (gap / 2.0
                                       if i + 1 < len(cuts) - 1
                                       else 0.0)
                    if b - a <= EPS:
                        continue
                    if stock > EPS and b - a > stock + EPS:
                        notes.append(
                            "направляющая X=%.0f длиной %.0f > хлыста "
                            "%.0f — задайте перекрытия" % (s_x, b - a,
                                                           stock))
                    rails.append({"x": round(s_x, 4),
                                  "y0": round(a, 4),
                                  "y1": round(b, 4),
                                  "len": round(b - a, 4),
                                  "kind": "направляющая"})
                    if step is not None and start_off is not None:
                        pos = _rail_brackets(a, b, float(start_off),
                                             float(step))
                        for pi, y in enumerate(pos):
                            kind = ("несущий" if i > 0 and pi == 0
                                    else "рядовой")
                            brackets.append({"x": round(s_x, 4),
                                             "y": round(y, 4),
                                             "kind": kind})
                # заглушка межэтажной (bracket_step null): несущие
                # на отметках — до своего генератора (FRAME_TZ §2)
                if step is None or start_off is None:
                    for f in fl_in:
                        brackets.append({"x": round(s_x, 4),
                                         "y": round(f, 4),
                                         "kind": "несущий"})
                _piece_clamps(clamps, rows, s_lo, s_hi, s_x, side,
                              fl_in)

    lm = sum(r["len"] for r in rails) / 1000.0
    hlm = sum(r["len"] for r in hrails) / 1000.0
    system_used = {k: v for k, v in system.items()
                   if not k.startswith("_src")}
    summary = {
        "system": system.get("_name", "?"),
        "sub_type": sub,
        "rails": len(rails),
        "rails_lm": round(lm, 2),
        "hrails": len(hrails),
        "hrails_lm": round(hlm, 2),
        "fittings": len(fittings),
        "rail_stock_est": (int(math.ceil(lm * 1000.0 / stock))
                           if stock > EPS else None),
        "brackets_main": sum(1 for b in brackets
                             if b["kind"] == "несущий"),
        "brackets_row": sum(1 for b in brackets
                            if b["kind"] == "рядовой"),
        "clamps_start": sum(1 for cl in clamps
                            if cl["kind"] == "стартовый"),
        "clamps_row": sum(1 for cl in clamps
                          if cl["kind"] == "рядовой"),
        "clamps_side": sum(1 for cl in clamps
                           if cl["kind"] == "боковой"),
        "clamps_combo": sum(1 for cl in clamps
                            if cl["kind"] == "комбинированный"),
    }
    out = {"ok": True, "rails": rails, "hrails": hrails,
           "brackets": brackets, "clamps": clamps,
           "fittings": fittings, "summary": summary, "notes": notes,
           "system_used": system_used}
    if calc_rep is not None:
        out["calc_report"] = calc_rep
        summary["calc_steps"] = calc_rep["steps"]
    return out
