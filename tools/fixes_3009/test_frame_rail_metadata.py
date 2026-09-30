"""Execute the actual C# rail metadata writer/reader with minimal CAD doubles.

Tests short rails, translation/COPY semantics, horizontal discrimination and
explicit rejection after shape/rotation edits. Does not claim an AutoCAD run.
"""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
STUBS = r'''
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
class Point { public double X,Y; public Point(double x,double y){X=x;Y=y;} }
class Extents3d { public Point MinPoint,MaxPoint; public Extents3d(double x0,double y0,double x1,double y1){MinPoint=new Point(x0,y0);MaxPoint=new Point(x1,y1);} }
class Entity { public Extents3d GeometricExtents; public string Saved; }
class Scale { public double X=1,Y=1,Z=1; }
class BlockReference:Entity { public double Rotation; public Scale ScaleFactors=new Scale(); }
class Transaction { }
class Subject {
    static string XKeyRail="ATFRAME_RAIL";
    static string ReadData(Transaction tr,Entity e,string key){return e.Saved;}
    static void StoreData(Transaction tr,Entity e,string json,string key){e.Saved=json;}
    static object Get(Dictionary<string,object> d,string k){return d!=null&&d.ContainsKey(k)?d[k]:null;}
    static string SafeStr(object value){return Convert.ToString(value);}
    static double ToD(object value){return Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture);}
__METHODS__
    static int errors=0, checks=0;
    static void Check(bool pass,string label){checks++;if(!pass){errors++;Console.WriteLine("FAIL "+label);}}
    static void MainCheck() {
        var tr=new Transaction();
        var source=new Dictionary<string,object>{{"x",300.0},{"y0",3010.0},{"y1",3150.0},{"clamp_role","window"}};
        var shortRail=new Entity{GeometricExtents=new Extents3d(270,3010,330,3150)};
        StoreRailRole(tr,shortRail,source,true);
        var read=ReadRailRole(tr,shortRail);
        Check(ToD(read["x"])==300 && ToD(read["y0"])==3010 && ToD(read["y1"])==3150,"140mm rail survives60mm width");
        Check(SafeStr(read["clamp_role"])=="window","window role survives");
        // COPY translates extents, while the copied JSON offsets stay local.
        shortRail.GeometricExtents=new Extents3d(370,3210,430,3350);
        read=ReadRailRole(tr,shortRail);
        Check(ToD(read["x"])==400 && ToD(read["y0"])==3210 && ToD(read["y1"])==3350,"translated copy uses current geometry");
        shortRail.GeometricExtents=new Extents3d(370,3210,430,3360);
        bool rejected=false;try{ReadRailRole(tr,shortRail);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"changed length explicitly rejected");
        StoreRailRole(tr,shortRail,source,false);
        Check(ReadRailRole(tr,shortRail).Count==0,"horizontal cannot become vertical");
        var block=new BlockReference{GeometricExtents=new Extents3d(270,3010,330,3150)};
        StoreRailRole(tr,block,source,true);block.Rotation=Math.PI;
        rejected=false;try{ReadRailRole(tr,block);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"rotated block explicitly rejected");
        var old=new Entity{GeometricExtents=new Extents3d(270,3010,330,3150)};
        Check(ReadRailRole(tr,old)==null,"old metadata routed to legacy handling");
    }
    static int Main(){MainCheck();Console.WriteLine("native rail metadata: "+checks+" checks, "+errors+" failures; CAD doubles, not AutoCAD");return errors==0?0:1;}
}
'''


def extract(source, name):
    start = source.rfind("        private static ", 0, source.index(name))
    if name not in source[start:source.index("{", start)]:
        raise ValueError("method signature not found: " + name)
    brace = source.index("{", start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not shutil.which("mcs") or not shutil.which("mono"):
        print("BLOCKED: mono/mcs unavailable")
        return 2
    out = args.out or Path(tempfile.mkdtemp(prefix="frame_rail_meta_"))
    out.mkdir(parents=True, exist_ok=True)
    product = (ROOT / "AFrame/src/AFramePlugin/FrameCommand.cs").read_text(encoding="utf-8")
    methods = "\n".join(extract(product[product.index("        private static void StoreRailRole"):], name)
                        for name in ("StoreRailRole", "ReadRailRole"))
    src, exe = out / "RailMeta.cs", out / "RailMeta.exe"
    src.write_text(STUBS.replace("__METHODS__", methods), encoding="utf-8")
    subprocess.run(["mcs", "-out:" + str(exe), "-r:System.Web.Extensions.dll", str(src)], check=True)
    return subprocess.run(["mono", str(exe)]).returncode


if __name__ == "__main__":
    raise SystemExit(main())
