#!/usr/bin/env python3
"""Generate an AutoCAD 2024 authoring LISP; this does not generate a DWG.

Only the command processor in a licensed AutoCAD host creates the native
parameters/actions. Input geometry is deliberately separate (private sources).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
from pathlib import Path
from typing import Any

SCHEMA = "af_node_native_author/1"
PROPERTIES = ("AFN_INSULATION", "AFN_CLADDING_X", "AFN_VARIANT")
MARKER_KEY = "AF_NODE_LIBRARY"
ENTITY_TYPES = {"line", "polyline", "circle", "text", "dimension", "attribute"}


class ContractError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ContractError(message)


def _number(value: Any, path: str, positive: bool = False) -> float:
    _require(type(value) in (int, float) and math.isfinite(value), f"{path}: finite number required")
    _require(not positive or value > 0, f"{path}: positive value required")
    return float(value)


def _point(value: Any, path: str) -> list[float]:
    _require(isinstance(value, list) and len(value) == 2, f"{path}: [x, y] required")
    return [_number(x, path) for x in value]


def _text(value: Any, path: str, maximum: int = 512) -> str:
    _require(isinstance(value, str) and 0 < len(value) <= maximum, f"{path}: nonempty string required")
    _require(not any(ord(c) < 32 for c in value), f"{path}: control characters are forbidden")
    return value


def validate(data: dict[str, Any]) -> dict[str, Any]:
    """Reject ambiguous geometry/action mappings before any native mutation.

    Numeric limits here are graphic-input checks, never catalogue or structural
    limits. The node source/provenance remains part of the private input.
    """
    _require(isinstance(data, dict), "root must be an object")
    _require(data.get("schema") == SCHEMA, "unsupported schema")
    _require(data.get("version") == 1 and type(data.get("version")) is int, "version must be 1")
    _require(data.get("units") == "mm", "units must be mm")
    _require(data.get("origin") == [0, 0], "explicit base point [0, 0] required")
    _text(data.get("node_id"), "node_id", 128)
    name = _text(data.get("block_name"), "block_name", 128)
    _require(bool(re.fullmatch(r"AFN_[A-Z0-9_]+", name)), "block_name must use AFN_ and ASCII identifiers")
    _require(isinstance(data.get("provenance"), dict) and data["provenance"], "provenance is required")
    variants = data.get("variants")
    _require(isinstance(variants, list) and len(variants) == 2, "exactly two variants required")
    for variant in variants:
        _require(bool(re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,127}", _text(variant, "variant"))), "invalid variant identifier")
    _require(len(set(variants)) == 2, "variant identifiers must be distinct")
    for state_name in ("defaults", "alternate"):
        state = data.get(state_name)
        _require(isinstance(state, dict) and set(state) == set(PROPERTIES), f"{state_name}: exact three properties required")
        for name in PROPERTIES[:2]:
            _number(state[name], f"{state_name}.{name}", positive=True)
        _require(state[PROPERTIES[1]] > state[PROPERTIES[0]], f"{state_name}: cladding X must exceed insulation thickness")
        _require(state[PROPERTIES[2]] in variants, f"{state_name}: unknown variant")
    _require(data["defaults"][PROPERTIES[2]] == variants[0], "default variant must be first")
    _require(data["alternate"][PROPERTIES[2]] == variants[1], "alternate variant must be second")
    for name in PROPERTIES[:2]:
        _require(data["defaults"][name] != data["alternate"][name], f"{name}: two distinct verification values required")

    parameters = data.get("parameters")
    _require(isinstance(parameters, dict) and set(parameters) == set(PROPERTIES[:2]), "exact two linear parameter definitions required")
    for name, spec in parameters.items():
        _require(isinstance(spec, dict), f"{name}: object required")
        _require(set(spec) == {"start", "end", "label", "dimension_id"}, f"{name}: start/end/label/dimension_id required")
        _text(spec.get("dimension_id"), f"{name}.dimension_id", 128)
        p1, p2 = _point(spec.get("start"), name), _point(spec.get("end"), name)
        _point(spec.get("label"), name)
        _require(p1[0] == 0 and p2[0] == data["defaults"][name] and p1[1] == p2[1], f"{name}: explicit horizontal wall datum and default endpoint required")

    entities = data.get("entities")
    _require(isinstance(entities, list) and 1 <= len(entities) <= 10000, "entities: nonempty bounded array required")
    ids, by_id, attribute_tags = set(), {}, set()
    allowed_common = {"id", "type", "variant", "color", "role"}
    type_keys = {
        "line": {"start", "end"}, "polyline": {"points", "closed"},
        "circle": {"center", "radius"}, "text": {"position", "height", "text"},
        "dimension": {"p1", "p2", "line_point", "height"},
        "attribute": {"position", "height", "tag", "text", "prompt"},
    }
    for entity in entities:
        _require(isinstance(entity, dict), "entity must be an object")
        ident = _text(entity.get("id"), "entity.id", 128)
        _require(ident not in ids, f"duplicate entity id: {ident}")
        ids.add(ident)
        by_id[ident] = entity
        kind = entity.get("type")
        _require(kind in ENTITY_TYPES, f"{ident}: unsupported entity type {kind}")
        _require(set(entity) <= allowed_common | type_keys[kind], f"{ident}: unknown entity fields")
        _require(entity.get("variant") is None or entity["variant"] in variants, f"{ident}: unknown variant")
        _require(type(entity.get("color", 256)) is int and 1 <= entity.get("color", 256) <= 256, f"{ident}: invalid ACI colour")
        if kind == "line":
            _require(_point(entity.get("start"), ident) != _point(entity.get("end"), ident), f"{ident}: zero-length line")
        elif kind == "polyline":
            pts = entity.get("points")
            _require(isinstance(pts, list) and 2 <= len(pts) <= 10000, f"{ident}: polyline points required")
            for pt in pts:
                _point(pt, ident)
            _require(type(entity.get("closed", False)) is bool, f"{ident}: closed must be boolean")
            _require(len({tuple(pt) for pt in pts}) >= 2, f"{ident}: degenerate polyline")
        elif kind == "circle":
            _point(entity.get("center"), ident)
            _number(entity.get("radius"), ident, positive=True)
        elif kind == "dimension":
            _require(_point(entity.get("p1"), ident) != _point(entity.get("p2"), ident), f"{ident}: degenerate dimension")
            _point(entity.get("line_point"), ident)
            _number(entity.get("height", 3), ident, positive=True)
        else:
            _point(entity.get("position"), ident)
            _number(entity.get("height"), ident, positive=True)
            _text(entity.get("text"), ident)
            if kind == "attribute":
                tag = _text(entity.get("tag"), ident).upper()
                _require(bool(re.fullmatch(r"[A-Z][A-Z0-9_]{0,63}", tag)), f"{ident}: invalid attribute tag")
                _require(tag not in attribute_tags, f"{ident}: duplicate attribute tag")
                attribute_tags.add(tag)
                _require(entity.get("variant") is None, f"{ident}: instance attributes must be common")
                _text(entity.get("prompt", tag), ident)

    bound_dimensions = set()
    for name, spec in parameters.items():
        ident = spec["dimension_id"]
        dimension = by_id.get(ident)
        _require(dimension is not None and dimension["type"] == "dimension", f"{name}: dimension_id must identify a dimension")
        _require(ident not in bound_dimensions, f"{name}: each parameter requires its own dimension_id")
        bound_dimensions.add(ident)
        _require(dimension.get("variant") is None, f"{name}: bound dimension must be common to both variants")
        p1, p2 = dimension["p1"], dimension["p2"]
        _require(p1[0] == 0 and p2[0] == data["defaults"][name] and p1[1] == p2[1],
                 f"{name}: bound dimension must measure the default distance from the wall datum")

    actions = data.get("actions")
    _require(isinstance(actions, list) and actions, "explicit actions are required")
    selected = {name: set() for name in PROPERTIES[:2]}
    for action in actions:
        _require(isinstance(action, dict), "action must be an object")
        _require(set(action) <= {"parameter", "kind", "entities", "frame"}, "unknown action fields")
        name, kind = action.get("parameter"), action.get("kind")
        _require(name in parameters and kind in ("move", "stretch"), "action parameter/kind unsupported")
        members = action.get("entities")
        _require(isinstance(members, list) and members and all(isinstance(x, str) for x in members), "action entity ids required")
        _require(len(set(members)) == len(members), "duplicate action entity id")
        _require(set(members) <= ids, f"{name}: unknown action entity")
        _require(not selected[name].intersection(members), f"{name}: object would move twice")
        selected[name].update(members)
        _require(not any(by_id[x]["type"] == "attribute" for x in members), "instance attributes are not action geometry in this version")
        if kind == "stretch":
            frame = action.get("frame")
            _require(isinstance(frame, list) and len(frame) == 2, "stretch frame required")
            p1, p2 = _point(frame[0], "frame"), _point(frame[1], "frame")
            _require(p1[0] < p2[0] and p1[1] < p2[1], "stretch frame must be nonempty min/max rectangle")
        else:
            _require("frame" not in action, "move action must not have a stretch frame")
        for other_name, spec in parameters.items():
            ident = spec["dimension_id"]
            if ident not in members:
                continue
            _require(name == other_name, f"{name}: cannot act on another parameter's bound dimension")
            _require(kind == "stretch", f"{name}: bound dimension needs endpoint stretch, not whole-object move")
            fixed, moving = by_id[ident]["p1"], by_id[ident]["p2"]
            inside = lambda pt: p1[0] < pt[0] < p2[0] and p1[1] < pt[1] < p2[1]
            outside = lambda pt: pt[0] < p1[0] or pt[0] > p2[0] or pt[1] < p1[1] or pt[1] > p2[1]
            _require(inside(moving) and outside(fixed), f"{name}: stretch frame must contain only the moving dimension endpoint, away from the boundary")
    for name, members in selected.items():
        _require(parameters[name]["dimension_id"] in members, f"{name}: bound dimension must be in its action selection")
        _require(any(by_id[x]["type"] in ("line", "polyline", "circle") for x in members), f"{name}: actual geometry required")
    for variant in variants:
        own = [x for x in entities if x.get("variant") == variant]
        _require(any(x["type"] in ("line", "polyline", "circle") for x in own), f"{variant}: discrete geometry required")
        _require(any(x["type"] == "text" for x in own), f"{variant}: native discrete mark required")
    return data


def lisp(value: Any) -> str:
    """Literal-only writer: source data cannot inject AutoLISP expressions."""
    if value is None or value is False:
        return "nil"
    if value is True:
        return "T"
    if isinstance(value, str):
        return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'
    if type(value) in (int, float):
        _number(value, "literal")
        return repr(value)
    if isinstance(value, list):
        return "(" + " ".join(map(lisp, value)) + ")"
    if isinstance(value, dict):
        return "(" + " ".join("(" + lisp(k) + " . " + lisp(v) + ")" for k, v in value.items()) + ")"
    raise ContractError(f"unsupported literal: {type(value).__name__}")


def render(data: dict[str, Any]) -> str:
    validate(data)
    canonical = json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    runtime = Path(__file__).with_name("native_author.lsp").read_text(encoding="utf-8")
    return (
        "; Generated for full AutoCAD 2024. Native execution has NOT occurred.\n"
        "; Geometry/data may be private: keep this generated file with its source.\n"
        f"; Source contract SHA256: {digest}\n"
        f"(setq afn:data '{lisp(data)})\n"
        + runtime
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path, help="UTF-8 .lsp output; keep private when geometry is private")
    args = parser.parse_args()
    data = json.loads(args.input.read_text(encoding="utf-8-sig"))
    output = render(data)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(output, encoding="utf-8-sig")
    print(f"Prepared {args.output}; native DWG NOT generated or verified. In a new AutoCAD 2024 drawing: APPLOAD, ATFNATIVEBUILD.")


if __name__ == "__main__":
    main()
