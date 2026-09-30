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
enum OpenMode { ForWrite }
class ObjectId {
    public Entity Item;
    public bool IsNull { get { return Item==null; } }
    public bool IsErased { get { return Item!=null && Item.Erased; } }
}
class Entity {
    public string Layer; public string Saved; public bool Erased; public ObjectId ObjectId;
    public Entity(string layer, string saved=null) {Layer=layer;Saved=saved;ObjectId=new ObjectId{Item=this};}
    public void Erase(){Erased=true;}
}
class Transaction { public object GetObject(ObjectId id, OpenMode mode){return id.Item;} }
class Database {
    public Dictionary<long,Entity> Entities=new Dictionary<long,Entity>();
    public ObjectId GetObjectId(bool create,Handle handle,int zero){return Entities[handle.Value].ObjectId;}
}
static class Subject {
    static string LinesKey="ATFZONE_LINES", ParapetKey="ATFZONE_PARAPET";
    static string[] SchemeLayers={"Оконные откосы","Оконные отливы","Примыкание к витражам"};
    static string LayerParapet="Парапет";
    static string ReadZoneData(Transaction tr,Entity e,string key){return e.Saved;}
    static string SafeStr(object o){return Convert.ToString(o);}
    static object Get(Dictionary<string,object> d,string k){return d!=null && d.ContainsKey(k)?d[k]:null;}
    // The methods below are inserted verbatim from the reviewed product source.
__METHODS__
    static int Main() {
        var db=new Database(); var tr=new Transaction();var ser=new JavaScriptSerializer();
        var originalSlope=new Entity("Оконные откосы");var originalSill=new Entity("Оконные отливы");
        db.Entities[0x10]=originalSlope;db.Entities[0x11]=originalSill;
        // Text chunks in a copied Xrecord still contain the original hexadecimal handles.
        var copiedOpening=new Entity("Контур окон","[\"10\",\"11\"]");
        int n=EraseOldLines(tr,db,ser,copiedOpening);
        Console.WriteLine("copied opening: original lines erased="+n+"; expected 0");
        var originalParapetLine=new Entity("Парапет");db.Entities[0x20]=originalParapetLine;
        var copiedParapet=new Entity("Контур парапета","{\"id\":\"A\",\"lines\":[\"20\"]}");
        int p=EraseOldParapetLines(tr,db,ser,copiedParapet);
        Console.WriteLine("copied parapet: original lines erased="+p+"; expected 0");
        Console.WriteLine("Scope: real product erase methods + in-memory CAD API; AutoCAD COPY not executed.");
        return n==0 && p==0 ? 0:1;
    }
}
'''


def extract(source, name):
    start = source.index("        private static int " + name + "(")
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
    methods="\n".join(extract(product,n) for n in ("EraseOldLines","EraseOldParapetLines"))
    source=out/"CopyMethods.cs"
    source.write_text(STUBS.replace("__METHODS__",methods),encoding="utf-8")
    exe=out/"CopyMethods.exe"
    subprocess.run(["mcs","-out:"+str(exe),"-r:System.Web.Extensions.dll",str(source)],check=True)
    return subprocess.run(["mono",str(exe)]).returncode


if __name__=="__main__":
    raise SystemExit(main())
