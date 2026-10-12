"""Execute the probe's actual pure C# validators/JSON reader, without Autodesk API doubles.

This is a regression test of the measuring/reporting tool, NOT a native AutoCAD run.
"""
import argparse
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AClad/tests"))
# Reuse runtime selection, but do not use its broad method regex (it can match an inline call).
import test_dynamic_block_size
from probe_runtime import available, command, compile_probe


def method(source, name):
    match = re.search(r"(?m)^        private static [^\n{]+? " + re.escape(name) + r"\(", source)
    if not match:
        raise ValueError("Actual method missing: " + name)
    start = match.start()
    brace = source.index("{", start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def class_block(source, name):
    start = source.index("        public sealed class " + name + "\n")
    brace = source.index("{", start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


DRIVER = r'''
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
class ProbeContractCheck
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    const string Config = "config.json";
__ACTUAL_CLASSES__
__ACTUAL_METHODS__
    static int checks, failures;
    static void Check(string label, bool accepted, Action action)
    {
        bool pass = true;
        try { action(); } catch (Exception) { pass = false; }
        checks++;
        if (pass != accepted) { failures++; Console.WriteLine("FAIL " + label); }
    }
    static void Assert(bool b) { if (!b) throw new InvalidOperationException("test assertion"); }
    static Case GoodCase() { return new Case { id="fractional", mode="definition", order="production", width=563.55, height=588.25, x=1000.125, y=-42.25 }; }
    static Dictionary<string, object> RawCase() { return Json.Deserialize<Dictionary<string, object>>(Json.Serialize(GoodCase())); }
    static Dictionary<string, object> RawConfig()
    {
        return Json.Deserialize<Dictionary<string, object>>(Json.Serialize(new Configuration {
            schema_version=1, run_id="7c2a5d67-06a1-4b41-b479-88daf6368bfe", fixture_id="unit-contract",
            input_sha256=new string('a',64), source_handle="375", width_property="ширина", height_property="высота",
            outline_layer="0", tolerance=1e-6, cases=new[]{GoodCase()} }));
    }
    static void ConfigTest(string label, Action<Dictionary<string, object>> edit, bool accept)
    { var cfg=RawConfig(); edit(cfg); File.WriteAllText(Config,Json.Serialize(cfg),Encoding.UTF8); Check(label,accept,()=>ReadConfig()); }
    static double[][] Points(double z=0) { return new[]{new[]{1000.125,-42.25,z},new[]{1563.675,-42.25,z},new[]{1563.675,546.0,z},new[]{1000.125,546.0,z}}; }
    static Dictionary<string, object> Observation()
    {
        return new Dictionary<string, object> { {"actual_width",563.55},{"actual_height",588.25},
            {"position",new[]{1000.125,-42.25,0.0}},{"rotation",0.0},{"scale",new[]{1.0,1.0,1.0}},
            {"actual_properties",new object[]{new Dictionary<string,object>{{"name","ширина"},{"value",563.55},{"type","System.Double"}},
                new Dictionary<string,object>{{"name","высота"},{"value",588.25},{"type","System.Double"}}}},
            {"native_outline_vertices",Points()} };
    }
    static Dictionary<string, object> RoundTrip(Dictionary<string, object> d)
    { return Json.Deserialize<Dictionary<string, object>>(Json.Serialize(d)); }
    static int Main()
    {
        // Actual JavaScriptSerializer nested arrays bind to ArrayList on Mono; both forms are valid.
        var nested=Json.Deserialize<Dictionary<string,object>>("{\"cases\":[{\"id\":\"one\"}],\"coords\":[1,2.5]}");
        Check("deserialized array",true,()=>Assert(Arr(nested["cases"]).Length==1));
        Check("manual ArrayList",true,()=>Assert(Arr(new ArrayList{1,2}).Length==2));
        Check("object array",true,()=>Assert(Arr(new object[]{1,2}).Length==2));
        Check("string is not array",false,()=>Arr("[]"));
        Check("dictionary is not array",false,()=>Arr(nested));
        Check("null is not array",false,()=>Arr(null));
        Check("requested generic round-trip",true,()=>RequireSameCase(RawCase(),GoodCase()));
        var raw=RawCase(); raw["width"]=563.5500000000001;
        Check("request equality exact, not tolerance",false,()=>RequireSameCase(raw,GoodCase()));
        raw=RawCase();raw["width"]="563.55";
        Check("numeric string invalid",false,()=>RequireSameCase(raw,GoodCase()));
        raw=RawCase();raw.Remove("x");
        Check("missing case x",false,()=>RequireSameCase(raw,GoodCase()));
        raw=RawCase();raw["extra"]=1;
        Check("unexpected case field",false,()=>RequireSameCase(raw,GoodCase()));
        ConfigTest("valid config",c=>{},true);
        ConfigTest("missing x not default origin",c=>Dict(Arr(c["cases"])[0]).Remove("x"),false);
        ConfigTest("missing y not default origin",c=>Dict(Arr(c["cases"])[0]).Remove("y"),false);
        ConfigTest("zero tolerance",c=>c["tolerance"]=0,false);
        ConfigTest("too large tolerance",c=>c["tolerance"]=0.5,false);
        ConfigTest("negative tolerance",c=>c["tolerance"]=-1e-6,false);
        ConfigTest("nonfinite tolerance",c=>c["tolerance"]=double.NaN,false);
        ConfigTest("unknown mode",c=>Dict(Arr(c["cases"])[0])["mode"]="static",false);
        ConfigTest("unknown order",c=>Dict(Arr(c["cases"])[0])["order"]="random",false);
        ConfigTest("sample valid",c=>Dict(Arr(c["cases"])[0])["mode"]="sample",true);
        ConfigTest("width-first valid",c=>Dict(Arr(c["cases"])[0])["order"]="width_height",true);
        ConfigTest("height-first valid",c=>Dict(Arr(c["cases"])[0])["order"]="height_width",true);
        ConfigTest("invalid width",c=>Dict(Arr(c["cases"])[0])["width"]=0,false);
        ConfigTest("empty cases",c=>c["cases"]=new object[0],false);
        ConfigTest("duplicate ids",c=>c["cases"]=new[]{RawCase(),RawCase()},false);
        ConfigTest("bad sha",c=>c["input_sha256"]=new string('z',64),false);
        ConfigTest("bad handle",c=>c["source_handle"]="nothex",false);
        ConfigTest("same property names",c=>c["height_property"]="ширина",false);
        ConfigTest("empty outline",c=>c["outline_layer"]="",false);
        ConfigTest("invalid UUID",c=>c["run_id"]="run",false);
        ConfigTest("schema mismatch",c=>c["schema_version"]=2,false);
        foreach(var culture in new[]{"en-US","ru-RU","de-DE"})
        { CultureInfo.CurrentCulture=new CultureInfo(culture); ConfigTest("config culture "+culture,c=>{},true); }
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        // All 24 vertex orders; only the 8 perimeter orders satisfy the independent oracle.
        foreach(int a in Enumerable.Range(0,4))foreach(int b in Enumerable.Range(0,4))
        foreach(int c in Enumerable.Range(0,4))foreach(int d in Enumerable.Range(0,4))
        {
            var order=new[]{a,b,c,d}; if(order.Distinct().Count()!=4)continue;
            var steps=Enumerable.Range(0,4).Select(i=>(order[(i+1)%4]-order[i]+4)%4).ToArray();
            var points=Points(); bool perimeter=steps.All(v=>v==1)||steps.All(v=>v==3);
            Check("outline order "+string.Join(",",order),perimeter,()=>CheckOutline(order.Select(i=>points[i]).ToArray(),1000.125,-42.25,563.55,588.25,1e-6,0));
        }
        Check("source plane explicit",true,()=>CheckOutline(Points(-14000.125),1000.125,-42.25,563.55,588.25,1e-6,-14000.125));
        Check("source plane wrong",false,()=>CheckOutline(Points(-14000.125),1000.125,-42.25,563.55,588.25,1e-6,0));
        Check("wrong requested width",false,()=>CheckOutline(Points(),1000.125,-42.25,564,588.25,1e-6,0));
        Check("wrong requested height",false,()=>CheckOutline(Points(),1000.125,-42.25,563.55,588,1e-6,0));
        var p=Points();p[0][0]+=0.15;
        Check("wrong corner",false,()=>CheckOutline(p,1000.125,-42.25,563.55,588.25,1e-6,0));
        p=Points();p[0][0]+=1e-8;
        Check("bounded harmless float error",true,()=>CheckOutline(p,1000.125,-42.25,563.55,588.25,1e-6,0));
        p=Points();p[0][0]=double.NaN;
        Check("NaN corner",false,()=>CheckOutline(p,1000.125,-42.25,563.55,588.25,1e-6,0));
        p=Points();p[0][2]=double.PositiveInfinity;
        Check("infinite corner",false,()=>CheckOutline(p,1000.125,-42.25,563.55,588.25,1e-6,0));
        Check("three corners",false,()=>CheckOutline(Points().Take(3).ToArray(),1000.125,-42.25,563.55,588.25,1e-6,0));
        p=Points();p[1]=p[0];
        Check("duplicate corner",false,()=>CheckOutline(p,1000.125,-42.25,563.55,588.25,1e-6,0));
        var original=Observation();var saved=RoundTrip(original);
        Check("saved full JSON roundtrip",true,()=>RequireSavedObservations(saved,original,1e-6));
        saved=RoundTrip(original);saved["actual_width"]=563.55000000001;
        Check("reopen harmless numerical noise",true,()=>RequireSavedObservations(saved,original,1e-6));
        saved=RoundTrip(original);saved["actual_width"]=564;
        Check("reopen wrong width",false,()=>RequireSavedObservations(saved,original,1e-6));
        saved=RoundTrip(original);saved["actual_properties"]=Arr(saved["actual_properties"]).Reverse().ToArray();
        saved["native_outline_vertices"]=Arr(saved["native_outline_vertices"]).Reverse().ToArray();
        Check("reopen property/corner order independent",true,()=>RequireSavedObservations(saved,original,1e-6));
        saved=RoundTrip(original);Dict(Arr(saved["actual_properties"])[0])["value"]=563;
        Check("reopen property mutation",false,()=>RequireSavedObservations(saved,original,1e-6));
        saved=RoundTrip(original);Dict(Arr(saved["actual_properties"])[0])["type"]="System.Int32";
        Check("reopen property type mutation",false,()=>RequireSavedObservations(saved,original,1e-6));
        saved=RoundTrip(original);Dict(Arr(saved["actual_properties"])[0])["name"]="высота";
        Check("reopen duplicate property name",false,()=>RequireSavedObservations(saved,original,1e-6));
        saved=RoundTrip(original);saved["position"]=new[]{1000.275,-42.25,0.0};
        Check("reopen position drift",false,()=>RequireSavedObservations(saved,original,1e-6));
        Check("bool not numeric",true,()=>Assert(!Equivalent(true,1,1e-6)));
        Check("NaN never equivalent",true,()=>Assert(!Equivalent(double.NaN,double.NaN,1e-6)));
        Check("infinity never equivalent",true,()=>Assert(!Equivalent(double.PositiveInfinity,double.PositiveInfinity,1e-6)));
        Console.WriteLine("Core probe helpers: "+checks+" checks, "+failures+" failures; actual C# and JSON, native execution NOT_RUN.");
        return failures==0?0:1;
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: .NET compiler/runtime absent; native execution NOT_RUN")
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix="core_probe_contract_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    source = Path(__file__).with_name("Probe.cs").read_text(encoding="utf-8")
    names = ("ReadConfig", "CheckOutline", "Require", "Finite", "Arr", "Dict", "Get", "Str", "Numeric", "Equivalent", "RequireSameCase", "RequireSavedObservations")
    actual = "\n".join(method(source, name) for name in names)
    classes = "\n".join(class_block(source, name) for name in ("Configuration", "Case"))
    cs, exe = out / "ProbeContractCheck.cs", out / "ProbeContractCheck.exe"
    cs.write_text(DRIVER.replace("__ACTUAL_METHODS__", actual).replace("__ACTUAL_CLASSES__", classes), encoding="utf-8")
    result = compile_probe([cs], ["System.Web.Extensions"], exe)
    if result.returncode:
        print(result.stdout + result.stderr)
        return 1
    return subprocess.run(command(exe), cwd=out).returncode


if __name__ == "__main__":
    raise SystemExit(main())
