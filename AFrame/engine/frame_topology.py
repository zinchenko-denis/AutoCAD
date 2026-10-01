# -*- coding: utf-8 -*-
"""Check actual member geometry and limited beam arithmetic.

Beam arithmetic does not assert mounting connection properties. Each
emitted rail remains a separate member, including abutting pieces at gap=0.
Geometric intersections are candidate supports, not evidence of fixed/sliding
connections, moment transfer, gravity-load distribution or cantilever capacity.
"""
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


def evaluate_member(calc_inputs, profile, member, zone, bracket_step, beam_cache=None):
    """One real piece; cache geometry responses only inside the current operation."""
    import frame_beam
    import frame_calc
    key = (tuple(member["intervals"]), member["bottom_free"], member["top_free"])
    cache = beam_cache if beam_cache is not None else {}
    if key not in cache:
        cache[key] = frame_beam.response(*key)
    beam = cache[key]
    step = beam["span"] if calc_inputs["scheme"] == "vertical" else bracket_step
    chain = frame_calc.calc_chain(dict(calc_inputs, profile=profile), step, zone,
                                  member_response=beam)
    return dict(response=beam, chain=chain)


def screen_layout(sub, rails, hrails, brackets, calc_inputs, member_zones=None,
                  rail_gap=0.0, profiles_by_zone=None, bracket_steps=None):
    """Check each physical member under the existing uniform-load idealisation.

    Geometric intersections remain candidate supports. Per-member arithmetic
    uses actual spans and free ends, and does not establish fixed/sliding
    joints, axial load distribution or continuity across separate pieces.
    Minimal geometry-only callers (without scheme) receive no strength claim.
    """
    members, horizontal_members = geometric_members(
        sub, rails, hrails, brackets, member_zones, rail_gap)
    reasons, cases = [], []
    beam_cache, case_cache = {}, {}
    calculate = calc_inputs.get("scheme") in ("vertical", "ortho")
    if not members:
        reasons.append(dict(reason="empty_geometry"))
    if sub == "interfloor":
        reasons.append(dict(reason="interfloor_model_unconfirmed"))
    else:
        for member in members:
            span = max(member["intervals"], default=0.0)
            member["coefficient_span"] = span or None
            member["coefficient_span_class"] = member["actual_span_class"] if span else None
            if member["support_count"] < 2:
                reasons.append(dict(reason="insufficient_supports", member_index=member["index"],
                    geometry=dict(member["geometry"]), support_count=member["support_count"], span_count=member["span_count"],
                    actual_span_class=member["actual_span_class"],
                    coefficient_span_class=member["coefficient_span_class"],
                    coefficient_span=member["coefficient_span"]))
                continue
            if not calculate:
                continue
            zone = member["zone"]
            profile = (profiles_by_zone or {}).get(zone, calc_inputs["profile"])
            geometry_key = (tuple(member["intervals"]), member["bottom_free"], member["top_free"])
            step = span if sub == "vertical" else float((bracket_steps or {}).get(
                "corner" if zone == "corner" else "main", calc_inputs.get("max_step", 800)))
            # A resolved section is immutable during this one layout operation.
            profile_key = tuple(sorted(profile.items())) if isinstance(profile, dict) else profile
            key = (geometry_key, zone, profile_key, step)
            if key not in case_cache:
                calculation = evaluate_member(calc_inputs, profile, member, zone, step, beam_cache)
                case_cache[key] = len(cases)
                cases.append(dict(id=len(cases), zone=zone, profile=profile,
                    span=span, intervals=member["intervals"],
                    bottom_free=member["bottom_free"], top_free=member["top_free"],
                    response=calculation["response"], chain=calculation["chain"]))
            case_id = case_cache[key]
            member["calculation_case"] = case_id
            if not cases[case_id]["chain"]["passed"]:
                reasons.append(dict(reason="member_capacity_exceeded", member_index=member["index"],
                    geometry=dict(member["geometry"]), support_count=member["support_count"], span_count=member["span_count"],
                    calculation_case=case_id, failed_checks=[c["name"] for c in
                    cases[case_id]["chain"]["checks"] if not c["ok"]]))

    return dict(status="not_verified", scheme=sub,
                geometric_screening=dict(status="refused" if reasons else "passed", reasons=reasons),
                members=members, horizontal_members=horizontal_members,
                member_count=len(members), pieces_merged=False,
                member_calculation=dict(status=("passed" if cases and not reasons else
                    "refused" if calculate else "not_requested"), cases=cases,
                    evaluated_members=sum("calculation_case" in m for m in members),
                    unique_geometries=len(beam_cache)),
                fixed_sliding="not_modeled", splice_continuity="not_modeled",
                gravity_load_distribution="not_verified",
                cantilevers="uniform_load_screened" if cases else "not_verified",
                unequal_spans="uniform_load_screened" if cases else "not_verified",
                horizontal_member_strength="not_verified",
                scope="Каждый отдельный кусок проверен по фактическим пролётам и свободным концам "
                      "при равномерной поперечной нагрузке и простых опорах. Разные куски не склеены. "
                      "Это ограниченный расчёт: неподвижные/подвижные соединения, передача момента "
                      "через стыки, распределение веса и работа горизонтальных элементов не подтверждены.")


def refusal(static_model):
    """Addressed refusal payload; never contains partial drawable arrays."""
    screening = static_model["geometric_screening"]
    if screening["status"] != "refused":
        return None
    interfloor = static_model["scheme"] == "interfloor"
    descriptions = {
        "empty_geometry": "нет вертикальных элементов для проверки",
        "insufficient_supports": "менее двух геометрических опор",
        "member_capacity_exceeded": "проверка фактического куска по нагрузке не проходит",
        "interfloor_model_unconfirmed": "для межэтажного каркаса не подтверждена модель соединений "
                                          "и непрерывности НСП через стыки",
    }
    kinds = list(dict.fromkeys(reason["reason"] for reason in screening["reasons"]))
    piece_reasons = [reason for reason in screening["reasons"] if "member_index" in reason]
    detail = ""
    if piece_reasons:
        first = piece_reasons[0]
        geometry = first["geometry"]
        detail = (" Первый проблемный кусок: X=%g, Y=%g…%g мм; опор %d, пролётов %d." %
                  (geometry["x"], geometry["y0"], geometry["y1"],
                   first["support_count"], first["span_count"]))
        if first["reason"] == "member_capacity_exceeded":
            case = static_model["member_calculation"]["cases"][first["calculation_case"]]
            failed = next(check for check in case["chain"]["checks"] if not check["ok"])
            detail += " %s: %g > %g." % (failed["name"], failed["value"], failed["limit"])
        if len(piece_reasons) > 1:
            detail += " Всего проблемных кусков: %d." % len(piece_reasons)
    return dict(ok=False,
        error_code=("E_CALC_MODEL_UNCONFIRMED" if interfloor else
                    "E_CALC_MEMBER_CAPACITY" if "member_capacity_exceeded" in kinds else
                    "E_CALC_TOPOLOGY_UNSUPPORTED"),
        error=("Расчёт не применён: %s.%s Уточните сечение, шаг и опоры; "
               "прежняя подсистема сохранена.") %
              ("; ".join(descriptions[kind] for kind in kinds), detail),
        static_model=static_model, unsupported=screening["reasons"],
        unsupported_counts=dict(vertical_members=len({r["member_index"] for r in screening["reasons"]
                                                      if "member_index" in r})))
