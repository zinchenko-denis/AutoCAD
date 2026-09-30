"""Exercise actual FrameQuantities constructor/Store at the CAD write boundary.

The producer methods are extracted unchanged. Only native CAD classes and the
final persistence call are doubles. This does not execute a live AutoCAD DWG.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
from probe_runtime import available, command, compile_probe

FIELDS = """
private readonly QuantityReport report;
private readonly Dictionary<string,List<QuantityElement>> elements=new Dictionary<string,List<QuantityElement>>();
private readonly Dictionary<string,object[]> inputs=new Dictionary<string,object[]>();
private readonly List<QuantityReport> retained=new List<QuantityReport>();
private string unavailable;
internal void Unavailable(string reason){if(unavailable==null)unavailable=reason;}
internal static void CheckCancel(){}
internal void Poll(){}
"""

PROBE = r'''
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;
using AFramePlugin;
namespace AFramePlugin { class Transaction {} class Database {} class Entity {} }
namespace FacadeSafety
{
    class QuantitySelection
    {
        public bool Ok = true;
        public string Reason = "";
        public List<string> SelectedZoneIds = new List<string>();
        public List<QuantityReport> Reports = new List<QuantityReport>();
    }
    static class FacadeQuantityStore
    {
        public class FrameSources {}
        public static QuantityReport Saved;
        public static string Unavailable;
        public static void MarkFrameUnavailable(Transaction t, Database d, IEnumerable<Entity> owners, string reason)
        { Unavailable = reason; }
        public static void StoreFrame(Transaction t, Database d, IEnumerable<Entity> owners, QuantityReport report, FrameSources sources)
        { Saved = report; }
    }
}
class BoundaryProbe
{
    static readonly List<object> Results = new List<object>();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    static QuantityReport Old(string id, bool manual)
    {
        var r = new QuantityReport { kind = "frame", document_id = "D", run_id = id, report_id = id,
            zone_ids = new List<string> { "Z" }, scope = manual ? "manual_zone_contribution" : "whole_frame_run",
            algorithm = manual ? "manual-import/1" : "ATFRAME/facade_quantities/1" };
        r.elements.Add(new QuantityElement { element_id = id + ":rails:0", role = "rail", zone_id = "Z",
            zone_ids = new List<string> { "Z" }, length_mm = 3000, piece_kind = "segment", mark = "fixture",
            origin = manual ? "manual:linear" : "generated:ATFRAME:rails",
            cad_entities = new List<QuantityCadEntity> { new QuantityCadEntity { handle = id, fingerprint = "unchanged", role = "outer" } } });
        return r;
    }
    static void Store(params QuantityReport[] reports)
    {
        FacadeQuantityStore.Saved = null; FacadeQuantityStore.Unavailable = null;
        var previous = new QuantitySelection(); previous.SelectedZoneIds.Add("Z"); previous.Reports.AddRange(reports);
        var response = new Dictionary<string, object> { { "ok", true },
            { "per_zone", new object[] { new Dictionary<string, object> { { "zone_id", "Z" } } } } };
        foreach (string key in new[] { "rails", "hrails", "brackets", "clamps", "fittings" }) response[key] = new object[0];
        var producer = new FrameQuantities(response, new Dictionary<string, object> { { "parts", "clamps" } },
            new Dictionary<string, string>(), true, previous);
        producer.Store(new Transaction(), new Database(), new Entity[0], new FacadeQuantityStore.FrameSources(), new HashSet<string>());
    }
    static double Bill(params QuantityReport[] reports)
    {
        var result = FacadeQuantitiesCore.BuildRows(reports, new[] { "Z" }, true, false);
        Need(result.ok, "Normal bill refused: " + Json.Serialize(result.issues));
        return result.rows.Sum(x => x.quantity);
    }
    static void Check(string name, Action action)
    { action(); Results.Add(new { name, status = "PASS" }); }
    static int Main()
    {
        Check("normal mixed bill counts generated and manual", delegate {
            Need(Bill(Old("generated", false), Old("manual", true)) == 2, "manual lost from normal bill");
        });
        Check("clamps update persists only generated contribution", delegate {
            var generated = Old("generated", false); var manual = Old("manual", true);
            string beforeManual = Json.Serialize(manual);
            Store(generated, manual);
            Need(FacadeQuantityStore.Unavailable == null && FacadeQuantityStore.Saved != null, "valid mixed selection refused");
            Need(FacadeQuantityStore.Saved.elements.Count == 1 && FacadeQuantityStore.Saved.elements[0].element_id == "generated:rails:0",
                "manual adopted into generated StoreFrame");
            Need(beforeManual == Json.Serialize(manual), "manual contribution mutated");
            FacadeQuantityStore.Saved.document_id = "D";
            Need(Bill(FacadeQuantityStore.Saved, manual) == 2, "normal bill after clamps loses or duplicates a contribution");
        });
        Check("manual-only selection cannot become generated frame", delegate {
            Store(Old("manual", true));
            Need(FacadeQuantityStore.Saved == null && !string.IsNullOrEmpty(FacadeQuantityStore.Unavailable), "manual-only adopted");
        });
        Check("contaminated generated origin refused", delegate {
            var old = Old("generated", false); old.elements.Add(Old("manual", true).elements[0]);
            Store(old);
            Need(FacadeQuantityStore.Saved == null && !string.IsNullOrEmpty(FacadeQuantityStore.Unavailable), "mixed generated report adopted");
        });
        Check("contaminated manual scope refused", delegate {
            var old = Old("manual", true); old.elements.Add(Old("generated", false).elements[0]);
            Store(Old("valid", false), old);
            Need(FacadeQuantityStore.Saved == null && !string.IsNullOrEmpty(FacadeQuantityStore.Unavailable), "mixed manual report silently skipped");
        });
        Check("unrecognized report producer refused", delegate {
            var old = Old("generated", false); old.algorithm = "other-producer/1";
            Store(old);
            Need(FacadeQuantityStore.Saved == null && !string.IsNullOrEmpty(FacadeQuantityStore.Unavailable), "foreign producer adopted");
        });
        Check("valid generated reports retain independent origins", delegate {
            Store(Old("first", false), Old("second", false));
            Need(FacadeQuantityStore.Unavailable == null && FacadeQuantityStore.Saved != null, "valid generated inputs refused");
            Need(new HashSet<string>(FacadeQuantityStore.Saved.elements.Select(x => x.element_id)).SetEquals(new[] { "first:rails:0", "second:rails:0" }), "generated identities changed");
        });
        Console.WriteLine(Json.Serialize(new { status = "PASS", checks = Results.Count, cases = Results }));
        return 0;
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: native .NET compiler/runtime unavailable")
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix="clamps_manual_boundary_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    producer = ROOT / "AFrame/src/AFramePlugin/FrameQuantities.cs"
    core = ROOT / "Common/FacadeQuantities.cs"
    source = producer.read_text(encoding="utf-8")
    ctor_start, ctor_end = "        internal FrameQuantities(", "        internal void Unavailable("
    store_start, store_end = "        internal void Store(", "        // Expected visibility mapping"
    tail_start = "        internal static QuantityReport BuildReport("
    for marker in (ctor_start, ctor_end, store_start, store_end, tail_start):
        assert source.count(marker) == 1, "Actual producer extraction needs review: " + marker
    constructor = source[source.index(ctor_start):source.index(ctor_end)]
    store = source[source.index(store_start):source.index(store_end)]
    tail = source[source.index(tail_start):]
    prefix = "\n".join(line for line in source.splitlines() if line.startswith("using ") and "Autodesk." not in line)
    categories = re.findall(r"(?m)^\s*private static readonly string\[\] Categories = .*?;", source)
    assert len(categories) == 1
    extracted, driver = out / "FrameQuantitiesBoundary.cs", out / "BoundaryProbe.cs"
    extracted.write_text(prefix + "\nnamespace AFramePlugin { internal sealed class FrameQuantities {\n" +
                         categories[0] + FIELDS + constructor + store + tail, encoding="utf-8")
    driver.write_text(PROBE, encoding="utf-8")
    executable = out / "BoundaryProbe.exe"
    compiled = compile_probe([driver, extracted, core], ["System.Web.Extensions"], executable)
    (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    run = subprocess.run(command(executable), capture_output=True, text=True)
    (out / "probe.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    if run.returncode:
        print(run.stdout + run.stderr)
        return 1
    result = json.loads(run.stdout)
    result.update(live_autocad_checked=False,
        scope="Actual constructor and Store method; only CAD persistence boundary is captured. No native CAD mutation.",
        before_evidence={"base_commit": "0a397c24d49474f4408fcda7e30da71e163202b0",
            "pre_fix_working_source_sha256": "783c672c0a0244758fb8de8b0c5de5cbc3cfe7dcaed62dd4247fe7febf5aa928",
            "observation": "captured_for_generated_StoreFrame=generated:rail,manual:rail; manual_copied_into_generated=True"},
        source_sha256={str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
                       for path in (producer, core, Path(__file__).resolve())},
        extracted_methods_sha256=hashlib.sha256((constructor + store + tail).encode("utf-8")).hexdigest())
    (out / "manifest.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Clamps/manual write-boundary checks:", result["checks"], "PASS; CAD persistence capture only")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
