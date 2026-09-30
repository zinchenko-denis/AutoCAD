"""Identity/scope regression tests; no claimed engineering or CAD validation."""
import copy
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import frame_rules as rules

ROOT = Path(__file__).resolve().parents[2]


class ContractTests(unittest.TestCase):
    def resolve(self, system="Вектор-1", sub="vertical", cladding="porcelain", **extra):
        req = dict(system=system, sub_type=sub, cladding=cladding, **extra)
        return rules.resolve_layout_contract(req, {}, sub)

    def test_unknown_named_override_is_not_custom(self):
        for system in ({"name": "NordFOX", "bracket_step": 800}, {"name": "unknown"}, "NordFOX", "Вектор-4"):
            with self.subTest(system=system):
                result = self.resolve(system)
                self.assertFalse(result["ok"])
                self.assertEqual(result["error_code"], "E_SYSTEM_UNSUPPORTED")
                self.assertNotIn("contract", result)

    def test_unknown_scheme_does_not_fall_through_to_vertical(self):
        for sub in ("unknown", "interfl_direct", "type5", "", None):
            with self.subTest(sub=sub):
                result = self.resolve(sub=sub)
                self.assertFalse(result["ok"])
                self.assertEqual(result["error_code"], "E_LAYOUT_SCHEME_UNSUPPORTED")

    def test_unknown_cladding_is_not_porcelain(self):
        for material in ("aluminium", "unknown", "stone"):
            with self.subTest(material=material):
                result = self.resolve(cladding=material)
                self.assertFalse(result["ok"])
                self.assertEqual(result["error_code"], "E_CLADDING_UNSUPPORTED")

    def test_named_presets_and_three_schemes_remain_available(self):
        for name in rules.REGISTRY["presets"]:
            for scheme in rules.SUPPORTED_SCHEMES:
                for material in rules.SUPPORTED_CLADDINGS:
                    with self.subTest(name=name, scheme=scheme, material=material):
                        self.assertTrue(self.resolve(name, scheme, material)["ok"])

    def test_unnamed_custom_keeps_parameters_without_manufacturer_claim(self):
        raw = {"bracket_step": 523, "rail_gap": 7.5}
        before = copy.deepcopy(raw)
        result = self.resolve(raw)
        self.assertTrue(result["ok"])
        c = result["contract"]
        self.assertEqual(c["registry_id"], "custom_unnamed")
        for key in ("manufacturer_id", "series", "album_edition", "approved_node_id"):
            self.assertIsNone(c[key])
        self.assertEqual(c["source_ids"], [])
        self.assertEqual(c["album_type_candidates"], [])
        self.assertEqual(raw, before)
        self.assertEqual(rules.declared_scope(c)["metadata"]["scope"], "custom_geometry")

    def test_known_named_overrides_do_not_mutate_inputs(self):
        req = {"system": {"name": "Вектор-1", "bracket_step": 650}, "calc": {"height": 30}}
        system = {"_name": "Вектор-1", "bracket_step": 650}
        before = copy.deepcopy((req, system))
        c = rules.resolve_layout_contract(req, system, "vertical")["contract"]
        self.assertTrue(c["preset_overrides_present"])
        self.assertTrue(c["calculation_requested"])
        self.assertFalse(c["design_limits_from_preset"])
        self.assertEqual((req, system), before)

    def test_default_identity_matches_existing_planner_default(self):
        for req in ({}, {"system": None}, {"system": ""}, {"system": {}}):
            with self.subTest(req=req):
                c = rules.resolve_layout_contract(req, {"_name": "Standart"}, "vertical")["contract"]
                self.assertEqual(c["requested_system_name"], "Standart")
                self.assertEqual(c["registry_id"], "legacy_standart_geometry")

    def test_cladding_attachment_scope_is_separate_from_scheme(self):
        for material, expected in (("porcelain", "porcelain_clamps_geometry"),
                                   ("composite", "frame_only_attachment_unimplemented"),
                                   ("clinker", "tile_rail_geometry_by_workflow"),
                                   ("concrete", "tile_rail_geometry_by_workflow")):
            for scheme in rules.SUPPORTED_SCHEMES:
                with self.subTest(material=material, scheme=scheme):
                    c = self.resolve(sub=scheme, cladding=material)["contract"]
                    self.assertEqual(c["attachment_scope"], expected)
                    self.assertEqual(c["manufacturer_compliance"], "not_asserted")

    def test_fire_note_is_conditional_without_universal_number(self):
        c = self.resolve()["contract"]
        absent = rules.declared_scope(c, False)["notes"]
        present = rules.declared_scope(c, True)["notes"]
        self.assertFalse(any("КГ с проёмами" in note for note in absent))
        fire = [note for note in present if "КГ с проёмами" in note]
        self.assertEqual(len(fire), 1)
        self.assertNotIn("350", fire[0])
        composite = rules.declared_scope(self.resolve(cladding="composite")["contract"], True)["notes"]
        self.assertFalse(any("КГ с проёмами" in note for note in composite))

    def test_manual_preset_is_not_a_calculation_limit(self):
        c = self.resolve()["contract"]
        out = rules.declared_scope(c)
        self.assertFalse(out["metadata"]["calculation_requested"])
        self.assertTrue(any("Ручные шаги" in note for note in out["notes"]))
        calculated = self.resolve(calc={})["contract"]
        self.assertTrue(calculated["calculation_requested"])
        self.assertNotIn("calculation_passed", calculated)

    def test_frame_only_does_not_claim_attachment_output(self):
        out = rules.declared_scope(self.resolve(cladding="composite", parts="frame")["contract"])
        self.assertTrue(out["metadata"]["frame_geometry_requested"])
        self.assertFalse(out["metadata"]["cladding_attachment_requested"])
        self.assertTrue(any("крепление конкретной кассеты не реализовано" in note for note in out["notes"]))

    def test_type5_is_not_mapped_to_interfloor(self):
        for source in ("vector1_2015", "vector5_2017"):
            direct = rules.REGISTRY["album_types"][source + "_type5"]
            self.assertIsNone(direct["code_scheme"])
            self.assertEqual(direct["correspondence"], "unsupported")
            cross = rules.REGISTRY["album_types"][source + "_type4"]
            self.assertEqual(cross["code_scheme"], "interfloor")
            self.assertEqual(cross["correspondence"], "partial")

    def test_nordfox_is_separate_aluminium_and_unsupported(self):
        item = rules.REGISTRY["manufacturers"]["nordfox"]
        self.assertEqual(item["frame_material_family"], "aluminium")
        self.assertEqual(item["implementation_status"], "unsupported")

    def test_runtime_contract_does_not_open_catalog_files(self):
        with patch("builtins.open", side_effect=AssertionError("runtime must not load external catalogue")):
            c = self.resolve()["contract"]
            self.assertTrue(rules.declared_scope(c)["metadata"])

    def test_returned_metadata_cannot_mutate_registry_or_contract(self):
        snapshot = rules.registry_snapshot()
        snapshot["presets"].clear()
        self.assertEqual(len(rules.REGISTRY["presets"]), 4)
        c = self.resolve()["contract"]
        before = copy.deepcopy(c)
        out = rules.declared_scope(c)
        out["metadata"]["known_missing"].clear()
        self.assertEqual(c, before)


class RegistryIntegrity(unittest.TestCase):
    def test_known_names_match_actual_preset_catalog(self):
        existing = json.loads((Path(__file__).with_name("systems.json")).read_text(encoding="utf-8"))
        self.assertEqual(set(rules.REGISTRY["presets"]), set(existing["systems"]))

    def test_generated_json_matches_single_python_source(self):
        snapshot = json.loads((Path(__file__).with_name("assembly_catalog.json")).read_text(encoding="utf-8"))
        self.assertEqual(snapshot, rules.registry_snapshot())

    def test_public_source_links_exist_without_private_repository(self):
        for source in rules.REGISTRY["sources"].values():
            for key in ("public_summary", "review"):
                if key in source:
                    with self.subTest(path=source[key]):
                        self.assertTrue((ROOT / source[key]).is_file())
                        self.assertNotIn("..", Path(source[key]).parts)

    def test_registry_references_and_page_identities_resolve(self):
        sources = rules.REGISTRY["sources"]

        def walk(value):
            if isinstance(value, dict):
                if "source_id" in value:
                    self.assertIn(value["source_id"], sources)
                    count = sources[value["source_id"]].get("pdf_pages")
                    for locator in value.get("locators", [value] if "pdf_page" in value else []):
                        self.assertGreater(locator["pdf_page"], 0)
                        if count is not None:
                            self.assertLessEqual(locator["pdf_page"], count)
                        self.assertTrue(locator["sheet"])
                for source_id in value.get("source_ids", []):
                    self.assertIn(source_id, sources)
                for missing in value.get("missing", []) if isinstance(value.get("missing"), list) else []:
                    self.assertIn(missing, rules.REGISTRY["known_missing"])
                for child in value.values():
                    walk(child)
            elif isinstance(value, list):
                for child in value:
                    walk(child)

        walk(rules.REGISTRY)


class PlannerIntegration(unittest.TestCase):
    def request(self, **extra):
        return dict(contours=[{"outer": [[0, 0], [1800, 0], [1800, 2400], [0, 2400]],
                               "holes": []}], joints_x=[300, 900, 1500],
                    rows_y=[0, 600, 1200, 1800, 2400], **extra)

    def test_unknown_identity_refused_before_all_geometry_modes(self):
        from frame_plan import frame_plan
        for parts in ("all", "frame", "clamps"):
            for extra, code in (({"system": {"name": "NordFOX"}}, "E_SYSTEM_UNSUPPORTED"),
                                ({"sub_type": "type5"}, "E_LAYOUT_SCHEME_UNSUPPORTED"),
                                ({"cladding": "unknown"}, "E_CLADDING_UNSUPPORTED")):
                with self.subTest(parts=parts, extra=extra):
                    out = frame_plan(self.request(parts=parts, **extra))
                    self.assertFalse(out["ok"])
                    self.assertEqual(out["error_code"], code)
                    self.assertFalse(out.get("rails"))
                    self.assertFalse(out.get("clamps"))

    def test_scope_survives_clamps_only_early_return(self):
        from frame_plan import frame_plan
        req = self.request()
        first = frame_plan(req)
        self.assertTrue(first["ok"], first)
        out = frame_plan(dict(req, parts="clamps", rails_fixed=first["rails"]))
        self.assertTrue(out["ok"], out)
        self.assertTrue(out["clamps"])
        self.assertFalse(out["design_scope"]["frame_geometry_requested"])
        self.assertEqual(out["design_scope"]["manufacturer_compliance"], "not_asserted")

    def test_engine_retains_each_zones_opening_scope(self):
        from frame_engine import run
        outer = {"pts": [[0, 0], [1800, 0], [1800, 2400], [0, 2400]]}
        opening = {"poly": {"pts": [[600, 700], [1200, 700], [1200, 1500], [600, 1500]]}}
        out = run({"op": "frame", "zones": [
            {"zone_id": "solid", "zone": {"outer": outer}},
            {"zone_id": "window", "zone": {"outer": outer, "openings": [opening]}}],
            "joints_x": [300, 900, 1500], "rows_y": [0, 600, 1200, 1800, 2400]})
        self.assertTrue(out["ok"], out)
        scopes = {x["zone_id"]: x["scope"] for x in out["design_scope"]["per_zone"]}
        self.assertFalse(scopes["solid"]["has_openings"])
        self.assertTrue(scopes["window"]["has_openings"])
        self.assertTrue(any("window: КГ с проёмами" in note for note in out["notes"]))
        self.assertFalse(any("solid: КГ с проёмами" in note for note in out["notes"]))


if __name__ == "__main__":
    unittest.main(verbosity=2)
