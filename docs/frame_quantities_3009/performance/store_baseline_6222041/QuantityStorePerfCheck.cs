// Scratch benchmark: real 6222041 production code, explicit instrumented CAD doubles.
// Numbers are managed adapter timings/operation counts, not AutoCAD wall times.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;

internal static class QuantityStorePerfCheck
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly List<Dictionary<string,object>> Phases = new List<Dictionary<string,object>>();
    private static void Require(bool value,string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Write(Transaction tr,Entity e,string key,string text) {
        if (e.ExtensionDictionary.IsNull) e.CreateExtensionDictionary();
        var ext = (DBDictionary)tr.GetObject(e.ExtensionDictionary,OpenMode.ForWrite);
        var xr = new Xrecord { Data = new ResultBuffer(new TypedValue((int)DxfCode.Text,text)) };
        ext.SetAt(key,xr); tr.AddNewlyCreatedDBObject(xr,true);
    }
    private static void Measure(string name,Action action) {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var memory = GC.GetTotalMemory(false);
        int gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2);
        CadCounters.Reset(); var timer=Stopwatch.StartNew(); action(); timer.Stop();
        var result=CadCounters.Snapshot(); result["phase"]=name; result["elapsed_ms"]=timer.Elapsed.TotalMilliseconds;
        result["managed_live_bytes_before"]=memory; result["managed_live_bytes_after"]=GC.GetTotalMemory(false);
        result["gen0_collections"]=GC.CollectionCount(0)-gen0;result["gen1_collections"]=GC.CollectionCount(1)-gen1;
        result["gen2_collections"]=GC.CollectionCount(2)-gen2;
        using(var process=Process.GetCurrentProcess()) { result["working_set_bytes"]=process.WorkingSet64; result["peak_working_set_bytes"]=process.PeakWorkingSet64; result["resident_memory_basis"]="Process"; }
        if(File.Exists("/proc/self/status")) {
            foreach(string line in File.ReadAllLines("/proc/self/status")) {
                string[] words=line.Split(new[]{' ','\t'},StringSplitOptions.RemoveEmptyEntries);
                if(words.Length>1 && words[0]=="VmRSS:") result["working_set_bytes"]=long.Parse(words[1])*1024;
                if(words.Length>1 && words[0]=="VmHWM:") result["peak_working_set_bytes"]=long.Parse(words[1])*1024;
            }
            result["resident_memory_basis"]="/proc/self/status VmRSS/VmHWM";
        }
        Phases.Add(result); Console.WriteLine(Json.Serialize(result)); Console.Out.Flush();
    }
    public static int Main(string[] args) {
        if(args.Length!=4) throw new ArgumentException("zones pieces_per_zone fixtures.json output.json");
        int zcount=int.Parse(args[0]), piecesPerZone=int.Parse(args[1]);
        QuantityStoreCheck.Fixtures=(Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(args[2]));
        var db=new Database();var tr=new Transaction(db);
        var zones=new List<QuantityStoreCheck.Fixture>(); var carriers=new List<Entity>();
        for(int z=0;z<zcount;z++) {
            var f=new QuantityStoreCheck.Fixture("single",db,"Z-"+z.ToString("D3"));
            Require(f.Captured.Ok,"zone fixture capture rejected: "+f.Captured.Reason);
            zones.Add(f);carriers.Add(f.Hatch);carriers.Add(f.Mark);
        }
        LayoutGeometryGuard.Snapshot snapshot=null;
        Measure("layout_capture",()=>snapshot=LayoutGeometryGuard.Capture(tr,db,new ObjectId[0],zones.Select(z=>z.Captured)));
        Measure("layout_store_all_carriers",()=> {
            foreach(var carrier in carriers) {
                Write(tr,carrier,"ATTILE","{\"joints_x\":[0,600,1200],\"rows_y\":[0,600,1200]}");
                LayoutGeometryGuard.Store(tr,db,carrier,"ATTILE",snapshot);
            }
        });
        var report=new QuantityReport {report_id="perf-report",run_id="perf-run",kind="cladding",scope="whole_layout_run",
            algorithm="synthetic-fixed-geometry",completeness="complete",engineering_coverage="geometry_only"};
        var physical=new List<Polyline>();
        for(int z=0;z<zcount;z++) {
            string zid="Z-"+z.ToString("D3"); report.zone_ids.Add(zid);
            report.source_revisions["owner_zone:"+zones[z].Hatch.Handle]=zid;
            report.source_revisions["owner_zone:"+zones[z].Mark.Handle]=zid;
            for(int i=0;i<piecesPerZone;i++) {
                double x=10000*z+600*(i%10),y=600*(i/10);
                var ring=new QuantityRing {points=new[] {new[]{x,y},new[]{x+600,y},new[]{x+600,y+600},new[]{x,y+600}}};
                QuantityShape shape;string reason;
                Require(FacadeQuantitiesCore.TryShape(new[]{ring},out shape,out reason),reason);
                var poly=db.Add(new Polyline());for(int v=0;v<4;v++)poly.AddVertexAt(v,new Point2d(ring.points[v][0],ring.points[v][1]),0,0,0);
                physical.Add(poly);
                report.elements.Add(new QuantityElement {element_id="p-"+z+"-"+i,zone_id=zid,zone_ids=new List<string>{zid},role="cladding",
                    product_id="synthetic-test-plate",mark="P600",type="A",material="porcelain",color="CAD ACI 7",orientation="front",
                    origin="synthetic",piece_kind="full",width_mm=600,height_mm=600,area_mm2=360000,shape_id=shape.shape_id,
                    rings=new List<QuantityRing>{ring}});
            }
        }
        Measure("quantity_capture_physical",()=> {
            for(int i=0;i<physical.Count;i++) report.elements[i].cad_entities.Add(FacadeQuantityStore.CaptureEntity(tr,physical[i],"outer"));
        });
        Measure("quantity_store",()=>FacadeQuantityStore.Store(tr,db,carriers,"ATTILE",report));
        QuantitySelection read=null;
        Measure("quantity_read",()=> {
            read=FacadeQuantityStore.ReadCladding(tr,db,zones.Select(z=>z.Hatch.ObjectId));
            Require(read.Ok,"read refused: "+read.Reason);
            Require(read.Reports.Count==1 && read.Reports[0].elements.Count==zcount*piecesPerZone,"read changed physical count");
            Require(read.SelectedZoneIds.Count==zcount,"read changed selected scope");
        });
        var result=new Dictionary<string,object> { {"status","PASS"},{"zones",zcount},{"carriers",carriers.Count},
            {"pieces_per_zone",piecesPerZone},{"physical_elements",physical.Count},{"phases",Phases},
            {"scope","Real production code 6222041, CAD doubles. Zone source rectangles intentionally coincident; this measures per-source verification work, not spatial layout or AutoCAD runtime."} };
        File.WriteAllText(args[3],Json.Serialize(result));return 0;
    }
}
