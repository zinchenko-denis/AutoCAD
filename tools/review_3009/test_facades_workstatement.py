"""Run real WorkStatement C# with independent quantity assertions (requires mono/mcs).

The stock fixtures and product C# are not changed. Scratch outputs are placed in
--out (default temporary directory). At e2a4e4d the stock smoke check succeeds,
but a reused template with one insulation group retains stale quantities.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

import openpyxl

ROOT = Path(__file__).resolve().parents[2]
HARNESS = r'''
using System;
using System.IO;
using System.Collections.Generic;
using AFacadesPlugin;
static class IndependentVedomost {
    static WorkStatement.Group G(string layer, double area) {
        var g = new WorkStatement.Group { Layer=layer };
        g.Reports.Add(new Dictionary<string,object>{{"area_net_m2",area},{"window_count",0}});
        return g;
    }
    static void Main(string[] a) {
        var stock = WorkStatement.LoadConfig(Path.Combine(a[0],"vent.json"));
        WorkStatement.Build(Path.Combine(a[0],"vent.xlsx"),stock,
            new List<WorkStatement.Group>{G("НВФ утеплитель 150 мм",1000),G("НВФ без толщины",250)},
            0,Path.Combine(a[1],"unknown_thickness.xlsx"));
        var reused = WorkStatement.LoadConfig(Path.Combine(a[1],"vent2.json"));
        WorkStatement.Build(Path.Combine(a[1],"vent_two.xlsx"),reused,
            new List<WorkStatement.Group>{G("НВФ утеплитель 150 мм",300)},
            0,Path.Combine(a[1],"reused_single.xlsx"));
    }
}
'''


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", type=Path)
    args = ap.parse_args()
    if not shutil.which("mcs") or not shutil.which("mono"):
        print("BLOCKED: mono/mcs unavailable; no C# execution claimed")
        return 2
    out = args.out or Path(tempfile.mkdtemp(prefix="facades_review_"))
    out.mkdir(parents=True, exist_ok=True)
    tpl = ROOT / "Facades/bundle/AFacades.bundle/Contents/templates"
    product = ROOT / "Facades/src/AFacadesPlugin/WorkStatement.cs"
    flags = ["-r:System.Web.Extensions.dll", "-r:System.IO.Compression.dll", "-r:System.Xml.dll"]
    smoke = out / "StockCheck.exe"
    subprocess.run(["mcs", "-out:"+str(smoke), *flags,
                    str(ROOT/"Facades/tools/vedomost/Check.cs"), str(product)], check=True)
    subprocess.run(["mono", str(smoke), str(tpl), str(out)], check=True,
                   stdout=subprocess.DEVNULL)
    source = out / "IndependentVedomost.cs"
    source.write_text(HARNESS, encoding="utf-8")
    executable = out / "IndependentVedomost.exe"
    subprocess.run(["mcs", "-out:"+str(executable), *flags, str(source), str(product)], check=True)
    subprocess.run(["mono", str(executable), str(tpl), str(out)], check=True)
    results = {}
    for name in ("unknown_thickness", "reused_single"):
        ws = openpyxl.load_workbook(out/(name+".xlsx")).worksheets[0]
        results[name] = {a:ws[a].value for a in ("D3","B5","D5","B6","D6")}
    print(json.dumps(results, ensure_ascii=False, indent=2))
    (out/"independent_results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2),encoding="utf-8")
    fails=[]
    if results["reused_single"]["D5"] not in (300,"=D3"):
        fails.append("one 150 mm group has area 300 m2, but its cell D5 remains stale")
    if "150" in results["unknown_thickness"]["B6"]:
        fails.append("unknown-thickness group was labelled 150 mm without input evidence")
    for message in fails:
        print("FAIL:", message)
    return int(bool(fails))


if __name__ == "__main__":
    raise SystemExit(main())
