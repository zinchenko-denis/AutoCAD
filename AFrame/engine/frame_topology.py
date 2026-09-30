# -*- coding: utf-8 -*-
"""Screen the applicability of existing frame_calc coefficients to real pieces.

This module does not calculate a beam or assert connection properties. Each
emitted rail remains a separate member, including abutting pieces at gap=0.
Geometric intersections are candidate supports, not evidence of fixed/sliding
connections, moment transfer, gravity-load distribution or cantilever capacity.
"""
import math

from frame_spatial import BoundsIndex

MATCH_TOL = 0.5  # Coordinate rounding only; not a structural allowance.


def _span_class(count):
    if count >= 4:
        return "multi"
    return str(count)


def _unique_supports(matches):
    """One geometric support position; preserve source members and role labels."""
    grouped = {}
    for coordinate, source, index, kind in matches:
        key = round(float(coordinate), 4)
        grouped.setdefault(key, []).append(dict(source=source, index=index, kind=kind))
    return [dict(position=position, sources=grouped[position],
                 connection="not_modeled") for position in sorted(grouped)]


def _member_geometry(index, rail, supports, tolerance):
    positions = [s["position"] for s in supports]
    intervals = [round(b - a, 4) for a, b in zip(positions, positions[1:])]
    length = float(rail["y1"]) - float(rail["y0"])
    return dict(index=index, kind=rail.get("kind"),
                geometry={key: rail[key] for key in ("x", "y0", "y1")},
                supports=supports, support_y=positions, support_count=len(positions),
                intervals=intervals, span_count=len(intervals),
                actual_span_class=_span_class(len(intervals)),
                bottom_free=round(max(0.0, positions[0] - rail["y0"]), 4) if positions else length,
                top_free=round(max(0.0, rail["y1"] - positions[-1]), 4) if positions else length,
                coordinate_match_tolerance=tolerance)


def geometric_members(sub, rails, hrails, brackets, member_zones=None,
                      rail_gap=0.0, diagnostics=None):
    """Read candidate support intersections without applying calculation rules.

    Indexes live for this call only. Exact legacy coordinate comparisons and
    source order are retained; no quantization or structural tolerance is added.
    Optional counters measure work and never become engineering output.
    """
    zones = list(member_zones or ["row"] * len(rails))
    if len(zones) != len(rails) or any(z not in ("row", "corner") for z in zones):
        raise ValueError("member_zones must contain one row/corner entry per rail")
    if diagnostics is not None:
        for name in ("point_queries", "segment_queries", "index_nodes_visited",
                     "candidates_tested", "matches_emitted"):
            diagnostics[name] = 0
    bracket_index = BoundsIndex(((bi, b["x"], b["x"], b["y"], b["y"])
                                 for bi, b in enumerate(brackets)), diagnostics) \
        if (sub == "vertical" and rails) or hrails else None
    kinds = ("НГП", "СП-60-40") if sub == "interfloor" else ("ГП-40-40",)
    horizontal_index = BoundsIndex(
        ((hi, h["x0"] - MATCH_TOL, h["x1"] + MATCH_TOL, h["y"], h["y"])
         for hi, h in enumerate(hrails) if h.get("kind") in kinds), diagnostics) \
        if sub != "vertical" and rails else None
    members = []
    for index, rail in enumerate(rails):
        matches = []
        tolerance = MATCH_TOL
        if sub == "vertical":
            candidates = bracket_index.query(near_x=(rail["x"], MATCH_TOL),
                y=(rail["y0"] - MATCH_TOL, rail["y1"] + MATCH_TOL))
            if diagnostics is not None:
                diagnostics["point_queries"] += 1
                diagnostics["candidates_tested"] += len(candidates)
            for bi in candidates:
                bracket = brackets[bi]
                if abs(bracket["x"] - rail["x"]) <= MATCH_TOL and \
                        rail["y0"] - MATCH_TOL <= bracket["y"] <= rail["y1"] + MATCH_TOL:
                    matches.append((bracket["y"], "brackets", bi, bracket.get("kind")))
        else:
            # Interfloor uses the pre-existing endpoint/splice matching rule.
            # This only records candidate intersections across that small gap.
            tolerance = float(rail_gap) / 2.0 + 1.0 if sub == "interfloor" else MATCH_TOL
            candidates = horizontal_index.query(x=(rail["x"], rail["x"]),
                y=(rail["y0"] - tolerance, rail["y1"] + tolerance))
            if diagnostics is not None:
                diagnostics["segment_queries"] += 1
                diagnostics["candidates_tested"] += len(candidates)
            for hi in candidates:
                horizontal = hrails[hi]
                if horizontal.get("kind") in kinds and \
                        horizontal["x0"] - MATCH_TOL <= rail["x"] <= horizontal["x1"] + MATCH_TOL and \
                        rail["y0"] - tolerance <= horizontal["y"] <= rail["y1"] + tolerance:
                    matches.append((horizontal["y"], "hrails", hi, horizontal.get("kind")))
        if diagnostics is not None:
            diagnostics["matches_emitted"] += len(matches)
        member = _member_geometry(index, rail, _unique_supports(matches), tolerance)
        member["zone"] = zones[index]
        members.append(member)

    horizontal_members = []
    for hi, horizontal in enumerate(hrails):
        candidates = bracket_index.query(
            x=(horizontal["x0"] - MATCH_TOL, horizontal["x1"] + MATCH_TOL),
            near_y=(horizontal["y"], MATCH_TOL))
        if diagnostics is not None:
            diagnostics["point_queries"] += 1
            diagnostics["candidates_tested"] += len(candidates)
        matches = [(b["x"], "brackets", bi, b.get("kind"))
                   for bi in candidates for b in (brackets[bi],)
                   if abs(b["y"] - horizontal["y"]) <= MATCH_TOL
                   and horizontal["x0"] - MATCH_TOL <= b["x"] <= horizontal["x1"] + MATCH_TOL]
        if diagnostics is not None:
            diagnostics["matches_emitted"] += len(matches)
        horizontal_members.append(dict(index=hi, kind=horizontal.get("kind"),
            geometry={key: horizontal[key] for key in ("y", "x0", "x1")},
            bracket_intersections=_unique_supports(matches), strength="not_verified"))
    return members, horizontal_members


def screen_layout(sub, rails, hrails, brackets, calc_inputs, member_zones=None,
                  rail_gap=0.0):
    """Return a serializable inventory and *limited* geometric screening result.

member_zones aligns with rails and names their final row/corner calculation.
Vertical coefficients follow the same largest actual interval per zone as
frame_plan._verify_calc_spacing. Ortho uses frame_calc's fixed v_step. The
classification mirrors spans_const; no new formula or threshold is introduced.
Interfloor has no confirmed model of the emitted joints/constraints, regardless
of the number of intersections. Its existing free-end refusal has precedence
in the caller.
    """
    members, horizontal_members = geometric_members(
        sub, rails, hrails, brackets, member_zones, rail_gap)

    reasons = []
    if not members:
        reasons.append(dict(reason="empty_geometry"))
    if sub == "interfloor":
        reasons.append(dict(reason="interfloor_model_unconfirmed"))
    else:
        actual_max = {zone: max((b - a for member in members if member["zone"] == zone
                                for a, b in zip(member["support_y"], member["support_y"][1:])), default=0.0)
                      for zone in ("row", "corner")}
        for member in members:
            span = actual_max[member["zone"]] if sub == "vertical" else float(calc_inputs["v_step"])
            expected_count = max(2, int(math.floor(float(calc_inputs["rail_len"]) / span))) if span > 0 else None
            member["coefficient_span"] = span if span > 0 else None
            member["coefficient_span_class"] = _span_class(expected_count) if expected_count is not None else None
            # A topology with two supports has one span; spans_const has no
            # one-span row. Do not quietly promote it to the two-span row.
            if member["support_count"] < 2:
                reason = "insufficient_supports"
            elif member["span_count"] == 1:
                reason = "single_span_unsupported"
            elif member["actual_span_class"] != member["coefficient_span_class"]:
                reason = "span_class_mismatch"
            elif sub == "ortho" and max(member["intervals"]) > span + MATCH_TOL:
                # The existing chain would use a shorter span than the piece.
                reason = "support_interval_exceeds_calculated_span"
            else:
                reason = None
            if reason:
                reasons.append(dict(reason=reason, member_index=member["index"],
                    support_count=member["support_count"], span_count=member["span_count"],
                    actual_span_class=member["actual_span_class"],
                    coefficient_span_class=member["coefficient_span_class"],
                    coefficient_span=member["coefficient_span"]))

    return dict(status="not_verified", scheme=sub,
                geometric_screening=dict(status="refused" if reasons else "passed", reasons=reasons),
                members=members, horizontal_members=horizontal_members,
                member_count=len(members), pieces_merged=False,
                fixed_sliding="not_modeled", splice_continuity="not_modeled",
                gravity_load_distribution="not_verified", cantilevers="not_verified",
                unequal_spans="not_verified", horizontal_member_strength="not_verified",
                scope="Проверены только геометрические пересечения и совместимость числа пролётов "
                      "с уже применяемыми коэффициентами. Это не проверка статической модели: "
                      "неподвижные/подвижные соединения, передача момента через стыки, распределение "
                      "веса, консоли и неравные пролёты не подтверждены.")


def refusal(static_model):
    """Addressed refusal payload; never contains partial drawable arrays."""
    screening = static_model["geometric_screening"]
    if screening["status"] != "refused":
        return None
    interfloor = static_model["scheme"] == "interfloor"
    descriptions = {
        "empty_geometry": "нет вертикальных элементов для проверки",
        "insufficient_supports": "менее двух геометрических опор",
        "single_span_unsupported": "однопролётная схема отсутствует в применённой расчётной цепочке",
        "span_class_mismatch": "число пролётов куска не соответствует применённым коэффициентам",
        "support_interval_exceeds_calculated_span": "интервал опор превышает пролёт расчётной цепочки",
        "interfloor_model_unconfirmed": "для межэтажного каркаса не подтверждена модель соединений "
                                          "и непрерывности НСП через стыки",
    }
    kinds = list(dict.fromkeys(reason["reason"] for reason in screening["reasons"]))
    return dict(ok=False,
        error_code="E_CALC_MODEL_UNCONFIRMED" if interfloor else "E_CALC_TOPOLOGY_UNSUPPORTED",
        error="Расчёт не применён: %s. Используйте ручной режим по отдельному проектному расчёту; "
              "прежняя подсистема сохранена." % "; ".join(descriptions[kind] for kind in kinds),
        static_model=static_model, unsupported=screening["reasons"],
        unsupported_counts=dict(vertical_members=len({r["member_index"] for r in screening["reasons"]
                                                      if "member_index" in r})))
