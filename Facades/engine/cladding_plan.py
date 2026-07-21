# -*- coding: utf-8 -*-
"""Фаза 2 — облицовка: раскладка универсального блока в контурах.

Формализация — docs/CLADDING.md; правила уточнены ответами Германа
В1–В8 (20.07, Facades/docs/NEXT.md §ОТВЕТЫ ГЕРМАНА):
- В1: ниже отметки старта (datum) не кладём (note);
- В2: якоря проёмов действуют на ВСЮ высоту контура — вертикальные
  швы продолжаются от границ проёмов в рядах над/под ними;
- В4: min_cut = 150 мм (дефолт);
- В5: остаток простенка ≥300 — один подрезной в середине; <300 —
  целая плитка в середине, два равных подрезных по краям;
- В9 (ОТКРЫТ): подрезка < min_cut пропускается с note (по В4).

Вход/выход — JSON-словари (как vitrage_*): контуры — ортогональные
замкнутые полилинии (outer) с дырами-проёмами (holes), глобальная
сетка рядов от датума, два режима раскладки («edge» — от левого края,
«openings» — от границ проёмов). Только stdlib (движок замораживается
PyInstaller).
"""

EPS = 1e-6


def _closed(pts):
    """Замкнутый список вершин [[x,y],...] без дубля последней точки."""
    p = [(float(a), float(b)) for a, b in pts]
    if len(p) >= 2 and abs(p[0][0] - p[-1][0]) < EPS and \
       abs(p[0][1] - p[-1][1]) < EPS:
        p = p[:-1]
    return p


def _edges(poly):
    """Рёбра замкнутого полигона парами вершин."""
    n = len(poly)
    return [(poly[i], poly[(i + 1) % n]) for i in range(n)]


def _is_ortho(poly):
    return all(abs(a[0] - b[0]) < EPS or abs(a[1] - b[1]) < EPS
               for a, b in _edges(poly))


def _xs_at(polys, y):
    """X-пересечения горизонтали y с вертикальными рёбрами полигонов
    (чётно-нечётное правило) → отсортированные интервалы внутренности."""
    xs = []
    for poly in polys:
        for a, b in _edges(poly):
            if abs(a[0] - b[0]) < EPS:                  # вертикальное ребро
                y0, y1 = sorted((a[1], b[1]))
                # полуоткрытое [y0, y1): нижний конец включён — вершина-
                # стык двух рёбер не считается дважды
                if y0 - EPS <= y < y1 - EPS:
                    xs.append(a[0])
    xs.sort()
    return [(xs[i], xs[i + 1]) for i in range(0, len(xs) - 1, 2)]


def _hys_in(polys, y0, y1):
    """Y горизонтальных рёбер полигонов строго внутри (y0, y1)."""
    ys = set()
    for poly in polys:
        for a, b in _edges(poly):
            if abs(a[1] - b[1]) < EPS and y0 + EPS < a[1] < y1 - EPS:
                ys.add(a[1])
    return sorted(ys)


def _isect(iv1, iv2):
    """Пересечение двух списков интервалов."""
    out = []
    for a1, b1 in iv1:
        for a2, b2 in iv2:
            a, b = max(a1, a2), min(b1, b2)
            if b - a > EPS:
                out.append((a, b))
    out.sort()
    return out


def strip_bands(polys, y0, y1):
    """Полоса ряда [y0,y1] → подполосы [(sy0, sy1, [интервалы X])].

    Горизонтальные рёбра внутри полосы режут её на подполосы; в каждой
    X-интервалы постоянны (сэмпл в середине). Пустые подполосы
    опускаются. Ступени контура/верх ряда дают подполосы НИЖЕ высоты
    камня — подрезка по высоте получается сама (CLADDING.md §Алгоритм)."""
    cuts = [y0] + _hys_in(polys, y0, y1) + [y1]
    bands = []
    for i in range(len(cuts) - 1):
        a, b = cuts[i], cuts[i + 1]
        if b - a < EPS:
            continue
        iv = _xs_at(polys, (a + b) / 2.0)
        if iv:
            bands.append((a, b, iv))
    return bands


def contour_anchors(holes):
    """X-границы ВСЕХ проёмов контура — по ответу Германа В2 (20.07)
    вертикальные швы продолжаются от границ проёмов и в рядах выше/ниже
    них: якоря действуют на всю высоту контура, не только в полосе
    самого проёма."""
    anch = set()
    for h in holes:
        anch.add(min(p[0] for p in h))
        anch.add(max(p[0] for p in h))
    return anch


def _put(out, notes, x, wc, min_cut):
    if wc <= EPS:
        return
    if wc >= min_cut:
        out.append((x, wc))
    else:
        notes.append("подрезка %.0f < min_cut" % wc)


def _dedup_notes(notes):
    """Одинаковые заметки схлопываются со счётчиком «(×N)» — при
    min_cut=150 однотипных пропусков может быть много."""
    count, order = {}, []
    for n in notes:
        if n in count:
            count[n] += 1
        else:
            count[n] = 1
            order.append(n)
    return [n if count[n] == 1 else "%s (×%d)" % (n, count[n])
            for n in order]


def _row_edge(a, b, x_min, w, gv, min_cut, notes):
    """Режим «от края»: модульная сетка от x_min (левый край КОНТУРА —
    швы соосны по всей высоте); каждый столбец клипуется интервалом
    [a,b] — у проёма и у правого края вертикальный ряд подрезается."""
    out = []
    step = w + gv
    j = int((a - x_min + EPS) // step)
    while True:
        xj = x_min + j * step
        if xj >= b - EPS:
            break
        x0, x1 = max(xj, a), min(xj + w, b)
        _put(out, notes, x0, x1 - x0, min_cut)
        j += 1
    return out


# В5 (ответ Германа 20.07): остаток простенка ≥ CENTER_FULL_MIN —
# один подрезной камень в середине; меньше — ЦЕЛАЯ плитка в середине,
# два равных подрезных по краям (каждый = (остаток+камень)/2 ≥ 300
# при 600-м камне).
CENTER_FULL_MIN = 300.0


def _seg_wall(out, notes, a, b, w, gv, min_cut):
    """Простенок [a,b] — якоря с ОБЕИХ сторон (границы проёмов).

    В5 двухвариантный: n целых максимум, остаток rem = L - n·(w+gv);
    rem ≥ 300 — целые навстречу (слева на один больше при нечётном),
    остаток одним камнем в середине; rem < 300 — блок из n-1 целых
    центрируется, по краям два равных подрезных (rem+w)/2."""
    step = w + gv
    L = b - a
    n = int((L + gv + EPS) // step)
    if n == 0:
        # простенок уже камня: один подрезной на всю ширину
        _put(out, notes, a, min(L, w), min_cut)
        return
    rem = L - n * step
    if rem <= EPS or rem >= CENTER_FULL_MIN - EPS:
        nl = (n + 1) // 2
        for k in range(nl):
            out.append((a + k * step, w))
        if rem > EPS:
            _put(out, notes, a + nl * step, min(rem, w), min_cut)
        for k in range(n - nl):
            out.append((b - w - k * step, w))
    else:
        cut = (rem + w) / 2.0
        _put(out, notes, a, cut, min_cut)
        x = a + cut + gv
        for k in range(n - 1):
            out.append((x, w))
            x += step
        _put(out, notes, b - cut, cut, min_cut)


def _seg_anchored(out, notes, a, b, w, gv, min_cut, from_right):
    """Сегмент с якорем с ОДНОЙ стороны: целые от якоря, подрезка у
    противоположного (контурного) края."""
    step = w + gv
    L = b - a
    n = int((L + gv + EPS) // step)
    for k in range(n):
        out.append(((b - w - k * step) if from_right else (a + k * step), w))
    rem = L - n * step
    if rem > EPS:
        _put(out, notes, a if from_right else (a + n * step),
             min(rem, w), min_cut)


def _row_openings(a, b, anchors, w, gv, min_cut, notes):
    """Режим «от проёмов»: якоря — X-границы проёмов КОНТУРА (все, В2:
    швы простенков продолжаются в рядах над/под проёмами). Интервал
    режется внутренними якорями на сегменты; сегмент между якорями —
    простенок (В5), с якорем с одной стороны — целые от якоря и
    подрезка у края контура. Совсем без якорей — от левого края."""
    la = any(abs(a - x) < EPS for x in anchors)
    ra = any(abs(b - x) < EPS for x in anchors)
    inner = sorted(x for x in anchors if a + EPS < x < b - EPS)
    out = []
    if not inner and not la and not ra:
        _seg_anchored(out, notes, a, b, w, gv, min_cut, False)
        return out
    cuts = [a] + inner + [b]
    last = len(cuts) - 2
    for i in range(last + 1):
        s0, s1 = cuts[i], cuts[i + 1]
        sl = la if i == 0 else True       # внутренний разрез = якорь
        sr = ra if i == last else True
        if sl and sr:
            _seg_wall(out, notes, s0, s1, w, gv, min_cut)
        else:
            _seg_anchored(out, notes, s0, s1, w, gv, min_cut, sr)
    out.sort()
    return out


def cladding_plan(req):
    """Главный вход: req (CLADDING.md §Модель данных) → план вставок."""
    tile = req.get("tile") or {}
    w = float(tile.get("w", 600.0))
    h = float(tile.get("h", 600.0))
    gap = req.get("gap") or {}
    gv = float(gap.get("v", 0.0))
    gh = float(gap.get("h", 0.0))
    datum = float(req.get("datum", 0.0))
    mode = (req.get("mode") or "edge").strip().lower()
    # В4 (Герман, 20.07): минимальная подрезка 150 мм
    min_cut = float(req.get("min_cut", 150.0))
    notes, inserts = [], []
    if w < EPS or h < EPS:
        return {"ok": False, "error": "нулевой размер камня"}
    n_rows = 0
    for ci, c in enumerate(req.get("contours") or []):
        outer = _closed(c.get("outer") or [])
        if len(outer) < 4:
            notes.append("контур %d: меньше 4 вершин — пропуск" % (ci + 1))
            continue
        holes = [_closed(hh) for hh in (c.get("holes") or [])]
        polys = [outer] + holes
        bad = [p for p in polys if not _is_ortho(p)]
        if bad:
            notes.append("контур %d: неортогональные рёбра — пропуск"
                         % (ci + 1))
            continue
        y_lo = min(p[1] for p in outer)
        y_hi = max(p[1] for p in outer)
        x_min = min(p[0] for p in outer)
        if y_lo < datum - EPS:
            notes.append("контур %d ниже отметки старта на %.0f мм — "
                         "ниже не облицовывается" % (ci + 1, datum - y_lo))
        # якоря проёмов — на ВЕСЬ контур (В2: швы сквозные по высоте)
        anchors = contour_anchors(holes) if mode == "openings" else set()
        # глобальная сетка рядов от датума (общий горизонт всех контуров)
        i = 0
        while True:
            y = datum + i * (h + gh)
            if y >= y_hi - EPS:
                break
            for sy0, sy1, ivs in strip_bands(polys, y, min(y + h, y_hi)):
                hc = sy1 - sy0
                if hc < min_cut:
                    notes.append("контур %d: ряд на отм. %.0f высотой "
                                 "%.0f < min_cut" % (ci + 1, sy0, hc))
                    continue
                for a, b in ivs:
                    row = (_row_openings(a, b, anchors, w, gv, min_cut,
                                         notes)
                           if mode == "openings" else
                           _row_edge(a, b, x_min, w, gv, min_cut, notes))
                    for x, wc in row:
                        inserts.append({
                            "x": round(x, 4), "y": round(sy0, 4),
                            "w": round(wc, 4), "h": round(hc, 4)})
            n_rows += 1
            i += 1
    full = sum(1 for t in inserts
               if abs(t["w"] - w) < EPS and abs(t["h"] - h) < EPS)
    return {"ok": True, "inserts": inserts, "notes": _dedup_notes(notes),
            "summary": {"tiles": len(inserts), "full": full,
                        "cut": len(inserts) - full, "rows": n_rows}}
