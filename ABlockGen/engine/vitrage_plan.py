# -*- coding: utf-8 -*-
"""ABlockGen: расчёт плана витража (Э1, полуавтомат без распознавания).

Вход  — JSON-запрос: прямоугольник проёма + параметры сетки + имена блоков чертежа.
Выход — JSON-план: плоский список вставок (block/layer/x/y/rot/dyn/attrs),
        который C#-плагин вставляет вхождениями СУЩЕСТВУЮЩИХ определений блоков
        чертежа. Никаких своих определений блоков движок не выдумывает.

Блочный контракт (эталон Проба_штапики_2.dxf, разбор 13.07 — docs/CONTRACT.md):
  стойка  RF-стойки   rot=0,  insert=(ось X, низ Y), dyn «Длина», ATTRIB ИМЯ/ПРОФ/ДЛИНА
  ригель  RF-ригеля   rot=270, insert=(ось ЛЕВОЙ стойки, ось ригеля Y),
                      dyn «Длина» = ОСЕВОЙ шаг (тело в свету за счёт отступов
                      полширины стойки в самом определении), ATTRIB ИМЯ/ПРОФ/ДЛИНА
  заполн. RF-заполнения rot=0, insert=(левый край света, низ света),
                      dyn «Ширина»/«Высота» = свет,
                      ATTRIB МАРКИРОВКА, РАЗМЕР_ЗАП = «{округл(светШ)+fold}Х{округл(светВ)+fold}»
                      (Х — КИРИЛЛИЧЕСКАЯ, U+0425; fold=15 подтверждён эталоном
                      на Сп1/Сп2/Сп4/Вр1/Эм1 по обеим осям)

Осевая арифметика (эталон): ширина проёма = n_пролётов×шаг + ширина тела стойки
  (2169.4 = 3×705 + 54.4); ось крайней стойки = край проёма + body_w/2.

Запуск: python vitrage_plan.py <req.json|-> [out.json]  (без out — stdout, UTF-8).
"""
import json
import sys

EPS = 1e-9
X_CYR = "Х"          # кириллическая Х для РАЗМЕР_ЗАП
MIN_FILL_DEFAULT = 50.0   # мм: свет меньше — ячейка не заполняется (щель)


# ───────────────────────── утилиты ─────────────────────────

def _rnd(v):
    """Округление мм до целого, половина всегда вверх (не банковское)."""
    import math
    return int(math.floor(float(v) + 0.5))


def fmt_len(v):
    """Атрибут ДЛИНА: формат эталона '2715.00'."""
    return f"{float(v):.2f}"


def size_attr(w_clear, h_clear, fold):
    """РАЗМЕР_ЗАП: округлённый свет + fold, разделитель — кириллическая Х."""
    return f"{_rnd(w_clear) + int(fold)}{X_CYR}{_rnd(h_clear) + int(fold)}"


# ───────────────────────── сетка ─────────────────────────

def stand_axes(x0, x1, body_w, step_x=None, n_cols=None):
    """Оси стоек. Крайние: край проёма + body_w/2. Промежуточные: шаг слева
    направо (остаточный последний пролёт) либо n_cols равных долей.
    Возвращает (axes, notes)."""
    notes = []
    w = x1 - x0
    if w <= body_w:
        raise ValueError(f"проём уже тела стойки: {w:.1f} <= {body_w:.1f}")
    a0 = x0 + body_w / 2.0
    a1 = x1 - body_w / 2.0
    span_total = a1 - a0                      # межосевое расстояние крайних
    if n_cols is not None and n_cols > 0:
        step = span_total / n_cols
        axes = [a0 + i * step for i in range(n_cols)] + [a1]
        return axes, notes
    if not step_x or step_x <= 0:
        raise ValueError("нужен step_x > 0 либо n_cols")
    axes = [a0]
    x = a0 + step_x
    while x < a1 - EPS:
        axes.append(x)
        x += step_x
    axes.append(a1)
    last = axes[-1] - axes[-2]
    if abs(last - step_x) > 0.5:
        notes.append(f"остаточный пролёт {last:.1f} (шаг {step_x:g})")
    return axes, notes


def stand_tiers(y0, height, tiers=None, gap=0.0):
    """Ярусы стоек: список (y_низ, длина). Пусто → одна стойка на всю высоту.
    Несходимость сумм — заметка, считаем как задано."""
    notes = []
    if not tiers:
        return [(y0, height)], notes
    segs = []
    y = y0
    for i, ln in enumerate(tiers):
        if ln <= 0:
            raise ValueError(f"ярус {i + 1}: длина {ln} <= 0")
        segs.append((y, float(ln)))
        y += ln + (gap if i < len(tiers) - 1 else 0.0)
    total = sum(tiers) + gap * (len(tiers) - 1)
    if abs(total - height) > 0.5:
        notes.append(f"ярусы {total:.1f} != высота проёма {height:.1f} "
                     f"(зазор {gap:g}) — проверить")
    return segs, notes


def cell_rows(y0, y1, rail_axes, rigel_w, min_fill):
    """Вертикальные света ячеек между телами ригелей (и краями проёма).
    rail_axes — осевые Y ригелей (абсолютные). Возвращает [(низ, высота света)]."""
    bounds_lo = [y0] + [a + rigel_w / 2.0 for a in rail_axes]
    bounds_hi = [a - rigel_w / 2.0 for a in rail_axes] + [y1]
    rows = []
    for lo, hi in zip(bounds_lo, bounds_hi):
        h = hi - lo
        if h >= min_fill:
            rows.append((lo, h))
    return rows


# ───────────────────────── план ─────────────────────────

def build_plan(req):
    """Чистая функция: запрос → план. Исключения ValueError — ошибки входа."""
    op = req.get("opening") or {}
    for k in ("x0", "y0", "x1", "y1"):
        if k not in op:
            raise ValueError(f"opening.{k} отсутствует")
    x0, y0 = float(op["x0"]), float(op["y0"])
    x1, y1 = float(op["x1"]), float(op["y1"])
    if x1 < x0:
        x0, x1 = x1, x0
    if y1 < y0:
        y0, y1 = y1, y0
    W, H = x1 - x0, y1 - y0
    if W < 1 or H < 1:
        raise ValueError(f"вырожденный проём {W:.1f}×{H:.1f}")

    grid = req.get("grid") or {}
    blocks = req.get("blocks") or {}
    bs = blocks.get("stand") or {}
    br = blocks.get("rigel") or {}
    bf = blocks.get("fill") or {}
    stand_name = bs.get("name")
    rigel_name = br.get("name")
    fill_name = bf.get("name")
    if not stand_name:
        raise ValueError("blocks.stand.name обязателен (определение из чертежа)")
    body_w = float(bs.get("body_w", 50.0))
    rigel_w = float(br.get("body_w", 45.6))
    fold = float(bf.get("fold", 15))
    min_fill = float(bf.get("min_fill", MIN_FILL_DEFAULT))

    marks = req.get("marks") or {}
    m_stand = marks.get("stand", "С{n}")
    m_rigel = marks.get("rigel", "Р{n}")
    m_fill = marks.get("fill", "Сп{n}")

    notes = []
    axes, n1 = stand_axes(x0, x1, body_w,
                          grid.get("step_x"), grid.get("n_cols"))
    notes += n1
    segs, n2 = stand_tiers(y0, H, grid.get("tiers"), float(grid.get("tier_gap", 0)))
    notes += n2
    rail_rel = sorted(float(v) for v in (grid.get("rail_y") or []))
    for v in rail_rel:
        if v < -EPS or v > H + EPS:
            raise ValueError(f"отметка ригеля {v:g} вне высоты проёма 0..{H:g}")
    rail_axes = [y0 + v for v in rail_rel]

    inserts = []

    # марки по типоразмерам: одинаковый размер → одна марка
    def marker(tmpl):
        seen = {}
        def mk(key):
            if key not in seen:
                seen[key] = tmpl.replace("{n}", str(len(seen) + 1))
            return seen[key]
        return mk
    mk_stand, mk_rigel, mk_fill = marker(m_stand), marker(m_rigel), marker(m_fill)

    # стойки: по осям, ярусами снизу вверх
    for ax in axes:
        for (sy, ln) in segs:
            inserts.append({
                "kind": "stand", "block": stand_name, "layer": "RF-стойки",
                "x": round(ax, 4), "y": round(sy, 4), "rot": 0,
                "dyn": {"Длина": round(ln, 4)},
                "attrs": {"ИМЯ": mk_stand((stand_name, _rnd(ln * 10))),
                          "ПРОФ": stand_name, "ДЛИНА": fmt_len(ln)},
            })

    # ригели: каждая отметка × каждый пролёт; «Длина» = осевой шаг пролёта
    if rail_axes and not rigel_name:
        raise ValueError("blocks.rigel.name обязателен при заданных отметках ригелей")
    for ay in rail_axes:
        for i in range(len(axes) - 1):
            span = axes[i + 1] - axes[i]
            inserts.append({
                "kind": "rigel", "block": rigel_name, "layer": "RF-ригеля",
                "x": round(axes[i], 4), "y": round(ay, 4), "rot": 270,
                "dyn": {"Длина": round(span, 4)},
                "attrs": {"ИМЯ": mk_rigel((rigel_name, _rnd(span * 10))),
                          "ПРОФ": rigel_name, "ДЛИНА": fmt_len(span)},
            })

    # заполнения: ячейки (пролёт × ряд света)
    n_fills = 0
    if fill_name:
        rows = cell_rows(y0, y1, rail_axes, rigel_w, min_fill)
        for (lo, hc) in rows:
            for i in range(len(axes) - 1):
                wc = (axes[i + 1] - axes[i]) - body_w
                if wc < min_fill:
                    notes.append(f"пролёт {i + 1}: свет {wc:.1f} < {min_fill:g}, "
                                 f"заполнение пропущено")
                    continue
                sa = size_attr(wc, hc, fold)
                inserts.append({
                    "kind": "fill", "block": fill_name, "layer": "RF-заполнения",
                    "x": round(axes[i] + body_w / 2.0, 4), "y": round(lo, 4),
                    "rot": 0,
                    "dyn": {"Ширина": round(wc, 4), "Высота": round(hc, 4)},
                    "attrs": {"МАРКИРОВКА": mk_fill(sa), "РАЗМЕР_ЗАП": sa},
                })
                n_fills += 1
    else:
        notes.append("blocks.fill.name не задан — заполнения не генерируются")

    n_stand = len(axes) * len(segs)
    n_rigel = len(rail_axes) * (len(axes) - 1)
    return {
        "ok": True,
        "opening": {"x0": x0, "y0": y0, "x1": x1, "y1": y1, "w": W, "h": H},
        "inserts": inserts,
        "summary": {"stands": n_stand, "rigels": n_rigel, "fills": n_fills,
                    "axes_x": [round(a, 2) for a in axes]},
        "notes": notes,
    }


# ───────────────────────── CLI ─────────────────────────

def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    if len(argv) < 2:
        print(json.dumps({"ok": False, "error": "usage: vitrage_plan.py <req.json|-> [out.json]"},
                         ensure_ascii=False))
        return 2
    src = argv[1]
    try:
        if src == "-":
            req = json.load(sys.stdin)
        else:
            with open(src, "r", encoding="utf-8-sig") as f:
                req = json.load(f)
        plan = build_plan(req)
    except ValueError as ex:
        plan = {"ok": False, "error": str(ex)}
    except Exception as ex:  # noqa
        plan = {"ok": False, "error": f"{type(ex).__name__}: {ex}"}
    text = json.dumps(plan, ensure_ascii=False, indent=1)
    if len(argv) > 2:
        with open(argv[2], "w", encoding="utf-8") as f:
            f.write(text)
        print("ok" if plan.get("ok") else f"error: {plan.get('error')}")
    else:
        print(text)
    return 0 if plan.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
