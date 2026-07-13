# -*- coding: utf-8 -*-
"""VitrageGen Э3: распознавание каркаса витража из АР-графики.

Вход  — JSON: полосы (bbox прямоугольных элементов АР — вставки импостов,
        в перспективе контуры линий/полилиний), панели (вставки с именами —
        для дверей), блоки-образцы (указанные пользователем), марки.
Выход — ТОТ ЖЕ формат плана, что у vitrage_plan (Э1): inserts[] с
        block/layer/x/y/rot/dyn/attrs — C#-вставка общая.

Правила (доказаны файлом «Проба 3.dxf», разбор 13.07 — docs/CONTRACT.md §Э3):
  1. Вертикальные полосы толщиной strip_w (40..120) кластеризуются по
     X-интервалу и сливаются по Y в цепочку → СТОЙКА на всю высоту цепочки
     (АР рисует стойку сегментами между ригелями: 250+50+2400+900+300+285=4185).
  2. Ось стойки: средняя полоса (w≈body_w) → центр; КРАЙНЯЯ полоса шире
     body_w+5 («50мм + 25мм зазор» к откосу) → тело body_w прижато к
     ВНУТРЕННЕЙ стороне: ось = внутренняя грань ∓ body_w/2.
  3. Горизонтальные полосы группируются по оси Y → ОТМЕТКА ригелей;
     ригель ставится в пролёте, если полосы отметки покрывают >50% его света.
  4. ДВЕРЬ (панель с именем под door_pat): в перекрытых ею пролётах
     отметка-«порог» (ось в полосе [y0двери−60 .. y0двери+60] или внутри
     Y-габарита двери) — ригель НЕ ставится.
  5. Вставка — контракт Проба_штапики_2: стойка (ось, низ) rot=0,
     dyn «Длина» = высота; ригель (ось левой стойки, отметка) rot=270,
     dyn «Длина» = ОСЕВОЙ шаг пролёта. Марки — по типоразмерам.
"""
import json
import math
import sys

EPS = 1.0          # мм: допуск слияния координат АР
COVER_MIN = 0.5    # доля света пролёта, которую должна покрыть отметка


def _rnd05(v):
    return int(math.floor(float(v) + 0.5))


def fmt_len(v):
    return "{0:.2f}".format(float(v))


# ─────────────── классификация полос ───────────────

def classify_strips(strips, wmin, wmax):
    """→ (verticals, horizontals, cubes): списки bbox-кортежей (x0,y0,x1,y1)."""
    vert, horz, cube = [], [], []
    for s in strips:
        x0, y0, x1, y1 = (float(s["x0"]), float(s["y0"]),
                          float(s["x1"]), float(s["y1"]))
        if x1 < x0: x0, x1 = x1, x0
        if y1 < y0: y0, y1 = y1, y0
        w, h = x1 - x0, y1 - y0
        win = wmin <= w <= wmax
        hin = wmin <= h <= wmax
        if win and hin:
            cube.append((x0, y0, x1, y1))       # стыковой кубик ~50×50
        elif win and h > w:
            vert.append((x0, y0, x1, y1))
        elif hin and w > h:
            horz.append((x0, y0, x1, y1))
        # остальное (значки, стрелки, крупные панели) — не полосы, мимо
    return vert, horz, cube


def chain_verticals(vert, cube):
    """Группировка вертикалей по X-интервалу (±EPS), слияние Y с кубиками.
    → список {x0,x1,y0,y1,gaps} по возрастанию центра X."""
    groups = []
    for b in vert:
        for g in groups:
            if abs(g["x0"] - b[0]) <= EPS and abs(g["x1"] - b[2]) <= EPS:
                g["seg"].append((b[1], b[3])); break
        else:
            groups.append({"x0": b[0], "x1": b[2], "seg": [(b[1], b[3])]})
    for b in cube:  # кубики докидываются ТОЛЬКО в существующие X-группы
        for g in groups:
            if abs(g["x0"] - b[0]) <= EPS and abs(g["x1"] - b[2]) <= EPS:
                g["seg"].append((b[1], b[3])); break
    out = []
    for g in groups:
        seg = sorted(g["seg"])
        y0, y1 = seg[0][0], seg[0][1]
        gaps = []
        for a, b in seg[1:]:
            if a > y1 + EPS:
                gaps.append((y1, a))
            y1 = max(y1, b)
        out.append({"x0": g["x0"], "x1": g["x1"], "y0": y0, "y1": y1,
                    "gaps": gaps})
    out.sort(key=lambda z: (z["x0"] + z["x1"]) / 2.0)
    return out


def stand_axis(chain, body_w, is_left_edge, is_right_edge):
    """Ось стойки по полосе цепочки (правило крайних: тело изнутри)."""
    w = chain["x1"] - chain["x0"]
    if w > body_w + 5.0:
        if is_left_edge:
            return chain["x1"] - body_w / 2.0      # зазор снаружи (слева)
        if is_right_edge:
            return chain["x0"] + body_w / 2.0
    return (chain["x0"] + chain["x1"]) / 2.0


def group_rails(horz):
    """Горизонтали → отметки: группировка по оси Y (±EPS), объединение
    X-интервалов. → [{y, spans:[(x0,x1)]}] по возрастанию y."""
    rails = []
    for b in horz:
        ax = (b[1] + b[3]) / 2.0
        for r in rails:
            if abs(r["y"] - ax) <= EPS:
                r["spans"].append((b[0], b[2])); break
        else:
            rails.append({"y": ax, "spans": [(b[0], b[2])]})
    for r in rails:
        r["spans"].sort()
    rails.sort(key=lambda z: z["y"])
    return rails


def covered(spans, a, b, min_frac):
    """Покрывают ли интервалы spans отрезок [a,b] хотя бы на min_frac?"""
    if b <= a:
        return False
    total = 0.0
    for s0, s1 in spans:
        lo, hi = max(a, s0), min(b, s1)
        if hi > lo:
            total += hi - lo
    return total >= (b - a) * min_frac


# ─────────────── распознавание ───────────────

def recognize(req):
    blocks = req.get("blocks") or {}
    bs = blocks.get("stand") or {}
    br = blocks.get("rigel") or {}
    stand_name = bs.get("name")
    rigel_name = br.get("name")
    if not stand_name or not rigel_name:
        raise ValueError("blocks.stand.name и blocks.rigel.name обязательны (образцы)")
    body_w = float(bs.get("body_w") or 0.0)   # 0/None → вывести из ширин полос АР
    stand_prof = bs.get("prof") or stand_name
    rigel_prof = br.get("prof") or rigel_name

    params = req.get("params") or {}
    wmin = float(params.get("strip_w_min", 40.0))
    wmax = float(params.get("strip_w_max", 120.0))
    door_pat = [p.lower() for p in (params.get("door_pat") or ["дверь", "door"])]

    marks = req.get("marks") or {}
    m_stand = marks.get("stand", "С{n}")
    m_rigel = marks.get("rigel", "Р{n}")

    strips = req.get("strips") or []
    if not strips:
        raise ValueError("strips пуст — в выборе нет прямоугольной графики АР")

    notes = []
    vert, horz, cube = classify_strips(strips, wmin, wmax)
    chains = chain_verticals(vert, cube)
    if len(chains) < 2:
        raise ValueError("распознано меньше двух вертикальных полос (стоек) — "
                         "витраж не собрать (вертикалей: %d)" % len(chains))
    if body_w <= 0:
        # тело профиля = МИНИМАЛЬНАЯ ширина вертикальной цепочки (крайние полосы
        # с зазором к откосу шире тела; доказано «Проба 3»: 50 при крайних 75)
        body_w = min(c["x1"] - c["x0"] for c in chains)
        notes.append("тело профиля выведено из АР: %.1f мм" % body_w)
    for c in chains:
        for g0, g1 in c["gaps"]:
            notes.append("разрыв вертикали X=%.1f: %.1f..%.1f — слит"
                         % ((c["x0"] + c["x1"]) / 2.0, g0, g1))

    axes = []
    for i, c in enumerate(chains):
        axes.append(stand_axis(c, body_w, i == 0, i == len(chains) - 1))
    for i in range(1, len(axes)):
        if axes[i] - axes[i - 1] < body_w:
            raise ValueError("оси стоек слишком близко: %.1f и %.1f"
                             % (axes[i - 1], axes[i]))

    rails = group_rails(horz)
    if not rails:
        notes.append("горизонтальных полос не найдено — только стойки")

    # двери: панели с именем под паттерн
    doors = []
    for p in (req.get("panels") or []):
        nm = (p.get("name") or "").lower()
        if any(pat in nm for pat in door_pat):
            doors.append((float(p["x0"]), float(p["y0"]),
                          float(p["x1"]), float(p["y1"])))
    if doors:
        notes.append("дверей распознано: %d (пороги пропущены)" % len(doors))

    def door_blocks_rail(rail_y, a, b):
        """Порог: отметка у низа двери (±60) или внутри её габарита,
        в пролёте [a,b], перекрытом дверью >50% света."""
        for dx0, dy0, dx1, dy1 in doors:
            lo, hi = max(a, dx0), min(b, dx1)
            if hi - lo < (b - a) * COVER_MIN:
                continue
            if abs(rail_y - dy0) <= 60.0 or (dy0 - EPS <= rail_y <= dy1 + EPS):
                return True
        return False

    inserts = []

    def marker(tmpl):
        seen = {}
        def mk(key):
            if key not in seen:
                seen[key] = tmpl.replace("{n}", str(len(seen) + 1))
            return seen[key]
        return mk
    mk_stand, mk_rigel = marker(m_stand), marker(m_rigel)

    # стойки
    for ax, c in zip(axes, chains):
        ln = c["y1"] - c["y0"]
        inserts.append({
            "kind": "stand", "block": stand_name, "layer": "RF-стойки",
            "x": round(ax, 4), "y": round(c["y0"], 4), "rot": 0,
            "dyn": {"Длина": round(ln, 4)},
            "attrs": {"ИМЯ": mk_stand((stand_name, _rnd05(ln * 10))),
                      "ПРОФ": stand_prof, "ДЛИНА": fmt_len(ln)},
        })

    # ригели по отметкам × пролётам
    n_rig = 0
    skipped_doors = 0
    for r in rails:
        for i in range(len(axes) - 1):
            a_in = axes[i] + body_w / 2.0       # свет пролёта
            b_in = axes[i + 1] - body_w / 2.0
            if not covered(r["spans"], a_in, b_in, COVER_MIN):
                continue
            if door_blocks_rail(r["y"], a_in, b_in):
                skipped_doors += 1
                continue
            span = axes[i + 1] - axes[i]
            inserts.append({
                "kind": "rigel", "block": rigel_name, "layer": "RF-ригеля",
                "x": round(axes[i], 4), "y": round(r["y"], 4), "rot": 270,
                "dyn": {"Длина": round(span, 4)},
                "attrs": {"ИМЯ": mk_rigel((rigel_name, _rnd05(span * 10))),
                          "ПРОФ": rigel_prof, "ДЛИНА": fmt_len(span)},
            })
            n_rig += 1
    if skipped_doors:
        notes.append("порогов под дверями пропущено: %d" % skipped_doors)

    return {
        "ok": True,
        "inserts": inserts,
        "summary": {"stands": len(axes), "rigels": n_rig,
                    "rails": len(rails),
                    "axes_x": [round(a, 2) for a in axes]},
        "notes": notes,
    }


# ─────────────── CLI (тот же контракт, что vitrage_plan) ───────────────

def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    if len(argv) < 2:
        print(json.dumps({"ok": False,
                          "error": "usage: vitrage_recognize.py <req.json|-> [out.json]"},
                         ensure_ascii=False))
        return 2
    try:
        if argv[1] == "-":
            req = json.load(sys.stdin)
        else:
            with open(argv[1], "r", encoding="utf-8-sig") as f:
                req = json.load(f)
        plan = recognize(req)
    except ValueError as ex:
        plan = {"ok": False, "error": str(ex)}
    except Exception as ex:  # noqa
        plan = {"ok": False, "error": "%s: %s" % (type(ex).__name__, ex)}
    text = json.dumps(plan, ensure_ascii=False, indent=1)
    if len(argv) > 2:
        with open(argv[2], "w", encoding="utf-8") as f:
            f.write(text)
        print("ok" if plan.get("ok") else "error: %s" % plan.get("error"))
    else:
        print(text)
    return 0 if plan.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
