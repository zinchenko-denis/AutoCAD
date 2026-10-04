// Executes the actual quantity store AND existing zone/layout guards.
// CAD doubles explicitly omit native transactions, COPY remapping, Save and Undo.
// Input reports and facade_zone/1 parts come from the actual Python engine.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;

internal static class QuantityStoreCheck
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

    // Shared only with the operation-count benchmark in the test assembly.
    internal sealed class Fixture
    {
        public readonly Database Db;
        public readonly Transaction Tr;
        public readonly List<Polyline> Outers = new List<Polyline>();
        public readonly List<Polyline> Holes = new List<Polyline>();
        public readonly List<Dictionary<string, object>> Parts;
        public readonly Dictionary<string, object> Data;
        public readonly Hatch Hatch;
        public readonly MText Mark;
        public readonly ZoneGeometryResult Captured;

        public Fixture(string key, Database sharedDatabase = null, string renamedZone = null)
        {
            Db = sharedDatabase ?? new Database();
            Tr = new Transaction(Db);
            // Roundtrip each fixture so mutations in one test cannot affect others.
            var fixture = Dict(Json.DeserializeObject(Json.Serialize(Fixtures[key])));
            var request = Dict(fixture["request"]); var result = Dict(fixture["result"]);
            var zone = Dict(Items(result["zones"])[0]);
            if (renamedZone != null) {
                string prior = (string)zone["zone_id"]; zone["zone_id"] = renamedZone;
                Dict(zone["report"])["zone_id"] = renamedZone;
                foreach (object raw in Items(result["zones_full"])) {
                    var part = Dict(raw); part["id"] = ((string)part["id"]).Replace(prior,renamedZone);
                }
            }
            var byHandle = new Dictionary<string, Polyline>();
            foreach (var raw in Items(request["contours"])) {
                var contour = Dict(raw); var p = Db.Add(new Polyline());
                var pts = Items(contour["pts"]);
                double[] bulges = contour.ContainsKey("bulges") ? Items(contour["bulges"]).Select(Convert.ToDouble).ToArray() : new double[pts.Length];
                for (int i = 0; i < pts.Length; i++) {
                    var xy = Items(pts[i]);
                    p.AddVertexAt(i, new Point2d(Convert.ToDouble(xy[0]), Convert.ToDouble(xy[1])), bulges[i], 0, 0);
                }
                if (sharedDatabase == null) Require(p.Handle.ToString() == (string)contour["id"], "fixture native/engine handles diverged");
                byHandle.Add((string)contour["id"], p);
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

    private static void Shift(Polyline poly, double x, double y)
    { for (int i=0; i<poly.Points.Count; i++) poly.Points[i] = new Point2d(poly.Points[i].X+x, poly.Points[i].Y+y); }

    private sealed class QuantityFixture
    {
        public readonly Fixture Zone;
        public readonly Polyline Piece;
        public readonly QuantityReport Report;
        public readonly string LayoutKey;
        public QuantityFixture(string zoneKind = "single", string key = "ATTILE", bool persist = true)
        {
            Zone = new Fixture(zoneKind); Zone.Fresh(); LayoutKey = key;
            var snapshot = LayoutGeometryGuard.Capture(Zone.Tr, Zone.Db, new ObjectId[0], new[] { Zone.Verify(Zone.Hatch) });
            foreach (var carrier in new Entity[] { Zone.Hatch, Zone.Mark }) {
                WriteRecord(Zone.Tr, carrier, key, "{\"joints_x\":[0,600,1200],\"rows_y\":[0,600,1200]}");
                LayoutGeometryGuard.Store(Zone.Tr, Zone.Db, carrier, key, snapshot);
            }
            Piece = Zone.Db.Add(new Polyline());
            Piece.AddVertexAt(0, new Point2d(0,0),0,0,0); Piece.AddVertexAt(1,new Point2d(600,0),0,0,0);
            Piece.AddVertexAt(2, new Point2d(600,600),0,0,0); Piece.AddVertexAt(3,new Point2d(0,600),0,0,0);
            var rings = new List<QuantityRing> { new QuantityRing { points = new[] {
                new[] {0.0,0.0}, new[] {600.0,0.0}, new[] {600.0,600.0}, new[] {0.0,600.0} } } };
            QuantityShape shape; string reason;
            Require(FacadeQuantitiesCore.TryShape(rings, out shape, out reason),"fixture shape failed: " + reason);
            string zid = (string)Zone.Data["zone_id"];
            Report = new QuantityReport {
                report_id = "synthetic-report-1", run_id = "synthetic-run-1", kind = "cladding", scope = "whole_layout_run",
                algorithm = key + "/facade_quantities/1", completeness = "complete", engineering_coverage = "geometry_only",
                zone_ids = new List<string> { zid },
                source_revisions = new Dictionary<string,string> { {"layout_revision",snapshot.Revision}, {"layout_geometry",snapshot.Fingerprint} },
                elements = new List<QuantityElement> { new QuantityElement {
                    element_id = "synthetic-piece-1", zone_id = zid, zone_ids = new List<string> {zid}, role = "cladding",
                    material = "porcelain", type = "A", origin = "generated:" + key, orientation = "XY; rotation=0; mirror=false",
                    piece_kind = "full", width_mm = 600, height_mm = 600, area_mm2 = 360000, shape_id = shape.shape_id,
                    rings = rings, cad_entities = new List<QuantityCadEntity> { FacadeQuantityStore.CaptureEntity(Zone.Tr, Piece, "outer") }
                } }
            };
            if (persist) Store();
        }
        public void Store() {
            foreach (Entity owner in new Entity[] { Zone.Hatch, Zone.Mark })
                Report.source_revisions["owner_zone:" + owner.Handle.ToString()] = (string)Zone.Data["zone_id"];
            FacadeQuantityStore.Store(Zone.Tr, Zone.Db, new Entity[] { Zone.Hatch, Zone.Mark }, LayoutKey, Report);
        }
        public QuantitySelection Read(params Entity[] entities)
        { return FacadeQuantityStore.ReadCladding(Zone.Tr, Zone.Db, entities.Select(e => e.ObjectId)); }
        public void Fresh()
        {
            foreach (Entity entity in new Entity[] { Zone.Hatch, Zone.Mark }) {
                var result = Read(entity);
                Accepted(result, 1);
                Require(result.Reports[0].elements.Count == 1, "one physical piece changed count during persistence");
                Require(result.Reports[0].elements[0].cad_entities.Count == 1, "CAD mapping was lost");
                Require(result.SelectedZoneIds.Contains((string)Zone.Data["zone_id"]), "selected zone missing");
            }
        }
    }
    private static void Accepted(QuantitySelection result, int reports)
    {
        Require(result.Ok, "fresh quantity selection refused: " + result.Reason);
        Require(result.Reports.Count == reports, "report count incorrect: " + result.Reports.Count);
        Require(result.SelectedZoneIds.Count > 0, "successful selection contains no zones");
    }
    private static void Rejected(QuantitySelection result)
    {
        Require(!result.Ok, "stale or corrupt quantities accepted as current");
        Require(!String.IsNullOrWhiteSpace(result.Reason), "refusal has no diagnostic");
        Require(result.Reports.Count == 0, "refusal exposed stale reports");
    }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex) { Require(!String.IsNullOrWhiteSpace(ex.Message), "refusal has no reason"); return; }
        throw new InvalidOperationException("invalid persistence accepted");
    }
    private static void CheckPayloadBoundary(long bytes)
    {
        // Exercise the common production bound directly without allocating a
        // 256 MiB string, UTF-8 buffer, decompression stream and duplicate JSON.
        // Compress/Decompress use this same helper; ordinary persisted Unicode
        // report roundtrips above exercise both call paths under the bound.
        var method = typeof(FacadeQuantityStore).GetMethod("CheckPayloadSize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Require(method != null,"production payload-size check missing");
        try { method.Invoke(null,new object[] {bytes}); }
        catch (System.Reflection.TargetInvocationException ex) {
            if (ex.InnerException is InvalidOperationException) throw (InvalidOperationException)ex.InnerException;
            throw;
        }
    }
    private static Polyline AddCladdingPiece(QuantityFixture f,string id,double shift)
    {
        var item=Json.Deserialize<QuantityElement>(Json.Serialize(f.Report.elements[0]));
        item.element_id=id;
        foreach(var ring in item.rings) foreach(var point in ring.points) point[0]+=shift;
        var piece=f.Zone.Db.Add(new Polyline());
        foreach(var point in item.rings[0].points) piece.AddVertexAt(piece.NumberOfVertices,new Point2d(point[0],point[1]),0,0,0);
        item.cad_entities=new List<QuantityCadEntity>{FacadeQuantityStore.CaptureEntity(f.Zone.Tr,piece,"outer")};
        f.Report.elements.Add(item);return piece;
    }
    private sealed class BlockFixture
    {
        public readonly QuantityFixture F = new QuantityFixture("single","ATCLAD",false);
        public readonly BlockReference Block;
        public readonly BlockTableRecord Definition;
        public readonly AttributeReference Attribute;
        public BlockFixture(bool dynamic = false)
        {
            Definition = F.Zone.Db.Add(new BlockTableRecord { Name = "SyntheticPlate600x600" });
            Definition.AppendEntity(F.Piece);
            Block = F.Zone.Db.Add(new BlockReference { BlockTableRecord = Definition.ObjectId,
                IsDynamicBlock = dynamic, DynamicBlockTableRecord = Definition.ObjectId });
            if (dynamic) Block.DynamicBlockReferencePropertyCollection.Add(
                new DynamicBlockReferenceProperty { PropertyName = "Width", Value = 600.0 });
            Attribute = F.Zone.Db.Add(new AttributeReference { Tag = "MARK", TextString = "A-1" });
            Block.AttributeCollection.Add(Attribute.ObjectId);
            F.Report.elements[0].cad_entities.Clear();
            F.Report.elements[0].cad_entities.Add(FacadeQuantityStore.CaptureEntity(F.Zone.Tr,Block,"block"));
            F.Store();
        }
    }
    private sealed class FrameFixture
    {
        public readonly QuantityFixture Cladding = new QuantityFixture();
        public readonly Line Rail;
        public readonly QuantityReport Report;
        public readonly FacadeQuantityStore.FrameSources Sources;
        public readonly string CladdingCurrent;
        public FrameFixture(bool persist = true)
        {
            var z=Cladding.Zone;
            CladdingCurrent=String.Concat(Record(z.Tr,z.Hatch,"ATLAYOUT_CURRENT").Data
                .Where(v=>v.TypeCode==(int)DxfCode.Text).Select(v=>(string)v.Value));
            Sources=FacadeQuantityStore.CaptureFrameSources(z.Tr,z.Db,new[] {z.Hatch.ObjectId,z.Mark.ObjectId,z.Hatch.ObjectId});
            Rail=z.Db.Add(new Line {StartPoint=new Point3d(300,0,0),EndPoint=new Point3d(300,1000,0)});
            Report=new QuantityReport { report_id="frame-report-1",run_id="frame-run-1",kind="frame",
                scope="whole_layout_run",algorithm="synthetic-frame-store-check",completeness="partial",
                engineering_coverage="geometry_only",zone_ids=new List<string>{"F-1"},
                elements=new List<QuantityElement>{new QuantityElement {
                    element_id="frame-rail-1",zone_id="F-1",zone_ids=new List<string>{"F-1"},role="rail",
                    product_id=null,mark=null,type="synthetic rail",material=null,coating=null,system="synthetic-test-system",
                    length_mm=1000,origin="test",cad_entities=new List<QuantityCadEntity>{FacadeQuantityStore.CaptureEntity(z.Tr,Rail,"rail")}
                }}
            };
            foreach(var owner in new Entity[]{z.Hatch,z.Mark}) {
                WriteRecord(z.Tr,owner,"ATFRAME",Json.Serialize(new Dictionary<string,object> {
                    {"owner",owner.Handle.ToString()},{"mode","manual"},{"rail_count",1}
                }));
                Report.source_revisions["owner_zone:"+owner.Handle]="F-1";
            }
            if(persist) Store();
        }
        public void Store() { var z=Cladding.Zone; FacadeQuantityStore.StoreFrame(z.Tr,z.Db,new Entity[]{z.Hatch,z.Mark},Report,Sources); }
        public QuantitySelection Read(params Entity[] entities) {
            var z=Cladding.Zone;
            return FacadeQuantityStore.ReadFrame(z.Tr,z.Db,(entities.Length==0 ? new Entity[]{z.Hatch} : entities).Select(e=>e.ObjectId));
        }
        public void Fresh() {
            var read=Read();Accepted(read,1);
            Require(read.Reports[0].kind=="frame" && read.Reports[0].elements.Count==1,"frame report changed physical count");
            Require(read.Rows!=null && read.Rows.ok && read.Rows.rows.Sum(r=>r.quantity)==1,"validated frame rows not returned");
            Require(!String.IsNullOrWhiteSpace(read.Fingerprint),"frame selection fingerprint missing");
            Cladding.Fresh();
        }
    }

    // Native HatchLoop.IsPolyline=false also represents an ordinary rectangle
    // made from LineSegment2d edges. The old double-only fixtures missed it.
    private sealed class HatchBlockFixture
    {
        internal readonly QuantityFixture F = new QuantityFixture("single", "ATTILE", false);
        internal readonly Hatch Hatch;
        internal readonly BlockReference Block;
        internal HatchBlockFixture(params Curve2d[] edges)
        {
            var definition = F.Zone.Db.Add(new BlockTableRecord { Name = "TileWithEdgeHatch" });
            definition.AppendEntity(F.Piece);
            Hatch = F.Zone.Db.Add(new Hatch());
            var loop = new HatchLoop { IsPolyline = false, LoopType = HatchLoopTypes.External };
            loop.Curves.AddRange(edges); Hatch.Loops.Add(loop); definition.AppendEntity(Hatch);
            Block = F.Zone.Db.Add(new BlockReference { BlockTableRecord = definition.ObjectId });
            F.Report.elements[0].cad_entities.Clear();
            F.Report.elements[0].cad_entities.Add(FacadeQuantityStore.CaptureEntity(F.Zone.Tr, Block, "block"));
            F.Store();
        }
        internal void Fresh() { Accepted(F.Read(F.Zone.Hatch), 1); }
        internal void Changed() { Rejected(F.Read(F.Zone.Hatch)); }
    }

    private static Curve2d[] RectangleEdges()
    {
        return new Curve2d[] {
            new LineSegment2d(new Point2d(0,0), new Point2d(600,0)),
            new LineSegment2d(new Point2d(600,0), new Point2d(600,600)),
            new LineSegment2d(new Point2d(600,600), new Point2d(0,600)),
            new LineSegment2d(new Point2d(0,600), new Point2d(0,0)) };
    }

    private static void HatchEdgeCases()
    {
        Test("ATTILE_block_rectangle_hatch_edges_roundtrip_and_edit", () => {
            var f = new HatchBlockFixture(RectangleEdges()); f.Fresh();
            // Keep the same overall bounds; a bounding-box-only fingerprint fails.
            f.Hatch.Loops[0].Curves[0].EndPoint = new Point2d(590,0); f.Changed();
        });
        Test("ATTILE_block_full_circle_hatch_roundtrip_and_radius", () => {
            var arc = new CircularArc2d { Center = new Point2d(300,300), Radius = 200,
                StartAngle = 0, EndAngle = 2*Math.PI, ReferenceVector = new Vector2d(1,0) };
            var f = new HatchBlockFixture(arc); f.Fresh(); arc.Radius = 190; f.Changed();
        });
        Test("ATTILE_block_arc_reference_vector_and_orientation_checked", () => {
            var arc = new CircularArc2d { Center = new Point2d(300,300), Radius = 200,
                StartAngle = 0, EndAngle = Math.PI, ReferenceVector = new Vector2d(1,0) };
            var f = new HatchBlockFixture(arc, new LineSegment2d(new Point2d(100,300), new Point2d(500,300)));
            f.Fresh(); arc.ReferenceVector = new Vector2d(0,1); f.Changed();
            arc.ReferenceVector = new Vector2d(1,0); f.Fresh(); arc.IsClockWise = true; f.Changed();
        });
        Test("ATTILE_block_elliptical_hatch_roundtrip_and_axis", () => {
            var arc = new EllipticalArc2d { Center = new Point2d(300,300),
                MajorAxis = new Vector2d(1,0), MinorAxis = new Vector2d(0,1),
                MajorRadius = 250, MinorRadius = 100, StartAngle = 0, EndAngle = 2*Math.PI };
            var f = new HatchBlockFixture(arc); f.Fresh(); arc.MinorRadius = 120; f.Changed();
        });
        Test("ATTILE_block_spline_hatch_exact_control_points_knots_weights", () => {
            var spline = new NurbCurve2d { DefinitionData = new NurbCurve2dData {
                Degree = 2, Rational = true, Periodic = false,
                ControlPoints = new List<Point2d> { new Point2d(0,0), new Point2d(300,500), new Point2d(600,0) },
                Knots = new List<double> { 0,0,0,1,1,1 }, Weights = new List<double> { 1,1,1 } } };
            var f = new HatchBlockFixture(spline, new LineSegment2d(new Point2d(600,0), new Point2d(0,0))); f.Fresh();
            spline.DefinitionData.ControlPoints[1] = new Point2d(300,450); f.Changed();
            spline.DefinitionData.ControlPoints[1] = new Point2d(300,500); f.Fresh();
            spline.DefinitionData.Weights[1] = 0.8; f.Changed();
            spline.DefinitionData.Weights[1] = 1; f.Fresh();
            spline.DefinitionData.Knots[3] = 0.9; f.Changed();
        });
        Test("ATTILE_many_tiles_share_one_hatch_definition_capture", () => {
            var f = new HatchBlockFixture(RectangleEdges()); f.Fresh();
            long before = CadCounters.HatchLoopsRead;
            for (int i=0; i<1000; i++) {
                var tile = f.F.Zone.Db.Add(new BlockReference { BlockTableRecord = f.Block.BlockTableRecord,
                    Position = new Point3d(i*610,0,0) });
                FacadeQuantityStore.CaptureEntity(f.F.Zone.Tr, tile, "block");
            }
            Require(CadCounters.HatchLoopsRead - before <= 1, "sample hatch traversed once per tile instead of once per phase");
        });
        Test("ATTILE_unknown_hatch_edge_not_silently_fingerprinted", () => {
            Throws(() => new HatchBlockFixture(new UnsupportedCurve2d()));
        });
    }
    private static Fixture AddMergedGroup(QuantityFixture f, bool sharedPiece = true, bool emptySecondZone = false)
    {
        // Two independently captured zones, deliberately coincident synthetic
        // geometry: this case tests selection identity/scope, not layout nesting.
        var second = new Fixture("single",f.Zone.Db,"F-2"); second.Fresh();
        var source = LayoutGeometryGuard.Capture(f.Zone.Tr,f.Zone.Db,new ObjectId[0],
            new[] { f.Zone.Verify(f.Zone.Hatch), second.Verify(second.Hatch) });
        var owners = new Entity[] { f.Zone.Hatch,f.Zone.Mark,second.Hatch,second.Mark };
        f.Report.zone_ids.Add("F-2");
        if (sharedPiece) {
            f.Report.elements[0].zone_ids.Add("F-2");
            f.Report.elements[0].zone_id = "F-1+F-2";
        } else if (!emptySecondZone) {
            var piece = f.Zone.Db.Add(new Polyline());
            for (int i=0;i<4;i++) piece.AddVertexAt(i,f.Piece.Points[i],0,0,0);
            var item = Json.Deserialize<QuantityElement>(Json.Serialize(f.Report.elements[0]));
            item.element_id = "synthetic-piece-2"; item.zone_id = "F-2"; item.zone_ids = new List<string> {"F-2"};
            item.cad_entities = new List<QuantityCadEntity> { FacadeQuantityStore.CaptureEntity(f.Zone.Tr,piece,"outer") };
            f.Report.elements.Add(item);
            f.Report.cutting.Add(new QuantityCuttingGroup {
                group_id = "synthetic-joint-cutting", scope_zone_ids = new List<string> {"F-1","F-2"},
                rows = new List<QuantityRow> {new QuantityRow { basis = "cutting", role = "blanks_total", quantity = 2,
                    zone_id = "F-1+F-2", zone_ids = new List<string> {"F-1","F-2"}, material = "porcelain", type = "A",
                    width_mm = 600, height_mm = 600, unit = "шт." }}
            });
        }
        foreach (var owner in owners) {
            WriteRecord(f.Zone.Tr,owner,f.LayoutKey,"{\"joints_x\":[0,600,1200],\"rows_y\":[0,600,1200]}");
            LayoutGeometryGuard.Store(f.Zone.Tr,f.Zone.Db,owner,f.LayoutKey,source);
            f.Report.source_revisions["owner_zone:" + owner.Handle.ToString()] =
                owner == second.Hatch || owner == second.Mark ? "F-2" : "F-1";
        }
        FacadeQuantityStore.Store(f.Zone.Tr,f.Zone.Db,owners,f.LayoutKey,f.Report);
        return second;
    }
    private static void Test(string name, Action action)
    {
        try { action(); Results.Add(new Dictionary<string,object> { {"name", name}, {"status", "PASS"} }); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { Results.Add(new Dictionary<string,object> { {"name", name}, {"status", "FAIL"}, {"detail", ex.Message} }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    public static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--fixtures" || args[2] != "--report") {
            Console.Error.WriteLine("Usage: QuantityStoreCheck.exe --fixtures fixtures.json --report cases.json"); return 2;
        }
        Fixtures = Dict(Json.DeserializeObject(File.ReadAllText(args[1])));
        Test("attile_quantity_roundtrip_hatch_and_mark", () => new QuantityFixture().Fresh());
        Test("atclad_quantity_roundtrip_hatch_and_mark", () => new QuantityFixture("single","ATCLAD").Fresh());
        Test("merged_zone_all_parts_current", () => { var f = new QuantityFixture("merged"); f.Fresh(); Require(f.Zone.Parts.Count == 2, "fixture has no merged parts"); });
        Test("carrier_selection_deduplicated", () => {
            var f = new QuantityFixture(); var result = f.Read(f.Zone.Hatch, f.Zone.Mark, f.Zone.Mark);
            Accepted(result,1); Require(result.Reports[0].elements.Count == 1, "duplicate physical piece");
        });
        Test("deleted_physical_piece_refused", () => { var f = new QuantityFixture(); f.Piece.Erase(); Rejected(f.Read(f.Zone.Hatch)); });
        Test("moved_physical_piece_refused", () => { var f = new QuantityFixture(); Shift(f.Piece,50,0); Rejected(f.Read(f.Zone.Hatch)); });
        Test("changed_same_area_piece_refused", () => {
            var f = new QuantityFixture(); // 600x600 -> 1200x300: exact equal area, different actual element.
            f.Piece.Points[1] = new Point2d(1200,0); f.Piece.Points[2] = new Point2d(1200,300); f.Piece.Points[3] = new Point2d(0,300);
            Rejected(f.Read(f.Zone.Mark));
        });
        Test("moved_source_window_same_area_refused", () => {
            var f = new QuantityFixture(); Shift(f.Zone.Holes[0],100,0); f.Zone.RefreshHatchFromSources();
            Rejected(f.Read(f.Zone.Hatch));
        });
        Test("deleted_source_refused", () => { var f = new QuantityFixture(); f.Zone.Outers[0].Erase(); Rejected(f.Read(f.Zone.Mark)); });
        Test("copied_carrier_metadata_retaining_old_pointers_refused", () => {
            var f = new QuantityFixture(); var copy = f.Zone.Db.Add(new MText { Location = f.Zone.Mark.Location, Contents = f.Zone.Mark.Contents });
            CopyRecords(f.Zone.Tr,f.Zone.Mark,copy); Rejected(f.Read(copy));
        });
        Test("layout_label_tamper_refused", () => {
            var f = new QuantityFixture(); WriteRecord(f.Zone.Tr, f.Zone.Hatch, "ATTILE", "{\"joints_x\":[0,700,1400],\"rows_y\":[0,600,1200]}");
            Rejected(f.Read(f.Zone.Hatch));
        });
        Test("previous_layout_generation_refused", () => {
            var f = new QuantityFixture();
            var source = LayoutGeometryGuard.Capture(f.Zone.Tr,f.Zone.Db,new ObjectId[0],new[] { f.Zone.Verify(f.Zone.Hatch) });
            // Same geometry and arithmetic payload, genuinely new source revision.
            LayoutGeometryGuard.Store(f.Zone.Tr,f.Zone.Db,f.Zone.Hatch,"ATTILE",source);
            Rejected(f.Read(f.Zone.Hatch)); Rejected(f.Read(f.Zone.Mark));
        });
        Test("old_layout_without_quantity_passport_refused", () => {
            var f = new QuantityFixture("single","ATTILE",false); Rejected(f.Read(f.Zone.Hatch));
        });
        Test("old_zone_without_geometry_passport_refused", () => {
            var f = new QuantityFixture(); var ext = (DBDictionary)f.Zone.Tr.GetObject(f.Zone.Hatch.ExtensionDictionary,OpenMode.ForWrite);
            ext.Remove(ZoneGeometryGuard.Key); Rejected(f.Read(f.Zone.Hatch));
        });
        Test("missing_canonical_hatch_refused", () => { var f = new QuantityFixture(); f.Zone.Hatch.Erase(); Rejected(f.Read(f.Zone.Mark)); });
        Test("piece_selection_returns_explicit_whole_zone_scope", () => {
            var f = new QuantityFixture(); var read = f.Read(f.Piece); Accepted(read,1);
            Require(read.SelectedZoneIds.SequenceEqual(new[] {"F-1"}),"piece selection did not expose expanded scope to UI");
            Require(read.Reports[0].elements.Count == 1,"whole zone piece set lost");
        });
        Test("selected_manual_object_explicitly_excluded", () => {
            var f = new QuantityFixture(); var manual = f.Zone.Db.Add(new Line());
            var read = f.Read(f.Zone.Hatch,manual); Accepted(read,1);
            Require(read.Warnings.Count > 0,"manual object silently discarded");
            Require(read.Reports[0].elements.Count == 1,"manual unregistered line counted as manufactured part");
        });
        Test("old_frame_metadata_not_counted_as_cladding", () => {
            var f = new QuantityFixture(); var frame = f.Zone.Db.Add(new Line());
            WriteRecord(f.Zone.Tr,frame,"ATFRAME","{\"profiles\":99,\"stale\":true}");
            Require(!FacadeQuantityStore.IsCandidate(f.Zone.Tr,frame),"frame metadata used as cladding candidate");
            var read = f.Read(f.Zone.Hatch,frame); Accepted(read,1);
            Require(read.Reports[0].elements.Count == 1 && read.Warnings.Count > 0,"stale frame report leaked into quantities");
        });
        Test("table_with_copied_metadata_never_candidate", () => {
            var f = new QuantityFixture(); var table = f.Zone.Db.Add(new Table());
            CopyRecords(f.Zone.Tr,f.Zone.Hatch,table);
            Require(!FacadeQuantityStore.IsCandidate(f.Zone.Tr,table),"old result table became a source");
        });
        Test("unselected_copied_piece_detected_by_modelspace_scan", () => {
            var f = new QuantityFixture(); f.Fresh(); // Instantiate modelspace before adding another entity.
            var copy = f.Zone.Db.Add(new Polyline());
            for (int i=0;i<4;i++) copy.AddVertexAt(i,f.Piece.Points[i],0,0,0);
            CopyRecords(f.Zone.Tr,f.Piece,copy);
            Rejected(f.Read(f.Zone.Hatch));
        });
        Test("piece_stamp_tamper_refused", () => {
            var f = new QuantityFixture();
            var record = Record(f.Zone.Tr,f.Piece,FacadeQuantityStore.ElementKey);
            string text = String.Concat(record.Data.Select(v => (string)v.Value));
            WriteRecord(f.Zone.Tr,f.Piece,FacadeQuantityStore.ElementKey,text.Replace("synthetic-piece-1","synthetic-piece-2"));
            Rejected(f.Read(f.Zone.Hatch));
        });
        Test("multiple_graphics_one_physical_element", () => {
            var f = new QuantityFixture("single","ATTILE",false);
            var hatch = f.Zone.Db.Add(new Hatch()); var loop = new HatchLoop();
            foreach (var point in f.Piece.Points) loop.Polyline.Add(new BulgeVertex(point,0));
            hatch.Loops.Add(loop);
            var warning = f.Zone.Db.Add(new MText { Contents = "cut" });
            f.Report.elements[0].cad_entities.Add(FacadeQuantityStore.CaptureEntity(f.Zone.Tr,hatch,"hatch"));
            f.Report.elements[0].cad_entities.Add(FacadeQuantityStore.CaptureEntity(f.Zone.Tr,warning,"warning"));
            f.Store(); var read = f.Read(f.Zone.Hatch); Accepted(read,1);
            Require(read.Reports[0].elements.Count == 1 && read.Reports[0].elements[0].cad_entities.Count == 3,
                "graphic representations incorrectly counted as multiple physical pieces");
            warning.Contents = "changed mark"; Rejected(f.Read(f.Zone.Hatch));
        });
        Test("deleted_auxiliary_hatch_refused", () => {
            var f = new QuantityFixture("single","ATTILE",false); var hatch = f.Zone.Db.Add(new Hatch());
            f.Report.elements[0].cad_entities.Add(FacadeQuantityStore.CaptureEntity(f.Zone.Tr,hatch,"hatch"));
            f.Store(); hatch.Erase(); Rejected(f.Read(f.Zone.Hatch));
        });
        Test("layer_color_change_refused", () => {
            var f = new QuantityFixture("single","ATTILE",false); var layer = f.Zone.Db.Add(new LayerTableRecord());
            f.Piece.LayerId = layer.ObjectId;
            f.Report.elements[0].cad_entities[0] = FacadeQuantityStore.CaptureEntity(f.Zone.Tr,f.Piece,"outer");
            f.Store(); layer.Color.ColorIndex = 3; Rejected(f.Read(f.Zone.Hatch));
        });
        Test("piece_changed_between_capture_and_store_refused", () => {
            var f = new QuantityFixture("single","ATTILE",false); Shift(f.Piece,10,0);
            Throws(f.Store); Rejected(f.Read(f.Zone.Hatch));
        });
        Test("unsupported_sample_entity_capture_refused", () => {
            var f = new QuantityFixture(); var unsupported = f.Zone.Db.Add(new Entity());
            Throws(() => FacadeQuantityStore.CaptureEntity(f.Zone.Tr,unsupported,"block"));
        });
        Test("static_block_roundtrip_and_definition_change", () => {
            var f = new BlockFixture(); Accepted(f.F.Read(f.F.Zone.Hatch),1);
            Shift(f.F.Piece,30,0); Rejected(f.F.Read(f.F.Zone.Hatch));
        });
        Test("block_attribute_change_refused", () => {
            var f = new BlockFixture(); f.Attribute.TextString = "A-2"; Rejected(f.F.Read(f.F.Zone.Hatch));
        });
        Test("block_scale_change_refused", () => {
            var f = new BlockFixture(); f.Block.ScaleFactors = new Scale3d(2,1,1); Rejected(f.F.Read(f.F.Zone.Hatch));
        });
        Test("block_dynamic_property_change_refused", () => {
            var f = new BlockFixture(true); Accepted(f.F.Read(f.F.Zone.Hatch),1);
            f.Block.DynamicBlockReferencePropertyCollection[0].Value = 750.0; Rejected(f.F.Read(f.F.Zone.Hatch));
        });
        Test("whole_merged_group_deduplicates_shared_piece", () => {
            var f = new QuantityFixture("single","ATTILE",false); var second = AddMergedGroup(f);
            var read = f.Read(f.Zone.Hatch,second.Mark); Accepted(read,1);
            Require(read.SelectedZoneIds.Count == 2 && read.Reports[0].elements.Count == 1,"shared piece counted twice");
        });
        Test("partial_merged_group_selection_refused_without_stale_rows", () => {
            var f = new QuantityFixture("single","ATTILE",false); AddMergedGroup(f);
            Rejected(f.Read(f.Zone.Hatch));
        });
        Test("unselected_merged_group_source_change_refused", () => {
            var f = new QuantityFixture("single","ATTILE",false); var second = AddMergedGroup(f);
            Shift(second.Outers[0],50,0); Rejected(f.Read(f.Zone.Hatch,second.Hatch));
        });
        Test("partial_independent_zone_quantities_allowed_joint_cutting_refused", () => {
            var f = new QuantityFixture("single","ATTILE",false); var second = AddMergedGroup(f,false);
            var read = f.Read(f.Zone.Hatch); Accepted(read,1);
            Require(read.SelectedZoneIds.SequenceEqual(new[] {"F-1"}),"partial zone selection silently expanded");
            var installed = FacadeQuantitiesCore.BuildRows(read.Reports,read.SelectedZoneIds,true,false);
            Require(installed.ok && installed.rows.Sum(r=>r.quantity) == 1,"independent partial zone elements not limited");
            var cutting = FacadeQuantitiesCore.BuildRows(read.Reports,read.SelectedZoneIds,true,true);
            Require(!cutting.ok && cutting.rows.Count == 0 && cutting.issues.Any(i=>i.code == "Q_CUTTING_SCOPE_PARTIAL"),
                "joint cutting allocated to a selected subset");
            Accepted(f.Read(f.Zone.Hatch,second.Hatch),1);
        });
        Test("raw_contour_layout_roundtrip_and_same_area_move_refused", () => {
            var f = new QuantityFixture("single","ATTILE",false); var carrier = f.Zone.Outers[0];
            var source = LayoutGeometryGuard.Capture(f.Zone.Tr,f.Zone.Db,
                f.Zone.Outers.Concat(f.Zone.Holes).Select(p=>p.ObjectId),new ZoneGeometryResult[0]);
            WriteRecord(f.Zone.Tr,carrier,"ATTILE","{\"joints_x\":[0,600],\"rows_y\":[0,600]}");
            LayoutGeometryGuard.Store(f.Zone.Tr,f.Zone.Db,carrier,"ATTILE",source);
            f.Report.zone_ids = new List<string> {"RAW-1"};
            f.Report.elements[0].zone_id = "RAW-1"; f.Report.elements[0].zone_ids = new List<string> {"RAW-1"};
            f.Report.source_revisions["owner_zone:" + carrier.Handle.ToString()] = "RAW-1";
            FacadeQuantityStore.Store(f.Zone.Tr,f.Zone.Db,new Entity[] {carrier},"ATTILE",f.Report);
            Accepted(f.Read(carrier),1); Shift(f.Zone.Holes[0],50,0); Rejected(f.Read(carrier));
        });
        Test("foreign_database_selection_refused", () => {
            var first = new QuantityFixture(); var foreign = new QuantityFixture();
            var read = FacadeQuantityStore.ReadCladding(first.Zone.Tr,first.Zone.Db,new[] { first.Zone.Hatch.ObjectId, foreign.Zone.Hatch.ObjectId });
            Rejected(read);
        });
        Test("explicit_known_empty_run_stored_and_read_as_zero", () => {
            var f = new QuantityFixture("single","ATCLAD",false); f.Report.elements.Clear(); f.Piece.Erase();
            f.Store(); var read = f.Read(f.Zone.Hatch); Accepted(read,1);
            Require(read.Reports[0].elements != null && read.Reports[0].elements.Count == 0,
                "known zero result was replaced by missing data or fabricated elements");
            var rows = FacadeQuantitiesCore.BuildRows(read.Reports,read.SelectedZoneIds,true,false);
            Require(rows.ok && rows.rows.Count == 0,"known zero result not explicitly preserved");
        });
        Test("selected_known_empty_zone_of_mixed_run_is_zero", () => {
            var f = new QuantityFixture("single","ATCLAD",false); var empty = AddMergedGroup(f,false,true);
            var read = f.Read(empty.Hatch); Accepted(read,1);
            Require(read.SelectedZoneIds.SequenceEqual(new[] {"F-2"}) && read.Reports[0].elements.Count == 1,
                "empty selected zone lost scope or imported another zone's elements");
            var rows = FacadeQuantitiesCore.BuildRows(read.Reports,read.SelectedZoneIds,true,false);
            Require(rows.ok && rows.rows.Count == 0,"selected known empty zone emitted quantities from another zone");
            var full = f.Read(f.Zone.Hatch,empty.Hatch); Accepted(full,1);
            var fullRows = FacadeQuantitiesCore.BuildRows(full.Reports,full.SelectedZoneIds,true,false);
            Require(fullRows.ok && fullRows.rows.Sum(r=>r.quantity) == 1,"zero zone altered nonempty neighbour's count");
        });
        Test("null_element_list_is_missing_data_not_known_zero", () => {
            var f = new QuantityFixture("single","ATCLAD",false); f.Report.elements = null;
            Throws(f.Store); Rejected(f.Read(f.Zone.Hatch));
        });
        Test("payload_boundary_exactly_256_mib_accepted_without_allocation", () => {
            CheckPayloadBoundary(256L * 1024 * 1024);
        });
        Test("payload_boundary_one_byte_over_256_mib_refused_without_allocation", () => {
            Throws(() => CheckPayloadBoundary(256L * 1024 * 1024 + 1));
            Throws(() => CheckPayloadBoundary(long.MaxValue));
        });
        Test("malformed_selected_piece_stamp_has_actionable_russian_refusal", () => {
            var f = new QuantityFixture();
            WriteRecord(f.Zone.Tr,f.Piece,FacadeQuantityStore.ElementKey,"{invalid-json");
            var read = f.Read(f.Piece); Rejected(read);
            Require(read.Reason.StartsWith("Повреждён паспорт объекта облицовки. Восстановите",StringComparison.Ordinal),
                "malformed selected passport exposed a serializer diagnostic");
        });
        Test("malformed_unselected_stamp_scan_refuses_unknown_run_with_russian_reason", () => {
            var f = new QuantityFixture(); var unrelated = f.Zone.Db.Add(new Line());
            WriteRecord(f.Zone.Tr,unrelated,FacadeQuantityStore.ElementKey,"{invalid-json");
            var read = f.Read(f.Zone.Hatch); Rejected(read);
            Require(read.Reason.StartsWith("Повреждён паспорт объекта облицовки. Восстановите",StringComparison.Ordinal),
                "unknown-run corrupted stamp was ignored or exposed a serializer diagnostic");
        });
        Test("frame_and_cladding_passports_coexist_without_axis_stamp_overwrite", () => {
            var f=new FrameFixture();f.Fresh();var z=f.Cladding.Zone;
            string current=String.Concat(Record(z.Tr,z.Hatch,"ATLAYOUT_CURRENT").Data
                .Where(v=>v.TypeCode==(int)DxfCode.Text).Select(v=>(string)v.Value));
            Require(current==f.CladdingCurrent,"frame storage overwrote current cladding layout");
            Require(f.Sources.sources.Select(s=>s.handle).Distinct().Count()==f.Sources.sources.Count,"frame source graph contains duplicate objects");
            Require(!f.Sources.sources.Any(s=>s.handle==f.Cladding.Piece.Handle.ToString()),"frame source guard traversed cladding rendering entities");
        });
        Test("frame_read_reuses_ready_rows_and_stable_selection_fingerprint", () => {
            var f=new FrameFixture();var a=f.Read(f.Cladding.Zone.Hatch);var b=f.Read(f.Cladding.Zone.Hatch,f.Cladding.Zone.Mark);
            Accepted(a,1);Accepted(b,1);Require(a.Fingerprint==b.Fingerprint,"redundant carrier changes selection fingerprint");
            Require(a.Rows!=null && a.Rows.ok,"frame caller would need to reaggregate missing rows");
        });
        Test("frame_deleted_element_refused_cladding_still_current", () => {
            var f=new FrameFixture();f.Rail.Erase();Rejected(f.Read());f.Cladding.Fresh();
        });
        Test("frame_changed_length_refused_after_prior_successful_read", () => {
            var f=new FrameFixture();f.Fresh();f.Rail.EndPoint=new Point3d(300,1200,0);Rejected(f.Read());
        });
        Test("frame_copied_element_detected_without_affecting_cladding", () => {
            var f=new FrameFixture();f.Fresh();var z=f.Cladding.Zone;
            var copy=z.Db.Add(new Line {StartPoint=f.Rail.StartPoint,EndPoint=f.Rail.EndPoint});
            CopyRecords(z.Tr,f.Rail,copy);Rejected(f.Read());f.Cladding.Fresh();
        });
        Test("frame_same_area_source_window_move_refused_after_read", () => {
            var f=new FrameFixture();f.Fresh();Shift(f.Cladding.Zone.Holes[0],100,0);
            f.Cladding.Zone.RefreshHatchFromSources();Rejected(f.Read());
        });
        Test("frame_hatch_style_mutation_refused", () => {
            var f=new FrameFixture();f.Cladding.Zone.Hatch.HatchStyle=HatchStyle.Ignore;Rejected(f.Read());
        });
        Test("frame_hatch_associativity_mutation_refused", () => {
            var f=new FrameFixture();f.Cladding.Zone.Hatch.Associative=false;Rejected(f.Read());
        });
        Test("frame_hatch_associated_source_pointer_mutation_refused", () => {
            var f=new FrameFixture();var z=f.Cladding.Zone;
            z.Hatch.AssociatedIds[0]=new ObjectIdCollection(new[]{z.Holes[0].ObjectId});Rejected(f.Read());
        });
        Test("frame_cladding_axes_metadata_change_refused", () => {
            var f=new FrameFixture();var z=f.Cladding.Zone;
            WriteRecord(z.Tr,z.Hatch,"ATTILE","{\"joints_x\":[0,700,1400],\"rows_y\":[0,600,1200]}");Rejected(f.Read());
        });
        Test("frame_source_changed_between_capture_and_store_refused", () => {
            var f=new FrameFixture(false);Shift(f.Cladding.Zone.Holes[0],50,0);Throws(f.Store);Rejected(f.Read());
        });
        Test("frame_incomplete_legacy_clamps_marker_blocks_frame_only", () => {
            var f=new FrameFixture();var z=f.Cladding.Zone;
            FacadeQuantityStore.MarkFrameUnavailable(z.Tr,z.Db,new Entity[]{z.Hatch,z.Mark},"Требуется полное построение ATFRAME для подтверждения состава.");
            var read=f.Read();Rejected(read);Require(read.Reason.Contains("полное построение ATFRAME"),"unavailable-frame reason lost");
            f.Cladding.Fresh();
        });
        Test("frame_owner_label_tamper_refused", () => {
            var f=new FrameFixture();var z=f.Cladding.Zone;
            WriteRecord(z.Tr,z.Hatch,"ATFRAME",Json.Serialize(new Dictionary<string,object>{{"owner",z.Hatch.Handle.ToString()},{"mode","manual"},{"rail_count",99}}));
            Rejected(f.Read());
        });
        Test("frame_copied_owner_stamp_refused", () => {
            var f=new FrameFixture();var z=f.Cladding.Zone;var copy=z.Db.Add(new MText());
            CopyRecords(z.Tr,z.Mark,copy);Rejected(f.Read(copy));
        });
        Test("frame_known_zero_result_preserved", () => {
            var f=new FrameFixture(false);f.Report.elements.Clear();f.Rail.Erase();f.Store();
            var read=f.Read();Accepted(read,1);Require(read.Rows.ok && read.Rows.rows.Count==0,"known zero frame fabricated physical parts");
        });
        Test("frame_source_capture_rejects_legacy_zone_without_geometry", () => {
            var f=new QuantityFixture();var z=f.Zone;
            ((DBDictionary)z.Tr.GetObject(z.Hatch.ExtensionDictionary,OpenMode.ForWrite)).Remove(ZoneGeometryGuard.Key);
            Throws(()=>FacadeQuantityStore.CaptureFrameSources(z.Tr,z.Db,new[]{z.Hatch.ObjectId}));
        });
        Test("all_piece_selection_matches_owner_scope_counts_and_fingerprint", () => {
            var f=new QuantityFixture("single","ATTILE",false);
            var second=AddCladdingPiece(f,"synthetic-piece-2",1000);var third=AddCladdingPiece(f,"synthetic-piece-3",2000);f.Store();
            var owners=f.Read(f.Zone.Hatch,f.Zone.Mark);var pieces=f.Read(f.Piece,second,third,second);
            Accepted(owners,1);Accepted(pieces,1);
            Require(pieces.Rows!=null && pieces.Rows.ok && pieces.Rows.rows.Sum(r=>r.quantity)==3,"selection of every piece changes physical quantity");
            Require(pieces.Fingerprint==owners.Fingerprint,"same complete scope has different owner/piece selection fingerprints");
        });
        Test("conflicting_selected_piece_digest_refused", () => {
            var f=new QuantityFixture("single","ATTILE",false);var second=AddCladdingPiece(f,"synthetic-piece-2",1000);f.Store();
            var record=Record(f.Zone.Tr,second,FacadeQuantityStore.ElementKey);
            var stamp=Json.Deserialize<FacadeQuantityStore.Stamp>(String.Concat(record.Data.Where(v=>v.TypeCode==(int)DxfCode.Text).Select(v=>(string)v.Value)));
            stamp.entry_digest=new String('0',64);WriteRecord(f.Zone.Tr,second,FacadeQuantityStore.ElementKey,Json.Serialize(stamp));
            Rejected(f.Read(f.Piece,second));
        });
        Test("frame_retained_rail_plus_new_clamp_is_one_complete_generation", () => {
            var f=new FrameFixture();var z=f.Cladding.Zone;
            var sources=FacadeQuantityStore.CaptureFrameSources(z.Tr,z.Db,new[]{z.Hatch.ObjectId,z.Mark.ObjectId,f.Rail.ObjectId});
            var next=Json.Deserialize<QuantityReport>(Json.Serialize(f.Report));next.report_id="frame-report-2";next.run_id="frame-run-2";
            var clamp=z.Db.Add(new Circle {Center=new Point3d(300,500,0),Radius=5});
            next.elements.Add(new QuantityElement {element_id="frame-clamp-2",zone_id="F-1",zone_ids=new List<string>{"F-1"},
                role="clamp",type="synthetic clamp",system="synthetic-test-system",origin="test",
                cad_entities=new List<QuantityCadEntity>{FacadeQuantityStore.CaptureEntity(z.Tr,clamp,"clamp")}});
            foreach(var owner in new Entity[]{z.Hatch,z.Mark})
                WriteRecord(z.Tr,owner,"ATFRAME",Json.Serialize(new Dictionary<string,object>{{"owner",owner.Handle.ToString()},{"mode","retained_rails_plus_new_clamps"}}));
            FacadeQuantityStore.StoreFrame(z.Tr,z.Db,new Entity[]{z.Hatch,z.Mark},next,sources);
            var read=f.Read();Accepted(read,1);
            Require(read.Reports[0].run_id==next.run_id && read.Reports[0].elements.Count==2,"complete retained/new generation lost or duplicated elements");
            Require(read.Rows.ok && read.Rows.rows.Sum(r=>r.quantity)==2 && read.Reports[0].estimates.Count==0,
                "retained rail, new clamp or absent new anchor estimate counted incorrectly");
            f.Cladding.Fresh();
        });
        Test("actual_published_schema1_xrecords_remain_readable", () => {
            var f=new QuantityFixture();var z=f.Zone;var saved=Items(Fixtures["legacy_xrecords"]);
            var objects=(Dictionary<long,DBObject>)typeof(Database).GetField("objects",
                System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(z.Db);
            Require(saved.Length==14 && objects.Values.OfType<Xrecord>().Count()==14,"old/current synthetic fixture object topology differs");
            foreach(var raw in saved) {
                var record=Dict(raw);var id=z.Db.GetObjectId(false,new Handle(Convert.ToInt64((string)record["handle"],16)),0);
                var xr=z.Tr.GetObject(id,OpenMode.ForWrite) as Xrecord;Require(xr!=null,"old record destination is not an Xrecord");
                xr.Data=new ResultBuffer(Items(record["values"]).Select(v=> {
                    var value=Dict(v);int code=Convert.ToInt32(value["code"]);string text=(string)value["value"];
                    return new TypedValue(code,code==(int)DxfCode.SoftPointerId ?
                        (object)z.Db.GetObjectId(false,new Handle(Convert.ToInt64(text,16)),0) : (object)text);
                }).ToArray());
            }
            // These are the old writer's complete bytes and digests, not a new
            // report with its schema changed or a digest recomputed by this test.
            var read=f.Read(z.Hatch,z.Mark);Accepted(read,1);
            Require(read.Rows.ok && read.Rows.rows.Sum(r=>r.quantity)==1 &&
                Math.Abs(read.Rows.rows.Sum(r=>r.area_m2??0)-0.36)<1e-9,"published v1 quantity result changed");
        });
        HatchEdgeCases();
        int passed = Results.Count(r => (string)r["status"] == "PASS");
        File.WriteAllText(args[3],Json.Serialize(new Dictionary<string,object> {
            {"scope","Actual production quantity store and actual geometry guards with CAD doubles. No AutoCAD runtime claimed."},
            {"total",Results.Count}, {"passed",passed}, {"failed",Results.Count-passed}, {"cases",Results} }));
        Console.WriteLine("Quantity store: " + passed + "/" + Results.Count);
        return passed == Results.Count ? 0 : 1;
    }
}
