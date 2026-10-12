using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(FacadeCoreProbe.Probe))]

namespace FacadeCoreProbe
{
    // A real Core-engine investigation of the original DWG's dynamic properties.
    // This deliberately does NOT claim to execute ATTILE, MakeDynRef, SampleElem or desktop UI.
    public sealed class Probe
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        private const string Input = "input.dwg", Config = "config.json", Output = "output.dwg";
        private const string ProbeReport = "probe-result.json", ReopenReport = "reopen-result.json", Expected = "expected.json";

        public sealed class Configuration
        {
            public int schema_version { get; set; }
            public string run_id { get; set; }
            public string fixture_id { get; set; }
            public string input_sha256 { get; set; }
            public string source_handle { get; set; }
            public string width_property { get; set; }
            public string height_property { get; set; }
            public string outline_layer { get; set; }
            public double tolerance { get; set; }
            public Case[] cases { get; set; }
        }

        public sealed class Case
        {
            public string id { get; set; }
            public string mode { get; set; }
            public string order { get; set; }
            public double width { get; set; }
            public double height { get; set; }
            // Requested lower-left outline corner in the XY plane; insertion base may differ.
            public double x { get; set; }
            public double y { get; set; }
        }

        [CommandMethod("ATPROBE", CommandFlags.Modal)]
        public void Run()
        {
            var report = BaseReport("probe");
            var results = new List<Dictionary<string, object>>();
            report["cases"] = results;
            try
            {
                RequireNew(ProbeReport); RequireNew(Output);
                var cfg = ReadConfig(); Bind(report, cfg);
                var db = ActiveDatabase(report);
                RequireHash(Input, cfg.input_sha256);
                report["input_sha256"] = HashFile(Input);
                ObjectId sourceId = SourceId(db, cfg.source_handle);
                string before;
                string[] originalMembers;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var source = RequireSource(tr, db, sourceId, cfg);
                    before = SourceFingerprint(tr, source);
                    originalMembers = Members(tr, db);
                    report["source_properties"] = Properties(source);
                    report["source_fingerprint_before"] = HashText(before);
                    report["source_position"] = Point(source.Position);
                    report["source_definition_handle"] = source.DynamicBlockTableRecord.Handle.ToString();
                    var outline = Outline(source, cfg.outline_layer);
                    CheckOutline(outline, outline.Min(p => p[0]), outline.Min(p => p[1]),
                        Number(source, cfg.width_property), Number(source, cfg.height_property), cfg.tolerance, source.Position.Z);
                    report["source_outline"] = outline;
                    tr.Abort();
                }
                report["native_execution"] = true;
                var created = new List<string>();
                bool allPass = true;
                foreach (var c in cfg.cases)
                {
                    var item = RunCase(db, sourceId, cfg, c, before, originalMembers, created);
                    results.Add(item);
                    if ((string)item["status"] != "PASS") allPass = false;
                    if (!(bool)item["source_preserved"] || !(bool)item["membership_preserved"])
                        throw new InvalidOperationException("Integrity failed; no further cases or output DWG accepted.");
                }
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    Require(SourceFingerprint(tr, (BlockReference)tr.GetObject(sourceId, OpenMode.ForRead)) == before,
                        "Original source changed before SaveAs.");
                    RequireMembers(tr, db, originalMembers, created);
                    tr.Abort();
                }
                RequireHash(Input, cfg.input_sha256);
                // Keep observed failed insertions in the diagnostic output too; report remains FAIL.
                db.SaveAs(Path.GetFullPath(Output), DwgVersion.Current);
                RequireHash(Input, cfg.input_sha256);
                report["output_sha256"] = HashFile(Output);
                report["created_handles"] = created;
                report["original_modelspace_handles"] = originalMembers;
                report["input_file_unchanged"] = true;
                report["source_preserved"] = true;
                report["source_unchanged"] = true;
                report["execution_status"] = "COMPLETED";
                report["status"] = allPass ? "PASS" : "FAIL";
            }
            catch (System.Exception ex) { Fail(report, ex); }
            Finish(ProbeReport, report);
        }

        [CommandMethod("ATREOPENVERIFY", CommandFlags.Modal)]
        public void VerifyReopened()
        {
            var report = BaseReport("reopen");
            var results = new List<Dictionary<string, object>>();
            report["cases"] = results;
            try
            {
                RequireNew(ReopenReport);
                var cfg = ReadConfig(); Bind(report, cfg);
                var expected = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Expected, Encoding.UTF8));
                Require(expected != null && Convert.ToInt32(Get(expected, "schema_version")) == 1, "Invalid expected report schema.");
                Require(Str(expected, "phase") == "probe" && Str(expected, "execution_status") == "COMPLETED", "Probe did not complete.");
                Require(Str(expected, "run_id") == cfg.run_id && Str(expected, "fixture_id") == cfg.fixture_id, "Run/fixture identity mismatch.");
                Require(Str(expected, "configuration_sha256") == HashFile(Config), "Configuration differs from original probe.");
                Require(Str(expected, "harness_sha256") == HashFile(Assembly.GetExecutingAssembly().Location), "Harness differs from original probe.");
                Require(Str(expected, "input_sha256") == cfg.input_sha256.ToLowerInvariant(), "Original input identity mismatch.");
                Require(Str(expected, "source_handle") == cfg.source_handle, "Source handle mismatch.");
                Require((bool)Get(expected, "native_execution") && (bool)Get(expected, "input_file_unchanged") &&
                    (bool)Get(expected, "source_preserved"), "Original native integrity proof missing.");
                string outputHash = Str(expected, "output_sha256"); RequireHash(Input, outputHash);
                report["input_sha256"] = outputHash;
                report["expected_report_sha256"] = HashFile(Expected);
                report["original_probe_status"] = Str(expected, "status");
                var db = ActiveDatabase(report);
                report["native_execution"] = true;
                var oldCases = Arr(Get(expected, "cases"));
                Require(oldCases.Length == cfg.cases.Length, "Case count mismatch.");
                string[] created = Arr(Get(expected, "created_handles")).Select(Convert.ToString).ToArray();
                string[] originals = Arr(Get(expected, "original_modelspace_handles")).Select(Convert.ToString).ToArray();
                Require(created.Length == cfg.cases.Length && created.Distinct(StringComparer.Ordinal).Count() == created.Length,
                    "One unique persisted insertion is required for each case.");
                bool allPass = Str(expected, "status") == "PASS";
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var source = RequireSource(tr, db, SourceId(db, cfg.source_handle), cfg);
                    Require(HashText(SourceFingerprint(tr, source)) == Str(expected, "source_fingerprint_before"),
                        "Source fingerprint changed after save/reopen.");
                    RequireMembers(tr, db, originals, created);
                    for (int i = 0; i < cfg.cases.Length; i++)
                    {
                        var c = cfg.cases[i];
                        var old = Dict(oldCases[i]);
                        Require(Str(old, "id") == c.id, "Case order mismatch.");
                        RequireSameCase(Dict(Get(old, "requested")), c);
                        Require(Str(old, "created_handle") == created[i], "Persisted case handle mismatch.");
                        var item = new Dictionary<string, object> { { "id", c.id }, { "requested", c }, { "status", "FAIL" },
                            { "created_handle", created[i] }, { "original_case_status", Str(old, "status") } };
                        results.Add(item);
                        try
                        {
                            var made = tr.GetObject(SourceId(db, created[i]), OpenMode.ForRead) as BlockReference;
                            Require(made != null && made.IsDynamicBlock, "Saved case is not a native dynamic insertion.");
                            Observe(made, cfg, c, item);
                            RequireSavedObservations(old, item, cfg.tolerance);
                            Require(Str(old, "status") == "PASS", "Original case failed; reopen cannot upgrade it to PASS.");
                            item["status"] = "PASS";
                        }
                        catch (System.Exception ex) { item["error"] = ex.ToString(); allPass = false; }
                    }
                    tr.Abort();
                }
                RequireHash(Input, outputHash);
                report["input_file_unchanged"] = true;
                report["source_preserved"] = true;
                report["source_unchanged"] = true;
                report["execution_status"] = "COMPLETED";
                report["status"] = allPass ? "PASS" : "FAIL";
            }
            catch (System.Exception ex) { Fail(report, ex); }
            Finish(ReopenReport, report);
        }

        private static Dictionary<string, object> RunCase(Database db, ObjectId sourceId, Configuration cfg,
            Case c, string sourceBefore, string[] originalMembers, List<string> committed)
        {
            var item = new Dictionary<string, object> { { "id", c.id }, { "requested", c }, { "status", "FAIL" },
                { "source_preserved", false }, { "membership_preserved", false } };
            var watch = Stopwatch.StartNew();
            string madeHandle = null;
            bool committedCase = false;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var source = RequireSource(tr, db, sourceId, cfg);
                    var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    var made = new BlockReference(Point3d.Origin, source.DynamicBlockTableRecord) { Layer = source.Layer, Color = source.Color };
                    ms.AppendEntity(made); tr.AddNewlyCreatedDBObject(made, true);
                    madeHandle = made.Handle.ToString(); item["created_handle"] = madeHandle;
                    item["fresh_properties"] = Properties(made);
                    if (c.mode == "sample") CopyWritableProperties(source, made);
                    item["prepared_properties"] = Properties(made);
                    // Establish base offset BEFORE requested writes. The measured result never supplies tolerance or requested size.
                    var baseline = Outline(made, cfg.outline_layer);
                    CheckOutline(baseline, baseline.Min(p => p[0]), baseline.Min(p => p[1]),
                        Number(made, cfg.width_property), Number(made, cfg.height_property), cfg.tolerance, 0);
                    double ox = baseline.Min(p => p[0]), oy = baseline.Min(p => p[1]);
                    made.Position = new Point3d(c.x - ox, c.y - oy, 0);
                    item["baseline_outline_at_origin"] = baseline;
                    item["placement_offset"] = new[] { ox, oy };
                    item["before_assignments"] = Properties(made);
                    var traces = new List<Dictionary<string, object>>(); item["assignments"] = traces;
                    IEnumerable<DynamicBlockReferenceProperty> assignments;
                    if (c.order == "production") assignments = made.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>();
                    else assignments = (c.order == "width_height" ? new[] { cfg.width_property, cfg.height_property } :
                        new[] { cfg.height_property, cfg.width_property }).Select(name => Property(made, name));
                    foreach (var property in assignments)
                    {
                        if (property.PropertyName != cfg.width_property && property.PropertyName != cfg.height_property) continue;
                        double requested = property.PropertyName == cfg.width_property ? c.width : c.height;
                        traces.Add(Assign(property, requested));
                    }
                    Require(traces.Count == 2, "Expected exactly two dimension assignment observations.");
                    // Both setters have run before any Explode/geometry read.
                    try { Observe(made, cfg, c, item); item["status"] = "PASS"; }
                    catch (System.Exception ex) { item["error"] = ex.ToString(); }
                    Require(SourceFingerprint(tr, source) == sourceBefore, "Source changed during case.");
                    RequireMembers(tr, db, originalMembers, committed.Concat(new[] { madeHandle }));
                    tr.Commit(); committedCase = true; committed.Add(madeHandle);
                }
                catch (System.Exception ex)
                {
                    item["status"] = "FAIL"; item["error"] = ex.ToString();
                    tr.Abort(); item["transaction"] = "ABORTED";
                }
            }
            using (var check = db.TransactionManager.StartTransaction())
            {
                item["source_preserved"] = SourceFingerprint(check, (BlockReference)check.GetObject(sourceId, OpenMode.ForRead)) == sourceBefore;
                item["membership_preserved"] = Members(check, db).SequenceEqual(originalMembers.Concat(committed).OrderBy(v => v, StringComparer.Ordinal));
                check.Abort();
            }
            item["persisted"] = committedCase;
            if (committedCase) item["transaction"] = "COMMITTED_DIAGNOSTIC_COPY";
            item["elapsed_ms"] = watch.ElapsedMilliseconds;
            return item;
        }

        private static Dictionary<string, object> Assign(DynamicBlockReferenceProperty property, double requested)
        {
            var events = new List<object>();
            var trace = new Dictionary<string, object> { { "name", property.PropertyName }, { "requested", Box(requested) },
                { "read_only", property.ReadOnly }, { "events", events } };
            if (property.ReadOnly) { trace["setter_result"] = "NOT_WRITTEN_READ_ONLY"; return trace; }
            bool ok = ACladPlugin.LayoutSafety.SetNumberChecked(
                () => { try { object value = property.Value; events.Add(new object[] { "read", Box(value) }); return value; }
                    catch (System.Exception ex) { events.Add(new object[] { "read_exception", ex.ToString() }); throw; } },
                value => { events.Add(new object[] { "write_candidate", Box(value) });
                    try { property.Value = value; events.Add(new object[] { "write_returned" }); }
                    catch (System.Exception ex) { events.Add(new object[] { "write_exception", ex.ToString() }); throw; } }, requested);
            trace["setter_result"] = ok;
            return trace;
        }

        private static void Observe(BlockReference made, Configuration cfg, Case c, Dictionary<string, object> item)
        {
            // Read properties and geometry independently, even if one mismatches the request.
            var errors = new List<string>();
            try
            {
                item["actual_width"] = Number(made, cfg.width_property);
                item["actual_height"] = Number(made, cfg.height_property);
                item["actual_properties"] = Properties(made);
                Near("width", (double)item["actual_width"], c.width, cfg.tolerance);
                Near("height", (double)item["actual_height"], c.height, cfg.tolerance);
            }
            catch (System.Exception ex) { errors.Add("properties: " + ex.Message); }
            try
            {
                var outline = Outline(made, cfg.outline_layer); item["native_outline_vertices"] = outline;
                CheckOutline(outline, c.x, c.y, c.width, c.height, cfg.tolerance, 0);
            }
            catch (System.Exception ex) { errors.Add("geometry: " + ex.Message); }
            item["position"] = Point(made.Position);
            item["rotation"] = made.Rotation;
            item["scale"] = new[] { made.ScaleFactors.X, made.ScaleFactors.Y, made.ScaleFactors.Z };
            item["observed_signature"] = HashText(Json.Serialize(new object[] {
                GetOrNull(item, "actual_width"), GetOrNull(item, "actual_height"), GetOrNull(item, "actual_properties"),
                GetOrNull(item, "native_outline_vertices"), Point(made.Position), made.Rotation,
                new[] { made.ScaleFactors.X, made.ScaleFactors.Y, made.ScaleFactors.Z } }));
            item["observed_errors"] = errors;
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        }

        private static void CopyWritableProperties(BlockReference source, BlockReference made)
        {
            // This is an explicit diagnostic preparation mode, not production SampleElem.ApplyLook.
            var values = new List<KeyValuePair<string, object>>();
            foreach (DynamicBlockReferenceProperty p in source.DynamicBlockReferencePropertyCollection)
                if (!p.ReadOnly && p.PropertyName != "Origin") values.Add(new KeyValuePair<string, object>(p.PropertyName, p.Value));
            foreach (var pair in values)
            {
                var property = Property(made, pair.Key);
                Require(!property.ReadOnly, "Copied property became read-only: " + pair.Key);
                property.Value = pair.Value;
            }
            foreach (var pair in values)
                Require(Json.Serialize(Scalar(Property(made, pair.Key).Value)) == Json.Serialize(Scalar(pair.Value)),
                    "Prepared sample property differs: " + pair.Key);
        }

        private static Database ActiveDatabase(Dictionary<string, object> report)
        {
            string process = Process.GetCurrentProcess().ProcessName;
            string version = Convert.ToString(AcApp.GetSystemVariable("ACADVER"), CultureInfo.InvariantCulture);
            report["host"] = new Dictionary<string, object> { { "process", process }, { "acadver", version },
                { "managed_api", typeof(Database).Assembly.FullName }, { "managed_api_sha256", HashFile(typeof(Database).Assembly.Location) },
                { "executable_version", FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule.FileName).FileVersion } };
            Require(string.Equals(process, "accoreconsole", StringComparison.OrdinalIgnoreCase) && version.StartsWith("24.3", StringComparison.Ordinal),
                "Requires genuine AutoCAD 2024 Core Console (24.3); other hosts do not satisfy this probe.");
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            Require(doc != null && string.Equals(Path.GetFullPath(doc.Database.Filename), Path.GetFullPath(Input), StringComparison.OrdinalIgnoreCase),
                "Active database is not input.dwg.");
            return doc.Database;
        }

        private static Configuration ReadConfig()
        {
            string text = File.ReadAllText(Config, Encoding.UTF8);
            var c = Json.Deserialize<Configuration>(text);
            Guid run;
            Require(c != null && c.schema_version == 1 && Guid.TryParse(c.run_id, out run) && !string.IsNullOrWhiteSpace(c.fixture_id), "Invalid schema/run/fixture.");
            Require(c.input_sha256 != null && c.input_sha256.Length == 64 && c.input_sha256.All(Uri.IsHexDigit), "Explicit input SHA256 required.");
            Require(!string.IsNullOrWhiteSpace(c.source_handle) && c.source_handle.All(Uri.IsHexDigit), "Explicit source handle required.");
            Require(!string.IsNullOrWhiteSpace(c.width_property) && !string.IsNullOrWhiteSpace(c.height_property) && c.width_property != c.height_property &&
                !string.IsNullOrWhiteSpace(c.outline_layer), "Explicit property names/outline layer required.");
            Require(Finite(c.tolerance) && c.tolerance > 0 && c.tolerance <= 0.0001, "Tolerance must be finite and within (0, 0.0001].");
            Require(c.cases != null && c.cases.Length > 0 && c.cases.Length <= 1000, "Expected 1..1000 explicit cases.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in c.cases)
                Require(t != null && !string.IsNullOrWhiteSpace(t.id) && ids.Add(t.id) && (t.mode == "sample" || t.mode == "definition") &&
                    (t.order == "production" || t.order == "width_height" || t.order == "height_width") &&
                    Finite(t.width) && Finite(t.height) && Finite(t.x) && Finite(t.y) && t.width > 0 && t.height > 0 &&
                    Finite(t.x + t.width) && Finite(t.y + t.height), "Invalid/duplicate case.");
            // Typed JSON binding supplies 0 for omitted x/y; do not silently turn missing input into an expected origin.
            var raw = Json.Deserialize<Dictionary<string, object>>(text);
            var rawCases = Arr(Get(raw, "cases"));
            Require(rawCases.Length == c.cases.Length, "Case binding count mismatch.");
            for (int i = 0; i < c.cases.Length; i++) RequireSameCase(Dict(rawCases[i]), c.cases[i]);
            return c;
        }

        private static Dictionary<string, object> BaseReport(string phase)
        {
            return new Dictionary<string, object> { { "schema_version", 1 }, { "phase", phase }, { "status", "NOT_RUN" },
                { "execution_status", "NOT_COMPLETED" }, { "native_execution", false }, { "full_native_gate", "NOT_RUN" },
                { "scope", "Core engine dynamic-property setter and new-insertion rectangular outline" },
                { "coordinate_units", "DWG drawing units; no conversion to millimetres is performed" },
                { "limitations", new[] { "Does not execute ATTILE/MakeDynRef/SampleElem or production plug-in loading",
                    "No desktop dialogs, LISP author, grips, user Undo or native node acceptance",
                    "Fresh insertion per case, not sequential resizing of one existing insertion",
                    "Source preservation checks recursive source fingerprint and ModelSpace membership, not every DWG object",
                    "Other curves in source fingerprint use 17 samples; hatch/proxy internals are not fully fingerprinted",
                    "Baseline Explode before size writes can trigger evaluation; this is not an uninstrumented production trace" } },
                { "started_utc", DateTime.UtcNow.ToString("o") }, { "harness_sha256", HashFile(Assembly.GetExecutingAssembly().Location) } };
        }
        private static void Bind(Dictionary<string, object> r, Configuration cfg)
        { r["run_id"] = cfg.run_id; r["fixture_id"] = cfg.fixture_id; r["source_handle"] = cfg.source_handle; r["configuration_sha256"] = HashFile(Config); }
        private static void Fail(Dictionary<string, object> r, System.Exception ex)
        { r["status"] = (bool)r["native_execution"] ? "FAIL" : "NOT_RUN"; r["error"] = ex.ToString(); r["execution_status"] = "FAILED"; }
        private static void Finish(string path, Dictionary<string, object> r)
        {
            r["finished_utc"] = DateTime.UtcNow.ToString("o");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(Json.Serialize(r));
                AcApp.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nCORE PROBE " + r["phase"] + ": " + r["status"] + "; desktop acceptance NOT_RUN.\n");
            }
            catch (System.Exception ex)
            { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCORE PROBE REPORT FAILURE: " + ex + "\n"); }
        }
        private static void RequireNew(string path) { Require(!File.Exists(path), "Refusing to overwrite existing file: " + path); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static ObjectId SourceId(Database db, string handle)
        { return db.GetObjectId(false, new Handle(long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0); }
        private static BlockReference RequireSource(Transaction tr, Database db, ObjectId id, Configuration cfg)
        {
            var source = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
            Require(source != null && source.IsDynamicBlock && source.OwnerId == SymbolUtilityServices.GetBlockModelSpaceId(db), "Source must be a native dynamic insertion directly in ModelSpace.");
            Near("source rotation", source.Rotation, 0, 1e-12);
            Near("source scale X", source.ScaleFactors.X, 1, 1e-12); Near("source scale Y", source.ScaleFactors.Y, 1, 1e-12);
            Near("source scale Z", source.ScaleFactors.Z, 1, 1e-12);
            Near("source normal X", source.Normal.X, 0, 1e-12); Near("source normal Y", source.Normal.Y, 0, 1e-12); Near("source normal Z", source.Normal.Z, 1, 1e-12);
            Property(source, cfg.width_property); Property(source, cfg.height_property);
            return source;
        }
        private static DynamicBlockReferenceProperty Property(BlockReference br, string name)
        {
            var found = br.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>().Where(p => p.PropertyName == name).ToArray();
            Require(found.Length == 1, "Expected exactly one dynamic property: " + name); return found[0];
        }
        private static double Number(BlockReference br, string name)
        { double value = Convert.ToDouble(Property(br, name).Value, CultureInfo.InvariantCulture); Require(Finite(value), "Nonfinite property: " + name); return value; }
        private static object[] Properties(BlockReference br)
        {
            if (!br.IsDynamicBlock) return new object[0];
            return br.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>().Select(p => (object)new Dictionary<string, object> {
                { "name", p.PropertyName }, { "value", Scalar(p.Value) }, { "type", p.Value?.GetType().FullName },
                { "api_type", p.PropertyTypeCode }, { "read_only", p.ReadOnly }, { "visible", p.VisibleInCurrentVisibilityState },
                { "units", p.UnitsType.ToString() }, { "allowed", p.GetAllowedValues().Select(Scalar).ToArray() } }).ToArray();
        }
        private static object Scalar(object value)
        {
            if (value == null || value is string || value is bool || value is double || value is float || value is decimal ||
                value is short || value is int || value is long || value is byte) return value;
            if (value is Point3d) return Point((Point3d)value);
            throw new InvalidOperationException("Unsupported property type: " + value.GetType().FullName);
        }
        private static object Box(object value)
        {
            return new Dictionary<string, object> { { "type", value?.GetType().FullName }, { "value", Scalar(value) },
                { "exact", value is double ? ((double)value).ToString("R", CultureInfo.InvariantCulture) :
                    value is float ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) } };
        }
        private static double[][] Outline(BlockReference br, string layer)
        {
            using (var objects = new DBObjectCollection())
            {
                try
                {
                    br.Explode(objects);
                    var outlines = objects.Cast<DBObject>().OfType<Polyline>().Where(p => p.Visible && p.Layer == layer && p.Closed).ToArray();
                    Require(outlines.Length == 1 && outlines[0].NumberOfVertices == 4, "Expected exactly one visible, closed, four-vertex polyline on configured outline layer.");
                    var p = outlines[0]; var points = new double[4][];
                    for (int i = 0; i < 4; i++)
                    { Require(p.GetBulgeAt(i) == 0 && p.GetStartWidthAt(i) == 0 && p.GetEndWidthAt(i) == 0, "Outline must have straight zero-width edges."); points[i] = Point(p.GetPoint3dAt(i)); }
                    return points;
                }
                finally { foreach (DBObject item in objects) item.Dispose(); }
            }
        }
        private static void CheckOutline(double[][] points, double x, double y, double width, double height, double tolerance, double z)
        {
            Require(points.Length == 4 && Finite(x) && Finite(y) && Finite(z), "Invalid outline input.");
            var expected = new List<double[]> { new[] { x, y, z }, new[] { x + width, y, z }, new[] { x + width, y + height, z }, new[] { x, y + height, z } };
            for (int i = 0; i < points.Length; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Length];
                Require(a.Length == 3 && a.All(Finite) && b.Length == 3 && b.All(Finite), "Invalid outline coordinates.");
                Require((Math.Abs(a[0] - b[0]) <= tolerance) != (Math.Abs(a[1] - b[1]) <= tolerance), "Diagonal or zero-length outline edge.");
                int match = expected.FindIndex(p => p.Zip(a, (u, v) => Math.Abs(u - v) <= tolerance).All(v => v));
                Require(match >= 0, "Exploded contour differs from exact requested rectangle."); expected.RemoveAt(match);
            }
            Require(expected.Count == 0, "Missing requested rectangle corners.");
        }
        private static string[] Members(Transaction tr, Database db)
        { return ((BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead)).Cast<ObjectId>()
            .Where(id => !id.IsErased).Select(id => id.Handle.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToArray(); }
        private static void RequireMembers(Transaction tr, Database db, IEnumerable<string> original, IEnumerable<string> created)
        { Require(Members(tr, db).SequenceEqual(original.Concat(created).OrderBy(v => v, StringComparer.Ordinal)), "ModelSpace membership changed outside known diagnostic insertions."); }
        private static string SourceFingerprint(Transaction tr, BlockReference br)
        { return Json.Serialize(EntityData(tr, br, new HashSet<ObjectId>(), 0)); }
        private static object EntityData(Transaction tr, Entity ent, HashSet<ObjectId> ancestry, int depth)
        {
            Require(depth <= 16, "Source nesting exceeds fingerprint limit.");
            var d = new Dictionary<string, object> { { "handle", ent.Handle.ToString() }, { "type", ent.GetType().FullName },
                { "layer", ent.Layer }, { "visible", ent.Visible }, { "color", ent.Color.ToString() }, { "linetype", ent.Linetype }, { "lineweight", ent.LineWeight.ToString() } };
            try { var e = ent.GeometricExtents; d["extents"] = new[] { e.MinPoint.X, e.MinPoint.Y, e.MinPoint.Z, e.MaxPoint.X, e.MaxPoint.Y, e.MaxPoint.Z }; }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) { d["extents_error"] = ex.ErrorStatus.ToString(); }
            var poly = ent as Polyline;
            if (poly != null)
            {
                var vertices = new List<object>();
                for (int i = 0; i < poly.NumberOfVertices; i++) vertices.Add(new object[] { Point(poly.GetPoint3dAt(i)), poly.GetBulgeAt(i), poly.GetStartWidthAt(i), poly.GetEndWidthAt(i) });
                d["vertices"] = vertices; d["closed"] = poly.Closed;
            }
            else if (ent is Curve)
            {
                var curve = (Curve)ent; var points = new List<double[]>();
                for (int i = 0; i <= 16; i++) points.Add(Point(curve.GetPointAtParameter(curve.StartParam + (curve.EndParam - curve.StartParam) * i / 16.0)));
                d["curve_samples"] = points;
            }
            if (ent is DBText) { var t = (DBText)ent; d["text"] = t.TextString; d["text_position"] = Point(t.Position); d["text_height"] = t.Height; }
            if (ent is AttributeReference) d["attribute_tag"] = ((AttributeReference)ent).Tag;
            if (ent is AttributeDefinition) d["attribute_tag"] = ((AttributeDefinition)ent).Tag;
            if (ent is MText) { var t = (MText)ent; d["text"] = t.Contents; d["text_position"] = Point(t.Location); }
            if (ent is Dimension) { var dim = (Dimension)ent; d["dimension_text"] = dim.DimensionText; d["measurement"] = dim.Measurement; }
            var block = ent as BlockReference;
            if (block != null)
            {
                d["position"] = Point(block.Position); d["rotation"] = block.Rotation;
                d["scale"] = new[] { block.ScaleFactors.X, block.ScaleFactors.Y, block.ScaleFactors.Z }; d["normal"] = new[] { block.Normal.X, block.Normal.Y, block.Normal.Z };
                d["properties"] = Properties(block); d["definition"] = block.BlockTableRecord.Handle.ToString();
                var attrs = new List<object>();
                foreach (ObjectId id in block.AttributeCollection) attrs.Add(EntityData(tr, (Entity)tr.GetObject(id, OpenMode.ForRead), ancestry, depth + 1));
                d["attributes"] = attrs;
                Require(ancestry.Add(block.BlockTableRecord), "Cyclic source definition.");
                var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead); var entities = new List<object>();
                foreach (ObjectId id in definition)
                { Require(entities.Count < 5000, "Source definition exceeds fingerprint limit."); var child = tr.GetObject(id, OpenMode.ForRead) as Entity; if (child != null) entities.Add(EntityData(tr, child, ancestry, depth + 1)); }
                d["entities"] = entities; ancestry.Remove(block.BlockTableRecord);
            }
            return d;
        }
        private static object Get(Dictionary<string, object> d, string key) { object value; Require(d.TryGetValue(key, out value), "Missing report field: " + key); return value; }
        private static object GetOrNull(Dictionary<string, object> d, string key) { object value; return d.TryGetValue(key, out value) ? value : null; }
        private static string Str(Dictionary<string, object> d, string key) { return Convert.ToString(Get(d, key), CultureInfo.InvariantCulture); }
        private static Dictionary<string, object> Dict(object value) { var d = value as Dictionary<string, object>; Require(d != null, "Expected report object."); return d; }
        private static object[] Arr(object value)
        { var a = value as IList; Require(a != null, "Expected report array."); return a.Cast<object>().ToArray(); }
        private static void RequireSameCase(Dictionary<string, object> actual, Case expected)
        {
            Require(actual.Count == 7 && Str(actual, "id") == expected.id && Str(actual, "mode") == expected.mode &&
                Str(actual, "order") == expected.order, "Case parameters differ.");
            foreach (var p in new[] { new KeyValuePair<string, double>("width", expected.width),
                new KeyValuePair<string, double>("height", expected.height), new KeyValuePair<string, double>("x", expected.x),
                new KeyValuePair<string, double>("y", expected.y) })
            {
                object raw = Get(actual, p.Key); Require(Numeric(raw), "Case parameter is not numeric: " + p.Key);
                double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                Require(Finite(value) && value == p.Value, "Case parameter differs: " + p.Key);
            }
        }
        private static void RequireSavedObservations(Dictionary<string, object> before, Dictionary<string, object> after, double tolerance)
        {
            // JSON hashes remain provenance/diagnostics. Native reevaluation may change harmless low-order bits.
            foreach (string field in new[] { "actual_width", "actual_height", "position", "rotation", "scale" })
                Require(Equivalent(Get(before, field), Get(after, field), tolerance), "Saved observation differs: " + field);
            var oldProperties = Arr(Get(before, "actual_properties")).Select(Dict).ToArray();
            var newProperties = Arr(Get(after, "actual_properties")).Select(Dict).ToArray();
            Require(oldProperties.Length == newProperties.Length, "Saved property count differs.");
            Require(oldProperties.Select(p => Str(p, "name")).Distinct(StringComparer.Ordinal).Count() == oldProperties.Length &&
                newProperties.Select(p => Str(p, "name")).Distinct(StringComparer.Ordinal).Count() == newProperties.Length, "Duplicate observed property name.");
            foreach (var p in oldProperties)
            {
                var candidates = newProperties.Where(q => Str(q, "name") == Str(p, "name")).ToArray();
                Require(candidates.Length == 1 && Equivalent(p, candidates[0], tolerance), "Saved dynamic property differs: " + Str(p, "name"));
            }
            // Both contours already passed the same explicit rectangle oracle. Vertex order may legitimately change.
            var oldPoints = Arr(Get(before, "native_outline_vertices"));
            var newPoints = Arr(Get(after, "native_outline_vertices")).ToList();
            Require(oldPoints.Length == newPoints.Count, "Saved contour count differs.");
            foreach (var point in oldPoints)
            { int match = newPoints.FindIndex(p => Equivalent(point, p, tolerance)); Require(match >= 0, "Saved contour differs."); newPoints.RemoveAt(match); }
        }
        private static bool Equivalent(object a, object b, double tolerance)
        {
            if (a == null || b == null) return a == null && b == null;
            var da = a as IDictionary<string, object>; var db = b as IDictionary<string, object>;
            if (da != null || db != null)
                return da != null && db != null && da.Count == db.Count && da.All(p => db.ContainsKey(p.Key) && Equivalent(p.Value, db[p.Key], tolerance));
            var aa = a as IList; var ab = b as IList;
            if (aa != null || ab != null)
                return aa != null && ab != null && aa.Count == ab.Count && Enumerable.Range(0, aa.Count).All(i => Equivalent(aa[i], ab[i], tolerance));
            if (Numeric(a) && Numeric(b))
            { double x = Convert.ToDouble(a, CultureInfo.InvariantCulture), y = Convert.ToDouble(b, CultureInfo.InvariantCulture); return Finite(x) && Finite(y) && Math.Abs(x - y) <= tolerance; }
            return a.GetType() == b.GetType() && a.Equals(b);
        }
        private static bool Numeric(object v)
        { return v is byte || v is short || v is int || v is long || v is float || v is double || v is decimal; }
        private static double[] Point(Point3d p) { return new[] { p.X, p.Y, p.Z }; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static void Near(string label, double actual, double expected, double tolerance)
        { Require(Finite(actual) && Finite(expected) && Math.Abs(actual - expected) <= tolerance, label + " mismatch: " + actual.ToString("R", CultureInfo.InvariantCulture) + " vs " + expected.ToString("R", CultureInfo.InvariantCulture)); }
        private static string HashText(string s) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(s))); }
        private static string HashFile(string path) { using (var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var h = SHA256.Create()) return Hex(h.ComputeHash(f)); }
        private static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
        private static void RequireHash(string path, string sha) { Require(string.Equals(HashFile(path), sha, StringComparison.OrdinalIgnoreCase), "File SHA256 mismatch: " + path); }
    }
}
