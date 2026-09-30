"""Exercise the exact two Facades erase methods with a minimal in-memory CAD double.

This verifies deletion decisions on copied JSON metadata, not AutoCAD COPY itself.
Real AutoCAD clone/Undo/transaction integration still requires a live test.
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
class Handle { public long Value; public Handle(long value){Value=value;} }
enum OpenMode { ForRead, ForWrite }
class ObjectId {
    public Entity Item;
    public bool IsNull { get { return Item==null; } }
    public bool IsErased { get { return Item!=null && Item.Erased; } }
}
class Entity {
    static int next;
    public string Layer; public bool Erased; public ObjectId ObjectId;
    public string Handle=(++next).ToString("X");
    public Dictionary<string,string> Data=new Dictionary<string,string>();
    public Dictionary<string,List<ObjectId>> Refs=new Dictionary<string,List<ObjectId>>();
    public Entity(string layer){Layer=layer;ObjectId=new ObjectId{Item=this};}
    public void Erase(){Erased=true;}
}
class Polyline:Entity {public Polyline(string layer):base(layer){}}
class Transaction { public object GetObject(ObjectId id, OpenMode mode){return id.Item;} }
class Database {}
static class Subject {
    static string LinesKey="ATFZONE_LINES", ParapetKey="ATFZONE_PARAPET", SchemeOwnerKey="ATFZONE_SCHEME_OWNER";
    static string[] SchemeLayers={"Оконные откосы","Оконные отливы","Примыкание к витражам"};
    static string LayerParapet="Парапет";
    static string ReadZoneData(Transaction tr,Entity e,string key){return e.Data.ContainsKey(key)?e.Data[key]:null;}
    static List<ObjectId> ReadZoneRefs(Transaction tr,Entity e,string key){return e.Refs.ContainsKey(key)?e.Refs[key]:new List<ObjectId>();}
    static string SafeStr(object o){return Convert.ToString(o);}
    static string Hnd(Entity e){return e.Handle;}
    static object Get(Dictionary<string,object> d,string k){return d!=null && d.ContainsKey(k)?d[k]:null;}
    // Below: exact product methods. Only AutoCAD persistence/API is represented by doubles.
__METHODS__
    static int fails=0;
    static void Check(bool ok,string msg){Console.WriteLine((ok?"OK ":"FAIL ")+msg);if(!ok)fails++;}
    static void Own(Entity parent,string key,params Entity[] children) {
        parent.Data[key]="{\"schema\":3,\"id\":\"old-id\",\"top_m\":5}";
        parent.Refs[key]=new List<ObjectId>();
        foreach(var child in children) {
            parent.Refs[key].Add(child.ObjectId);
            child.Data[SchemeOwnerKey]="{\"schema\":3,\"kind\":\""+key+"\"}";
            child.Refs[SchemeOwnerKey]=new List<ObjectId>{parent.ObjectId};
        }
    }
    static int Main() {
        var db=new Database(); var tr=new Transaction();var ser=new JavaScriptSerializer();
        var copiedOpening=new Polyline("Контур окон");copiedOpening.Data[LinesKey]="[\"10\",\"11\"]";
        Check(EraseOldLines(tr,db,ser,copiedOpening)==0,"legacy copied opening cannot delete originals");
        Check(NeedsSchemeReset(tr,copiedOpening,LinesKey),"legacy opening requires explicit migration");
        var copiedParapet=new Polyline("Контур парапета");copiedParapet.Data[ParapetKey]="{\"id\":\"A\",\"lines\":[\"20\"]}";
        Check(EraseOldParapetLines(tr,db,ser,copiedParapet)==0,"legacy copied parapet cannot delete original");
        Check(NeedsSchemeReset(tr,copiedParapet,ParapetKey),"legacy parapet requires explicit migration");
        var orig=new Polyline("Контур окон");var slope=new Polyline("Оконные откосы");var sill=new Polyline("Оконные отливы");
        Own(orig,LinesKey,slope,sill);
        var copy=new Polyline("Контур окон");copy.Data[LinesKey]=orig.Data[LinesKey];copy.Refs[LinesKey]=orig.Refs[LinesKey];
        Check(EraseOldLines(tr,db,ser,copy)==0 && !slope.Erased && !sill.Erased,"COPY contour alone preserves 2 original lines");
        var clonedSlope=new Polyline("Оконные откосы");var clonedSill=new Polyline("Оконные отливы");
        Own(copy,LinesKey,clonedSlope,clonedSill);
        Check(EraseOldLines(tr,db,ser,copy)==2 && !slope.Erased && !sill.Erased,"COPY complete set replaces only its 2 cloned lines");
        Check(EraseOldLines(tr,db,ser,orig)==2,"ordinary regeneration replaces its 2 owned lines");
        var para=new Polyline("Контур парапета");var top=new Polyline("Парапет");Own(para,ParapetKey,top);
        var fromRoot=ser.DeserializeObject(ReadParapetData(tr,para)) as Dictionary<string,object>;
        var fromLine=ser.DeserializeObject(ReadParapetData(tr,top)) as Dictionary<string,object>;
        Check(SafeStr(Get(fromRoot,"id"))==SafeStr(Get(fromLine,"id")) && Convert.ToDouble(Get(fromLine,"top_m"))==5,
            "parapet root and visible line resolve same 5 m owner (deduplication)");
        var loneLine=new Polyline("Парапет");loneLine.Data[SchemeOwnerKey]=top.Data[SchemeOwnerKey];loneLine.Refs[SchemeOwnerKey]=top.Refs[SchemeOwnerKey];
        Check(ReadParapetData(tr,loneLine)==null,"COPY visible line alone cannot borrow original parapet quantity");
        var paraClone=new Polyline("Контур парапета");paraClone.Data[ParapetKey]=para.Data[ParapetKey];paraClone.Refs[ParapetKey]=para.Refs[ParapetKey];
        Check(EraseOldParapetLines(tr,db,ser,paraClone)==0 && !top.Erased,"COPY parapet contour alone preserves original top");
        var cloneTop=new Polyline("Парапет");Own(paraClone,ParapetKey,cloneTop);
        Check(EraseOldParapetLines(tr,db,ser,paraClone)==1 && !top.Erased,"COPY complete parapet replaces only clone");
        Check(EraseOldParapetLines(tr,db,ser,para)==1,"ordinary parapet regeneration deletes its own line");
        var wrong=new Polyline("Пользовательский слой");var protectedOwner=new Polyline("Контур окон");Own(protectedOwner,LinesKey,wrong);
        Check(EraseOldLines(tr,db,ser,protectedOwner)==0 && !wrong.Erased,"unrelated layer is never erased");
        var resetLine=new Polyline("Оконные откосы");var sources=new HashSet<ObjectId>{orig.ObjectId};
        Check(CanResetErase(tr,sources,resetLine),"RESET accepts explicitly selected old scheme line");
        Check(!CanResetErase(tr,sources,orig),"RESET never erases selected source contour");
        Check(!CanResetErase(tr,sources,para),"RESET never erases another source parapet on scheme layer");
        Check(!CanResetErase(tr,sources,wrong),"RESET never erases object on unrelated layer");
        Console.WriteLine("Scope: real product methods + CAD doubles; live AutoCAD COPY not executed.");
        return fails==0?0:1;
    }
}
'''



def extract(source, name):
    import re
    found=re.search(r"        (?:private|internal) static [^\n]+ "+re.escape(name)+r"\(",source)
    if not found:
        raise ValueError("method missing: "+name)
    start=found.start()
    brace = source.index("{", start)
    depth=1
    end=brace+1
    while depth:
        if source[end]=="{":depth+=1
        elif source[end]=="}":depth-=1
        end+=1
    return source[start:end]


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--out",type=Path)
    args=parser.parse_args()
    if not shutil.which("mcs") or not shutil.which("mono"):
        print("BLOCKED: mono/mcs unavailable")
        return 2
    out=args.out or Path(tempfile.mkdtemp(prefix="facades_copy_"))
    out.mkdir(parents=True,exist_ok=True)
    product=(ROOT/"Facades/src/AFacadesPlugin/ZoneCommand.cs").read_text(encoding="utf-8")
    methods="\n".join(extract(product,n) for n in ("EraseOldLines","EraseOldParapetLines","EraseOwnedLines","NeedsSchemeReset","GeneratedOwnerIs","ReadParapetData","CanResetErase"))
    source=out/"CopyMethods.cs"
    source.write_text(STUBS.replace("__METHODS__",methods),encoding="utf-8")
    exe=out/"CopyMethods.exe"
    subprocess.run(["mcs","-out:"+str(exe),"-r:System.Web.Extensions.dll",str(source)],check=True)
    return subprocess.run(["mono",str(exe)]).returncode


if __name__=="__main__":
    raise SystemExit(main())
