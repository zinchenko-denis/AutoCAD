using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using FacadeSafety;

internal static class QuantityLegacyCompatibilityProbe
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
    public sealed class Record {public string handle{get;set;} public List<Value> values{get;set;}}
    public sealed class Value {public int code{get;set;}public string value{get;set;}}
    public static int Main(string[] args)
    {
        var binding=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
        typeof(QuantityStoreCheck).GetField("Fixtures",binding).SetValue(null,
            (Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(args[1])));
        var type=typeof(QuantityStoreCheck).GetNestedType("QuantityFixture",BindingFlags.NonPublic);
        var fixture=Activator.CreateInstance(type,new object[]{"single","ATTILE",true});
        var zone=(QuantityStoreCheck.Fixture)type.GetField("Zone").GetValue(fixture);
        var objects=(Dictionary<long,DBObject>)typeof(Database).GetField("objects",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(zone.Db);
        if(args[0]=="write") {
            var records=new List<Record>();
            foreach(var pair in objects.OrderBy(x=>x.Key)) {
                var record=pair.Value as Xrecord;if(record==null)continue;
                records.Add(new Record {handle=record.Handle.ToString(),values=record.Data.Select(v=>new Value{
                    code=v.TypeCode,value=v.Value is ObjectId ? ((ObjectId)v.Value).Handle.ToString() : Convert.ToString(v.Value)}).ToList()});
            }
            File.WriteAllText(args[2],Json.Serialize(records));Console.WriteLine("LEGACY WRITE PASS records="+records.Count);return 0;
        }
        var old=Json.Deserialize<List<Record>>(File.ReadAllText(args[2]));
        if(old.Count!=objects.Values.OfType<Xrecord>().Count())throw new InvalidOperationException("fixture object topology changed");
        foreach(var saved in old) {
            var id=zone.Db.GetObjectId(false,new Handle(Convert.ToInt64(saved.handle,16)),0);
            var xr=zone.Tr.GetObject(id,OpenMode.ForWrite) as Xrecord;
            if(xr==null)throw new InvalidOperationException("legacy Xrecord target mismatch");
            xr.Data=new ResultBuffer(saved.values.Select(v=>new TypedValue(v.code,v.code==(int)DxfCode.SoftPointerId ?
                (object)zone.Db.GetObjectId(false,new Handle(Convert.ToInt64(v.value,16)),0) : (object)v.value)).ToArray());
        }
        var read=FacadeQuantityStore.ReadCladding(zone.Tr,zone.Db,new[]{zone.Hatch.ObjectId,zone.Mark.ObjectId});
        if(!read.Ok || read.Reports.Count!=1 || read.Reports[0].elements.Count!=1 || read.Reports[0].kind!="cladding")
            throw new InvalidOperationException("legacy full passport read refused: "+read.Reason);
        var result=FacadeQuantitiesCore.BuildRows(read.Reports,read.SelectedZoneIds,true,false);
        if(!result.ok || result.rows.Sum(r=>r.quantity)!=1 || Math.Abs(result.rows.Sum(r=>r.area_m2??0)-0.36)>1e-9)
            throw new InvalidOperationException("legacy quantity result changed");
        Console.WriteLine("LEGACY READ PASS actual 6222041 Xrecords; 1 element, 0.36 m2; "+old.Count+" records");return 0;
    }
}
