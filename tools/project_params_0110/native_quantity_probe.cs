// Actual quantity-store freshness integration. CAD objects/geometry are explicit
// managed doubles; none of these checks execute an AutoCAD document.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;
using Store = FacadeSafety.FacadeProjectParameterStore;

internal static class ProjectQuantityProbe
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly List<object> Cases = new List<object>(), Performance = new List<object>();
    private static int failed, serial;
    private static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Refuses(Action action) { bool refused = false; try { action(); } catch (InvalidOperationException) { refused = true; } Need(refused, "expected explicit refusal"); }
    private static void Check(string name, Action action) {
        try { action(); Cases.Add(new { name, status = "PASS" }); }
        catch (Exception error) { failed++; Cases.Add(new { name, status = "FAIL", reason = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static Dictionary<string, object> Project(int revision = 1) { return new Dictionary<string, object> {
        { "schema", "aframe_project_parameters/1" }, { "project_id", "11111111111111111111111111111111" },
        { "revision", revision }, { "content_digest", new string('a', 64) } }; }
    private static void WriteProject(Transaction tr, Database db, int revision = 1)
    { Store.WriteProject(tr, db, Store.ReadProject(tr, db), Project(revision)); }
    private static Dictionary<string, object> Override(double value) { return new Dictionary<string, object> {
        { "project_id", "11111111111111111111111111111111" }, { "revision", 1 },
        { "overrides", new Dictionary<string, object> { { "geometry.cladding_front_offset_mm", value } } } }; }
    private static Xrecord ProjectRecord(Transaction tr, Database db)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        var root = (DBDictionary)tr.GetObject(nod.GetAt(Store.ProjectRootKey), OpenMode.ForRead);
        return (Xrecord)tr.GetObject(root.GetAt(Store.ProjectRecordKey), OpenMode.ForRead);
    }
    private static void Write(Transaction tr, Entity entity, string key, string text)
    {
        if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
        var ext = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite);
        var record = new Xrecord { Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, text)) };
        ext.SetAt(key, record); tr.AddNewlyCreatedDBObject(record, true);
    }
    private sealed class Fixture
    {
        internal readonly QuantityStoreCheck.Fixture Zone;
        internal readonly QuantityReport Report;
        internal readonly FacadeQuantityStore.FrameSources Sources;
        internal Fixture(bool bound, bool zoneOverride = false, bool persist = true, Database shared = null)
        {
            var db = shared ?? new Database(); var tr = new Transaction(db);
            if (bound && !Store.ReadProject(tr, db).Present) WriteProject(tr, db);
            string id = "Z-" + (++serial).ToString("D4");
            Zone = new QuantityStoreCheck.Fixture("single", db, id);
            Need(Zone.Captured.Ok, "actual ZoneGeometryGuard fixture failed: " + Zone.Captured.Reason);
            if (zoneOverride) Store.WriteZone(tr, Zone.Hatch, Zone.Captured.Fingerprint, Store.ReadZone(tr, Zone.Hatch), Override(340));
            var dependency = bound ? Store.Dependency(Store.ReadProject(tr, db)) : null;
            Sources = FacadeQuantityStore.CaptureFrameSources(tr, db, new[] { Zone.Hatch.ObjectId }, dependency);
            var rail = db.Add(new Line { StartPoint = new Point3d(300, 0, 0), EndPoint = new Point3d(300, 1000, 0) });
            Report = new QuantityReport { report_id = "report-" + id, run_id = "run-" + id, kind = "frame",
                scope = "whole_layout_run", algorithm = "synthetic-project-freshness", completeness = "partial",
                engineering_coverage = "geometry_only", zone_ids = new List<string> { id },
                elements = new List<QuantityElement> { new QuantityElement { element_id = "rail-" + id,
                    zone_id = id, zone_ids = new List<string> { id }, role = "rail", type = "synthetic rail",
                    product_id = null, system = "synthetic-system", length_mm = 1000, origin = "test",
                    cad_entities = new List<QuantityCadEntity> { FacadeQuantityStore.CaptureEntity(tr, rail, "rail") } } } };
            Write(tr, Zone.Hatch, "ATFRAME", Json.Serialize(new Dictionary<string, object> { { "owner", Zone.Hatch.Handle.ToString() }, { "mode", "manual" } }));
            Report.source_revisions["owner_zone:" + Zone.Hatch.Handle] = id;
            if (persist) Save();
        }
        internal void Save() { FacadeQuantityStore.StoreFrame(Zone.Tr, Zone.Db, new[] { Zone.Hatch }, Report, Sources); }
        internal QuantitySelection Read() { return FacadeQuantityStore.ReadFrame(Zone.Tr, Zone.Db, new[] { Zone.Hatch.ObjectId }); }
    }
    public static int Main(string[] args)
    {
        typeof(QuantityStoreCheck).GetField("Fixtures", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(args[0])));
        Check("bound stored quantity is initially fresh", delegate {
            var fixture = new Fixture(true, true); var read = fixture.Read();
            Need(read.Ok && read.Reports.Count == 1 && read.Reports[0].elements.Count == 1, read.Reason ?? "physical report changed");
        });
        Check("project revision invalidates an already stored quantity", delegate {
            var f = new Fixture(true); WriteProject(f.Zone.Tr, f.Zone.Db, 2); var read = f.Read();
            Need(!read.Ok && read.Reason.Contains("проекта"), "project change was invisible to quantity freshness");
        });
        Check("project raw whitespace mutation invalidates dependency independently of semantic digest", delegate {
            var f = new Fixture(true); var before = Store.ReadProject(f.Zone.Tr, f.Zone.Db);
            ProjectRecord(f.Zone.Tr, f.Zone.Db).Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, " \n" + before.Json));
            Need(!f.Read().Ok, "raw project mutation accepted");
        });
        Check("deleted project cannot turn a bound stored quantity into local legacy", delegate {
            var f = new Fixture(true); var nod = (DBDictionary)f.Zone.Tr.GetObject(f.Zone.Db.NamedObjectsDictionaryId, OpenMode.ForWrite);
            nod.Remove(Store.ProjectRootKey); Need(!f.Read().Ok, "missing project silently removed dependency");
        });
        Check("zone override edits and deletion invalidate stored quantity", delegate {
            var f = new Fixture(true, true);
            Store.WriteZone(f.Zone.Tr, f.Zone.Hatch, f.Zone.Captured.Fingerprint, Store.ReadZone(f.Zone.Tr, f.Zone.Hatch), Override(350));
            Need(!f.Read().Ok, "override change was invisible to quantity freshness");
            var g = new Fixture(true, true);
            Store.ClearZone(g.Zone.Tr, g.Zone.Hatch, g.Zone.Captured.Fingerprint, Store.ReadZone(g.Zone.Tr, g.Zone.Hatch));
            Need(!g.Read().Ok, "override deletion became inheritance in existing report");
        });
        Check("new explicit zone binding invalidates earlier source snapshot", delegate {
            var f = new Fixture(true);
            Store.WriteZone(f.Zone.Tr, f.Zone.Hatch, f.Zone.Captured.Fingerprint, Store.ReadZone(f.Zone.Tr, f.Zone.Hatch), Override(350));
            Need(!f.Read().Ok, "new zone binding was invisible to existing snapshot");
        });
        Check("legacy unbound quantity remains independent from a later project", delegate {
            var f = new Fixture(false); Need(f.Read().Ok, "legacy fixture not initially readable");
            WriteProject(f.Zone.Tr, f.Zone.Db); WriteProject(f.Zone.Tr, f.Zone.Db, 2);
            Need(f.Read().Ok, "unbound legacy quantity was silently enrolled into a project");
        });
        Check("source hashes without binding match actual 375f082 algorithm", delegate {
            var f = new Fixture(false, false, false);
            foreach (var source in f.Sources.sources) {
                long number = Convert.ToInt64(source.handle, 16); ObjectId id;
                Need(f.Zone.Db.TryGetObjectId(new Handle(number), out id), "source handle missing");
                var entity = (Entity)f.Zone.Tr.GetObject(id, OpenMode.ForRead);
                Need(source.metadata == FacadeQuantityStore.LegacyFrameSourceMetadata(f.Zone.Tr, entity, null), "legacy source hash changed without override");
            }
        });
        Check("store checks changed project before any quantity write", delegate {
            var f = new Fixture(true, false, false); WriteProject(f.Zone.Tr, f.Zone.Db, 2); CadCounters.Reset();
            Refuses(() => f.Save());
            Need(CadCounters.DictionarySet == 0 && CadCounters.XrecordWrites == 0, "stale result partially wrote quantity passport");
        });
        Check("quantity prewrite reuses the current phase canonical binding snapshot", delegate {
            var f = new Fixture(true, true, false); var context = new Store.ReadContext();
            var ext = (DBDictionary)f.Zone.Tr.GetObject(f.Zone.Hatch.ExtensionDictionary, OpenMode.ForRead);
            CadCounters.WatchedXrecord = ext.GetAt(Store.ZoneKey); CadCounters.Reset();
            Need(Store.ReadZone(f.Zone.Tr, f.Zone.Hatch, f.Zone.Captured.Fingerprint, context).Present, "canonical phase snapshot missing");
            FacadeQuantityStore.StoreFrame(f.Zone.Tr, f.Zone.Db, new[] { f.Zone.Hatch }, f.Report, f.Sources, context);
            Need(context.ZoneReads == 1 && CadCounters.WatchedXrecordReads == 1,
                "quantity prewrite reread a binding verified earlier in the same phase");
            Need(CadCounters.ModelSpaceVisits == 0, "store prewrite introduced a ModelSpace scan");
            Need(f.Read().Ok, "saved snapshot did not pass a fresh independent read");
        });
        foreach (int count in new[] { 100, 1000 }) { int n = count; Check("many report reads share one project record " + n, delegate {
            var db = new Database(); var items = new List<Fixture>();
            for (int i = 0; i < n; i++) items.Add(new Fixture(true, false, true, db));
            var tr = new Transaction(db); CadCounters.WatchedXrecord = ProjectRecord(tr, db).ObjectId;
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int drawingEntities = modelSpace.Children.Count;
            CadCounters.Reset(); var watch = Stopwatch.StartNew();
            var result = FacadeQuantityStore.ReadFrame(tr, db, items.Select(item => item.Zone.Hatch.ObjectId)); watch.Stop();
            Need(result.Ok && result.Reports.Count == n, result.Reason ?? "report count differs");
            Need(CadCounters.WatchedXrecordReads == 1, "project record read per report");
            // ATFTABLE already has one shared orphan/extra-entity scan. Project
            // freshness must not introduce an additional scan or one per report.
            Need(CadCounters.ModelSpaceVisits == drawingEntities, "parameter freshness changed the existing one shared ModelSpace scan");
            Performance.Add(new { reports = n, project_record_reads = CadCounters.WatchedXrecordReads,
                expected_existing_modelspace_visits = drawingEntities, additional_modelspace_scans = 0,
                elapsed_ms = watch.Elapsed.TotalMilliseconds, counters = CadCounters.Snapshot() });
        }); }
        File.WriteAllText(args[1], Json.Serialize(new { status = failed == 0 ? "PASS" : "FAIL", checks = Cases.Count,
            cases = Cases, performance = Performance, live_autocad_checked = false }));
        Console.WriteLine("Project quantity freshness checks: " + Cases.Count + (failed == 0 ? " PASS" : " FAIL"));
        return failed == 0 ? 0 : 1;
    }
}
