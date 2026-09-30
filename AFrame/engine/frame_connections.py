# -*- coding: utf-8 -*-
"""Compact read-only inventory of geometric support and adjacent-piece candidates.

References address emitted physical arrays, never independently copied geometry.
A geometric intersection does not set a fixed/sliding connection, establish a
splice or adopt section properties/BOM from the reference calculation.
"""
import copy
import math

from frame_catalog import catalog_snapshot, match_reference, scope_reason


SCHEMA = "aframe_connection_passport/1"
_COVERAGE = {"fixed_sliding": "not_modeled", "splice_continuity": "not_modeled",
             "gravity_load_distribution": "not_verified", "strength": "not_verified"}
_SUMMARY_KEYS = ("members", "support_positions", "support_links", "geometric_joints",
                 "matched_profile_references", "unresolved_profile_references")


def _empty(req, reason=None):
    catalog = catalog_snapshot()
    return {"schema": SCHEMA, "status": "unavailable" if reason else "inventory_only",
            "reason": reason, "scheme": str(req.get("sub_type") or "vertical").strip().lower(),
            "catalog_revision": catalog["revision"], "catalog_scope": catalog["scope"],
            "sources": catalog["sources"], "references": catalog["references"],
            "members": [], "joints": [], "coverage": dict(_COVERAGE),
            "summary": {key: 0 for key in _SUMMARY_KEYS}, "issues": []}


def _number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _summarize(passport):
    summary = {key: 0 for key in _SUMMARY_KEYS}
    summary["members"] = len(passport["members"])
    summary["geometric_joints"] = len(passport["joints"])
    for member in passport["members"]:
        summary["support_positions"] += member["support_count"]
        summary["support_links"] += sum(len(s["bracket_indices"]) for s in member["supports"])
        key = "matched_profile_references" if member["profile_match"]["status"] == "matched_source_identity" else "unresolved_profile_references"
        summary[key] += 1
    passport["summary"] = summary
    passport["issues"] = [{"code": "CONNECTIONS_GEOMETRY_ONLY", "count": summary["support_positions"],
        "message": "Пересечения — кандидаты опор. Закрепления, передача усилий и непрерывность через стыки не подтверждены."}]
    if summary["unresolved_profile_references"]:
        passport["issues"].append({"code": "CONNECTION_PROFILE_REFERENCE_UNKNOWN",
            "count": summary["unresolved_profile_references"],
            "message": "Марка части направляющих не совпала с точным обозначением расчётного примера. Каталожное изделие не назначено."})


def build_connection_passport(req, response, prepared_members=None, diagnostics=None):
    """Inventory one planner scope; indices address its unmodified output arrays.

Manual and calculation modes use the same geometric extraction. The latter
can reuse its existing static-model inventory, without rerunning CAD or a
structural calculation. No calculation outcome is promoted by this function.
"""
    reason = scope_reason(req)
    result = _empty(req, reason)
    if reason:
        return result
    rails, brackets = response.get("rails") or [], response.get("brackets") or []
    if not rails:
        return _empty(req, "В результате нет направляющих для паспорта соединений.")
    for rail in rails:
        if not all(_number(rail.get(k)) for k in ("x", "y0", "y1")) or rail["y1"] <= rail["y0"]:
            return _empty(req, "Неполная геометрия направляющей: паспорт соединений не сформирован.")
    for bracket in brackets:
        if not all(_number(bracket.get(k)) for k in ("x", "y")):
            return _empty(req, "Неполная геометрия кронштейна: паспорт соединений не сформирован.")
    if prepared_members is None:
        from frame_topology import geometric_members
        prepared_members, _ = geometric_members("vertical", rails, [], brackets, diagnostics=diagnostics)
    if len(prepared_members) != len(rails):
        return _empty(req, "Состав геометрических опор не соответствует направляющим результата.")
    for index, (rail, source) in enumerate(zip(rails, prepared_members)):
        if source.get("index") != index or source.get("geometry") != {key: rail[key] for key in ("x", "y0", "y1")}:
            return _empty(req, "Геометрический снимок опор относится к другой направляющей или другой версии результата.")
        if source.get("support_count") != len(source.get("supports") or []) or source.get("span_count") != max(0, source["support_count"] - 1):
            return _empty(req, "Число геометрических опор не согласовано со снимком результата.")
        supports = []
        for support in source["supports"]:
            references = [s["index"] for s in support["sources"] if s["source"] == "brackets"]
            if len(references) != len(support["sources"]) or any(i < 0 or i >= len(brackets) for i in references):
                return _empty(req, "Ссылка геометрической опоры не соответствует кронштейнам результата.")
            if len(references) != len(set(references)) or any(
                abs(brackets[i]["x"] - rail["x"]) > 0.5 or not rail["y0"] - 0.5 <= brackets[i]["y"] <= rail["y1"] + 0.5 or
                round(float(brackets[i]["y"]), 4) != support["position"] for i in references):
                return _empty(req, "Геометрический снимок опоры не совпадает с положением кронштейна результата.")
            supports.append({"offset_mm": round(support["position"] - rail["y0"], 4),
                             "bracket_indices": references})
        result["members"].append({"rail_index": index, "zone_id": str(rail.get("zone") or ""),
            "support_count": source["support_count"], "span_count": source["span_count"],
            "intervals_mm": list(source["intervals"]), "bottom_free_mm": source["bottom_free"],
            "top_free_mm": source["top_free"], "supports": supports,
            "profile_match": match_reference("rail", rail.get("profile"))})
    # Exact same-axis neighbours are descriptive pairs, including gaps and
    # overlaps. No maximum gap, joint strength or moment transfer is invented.
    axes = {}
    for index, rail in enumerate(rails):
        axes.setdefault(rail["x"], []).append(index)
    for indices in axes.values():
        indices.sort(key=lambda i: (rails[i]["y0"], rails[i]["y1"], i))
        for first, second in zip(indices, indices[1:]):
            result["joints"].append({"first_rail_index": first, "second_rail_index": second,
                "gap_mm": round(rails[second]["y0"] - rails[first]["y1"], 4),
                "status": "geometric_adjacency_only"})
    result["joints"].sort(key=lambda item: (item["first_rail_index"], item["second_rail_index"]))
    _summarize(result)
    return result


def merge_connection_passports(req, entries):
    """Join per-zone compact inventories using final emitted array offsets.

entries: (zone_id, local_passport, rail_offset, bracket_offset). Geometry from
different zones is never matched, even for identical world coordinates.
"""
    result = _empty(req, scope_reason(req))
    if result["reason"]:
        return result
    entries = list(entries)
    if not entries:
        return _empty(req, "Нет результатов зон для паспорта соединений.")
    for zone, passport, rail_offset, bracket_offset in entries:
        if not passport or passport.get("schema") != SCHEMA or passport.get("status") != "inventory_only":
            return _empty(req, "Зона %s: %s" % (zone, (passport or {}).get("reason") or "паспорт соединений не сформирован"))
        if passport.get("catalog_revision") != result["catalog_revision"]:
            return _empty(req, "Паспорта зон относятся к разным редакциям источников.")
        for source in passport["members"]:
            member = copy.deepcopy(source)
            member["rail_index"] += rail_offset
            member["zone_id"] = str(zone)
            for support in member["supports"]:
                support["bracket_indices"] = [i + bracket_offset for i in support["bracket_indices"]]
            result["members"].append(member)
        for source in passport["joints"]:
            joint = dict(source)
            joint["first_rail_index"] += rail_offset
            joint["second_rail_index"] += rail_offset
            result["joints"].append(joint)
    _summarize(result)
    return result
