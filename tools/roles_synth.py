# -*- coding: utf-8 -*-
"""Синтетика «в ролях конструктора» (29.09, Денис: «прогоняем синтетические тесты на
поиск ошибок в разных ролях конструктора»). Цепочка фасада целиком — ATFZONE (зона, типы
проёмов, парапет) → раскладка (ATTILE под плитку / ATCLAD под керамогранит / без
раскладки) → ATFRAME (подсистема) — так, как её прошли бы разные конструкторы со своими
привычками. Настройки окна ATFRAME применяет НАСТОЯЩИЙ C# (FrameSettings под mono,
AFrame/tools/roles/RolesDump.cs): «поле окна не дошло до движка» (ГРАБЛЯ-13) ловится здесь.

Проверки:
 R0  действия роли применимы к окну; круг метки (ToDict→FromDict) даёт тот же запрос;
 R1  окно пропустило — движок не отказал; роль «неверный ввод» — окно её ловит (R10);
 A1  ATFZONE: нетто = брутто − (окна + витражи + двери); A2 отливы = низ окон, откосы =
     окна + двери; A3 линии схемы = суммам отчёта; A4 у дверей нет линии по низу;
 F*/T* — инварианты расстановки и шин (AFrame/tools/frame_synth.py) на этой же зоне;
 R3  керамогранит, вертикальная, с раскладкой: у каждой оси руста вдали от проёмов —
     направляющая;
 R4  плитка: кляммеров нет;
 R5  углы здания (вертикальная): на направляющих угловой зоны шаг кронштейнов ≤ угловому;
 R6  межэтажная по отметкам: несущие кронштейны — на отметках;
 R7  «только подсистема» — без кляммеров;
 R8  «только кляммеры» по направляющим полного прогона — те же кляммеры;
 R9  повторный ATFRAME со сменой облицовки: у каждой плитки своя марка шины, возврат к
     клинкеру возвращает его марку; каждый шаг проходит движок;
 X1  раскладка и подсистема не теряют зону ATFZONE.

Запуск (из корня репо): PYTHONUTF8=1 python3 tools/roles_synth.py [--n N] [--seed S] [--quick]
  [--dump DIR]. Нужны mono (mcs) и shapely; в CI (Windows) — ROLES_DUMP_EXE=<собранный
  RolesDump.exe>. Выход ≠ 0 при нарушениях."""
import argparse
import json
import re
import math
import os
import random
import subprocess
import sys
import tempfile
import time
from collections import Counter, defaultdict

from shapely.geometry import LineString, Polygon

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
for sub in ("AFrame/tools", "AFrame/engine", "AClad/engine", "Facades/engine"):
    sys.path.insert(0, os.path.join(ROOT, sub))
import frame_synth as fsy          # noqa: E402  (генераторы стен/окон и инварианты F*/T*)
import frame_engine as fre         # noqa: E402
import clad_engine as ce           # noqa: E402
import facades_engine as fze       # noqa: E402

T = 1.0
INFO = Counter()
DUMP = {}
TILES = {"attile290": ((290.0, 82.0), (7.0, 7.0)), "attile240": ((240.0, 71.0), (10.0, 10.0))}

# ── роли: действия в окне ATFRAME по порядку, как их сделал бы человек ──
ROLES = [
    dict(name="керамогранит по умолчанию", actions=[], layout="atclad", has_layout=True),
    dict(name="керамогранит: отметки перекрытий и углы по краям", actions=[], layout="atclad",
         has_layout=True, floors=True, corners="edges"),
    dict(name="керамогранит: межэтажная вручную 700/500", layout="atclad", has_layout=True, floors=True,
         actions=[("SetSubType", "interfloor"), ("Steps", "manual"), ("StepMain", 700), ("StepCorner", 500)]),
    dict(name="керамогранит: ортогональная, только подсистема", layout="atclad", has_layout=True,
         actions=[("SetSubType", "ortho"), ("Mode", "frame")]),
    dict(name="керамогранит: только кляммеры по готовой подсистеме", layout="atclad", has_layout=True,
         actions=[("SNAPSHOT",), ("Mode", "clamps")], clamps_after_full=True),
    dict(name="клинкер: вертикальная по расчёту, углы, угловой шаг 400, марка", layout="attile290",
         has_layout=True, corners="edges",
         actions=[("Cladding", "clinker"), ("RailBrand", "ШК-1"), ("TileStepHCorner", 400)]),
    dict(name="бетон: межэтажная по отметкам", layout="attile240", has_layout=True, floors=True,
         actions=[("Cladding", "concrete"), ("SetSubType", "interfloor"), ("RailBrand", "Б-2")]),
    dict(name="бетон: межэтажная без отметок (шаг этажа 3000)", layout="attile240", has_layout=True,
         actions=[("Cladding", "concrete"), ("SetSubType", "interfloor"), ("AskFloors", False),
                  ("FloorStep", 3000)]),
    dict(name="клинкер: ортогональная вручную 600/400, хлыст 3 м", layout="attile290", has_layout=True,
         corners="left",
         actions=[("Cladding", "clinker"), ("SetSubType", "ortho"), ("Steps", "manual"),
                  ("StepMain", 600), ("StepCorner", 400), ("TileWhip", 3000)]),
    dict(name="клинкер без раскладки ATTILE (ряды шагом 88)", layout=None, has_layout=False,
         actions=[("Cladding", "clinker"), ("RowStep", 88)]),
    dict(name="керамогранит без раскладки: оси шагом 608, швы 605", layout=None, has_layout=False,
         actions=[("AxisStep", 608), ("RowStep", 605)]),
    dict(name="переделщик: керамогранит → клинкер → бетон → снова клинкер", layout="attile290",
         has_layout=True, rerun=True,
         actions=[("SNAPSHOT",), ("Cladding", "clinker"), ("RailBrand", "ШК-40"), ("SNAPSHOT",),
                  ("Cladding", "concrete"), ("RailBrand", "Б-7"), ("SNAPSHOT",), ("Cladding", "clinker")]),
    dict(name="сметчик: витражи, двери, парапет; керамогранит", layout="atclad", has_layout=True,
         actions=[], vitrages=True, parapet=True),
    dict(name="невнимательный: шаг направляющих 50", invalid=True, layout=None, has_layout=True,
         actions=[("Cladding", "clinker"), ("TileStepH", 50)]),
    dict(name="невнимательный: плитка без раскладки и без шага рядов", invalid=True, layout=None,
         has_layout=False, actions=[("Cladding", "concrete"), ("RowStep", 0)]),
    dict(name="невнимательный: межэтажная без отметок и без шага этажа", invalid=True, layout=None,
         has_layout=True, actions=[("SetSubType", "interfloor"), ("AskFloors", False), ("FloorStep", 0)]),
    dict(name="невнимательный: расчёт с высотой здания 0", invalid=True, layout=None, has_layout=True,
         actions=[("Height", 0)]),
]


def dump_roles():
    """Действия ролей → настоящий C# (FrameSettings) → проверка окна, запрос, круг метки."""
    prebuilt = os.environ.get("ROLES_DUMP_EXE")
    if prebuilt:
        # CI (Windows): exe собран dotnet build AFrame/tools/roles/RolesDump.csproj
        runner = ([] if os.name == "nt" else ["mono"]) + [os.path.abspath(prebuilt)]
    else:
        exe = os.path.join(tempfile.gettempdir(), "roles_dump.exe")
        src = [os.path.join(ROOT, "AFrame", "tools", "roles", "RolesDump.cs"),
               os.path.join(ROOT, "AFrame", "src", "AFramePlugin", "FrameSettings.cs")]
        subprocess.check_call(["mcs", "-nologo", "-out:" + exe, "-r:System.Web.Extensions.dll"] + src,
                              stdout=subprocess.DEVNULL)
        runner = ["mono", exe]
    spec = [{"name": r["name"], "has_layout": bool(r.get("has_layout")),
             "floors_picked": 1 if r.get("floors") else 0,
             "actions": [list(a) for a in r["actions"]]} for r in ROLES]
    with tempfile.TemporaryDirectory() as d:
        ip, op = os.path.join(d, "in.json"), os.path.join(d, "out.json")
        with open(ip, "w", encoding="utf-8") as f:
            json.dump(spec, f, ensure_ascii=False)
        subprocess.check_call(runner + [ip, op])
        with open(op, encoding="utf-8-sig") as f:
            return dict((o["name"], o) for o in json.load(f))


# ── фасад роли ──
def facade(rng, role):
    fam = rng.choice(["rect", "rect", "L", "step", "U", "gable"])
    outer = fsy.make_outer(rng, fam)
    wall = Polygon(outer)
    holes = fsy._windows(rng, wall, rng.randrange(1, 6))
    x0, y0, x1, y1 = wall.bounds
    kinds = {}
    for i, h in enumerate(holes):
        hx0, hy0, hx1, hy1 = Polygon(h).bounds
        if abs(hy0 - y0) < 1e-6:
            kinds["H%d" % i] = "door"                       # дверь в пол
        elif role.get("vitrages") and hx1 - hx0 >= 1500 and rng.random() < 0.6:
            kinds["H%d" % i] = "vitrage"
        elif role.get("vitrages") and rng.random() < 0.2:
            kinds["H%d" % i] = "door"                       # балконная дверь в стене
    parapets = []
    if role.get("parapet"):
        parapets = [{"id": "PAR", "pts": fsy.rect(x0, y1, x1, y1 + 600)}]
    return fam, outer, holes, kinds, parapets


def check_zones(res, holes, kinds, bad, tag):
    z = res["zones"][0]
    rep = z["report"]
    s_ops = rep["window_area_m2"] + rep["vitrage_area_m2"] + rep["door_area_m2"]
    if abs(rep["area_outer_m2"] - s_ops - rep["area_net_m2"]) > 1e-6:
        bad.append(("A1", "%s: нетто %.4f ≠ брутто %.4f − проёмы %.4f" % (tag, rep["area_net_m2"],
                                                                      rep["area_outer_m2"], s_ops)))
    if abs(rep["sills_total_m"] - rep["window_sills_m"]) > 1e-9 or \
            abs(rep["jambs_total_m"] - rep["window_slopes_m"] - rep["door_slopes_m"]) > 1e-9:
        bad.append(("A2", "%s: отливы/откосы не сходятся с типами проёмов" % tag))
    tot = Counter()
    for ln in z["lines"]:
        tot[ln["cat"]] += ln["len_m"]
    for cat, key in (("window_slope", "window_slopes_m"), ("window_sill", "window_sills_m"),
                     ("door_slope", "door_slopes_m"), ("vitrage_side", "vitrage_side_m"),
                     ("vitrage_top", "vitrage_top_m"), ("vitrage_bottom", "vitrage_bottom_m")):
        if abs(tot[cat] - rep[key]) > 1e-6:
            bad.append(("A3", "%s: линии %s %.4f, отчёт %.4f" % (tag, cat, tot[cat], rep[key])))
            break
    for ln in z["lines"]:
        if kinds.get(ln["opening_id"]) == "door" and ln["cat"] != "door_slope":
            bad.append(("A4", "%s: у двери %s линия %s" % (tag, ln["opening_id"], ln["cat"])))
            break
        if kinds.get(ln["opening_id"]) == "door":
            hb = Polygon(holes[int(ln["opening_id"][1:])]).bounds
            if any(abs(p[1] - hb[1]) < 0.5 and abs(q[1] - hb[1]) < 0.5 and abs(q[0] - p[0]) > 1
                   for p, q in zip(ln["pts"], ln["pts"][1:])):
                bad.append(("A4", "%s: у двери %s линия по низу (порог)" % (tag, ln["opening_id"])))
                break


def layout(kind, zfull, zid):
    if kind == "atclad":
        req = {"op": "cladding", "tile": {"w": 600.0, "h": 600.0}, "gap": {"v": 8.0, "h": 8.0},
               "zones": [{"zone_id": zid, "zone": zfull}]}
    else:
        (w, h), (gv, gh) = TILES[kind]
        req = {"op": "tile_pattern", "tile": {"w": w, "h": h}, "gap": {"v": gv, "h": gh},
               "zones": [{"zone_id": zid, "zone": zfull}]}
    res = ce.run(req)
    if not res.get("ok"):
        return None, None, res.get("error")
    pz = [p for p in res.get("per_zone") or [] if p.get("zone_id") == zid]
    if not pz:
        return None, None, "раскладка потеряла зону %s" % zid
    return pz[0].get("joints_x") or [], pz[0].get("rows_y") or [], None


def manual_axes(outer, step, row_step):
    """Как C# ATFRAME без раскладки: первая ось — точка пользователя (здесь 300 от левого
    края), дальше шагом в обе стороны до 150 от краёв габарита; швы шагом от низа."""
    xs = [p[0] for p in outer]
    ys = [p[1] for p in outer]
    bx0, bx1, by0, by1 = min(xs), max(xs), min(ys), max(ys)
    first = bx0 + 300.0
    jx = []
    x = first
    while x >= bx0 + 150.0:
        jx.append(x)
        x -= step
    x = first + step
    while x <= bx1 - 150.0:
        jx.append(x)
        x += step
    ry = []
    if row_step >= 50:
        y = by0 + row_step
        while y < by1:
            ry.append(y)
            y += row_step
    return sorted(jx), ry


def floors_for(outer):
    ys = [p[1] for p in outer]
    y0, y1 = min(ys), max(ys)
    out, y = [], y0 + 3000.0
    while y < y1 - 300.0:
        out.append(y)
        y += 3000.0
    return out


def corners_for(outer, mode):
    xs = [p[0] for p in outer]
    if mode == "edges":
        return [min(xs), max(xs)]
    if mode == "left":
        return [min(xs)]
    return None


def frame_req(params, zid, zfull, jx, ry, floors, corners, per_zone=True):
    req = {"op": "frame", "contours": [], "joints_x": jx, "rows_y": ry, "floors_y": floors,
           "zones": [dict({"zone_id": zid, "zone": zfull},
                          **({"joints_x": jx, "rows_y": ry} if per_zone and jx else {}))]}
    req.update(json.loads(json.dumps(params)))
    if corners:
        req["corners_x"] = corners
    return req


def steps_of(res, params):
    rep = res.get("calc_report")
    if rep:
        return float(rep["steps"]["main"]), float(rep["steps"]["corner"])
    su = res.get("system_used") or {}
    sysd = params.get("system") or {}
    m = sysd.get("bracket_step") or su.get("bracket_step")
    c = sysd.get("bracket_step_corner") or su.get("bracket_step_corner") or m
    return (float(m) if m else None), (float(c) if c else None)


def run_role(rng, role, dump, n, stats, first, times):
    name = role["name"]
    d = dump[name]
    bad = []

    def add(code, msg):
        stats[name][code] += 1
        first.setdefault((name, code), msg)

    if d.get("apply_error"):
        add("R0", "действия не применились к окну: %s" % d["apply_error"])
        return 0
    if not d["roundtrip"]:
        add("R0", "метка подсистемы (ToDict→FromDict) даёт другой запрос движку")
    if role.get("invalid"):
        if d["valid"] is None:
            add("R10", "окно пропустило неверный ввод (%s)" % role["actions"])
        return 0
    if d["valid"] is not None:
        add("R1", "окно отвергло нормальную роль: %s" % d["valid"])
        return 0
    params = d["params"]
    done = 0
    for i in range(n):
        t0 = time.time()
        fam, outer, holes, kinds, parapets = facade(rng, role)
        tag = "%s/%s-%d" % (name[:24], fam, i)
        zr = fze.run({"op": "zones", "cladding": "облицовка", "zone_prefix": "Ф-", "start_index": 1,
                      "units": "mm", "contours": [{"id": "O", "pts": outer}] +
                      [{"id": "H%d" % k, "pts": h} for k, h in enumerate(holes)],
                      "opening_kinds": kinds, "parapets": parapets})
        if not zr.get("ok") or len(zr.get("zones") or []) != 1:
            add("X1", "%s: ATFZONE не дал зону (%s)" % (tag, zr.get("error") or zr.get("failed")))
            continue
        check_zones(zr, holes, kinds, bad, tag)
        if role.get("parapet") and not zr.get("parapets"):
            bad.append(("A1", "%s: парапет не посчитан" % tag))
        zid = zr["zones"][0]["zone_id"]
        zfull = zr["zones_full"][0]
        sub = params["sub_type"]
        tile = params["cladding"] in ("concrete", "clinker")
        lay = role.get("layout")
        if lay:
            jx, ry, err = layout(lay, zfull, zid)
            if err or not (jx or ry):
                if fam == "gable" and lay != "attile290":
                    # ATCLAD (керамогранит) неортогональный контур не раскладывает (зона пропускается с
                    # нотой) — конструктор идёт в ATFRAME без раскладки. ATTILE фронтон раскладывает
                    # с 29.09v (Герман, ответ на 6з PDF №28) — у него отказ здесь уже нарушение
                    INFO["фронтон: ATCLAD не раскладывает неортогональную зону — ATFRAME без раскладки"] += 1
                    lay = None
                else:
                    add("X1", "%s: раскладка: %s" % (tag, err or "пустая"))
                    continue
        if lay:
            per_zone = True
        else:
            rstep = float(d["settings"]["row_step"])
            jx, ry = manual_axes(outer, float(d["settings"]["axis_step"]), 0.0 if tile else rstep)
            if tile:
                jx = []
            per_zone = False
        floors = floors_for(outer) if role.get("floors") else []
        corners = corners_for(outer, role.get("corners"))
        runs = [params]
        if role.get("rerun"):
            runs = [s["params"] for s in d["snapshots"]] + [params]
        for pi, prm in enumerate(runs):
            if role.get("rerun") and pi == 0:
                # керамогранит до смены облицовки — на своей раскладке ATCLAD; фронтон ATCLAD
                # не раскладывает — как в C#, оси шагом из окна (снимок до смены облицовки)
                jx0, ry0, err0 = layout("atclad", zfull, zid)
                if err0 or not (jx0 or ry0):
                    s0 = d["snapshots"][0]["settings"]
                    jx0, ry0 = manual_axes(outer, float(s0["axis_step"]), float(s0["row_step"]))
                    req = frame_req(prm, zid, zfull, jx0, ry0, floors, corners, per_zone=False)
                else:
                    req = frame_req(prm, zid, zfull, jx0, ry0, floors, corners)
            else:
                req = frame_req(prm, zid, zfull, jx, ry, floors, corners, per_zone)
            res = fre.run(req)
            if not res.get("ok"):
                bad.append(("R1", "%s: окно пропустило, движок отказал: %s" % (tag, res.get("error"))))
                DUMP.setdefault((name, "R1"), req)
                continue
            psub = prm["sub_type"]
            ptile = prm["cladding"] in ("concrete", "clinker")
            sc = {"outer": outer, "holes": holes, "sub": psub, "req": req}
            su = res.get("system_used") or {}
            step_max = None
            if psub == "vertical":
                step_max = max(float(su.get("bracket_step") or 0), float(su.get("bracket_step_corner") or 0),
                               *(steps_of(res, prm)[0:1] or [0])) or None
            vp = fsy.check_place(sc, res, step_max)
            for c, _m in vp:
                DUMP.setdefault((name, c), req)
            bad += [(c, "%s: %s" % (tag, m)) for c, m in vp]
            if ptile:
                rows_used = [r for r in ry if Polygon(outer).bounds[1] < r < Polygon(outer).bounds[3]]
                if not lay:
                    ys = [p[1] for p in outer]
                    st = float(prm.get("tile_row_step") or 0)
                    rows_used, y = [], min(ys) + st
                    while st >= 50 and y < max(ys) - 1e-6:
                        rows_used.append(y)
                        y += st
                sc["tile"] = {"step": float(prm["tile_step_x"]), "sc": float(prm.get("tile_step_x_corner") or 0),
                              "whip": float(prm.get("tile_whip") or 2500), "rows": rows_used}
                if not (psub == "interfloor" and not floors and not prm.get("floor_step")):
                    bad += [(c, "%s: %s" % (tag, m)) for c, m in fsy.check_tile(sc, res)]
                    # 29.09l (прогон на эталоне 290×82): куски шины, под которыми нет ни одной
                    # направляющей (узкий простенок/проём уже шага, у межэтажной — нет оконных
                    # стоек) — вопрос Герману в PDF №28; пока справка, не нарушение
                    for n in res.get("notes") or []:
                        m = re.search(r"кусков шины без направляющей под ними (\d+)", n)
                        if m:
                            INFO["шины без направляющей под ними (кусков), %s" % psub] += int(m.group(1))
                if res["clamps"]:
                    bad.append(("R4", "%s: у плитки %d кляммеров" % (tag, len(res["clamps"]))))
            # R3: керамогранит, вертикальная, с раскладкой — у оси руста вдали от проёмов направляющая
            if not ptile and psub == "vertical" and lay and jx and not role.get("rerun") and \
                    prm.get("parts") != "clamps":
                wall = Polygon(outer)
                hb = [Polygon(h).bounds for h in holes]
                ys = wall.bounds
                for j in jx:
                    if any(b[0] - 250 <= j <= b[2] + 250 for b in hb):
                        continue
                    if wall.intersection(LineString([(j, ys[1] - 1), (j, ys[3] + 1)])).length < 400:
                        continue
                    if not any(abs(r["x"] - j) <= 2.0 for r in res["rails"]):
                        bad.append(("R3", "%s: у оси руста x=%.0f нет направляющей" % (tag, j)))
                        break
            # R5: углы (вертикальная): шаг кронштейнов на направляющих угловой зоны ≤ угловому
            if corners and psub == "vertical":
                main, corner = steps_of(res, prm)
                if corner:
                    for r in res["rails"]:
                        if not any(abs(r["x"] - c) <= 1500 - 1 for c in corners):
                            continue
                        yb = sorted(b["y"] for b in res["brackets"] if abs(b["x"] - r["x"]) <= T and
                                    r["y0"] - T <= b["y"] <= r["y1"] + T)
                        g = max((b - a for a, b in zip(yb, yb[1:])), default=0)
                        if g > corner + T:
                            bad.append(("R5", "%s: в угловой зоне x=%.0f шаг кронштейнов %.0f > углового %.0f"
                                        % (tag, r["x"], g, corner)))
                            break
            # R6: межэтажная по отметкам — несущие на отметках
            if psub == "interfloor" and floors:
                off = [b for b in res["brackets"] if b["kind"] == "несущий" and
                       not any(abs(b["y"] - f) <= T for f in floors)]
                if off:
                    bad.append(("R6", "%s: несущий кронштейн вне отметок (y=%.0f)" % (tag, off[0]["y"])))
            if psub == "interfloor" and not floors and prm.get("floor_step") and \
                    not any("перекрытия автоматически" in n_ for n_ in res.get("notes") or []) and \
                    (Polygon(outer).bounds[3] - Polygon(outer).bounds[1]) > prm["floor_step"]:
                bad.append(("R6", "%s: межэтажная без отметок — перекрытия шагом этажа не построены" % tag))
            # R7: только подсистема
            if prm.get("parts") == "frame" and res["clamps"]:
                bad.append(("R7", "%s: «только подсистема», а кляммеров %d" % (tag, len(res["clamps"]))))
            # R8: только кляммеры по направляющим полного прогона
            if role.get("clamps_after_full"):
                full = fre.run(frame_req(d["snapshots"][0]["params"], zid, zfull, jx, ry, floors, corners))
                q = frame_req(prm, zid, zfull, jx, ry, floors, corners)
                q["rails_fixed"] = [{"x": r["x"], "y0": r["y0"], "y1": r["y1"]} for r in full["rails"]]
                r5 = fre.run(q)
                key = lambda c: (round(c["x"], 3), round(c["y"], 3), c["kind"], c.get("orient"))
                if not r5.get("ok") or Counter(map(key, r5["clamps"])) != Counter(map(key, full["clamps"])):
                    bad.append(("R8", "%s: только кляммеры ≠ кляммерам полного прогона" % tag))
                if r5.get("ok") and (r5["rails"] or r5["brackets"]):
                    bad.append(("R8", "%s: «только кляммеры» выдал направляющие/кронштейны" % tag))
        done += 1
        times.append((time.time() - t0, tag))
    # R9: смена облицовки — марки шин у каждой плитки свои
    if role.get("rerun"):
        sn = d["snapshots"]
        brands = [s["params"].get("tile_rail_brand") for s in sn] + [params.get("tile_rail_brand")]
        if brands[1:] != ["ШК-40", "Б-7", "ШК-40"] or sn[0]["params"]["cladding"] != "porcelain":
            add("R9", "марки шин по шагам сценария: %s" % brands)
    for code, msg in bad:
        add(code, msg)
    return done


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=2909)
    ap.add_argument("--n", type=int, default=12, help="фасадов на роль")
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--dump", default=None, help="каталог: запросы ATFRAME первых нарушений")
    a = ap.parse_args(argv)
    n = 3 if a.quick else a.n
    t_all = time.time()
    dump = dump_roles()
    rng = random.Random(a.seed)
    stats, first, times = defaultdict(Counter), {}, []
    total = 0
    for role in ROLES:
        total += run_role(rng, role, dump, n, stats, first, times)
    print("РОЛИ: ролей %d, фасадов %d, время %.1f с" % (len(ROLES), total, time.time() - t_all))
    for role in ROLES:
        st = stats[role["name"]]
        print("  %-62s %s" % (role["name"], ", ".join("%s×%d" % kv for kv in sorted(st.items())) or "OK"))
    for (nm, code), msg in sorted(first.items()):
        print("  · %s [%s] %s" % (code, nm, msg))
    for k, v in sorted(INFO.items()):
        print("  [INFO] %s: %d" % (k, v))
    if a.dump:
        os.makedirs(a.dump, exist_ok=True)
        for (nm, code), req in DUMP.items():
            fn = "%s_%s.json" % (code, "".join(ch if ch.isalnum() else "_" for ch in nm)[:40])
            with open(os.path.join(a.dump, fn), "w", encoding="utf-8") as f:
                json.dump(req, f, ensure_ascii=False)
    times.sort(reverse=True)
    if times:
        print("  самые долгие: %s" % ", ".join("%s %.2fс" % (t[1], t[0]) for t in times[:3]))
    hard = sum(sum(v.values()) for v in stats.values())
    return 1 if hard else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
