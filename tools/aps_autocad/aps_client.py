#!/usr/bin/env python3
"""Bounded APS AutoCAD Core probe client. No desktop/ATTILE acceptance claim.

Only --execute permits network requests. Credentials come solely from
APS_CLIENT_ID / APS_CLIENT_SECRET. Signed storage URLs stay in memory.
See README.md for the intentionally narrow verification boundary.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

HOST = "https://developer.api.autodesk.com"
ENGINE = "Autodesk.AutoCAD+24_3"
MAX_FILE = 100 * 1024 * 1024
MAX_JSON = 5 * 1024 * 1024
PENDING = {"pending", "inprogress"}
TERMINAL = {"success", "cancelled", "failedLimitProcessingTime", "failedDownload",
            "failedInstructions", "failedUpload", "failedUploadOptional"}


class ProbeError(Exception):
    pass


class HTTPFailure(ProbeError):
    def __init__(self, status):
        self.status = status
        super().__init__("HTTP request failed (status %s; response/URL withheld)" % status)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def safe_text(value):
    """Remove credentials and complete URLs, including URLs in remote logs."""
    text = str(value).replace("\\/", "/")
    for name in ("APS_CLIENT_ID", "APS_CLIENT_SECRET"):
        secret = os.environ.get(name)
        if secret:
            text = text.replace(secret, "[REDACTED]")
    text = re.sub(r"https?://[^\s<>\"']+", "[URL REDACTED]", text, flags=re.I)
    text = re.sub(r"(?i)(Bearer|Basic)\s+[A-Za-z0-9_./+=-]+", r"\1 [REDACTED]", text)
    return text


def validate_https_url(url):
    if not isinstance(url, str) or len(url) > 16384:
        raise ProbeError("Invalid HTTPS URL")
    parsed = urllib.parse.urlsplit(url)
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username or
            parsed.password or parsed.fragment or any(c.isspace() for c in url)):
        raise ProbeError("Only HTTPS URLs without embedded credentials are accepted")
    return url


def read_bytes(path, limit=MAX_FILE):
    path = Path(path)
    if not path.is_file() or not 0 < path.stat().st_size <= limit:
        raise ProbeError("Missing, empty or oversized local input: " + path.name)
    with path.open("rb") as stream:
        result = stream.read(limit + 1)
    if len(result) > limit:
        raise ProbeError("Local input exceeded size limit")
    return result


def read_json(path):
    try:
        return json.loads(read_bytes(path, MAX_JSON).decode("utf-8-sig"))
    except (ValueError, UnicodeError) as exc:
        raise ProbeError("Invalid local JSON: " + Path(path).name) from exc


def write_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    # This helper is for explicitly constructed non-secret receipts, never API bodies.
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def identifier(value):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,39}", value):
        raise ProbeError("Invalid APS resource identifier")
    return value


def validate_config(config, input_data):
    if not isinstance(config, dict) or config.get("schema_version") != 1:
        raise ProbeError("Unsupported configuration schema")
    try:
        uuid.UUID(config["run_id"])
    except (KeyError, ValueError, TypeError, AttributeError) as exc:
        raise ProbeError("Configuration run_id must be a UUID") from exc
    for key in ("fixture_id", "source_handle", "width_property", "height_property", "outline_layer"):
        if not isinstance(config.get(key), str) or not config[key].strip():
            raise ProbeError("Missing configuration field: " + key)
    if config.get("input_sha256", "").lower() != sha256(input_data):
        raise ProbeError("Configuration input_sha256 does not match DWG")
    tolerance = config.get("tolerance")
    if isinstance(tolerance, bool) or not isinstance(tolerance, (int, float)) or not math.isfinite(tolerance) or not 0 < tolerance <= 1e-4:
        raise ProbeError("Tolerance must be finite, positive and <= 1e-4 mm")
    cases = config.get("cases")
    if not isinstance(cases, list) or not 1 <= len(cases) <= 1000:
        raise ProbeError("A probe requires 1..1000 explicit cases")
    ids = set()
    for case in cases:
        if not isinstance(case, dict) or not isinstance(case.get("id"), str) or not case["id"] or case["id"] in ids:
            raise ProbeError("Case ids must be non-empty and unique")
        ids.add(case["id"])
        if case.get("mode") not in ("sample", "definition") or case.get("order") not in ("production", "width_height", "height_width"):
            raise ProbeError("Unsupported probe mode or property order")
        for field in ("width", "height", "x", "y"):
            v = case.get(field)
            if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or abs(v) > 1e11:
                raise ProbeError("Case geometry must be finite and bounded")
            if field in ("width", "height") and v <= 0:
                raise ProbeError("Case dimensions must be positive")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # Never forward credentials to another host. APS/S3 URLs are already direct.
        return None


class Transport:
    def __init__(self, timeout=45):
        self.timeout = timeout
        self.opener = urllib.request.build_opener(NoRedirect())

    def request(self, method, url, data=None, headers=None, limit=MAX_JSON):
        validate_https_url(url)
        request = urllib.request.Request(url, data=data, headers=headers or {}, method=method)
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                body = response.read(limit + 1)
                if len(body) > limit:
                    raise ProbeError("HTTP response exceeded configured size limit")
                return body
        except urllib.error.HTTPError as exc:
            raise HTTPFailure(exc.code) from None
        except (urllib.error.URLError, TimeoutError, OSError):
            raise ProbeError("Network request failed; endpoint and credentials withheld") from None


class APSClient:
    def __init__(self, region="us-east", transport=None):
        if region not in ("us-east", "eu-west"):
            raise ProbeError("Unsupported APS region")
        self.base = HOST + "/da/" + region + "/v3"
        self.transport = transport or Transport()
        self.token = None
        self.expires_at = 0
        self.oss_scope = False

    def authenticate(self, oss=False):
        client_id = os.environ.get("APS_CLIENT_ID")
        secret = os.environ.get("APS_CLIENT_SECRET")
        if not client_id or not secret:
            raise ProbeError("APS credentials are not configured in environment; NOT_RUN")
        self.oss_scope = self.oss_scope or oss
        scope = "code:all"
        if self.oss_scope:
            scope += " bucket:read bucket:create data:read data:write data:create"
        authorization = base64.b64encode((client_id + ":" + secret).encode()).decode()
        raw = self.transport.request("POST", HOST + "/authentication/v2/token",
            urllib.parse.urlencode({"grant_type": "client_credentials", "scope": scope}).encode(),
            {"Authorization": "Basic " + authorization, "Content-Type": "application/x-www-form-urlencoded"})
        result = self.decode(raw)
        if not isinstance(result, dict) or not isinstance(result.get("access_token"), str):
            raise ProbeError("OAuth did not return an access token")
        self.token = result["access_token"]
        self.expires_at = time.monotonic() + min(int(result.get("expires_in", 300)), 3600) - 30

    @staticmethod
    def decode(raw):
        try:
            return json.loads(raw.decode("utf-8")) if raw else None
        except (UnicodeError, ValueError):
            raise ProbeError("APS returned invalid JSON; body withheld") from None

    def api(self, method, path, payload=None, oss=False):
        if not self.token or time.monotonic() >= self.expires_at or (oss and not self.oss_scope):
            self.authenticate(oss)
        if not path.startswith("/") or ".." in path:
            raise ProbeError("Invalid API path")
        base = HOST + "/oss/v2" if oss else self.base
        data = None if payload is None else json.dumps(payload).encode()
        raw = self.transport.request(method, base + path, data,
            {"Authorization": "Bearer " + self.token, "Content-Type": "application/json"})
        return self.decode(raw)

    def preflight(self, engine):
        engines = []
        token = None
        for _ in range(20):
            suffix = "?page=" + urllib.parse.quote(token, safe="") if token else ""
            result = self.api("GET", "/engines" + suffix)
            if not isinstance(result, dict) or not isinstance(result.get("data"), list):
                raise ProbeError("Engine inventory has an unexpected shape")
            engines.extend(result["data"])
            token = result.get("paginationToken")
            if not token:
                break
        else:
            raise ProbeError("Engine inventory pagination limit exceeded")
        if engine not in engines:
            raise ProbeError("Required AutoCAD engine is unavailable; no version fallback")
        profile = self.api("GET", "/forgeapps/me")
        # API versions may return the nickname as a string or a profile object.
        if not isinstance(profile, (str, dict)):
            raise ProbeError("APS namespace response has an unexpected shape")
        owner = profile if isinstance(profile, str) else profile.get("nickname")
        if not owner:
            owner = os.environ.get("APS_CLIENT_ID")
        if not isinstance(owner, str) or not re.fullmatch(r"[A-Za-z0-9_-]+", owner):
            raise ProbeError("Cannot resolve APS app namespace")
        return {"engine": engine, "owner": owner, "engine_available": True}

    def poll(self, workitem_id, timeout=900, interval=5, clock=time.monotonic, sleep=time.sleep):
        if not 1 <= timeout <= 1800 or not 0 < interval <= 30:
            raise ProbeError("Polling bounds must be 1..1800 seconds and interval <=30")
        if not re.fullmatch(r"[A-Za-z0-9_-]+", workitem_id):
            raise ProbeError("Invalid workitem identifier")
        deadline = clock() + timeout
        while True:
            result = self.api("GET", "/workitems/" + workitem_id)
            if not isinstance(result, dict):
                raise ProbeError("WorkItem status response is missing")
            status = result.get("status")
            if status in TERMINAL:
                return result
            if status not in PENDING:
                raise ProbeError("Unknown WorkItem status; not accepted as success")
            remaining = deadline - clock()
            if remaining <= 0:
                return {"id": workitem_id, "status": "CLIENT_TIMEOUT", "remote_job_may_still_run": True}
            sleep(min(interval, remaining))


def activity_template(prefix, owner, alias, engine, kind):
    identifier(prefix)
    identifier(alias)
    if len(prefix) > 34:
        raise ProbeError("AppBundle prefix must leave room for the Reopen suffix (max 34 characters)")
    if kind not in ("probe", "reopen"):
        raise ProbeError("Unknown activity kind")
    reopen = kind == "reopen"
    parameters = {
        "dwg": {"verb": "get", "required": True, "localName": "input.dwg"},
        "config": {"verb": "get", "required": True, "localName": "config.json"},
        "result": {"verb": "put", "required": True, "localName": "reopen-result.json" if reopen else "probe-result.json"},
    }
    if reopen:
        parameters["expected"] = {"verb": "get", "required": True, "localName": "expected.json"}
    else:
        parameters["output"] = {"verb": "put", "required": True, "localName": "output.dwg"}
    return {
        "id": prefix + ("Reopen" if reopen else "Probe"),
        "engine": engine,
        "appbundles": [owner + "." + prefix + "+" + alias],
        "commandLine": ['"$(engine.path)\\accoreconsole.exe" /i "$(args[dwg].path)" /al "$(appbundles[' + prefix + '].path)" /s "$(settings[script].path)"'],
        "parameters": parameters,
        "settings": {"script": {"value": ("ATREOPENVERIFY" if reopen else "ATPROBE") + "\n"}},
        "description": "Private diagnostic Core probe; no desktop or production ATTILE acceptance",
    }


def create_version(client, collection, resource_id, payload):
    try:
        result = client.api("POST", "/" + collection, payload)
    except HTTPFailure as exc:
        if exc.status != 409:
            raise
        update = dict(payload)
        update.pop("id", None)
        result = client.api("POST", "/" + collection + "/" + resource_id + "/versions", update)
    if not isinstance(result, dict) or type(result.get("version")) is not int or result["version"] < 1:
        raise ProbeError("APS resource version was not returned")
    return result


def upload_bundle(client, upload, bundle_data):
    if not isinstance(upload, dict) or not isinstance(upload.get("formData"), dict):
        raise ProbeError("Missing AppBundle signed upload contract")
    boundary = "apsprobe" + uuid.uuid4().hex
    chunks = []
    for key, value in upload["formData"].items():
        if not re.fullmatch(r"[A-Za-z0-9_-]+", key) or not isinstance(value, str):
            raise ProbeError("Invalid AppBundle multipart form field")
        chunks.append(("--" + boundary + '\r\nContent-Disposition: form-data; name="' + key + '"\r\n\r\n' + value + "\r\n").encode())
    chunks.append(("--" + boundary + '\r\nContent-Disposition: form-data; name="file"; filename="bundle.zip"\r\nContent-Type: application/zip\r\n\r\n').encode())
    chunks.extend([bundle_data, ("\r\n--" + boundary + "--\r\n").encode()])
    client.transport.request("POST", upload["endpointURL"], b"".join(chunks),
        {"Content-Type": "multipart/form-data; boundary=" + boundary})


def publish(client, bundle_path, prefix, engine, receipt_path):
    identifier(prefix)
    if len(prefix) > 34:
        raise ProbeError("AppBundle prefix must be at most 34 characters")
    data = read_bytes(bundle_path)
    import io
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            names = [n for n in archive.namelist() if n.endswith("/Contents/AutoCAD.CoreProbe.dll")]
            if len(names) != 1 or archive.getinfo(names[0]).file_size > MAX_FILE:
                raise ProbeError("Bundle must contain exactly one AutoCAD.CoreProbe.dll")
            harness_hash = sha256(archive.read(names[0]))
    except zipfile.BadZipFile:
        raise ProbeError("Invalid bundle ZIP") from None
    preflight = client.preflight(engine)
    owner = preflight["owner"]
    alias = "r" + uuid.uuid4().hex[:20]  # Unique alias is never repointed.
    result = create_version(client, "appbundles", prefix, {"id": identifier(prefix), "engine": engine,
        "description": "AutoCAD Core diagnostic probe " + sha256(data)})
    upload_bundle(client, result.get("uploadParameters"), data)
    client.api("POST", "/appbundles/" + prefix + "/aliases", {"id": alias, "version": result["version"]})
    deployment = {"schema_version": 1, "engine": engine, "owner": owner,
        "prefix": prefix, "alias": alias, "bundle_sha256": sha256(data), "harness_sha256": harness_hash,
        "appbundle_version": result["version"], "activities": {}}
    for kind in ("probe", "reopen"):
        template = activity_template(prefix, owner, alias, engine, kind)
        activity = create_version(client, "activities", template["id"], template)
        client.api("POST", "/activities/" + template["id"] + "/aliases", {"id": alias, "version": activity["version"]})
        qualified = owner + "." + template["id"] + "+" + alias
        deployment["activities"][kind] = {"id": qualified, "version": activity["version"]}
    write_json(receipt_path, deployment)
    return deployment


def verify_deployment(client, deployment):
    if not isinstance(deployment, dict) or deployment.get("schema_version") != 1 or deployment.get("engine") != ENGINE:
        raise ProbeError("Unsupported deployment schema/engine")
    identifier(deployment.get("prefix"))
    identifier(deployment.get("alias"))
    if not isinstance(deployment.get("activities"), dict) or set(deployment["activities"]) != {"probe", "reopen"}:
        raise ProbeError("Deployment requires exactly the probe and reopen activities")
    for field in ("bundle_sha256", "harness_sha256"):
        if not re.fullmatch(r"[0-9a-f]{64}", deployment.get(field, "")):
            raise ProbeError("Missing deployment binary hashes")
    namespace = client.preflight(deployment["engine"])["owner"]
    if namespace != deployment["owner"]:
        raise ProbeError("Deployment belongs to a different APS application")
    bundle_id = namespace + "." + deployment["prefix"] + "+" + deployment["alias"]
    bundle = client.api("GET", "/appbundles/" + urllib.parse.quote(bundle_id, safe=""))
    if not isinstance(bundle, dict) or bundle.get("version") != deployment["appbundle_version"] or bundle.get("engine") != deployment["engine"]:
        raise ProbeError("AppBundle alias/version changed after publication")
    for kind, item in deployment["activities"].items():
        expected_id = namespace + "." + deployment["prefix"] + ("Reopen" if kind == "reopen" else "Probe") + "+" + deployment["alias"]
        if not isinstance(item, dict) or item.get("id") != expected_id or type(item.get("version")) is not int or item["version"] < 1:
            raise ProbeError("Invalid deployment activity identity/version")
        activity = client.api("GET", "/activities/" + urllib.parse.quote(item["id"], safe=""))
        if (not isinstance(activity, dict) or activity.get("version") != item["version"] or activity.get("engine") != deployment["engine"]
                or activity.get("appbundles") != [bundle_id]):
            raise ProbeError("Activity alias/version or AppBundle changed after publication")
        expected = activity_template(deployment["prefix"], namespace, deployment["alias"], deployment["engine"], kind)
        for field in ("commandLine", "parameters", "settings"):
            # APS can fill defaults into parameters; require our explicit fields.
            actual = activity.get(field)
            if field in ("parameters", "settings") and isinstance(actual, dict):
                matches = set(actual) == set(expected[field]) and all(
                    isinstance(actual[name], dict) and all(actual[name].get(k) == v for k, v in details.items())
                    for name, details in expected[field].items())
            else:
                matches = actual == expected[field]
            if not matches:
                raise ProbeError("Published Activity execution contract changed: " + field)


class OSS:
    def __init__(self, client, bucket):
        if not re.fullmatch(r"[a-z0-9_-]{3,128}", bucket):
            raise ProbeError("Invalid private OSS bucket name")
        self.client, self.bucket = client, bucket

    def ensure(self, create=False):
        if create:
            try:
                self.client.api("POST", "/buckets", {"bucketKey": self.bucket, "policyKey": "transient"}, oss=True)
            except HTTPFailure as exc:
                if exc.status != 409:
                    raise
        info = self.client.api("GET", "/buckets/" + self.bucket + "/details", oss=True)
        if not isinstance(info, dict) or info.get("bucketKey") != self.bucket:
            raise ProbeError("Private OSS bucket was not verified")

    def path(self, key):
        return "/buckets/" + self.bucket + "/objects/" + urllib.parse.quote(key, safe="")

    def start_upload(self, key):
        info = self.client.api("GET", self.path(key) + "/signeds3upload?parts=1&minutesExpiration=60", oss=True)
        if not isinstance(info, dict) or len(info.get("urls", [])) != 1 or not info.get("uploadKey"):
            raise ProbeError("Missing OSS upload URL/key")
        validate_https_url(info["urls"][0])
        return {"key": key, "url": info["urls"][0], "upload_key": info["uploadKey"]}

    def finish_upload(self, transfer):
        self.client.api("POST", self.path(transfer["key"]) + "/signeds3upload",
            {"uploadKey": transfer["upload_key"]}, oss=True)

    def download_url(self, key):
        info = self.client.api("GET", self.path(key) + "/signeds3download?minutesExpiration=60", oss=True)
        if not isinstance(info, dict) or info.get("status") != "complete" or not info.get("url"):
            raise ProbeError("OSS object is not assembled; download not available")
        return validate_https_url(info["url"])

    def upload(self, key, data):
        if not data or len(data) > MAX_FILE:
            raise ProbeError("Upload size must be 1..100 MiB")
        transfer = self.start_upload(key)
        self.client.transport.request("PUT", transfer["url"], data, {"Content-Type": "application/octet-stream"})
        self.finish_upload(transfer)
        return self.download_url(key)


def report_bindings(report, config, config_sha256, harness_sha256, input_sha256):
    if not isinstance(report, dict) or report.get("schema_version") != 1:
        raise ProbeError("Missing or unsupported probe result schema")
    expected = {"run_id": config["run_id"], "fixture_id": config["fixture_id"],
        "configuration_sha256": config_sha256, "harness_sha256": harness_sha256,
        "input_sha256": input_sha256, "source_handle": config["source_handle"]}
    for key, value in expected.items():
        actual = report.get(key)
        if isinstance(value, str) and key.endswith("sha256"):
            actual = actual.lower() if isinstance(actual, str) else actual
        if actual != value:
            raise ProbeError("Result binding mismatch: " + key)
    cases = report.get("cases")
    if not isinstance(cases, list) or len(cases) != len(config["cases"]):
        raise ProbeError("Result is missing requested cases")
    ids = [c.get("id") for c in cases if isinstance(c, dict)]
    if len(ids) != len(cases) or len(set(ids)) != len(ids) or set(ids) != {c["id"] for c in config["cases"]}:
        raise ProbeError("Result case ids do not match requested cases")
    requested = {c["id"]: c for c in config["cases"]}
    for case in cases:
        if case.get("requested") != requested[case["id"]]:
            raise ProbeError("Result case parameters do not match requested matrix")
    return cases


def finite_number(value):
    return not isinstance(value, bool) and isinstance(value, (int, float)) and math.isfinite(value)


def native_evidence(report, phase):
    host = report.get("host")
    return (report.get("phase") == phase and report.get("execution_status") == "COMPLETED"
        and report.get("native_execution") is True and report.get("input_file_unchanged") is True
        and report.get("source_preserved") is True and report.get("source_unchanged") is True
        and isinstance(host, dict) and host.get("process", "").lower() == "accoreconsole"
        and str(host.get("acadver", "")).startswith("24.3"))


def case_evidence(case, config):
    """Independent numeric oracle; a PASS label alone is insufficient."""
    requested = case["requested"]
    tolerance = config["tolerance"]
    if case.get("status") != "PASS" or not case.get("created_handle"):
        return False
    for field in ("width", "height"):
        actual = case.get("actual_" + field)
        if not finite_number(actual) or abs(actual - requested[field]) > tolerance:
            return False
    points = case.get("native_outline_vertices")
    if not isinstance(points, list) or len(points) != 4:
        return False
    if any(not isinstance(p, list) or len(p) != 3 or not all(finite_number(v) for v in p) for p in points):
        return False
    x, y, w, h = (requested[k] for k in ("x", "y", "width", "height"))
    expected = [[x, y, 0], [x + w, y, 0], [x + w, y + h, 0], [x, y + h, 0]]
    for i, point in enumerate(points):
        next_point = points[(i + 1) % 4]
        if (abs(point[0] - next_point[0]) <= tolerance) == (abs(point[1] - next_point[1]) <= tolerance):
            return False  # Reject crossed/diagonal or zero-length edges.
        found = next((j for j, target in enumerate(expected)
            if all(abs(a - b) <= tolerance for a, b in zip(point, target))), None)
        if found is None:
            return False
        expected.pop(found)
    return not expected


def validate_probe_report(report, config, config_sha256, harness_sha256, input_sha256, output_sha256=None):
    cases = report_bindings(report, config, config_sha256, harness_sha256, input_sha256)
    if output_sha256 is None or report.get("output_sha256") != output_sha256:
        raise ProbeError("Saved DWG hash does not match probe report")
    if report.get("status") not in ("PASS", "FAIL", "NOT_RUN"):
        raise ProbeError("Unknown probe result status")
    handles = [c.get("created_handle") for c in cases]
    positive = (report["status"] == "PASS" and native_evidence(report, "probe")
        and len(set(handles)) == len(handles) and report.get("created_handles") == handles
        and all(case_evidence(c, config) and c.get("persisted") is True
                and c.get("source_preserved") is True and c.get("membership_preserved") is True for c in cases))
    return "PASS" if positive else "FAIL"


def validate_reopen_report(report, probe_report, config, config_sha256, harness_sha256,
                           input_sha256, expected_report_sha256):
    cases = report_bindings(report, config, config_sha256, harness_sha256, input_sha256)
    if report.get("expected_report_sha256") != expected_report_sha256:
        raise ProbeError("Reopen result does not bind the first report")
    expected_handles = {c["id"]: c.get("created_handle") for c in probe_report["cases"]}
    expected_cases = {c["id"]: c for c in probe_report["cases"]}
    positive = (report.get("status") == "PASS" and probe_report.get("status") == "PASS"
        and native_evidence(report, "reopen") and all(case_evidence(c, config)
            and c.get("created_handle") == expected_handles[c["id"]]
            and saved_observations_match(expected_cases[c["id"]], c, config["tolerance"]) for c in cases))
    return "PASS" if positive else "FAIL"


def equivalent(before, after, tolerance):
    numeric_before = isinstance(before, (int, float)) and not isinstance(before, bool)
    numeric_after = isinstance(after, (int, float)) and not isinstance(after, bool)
    if numeric_before or numeric_after:
        return finite_number(before) and finite_number(after) and abs(before - after) <= tolerance
    if isinstance(before, dict) and isinstance(after, dict):
        return set(before) == set(after) and all(equivalent(value, after[key], tolerance) for key, value in before.items())
    if isinstance(before, list) and isinstance(after, list):
        return len(before) == len(after) and all(equivalent(a, b, tolerance) for a, b in zip(before, after))
    return type(before) is type(after) and before == after


def saved_observations_match(before, after, tolerance):
    # Signature hashes remain diagnostics: native reevaluation can change low-order bits.
    for field in ("actual_width", "actual_height", "position", "rotation", "scale"):
        if field not in before or field not in after or not equivalent(before[field], after[field], tolerance):
            return False
    old, new = before.get("actual_properties"), after.get("actual_properties")
    if not isinstance(old, list) or not isinstance(new, list) or len(old) != len(new):
        return False
    if any(not isinstance(p, dict) or not isinstance(p.get("name"), str) for p in old + new):
        return False
    old_properties, new_properties = ({p["name"]: p for p in collection} for collection in (old, new))
    if len(old_properties) != len(old) or len(new_properties) != len(new) or not equivalent(old_properties, new_properties, tolerance):
        return False
    old_points, new_points = before.get("native_outline_vertices"), after.get("native_outline_vertices")
    if not isinstance(old_points, list) or not isinstance(new_points, list) or len(old_points) != len(new_points):
        return False
    remaining = list(new_points)
    for point in old_points:
        index = next((i for i, candidate in enumerate(remaining) if equivalent(point, candidate, tolerance)), None)
        if index is None:
            return False
        remaining.pop(index)
    return not remaining


def execute_stage(client, oss, activity, inputs, output_names, key_prefix, output_dir, timeout, processing_limit):
    transfers = {name: oss.start_upload(key_prefix + "/" + local_name) for name, local_name in output_names.items()}
    arguments = {name: {"verb": "get", "url": url} for name, url in inputs.items()}
    arguments.update({name: {"verb": "put", "url": item["url"]} for name, item in transfers.items()})
    submitted = client.api("POST", "/workitems", {"activityId": activity["id"], "arguments": arguments,
        "limitProcessingTimeSec": processing_limit})
    if not isinstance(submitted, dict) or not submitted.get("id"):
        raise ProbeError("APS did not return WorkItem id; submission is ambiguous; do not auto-retry")
    workitem_id = submitted["id"]
    receipt = {"workitem_id": workitem_id, "activity_id": activity["id"], "activity_version": activity["version"], "status": "SUBMITTED"}
    write_json(output_dir / "workitem.json", receipt)
    result = client.poll(workitem_id, timeout=timeout)
    receipt["status"] = result["status"]
    receipt["remote_job_may_still_run"] = result.get("remote_job_may_still_run", False)
    write_json(output_dir / "workitem.json", receipt)
    files = {}
    errors = []
    if result.get("reportUrl"):
        try:
            report = client.transport.request("GET", result["reportUrl"], limit=MAX_JSON)
            (output_dir / "aps-report.redacted.txt").write_text(safe_text(report.decode("utf-8", errors="replace")), encoding="utf-8")
        except ProbeError as exc:
            errors.append("APS report: " + safe_text(exc))
    elif result["status"] != "CLIENT_TIMEOUT":
        errors.append("APS execution report is missing")
    if result["status"] != "CLIENT_TIMEOUT":
        for name, transfer in transfers.items():
            try:
                oss.finish_upload(transfer)
                data = client.transport.request("GET", oss.download_url(transfer["key"]), limit=MAX_JSON if name == "result" else MAX_FILE)
                if not data:
                    raise ProbeError("Empty output")
                local_path = output_dir / output_names[name]
                local_path.write_bytes(data)
                files[name] = {"path": str(local_path), "sha256": sha256(data), "oss_key": transfer["key"]}
            except ProbeError as exc:
                errors.append(name + ": " + safe_text(exc))
    receipt["outputs"] = files
    receipt["output_errors"] = errors
    write_json(output_dir / "workitem.json", receipt)
    return receipt


def run_pipeline(client, args):
    input_data, config_data = read_bytes(args.input), read_bytes(args.config, MAX_JSON)
    config = json.loads(config_data.decode("utf-8-sig"))
    validate_config(config, input_data)
    deployment = read_json(args.deployment)
    verify_deployment(client, deployment)
    output_dir = Path(args.output_dir)
    if output_dir.exists() and any(output_dir.iterdir()):
        raise ProbeError("Output directory must be empty to prevent stale results")
    output_dir.mkdir(parents=True, exist_ok=True)
    oss = OSS(client, args.bucket)
    oss.ensure(args.create_bucket)
    prefix = "coreprobe/" + uuid.uuid4().hex
    config_url = oss.upload(prefix + "/config.json", config_data)
    input_url = oss.upload(prefix + "/input.dwg", input_data)
    summary = {"status": "NOT_RUN", "scope": "APS AutoCAD Core diagnostic probe only",
        "engine": deployment["engine"], "run_id": config["run_id"], "case_count": len(config["cases"]),
        "input_sha256": sha256(input_data), "configuration_sha256": sha256(config_data),
        "harness_sha256": deployment["harness_sha256"], "bundle_sha256": deployment["bundle_sha256"],
        "desktop_acceptance": "NOT_RUN", "probe": None, "reopen": None}
    write_json(output_dir / "summary.json", summary)
    stage = execute_stage(client, oss, deployment["activities"]["probe"], {"dwg": input_url, "config": config_url},
        {"result": "probe-result.json", "output": "output.dwg"}, prefix + "/probe", output_dir / "probe", args.timeout, args.processing_limit)
    summary["probe"] = stage
    summary["status"] = "FAIL"
    write_json(output_dir / "summary.json", summary)
    if stage["status"] != "success" or stage["output_errors"] or set(stage["outputs"]) != {"result", "output"}:
        return summary
    probe = read_json(stage["outputs"]["result"]["path"])
    probe_status = validate_probe_report(probe, config, sha256(config_data), deployment["harness_sha256"],
        sha256(input_data), stage["outputs"]["output"]["sha256"])
    summary["probe_assertions"] = probe_status
    # Reopening a failing probe remains diagnostic; it must never erase its failure.
    if args.reopen:
        reopen = execute_stage(client, oss, deployment["activities"]["reopen"], {
            "dwg": oss.download_url(stage["outputs"]["output"]["oss_key"]), "config": config_url,
            "expected": oss.download_url(stage["outputs"]["result"]["oss_key"])},
            {"result": "reopen-result.json"}, prefix + "/reopen", output_dir / "reopen", args.timeout, args.processing_limit)
        summary["reopen"] = reopen
        if reopen["status"] == "success" and not reopen["output_errors"] and "result" in reopen["outputs"]:
            reopened = read_json(reopen["outputs"]["result"]["path"])
            reopen_status = validate_reopen_report(reopened, probe, config, sha256(config_data), deployment["harness_sha256"],
                stage["outputs"]["output"]["sha256"], stage["outputs"]["result"]["sha256"])
            summary["reopen_assertions"] = reopen_status
            if probe_status == "PASS" and reopen_status == "PASS":
                summary["status"] = "PASS_CORE_PROBE_AND_REOPEN"
    elif probe_status == "PASS":
        summary["status"] = "PASS_CORE_PROBE_ONLY"
    write_json(output_dir / "summary.json", summary)
    return summary


def make_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--region", choices=("us-east", "eu-west"), default="us-east")
    sub = parser.add_subparsers(dest="command", required=True)
    preflight = sub.add_parser("preflight")
    preflight.add_argument("--engine", default=ENGINE, choices=(ENGINE,))
    publish_cmd = sub.add_parser("publish")
    publish_cmd.add_argument("--bundle", required=True)
    publish_cmd.add_argument("--prefix", default="AutoCADCoreProbe")
    publish_cmd.add_argument("--engine", default=ENGINE, choices=(ENGINE,))
    publish_cmd.add_argument("--receipt", required=True)
    run = sub.add_parser("run")
    for arg in ("deployment", "input", "config", "bucket", "output-dir"):
        run.add_argument("--" + arg, required=True)
    run.add_argument("--create-bucket", action="store_true")
    run.add_argument("--reopen", action="store_true")
    run.add_argument("--timeout", type=int, default=900)
    run.add_argument("--processing-limit", type=int, default=120)
    for command in (preflight, publish_cmd, run):
        command.add_argument("--execute", action="store_true", help="Permit real network calls (run may incur APS charges)")
    return parser


def main(argv=None):
    args = make_parser().parse_args(argv)
    try:
        if args.command == "run" and (not 1 <= args.timeout <= 1800 or not 1 <= args.processing_limit <= 900):
            raise ProbeError("Timeout must be <=1800 and engine processing <=900 seconds")
        if not args.execute:
            print(json.dumps({"status": "NOT_RUN", "mode": "offline_plan", "command": args.command,
                "network_requests": 0, "engine": ENGINE, "credentials_checked": False, "inputs_validated": False,
                "next_step": "Configure existing APS application credentials in environment; then explicitly use --execute",
                "native_desktop_acceptance": "NOT_RUN"}, ensure_ascii=False, indent=2))
            return 0
        client = APSClient(args.region)
        if args.command == "preflight":
            result = client.preflight(args.engine)
            # Namespace may equal client id; do not print credentials/identifiers.
            print(json.dumps({"status": "PREFLIGHT_OK", "engine": result["engine"], "namespace_resolved": True,
                "workitems_submitted": 0, "native_desktop_acceptance": "NOT_RUN"}))
        elif args.command == "publish":
            result = publish(client, args.bundle, args.prefix, args.engine, args.receipt)
            print(json.dumps({"status": "PUBLISHED_NOT_EXECUTED", "bundle_sha256": result["bundle_sha256"],
                "harness_sha256": result["harness_sha256"], "workitems_submitted": 0}))
        else:
            result = run_pipeline(client, args)
            print(json.dumps({"status": result["status"], "case_count": result["case_count"], "desktop_acceptance": "NOT_RUN"}))
            return 0 if result["status"].startswith("PASS_CORE_PROBE") else 1
        return 0
    except (ProbeError, ValueError, KeyError, TypeError, OSError) as exc:
        print(json.dumps({"status": "BLOCKED_OR_FAILED", "error": safe_text(exc), "native_desktop_acceptance": "NOT_RUN"}, ensure_ascii=False), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
