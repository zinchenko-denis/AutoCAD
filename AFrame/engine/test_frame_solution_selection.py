# -*- coding: utf-8 -*-
"""Historical catalogue contract, refusal boundary and one-snapshot integration."""
import copy
from dataclasses import FrozenInstanceError
import hashlib
import json
from pathlib import Path
import re
import unittest
from unittest.mock import patch

import frame_engine as fe
import frame_plan as fp
import frame_solution_catalog as catalog
import frame_solution_selection as selection

ROOT = Path(__file__).resolve().parents[2]


def example_selection():
    """Synthetic user inputs for tests only; not a default/design recommendation."""
    cat = catalog.catalog_snapshot()
    return {"schema": "aframe_solution_selection/1", "catalog_id": cat["catalog_id"],
        "catalog_revision": cat["revision"], "solution_id": cat["solution"]["solution_id"],
        "source_id": cat["source"]["source_id"], "source_sha256": cat["source"]["sha256"],
        "node": copy.deepcopy(cat["solution"]["node"]),
        "bracket": {"family_id": "kr2", "execution": "galvanized_painted", "nominal_width_mm": 70, "L_mm": 200},
        "extender": {"family_id": "uk", "execution": "galvanized_painted", "nominal_width_mm": 70, "L_mm": 100, "thickness_mm": 1.2},
        "profile": {"family_id": "gp", "execution": "galvanized_painted", "a_mm": 40, "b_mm": 40,
                    "thickness_mm": 1.2, "b_basis": "project_declared", "thickness_basis": "project_declared"},
        "geometry": {"cladding_front_offset_mm": None, "cladding_offset_basis": "wall_structural_face_to_cladding_exterior", "insulation_layers_mm": []}}


def plan_request():
    return {"system": {"name": "Вектор-1", "bracket_step": 800, "bracket_step_corner": 600},
        "sub_type": "vertical", "cladding": "porcelain", "parts": "frame", "exact_step": True,
        "joints_x": [300, 900], "rows_y": [600, 1200, 1800],
        "contours": [{"outer": [[0, 0], [1200, 0], [1200, 2400], [0, 2400]], "holes": []}]}


def engine_request(count=2):
    req = plan_request()
    contour = req.pop("contours")[0]["outer"]
    req.update({"op": "frame", "zones": [{"zone_id": "zone_%d" % n,
        "zone": {"contour": {"pts": [[x + 2000*n, y] for x, y in contour]}, "openings": []},
        "joints_x": [300 + 2000*n, 900 + 2000*n]} for n in range(count)]})
    return req


class Catalogue(unittest.TestCase):
    def test_generated_export_and_csharp_snapshot_match(self):
        path = ROOT / "Common/catalogs/vector1_2015_type1_historical.json"
        self.assertEqual(path.read_bytes(), catalog.export_text().encode("utf-8"))
        cs = (ROOT / "AFrame/src/AFramePlugin/FrameSolutionSelection.cs").read_text(encoding="utf-8")
        found = re.search(r'private const string CatalogJson = @"((?:""|[^"])*)";', cs)
        self.assertIsNotNone(found, "C# embedded catalogue snapshot must be generated")
        self.assertEqual(found.group(1).replace('""', '"'), catalog.canonical_text(catalog.catalog_snapshot()))
        data = catalog.catalog_snapshot()
        revision = data.pop("revision")
        self.assertEqual(revision, hashlib.sha256(catalog.canonical_text(data).encode("utf-8")).hexdigest())

    def test_source_boundaries_and_detached_snapshot(self):
        data = catalog.catalog_snapshot()
        self.assertEqual(data["solution"]["node"], {"pdf_page": 20, "sheet": "4.2.1"})
        self.assertIsNone(data["solution"]["overlap_minimum_mm"])
        self.assertIsNone(data["solution"]["adjustment_interval_mm"])
        self.assertEqual(data["solution"]["local_clearance"]["to"], "nearest_profile_surface")
        self.assertIsNone(data["families"]["gp"]["dimensions"]["b_mm"]["values"])
        data["families"]["kr2"]["dimensions"]["L_mm"]["minimum"] = 0
        self.assertEqual(catalog.catalog_snapshot()["families"]["kr2"]["dimensions"]["L_mm"]["minimum"], 50)


class Declaration(unittest.TestCase):
    def assert_invalid(self, value, code=None):
        with self.assertRaises(selection.SelectionError) as cm:
            selection.validate_selection(value)
        if code:
            self.assertEqual(cm.exception.code, code)

    def test_all_printed_family_dimensions_and_executions(self):
        for width in (50, 60, 70, 85):
            for length in range(50, 351, 10):
                for execution in ("galvanized_painted", "corrosion_resistant"):
                    value = example_selection()
                    value["bracket"].update(nominal_width_mm=width, L_mm=length, execution=execution)
                    self.assertEqual(selection.validate_selection(value), value)
        for length in (100, 150):
            for thickness in (1, 1.2, 1.5):
                for a in (40, 50, 60, 80):
                    value = example_selection()
                    value["extender"].update(L_mm=length, thickness_mm=thickness)
                    value["profile"]["a_mm"] = a
                    self.assertEqual(selection.validate_selection(value), value)

    def test_unknown_missing_and_extra_fields_refuse(self):
        template = example_selection()
        for key in template:
            with self.subTest(key=key):
                value = copy.deepcopy(template)
                del value[key]
                self.assert_invalid(value, "E_SOLUTION_MALFORMED")
        for path in (None, "node", "bracket", "extender", "profile", "geometry"):
            value = example_selection()
            node = value if path is None else value[path]
            node["approved_capacity"] = 1234
            self.assert_invalid(value, "E_SOLUTION_MALFORMED")
        for key in ("schema", "catalog_id", "catalog_revision", "solution_id", "source_id", "source_sha256"):
            value = example_selection()
            value[key] = "future-or-unknown"
            self.assert_invalid(value, "E_SOLUTION_IDENTITY")

    def test_malformed_numbers_are_not_coerced(self):
        cases = [("bracket", "L_mm"), ("extender", "thickness_mm"), ("profile", "b_mm"),
                 ("profile", "thickness_mm"), ("node", "pdf_page")]
        for role, key in cases:
            for bad in (None, True, False, "1.2", "", 0, -1, float("nan"), float("inf"), -float("inf"), 10**1000, [], {}):
                value = example_selection()
                value[role][key] = bad
                with self.subTest(role=role, key=key, bad=str(bad)[:20]):
                    self.assert_invalid(value)
        for bad in (0, -2, True, "100", float("nan")):
            value = example_selection()
            value["geometry"]["insulation_layers_mm"] = [bad]
            self.assert_invalid(value, "E_SOLUTION_MALFORMED")

    def test_unknown_dimensions_node_and_basis_refuse(self):
        cases = [("bracket", "nominal_width_mm", 54), ("bracket", "L_mm", 205),
            ("bracket", "L_mm", 360), ("extender", "L_mm", 200),
            ("extender", "thickness_mm", 2), ("profile", "a_mm", 70),
            ("node", "pdf_page", 19), ("node", "sheet", "4.1"),
            ("profile", "b_basis", "calculated"), ("geometry", "cladding_offset_basis", "lever_arm")]
        for role, key, bad in cases:
            value = example_selection()
            value[role][key] = bad
            self.assert_invalid(value)

    def test_different_widths_and_executions_do_not_assert_compatibility(self):
        value = example_selection()
        value["extender"].update(nominal_width_mm=50, execution="corrosion_resistant")
        req = dict(plan_request(), solution_selection=value)
        prepared, error = selection.prepare_solution(req)
        self.assertIsNone(error)
        self.assertEqual(prepared.report()["assembly_compatibility"], "not_verified")
        self.assertEqual(prepared.report()["physical_assignment"], "not_asserted")
        self.assertIsNone(prepared.report()["product_id"])

    def test_prepared_snapshot_and_reports_are_detached(self):
        value = example_selection()
        prepared, error = selection.prepare_solution(dict(plan_request(), solution_selection=value))
        self.assertIsNone(error)
        value["bracket"]["L_mm"] = 350
        report = prepared.report()
        self.assertEqual(report["selection"]["bracket"]["L_mm"], 200)
        report["selection"]["bracket"]["L_mm"] = 60
        self.assertEqual(prepared.report()["selection"]["bracket"]["L_mm"], 200)
        with self.assertRaises(FrozenInstanceError):
            prepared.selection_json = "{}"

    def test_preparation_has_no_runtime_file_access(self):
        req = dict(plan_request(), solution_selection=example_selection())
        with patch("builtins.open", side_effect=AssertionError("pure selection must not read files")), \
                patch.object(Path, "open", side_effect=AssertionError("pure selection must not read files")):
            prepared, error = selection.prepare_solution(req)
            self.assertIsNone(error)
            self.assertEqual(prepared.report()["selection"], req["solution_selection"])


class EngineBoundary(unittest.TestCase):
    def test_legacy_geometry_and_metadata_unchanged(self):
        req = plan_request()
        before = copy.deepcopy(req)
        implicit = fp.frame_plan(req)
        explicit_null = fp.frame_plan(dict(req, solution_selection=None))
        self.assertTrue(implicit["ok"], implicit)
        self.assertEqual(implicit, explicit_null)
        self.assertEqual(req, before)
        self.assertNotIn("solution_report", implicit)

    def test_manual_selection_preserves_geometry_without_assigning_products(self):
        req = plan_request()
        baseline = fp.frame_plan(req)
        value = example_selection()
        value["profile"].update(a_mm=80, b_mm=121, thickness_mm=3.4)
        value["geometry"].update(cladding_front_offset_mm=375, insulation_layers_mm=[80, 50])
        selected_req = dict(req, solution_selection=value)
        before = copy.deepcopy(selected_req)
        actual = fp.frame_plan(selected_req)
        self.assertTrue(actual["ok"], actual)
        for key in ("rails", "hrails", "brackets", "clamps", "fittings", "system_used", "summary", "connection_passport"):
            self.assertEqual(actual.get(key), baseline.get(key), key)
        self.assertEqual(actual["solution_report"]["selection"], value)
        self.assertEqual(selected_req, before)

    def test_explicit_calc_refuses_before_any_geometry(self):
        for calc in ({}, False, {"profile": "ШП-60-20-20-1,2", "offset": 230}):
            req = dict(plan_request(), solution_selection=example_selection(), calc=calc)
            with patch.object(fp, "_frame_plan", side_effect=AssertionError("geometry must not run")):
                result = fp.frame_plan(req)
            self.assertEqual(result["error_code"], "E_SOLUTION_CALC_UNCONFIRMED")
            self.assertNotIn("rails", result)

    def test_scope_and_legacy_profile_conflict_refuse(self):
        for delta in ({"system": "Standart"}, {"system": "NordFOX"}, {"system": None},
                {"system": {"name": "Вектор-1", "_name": "NordFOX"}},
                {"sub_type": "interfloor"}, {"sub_type": "ortho"}, {"cladding": "composite"},
                {"cladding": "clinker"}, {"rail_profile": "ГП-60-40"}):
            req = dict(plan_request(), solution_selection=example_selection(), **delta)
            with patch.object(fp, "_frame_plan", side_effect=AssertionError("geometry must not run")):
                result = fp.frame_plan(req)
            self.assertFalse(result["ok"], delta)
            self.assertNotIn("rails", result)

    def test_multizone_validates_once_and_emits_one_snapshot(self):
        req = dict(engine_request(12), solution_selection=example_selection())
        with patch.object(selection, "validate_selection", wraps=selection.validate_selection) as validate:
            result = fe.run(req)
        self.assertTrue(result["ok"], result)
        self.assertEqual(validate.call_count, 1)
        self.assertEqual(result["summary"]["zones"], 12)
        self.assertEqual(json.dumps(result).count('"aframe_solution_selection/1"'), 1)
        self.assertEqual(result["solution_report"]["selection"], req["solution_selection"])
        self.assertEqual(len([n for n in result["notes"] if n.startswith("Решение:")]), 1)

    def test_malformed_multizone_refuses_before_grouping(self):
        req = dict(engine_request(3), solution_selection=example_selection())
        req["solution_selection"]["source_sha256"] = "bad"
        with patch.object(fe, "_zone_to_contour", side_effect=AssertionError("must not read geometry")):
            result = fe.run(req)
        self.assertEqual(result["error_code"], "E_SOLUTION_IDENTITY")
        self.assertNotIn("rails", result)

    def test_json_cannot_supply_a_prevalidated_selection(self):
        req = dict(engine_request(1), solution_selection=example_selection(),
                   prepared_solution=None, _prepared_solution=None, _validated=True)
        req["solution_selection"]["catalog_revision"] = "unconfirmed"
        result = fe.run(req)
        self.assertEqual(result["error_code"], "E_SOLUTION_IDENTITY")
        self.assertNotIn("rails", result)

    def test_clamps_only_cannot_relabel_existing_rails(self):
        req = plan_request()
        rails = fp.frame_plan(req)["rails"]
        req.update(parts="clamps", rails_fixed=rails, solution_selection=example_selection())
        before = copy.deepcopy(rails)
        result = fp.frame_plan(req)
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["rails"], [])
        self.assertEqual(result["brackets"], [])
        self.assertEqual(req["rails_fixed"], before)
        report = result["solution_report"]
        self.assertEqual(report["status"], "retained_identity_only")
        self.assertEqual(report["geometry_effect"], "no_new_frame")
        self.assertEqual(report["retention_validation"], "caller_must_verify_saved_selection")
        self.assertEqual(report["physical_assignment"], "not_asserted")

    def test_legacy_calculation_still_reaches_existing_refusal(self):
        req = plan_request()
        req.update(sub_type="interfloor", calc={"wind_region": "II", "terrain": "B", "height": 40,
            "q_clad": 25, "offset": 230, "na_max": 1880, "profile": "ШП-60-20-20-1,2", "q_rails": 1.21})
        result = fp.frame_plan(req)
        self.assertFalse(result["ok"])
        self.assertNotEqual(result.get("error_code"), "E_SOLUTION_CALC_UNCONFIRMED")
        self.assertNotIn("solution_report", result)


if __name__ == "__main__":
    unittest.main()
