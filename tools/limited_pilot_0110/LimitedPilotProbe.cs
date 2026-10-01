using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using ACladPlugin;
using AFramePlugin;
using AFacadesPlugin;
using FacadeSafety;
using ProjectStore = FacadeSafety.FacadeProjectParameterStore;

// One persistent managed CAD-double database. Engines run in Python via JSON
// lines, between source capture and consumption. No native DWG file is created.
internal static partial class LimitedPilotProbe
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly Database Db = new Database();
    static readonly List<object> Ledger = new List<object>();
    static readonly List<string> Checks = new List<string>();
    static readonly List<Zone> Zones = new List<Zone>();
    static string Output;
    static partial void CommandCheckpoint(string stage);
    sealed class Zone
    {
        internal string Id, Fingerprint;
        internal Hatch Hatch; internal MText Mark; internal Polyline Outer;
        internal Dictionary<string, object> Part, TileMetadata;
        internal QuantityReport Cladding, Frame;
        internal List<Entity> FrameEntities = new List<Entity>();
        internal FrameNodeSnapshot Node;
        internal NodeFreshnessPredicate.Saved NodeSaved;
    }
    sealed class Prepared
    {
        internal Zone Zone; internal FrameSettings Settings;
        internal Dictionary<string, object> Request;
        internal FacadeQuantityStore.FrameSources Sources;
        internal FrameParameterResolution Resolution;
    }
    static Dictionary<string, object> D(object v) { return (Dictionary<string, object>)v; }
    static object[] A(object v) { return (object[])v; }
    static double N(object v) { return Convert.ToDouble(v); }
    static void Need(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); Checks.Add(reason); }
    static void Save(string name, object value) { string path = Path.Combine(Output, name + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, Json.Serialize(value), new UTF8Encoding(false)); }
    static void Emit(object value) { Console.WriteLine(Json.Serialize(value)); Console.Out.Flush(); }
    static Dictionary<string, object> Engine(string tag, string engine, Dictionary<string, object> request)
    {
        string before = Json.Serialize(request);
        Emit(new { event_type = "engine", tag, engine, request });
        string line = Console.ReadLine(); if (line == null) throw new InvalidOperationException("Engine transport ended");
        var response = D(Json.DeserializeObject(line));
        Need(response.ContainsKey("ok") && object.Equals(response["ok"], true), "engine accepted " + tag);
        Need(before == Json.Serialize(request), "engine transport preserves request " + tag);
        return response;
    }
    static void Write(Transaction tr, Entity e, string key, object value)
    {
        if (e.ExtensionDictionary.IsNull) e.CreateExtensionDictionary();
        var ext = (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForWrite);
        var data = new ResultBuffer(new TypedValue((int)DxfCode.Text, Json.Serialize(value)));
        if (ext.Contains(key)) ((Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForWrite)).Data = data;
        else ext.SetAt(key, new Xrecord { Data = data, XlateReferences = true });
    }
    static Dictionary<string, object> Read(Transaction tr, Entity e, string key)
    {
        var ext = (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForRead);
        var record = (Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForRead);
        return D(Json.DeserializeObject(string.Concat(record.Data.Where(v => v.TypeCode == (int)DxfCode.Text).Select(v => (string)v.Value))));
    }
    static Polyline Polygon(double[][] points)
    {
        var p = Db.Add(new Polyline());
        for (int i = 0; i < points.Length; ++i) p.AddVertexAt(i, new Point2d(points[i][0], points[i][1]), 0, 0, 0);
        p.Closed = true; return p;
    }
    static FrameParameterResolution Resolve(Transaction tr, Zone z)
    {
        var zone = ZoneGeometryGuard.Verify(tr, Db, z.Hatch, null); Need(zone.Ok, "real zone guard fresh " + z.Id);
        var p = ProjectStore.ReadProject(tr, Db); var b = ProjectStore.ReadZone(tr, z.Hatch, zone.Fingerprint);
        return FrameParameterResolver.Resolve(FrameProjectParameters.FromDict(p.Payload), p.RecordDigest,
            FrameZoneParameters.FromDict(b.Payload), b.RecordDigest, z.Hatch.Handle.ToString(), z.Id);
    }
    static void Observe(string stage)
    {
        using (var tr = new Transaction(Db))
        {
            var project = ProjectStore.ReadProject(tr, Db);
            Ledger.Add(new { stage, project = project.Payload, project_record_digest = project.RecordDigest,
                zones = Zones.Select(z => new { zone_id = z.Id, owner = z.Hatch.Handle.ToString(), geometry = z.Fingerprint, source_contour = z.Outer.Handle.ToString(), xmin = z.Outer.GetPoint2dAt(0).X,
                    context = Resolve(tr, z).Context.ToDict(), cladding_run = z.Cladding == null ? null : z.Cladding.run_id,
                    frame_run = z.Frame == null ? null : z.Frame.run_id,
                    source_run = z.Frame == null ? null : z.Frame.connection_passports[0].source_run_id,
                    physical = z.Frame == null ? null : z.Frame.elements.Select(e => new { e.element_id, e.role, e.mark, e.product_id, e.cad_entities }).ToArray(),
                    node_digest = z.Node == null ? null : z.Node.SnapshotDigest }).ToArray() });
        }
    }
    static void CreateZones(FrameSolutionSelection selection)
    {
        var contours = new List<object>(); var outers = new Dictionary<string, Polyline>();
        foreach (double x in new[] { 0.0, 2410.0 })
        {
            double[][] points = { new[] { x, 0.0 }, new[] { x + 1210, 0.0 }, new[] { x + 1210, 1820.0 }, new[] { x, 1820.0 } };
            var p = Polygon(points); outers.Add(p.Handle.ToString(), p);
            contours.Add(new { id = p.Handle.ToString(), pts = points });
        }
        var request = new Dictionary<string, object> { { "op", "zones" }, { "units", "mm" }, { "contours", contours },
            { "cladding", "Керамогранит 600×600" }, { "zone_prefix", "П-" }, { "start_index", 1 }, { "merge", false } };
        var response = Engine("zones", "zones", request);
        using (var tr = new Transaction(Db))
        {
            var project = FrameProjectParameters.CreateNext(null, selection, "11111111111111111111111111111111");
            ProjectStore.WriteProject(tr, Db, ProjectStore.ReadProject(tr, Db), project.ToDict());
            foreach (var raw in A(response["zones"]))
            {
                var item = D(raw); string id = (string)item["zone_id"];
                var part = A(response["zones_full"]).Select(D).Single(p => (string)p["id"] == id);
                var outer = outers[(string)A(item["outer_ids"])[0]];
                var z = new Zone { Id = id, Part = part, Outer = outer, Hatch = Db.Add(new Hatch()), Mark = Db.Add(new MText()) };
                z.Hatch.Area = N(D(item["report"])["area_net_m2"]) * 1e6;
                var loop = new HatchLoop { LoopType = HatchLoopTypes.External };
                for (int i = 0; i < outer.NumberOfVertices; ++i) loop.Polyline.Add(new BulgeVertex(outer.GetPoint2dAt(i), 0));
                z.Hatch.Loops.Add(loop); z.Hatch.AssociatedIds.Add(new ObjectIdCollection(new[] { outer.ObjectId }));
                var data = new Dictionary<string, object> { { "zone_id", id }, { "cladding", item["cladding"] }, { "report", item["report"] } };
                Write(tr, z.Hatch, "ATFZONE", data); Write(tr, z.Mark, "ATFZONE", data);
                var captured = ZoneGeometryGuard.Capture(tr, Db, z.Hatch, z.Mark, data, new List<Dictionary<string, object>> { part },
                    new List<ObjectId> { outer.ObjectId }, new List<ObjectId>());
                Need(captured.Ok, "actual zone capture " + id); z.Fingerprint = captured.Fingerprint;
                var binding = FrameZoneParameters.CreateNext(null, project, new Dictionary<string, FrameParameterOverride>());
                ProjectStore.WriteZone(tr, z.Hatch, z.Fingerprint, ProjectStore.ReadZone(tr, z.Hatch), binding.ToDict());
                Zones.Add(z);
            }
            tr.Commit();
        }
        Need(Zones.Count == 2, "two independent canonical zones"); Observe("zones_and_project");
    }
    static void Tile(Zone z)
    {
        LayoutGeometryGuard.Snapshot snapshot; Dictionary<string, object> request;
        var settings = new BondSettings { Name = "Керамогранит 600×600", W = 600, H = 600, Gv = 10, Gh = 10,
            Kind = "none", AnchorH = "L", AnchorV = "B", GapAround = false, Merge = false, Shaped = "keep", WarnCut = 30 };
        Need(settings.Validate() == null, "actual ATTILE settings valid " + z.Id);
        using (var tr = new Transaction(Db))
        {
            var verified = ZoneGeometryGuard.Verify(tr, Db, z.Hatch, null); Need(verified.Ok, "ATTILE source verified");
            snapshot = LayoutGeometryGuard.Capture(tr, Db, new ObjectId[0], new[] { verified });
            request = settings.ToEngine(); request["op"] = "tile_pattern"; request["types"] = new[] { settings.Name };
            request["zones"] = verified.Parts.Select(p => new { zone_id = p["id"], zone = p }).ToArray();
        }
        Ledger.Add(new { stage = "prepare_attile", z.Id, layout_fingerprint = snapshot.Fingerprint, layout_revision = snapshot.Revision, sources = snapshot.Sources });
        var response = Engine("attile_" + z.Id, "cladding", request);
        using (var tr = new Transaction(Db))
        {
            string rawResponse = Json.Serialize(response), rawRequest = Json.Serialize(request);
            var report = CladdingQuantities.BuildReport("ATTILE", response, request, new Dictionary<string, string>(), settings.Material, 600, 600);
            Need(rawResponse == Json.Serialize(response) && rawRequest == Json.Serialize(request), "actual cladding producer preserves engine request and response");
            Need(report.elements.Count == 6 && report.elements.Sum(e => e.area_mm2.Value) == 2160000, "six full actual producer tiles " + z.Id);
            foreach (var e in report.elements)
            {
                var entity = Polygon(e.rings[0].points); e.cad_entities.Add(FacadeQuantityStore.CaptureEntity(tr, entity, "outer"));
            }
            z.TileMetadata = D(A(response["per_zone"])[0]);
            Write(tr, z.Hatch, "ATTILE", z.TileMetadata);
            LayoutGeometryGuard.Store(tr, Db, z.Hatch, "ATTILE", snapshot);
            report.source_revisions["owner_zone:" + z.Hatch.Handle] = z.Id;
            report.source_revisions["layout_revision"] = snapshot.Revision;
            report.source_revisions["layout_geometry"] = snapshot.Fingerprint;
            FacadeQuantityStore.Store(tr, Db, new Entity[] { z.Hatch }, "ATTILE", report); z.Cladding = report;
            Need(FacadeQuantityStore.ReadCladding(tr, Db, new[] { z.Hatch.ObjectId }).Ok, "actual cladding store reads fresh " + z.Id);
            Save("initial/" + z.Id + "_cladding_report", report); tr.Commit();
        }
    }
    static Prepared PrepareFrame(Zone z, string tag)
    {
        using (var tr = new Transaction(Db))
        {
            var verified = ZoneGeometryGuard.Verify(tr, Db, z.Hatch, null); Need(verified.Ok, "frame zone fresh before input");
            Need(LayoutGeometryGuard.Verify(tr, Db, z.Hatch, "ATTILE").Ok, "actual ATTILE axes source verified before frame");
            var resolution = Resolve(tr, z);
            var settings = new FrameSettings { Steps = "manual", Mode = "frame", StepMain = 800, StepCorner = 800, Axes = "points" };
            settings.ApplyProjectParameters(resolution); Need(settings.Validate(true) == null, "actual FrameSettings accepts bound manual pilot");
            var source = FacadeQuantityStore.CaptureFrameSources(tr, Db, new[] { z.Hatch.ObjectId }, ProjectStore.Dependency(ProjectStore.ReadProject(tr, Db)));
            var request = settings.EngineParams(0); var layout = Read(tr, z.Hatch, "ATTILE");
            request["op"] = "frame"; request["floors_y"] = new object[0];
            request["zones"] = verified.Parts.Select(p => new { zone_id = p["id"], zone = p, joints_x = layout["joints_x"], rows_y = layout["rows_y"] }).ToArray();
            Ledger.Add(new { stage = "prepare_frame", tag, zone = z.Id, context = resolution.Context.ToDict(), sources = source, settings = settings.ToDict() });
            return new Prepared { Zone = z, Settings = settings, Resolution = resolution, Request = request, Sources = source };
        }
    }
    static void ConsumeFrame(Prepared prepared, Dictionary<string, object> response, string tag)
    {
        var z = prepared.Zone;
        string rawResponse = Json.Serialize(response), rawRequest = Json.Serialize(prepared.Request);
        prepared.Settings.SolutionSelection.ValidateEngineReport(response["solution_report"], false);
        var report = FrameQuantities.BuildReport(response, prepared.Request, new Dictionary<string, string>());
        new FrameQuantities(report).SetProjectParameters(FrameParameterResolver.RunSnapshot(new[] { prepared.Resolution.Context }));
        Need(rawResponse == Json.Serialize(response) && rawRequest == Json.Serialize(prepared.Request), "actual frame producer preserves engine request and response " + tag);
        using (var tr = new Transaction(Db))
        {
            var entities = new List<Entity>();
            foreach (var e in report.elements)
            {
                string[] key = e.element_id.Split(':'); int index = int.Parse(key[2]); var piece = D(A(response[key[1]])[index]); Entity entity;
                if (e.role == "rail") entity = Db.Add(new Line { StartPoint = new Point3d(N(piece["x"]), N(piece["y0"]), 0), EndPoint = new Point3d(N(piece["x"]), N(piece["y1"]), 0) });
                else entity = Db.Add(new Circle { Center = new Point3d(N(piece["x"]), N(piece["y"]), 0), Radius = 75 });
                entities.Add(entity); e.cad_entities.Add(FacadeQuantityStore.CaptureEntity(tr, entity, "outer"));
            }
            Need(report.elements.Count(e => e.role == "rail") == 1 && report.elements.Count(e => e.role == "bracket") == 3, "fixed normal frame composition " + tag);
            Need(report.elements.Single(e => e.role == "rail").length_mm == 1820 && report.elements.All(e => e.product_id == null), "axis length and unassigned catalogue products " + tag);
            foreach (var old in z.FrameEntities) old.Erase();
            var metadata = prepared.Settings.ToDict(); metadata["owner"] = z.Hatch.Handle.ToString(); Write(tr, z.Hatch, "ATFRAME", metadata);
            report.source_revisions["owner_zone:" + z.Hatch.Handle] = z.Id;
            FacadeQuantityStore.StoreFrame(tr, Db, new Entity[] { z.Hatch }, report, prepared.Sources);
            z.Frame = report; z.FrameEntities = entities;
            Need(FacadeQuantityStore.ReadFrame(tr, Db, new[] { z.Hatch.ObjectId }).Ok, "new actual stored frame fresh " + tag);
            Save(tag + "/" + z.Id + "_frame_report", report); tr.Commit();
        }
        Node(z, tag);
    }
    static void GenerateFrame(Zone z, string tag) { var prepared = PrepareFrame(z, tag); ConsumeFrame(prepared, Engine(tag + "_frame_" + z.Id, "frame", prepared.Request), tag); }
    static void Node(Zone z, string tag)
    {
        using (var tr = new Transaction(Db))
        {
            var resolution = Resolve(tr, z);
            var input = new FrameNodeGeometryInput { profile_near_face_x_mm = 170, clearance_surface = new FrameNodeClearanceSurface { kind = FrameNodeClearanceSurface.Layers } };
            z.Node = FrameNodeSnapshot.Create(resolution.Selection, resolution.Context, input, "2026-10-01T00:00:00.0000000Z");
            var roundtrip = FrameNodeSnapshot.FromDict(Json.DeserializeObject(Json.Serialize(z.Node.ToDict())));
            Need(roundtrip.SnapshotDigest == z.Node.SnapshotDigest, "actual node snapshot JSON roundtrip " + tag);
            var drawing = FrameNodeDrawingBuilder.Build(z.Node.Result, z.Node.CaptionLines());
            var mounting = FrameMountingAssessment.Evaluate(z.Node.Selection, z.Node.Result);
            Need(z.Node.Result.GapMm == 20 && z.Node.Result.ClearanceStatus == "pass" && mounting.MountingStatus == "not_confirmed" && !mounting.AutomaticBracketSelectionAllowed,
                "node min20 is separate from blocked mounting " + tag);
            z.NodeSaved = new NodeFreshnessPredicate.Saved { ZoneId = z.Hatch.ObjectId, ZoneHandle = z.Hatch.Handle.ToString(), ZoneFingerprint = z.Fingerprint, Snapshot = z.Node };
            Save(tag + "/" + z.Id + "_node", new { snapshot = z.Node.ToDict(), drawing = drawing.ToDict(), mounting = mounting.ReviewText() });
        }
    }
    static bool NodeFresh(Zone z)
    {
        using (var tr = new Transaction(Db))
        {
            var zone = ZoneGeometryGuard.Verify(tr, Db, z.Hatch, null); Need(zone.Ok, "node predicate actual zone source fresh");
            var current = new NodeFreshnessPredicate.Current { ZoneId = z.Hatch.ObjectId, ZoneHandle = z.Hatch.Handle.ToString(), Fingerprint = zone.Fingerprint, Resolution = Resolve(tr, z) };
            bool fresh = NodeFreshnessPredicate.Check(current, z.NodeSaved);
            Ledger.Add(new { stage = "node_source_predicate", zone = z.Id, fresh, saved_digest = z.Node.SnapshotDigest, saved_context = z.Node.Context.ToDict(), current_context = current.Resolution.Context.ToDict(), actual_command_executed = false });
            return fresh;
        }
    }
    static bool FrameFresh(Zone z)
    {
        using (var tr = new Transaction(Db))
        {
            var read = FacadeQuantityStore.ReadFrame(tr, Db, new[] { z.Hatch.ObjectId });
            Ledger.Add(new { stage = "actual_frame_freshness", zone = z.Id, fresh = read.Ok, reason = read.Reason, warnings = read.Warnings, saved_run = z.Frame.run_id });
            return read.Ok;
        }
    }
    static void Export(string tag)
    {
        CadCounters.Reset(); var watch = Stopwatch.StartNew();
        using (var tr = new Transaction(Db))
        {
            var ids = Zones.Select(z => z.Hatch.ObjectId).ToArray();
            var cladding = FacadeQuantityStore.ReadCladding(tr, Db, ids); var frame = FacadeQuantityStore.ReadFrame(tr, Db, ids);
            Need(cladding.Ok && frame.Ok, "both actual quantity stores fresh before export " + tag);
            var cq = FacadeQuantitiesCore.BuildRows(cladding.Reports, cladding.SelectedZoneIds, true, true);
            var fq = FacadeQuantitiesCore.BuildRows(frame.Reports, frame.SelectedZoneIds, true, true);
            Need(cq.ok && fq.ok, "actual quantity core accepts stored reads " + tag);
            Need(cq.rows.Where(r => r.basis == "installed").Sum(r => r.quantity) == 12, "aggregate twelve tiles " + tag);
            var views = new Dictionary<string, QuantityTableView> {
                { "cladding", QuantityTableView.FromCladding(CladdingTableData.Build(cq, cladding.Reports, cladding.SelectedZoneIds, cladding.Warnings, true, true)) },
                { "frame", QuantityTableView.FromFrame(FrameTableData.Build(fq, frame.Reports, frame.SelectedZoneIds, frame.Warnings, true)) },
                { "connections", QuantityTableView.FromConnections(ConnectionTableData.Build(fq, frame.Reports, frame.SelectedZoneIds, frame.Warnings, true)) } };
            foreach (var pair in views)
            {
                var rows = pair.Value.Rows("Ограниченный пилот: монтаж и расчёт не подтверждены");
                Save(tag + "/" + pair.Key + "_rows", rows);
                string path = Path.Combine(Output, tag, pair.Key + ".xlsx");
                using (var file = new QuantityXlsxFile(path, pair.Key, rows)) { file.Publish(); file.Complete(); }
                byte[] before = File.ReadAllBytes(path);
                using (var file = new QuantityXlsxFile(path, pair.Key, new List<object[]> { new object[] { "cancelled staging" } })) { file.Publish(); }
                Need(before.SequenceEqual(File.ReadAllBytes(path)), "actual XLSX publish disposal restores prior file " + tag + "/" + pair.Key);
            }
            Save(tag + "/quantities", new { cladding = cq, frame = fq });
            Ledger.Add(new { stage = "read_tables_xlsx", tag, milliseconds = watch.Elapsed.TotalMilliseconds, counters = CadCounters.Snapshot() });
        }
    }
    static void ChangeProject(double front)
    {
        using (var tr = new Transaction(Db))
        {
            var before = ProjectStore.ReadProject(tr, Db); var previous = FrameProjectParameters.FromDict(before.Payload);
            var choice = previous.defaults.Clone(); choice.geometry.cladding_front_offset_mm = front;
            ProjectStore.WriteProject(tr, Db, before, FrameProjectParameters.CreateNext(previous, choice).ToDict()); tr.Commit();
        }
    }
    static void ChangeZone(Zone z, double front)
    {
        using (var tr = new Transaction(Db))
        {
            var project = FrameProjectParameters.FromDict(ProjectStore.ReadProject(tr, Db).Payload); var old = ProjectStore.ReadZone(tr, z.Hatch);
            var binding = FrameZoneParameters.CreateNext(FrameZoneParameters.FromDict(old.Payload), project,
                new Dictionary<string, FrameParameterOverride> { { "geometry.cladding_front_offset_mm", FrameParameterOverride.Set(front) } });
            ProjectStore.WriteZone(tr, z.Hatch, z.Fingerprint, old, binding.ToDict()); tr.Commit();
        }
    }
    static void Run(FrameSolutionSelection selection)
    {
        CreateZones(selection);
        foreach (var z in Zones) { Tile(z); GenerateFrame(z, "initial"); }
        Observe("initial_fresh"); Export("initial"); CommandCheckpoint("initial");
        var pending = PrepareFrame(Zones[0], "pending_before_project_change");
        var pendingResponse = Engine("pending_before_project_change", "frame", pending.Request);
        ChangeProject(240); Observe("project_changed_before_reissue"); CommandCheckpoint("project_changed");
        Need(Zones.All(z => !FrameFresh(z) && !NodeFresh(z)), "project change invalidates both old frames and node source predicates");
        using (var tr = new Transaction(Db))
        {
            var report = FrameQuantities.BuildReport(pendingResponse, pending.Request, new Dictionary<string, string>());
            CadCounters.Reset(); string refusal = null;
            try { FacadeQuantityStore.StoreFrame(tr, Db, new Entity[] { pending.Zone.Hatch }, report, pending.Sources); }
            catch (InvalidOperationException error) { refusal = error.Message; }
            Need(refusal != null && refusal.Contains("Параметры проекта изменились. Выполните полное построение ATFRAME; прежняя ведомость устарела.") && CadCounters.DictionarySet == 0 && CadCounters.XrecordWrites == 0, "stale prepared response refused before writes with its original captured dependency");
            Ledger.Add(new { stage = "stale_prepared_refused", reason = refusal, sources = pending.Sources, counters = CadCounters.Snapshot() });
        }
        foreach (var z in Zones) GenerateFrame(z, "project_reissue");
        Need(Zones.All(z => FrameFresh(z) && NodeFresh(z)), "new source captures and new engine runs reissue both zones");
        Observe("project_reissued"); Export("project_reissue"); CommandCheckpoint("project_reissued");
        var target = Zones.Single(z => z.Outer.GetPoint2dAt(0).X > 0); var other = Zones.Single(z => z != target);
        string unaffectedRun = other.Frame.run_id, unaffectedNode = other.Node.SnapshotDigest;
        ChangeZone(target, 260); Observe("zone_override_before_reissue"); CommandCheckpoint("zone_changed");
        Need(!FrameFresh(target) && !NodeFresh(target) && FrameFresh(other) && NodeFresh(other), "local override invalidates only its independent frame/node scope");
        GenerateFrame(target, "zone_reissue");
        Need(other.Frame.run_id == unaffectedRun && other.Node.SnapshotDigest == unaffectedNode, "unaffected zone retains exact run IDs and node snapshot");
        Need(Zones.All(z => FrameFresh(z) && NodeFresh(z)), "final isolated scopes both fresh");
        Observe("zone_reissued"); Export("zone_reissue");
        using (var tr = new Transaction(Db))
        {
            var mixed = Zones.ToDictionary(z => z.Id, z => Resolve(tr, z));
            Need(FrameParameterResolver.ValidateGroup(mixed, new string[0]) != null, "actual resolver refuses combined mixed final selections");
            Need(Zones.All(z => FacadeQuantityStore.ReadCladding(tr, Db, new[] { z.Hatch.ObjectId }).Ok), "project and zone declaration changes preserve independent ATTILE quantities");
        }
        CommandCheckpoint("final");
        Save("ledger", Ledger);
        Save("native_result", new { verification = "PASS", checks = Checks.Count, assertions = Checks,
            database_scope = "one persistent managed CAD-double Database", engineering = "BLOCKED",
            node_scope = "actual pure snapshot/drawing and extracted source predicate; node command/renderer/store NOT_RUN",
            transaction_scope = "QuantityCadDoubles Commit/Dispose are no-ops; no CAD rollback claim", live_autocad_checked = false });
    }
    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        try { Output = args[1]; Directory.CreateDirectory(Output); Run(FrameSolutionSelection.FromDict(Json.DeserializeObject(File.ReadAllText(args[0])))); Emit(new { event_type = "complete", verification = "PASS", checks = Checks.Count }); return 0; }
        catch (Exception error) { Emit(new { event_type = "failure", error = error.ToString() }); return 1; }
    }
}
