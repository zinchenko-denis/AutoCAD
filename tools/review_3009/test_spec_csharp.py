"""Compile actual pure C# methods in a console harness; no AutoCAD/UI emulation.
Run after sourcing review_work/deps/env.sh. Reports actual source behaviour.
Outputs live only in scratch review_work/spec, never in protected modules.
"""
from pathlib import Path
import subprocess
ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT.parent / "review_work/spec"
OUT.mkdir(parents=True, exist_ok=True)

def method(path, signature):
    s = (ROOT / path).read_text()
    a = s.index(signature)
    b = s.index("{", a)
    depth = 1
    j = b + 1
    while depth:
        depth += (s[j] == "{") - (s[j] == "}")
        j += 1
    return s[a:j]

pieces = [method("ATableSpec/src/AtSpecPlugin/ReportCommand.cs", "internal static string NumClean("),
          method("ATableSpec/src/AtSpecPlugin/ReportReactor.cs", "public static string ComputeShape("),
          method("ABlockGen/src/ABlockGenPlugin/VitrageForm.cs", "internal static double ParseD(")]
source = r'''using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
public class Probe {
    public class SectionView {
        public string Title = "Стойки";
        public IList Rows = new object[] { new object[] { "С1", 2 }};
        public bool HideHeader;
        public List<int[]> Merges = new List<int[]>();
    }
''' + "\n".join(pieces) + r'''
    public static int Main() {
        foreach(var culture in new[] {"ru-RU", "en-US", "de-DE"}) {
            Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
            string raw = Convert.ToString(1499.99999999998, CultureInfo.InvariantCulture);
            string cleaned = NumClean(raw);
            double comma = ParseD("1500,25", "length");
            double dot = ParseD("1500.25", "length");
            Console.WriteLine(culture + ": raw=" + raw + "; cleaned=" + cleaned + "; commaEqualsDot=" + (comma == dot));
            if(cleaned != "1500" || comma != 1500.25 || comma != dot) return 1;
        }
        var a = new SectionView(); var c = new SectionView(); c.Merges.Add(new[] {0,1});
        string sa = ComputeShape(3, new List<SectionView> {a});
        string sc = ComputeShape(3, new List<SectionView> {c});
        Console.WriteLine("header merges changed: shape before="+sa+"; after="+sc+"; equal="+(sa==sc));
        Console.WriteLine("negative dimension parser accepts="+ParseD("-50", "body"));
        return 0;
    }
}
'''
(OUT/"PureCSharpProbe.cs").write_text(source)
r = subprocess.run(["mcs", "-out:"+str(OUT/"PureCSharpProbe.exe"), str(OUT/"PureCSharpProbe.cs")],capture_output=True,text=True)
print(r.stdout+r.stderr,end="")
if r.returncode: raise SystemExit(r.returncode)
r = subprocess.run(["mono",str(OUT/"PureCSharpProbe.exe")],capture_output=True,text=True)
print(r.stdout+r.stderr,end="")
(OUT/"pure_csharp.log").write_text(r.stdout+r.stderr)
raise SystemExit(r.returncode)
