# -*- coding: utf-8 -*-
"""Strict, pure declaration of one historical solution; no engineering approval.

The prepared object belongs to one engine operation. Its JSON snapshot is an
immutable string, so per-zone geometry cannot mutate a selection after validation.
It is passed as a Python argument, never accepted from an untrusted JSON flag.
"""
from dataclasses import dataclass
import json
import math

from frame_solution_catalog import catalog_snapshot, canonical_text

_CATALOG = catalog_snapshot()
_SOLUTION = _CATALOG["solution"]
_EXECUTIONS = frozenset(e["id"] for e in _CATALOG["executions"])
_UNPREPARED = object()


class SelectionError(ValueError):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def _fail(code, message):
    raise SelectionError(code, message)


def _keys(value, expected, path):
    if not isinstance(value, dict) or set(value) != set(expected.split()):
        _fail("E_SOLUTION_MALFORMED", "%s: неверный состав полей выбора решения." % path)


def _number(value, path):
    if isinstance(value, bool) or not isinstance(value, (float, int)):
        _fail("E_SOLUTION_MALFORMED", "%s: требуется конечное положительное число." % path)
    try:
        valid = math.isfinite(value) and value > 0
    except (OverflowError, ValueError):
        valid = False
    if not valid:
        _fail("E_SOLUTION_MALFORMED", "%s: требуется конечное положительное число." % path)
    return value


def _exact(value, expected, path, code="E_SOLUTION_IDENTITY"):
    # bool is a number in Python but is never a PDF page or catalogue dimension.
    if isinstance(value, bool) or value != expected:
        _fail(code, "%s: значение не соответствует выбранному каталогу и его редакции." % path)


def _dimension(value, rule, path):
    _number(value, path)
    if "values" in rule:
        valid = value in rule["values"]
    else:
        valid = (rule["minimum"] <= value <= rule["maximum"] and
                 (value - rule["minimum"]) % rule["step"] == 0)
    if not valid:
        _fail("E_SOLUTION_DIMENSION", "%s: размер отсутствует в исторической номенклатуре." % path)


def validate_selection(value):
    """Return a detached, validated JSON DTO, or raise SelectionError.

    b/c of GP are explicitly declared positive project values, not values from
    a verified section table. Different member executions/widths are allowed:
    even equal choices cannot establish assembly compatibility without a rule.
    """
    _keys(value, "schema catalog_id catalog_revision solution_id source_id source_sha256 node bracket extender profile geometry", "solution_selection")
    for key, expected in (("schema", "aframe_solution_selection/1"),
            ("catalog_id", _CATALOG["catalog_id"]), ("catalog_revision", _CATALOG["revision"]),
            ("solution_id", _SOLUTION["solution_id"]), ("source_id", _CATALOG["source"]["source_id"]),
            ("source_sha256", _CATALOG["source"]["sha256"])):
        _exact(value[key], expected, key)
    _keys(value["node"], "pdf_page sheet", "node")
    _number(value["node"]["pdf_page"], "node.pdf_page")
    for key in ("pdf_page", "sheet"):
        _exact(value["node"][key], _SOLUTION["node"][key], "node." + key)
    shapes = {"bracket": "family_id execution nominal_width_mm L_mm",
              "extender": "family_id execution nominal_width_mm L_mm thickness_mm",
              "profile": "family_id execution a_mm b_mm thickness_mm b_basis thickness_basis"}
    for role, fields in shapes.items():
        member = value[role]
        _keys(member, fields, role)
        family_id = _SOLUTION["families"][role]
        _exact(member["family_id"], family_id, role + ".family_id")
        if not isinstance(member["execution"], str) or member["execution"] not in _EXECUTIONS:
            _fail("E_SOLUTION_IDENTITY", role + ": неизвестное исполнение изделия.")
        dimensions = _CATALOG["families"][family_id]["dimensions"]
        keys = ("a_mm",) if role == "profile" else (("nominal_width_mm", "L_mm", "thickness_mm") if role == "extender" else ("nominal_width_mm", "L_mm"))
        for key in keys:
            _dimension(member[key], dimensions[key], role + "." + key)
    for key in ("b_mm", "thickness_mm"):
        _number(value["profile"][key], "profile." + key)
    for key in ("b_basis", "thickness_basis"):
        _exact(value["profile"][key], "project_declared", "profile." + key)
    geometry = value["geometry"]
    _keys(geometry, "cladding_front_offset_mm cladding_offset_basis insulation_layers_mm", "geometry")
    _exact(geometry["cladding_offset_basis"], _CATALOG["geometry_parameters"]["cladding_offset_basis"], "geometry.cladding_offset_basis")
    if geometry["cladding_front_offset_mm"] is not None:
        _number(geometry["cladding_front_offset_mm"], "geometry.cladding_front_offset_mm")
    if not isinstance(geometry["insulation_layers_mm"], list):
        _fail("E_SOLUTION_MALFORMED", "geometry.insulation_layers_mm: требуется список толщин слоёв.")
    for index, layer in enumerate(geometry["insulation_layers_mm"]):
        _number(layer, "geometry.insulation_layers_mm[%s]" % index)
    return json.loads(canonical_text(value))


@dataclass(frozen=True)
class PreparedSolution:
    selection_json: str
    clamps_only: bool

    def report(self):
        return {"schema": "aframe_solution_report/1",
            "status": "retained_identity_only" if self.clamps_only else "declared_selection",
            "selection": json.loads(self.selection_json),
            "geometry_preset": _SOLUTION["geometry_preset"],
            "geometry_effect": "no_new_frame" if self.clamps_only else "existing_preset_only",
            "catalogue_dimensions": "validated_against_historical_source",
            "project_dimensions": "declared_not_verified",
            "assembly_compatibility": "not_verified", "calculation_binding": "not_confirmed",
            "physical_assignment": "not_asserted", "product_id": None,
            "retention_validation": "caller_must_verify_saved_selection" if self.clamps_only else "not_applicable",
            "limitations": ["historical_source_only", "project_profile_dimensions_unverified",
                "assembly_not_verified", "geometry_preset_not_catalogue_dimensions",
                "offset_not_calculation_lever_arm", "no_automatic_bracket_selection",
                "no_installed_product_assignment", "calculation_binding_unconfirmed"]}

    def notes(self):
        notes = ["Решение: исторический АТР Вектор-1, 2015, узел 4.2.1. Выбор изделий заявлен пользователем; совместимость сборки и сечение ГП не проверены.",
                 "Геометрия использует существующий пресет Вектор-1 и заданные шаги. Каталожные размеры не изменяют геометрию, не назначают изделия деталям и не определяют расчётное плечо или длину кронштейна."]
        if self.clamps_only:
            notes.append("Только кляммеры: сохранённая идентичность решения; новое назначение изделий существующему каркасу не выполняется. Команда должна проверить неизменность выбора в DWG.")
        return notes


def prepare_solution(req):
    """Validate once at the operation boundary; preserve legacy None as-is."""
    value = req.get("solution_selection")
    if value is None:
        return None, None
    try:
        selection = validate_selection(value)
        if req.get("calc") is not None:
            _fail("E_SOLUTION_CALC_UNCONFIRMED", "Расчёт для выбранных изделий АТР 2015 не разрешён: соответствие сечений, материала и исполнения расчётному источнику 2026 не подтверждено. Доступна расстановка с ручными шагами.")
        if req.get("rail_profile") is not None:
            _fail("E_SOLUTION_PROFILE_CONFLICT", "Явный каталог нельзя совмещать с выбором профиля прежнего расчётного справочника.")
        system = req.get("system")
        system_name = system.get("name") if isinstance(system, dict) else system
        if (system_name != _SOLUTION["geometry_preset"] or
                (isinstance(system, dict) and system.get("_name", system_name) != system_name) or
                (req.get("sub_type") or "vertical") != _SOLUTION["sub_type"] or
                (req.get("cladding") or "porcelain") != _SOLUTION["cladding"]):
            _fail("E_SOLUTION_SCOPE", "Выбранный исторический узел доступен только для вертикального пресета Вектор-1 с керамогранитом; иные области применения не подтверждены.")
        return PreparedSolution(canonical_text(selection), req.get("parts") == "clamps"), None
    except SelectionError as exc:
        return None, {"ok": False, "error_code": exc.code, "error": str(exc)}
