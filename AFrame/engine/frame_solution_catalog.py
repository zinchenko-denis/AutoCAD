# -*- coding: utf-8 -*-
"""Canonical historical nomenclature for the explicit 2B1 selection.

Only the printed node 4.2.1 (KR2 + UK + GP) is represented. Nominal catalogue
dimensions are not capacities, product availability or assembly compatibility.
The JSON export feeds the C# embedded snapshot; runtime reads no catalog files.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path

CATALOG_ID = "vector1_2015_type1_historical"
SOLUTION_ID = "vector1_2015_type1_4_2_1"
SOURCE_SHA256 = "7386f4152de455a5e2622704d51a98d8f05afd20050807f634eea4572de37142"

_CATALOG = {
    "schema": "aframe_solution_catalog/1", "catalog_id": CATALOG_ID,
    "scope": "historical_nomenclature_membership_only",
    "source": {"source_id": "vector1_2015", "sha256": SOURCE_SHA256,
        "kind": "historical_manufacturer_album", "title": "АТР Вектор-1, 2015",
        "issuer_as_printed": "ООО «Вектор групп»", "edition_year": 2015,
        "edition_label": None, "pdf_pages": 109,
        "edition_note": "3.15 есть только в имени файла; самостоятельный номер редакции не подтверждён.",
        "current_product_availability": "not_confirmed", "current_manufacturer_approval": "not_confirmed"},
    "solution": {"solution_id": SOLUTION_ID, "title": "Тип 1, Г-профиль с КР2 и УК; узел 4.2.1",
        "node": {"pdf_page": 20, "sheet": "4.2.1"}, "geometry_preset": "Вектор-1",
        "sub_type": "vertical", "cladding": "porcelain",
        "families": {"bracket": "kr2", "extender": "uk", "profile": "gp"},
        "combination_compatibility": "not_confirmed", "calculation_binding": "not_confirmed",
        "overlap_minimum_mm": None, "adjustment_interval_mm": None,
        "local_clearance": {"minimum_mm": 20, "from": "outer_insulation_membrane_surface",
            "to": "nearest_profile_surface", "pdf_page": 20, "sheet": "4.2.1",
            "not": "distance_to_outer_cladding_surface", "checked_by_this_increment": False},
        "note": "Минимум 30 мм узла 4.1 относится к пластинам У2/У и не переносится на выбранный узел с УК."},
    "executions": [
        {"id": "galvanized_painted", "title": "Оцинкованная сталь с порошковой окраской", "mark_token": None,
         "steel_grade": None, "coating_thickness": None, "pdf_pages": [3, 6, 8, 9]},
        {"id": "corrosion_resistant", "title": "Коррозионностойкая сталь (КС)", "mark_token": "КС",
         "steel_grade": None, "coating_thickness": None, "pdf_pages": [3, 6, 8, 9]}],
    "families": {
        "kr2": {"family_id": "kr2", "role": "bracket", "printed_pattern": "КР2-(КС)-50(60,70,85)-L",
            "source": {"pdf_page": 6, "sheet": "3.2.1", "position": "2.2"},
            "dimensions": {"nominal_width_mm": {"values": [50, 60, 70, 85]},
                "L_mm": {"minimum": 50, "maximum": 350, "step": 10},
                "thickness_mm": {"value": 2, "basis": "sketch_dimension"}}},
        "uk": {"family_id": "uk", "role": "extender", "printed_pattern": "УК-(КС)-50(60,70,85)-L-c",
            "source": {"pdf_page": 8, "sheet": "3.3", "position": "3.2"},
            "dimensions": {"nominal_width_mm": {"values": [50, 60, 70, 85]},
                "L_mm": {"values": [100, 150]}, "thickness_mm": {"values": [1, 1.2, 1.5]},
                "sketch_width_mm_by_nominal": {"50": 54, "60": 64, "70": 74, "85": 89}}},
        "gp": {"family_id": "gp", "role": "profile", "printed_pattern": "ГП-(КС)-a-b-c",
            "source": {"pdf_page": 9, "sheet": "3.4", "position": "4.4"},
            "dimensions": {"a_mm": {"values": [40, 50, 60, 80]},
                "b_mm": {"basis": "project_and_strength_calculation_required", "values": None},
                "thickness_mm": {"basis": "project_and_strength_calculation_required", "values": None},
                "supply_length_mm": {"basis": "not_stated", "values": None}}}},
    "geometry_parameters": {"unit": "mm",
        "cladding_offset_basis": "wall_structural_face_to_cladding_exterior",
        "cladding_offset_meaning": "Заявленное пользователем расстояние от поверхности строительного основания до наружной поверхности облицовки.",
        "insulation_layers_basis": "project_declared_layer_thicknesses",
        "legacy_calculation_offset_is_separate": True,
        "automatic_bracket_length_equation": "not_confirmed"},
    "missing": ["current_sku", "approved_section_properties", "steel_grade", "manufacturing_tolerances",
        "hole_coordinates", "node_adjustment_interval", "fixed_sliding_contract", "splice_continuity",
        "anchor_substrate_design", "complete_fastener_bom", "automatic_bracket_selection"]
}


def canonical_text(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)


CATALOG_REVISION = hashlib.sha256(canonical_text(_CATALOG).encode("utf-8")).hexdigest()


def catalog_snapshot():
    result = copy.deepcopy(_CATALOG)
    result["revision"] = CATALOG_REVISION
    return result


def export_text():
    return json.dumps(catalog_snapshot(), ensure_ascii=False, sort_keys=True, indent=2, allow_nan=False) + "\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export", type=Path, required=True)
    args = parser.parse_args()
    args.export.parent.mkdir(parents=True, exist_ok=True)
    args.export.write_text(export_text(), encoding="utf-8")
