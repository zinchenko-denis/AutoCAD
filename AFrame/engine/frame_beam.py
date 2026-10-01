# -*- coding: utf-8 -*-
"""Uniformly loaded prismatic beam on actual transverse simple supports.

Unit load q=1 and EI=1; coordinates are normalised by the longest span.
The three-moment equations enforce slope continuity *inside one member*.
Separate rails are never joined. End overhangs carry the same uniform load.
This is the existing beam idealisation, not a model of mounting connections,
axial fixed/sliding behaviour, support settlement or horizontal-member stiffness.

Derivation: integrate EI*y''=M and impose y=0 at each support; the interior
row is a*M[i-1]+2*(a+b)*M[i]+b*M[i+1]=-(a**3+b**3)/4.
References: MIT OCW, Unified Engineering M5–M8 beam notes; American Wood
Council, Beam Design Formulas, figures 1, 22, 29 and 31 (Purdue CE474 copy).
"""
import math


def _quadratic(a, b, c):
    if abs(a) < 1e-15:
        return [] if abs(b) < 1e-15 else [-c / b]
    d = b * b - 4.0 * a * c
    if d < 0:
        return []
    root = math.sqrt(d)
    q = -0.5 * (b + math.copysign(root, b))
    return [-b / (2.0 * a)] if abs(q) < 1e-15 else [q / a, c / q]


def _extrema(length, moment, shear, slope):
    """Exact integration; derivative roots bracketed at its turning points."""
    def rotation(x):
        return slope + moment * x + shear * x * x / 2.0 - x ** 3 / 6.0

    def displacement(x):
        return slope * x + moment * x * x / 2.0 + shear * x ** 3 / 6.0 - x ** 4 / 24.0

    turns = [0.0, length] + [x for x in _quadratic(-0.5, shear, moment)
                             if 0.0 < x < length]
    turns.sort()
    points = list(turns)
    for lo, hi in zip(turns, turns[1:]):
        f_lo, f_hi = rotation(lo), rotation(hi)
        if f_lo * f_hi >= 0:
            continue
        for _ in range(48):
            mid = (lo + hi) / 2.0
            if rotation(mid) * f_lo > 0:
                lo, f_lo = mid, rotation(mid)
            else:
                hi = mid
        points.append((lo + hi) / 2.0)
    m_points = [0.0, length] + ([shear] if 0.0 < shear < length else [])
    max_moment = max(abs(moment + shear * x - x * x / 2.0) for x in m_points)
    max_deflection = max(abs(displacement(x)) for x in points)
    return max_moment, max_deflection


def response(intervals, bottom_free=0.0, top_free=0.0):
    """Dimensionless maxima and reactions for ≥1 actual spans, O(n).

    deflection_ratio is max(|EI*y/q| / local_length) / max_span**3;
    the existing L/150 criterion is checked on each span and each overhang,
    using the actual free length for the latter (a conservative screen).
    It is not a manufacturer-approved cantilever length or connection check.
    """
    spans = [float(value) for value in intervals]
    ends = [float(bottom_free), float(top_free)]
    if not spans or any(not math.isfinite(v) or v <= 0 for v in spans) or \
            any(not math.isfinite(v) or v < 0 for v in ends):
        raise ValueError("beam requires finite positive spans and nonnegative overhangs")
    scale = max(spans)
    lengths = [v / scale for v in spans]
    a, b = [v / scale for v in ends]
    count = len(lengths)
    moments = [-a * a / 2.0] + [0.0] * (count - 1) + [-b * b / 2.0]
    # Thomas elimination; positive span lengths make this matrix SPD.
    upper, diagonal, rhs = [], [], []
    for i in range(1, count):
        left, right = lengths[i - 1], lengths[i]
        d, r = 2.0 * (left + right), -(left ** 3 + right ** 3) / 4.0
        if i == 1:
            r -= left * moments[0]
        else:
            factor = left / diagonal[-1]
            d -= factor * upper[-1]
            r -= factor * rhs[-1]
        if i == count - 1:
            r -= right * moments[-1]
        diagonal.append(d)
        upper.append(right)
        rhs.append(r)
    for i in range(count - 1, 0, -1):
        moments[i] = (rhs[i - 1] - (upper[i - 1] * moments[i + 1]
                        if i < count - 1 else 0.0)) / diagonal[i - 1]

    reactions = [0.0] * (count + 1)
    reactions[0], reactions[-1] = a, b
    max_moment = max_deflection = max_ratio = 0.0
    deflection_length = 1.0
    slopes = []
    for i, length in enumerate(lengths):
        shear = (moments[i + 1] - moments[i]) / length + length / 2.0
        slope = -moments[i] * length / 2.0 - shear * length ** 2 / 6.0 + length ** 3 / 24.0
        end_slope = slope + moments[i] * length + shear * length ** 2 / 2.0 - length ** 3 / 6.0
        slopes.append((slope, end_slope))
        reactions[i] += shear
        reactions[i + 1] += length - shear
        m, f = _extrema(length, moments[i], shear, slope)
        max_moment, max_deflection = max(max_moment, m), max(max_deflection, f)
        if f / length > max_ratio:
            max_ratio, deflection_length = f / length, length
    # Local coordinate points outward from the first/last support.
    for length, slope in ((a, -slopes[0][0]), (b, slopes[-1][1])):
        if length <= 0:
            continue
        m, f = _extrema(length, -length * length / 2.0, length, slope)
        max_moment, max_deflection = max(max_moment, m), max(max_deflection, f)
        if f / length > max_ratio:
            max_ratio, deflection_length = f / length, length
    return dict(span=scale, gravity_length=max(scale, ends[0] + spans[0] / 2.0,
                ends[1] + spans[-1] / 2.0), c_m=max_moment,
                k_reaction=max(abs(value) for value in reactions),
                c_f=max_deflection, c_f_local=max_ratio,
                deflection_length=deflection_length * scale,
                reactions=reactions, support_moments=moments,
                span_count=count, bottom_free=ends[0], top_free=ends[1])
