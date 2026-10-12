#!/usr/bin/env python3
"""Protocol regression tests only; these do not execute or emulate AutoCAD.

Synthetic reports intentionally claim PASS while individual evidence is broken.
The client must reject them independently before reporting a successful cloud gate.
"""
import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import Mock, patch

MODULE = Path(__file__).with_name("aps_client.py")
spec = importlib.util.spec_from_file_location("aps_client", MODULE)
aps = importlib.util.module_from_spec(spec)
spec.loader.exec_module(aps)

CONFIG_SHA = "a" * 64
DLL_SHA = "b" * 64
INPUT_SHA = "c" * 64
OUTPUT_SHA = "d" * 64
REPORT_SHA = "e" * 64


def fixture():
    config = {
        "schema_version": 1, "run_id": "dfa877d3-f35d-491c-96d5-510160f7c5fa",
        "fixture_id": "synthetic-protocol-only", "input_sha256": INPUT_SHA,
        "source_handle": "375", "width_property": "ширина", "height_property": "высота",
        "outline_layer": "0", "tolerance": 1e-6,
        "cases": [
            {"id": "integer", "mode": "sample", "order": "production", "width": 564.0,
             "height": 588.0, "x": 100.0, "y": 200.0},
            {"id": "fractional", "mode": "definition", "order": "height_width", "width": 563.55,
             "height": 588.25, "x": -1000.0, "y": 500.0},
        ],
    }
    cases = []
    for i, c in enumerate(config["cases"]):
        x, y, w, h = (c[k] for k in ("x", "y", "width", "height"))
        cases.append({
            "id": c["id"], "requested": copy.deepcopy(c), "status": "PASS",
            "created_handle": str(400 + i), "actual_width": w, "actual_height": h,
            "actual_properties": [{"name": "ширина", "value": w}, {"name": "высота", "value": h}],
            "native_outline_vertices": [[x, y, 0.0], [x+w, y, 0.0], [x+w, y+h, 0.0], [x, y+h, 0.0]],
            "source_preserved": True, "membership_preserved": True, "persisted": True,
            "observed_errors": [], "observed_signature": (str(i + 1) * 64),
            "position": [x, y, 0.0], "rotation": 0.0, "scale": [1.0, 1.0, 1.0],
        })
    report = {
        "schema_version": 1, "phase": "probe", "run_id": config["run_id"],
        "fixture_id": config["fixture_id"], "configuration_sha256": CONFIG_SHA,
        "harness_sha256": DLL_SHA, "input_sha256": INPUT_SHA, "output_sha256": OUTPUT_SHA,
        "source_handle": config["source_handle"], "status": "PASS", "execution_status": "COMPLETED",
        "native_execution": True, "full_native_gate": "NOT_RUN", "source_unchanged": True,
        "source_preserved": True, "input_file_unchanged": True,
        "host": {"process": "accoreconsole", "acadver": "24.3s (LMS Tech)",
                 "managed_api": "AcDbMgd, Version=24.3.0.0", "managed_api_sha256": "f" * 64,
                 "executable_version": "24.3.0.0"},
        "cases": cases, "created_handles": [c["created_handle"] for c in cases],
        "original_modelspace_handles": ["375"], "source_fingerprint_before": "0" * 64,
    }
    return config, report


class ReportEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.config, self.report = fixture()

    def validate(self, report=None):
        return aps.validate_probe_report(self.report if report is None else report, self.config,
            CONFIG_SHA, DLL_SHA, INPUT_SHA, OUTPUT_SHA)

    def rejected(self, report):
        try:
            status = self.validate(report)
        except aps.ProbeError:
            return
        self.assertNotEqual(status, "PASS")

    def test_consistent_report_passes_only_its_narrow_gate(self):
        self.assertEqual(self.validate(), "PASS")
        self.assertEqual(self.report["full_native_gate"], "NOT_RUN")

    def test_stale_or_cross_run_report_is_rejected(self):
        for field, value in (("run_id", "other-run"), ("fixture_id", "other-dwg"),
            ("configuration_sha256", "0"*64), ("harness_sha256", "0"*64),
            ("input_sha256", "0"*64), ("output_sha256", "0"*64), ("source_handle", "ABC")):
            with self.subTest(field=field):
                report = copy.deepcopy(self.report)
                report[field] = value
                self.rejected(report)

    def test_missing_duplicate_or_unrequested_cases_are_rejected(self):
        for change in (lambda r: r["cases"].pop(),
                       lambda r: r["cases"].append(copy.deepcopy(r["cases"][0])),
                       lambda r: r["cases"][1].update(id=r["cases"][0]["id"]),
                       lambda r: r["cases"][1].update(id="unrequested")):
            report = copy.deepcopy(self.report)
            change(report)
            self.rejected(report)

    def test_false_execution_or_integrity_claim_is_rejected(self):
        for field, value in (("status", "FAIL"), ("status", "NOT_RUN"),
            ("native_execution", False), ("execution_status", "FAILED"), ("phase", "reopen"),
            ("source_preserved", False), ("source_unchanged", False), ("input_file_unchanged", False)):
            with self.subTest(field=field, value=value):
                report = copy.deepcopy(self.report)
                report[field] = value
                self.rejected(report)

    def test_non_native_and_wrong_version_hosts_are_rejected(self):
        for host in ({}, {"process": "python", "acadver": "24.3"},
                     {"process": "acad", "acadver": "24.3"},
                     {"process": "accoreconsole", "acadver": "25.0"}):
            with self.subTest(host=host):
                report = copy.deepcopy(self.report)
                report["host"] = host
                self.rejected(report)

    def test_cases_require_unique_persisted_handles_and_exact_requests(self):
        for mutate in (lambda c: c.pop("created_handle"), lambda c: c.update(created_handle=""),
                       lambda c: c.update(created_handle="400"), lambda c: c.update(persisted=False),
                       lambda c: c["requested"].update(width=564),
                       lambda c: c.update(source_preserved=False),
                       lambda c: c.update(membership_preserved=False)):
            report = copy.deepcopy(self.report)
            mutate(report["cases"][1])
            self.rejected(report)

    def test_rounded_and_nonfinite_readback_is_rejected_despite_pass_label(self):
        for field, value in (("actual_width", 564.0), ("actual_height", 588.0),
            ("actual_width", float("nan")), ("actual_height", float("inf")),
            ("actual_width", True), ("actual_height", "588.25")):
            with self.subTest(field=field, value=value):
                report = copy.deepcopy(self.report)
                report["cases"][1][field] = value
                self.rejected(report)

    def test_missing_shifted_rounded_crossed_or_nonfinite_geometry_is_rejected(self):
        for variant in ("missing", "shifted", "rounded", "crossed", "z", "duplicate", "nan"):
            with self.subTest(variant=variant):
                report = copy.deepcopy(self.report)
                c = report["cases"][1]
                points = c["native_outline_vertices"]
                if variant == "missing":
                    c.pop("native_outline_vertices")
                elif variant == "shifted":
                    for p in points:
                        p[0] += 0.5
                elif variant == "rounded":
                    points[1][0] += 0.45
                    points[2][0] += 0.45
                elif variant == "crossed":
                    points[1], points[2] = points[2], points[1]
                elif variant == "z":
                    for p in points:
                        p[2] = 0.01
                elif variant == "duplicate":
                    points[3] = copy.deepcopy(points[0])
                else:
                    points[0][1] = float("nan")
                self.rejected(report)

    def test_cyclic_or_reversed_outline_order_is_valid(self):
        for points in (self.report["cases"][0]["native_outline_vertices"][1:] +
                       self.report["cases"][0]["native_outline_vertices"][:1],
                       list(reversed(self.report["cases"][0]["native_outline_vertices"]))):
            report = copy.deepcopy(self.report)
            report["cases"][0]["native_outline_vertices"] = points
            self.assertEqual(self.validate(report), "PASS")

    def test_reopen_binds_saved_dwg_first_report_and_handles(self):
        reopened = copy.deepcopy(self.report)
        reopened.update(phase="reopen", input_sha256=OUTPUT_SHA,
                        expected_report_sha256=REPORT_SHA, original_probe_status="PASS")
        for case in reopened["cases"]:
            case["original_case_status"] = "PASS"
        def check(report, first=None):
            return aps.validate_reopen_report(report, self.report if first is None else first,
                self.config, CONFIG_SHA, DLL_SHA, OUTPUT_SHA, REPORT_SHA)
        self.assertEqual(check(reopened), "PASS")
        for field in ("expected_report_sha256", "input_sha256"):
            bad = copy.deepcopy(reopened)
            bad[field] = "0" * 64
            with self.assertRaises(aps.ProbeError):
                check(bad)
        bad = copy.deepcopy(reopened)
        bad["cases"][1]["created_handle"] = "999"
        self.assertNotEqual(check(bad), "PASS")
        failed_first = copy.deepcopy(self.report)
        failed_first["status"] = "FAIL"
        self.assertNotEqual(check(reopened, failed_first), "PASS")

    def test_reopen_accepts_only_bounded_numeric_noise_and_harmless_order_changes(self):
        reopened = copy.deepcopy(self.report)
        reopened.update(phase="reopen", input_sha256=OUTPUT_SHA,
                        expected_report_sha256=REPORT_SHA, original_probe_status="PASS")
        changed = reopened["cases"][1]
        changed["actual_width"] += 1e-10
        changed["actual_properties"][0]["value"] += 1e-10
        changed["actual_properties"].reverse()
        changed["native_outline_vertices"].reverse()
        changed["observed_signature"] = "9" * 64  # Raw JSON hash may change legitimately.
        def check(report, before=None):
            return aps.validate_reopen_report(report, self.report if before is None else before,
                self.config, CONFIG_SHA, DLL_SHA, OUTPUT_SHA, REPORT_SHA)
        self.assertEqual(check(reopened), "PASS")
        before = copy.deepcopy(self.report)
        before["cases"][1]["actual_properties"].append({"name": "Видимость", "value": "A"})
        changed["actual_properties"].append({"name": "Видимость", "value": "B"})
        self.assertNotEqual(check(reopened, before), "PASS")
        changed["actual_properties"][-1]["value"] = "A"
        changed["position"][0] += 0.001
        self.assertNotEqual(check(reopened, before), "PASS")


class TransportContractTests(unittest.TestCase):
    def test_case_count_is_explicit_and_bounded(self):
        config, _ = fixture()
        data = b"synthetic config protocol fixture"
        config["input_sha256"] = aps.sha256(data)
        case = copy.deepcopy(config["cases"][0])
        for count, expected in ((0, False), (1000, True), (1001, False)):
            with self.subTest(count=count):
                config["cases"] = [dict(case, id="case-%d" % i) for i in range(count)]
                if expected:
                    aps.validate_config(config, data)
                else:
                    with self.assertRaises(aps.ProbeError):
                        aps.validate_config(config, data)

    def test_engine_pagination_uses_page_parameter_and_reads_later_engine(self):
        client = aps.APSClient()
        calls = []
        def api(method, path):
            calls.append((method, path))
            if path == "/engines":
                return {"data": ["Autodesk.Other+1"], "paginationToken": "token+/="}
            if path == "/engines?page=token%2B%2F%3D":
                return {"data": [aps.ENGINE], "paginationToken": None}
            if path == "/forgeapps/me":
                return "SyntheticOwner"
            raise AssertionError("Unexpected API query: " + path)
        client.api = api
        self.assertTrue(client.preflight(aps.ENGINE)["engine_available"])
        self.assertEqual(len(calls), 3)

    def test_terminal_failures_are_returned_without_becoming_success(self):
        for status in sorted(aps.TERMINAL):
            with self.subTest(status=status):
                client = aps.APSClient()
                client.api = lambda *_: {"id": "job1", "status": status}
                self.assertEqual(client.poll("job1")["status"], status)

    def test_unknown_or_missing_terminal_status_raises(self):
        for reply in (None, {}, {"status": "completed"}, {"status": "SUCCESS"}):
            client = aps.APSClient()
            client.api = lambda *_, response=reply: response
            with self.assertRaises(aps.ProbeError):
                client.poll("job1")

    def test_timeout_preserves_uncertainty_about_remote_execution(self):
        client = aps.APSClient()
        client.api = lambda *_: {"id": "job1", "status": "inprogress"}
        now = [0.0]
        def sleep(seconds):
            now[0] += seconds
        result = client.poll("job1", timeout=3, interval=2, clock=lambda: now[0], sleep=sleep)
        self.assertEqual(result["status"], "CLIENT_TIMEOUT")
        self.assertIs(result["remote_job_may_still_run"], True)
        self.assertEqual(now[0], 3)

    def test_credentials_and_signed_urls_are_removed_from_diagnostics(self):
        with patch.dict(os.environ, {"APS_CLIENT_ID": "MYCLIENTID", "APS_CLIENT_SECRET": "SUPERSECRET"}):
            text = aps.safe_text('MYCLIENTID SUPERSECRET Bearer abcdef+123== https://storage.example/path?X-Amz-Signature=abcd')
        for sensitive in ("MYCLIENTID", "SUPERSECRET", "abcdef", "storage.example", "X-Amz-Signature"):
            self.assertNotIn(sensitive, text)

    def test_unsafe_url_schemes_and_embedded_credentials_are_rejected(self):
        for url in ("http://example.test", "file:///tmp/a", "https://user:password@example.test/a",
                    "https://example.test/a#fragment", "https://example.test/a\nHeader: value"):
            with self.subTest(url=url), self.assertRaises(aps.ProbeError):
                aps.validate_https_url(url)
        self.assertEqual(aps.validate_https_url("https://example.test/a?signature=abc"),
                         "https://example.test/a?signature=abc")

    def test_offline_command_has_no_network_or_credential_requirement(self):
        with patch.object(aps, "APSClient", side_effect=AssertionError("offline path instantiated network client")):
            with contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(aps.main(["preflight"]), 0)
        receipt = json.loads(output.getvalue())
        self.assertEqual(receipt["status"], "NOT_RUN")
        self.assertEqual(receipt["network_requests"], 0)


class PipelineGateTests(unittest.TestCase):
    """Exercise the actual orchestration with fake cloud I/O, not CAD doubles."""
    def run_fake_pipeline(self, folder, failed_probe=False, missing_output=False):
        folder = Path(folder)
        config, prototype = fixture()
        input_data = b"Synthetic protocol fixture, not a valid DWG"
        saved_data = b"Synthetic saved artifact, not a valid DWG"
        config["input_sha256"] = aps.sha256(input_data)
        input_path = folder / "source.dwg"
        input_path.write_bytes(input_data)
        config_path = folder / "config.json"
        config_path.write_text(json.dumps(config), encoding="utf-8")
        configuration_sha = aps.sha256(config_path.read_bytes())
        deployment = {"engine": aps.ENGINE, "harness_sha256": DLL_SHA, "bundle_sha256": "9"*64,
                      "activities": {"probe": {"id": "fixture.probe+r", "version": 1},
                                     "reopen": {"id": "fixture.reopen+r", "version": 1}}}
        deployment_path = folder / "deployment.json"
        deployment_path.write_text(json.dumps(deployment), encoding="utf-8")
        args = SimpleNamespace(input=input_path, config=config_path, deployment=deployment_path,
            output_dir=folder / "result", bucket="synthetic-private-qa", create_bucket=False,
            timeout=3, processing_limit=3, reopen=True)
        first_report_data = [None]
        executed = []
        def stage(client, oss, activity, inputs, output_names, prefix, output_dir, timeout, processing_limit):
            reopen = "expected" in inputs
            executed.append("reopen" if reopen else "probe")
            output_dir.mkdir(parents=True)
            report = copy.deepcopy(prototype)
            report.update(configuration_sha256=configuration_sha,
                input_sha256=aps.sha256(saved_data if reopen else input_data),
                output_sha256=aps.sha256(saved_data))
            if failed_probe:
                report["status"] = "FAIL"
                report["cases"][1].update(status="FAIL", actual_width=564.0)
            if reopen:
                report.update(phase="reopen", expected_report_sha256=aps.sha256(first_report_data[0]),
                              original_probe_status="FAIL" if failed_probe else "PASS")
                for case in report["cases"]:
                    case["original_case_status"] = case["status"]
            data = json.dumps(report).encode()
            if not reopen:
                first_report_data[0] = data
            result_file = output_dir / output_names["result"]
            result_file.write_bytes(data)
            outputs = {"result": {"path": str(result_file), "sha256": aps.sha256(data), "oss_key": "report"}}
            if not reopen and not missing_output:
                output_file = output_dir / output_names["output"]
                output_file.write_bytes(saved_data)
                outputs["output"] = {"path": str(output_file), "sha256": aps.sha256(saved_data), "oss_key": "output"}
            return {"status": "success", "output_errors": [], "outputs": outputs}
        fake_oss = Mock()
        fake_oss.upload.return_value = "https://storage.example/input"
        fake_oss.download_url.return_value = "https://storage.example/result"
        with patch.object(aps, "verify_deployment"), patch.object(aps, "OSS", return_value=fake_oss), \
                patch.object(aps, "execute_stage", side_effect=stage):
            result = aps.run_pipeline(Mock(), args)
        return result, executed

    def test_probe_and_reopen_must_both_satisfy_assertions(self):
        with tempfile.TemporaryDirectory() as folder:
            result, executed = self.run_fake_pipeline(folder)
        self.assertEqual(executed, ["probe", "reopen"])
        self.assertEqual(result["status"], "PASS_CORE_PROBE_AND_REOPEN")
        self.assertEqual(result["desktop_acceptance"], "NOT_RUN")

    def test_successful_workitem_with_failed_assertion_still_reopens_but_cannot_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            result, executed = self.run_fake_pipeline(folder, failed_probe=True)
        self.assertEqual(executed, ["probe", "reopen"])
        self.assertEqual(result["probe"]["status"], "success")
        self.assertEqual(result["probe_assertions"], "FAIL")
        self.assertEqual(result["status"], "FAIL")

    def test_missing_dwg_artifact_cannot_pass_or_launch_reopen(self):
        with tempfile.TemporaryDirectory() as folder:
            result, executed = self.run_fake_pipeline(folder, missing_output=True)
        self.assertEqual(executed, ["probe"])
        self.assertEqual(result["status"], "FAIL")


if __name__ == "__main__":
    unittest.main(verbosity=2)
