# -*- coding: utf-8 -*-
"""Reference identity, compact connection inventories and engine integration.

Synthetic dimensions test software invariants; they are not design rules or
manufacturer section properties. Existing topology tests own static refusals.
"""
import copy
import json
import unittest
from unittest.mock import patch

import frame_catalog as catalog
import frame_connections as connections
import frame_engine as engine
import frame_topology as topology
from test_frame_topology import request


def manual_request():
    result = request()
    result.pop("calc")
    return result


def geometry(ys=(0, 1500, 3000), x=0):
    return {"rails": [{"x": x, "y0": 0, "y1": 3000, "kind": "направляющая", "profile": "ГП-40-40-1,2"}],
            "brackets": [{"x": x, "y": y, "kind": "рядовой"} for y in ys]}


class ReferenceCatalogTests(unittest.TestCase):
    def test_identity_is_exact_and_has_no_manufacturer_sku(self):
        exact = catalog.match_reference("rail", "ГП-40-40-1,2")
        self.assertEqual(exact, {"status": "matched_source_identity", "reference_ids": ["calc_C.gp40_40_1_2"]})
        for value in ("ГП", "ГП-40-40", "ГП-40-40-1.2", "GP-40-40-1,2", " ГП-40-40-1,2", None):
            with self.subTest(value=value):
                self.assertEqual(catalog.match_reference("rail", value), {"status": "unknown", "reference_ids": []})
        self.assertEqual(catalog.match_reference("bracket", "ГП-40-40-1,2")["status"], "unknown")
        snapshot = catalog.catalog_snapshot()
        self.assertEqual(snapshot["scope"], "reference_project_only")
        self.assertEqual(snapshot["sources"][0]["sha256"], "495f8fadf882dc6f6c3f2c023a08611035c5e916a3cc92ee4da675cb2a0f16d2")
        for reference in snapshot["references"]:
            self.assertEqual(reference["kind"], "project_calculation_section")
            self.assertEqual(reference["properties_status"], "not_imported")
            self.assertNotIn("product_id", reference)
            self.assertNotIn("Wx", reference)

    def test_snapshot_isolated_revision_deterministic_no_runtime_file_io(self):
        with patch("builtins.open", side_effect=AssertionError("runtime catalogue must not open files")):
            first = catalog.catalog_snapshot()
            first["sources"][0]["sha256"] = "changed"
            first["references"][0]["designation"] = "changed"
            second = catalog.catalog_snapshot()
            self.assertEqual(second["revision"], catalog.CATALOG_REVISION)
            self.assertNotEqual(first["sources"], second["sources"])
            self.assertEqual(catalog.match_reference("bracket", "КР2-70")["status"], "matched_source_identity")

    def test_scope_is_explicit_geometry_preset_not_album_type_selection(self):
        good = manual_request()
        self.assertIsNone(catalog.scope_reason(good))
        good["system"] = {"name": "Вектор-1", "bracket_step": 999}
        self.assertIsNone(catalog.scope_reason(good))
        for change in ({"system": "Standart"}, {"system": {"_name": "Вектор-1"}},
                       {"sub_type": "ortho"}, {"cladding": "composite"}, {"parts": "clamps"}):
            req = dict(good, **change)
            self.assertTrue(catalog.scope_reason(req))


class ConnectionPassportTests(unittest.TestCase):
    def test_regular_inventory_has_real_spans_and_no_static_approval(self):
        source = geometry()
        before = copy.deepcopy(source)
        result = connections.build_connection_passport(manual_request(), source)
        self.assertEqual(source, before)
        self.assertEqual(result["status"], "inventory_only")
        member = result["members"][0]
        self.assertEqual(member["support_count"], 3)
        self.assertEqual(member["span_count"], 2)
        self.assertEqual(member["intervals_mm"], [1500, 1500])
        self.assertEqual(member["supports"], [{"offset_mm": 0, "bracket_indices": [0]},
            {"offset_mm": 1500, "bracket_indices": [1]}, {"offset_mm": 3000, "bracket_indices": [2]}])
        self.assertEqual(result["coverage"]["fixed_sliding"], "not_modeled")
        self.assertEqual(result["coverage"]["strength"], "not_verified")
        self.assertNotIn("geometry", member)
        self.assertNotIn("rails", result)
        self.assertNotIn("brackets", result)
        self.assertNotIn("manufacturer_compliance", result)

    def test_zero_one_duplicate_and_tolerance_supports(self):
        for ys in ((), (1500,), (-0.5, 1500, 1500, 3000.5)):
            with self.subTest(ys=ys):
                result = connections.build_connection_passport(manual_request(), geometry(ys))
                member = result["members"][0]
                self.assertEqual(member["support_count"], len(set(ys)))
                self.assertEqual(member["span_count"], max(0, len(set(ys)) - 1))
                self.assertEqual(result["summary"]["support_links"], len(ys))
                self.assertEqual(result["coverage"]["strength"], "not_verified")
                if not ys:
                    self.assertEqual((member["bottom_free_mm"], member["top_free_mm"]), (3000, 3000))
                if len(ys) == 4:
                    self.assertEqual(member["supports"][1]["bracket_indices"], [1, 2])
                    self.assertEqual(member["supports"][0]["offset_mm"], -0.5)
                    self.assertEqual((member["bottom_free_mm"], member["top_free_mm"]), (0, 0))

    def test_same_axis_pairs_are_only_adjacency_gaps_overlap_and_zero(self):
        source = geometry()
        source["rails"] = [dict(source["rails"][0], y0=a, y1=b) for a, b in ((0, 1000), (1000, 1500), (1400, 1700), (4000, 5000))]
        source["brackets"] = []
        result = connections.build_connection_passport(manual_request(), source)
        self.assertEqual([item["gap_mm"] for item in result["joints"]], [0, -100, 2300])
        self.assertEqual({item["status"] for item in result["joints"]}, {"geometric_adjacency_only"})
        self.assertEqual(result["coverage"]["splice_continuity"], "not_modeled")

    def test_incomplete_marks_and_foreign_schemes_remain_unknown(self):
        source = geometry()
        source["rails"][0]["profile"] = "ГП-40-40"
        result = connections.build_connection_passport(manual_request(), source)
        self.assertEqual(result["summary"]["unresolved_profile_references"], 1)
        self.assertEqual(result["members"][0]["profile_match"]["reference_ids"], [])
        for change in ({"system": "Standart"}, {"sub_type": "ortho"}, {"parts": "clamps"}):
            result = connections.build_connection_passport(dict(manual_request(), **change), source)
            self.assertEqual(result["status"], "unavailable")
            self.assertTrue(result["reason"])
            self.assertEqual(result["members"], [])

    def test_prepared_static_snapshot_reused_and_identity_checked(self):
        source = geometry()
        prepared, _ = topology.geometric_members("vertical", source["rails"], [], source["brackets"])
        expected = connections.build_connection_passport(manual_request(), source)
        with patch.object(topology, "geometric_members", side_effect=AssertionError("reuse prepared snapshot")):
            self.assertEqual(connections.build_connection_passport(manual_request(), source, prepared), expected)
        for field, value in (("index", 1), ("geometry", {"x": 999, "y0": 0, "y1": 3000}), ("support_count", 2)):
            changed = copy.deepcopy(prepared)
            changed[0][field] = value
            self.assertEqual(connections.build_connection_passport(manual_request(), source, changed)["status"], "unavailable")
        changed = copy.deepcopy(prepared)
        changed[0]["supports"][1]["sources"][0]["index"] = 0
        self.assertEqual(connections.build_connection_passport(manual_request(), source, changed)["status"], "unavailable")

    def test_nonfinite_and_degenerate_geometry_never_yields_passport(self):
        for field, value in (("x", float("nan")), ("y1", 0), ("y0", True), ("y1", float("inf"))):
            source = geometry()
            source["rails"][0][field] = value
            self.assertEqual(connections.build_connection_passport(manual_request(), source)["status"], "unavailable")

    def test_merge_maps_final_global_indices_and_never_joins_zones(self):
        req, source = manual_request(), geometry()
        local = connections.build_connection_passport(req, source)
        merged = connections.merge_connection_passports(req, [("A", local, 0, 0), ("B", local, 1, 3)])
        self.assertEqual([m["rail_index"] for m in merged["members"]], [0, 1])
        self.assertEqual([m["zone_id"] for m in merged["members"]], ["A", "B"])
        self.assertEqual(merged["members"][1]["supports"][0]["bracket_indices"], [3])
        self.assertEqual(merged["joints"], [])
        self.assertEqual(merged["summary"]["matched_profile_references"], 2)
        self.assertEqual(len(merged["sources"]), 1)
        self.assertEqual(local["members"][0]["rail_index"], 0)


class ConnectionEngineTests(unittest.TestCase):
    def test_manual_and_calculation_modes_have_compact_passport(self):
        for req in (manual_request(), request()):
            result = engine.op_frame(req)
            self.assertTrue(result["ok"], result.get("error"))
            passport = result["connection_passport"]
            self.assertEqual(passport["status"], "inventory_only")
            self.assertEqual(passport["summary"]["members"], len(result["rails"]))
            self.assertEqual(passport["coverage"]["fixed_sliding"], "not_modeled")
            if req.get("calc"):
                self.assertEqual(result["calc_report"]["static_model"]["status"], "not_verified")
            else:
                self.assertNotIn("calc_report", result)

    def test_multiple_zones_reference_final_arrays(self):
        req = manual_request()
        req["contours"].append(dict(id="B", pts=[[2000, 0], [3200, 0], [3200, 3000], [2000, 3000]], joints_x=[2300, 2900]))
        result = engine.op_frame(req)
        self.assertTrue(result["ok"], result.get("error"))
        passport = result["connection_passport"]
        self.assertEqual(len({m["zone_id"] for m in passport["members"]}), 2)
        for member in passport["members"]:
            rail = result["rails"][member["rail_index"]]
            self.assertEqual(rail["zone"], member["zone_id"])
            for support in member["supports"]:
                for index in support["bracket_indices"]:
                    bracket = result["brackets"][index]
                    self.assertEqual(bracket["zone"], member["zone_id"])
                    self.assertAlmostEqual(bracket["y"] - rail["y0"], support["offset_mm"], places=3)

    def test_legacy_static_refusals_and_clamps_scope_unchanged(self):
        result = engine.op_frame(request("interfloor", 3000))
        self.assertFalse(result["ok"])
        self.assertNotIn("connection_passport", result)
        result = engine.op_frame(request(height=140))
        self.assertTrue(result["ok"], result.get("error"))
        self.assertEqual(result["calculation_status"], "partial")
        self.assertTrue(result["connection_passport"]["members"])
        self.assertEqual({i["reason"] for i in result["local_issues"]}, {"insufficient_supports"})
        self.assertEqual({i["status"] for i in result["local_issues"]}, {"not_verified"})
        req = manual_request()
        req["parts"] = "clamps"
        result = engine.op_frame(req)
        self.assertTrue(result["ok"])
        self.assertEqual(result["connection_passport"]["status"], "unavailable")
        self.assertEqual(result["connection_passport"]["members"], [])

    def test_serialization_does_not_multiply_geometry_or_catalogue_by_member(self):
        req = manual_request()
        source = geometry()
        source["rails"] = [dict(source["rails"][0], x=1000 * i) for i in range(1000)]
        source["brackets"] = [{"x": 1000 * i, "y": y, "kind": "рядовой"} for i in range(1000) for y in (0, 1500, 3000)]
        diagnostics = {}
        result = connections.build_connection_passport(req, source, diagnostics=diagnostics)
        payload = json.dumps(result, ensure_ascii=False)
        self.assertEqual(result["summary"]["members"], 1000)
        self.assertEqual(payload.count('"sha256"'), 1)
        self.assertNotIn('"geometry"', payload)
        self.assertLess(len(payload), 600000)


if __name__ == "__main__":
    unittest.main()
