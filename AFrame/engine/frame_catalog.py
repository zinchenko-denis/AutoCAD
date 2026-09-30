# -*- coding: utf-8 -*-
"""Source identities for the first connection-inventory increment.

These are members printed in one independently reviewed project calculation,
not manufacturer SKUs or authorisation to use that calculation on another
project. Numerical section properties remain in frame_calc; this registry
does not introduce a second table of design values. Runtime uses no file I/O.
"""
import copy
import hashlib
import json


SOURCE_ID = "vector1_type1_reference_2026"
_SOURCE = {
    "source_id": SOURCE_ID,
    "sha256": "495f8fadf882dc6f6c3f2c023a08611035c5e916a3cc92ee4da675cb2a0f16d2",
    "kind": "private_project_calculation",
    "edition": "2026",
    "locator": "PDF 3: обозначения элементов расчётного примера",
}
_REFERENCES = [
    {"reference_id": "calc_C.kr2_70", "kind": "project_calculation_section",
     "designation": "КР2-70", "role": "bracket", "source_id": SOURCE_ID,
     "pdf_pages": [3], "properties_status": "not_imported"},
    {"reference_id": "calc_C.uk70_1_2", "kind": "project_calculation_section",
     "designation": "УК-70-1,2", "role": "extender", "source_id": SOURCE_ID,
     "pdf_pages": [3], "properties_status": "not_imported"},
    {"reference_id": "calc_C.gp40_40_1_2", "kind": "project_calculation_section",
     "designation": "ГП-40-40-1,2", "role": "rail", "source_id": SOURCE_ID,
     "pdf_pages": [3], "properties_status": "not_imported"},
]
_CATALOG = {"schema": "aframe_reference_catalog/1", "scope": "reference_project_only",
            "sources": [_SOURCE], "references": _REFERENCES}
CATALOG_REVISION = hashlib.sha256(json.dumps(
    _CATALOG, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()
_BY_DESIGNATION = {(item["role"], item["designation"]): item for item in _REFERENCES}


def catalog_snapshot():
    """A caller cannot alter the authored registry or its canonical revision."""
    result = copy.deepcopy(_CATALOG)
    result["revision"] = CATALOG_REVISION
    return result


def match_reference(role, designation):
    """Exact source identity only: no fuzzy family/Latin/dimension guessing."""
    found = _BY_DESIGNATION.get((role, designation)) if isinstance(designation, str) else None
    return {"status": "matched_source_identity" if found else "unknown",
            "reference_ids": [found["reference_id"]] if found else []}


def scope_reason(req):
    """The geometry preset is not proof of an album type or current system."""
    if req.get("parts") == "clamps":
        return "Только кляммеры: новый каркас и его соединения в этой операции не сформированы."
    if str(req.get("sub_type") or "vertical").strip().lower() != "vertical":
        return "Геометрический паспорт этого этапа доступен только для вертикальной схемы."
    raw = req.get("system")
    name = raw.get("name") if isinstance(raw, dict) else raw
    if name != "Вектор-1":
        return "Для выбранного пресета паспорт соединений ещё не реализован; система по имени не подменяется."
    if str(req.get("cladding") or "porcelain").strip().lower() != "porcelain":
        return "Первый паспорт ограничен вертикальной геометрией под керамогранит; узлы иной облицовки не подтверждены."
    return None
