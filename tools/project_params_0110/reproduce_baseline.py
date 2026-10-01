"""Reproduce the 2B1 project/zone-model gap using pinned actual C# sources.

The probe compiles unchanged historical FrameSettings/FrameSolutionSelection
and the actual FrameOwnerState method. Storage holders are minimal doubles.
It does not claim a live AutoCAD run, nor classify unsupported future keys as
a pre-existing bug. Run --out docs/project_params_0110/baseline.json.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
BASELINE = "375f082887a052bcba3d0379bd4946ef39b25604"
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
from probe_runtime import available, command, compile_probe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", default=BASELINE)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: native C# compiler/runtime unavailable")
        return 2
    hashes = {}
    def original(name):
        data = subprocess.run(["git", "show", args.revision + ":" + name], cwd=ROOT,
                              check=True, capture_output=True).stdout
        hashes[name] = hashlib.sha256(data).hexdigest()
        return data.decode("utf-8")
    revision = subprocess.run(["git", "rev-parse", args.revision], cwd=ROOT,
                              check=True, capture_output=True, text=True).stdout.strip()
    with tempfile.TemporaryDirectory(prefix="project_params_baseline_") as tmp:
        work = Path(tmp)
        sources = [Path(__file__).with_suffix(".cs")]
        for name in ("FrameSettings.cs", "FrameSolutionSelection.cs"):
            source = work / name
            source.write_text(original("AFrame/src/AFramePlugin/" + name), encoding="utf-8")
            sources.append(source)
        store = original("Common/FacadeQuantityStore.Frame.cs")
        begin = store.index("        private static LayoutGeometryGuard.Result FrameOwnerState(")
        end = store.index("        // Capture unique input objects", begin)
        method = store[begin:end]
        helpers = original("Common/FacadeQuantityStore.cs")
        begin = helpers.index("        private static string Hash(string text)")
        end = helpers.index("        private static JavaScriptSerializer Serializer()", begin)
        hash_methods = helpers[begin:end]
        wrapper = work / "ActualFrameOwnerState.cs"
        wrapper.write_text('''using System; using System.Text; using System.Collections.Generic;
using System.Security.Cryptography; using System.Web.Script.Serialization;
internal sealed class BaselineEntity { public string Handle; public string Metadata; }
internal sealed class BaselineTransaction { public string ProjectRecord; public int ProjectReads; public int OwnerReads; }
internal static class LayoutGeometryGuard { internal sealed class Result { public bool Ok; public string Reason, Revision, Fingerprint, LayoutDigest; } }
internal static class ActualFrameOwnerState {
internal static LayoutGeometryGuard.Result Get(BaselineTransaction t,BaselineEntity e){ return FrameOwnerState(t,e); }
private static string Read(BaselineTransaction t,BaselineEntity e,string key){t.OwnerReads++;if(key!="ATFRAME")throw new Exception(key);return e.Metadata;}
private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer();}
''' + method.replace("Transaction tr, Entity e", "BaselineTransaction tr, BaselineEntity e") + hash_methods + "}\n", encoding="utf-8")
        sources.append(wrapper)
        executable = work / "Baseline.exe"
        compiled = compile_probe(sources, ["System.Web.Extensions"], executable)
        if compiled.returncode:
            raise RuntimeError(compiled.stdout + compiled.stderr)
        catalog = json.loads(original("Common/catalogs/vector1_2015_type1_historical.json"))
        selection = {"schema": "aframe_solution_selection/1", "catalog_id": catalog["catalog_id"],
            "catalog_revision": catalog["revision"], "solution_id": catalog["solution"]["solution_id"],
            "source_id": catalog["source"]["source_id"], "source_sha256": catalog["source"]["sha256"],
            "node": catalog["solution"]["node"],
            "bracket": {"family_id": "kr2", "execution": "galvanized_painted", "nominal_width_mm": 70, "L_mm": 200},
            "extender": {"family_id": "uk", "execution": "galvanized_painted", "nominal_width_mm": 70, "L_mm": 100, "thickness_mm": 1.2},
            "profile": {"family_id": "gp", "execution": "galvanized_painted", "a_mm": 40, "b_mm": 40,
                        "thickness_mm": 1.2, "b_basis": "project_declared", "thickness_basis": "project_declared"},
            "geometry": {"cladding_front_offset_mm": None,
                "cladding_offset_basis": catalog["geometry_parameters"]["cladding_offset_basis"], "insulation_layers_mm": []}}
        infile, outfile = work / "selection.json", work / "observations.json"
        infile.write_text(json.dumps(selection, ensure_ascii=False), encoding="utf-8")
        subprocess.run(command(executable) + [str(infile), str(outfile)], check=True)
        receipt = json.loads(outfile.read_text(encoding="utf-8"))
        receipt.update({"baseline_commit": revision, "source_sha256": hashes,
            "probe_sha256": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (Path(__file__), Path(__file__).with_suffix(".cs"))},
            "scope": "Pinned actual native settings, selection context and extracted FrameOwnerState; synthetic user dimensions, CAD storage doubled; no product edits"})
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("2B1 baseline:", len(receipt["observations"]), "observations REPRODUCED at", revision)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
