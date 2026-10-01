# -*- coding: utf-8 -*-
"""Analytical controls for actual rail pieces, independent of facade counts."""
import unittest
from unittest.mock import patch

import frame_beam as beam
import frame_calc as calc
import frame_topology as topology


INPUTS = dict(scheme="vertical", wind_region="II", terrain="B", height=6,
    q_clad=25, gamma_clad=1.1, q_rails=0.745, offset=170,
    na_max=3000, bracket="КР2-70", extender="УК-70-1,2",
    profile="ГП-40-40-1,2", b_row=608, b_corner=608, rail_len=3000)


class BeamTests(unittest.TestCase):
    def test_simple_span_matches_closed_form(self):
        value = beam.response([1234])
        self.assertAlmostEqual(value["c_m"], 1 / 8)
        self.assertAlmostEqual(value["k_reaction"], 1 / 2)
        self.assertAlmostEqual(value["c_f"], 5 / 384)
        self.assertEqual(value["reactions"], [0.5, 0.5])

    def test_two_equal_spans_moment_reactions_and_true_deflection(self):
        value = beam.response([2000, 2000])
        self.assertEqual(value["support_moments"], [0, -1 / 8, 0])
        self.assertEqual(value["reactions"], [3 / 8, 5 / 4, 3 / 8])
        # Full integration gives 0.00541612; the historical rounded row is
        # 0.0052 and must not understate the actual full-uniform-load result.
        self.assertAlmostEqual(value["c_f"], 0.005416121605828729, places=13)

    def test_two_unequal_spans(self):
        value = beam.response([1000, 2000])
        self.assertAlmostEqual(value["support_moments"][1], -(0.5**3 + 1) / (8 * 1.5))
        self.assertEqual(value["reactions"], [0.0625, 1.03125, 0.40625])

    def test_unequal_overhangs_closed_form_and_equilibrium(self):
        value = beam.response([1000], 300, 600)
        self.assertAlmostEqual(value["reactions"][1], 1.9 * 1.3 / 2)
        self.assertAlmostEqual(value["reactions"][0], 1.9 - 1.9 * 1.3 / 2)
        self.assertEqual(value["support_moments"], [-0.045, -0.18])
        self.assertAlmostEqual(value["c_f"], 0.0317)
        self.assertAlmostEqual(value["deflection_length"], 600)

    def test_reverse_and_rescale_do_not_change_dimensionless_response(self):
        original = beam.response([180, 800, 1300, 90], 320, 600)
        changed = beam.response([900, 13000, 8000, 1800], 6000, 3200)
        for name in ("c_m", "k_reaction", "c_f", "c_f_local"):
            self.assertAlmostEqual(original[name], changed[name], places=12)
        for left, right in zip(original["reactions"], reversed(changed["reactions"])):
            self.assertAlmostEqual(left, right, places=12)

    def test_absolute_uplift_reaction_is_not_discarded(self):
        value = beam.response([1000], 0, 3000)
        self.assertLess(value["reactions"][0], 0)
        self.assertEqual(value["k_reaction"], max(map(abs, value["reactions"])))
        self.assertAlmostEqual(sum(value["reactions"]), 4)

    def test_invalid_beam_cannot_be_approved(self):
        for spans, a, b in (([], 0, 0), ([0], 0, 0), ([-1], 0, 0),
                             ([100], -1, 0), ([float("inf")], 0, 0),
                             ([100], 0, float("nan"))):
            with self.subTest(spans=spans, a=a, b=b):
                with self.assertRaises(ValueError):
                    beam.response(spans, a, b)

    def test_single_span_chain_preserves_dead_load_and_increases_deflection(self):
        ordinary = calc.calc_chain(INPUTS, 800, "row")
        actual = calc.calc_chain(INPUTS, 800, "row", beam.response([800]))
        self.assertEqual(actual["n_p"], ordinary["n_p"])
        self.assertLess(actual["n_w"], ordinary["n_w"])
        self.assertEqual(actual["member_coefficients"]["span_count"], 1)
        old_f = next(c for c in ordinary["checks"] if c["name"] == "профиль f, мм")
        new_f = next(c for c in actual["checks"] if c["name"] == "профиль f, мм")
        self.assertGreater(new_f["value"], old_f["value"])
        self.assertEqual(new_f["limit"], old_f["limit"])

    def test_real_two_span_chain_does_not_use_nominal_multi_row(self):
        actual = calc.calc_chain(INPUTS, 600, "row", beam.response([600, 600], 300, 300))
        self.assertEqual(actual["member_coefficients"]["k_reaction"], 1.25)
        self.assertEqual(actual["member_coefficients"]["c_m"], 0.125)

    def test_overhang_weight_strip_is_not_lost(self):
        value = beam.response([200], 300, 300)
        self.assertEqual(value["gravity_length"], 400)
        self.assertEqual(beam.response([1000], 1000, 0)["gravity_length"], 1500)
        ordinary = calc.calc_chain(INPUTS, 200, "row")
        actual = calc.calc_chain(INPUTS, 200, "row", value)
        self.assertAlmostEqual(actual["n_p"], ordinary["n_p"] * 2, delta=0.1)

    def test_member_response_cannot_open_interfloor(self):
        with self.assertRaises(ValueError):
            calc.calc_chain(dict(INPUTS, scheme="interfloor"), 800, "row", beam.response([800]))

    def test_repeated_geometry_is_calculated_once_and_never_merged(self):
        rails = [dict(x=i * 600, y0=0, y1=1400, kind="ГП-40-40") for i in range(30)]
        brackets = [dict(x=r["x"], y=y, kind="рядовой") for r in rails for y in (300, 1100)]
        with patch.object(beam, "response", wraps=beam.response) as compute:
            model = topology.screen_layout("vertical", rails, [], brackets, INPUTS)
        self.assertEqual(compute.call_count, 1)
        self.assertEqual(model["member_count"], 30)
        self.assertEqual(len(model["member_calculation"]["cases"]), 1)
        self.assertEqual(model["member_calculation"]["status"], "passed")
        self.assertFalse(model["pieces_merged"])
        self.assertEqual(model["status"], "not_verified")

    def test_actual_capacity_failure_has_piece_address_and_no_geometry(self):
        rails = [dict(x=0, y0=0, y1=3800, kind="ГП-40-40")]
        brackets = [dict(x=0, y=y, kind="рядовой") for y in (300, 3500)]
        model = topology.screen_layout("vertical", rails, [], brackets, INPUTS)
        result = topology.refusal(model)
        self.assertEqual(result["error_code"], "E_CALC_MEMBER_CAPACITY")
        self.assertEqual(result["unsupported"][0]["member_index"], 0)
        self.assertTrue(result["unsupported"][0]["failed_checks"])
        self.assertEqual(result["unsupported"][0]["geometry"], {"x": 0, "y0": 0, "y1": 3800})
        self.assertIn("X=0, Y=0…3800 мм; опор 2, пролётов 1", result["error"])
        failed = next(c for c in model["member_calculation"]["cases"][0]["chain"]["checks"] if not c["ok"])
        self.assertIn("%s: %g > %g" % (failed["name"], failed["value"], failed["limit"]), result["error"])
        self.assertNotIn("rails", result)

    def test_insufficient_support_error_identifies_the_piece(self):
        rails = [dict(x=125, y0=800, y1=1200, kind="ГП-40-40")]
        brackets = [dict(x=125, y=1000, kind="рядовой")]
        result = topology.refusal(topology.screen_layout("vertical", rails, [], brackets, INPUTS))
        self.assertEqual(result["unsupported"][0]["geometry"], {"x": 125, "y0": 800, "y1": 1200})
        self.assertIn("X=125, Y=800…1200 мм; опор 1, пролётов 0", result["error"])
        self.assertNotIn("rails", result)


if __name__ == "__main__":
    unittest.main()
