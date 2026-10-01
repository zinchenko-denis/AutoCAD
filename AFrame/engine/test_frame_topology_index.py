# -*- coding: utf-8 -*-
"""Indexed joins versus the frozen 0a397c2 brute-force implementation.

The oracle below deliberately retains the old arithmetic and source iteration;
none of the production index/helpers are used to derive expected answers.
"""
import copy
import random
import unittest

import frame_topology as ft


MATCH_TOL = 0.5  # Coordinate rounding only; not a structural allowance.


def _legacy_span_class(count):
    if count >= 4:
        return "multi"
    return str(count)


def _legacy_unique_supports(matches):
    """One geometric support position; preserve source members and role labels."""
    grouped = {}
    for coordinate, source, index, kind in matches:
        key = round(float(coordinate), 4)
        grouped.setdefault(key, []).append(dict(source=source, index=index, kind=kind))
    return [dict(position=position, sources=grouped[position],
                 connection="not_modeled") for position in sorted(grouped)]


def _legacy_member_geometry(index, rail, supports, tolerance):
    positions = [s["position"] for s in supports]
    intervals = [round(b - a, 4) for a, b in zip(positions, positions[1:])]
    length = float(rail["y1"]) - float(rail["y0"])
    return dict(index=index, kind=rail.get("kind"),
                geometry={key: rail[key] for key in ("x", "y0", "y1")},
                supports=supports, support_y=positions, support_count=len(positions),
                intervals=intervals, span_count=len(intervals),
                actual_span_class=_legacy_span_class(len(intervals)),
                bottom_free=round(max(0.0, positions[0] - rail["y0"]), 4) if positions else length,
                top_free=round(max(0.0, rail["y1"] - positions[-1]), 4) if positions else length,
                coordinate_match_tolerance=tolerance)


def _legacyscreen_layout(sub, rails, hrails, brackets, calc_inputs, member_zones=None,
                  rail_gap=0.0):
    """Frozen brute-force support matching; calculation policy is tested elsewhere."""
    zones = list(member_zones or ["row"] * len(rails))
    if len(zones) != len(rails) or any(z not in ("row", "corner") for z in zones):
        raise ValueError("member_zones must contain one row/corner entry per rail")
    members = []
    for index, rail in enumerate(rails):
        matches = []
        tolerance = MATCH_TOL
        if sub == "vertical":
            for bi, bracket in enumerate(brackets):
                if abs(bracket["x"] - rail["x"]) <= MATCH_TOL and \
                        rail["y0"] - MATCH_TOL <= bracket["y"] <= rail["y1"] + MATCH_TOL:
                    matches.append((bracket["y"], "brackets", bi, bracket.get("kind")))
        else:
            # Interfloor uses the pre-existing endpoint/splice matching rule.
            # This only records candidate intersections across that small gap.
            tolerance = float(rail_gap) / 2.0 + 1.0 if sub == "interfloor" else MATCH_TOL
            kinds = ("НГП", "СП-60-40") if sub == "interfloor" else ("ГП-40-40",)
            for hi, horizontal in enumerate(hrails):
                if horizontal.get("kind") in kinds and \
                        horizontal["x0"] - MATCH_TOL <= rail["x"] <= horizontal["x1"] + MATCH_TOL and \
                        rail["y0"] - tolerance <= horizontal["y"] <= rail["y1"] + tolerance:
                    matches.append((horizontal["y"], "hrails", hi, horizontal.get("kind")))
        member = _legacy_member_geometry(index, rail, _legacy_unique_supports(matches), tolerance)
        member["zone"] = zones[index]
        members.append(member)

    horizontal_members = []
    for hi, horizontal in enumerate(hrails):
        matches = [(b["x"], "brackets", bi, b.get("kind"))
                   for bi, b in enumerate(brackets)
                   if abs(b["y"] - horizontal["y"]) <= MATCH_TOL
                   and horizontal["x0"] - MATCH_TOL <= b["x"] <= horizontal["x1"] + MATCH_TOL]
        horizontal_members.append(dict(index=hi, kind=horizontal.get("kind"),
            geometry={key: horizontal[key] for key in ("y", "x0", "x1")},
            bracket_intersections=_legacy_unique_supports(matches), strength="not_verified"))

    return dict(members=members, horizontal_members=horizontal_members)


def rail(x=0.0, low=0.0, high=3000.0):
    return dict(x=x, y0=low, y1=high, kind="ШП-60-20")


def horizontal(y=1500.0, low=-100.0, high=100.0, kind="ГП-40-40"):
    return dict(y=y, x0=low, x1=high, kind=kind)


def bracket(x=0.0, y=1500.0, kind="рядовой"):
    return dict(x=x, y=y, kind=kind)


class IndexedTopologyTests(unittest.TestCase):
    def equivalent(self, sub, rails, hrails, brackets, zones=None, gap=0.0):
        inputs = (sub, rails, hrails, brackets,
                  dict(rail_len=3000.0, v_step=600.0), zones, gap)
        unchanged = copy.deepcopy(inputs)
        expected = _legacyscreen_layout(*inputs)
        actual = ft.screen_layout(*inputs)
        self.assertEqual(inputs, unchanged, "read-only geometry path mutated input")
        diagnostics = {}
        members, horizontal_members = ft.geometric_members(
            sub, rails, hrails, brackets, zones, gap, diagnostics)
        # The public geometry API does not add calculation coefficients.
        geometries = copy.deepcopy(expected["members"])
        for member in geometries:
            member.pop("coefficient_span", None)
            member.pop("coefficient_span_class", None)
        self.assertEqual((members, horizontal_members),
                         (geometries, expected["horizontal_members"]))
        self.assertEqual(set(diagnostics), {"point_queries", "segment_queries",
            "index_nodes_visited", "candidates_tested", "matches_emitted"})
        self.assertNotIn("diagnostics", actual)
        # This oracle checks the spatial index, not the retired refusal for
        # one/two-span pieces. Calculation has independent analytical tests.
        actual_geometry = copy.deepcopy(actual["members"])
        for member in actual_geometry:
            member.pop("coefficient_span", None)
            member.pop("coefficient_span_class", None)
        self.assertEqual(actual_geometry, geometries)
        self.assertEqual(actual["horizontal_members"], expected["horizontal_members"])
        return actual, diagnostics

    def test_abs_roundoff_does_not_lose_boundary_support(self):
        rx = -0.37568324388893126
        bx = 0.12431675611106875
        self.assertEqual(abs(bx - rx), MATCH_TOL)
        self.assertGreater(bx, rx + MATCH_TOL)
        result, _ = self.equivalent("vertical", [rail(rx)], [], [bracket(bx)])
        self.assertEqual(result["members"][0]["support_y"], [1500.0])
        # Same ABS arithmetic is used on Y for horizontal bracket intersections.
        result, _ = self.equivalent("ortho", [], [horizontal(rx)], [bracket(0.0, bx)])
        self.assertEqual(result["horizontal_members"][0]["bracket_intersections"][0]["position"], 0.0)

    def test_horizontal_endpoint_is_not_algebraically_inverted(self):
        x0 = -0.0003315766897080295
        rx = x0 - MATCH_TOL
        self.assertFalse(x0 <= rx + MATCH_TOL)
        result, _ = self.equivalent("ortho", [rail(rx)], [horizontal(low=x0)], [])
        self.assertEqual(result["members"][0]["support_y"], [1500.0])

    def test_duplicate_positions_keep_every_source_in_original_order(self):
        values = [10.000049, 20.0, 10.00004, 10.000049]
        brackets = [bracket(0.0, y, str(i)) for i, y in enumerate(values)]
        result, _ = self.equivalent("vertical", [rail(high=30.0)], [], brackets)
        supports = result["members"][0]["supports"]
        self.assertEqual([p["position"] for p in supports], [10.0, 20.0])
        self.assertEqual([s["index"] for s in supports[0]["sources"]], [0, 2, 3])
        self.assertEqual([s["kind"] for s in supports[0]["sources"]], ["0", "2", "3"])

    def test_interfloor_tolerance_and_kind_filter_are_unchanged(self):
        hrails = [horizontal(94.0, kind="НГП"), horizontal(206.0, kind="СП-60-40"),
                  horizontal(93.9999, kind="НГП"), horizontal(150.0)]
        result, _ = self.equivalent("interfloor", [rail(low=100.0, high=200.0)],
                                     hrails, [bracket(0.0, 150.0)], gap=10.0)
        self.assertEqual(result["members"][0]["support_y"], [94.0, 206.0])
        self.assertEqual(result["members"][0]["coordinate_match_tolerance"], 6.0)
        self.assertEqual(len(result["horizontal_members"]), 4)
        self.assertTrue(result["horizontal_members"][3]["bracket_intersections"])
        self.assertEqual(result["geometric_screening"]["status"], "refused")

    def test_negative_coordinates_tolerance_edges_and_piece_seams(self):
        rails = [rail(-0.25, -100.0, 0.0), rail(-0.25, 0.0, 100.0)]
        brackets = [bracket(x, y) for x in (-0.75, 0.25, -0.7500001, 0.2500001)
                    for y in (-100.5, -100.50001, 0.0, 100.5, 100.50001)]
        self.equivalent("vertical", rails, [], brackets, ["row", "corner"])
        hrails = [horizontal(y, -.75, .25) for y in (-100.5, 0.0, 100.5)]
        self.equivalent("ortho", rails, hrails, brackets, ["row", "corner"])

    def test_reverse_intervals_preserve_legacy_expansion(self):
        result, _ = self.equivalent("ortho", [rail()], [horizontal(low=.2, high=-.2)], [])
        self.assertEqual(result["members"][0]["support_y"], [1500.0])
        self.equivalent("ortho", [rail()], [horizontal(low=100.0, high=-100.0)], [])
        self.equivalent("vertical", [rail(low=2.0, high=0.0)], [], [bracket(0.0, 1.0)])

    def test_empty_inputs_and_empty_zones_keep_contract(self):
        for sub in ("vertical", "ortho", "interfloor"):
            self.equivalent(sub, [], [], [])
            self.equivalent(sub, [rail()], [], [], zones=[])
        for zones in (["corner", "row"], ["unknown"]):
            with self.assertRaises(ValueError):
                ft.geometric_members("vertical", [rail()], [], [], zones)

    def test_nonfinite_candidates_retain_exact_comparison_fallback(self):
        brackets = [bracket(float("nan")), bracket(float("inf")),
                    bracket(float("-inf")), bracket(0.0, float("nan")), bracket()]
        result, _ = self.equivalent("vertical", [rail()], [], brackets)
        self.assertEqual(result["members"][0]["support_y"], [1500.0])
        self.assertEqual(result["members"][0]["supports"][0]["sources"][0]["index"], 4)

    def test_source_permutations_and_randomized_complete_results(self):
        rng = random.Random(3972)
        values = [-600.5, -600.0, -.5, -.000049, 0.0, .000049, .5, 600.0, 600.5]
        for case in range(90):
            rails = [rail(rng.choice(values), rng.choice(values[:5]),
                          rng.choice(values[4:]) + 1200.0) for _ in range(rng.randrange(1, 12))]
            hrails = [horizontal(rng.choice(values), rng.choice(values[:5]),
                                  rng.choice(values[4:]), rng.choice(("НГП", "СП-60-40", "ГП-40-40", "other")))
                      for _ in range(rng.randrange(25))]
            brackets = [bracket(rng.choice(values), rng.choice(values), str(i % 3))
                        for i in range(rng.randrange(30))]
            if brackets:
                brackets.extend([brackets[0], dict(brackets[0])])
            rng.shuffle(rails)
            rng.shuffle(hrails)
            rng.shuffle(brackets)
            with self.subTest(case=case):
                self.equivalent(("vertical", "ortho", "interfloor")[case % 3],
                    rails, hrails, brackets, [rng.choice(("row", "corner")) for _ in rails],
                    rng.choice((0.0, 10.0)))

    def test_index_prunes_separate_axes_and_tall_same_axis_stacks(self):
        for tall in (False, True):
            for count in (100, 500, 1000):
                rails = [rail(0.0 if tall else i * 100.0,
                              i * 4000.0 if tall else 0.0,
                              i * 4000.0 + 3000.0 if tall else 3000.0)
                         for i in range(count)]
                brackets = [bracket(r["x"], r["y0"] + dy)
                            for r in rails for dy in (0.0, 1500.0, 3000.0)]
                with self.subTest(tall=tall, count=count):
                    _, diagnostics = self.equivalent("vertical", rails, [], brackets)
                    self.assertEqual(diagnostics["matches_emitted"], count * 3)
                    self.assertEqual(diagnostics["candidates_tested"], count * 3)
                    self.assertLess(diagnostics["index_nodes_visited"], count * 30)

    def test_segment_and_horizontal_point_joins_prune_large_inputs(self):
        for tall in (False, True):
            count = 1000
            rails = [rail(0.0 if tall else i * 100.0,
                          i * 4000.0 if tall else 0.0,
                          i * 4000.0 + 3000.0 if tall else 3000.0)
                     for i in range(count)]
            hrails = [horizontal(r["y0"] + dy, r["x"] - 10.0, r["x"] + 10.0)
                      for r in rails for dy in (0.0, 1500.0, 3000.0)]
            brackets = [bracket(h["x0"], h["y"]) for h in hrails]
            with self.subTest(tall=tall):
                _, diagnostics = self.equivalent("ortho", rails, hrails, brackets)
                self.assertEqual(diagnostics["matches_emitted"], count * 6)
                self.assertEqual(diagnostics["candidates_tested"], count * 6)
                self.assertLess(diagnostics["index_nodes_visited"], count * 120)


if __name__ == "__main__":
    unittest.main()
