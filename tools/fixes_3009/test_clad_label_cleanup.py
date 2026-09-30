"""Run the actual CladCommand.RemoveData under Mono with shared CAD API doubles.

The method and key declarations are extracted unchanged from production sources.
This checks dictionary/record lifecycle, not Autodesk transactions or Undo.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def extract_method(source):
    anchor = re.compile(r"(?m)^[ \t]*internal\s+static\s+void\s+RemoveData\s*"
                        r"\(\s*Transaction\s+tr\s*,\s*Entity\s+ent\s*,\s*string\s+key\s*\)")
    matches = list(anchor.finditer(source))
    if len(matches) != 1:
        raise ValueError(f"Expected one actual RemoveData declaration, found {len(matches)}")
    start = matches[0].start()
    opening = source.index("{", matches[0].end())
    depth, index, state = 0, opening, "code"
    while index < len(source):
        char = source[index]
        following = source[index + 1:index + 2]
        if state == "line":
            if char == "\n":
                state = "code"
        elif state == "block":
            if char == "*" and following == "/":
                state, index = "code", index + 1
        elif state == "verbatim":
            if char == '"':
                if following == '"':
                    index += 1
                else:
                    state = "code"
        elif state in ("string", "char"):
            if char == "\\":
                index += 1
            elif char == ('"' if state == "string" else "'"):
                state = "code"
        elif char == "/" and following in ("/", "*"):
            state, index = ("line" if following == "/" else "block"), index + 1
        elif char == "@" and following == '"':
            state, index = "verbatim", index + 1
        elif char in ('"', "'"):
            state = "string" if char == '"' else "char"
        elif char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[start:index + 1], source.count("\n", 0, start) + 1
        index += 1
    raise ValueError("Unbalanced RemoveData method")


def extract_constant(source, name):
    pattern = re.compile(r"(?m)^[ \t]*internal\s+const\s+string\s+" + re.escape(name)
                         + r'\s*=\s*"(?:\\.|[^"\\])*"\s*;')
    matches = list(pattern.finditer(source))
    if len(matches) != 1:
        raise ValueError(f"Expected one {name} key declaration, found {len(matches)}")
    return matches[0].group()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="clad_label_cleanup_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    clad = ROOT / "AClad/src/ACladPlugin/CladCommand.cs"
    tile = ROOT / "AClad/src/ACladPlugin/TilePatternCommand.cs"
    driver = Path(__file__).with_suffix(".cs")
    doubles = Path(__file__).with_name("test_zone_geometry_cad_doubles.cs")
    sources = [clad, tile, driver, doubles, Path(__file__)]
    report = {"status": "BLOCKED", "scope": "Actual unmodified RemoveData; quantity-cleanup hook spy, native Mono, CAD API doubles, not AutoCAD. Real quantity store checked separately.",
              "source_sha256": {str(p.relative_to(ROOT)): sha(p.read_bytes()) for p in sources}}

    def finish(code):
        (out / "manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Clad label cleanup:", report["status"], "manifest:", out / "manifest.json")
        return code

    try:
        source = clad.read_text(encoding="utf-8")
        method, line = extract_method(source)
        clad_key = extract_constant(source, "XKeyClad")
        tile_key = extract_constant(tile.read_text(encoding="utf-8"), "XKeyTile")
    except (ValueError, OSError) as error:
        report["reason"] = str(error)
        return finish(2)
    report["extracted_method"] = {"name": "CladCommand.RemoveData", "line": line,
                                  "sha256": sha(method.encode("utf-8"))}
    report["constant_sha256"] = {"XKeyClad": sha(clad_key.encode("utf-8")),
                                  "XKeyTile": sha(tile_key.encode("utf-8"))}
    wrapper = out / "ActualCladRemoveData.cs"
    wrapper.write_text("using System;\nusing System.Collections.Generic;\n"
                       "using Autodesk.AutoCAD.DatabaseServices;\nusing FacadeSafety;\nnamespace ACladPlugin {\n"
                       "internal static class CladCommand {\n" + clad_key + "\n" + method + "\n}\n"
                       "internal static class TilePatternCommand {\n" + tile_key + "\n}\n}\n", encoding="utf-8")
    report["wrapper_sha256"] = sha(wrapper.read_bytes())
    if not shutil.which("mcs") or not shutil.which("mono"):
        report["reason"] = "Missing mcs/mono. No checks claimed."
        return finish(2)
    executable = out / "CladLabelCleanupCheck.exe"
    compiled = subprocess.run(["mcs", "-nologo", "-warnaserror", "-out:" + str(executable),
                               str(wrapper), str(doubles), str(driver)], capture_output=True, text=True)
    (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    report["compile_returncode"] = compiled.returncode
    if compiled.returncode:
        report["status"] = "FAIL"
        print(compiled.stdout + compiled.stderr, end="")
        return finish(1)
    run = subprocess.run(["mono", str(executable)], capture_output=True, text=True)
    (out / "run.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    print(run.stdout + run.stderr, end="")
    report["run_returncode"] = run.returncode
    counts = re.search(r"checks=(\d+), failures=(\d+)", run.stdout)
    if counts:
        report["checks"], report["failures"] = map(int, counts.groups())
    report["status"] = "PASS" if run.returncode == 0 and report.get("checks", 0) > 0 else "FAIL"
    return finish(0 if report["status"] == "PASS" else 1)


if __name__ == "__main__":
    sys.exit(main())
