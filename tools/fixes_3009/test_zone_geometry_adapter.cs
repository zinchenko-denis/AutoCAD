// Executes actual ZoneGeometryGuard against explicit in-memory CAD doubles.
// Input reports and facade_zone/1 parts come from the actual Python engine.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;

internal static class ZoneGeometryAdapterCheck
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static readonly List<Dictionary<string, object>> Results = new List<Dictionary<string, object>>();
    private static Dictionary<string, object> Fixtures;
    private static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
    private static object[] Items(object value) { return (object[])value; }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

    private static void WriteRecord(Transaction tr, Entity entity, string key, string text, IEnumerable<ObjectId> refs = null)
    {
        if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
        var ext = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite);
        var data = new ResultBuffer(new TypedValue((int)DxfCode.Text, text));
        if (refs != null) foreach (var id in refs) data.Add(new TypedValue((int)DxfCode.SoftPointerId, id));
        if (ext.Contains(key)) ((Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForWrite)).Data = data;
        else ext.SetAt(key, new Xrecord { Data = data, XlateReferences = true });
    }

    private static Xrecord Record(Transaction tr, Entity entity, string key)
    {
        var ext = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead);
        return (Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForRead);
    }

    private static void CopyRecords(Transaction tr, Entity source, Entity target)
    {
        var ext = (DBDictionary)tr.GetObject(source.ExtensionDictionary, OpenMode.ForRead);
        foreach (var entry in ext.Items) {
            var old = (Xrecord)tr.GetObject(entry.Value, OpenMode.ForRead);
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            var destination = (DBDictionary)tr.GetObject(target.ExtensionDictionary, OpenMode.ForWrite);
            // Clone metadata bytes and retain old pointer IDs deliberately. This
            // is one explicit carrier-copy state, not a simulated AutoCAD COPY.
            destination.SetAt(entry.Key, new Xrecord {
                Data = new ResultBuffer(old.Data.Select(v => new TypedValue(v.TypeCode, v.Value)).ToArray()),
                XlateReferences = old.XlateReferences
            });
        }
    }

    private sealed class Fixture
    {
        public readonly Database Db = new Database();
        public readonly Transaction Tr;
        public readonly List<Polyline> Outers = new List<Polyline>();
        public readonly List<Polyline> Holes = new List<Polyline>();
        public readonly List<Dictionary<string, object>> Parts;
        public readonly Dictionary<string, object> Data;
        public readonly Hatch Hatch;
        public readonly MText Mark;
        public readonly ZoneGeometryResult Captured;

        public Fixture(string key)
        {
            Tr = new Transaction(Db);
            // Roundtrip each fixture so mutations in one test cannot affect others.
            var fixture = Dict(Json.DeserializeObject(Json.Serialize(Fixtures[key])));
            var request = Dict(fixture["request"]); var result = Dict(fixture["result"]);
            var zone = Dict(Items(result["zones"])[0]);
            var byHandle = new Dictionary<string, Polyline>();
            foreach (var raw in Items(request["contours"])) {
                var contour = Dict(raw); var p = Db.Add(new Polyline());
                var pts = Items(contour["pts"]);
                double[] bulges = contour.ContainsKey("bulges") ? Items(contour["bulges"]).Select(Convert.ToDouble).ToArray() : new double[pts.Length];
                for (int i = 0; i < pts.Length; i++) {
                    var xy = Items(pts[i]);
                    p.AddVertexAt(i, new Point2d(Convert.ToDouble(xy[0]), Convert.ToDouble(xy[1])), bulges[i], 0, 0);
                }
                Require(p.Handle.ToString() == (string)contour["id"], "fixture native/engine handles diverged");
                byHandle.Add(p.Handle.ToString(), p);
            }
            foreach (var id in Items(zone["outer_ids"])) Outers.Add(byHandle[(string)id]);
            foreach (var id in Items(zone["opening_ids"])) Holes.Add(byHandle[(string)id]);
            Hatch = Db.Add(new Hatch()); Mark = Db.Add(new MText { Location = new Point3d(5900,5900,0) });
            Hatch.Area = Convert.ToDouble(Dict(zone["report"])["area_net_m2"]) * 1e6;
            RefreshHatchFromSources();
            Parts = Items(result["zones_full"]).Select(Dict).ToList();
            Data = new Dictionary<string, object> {
                { "zone_id", zone["zone_id"] }, { "cladding", zone["cladding"] }, { "report", zone["report"] }
            };
            WriteRecord(Tr, Hatch, "ATFZONE", Json.Serialize(Data));
            WriteRecord(Tr, Mark, "ATFZONE", Json.Serialize(Data));
            Captured = ZoneGeometryGuard.Capture(Tr, Db, Hatch, Mark, Data, Parts,
                Outers.Select(p=>p.ObjectId).ToList(), Holes.Select(p=>p.ObjectId).ToList());
        }

        public void RefreshHatchFromSources()
        {
            Hatch.Loops.Clear(); Hatch.AssociatedIds.Clear();
            foreach (var p in Outers.Concat(Holes)) {
                var loop = new HatchLoop { LoopType = Outers.Contains(p) ? HatchLoopTypes.External : HatchLoopTypes.Default };
                for (int i = 0; i < p.NumberOfVertices; i++) loop.Polyline.Add(new BulgeVertex(p.GetPoint2dAt(i), p.GetBulgeAt(i)));
                Hatch.Loops.Add(loop); Hatch.AssociatedIds.Add(new ObjectIdCollection(new[] { p.ObjectId }));
            }
        }

        public Dictionary<string, Dictionary<string, object>> Sidecar()
        {
            return Parts.ToDictionary(p=>(string)p["id"], p=>Dict(Json.DeserializeObject(Json.Serialize(p))));
        }

        public ZoneGeometryResult Verify(Entity carrier, IDictionary<string, Dictionary<string, object>> sidecar = null)
        { return ZoneGeometryGuard.Verify(Tr, Db, carrier, sidecar); }

        public void Fresh()
        {
            Require(Captured.Ok, "fresh Capture failed: " + Captured.Reason);
            var hatch = Verify(Hatch); var mark = Verify(Mark);
            Require(hatch.Ok, "fresh Hatch rejected: " + hatch.Reason);
            Require(mark.Ok, "fresh mark rejected: " + mark.Reason);
            Require(hatch.HatchId == Hatch.ObjectId && mark.HatchId == Hatch.ObjectId, "carriers resolved different Hatch");
            Require(hatch.Fingerprint == mark.Fingerprint && hatch.Fingerprint == Captured.Fingerprint, "fresh carrier revisions disagree");
            Require(hatch.Parts.Count == Parts.Count && mark.Parts.Count == Parts.Count, "fresh merged parts dropped");
            var sidecar = Verify(Mark, Sidecar());
            Require(sidecar.Ok && sidecar.Parts.Count == Parts.Count, "fresh engine sidecar rejected: " + sidecar.Reason);
        }
    }

    private static void Rejected(ZoneGeometryResult result)
    {
        Require(!result.Ok, "changed/corrupt state accepted as current");
        Require(!String.IsNullOrWhiteSpace(result.Reason), "refusal without diagnostic");
        Require(result.Parts.Count == 0, "refusal leaked usable stale parts");
    }
    private static void Shift(Polyline poly, double x, double y)
    { for (int i=0; i<poly.Points.Count; i++) poly.Points[i] = new Point2d(poly.Points[i].X+x, poly.Points[i].Y+y); }
    private static LayoutGeometryGuard.Snapshot ZoneLayout(Fixture f, string key, bool mark)
    {
        var snapshot = LayoutGeometryGuard.Capture(f.Tr,f.Db,new ObjectId[0],new[] { f.Verify(f.Hatch) });
        StoreLayout(f,f.Hatch,key,snapshot);
        if (mark) StoreLayout(f,f.Mark,key,snapshot);
        return snapshot;
    }
    private static void StoreLayout(Fixture f, Entity carrier, string key, LayoutGeometryGuard.Snapshot snapshot)
    {
        WriteRecord(f.Tr,carrier,key,"{\"joints_x\":[0,600,1200],\"rows_y\":[0,600,1200]}");
        LayoutGeometryGuard.Store(f.Tr,f.Db,carrier,key,snapshot);
    }
    private static LayoutGeometryGuard.Result Layout(Fixture f, Entity carrier, string key)
    { return LayoutGeometryGuard.Verify(f.Tr,f.Db,carrier,key); }
    private static void LayoutAccepted(LayoutGeometryGuard.Result result, int raws, int zones)
    {
        Require(result.Ok,"fresh layout rejected: " + result.Reason);
        Require(result.RawSourceIds.Count == raws && result.ZoneHatchIds.Count == zones,"layout omitted sources");
        Require(!String.IsNullOrWhiteSpace(result.Fingerprint),"layout lacks successful fingerprint");
    }
    private static void LayoutRejected(LayoutGeometryGuard.Result result)
    {
        Require(!result.Ok,"stale layout accepted"); Require(!String.IsNullOrWhiteSpace(result.Reason),"layout refusal lacks reason");
        Require(result.RawSourceIds.Count == 0 && result.ZoneHatchIds.Count == 0,"layout refusal exposed stale sources");
    }
    private static void Test(string name, Action action)
    {
        try { action(); Results.Add(new Dictionary<string, object> { { "name", name }, { "status", "PASS" } }); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { Results.Add(new Dictionary<string, object> { { "name", name }, { "status", "FAIL" }, { "detail", ex.Message } }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    public static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--fixtures" || args[2] != "--report") {
            Console.Error.WriteLine("Usage: ZoneGeometryAdapterCheck.exe --fixtures fixtures.json --report cases.json"); return 2;
        }
        Fixtures = Dict(Json.DeserializeObject(File.ReadAllText(args[1])));
        Test("fresh_single_hatch_mark_engine_sidecar", () => new Fixture("single").Fresh());
        Test("fresh_merged_hatch_mark_engine_sidecar", () => {
            var f = new Fixture("merged"); f.Fresh();
            Require(f.Captured.Parts.Count == 2, "merged positive fixture did not contain two parts");
            Require(f.Holes.Count == 2, "merged positive fixture did not contain two windows");
        });
        Test("hatch_lines_curve_representation_equivalent", () => {
            var f = new Fixture("single"); f.Fresh();
            foreach (var loop in f.Hatch.Loops) {
                loop.IsPolyline = false;
                for (int i=0; i<loop.Polyline.Count; i++)
                    loop.Curves.Add(new LineSegment2d(loop.Polyline[i].Vertex, loop.Polyline[(i+1)%loop.Polyline.Count].Vertex));
            }
            var verified = f.Verify(f.Mark); Require(verified.Ok, "connected line loops refused: " + verified.Reason);
        });
        Test("hatch_reordered_loops_and_vertices_equivalent", () => {
            var f = new Fixture("single"); f.Fresh();
            f.Hatch.Loops.Reverse(); f.Hatch.AssociatedIds.Reverse();
            foreach (var loop in f.Hatch.Loops) {
                var points = loop.Polyline.ToArray(); loop.Polyline.Clear();
                for (int i=0; i<points.Length; i++) loop.Polyline.Add(points[(i+1)%points.Length]);
            }
            var verified = f.Verify(f.Mark); Require(verified.Ok, "unchanged loop geometry refused: " + verified.Reason);
        });
        Test("hatch_semicircular_window_curve_supported", () => {
            var f = new Fixture("curved"); f.Fresh(); var loop = f.Hatch.Loops[1];
            loop.IsPolyline = false;
            for (int i=0; i<loop.Polyline.Count; i++) {
                var start = loop.Polyline[i].Vertex; var end = loop.Polyline[(i+1)%loop.Polyline.Count].Vertex;
                if (i == 2) {
                    // The engine fixture has a top semicircle of radius 600 mm,
                    // from (2200,3000) to (1000,3000), CCW around (1600,3000).
                    Require(start.X == 2200 && start.Y == 3000 && end.X == 1000 && end.Y == 3000,"semicircle oracle endpoints changed");
                    loop.Curves.Add(new CircularArc2d { StartPoint=start, EndPoint=end, StartAngle=0, EndAngle=Math.PI, IsClockWise=false });
                } else loop.Curves.Add(new LineSegment2d(start,end));
            }
            var verified = f.Verify(f.Mark); Require(verified.Ok,"matching semicircle refused: " + verified.Reason);
            ((CircularArc2d)loop.Curves[2]).IsClockWise = true;
            Rejected(f.Verify(f.Mark));
        });
        Test("source_window_moved_before_hatch_regeneration", () => {
            var f = new Fixture("single"); f.Fresh(); double area = f.Hatch.Area;
            Shift(f.Holes[0],200,0);
            Require(f.Hatch.Area == area, "test accidentally changed hatch area");
            Rejected(f.Verify(f.Hatch)); Rejected(f.Verify(f.Mark));
        });
        Test("source_and_hatch_window_moved_same_area", () => {
            var f = new Fixture("single"); f.Fresh(); double area = f.Hatch.Area;
            Shift(f.Holes[0],200,0); f.RefreshHatchFromSources();
            Require(f.Hatch.Area == area, "test accidentally changed hatch area");
            Rejected(f.Verify(f.Hatch)); Rejected(f.Verify(f.Mark));
        });
        Test("hatch_boundary_only_changed", () => {
            var f = new Fixture("single"); f.Fresh();
            var old = f.Hatch.Loops[1].Polyline[0];
            f.Hatch.Loops[1].Polyline[0] = new BulgeVertex(new Point2d(old.Vertex.X+100, old.Vertex.Y), old.Bulge);
            Rejected(f.Verify(f.Mark));
        });
        Test("merged_second_source_move_detected", () => {
            var f = new Fixture("merged"); f.Fresh(); Shift(f.Outers[1],200,0); Shift(f.Holes[1],200,0);
            Rejected(f.Verify(f.Mark));
        });
        Test("copied_mark_metadata_cannot_borrow_original", () => {
            var f = new Fixture("single"); f.Fresh(); var copied = f.Db.Add(new MText { Location = new Point3d(9000,5900,0) });
            CopyRecords(f.Tr,f.Mark,copied); Rejected(f.Verify(copied)); f.Fresh();
        });
        Test("copied_hatch_metadata_cannot_borrow_original", () => {
            var f = new Fixture("single"); f.Fresh(); var copied = f.Db.Add(new Hatch());
            CopyRecords(f.Tr,f.Hatch,copied); Rejected(f.Verify(copied)); f.Fresh();
        });
        Test("missing_sidecar_part_rejected", () => {
            var f = new Fixture("merged"); f.Fresh(); var parts = f.Sidecar(); parts.Remove((string)f.Parts[1]["id"]);
            Rejected(f.Verify(f.Mark,parts));
        });
        Test("damaged_sidecar_window_rejected", () => {
            var f = new Fixture("single"); f.Fresh(); var parts = f.Sidecar();
            var opening = Dict(Items(parts.Values.First()["openings"])[0]);
            foreach (var p in Items(Dict(opening["poly"])["pts"])) { var xy = Items(p); xy[0] = Convert.ToDouble(xy[0])+200; }
            Rejected(f.Verify(f.Mark,parts));
        });
        Test("sidecar_part_order_irrelevant", () => {
            var f = new Fixture("merged"); f.Fresh(); var parts = f.Sidecar().Reverse().ToDictionary(p=>p.Key,p=>p.Value);
            var verified = f.Verify(f.Mark,parts); Require(verified.Ok && verified.Parts.Count == 2, verified.Reason);
        });
        Test("missing_source_pointer_rejected", () => {
            var f = new Fixture("single"); f.Fresh();
            var record = Record(f.Tr,f.Mark,ZoneGeometryGuard.Key);
            record.Data = new ResultBuffer(record.Data.Where(v=>v.TypeCode != (int)DxfCode.SoftPointerId || (ObjectId)v.Value != f.Holes[0].ObjectId).ToArray());
            Rejected(f.Verify(f.Mark));
        });
        Test("erased_source_rejected", () => { var f = new Fixture("single"); f.Fresh(); f.Holes[0].Erase(); Rejected(f.Verify(f.Mark)); });
        Test("unsupported_hatch_curve_rejected", () => {
            var f = new Fixture("single"); f.Fresh();
            f.Hatch.Loops[0].IsPolyline = false; f.Hatch.Loops[0].Curves.Add(new UnsupportedCurve2d());
            Rejected(f.Verify(f.Hatch));
        });
        Test("hatch_island_style_change_rejected", () => {
            var f = new Fixture("single"); f.Fresh(); f.Hatch.HatchStyle = HatchStyle.Ignore; Rejected(f.Verify(f.Mark));
        });
        Test("hatch_loop_type_change_rejected", () => {
            var f = new Fixture("single"); f.Fresh(); f.Hatch.Loops[1].LoopType = HatchLoopTypes.External; Rejected(f.Verify(f.Mark));
        });
        Test("nonplanar_source_rejected", () => { var f = new Fixture("single"); f.Fresh(); f.Outers[0].Elevation = 10; Rejected(f.Verify(f.Mark)); });
        Test("malformed_geometry_record_rejected", () => {
            var f = new Fixture("single"); f.Fresh(); WriteRecord(f.Tr,f.Mark,ZoneGeometryGuard.Key,"{broken json"); Rejected(f.Verify(f.Mark));
        });
        Test("report_changed_after_capture_rejected", () => {
            var f = new Fixture("single"); f.Fresh(); Dict(f.Data["report"])["area_net_m2"] = 123;
            WriteRecord(f.Tr,f.Mark,"ATFZONE",Json.Serialize(f.Data)); Rejected(f.Verify(f.Mark));
        });
        Test("layout_raw_outer_and_hole_positive", () => {
            var f = new Fixture("single"); f.Fresh();
            var snapshot = LayoutGeometryGuard.Capture(f.Tr,f.Db,f.Outers.Concat(f.Holes).Select(p=>p.ObjectId),new ZoneGeometryResult[0]);
            StoreLayout(f,f.Outers[0],"ATCLAD",snapshot);
            LayoutAccepted(Layout(f,f.Outers[0],"ATCLAD"),2,0);
        });
        Test("layout_raw_hole_move_rejected", () => {
            var f = new Fixture("single"); f.Fresh();
            var snapshot = LayoutGeometryGuard.Capture(f.Tr,f.Db,f.Outers.Concat(f.Holes).Select(p=>p.ObjectId),new ZoneGeometryResult[0]);
            StoreLayout(f,f.Outers[0],"ATCLAD",snapshot);
            LayoutAccepted(Layout(f,f.Outers[0],"ATCLAD"),2,0);
            Shift(f.Holes[0],200,0); LayoutRejected(Layout(f,f.Outers[0],"ATCLAD"));
        });
        Test("layout_zone_hatch_and_mark_positive", () => {
            var f = new Fixture("merged"); f.Fresh(); ZoneLayout(f,"ATCLAD",true);
            LayoutAccepted(Layout(f,f.Hatch,"ATCLAD"),0,1); LayoutAccepted(Layout(f,f.Mark,"ATCLAD"),0,1);
        });
        Test("layout_new_hatch_revision_invalidates_old_mark", () => {
            var f = new Fixture("single"); f.Fresh(); ZoneLayout(f,"ATCLAD",true);
            LayoutAccepted(Layout(f,f.Mark,"ATCLAD"),0,1);
            // A second run, deliberately identical geometry AND axes. Only the
            // unique completed-layout generation can disqualify the old mark.
            ZoneLayout(f,"ATCLAD",false);
            LayoutAccepted(Layout(f,f.Hatch,"ATCLAD"),0,1); LayoutRejected(Layout(f,f.Mark,"ATCLAD"));
        });
        Test("layout_new_command_key_invalidates_old_mark", () => {
            var f = new Fixture("single"); f.Fresh(); ZoneLayout(f,"ATCLAD",true);
            LayoutAccepted(Layout(f,f.Mark,"ATCLAD"),0,1); ZoneLayout(f,"ATTILE",false);
            LayoutAccepted(Layout(f,f.Hatch,"ATTILE"),0,1); LayoutRejected(Layout(f,f.Mark,"ATCLAD"));
        });
        Test("layout_old_mark_missing_atfzone_cannot_skip_revision", () => {
            var f = new Fixture("single"); f.Fresh(); ZoneLayout(f,"ATTILE",true);
            LayoutAccepted(Layout(f,f.Mark,"ATTILE"),0,1);
            ZoneLayout(f,"ATTILE",false);
            LayoutAccepted(Layout(f,f.Hatch,"ATTILE"),0,1);
            // The mark retains ATTILE and ATTILE_GEOMETRY. Losing its ATFZONE
            // record must not turn a zone carrier into an unchecked raw one.
            ((DBDictionary)f.Tr.GetObject(f.Mark.ExtensionDictionary,OpenMode.ForWrite)).Remove("ATFZONE");
            LayoutRejected(Layout(f,f.Mark,"ATTILE"));
        });
        Test("layout_missing_canonical_metadata_rejected", () => {
            var f = new Fixture("single"); f.Fresh(); ZoneLayout(f,"ATCLAD",true);
            LayoutAccepted(Layout(f,f.Mark,"ATCLAD"),0,1);
            ((DBDictionary)f.Tr.GetObject(f.Hatch.ExtensionDictionary,OpenMode.ForWrite)).Remove("ATCLAD");
            LayoutRejected(Layout(f,f.Mark,"ATCLAD"));
        });
        Test("layout_tampered_canonical_metadata_rejected", () => {
            var f = new Fixture("single"); f.Fresh(); ZoneLayout(f,"ATCLAD",true);
            LayoutAccepted(Layout(f,f.Mark,"ATCLAD"),0,1);
            WriteRecord(f.Tr,f.Hatch,"ATCLAD","{\"joints_x\":[0,400,800],\"rows_y\":[0,400,800]}");
            LayoutRejected(Layout(f,f.Mark,"ATCLAD"));
        });
        int failed = Results.Count(r=>(string)r["status"] == "FAIL");
        File.WriteAllText(args[3],Json.Serialize(new Dictionary<string,object> {
            { "scope", "Actual ZoneGeometryGuard.cs, LayoutGeometryGuard.cs and GeometryFingerprint.cs with in-memory CAD API doubles; engine-generated reports/parts. AutoCAD runtime NOT RUN." },
            { "total", Results.Count }, { "passed", Results.Count-failed }, { "failed", failed }, { "cases", Results }
        }));
        Console.WriteLine("Zone adapter checks: " + (Results.Count-failed) + "/" + Results.Count + " passed; AutoCAD runtime: NOT RUN");
        return failed == 0 ? 0 : 1;
    }
}
