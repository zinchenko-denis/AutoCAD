"""Small stdlib polygon intersection used only for sloped cladding pieces.

Split both boundaries at their crossings, retain directed segments inside the
other polygon, then trace closed rings. Unlike intersecting edge half-planes,
this preserves concave corners, holes and disconnected results. All arithmetic
is local to the input polygon; returned rings use drawing coordinates.
"""
import math

TOL = 1e-7  # mm, local coordinates; output drawing precision is 1e-4 mm


def signed_area(points):
    if len(points) < 3:
        return 0.0
    ox, oy = points[0]
    return math.fsum((p[0] - ox) * (q[1] - oy) -
                     (q[0] - ox) * (p[1] - oy)
                     for p, q in zip(points, points[1:] + points[:1])) / 2.0


def _cross(a, b):
    return a[0] * b[1] - a[1] * b[0]


def _sub(a, b):
    return a[0] - b[0], a[1] - b[1]


def _edges(rings):
    return [(a, b) for ring in rings for a, b in zip(ring, ring[1:] + ring[:1])
            if math.hypot(b[0] - a[0], b[1] - a[1]) > TOL]


def point_location(point, rings):
    """1 inside (even/odd), 0 outside, -1 on boundary."""
    x, y = point
    inside = False
    for a, b in _edges(rings):
        d, r = _sub(b, a), _sub(point, a)
        length = math.hypot(*d)
        if abs(_cross(d, r)) <= TOL * length and \
                -TOL <= r[0] * d[0] / length + r[1] * d[1] / length <= length + TOL:
            return -1
        if (a[1] > y) != (b[1] > y):
            at_x = a[0] + (y - a[1]) * d[0] / d[1]
            if x < at_x:
                inside = not inside
    return 1 if inside else 0


def _cuts(a, b, c, d):
    """Parameters on AB of crossings or endpoints of a collinear overlap."""
    v, w, r = _sub(b, a), _sub(d, c), _sub(c, a)
    lv, lw = math.hypot(*v), math.hypot(*w)
    if lv <= TOL or lw <= TOL:
        return []
    den = _cross(v, w)
    if abs(den) > 1e-12 * lv * lw:
        t, u = _cross(r, w) / den, _cross(r, v) / den
        return [min(1.0, max(0.0, t))] if \
            -TOL / lv <= t <= 1 + TOL / lv and -TOL / lw <= u <= 1 + TOL / lw else []
    if abs(_cross(v, r)) > TOL * lv:
        return []
    vv = lv * lv
    return [min(1.0, max(0.0, (p[0] * v[0] + p[1] * v[1]) / vv))
            for p in (r, _sub(d, a))
            if -TOL * lv <= p[0] * v[0] + p[1] * v[1] <= vv + TOL * lv]


def _clean_ring(ring):
    clean = []
    for p in ring:
        if not clean or math.hypot(*_sub(p, clean[-1])) > TOL:
            clean.append(p)
    if len(clean) > 1 and math.hypot(*_sub(clean[-1], clean[0])) <= TOL:
        clean.pop()
    changed = True
    while changed and len(clean) >= 3:
        changed = False
        for i, p in enumerate(clean):
            v, w = _sub(p, clean[i - 1]), _sub(clean[(i + 1) % len(clean)], p)
            if abs(_cross(v, w)) <= TOL * max(math.hypot(*v), math.hypot(*w)) and \
                    v[0] * w[0] + v[1] * w[1] >= 0:
                del clean[i]
                changed = True
                break
    return clean


def is_simple(ring):
    """Reject crossing/touching non-neighbour edges before using a cover."""
    if len(ring) < 3 or not all(math.isfinite(v) for p in ring for v in p):
        return False
    ox, oy = ring[0]
    p = _clean_ring([(x - ox, y - oy) for x, y in ring])
    edges = _edges([p])
    if len(edges) < 3 or abs(signed_area(p)) < TOL:
        return False
    for i, (a, b) in enumerate(edges):
        for j in range(i + 1, len(edges)):
            if j == i + 1 or (i == 0 and j == len(edges) - 1):
                continue
            c, d = edges[j]
            if _cuts(a, b, c, d) or _cuts(c, d, a, b):
                return False
    return True


def intersect(rings, outer):
    """Intersect one polygon (outer + holes) with a simple outer contour.

    Result: list of components, each [CCW outer, CW hole, ...]. Invalid/open
    boundary graphs raise ValueError rather than returning incomplete geometry.
    """
    if not rings or not outer:
        return []
    ox, oy = rings[0][0]

    def local(ring, ccw):
        p = _clean_ring([(float(x) - ox, float(y) - oy) for x, y in ring])
        if (signed_area(p) > 0) != ccw:
            p.reverse()
        return p

    aa = [local(r, i == 0) for i, r in enumerate(rings)]
    bb = [local(outer, True)]
    if any(len(r) < 3 for r in aa + bb):
        raise ValueError("вырожденное кольцо при обрезке по скату")
    edge_sets = [_edges(aa), _edges(bb)]
    selected = set()
    # Quantized LOCAL endpoints provide one key for the same crossing reached
    # from either edge; 1e-7 mm is well below the retained 0.1 mm pieces.
    def key(p):
        return round(p[0], 7), round(p[1], 7)

    for side, own in enumerate(edge_sets):
        other, other_rings = edge_sets[1 - side], bb if side == 0 else aa
        for a, b in own:
            v = _sub(b, a)
            lv = math.hypot(*v)
            ts = [0.0, 1.0]
            for c, d in other:
                ts.extend(_cuts(a, b, c, d))
            cuts = []
            for t in sorted(ts):
                if not cuts or (t - cuts[-1]) * lv > TOL:
                    cuts.append(t)
            for lo, hi in zip(cuts, cuts[1:]):
                mid = (a[0] + v[0] * (lo + hi) / 2, a[1] + v[1] * (lo + hi) / 2)
                loc = point_location(mid, other_rings)
                keep = loc == 1
                if loc == -1:
                    # Coincident boundaries have an area on the same side iff
                    # their directions agree (all rings have interior on left).
                    keep = any(point_location(mid, [[c, d]]) == -1 and
                               v[0] * (d[0] - c[0]) + v[1] * (d[1] - c[1]) > 0
                               for c, d in other)
                if keep:
                    p = key((a[0] + v[0] * lo, a[1] + v[1] * lo))
                    q = key((a[0] + v[0] * hi, a[1] + v[1] * hi))
                    if p != q:
                        selected.add((p, q))
    selected -= {edge for edge in selected if (edge[1], edge[0]) in selected}
    starts = {}
    for a, b in selected:
        starts.setdefault(a, []).append(b)
    unused = set(selected)
    found = []
    while unused:
        first = min(unused)
        a, b = first
        ring = [a]
        unused.remove(first)
        while b != ring[0]:
            ring.append(b)
            candidates = [c for c in starts.get(b, ()) if (b, c) in unused]
            if not candidates:
                raise ValueError("незамкнутая граница при обрезке по скату")
            incoming = _sub(b, a)
            # Follow the face on the left; point-touching components stay
            # separate instead of producing a self-intersecting figure eight.
            c = max(candidates, key=lambda p: math.atan2(_cross(incoming, _sub(p, b)),
                         incoming[0] * (p[0] - b[0]) + incoming[1] * (p[1] - b[1])))
            unused.remove((b, c))
            a, b = b, c
        ring = _clean_ring(ring)
        if len(ring) >= 3 and abs(signed_area(ring)) > TOL:
            found.append(ring)
    positive = [[r] for r in found if signed_area(r) > 0]
    for hole in (r for r in found if signed_area(r) < 0):
        owners = [part for part in positive if point_location(hole[0], [part[0]]) != 0]
        if not owners:
            raise ValueError("проём без внешнего кольца при обрезке по скату")
        min(owners, key=lambda p: abs(signed_area(p[0]))).append(hole)
    positive.sort(key=lambda p: (min(x for x, y in p[0]), min(y for x, y in p[0])))
    return [[[(x + ox, y + oy) for x, y in r] for r in part] for part in positive]
