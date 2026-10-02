"""Полезность ATFRAME: пять обычных фасадов должны давать геометрию.

Явные аналоги замечаний ревизора 02.10, не его исходный DWG: точных
координат в отзыве нет. Размеры в мм. Ширина всех фасадов 18000; окна
1500×1800, кроме трёх ленточных 15600×1800. Высоты и координаты ниже.
Параметры получаем из настоящего FrameSettings, а не копируем defaults.
Локальные замечания проверяются отдельно от успешности построения.
AutoCAD, пригодность монтажа и принятие расчёта инженером здесь не проверяются.

Windows CI: ROLES_DUMP_EXE=<путь к RolesDump.exe> python <этот файл>.
Linux: mcs/mono должны быть в PATH. Нехватка среды — ошибка, не PASS/skip.
"""
import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from frame_engine import op_frame

ROOT = Path(__file__).resolve().parents[2]
COLS = (1500, 4500, 7500, 10500, 13500)


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def typical_facades():
    regular = [(x, y, x + 1500, y + 1800)
               for x in COLS for y in (1200, 4200, 7200)]
    return {
        "ordinary": (10200, regular),                 # над верхним окном 1200
        "top_300": (9300, regular),                   # над верхним окном 300
        "ribbon_600": (10200, [(1200, y, 16800, y + 1800)
                                for y in (1200, 3600, 6000)]),
        "sill_500": (10200, [(x, y, x + 1500, y + 1800)
                              for x in COLS for y in (500, 3500, 6500)]),
        "french_300": (10200, [(x, y, x + 1500, y + 1800)
                                for x in COLS for y in (300, 3300, 6300)]),
    }


def native_settings():
    """Тот же запрос окна, что EngineParams в ATFRAME; никаких заглушек."""
    with tempfile.TemporaryDirectory(prefix="aframe-typical-") as directory:
        temp = Path(directory)
        supplied = os.environ.get("ROLES_DUMP_EXE")
        if supplied:
            executable = Path(supplied).resolve()
        else:
            executable = temp / "RolesDump.exe"
            files = ["AFrame/tools/roles/RolesDump.cs"] + [
                "AFrame/src/AFramePlugin/" + name + ".cs" for name in
                ("FrameSettings", "FrameSolutionSelection", "FrameProjectParameters")]
            subprocess.run(["mcs", "-nologo", "-r:System.Web.Extensions.dll",
                            "-out:" + str(executable)] + [str(ROOT / f) for f in files], check=True)
        roles = [dict(name=mode, has_layout=True, floors_picked=0,
                      actions=[["Mode", "frame"], ["Steps", mode]])
                 for mode in ("manual", "calc")]
        source, output = temp / "roles.json", temp / "params.json"
        source.write_text(json.dumps(roles), encoding="utf-8")
        runner = ([] if os.name == "nt" else ["mono"]) + [str(executable)]
        subprocess.run(runner + [str(source), str(output)], check=True)
        return {item["name"]: item for item in json.loads(output.read_text(encoding="utf-8-sig"))}


def request(params, name, dx=0):
    height, windows = typical_facades()[name]
    result = copy.deepcopy(params)
    result.update(op="frame", corners_x=[0, 18000],
        joints_x=list(range(608, 18000, 608)),
        contours=[dict(id=name, pts=rect(0, 0, 18000, height))] + [
            dict(id="w%d" % index, pts=rect(x0 + dx, y0, x1 + dx, y1))
            for index, (x0, y0, x1, y1) in enumerate(windows)])
    return result


def support_positions(result, rail):
    """Независимая привязка по выданным координатам, без индекса движка."""
    return sorted({b["y"] for b in result["brackets"]
                   if b.get("zone") == rail.get("zone")
                   and abs(b["x"] - rail["x"]) <= 0.5
                   and rail["y0"] - 0.5 <= b["y"] <= rail["y1"] + 0.5})


class TypicalFacadeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.settings = native_settings()

    def check_layout(self, result, mode):
        # Refusal, empty rails, or an empty diagnostic object are NOT success.
        self.assertTrue(result.get("ok"), result.get("error"))
        self.assertGreater(len(result["rails"]), 0)
        self.assertGreater(len(result["brackets"]), 0)
        self.assertEqual(result["summary"]["rails"], len(result["rails"]))
        self.assertEqual(result["system_used"]["bracket_start_offset"], 300)
        issues = result["local_issues"]
        by_member = {issue["member_index"]: issue for issue in issues}
        self.assertEqual(len(by_member), len(issues), "one marker per physical rail")
        single = set()
        for index, rail in enumerate(result["rails"]):
            positions = support_positions(result, rail)
            self.assertTrue(positions, rail)
            length = rail["y1"] - rail["y0"]
            if length <= 600:
                single.add(index)
                self.assertEqual(positions, [(rail["y0"] + rail["y1"]) / 2])
            else:
                self.assertGreaterEqual(len(positions), 2, rail)
                self.assertAlmostEqual(positions[0] - rail["y0"], 300, places=3)
                self.assertAlmostEqual(rail["y1"] - positions[-1], 300, places=3)
            if index not in by_member:
                self.assertNotIn("issue_index", rail)
                self.assertNotIn(rail.get("check_status"), ("not_verified", "failed"))
                continue
            issue = by_member[index]
            self.assertEqual(issue["kind"], "rail")
            self.assertEqual(issue["zone_id"], rail["zone"])
            self.assertEqual({k: issue[k] for k in ("x", "y0", "y1")},
                             {k: rail[k] for k in ("x", "y0", "y1")})
            self.assertEqual(issue["support_count"], len(positions))
            self.assertEqual(rail["check_status"], issue["status"])
            self.assertIs(issues[rail["issue_index"]], issue)
            self.assertTrue(issue["message"])
        self.assertEqual({i for i, issue in by_member.items()
                          if issue["reason"] == "insufficient_supports"}, single)
        for index in single:
            self.assertEqual(by_member[index]["status"], "not_verified")
            self.assertEqual(by_member[index]["failed_checks"], [])
        if mode == "manual":
            self.assertEqual(set(by_member), single)
            self.assertEqual(result["calculation_status"], "not_requested")
            self.assertNotIn("calc_report", result)
        else:
            expected = {}
            rail_offset = 0
            counts = {zone["zone_id"]: zone["rails"] for zone in result["per_zone"]}
            for record in result["calc_reports"]:
                model = record["report"]["static_model"]
                cases = model["member_calculation"]["cases"]
                for member in model["members"]:
                    if "calculation_case" not in member:
                        continue
                    case = cases[member["calculation_case"]]
                    failed = [check["name"] for check in case["chain"]["checks"] if not check["ok"]]
                    if failed:
                        expected[rail_offset + member["index"]] = failed
                if model["geometric_screening"]["reasons"]:
                    self.assertNotEqual(model["member_calculation"]["status"], "passed")
                rail_offset += counts[record["zone_id"]]
            self.assertEqual(set(by_member), single | set(expected))
            for index, failed in expected.items():
                self.assertEqual(by_member[index]["reason"], "member_capacity_exceeded")
                self.assertEqual(by_member[index]["status"], "failed")
                self.assertEqual(by_member[index]["failed_checks"], failed)
            self.assertEqual(result["calculation_status"], "partial" if issues else "passed")
        return single

    def run_facade(self, name):
        results = {}
        for mode in ("manual", "calc"):
            with self.subTest(mode=mode, facade=name):
                result = op_frame(request(self.settings[mode]["params"], name))
                single = self.check_layout(result, mode)
                self.assertEqual(bool(single), name != "ordinary")
                results[mode] = result
        # The calculation may add supports to an individual rail, never delete
        # inconvenient pieces or move their endpoints to hide a failed check.
        self.assertEqual([(r["x"], r["y0"], r["y1"]) for r in results["manual"]["rails"]],
                         [(r["x"], r["y0"], r["y1"]) for r in results["calc"]["rails"]])

    def test_native_dialog_defaults_are_the_actual_300_mm_contract(self):
        for mode, native in self.settings.items():
            self.assertIsNone(native["valid"])
            self.assertIsNone(native["apply_error"])
            self.assertTrue(native["roundtrip"])
            self.assertEqual(native["settings"]["start_off"], 300)
            self.assertEqual(native["params"]["sub_type"], "vertical")
            self.assertEqual(native["params"]["cladding"], "porcelain")
        self.assertEqual(self.settings["calc"]["params"]["calc"], dict(
            wind_region="II", terrain="B", height=30, q_clad=25, offset=230,
            na_max=3000, gamma_clad=1.1))

    def test_ordinary_windows(self):
        self.run_facade("ordinary")

    def test_windows_300_below_wall_top(self):
        self.run_facade("top_300")

    def test_ribbon_windows_600_mm_spandrels(self):
        self.run_facade("ribbon_600")

    def test_low_500_mm_sill(self):
        self.run_facade("sill_500")

    def test_french_windows_300_mm_sill(self):
        self.run_facade("french_300")

    def test_short_length_boundary_does_not_change_global_offset(self):
        for mode in ("manual", "calc"):
            for height in (599, 600, 601):
                with self.subTest(mode=mode, height=height):
                    req = copy.deepcopy(self.settings[mode]["params"])
                    req.update(op="frame", corners_x=[], joints_x=[600],
                               contours=[dict(id="boundary", pts=rect(0, 0, 1200, height))])
                    result = op_frame(req)
                    self.check_layout(result, mode)
                    self.assertEqual([rail["x"] for rail in result["rails"]], [100, 600, 1100])
                    self.assertEqual(len(result["brackets"]), 3 if height <= 600 else 6)

    def test_shifted_windows_and_repeat_are_deterministic(self):
        for mode in ("manual", "calc"):
            for dx in (-101, 101):
                with self.subTest(mode=mode, dx=dx):
                    req = request(self.settings[mode]["params"], "sill_500", dx)
                    saved = copy.deepcopy(req)
                    first = op_frame(req)
                    self.check_layout(first, mode)
                    self.assertEqual(first, op_frame(copy.deepcopy(saved)))
                    self.assertEqual(req, saved, "one run must not mutate a request reused by the UI")

    def test_multiple_zones_use_global_member_and_marker_indices(self):
        for mode in ("manual", "calc"):
            with self.subTest(mode=mode):
                req = copy.deepcopy(self.settings[mode]["params"])
                req.update(op="frame", corners_x=[], zones=[
                    dict(zone_id=name, joints_x=[x + 600], zone={
                        "schema": "facade_zone/1", "units": "mm",
                        "outer": {"pts": rect(x, 0, x + 1200, height)}, "openings": []})
                    for name, x, height in (("long", 0, 3000),
                                            ("short-a", 3000, 500), ("short-b", 6000, 300))])
                result = op_frame(req)
                self.check_layout(result, mode)
                self.assertEqual(len(result["rails"]), 9)
                self.assertEqual([issue["member_index"] for issue in result["local_issues"]],
                                 [3, 4, 5, 6, 7, 8])
                self.assertEqual([issue["zone_id"] for issue in result["local_issues"]],
                                 ["short-a"] * 3 + ["short-b"] * 3)

    def test_missing_support_parameters_and_invalid_calc_stay_errors(self):
        for name, value in (("bracket_start_offset", None), ("bracket_step", None)):
            with self.subTest(parameter=name):
                req = request(self.settings["manual"]["params"], "ordinary")
                req["system"].update({name: value, "bracket_step_corner": None})
                result = op_frame(req)
                self.assertFalse(result["ok"])
                self.assertFalse(result.get("rails"))
        req = request(self.settings["calc"]["params"], "ordinary")
        req["calc"]["height"] = 0
        result = op_frame(req)
        self.assertFalse(result["ok"])
        self.assertFalse(result.get("rails"))

    def test_non_finite_or_negative_placement_settings_are_not_local_warnings(self):
        for key, values in (("bracket_step", (0, -1, float("nan"), float("inf"))),
                            ("bracket_start_offset", (-1, float("nan"), float("inf")))):
            for value in values:
                with self.subTest(parameter=key, value=value):
                    req = request(self.settings["manual"]["params"], "sill_500")
                    req["system"][key] = value
                    self.assertTrue(req["exact_step"])
                    result = op_frame(req)
                    self.assertFalse(result["ok"])
                    self.assertEqual(result["error_code"], "E_FRAME_INPUT")
                    self.assertFalse(result.get("rails"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
