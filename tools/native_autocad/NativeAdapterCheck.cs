using System;
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
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(FacadeNativeCheck.NativeAdapterCheck))]

namespace FacadeNativeCheck
{
    // A native, deliberately narrow integration check. No production algorithm or CAD API double.
    // It invokes the loaded ACladPlugin's private adapter on the real source block in a DWG copy.
    public sealed class NativeAdapterCheck
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public sealed class Configuration
        {
            public int schema_version { get; set; }
            public string run_id { get; set; }
            public string fixture_id { get; set; }
            public string source_dwg { get; set; }
            public string source_sha256 { get; set; }
            public string working_dwg { get; set; }
            public string working_sha256 { get; set; }
            public string production_dll { get; set; }
            public string production_sha256 { get; set; }
            public string harness_sha256 { get; set; }
            public string source_handle { get; set; }
            public string width_property { get; set; }
            public string height_property { get; set; }
            public string outline_layer { get; set; }
            public double tolerance { get; set; }
            public int repeats { get; set; }
            public Case[] cases { get; set; }
        }

        public sealed class Case
        {
            public string id { get; set; }
            public string mode { get; set; }
            public double width { get; set; }
            public double height { get; set; }
            public double x { get; set; }
            public double y { get; set; }
        }

        [CommandMethod("ATNATIVECLADTEST", CommandFlags.Modal)]
        public void Run()
        {
            var result = new Dictionary<string, object> {
                { "schema_version", 1 }, { "test_kind", "autocad-2024-production-makedynref" },
                { "status", "BLOCKED" }, { "exit_code", 2 }, { "native_execution", false },
                { "full_native_gate", "NOT_RUN" }, { "adapter_scope", "MakeDynRef/SampleElem/CellExtents" },
                { "started_utc", DateTime.UtcNow.ToString("o") },
                { "limitations", new[] { "ATTILE command UI and full-command rollback not executed",
                    "LISP author, native node 3-of-5, COPY/Undo and save/reopen not executed",
                    "Each case creates a fresh insertion; this is not resizing an existing insertion",
                    "Abort check covers source fingerprint and ModelSpace membership, not every DWG object",
                    "Source fingerprint: full polyline vertices, sampled other curves (17 points), attributes and properties; not full hatch/proxy contents" } }
            };
            string resultPath = Environment.GetEnvironmentVariable("AT_NATIVE_RESULT");
            bool mayWriteResult = false;
            var caseResults = new List<Dictionary<string, object>>();
            result["cases"] = caseResults;
            try
            {
                if (string.IsNullOrWhiteSpace(resultPath) || !Path.IsPathRooted(resultPath))
                    throw new InvalidOperationException("AT_NATIVE_RESULT must be an absolute JSON path.");
                if (!string.Equals(Path.GetExtension(resultPath), ".json", StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(resultPath))
                    throw new InvalidOperationException("Native result must be a new JSON file; existing files are never overwritten.");
                mayWriteResult = true;
                var configPath = Environment.GetEnvironmentVariable("AT_NATIVE_CONFIG");
                if (string.IsNullOrWhiteSpace(configPath) || !Path.IsPathRooted(configPath))
                    throw new InvalidOperationException("AT_NATIVE_CONFIG must be an absolute JSON path.");
                var cfg = Json.Deserialize<Configuration>(File.ReadAllText(configPath, Encoding.UTF8));
                Validate(cfg);
                result["run_id"] = cfg.run_id;
                result["fixture_id"] = cfg.fixture_id;
                result["configuration_sha256"] = HashFile(configPath);
                string acadver = Convert.ToString(AcApp.GetSystemVariable("ACADVER"), CultureInfo.InvariantCulture);
                string process = Process.GetCurrentProcess().ProcessName;
                result["host"] = new Dictionary<string, object> {
                    { "process", process }, { "process_id", Process.GetCurrentProcess().Id },
                    { "acadver", acadver }, { "managed_api", typeof(Database).Assembly.FullName },
                    { "executable_file_version", FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule.FileName).FileVersion },
                    { "managed_api_path", typeof(Database).Assembly.Location },
                    { "managed_api_sha256", HashFile(typeof(Database).Assembly.Location) }
                };
                if (!string.Equals(process, "acad", StringComparison.OrdinalIgnoreCase) ||
                    !acadver.StartsWith("24.3", StringComparison.Ordinal))
                    throw new InvalidOperationException("Requires full AutoCAD 2024 (acad.exe, ACADVER 24.3); core console is outside this gate.");
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null || !SamePath(doc.Database.Filename, cfg.working_dwg))
                    throw new InvalidOperationException("Active document is not the configured private working copy.");
                if (Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0)
                    throw new InvalidOperationException("Working copy was modified before the test.");
                RequireHash(cfg.source_dwg, cfg.source_sha256);
                RequireHash(cfg.working_dwg, cfg.working_sha256);
                RequireHash(cfg.production_dll, cfg.production_sha256);
                RequireHash(typeof(NativeAdapterCheck).Assembly.Location, cfg.harness_sha256);
                result["input_sha256"] = HashFile(cfg.working_dwg);
                result["source_sha256"] = HashFile(cfg.source_dwg);
                result["production_dll_sha256"] = HashFile(cfg.production_dll);
                result["harness_sha256"] = HashFile(typeof(NativeAdapterCheck).Assembly.Location);
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "ACladPlugin").ToArray();
                if (loaded.Length != 1 || !SamePath(loaded[0].Location, cfg.production_dll))
                    throw new InvalidOperationException("Exactly one ACladPlugin must be loaded from the configured DLL path.");
                RequireHash(loaded[0].Location, cfg.production_sha256);
                var adapter = new Adapter(loaded[0]);
                var db = doc.Database;
                var sourceId = db.GetObjectId(false, new Handle(long.Parse(cfg.source_handle,
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0);
                string before, members;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var source = tr.GetObject(sourceId, OpenMode.ForRead) as BlockReference;
                    if (source == null || !source.IsDynamicBlock)
                        throw new InvalidOperationException("Configured handle is not a native dynamic block reference.");
                    var modelId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                    if (source.OwnerId != modelId)
                        throw new InvalidOperationException("Source block must be directly in ModelSpace.");
                    RequireProperty(source, cfg.width_property);
                    RequireProperty(source, cfg.height_property);
                    before = SourceFingerprint(tr, source);
                    members = Members(tr, db);
                    result["source_properties"] = Properties(source);
                    result["source_fingerprint_before"] = HashText(before);
                    result["source_cell_extents"] = Bounds(adapter.Cell(tr, source));
                    tr.Abort();
                }
                result["native_execution"] = true;
                bool pass = true;
                bool rollbackFailed = false;
                var previous = new Dictionary<string, string>();
                foreach (var c in cfg.cases)
                {
                    for (int repeat = 1; repeat <= cfg.repeats; repeat++)
                    {
                        var item = RunCase(db, sourceId, cfg, c, repeat, adapter, before, members);
                        caseResults.Add(item);
                        string signature = item.ContainsKey("result_signature") ? (string)item["result_signature"] : null;
                        string first;
                        if (repeat > 1 && signature != null && previous.TryGetValue(c.id, out first) && first != signature)
                        { item["status"] = "FAIL"; item["repeat_error"] = "Native result differs between fresh repeated insertions."; }
                        if (repeat == 1 && signature != null) previous[c.id] = signature;
                        if ((string)item["status"] != "PASS") pass = false;
                        if ((string)item["rollback"] == "FAIL") { rollbackFailed = true; break; }
                    }
                    if (rollbackFailed) break;
                }
                result["stopped_after_rollback_failure"] = rollbackFailed;
                RequireHash(cfg.source_dwg, cfg.source_sha256);
                RequireHash(cfg.working_dwg, cfg.working_sha256);
                result["input_files_unchanged"] = true;
                result["status"] = pass ? "PASS" : "FAIL";
                result["exit_code"] = pass ? 0 : 1;
            }
            catch (System.Exception ex)
            {
                result["status"] = (bool)result["native_execution"] ? "FAIL" : "BLOCKED";
                result["exit_code"] = (bool)result["native_execution"] ? 1 : 2;
                result["error"] = Unwrap(ex).ToString();
            }
            result["completed_utc"] = DateTime.UtcNow.ToString("o");
            try
            {
                if (!mayWriteResult) throw new InvalidOperationException("No safe new JSON result path was supplied.");
                Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
                var temp = resultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                bool ownsTemp = false;
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        ownsTemp = true;
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(Json.Serialize(result));
                    }
                    File.Move(temp, resultPath); // Fails if destination appeared; never overwrite another report.
                    ownsTemp = false;
                }
                finally { if (ownsTemp) File.Delete(temp); }
                AcApp.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nNative adapter scope: " + result["status"] +
                    "; full native gate: NOT_RUN. JSON: " + resultPath + "\n");
            }
            catch (System.Exception ex)
            { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nBLOCKED: cannot write native result: " + ex.Message + "\n"); }
        }

        private static Dictionary<string, object> RunCase(Database db, ObjectId sourceId, Configuration cfg,
            Case c, int repeat, Adapter adapter, string before, string members)
        {
            var r = new Dictionary<string, object> { { "id", c.id }, { "repeat", repeat },
                { "requested", c }, { "status", "FAIL" }, { "rollback", "NOT_CHECKED" } };
            var watch = Stopwatch.StartNew();
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var source = (BlockReference)tr.GetObject(sourceId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                        object sample = null;
                        if (c.mode == "sample")
                        {
                            sample = adapter.From(tr, source, adapter.Cell(tr, source));
                            r["source_usable_as_sample"] = Field(sample, "Usable");
                            if (!(bool)Field(sample, "Usable"))
                                throw new InvalidOperationException("Explicit sample case rejected by production SampleElem (rotation/reflection/scale). " +
                                    "This does not establish failure of the separately selected definition branch; source was not normalized.");
                            adapter.Probe(sample, tr, ms);
                            if (!(bool)Field(sample, "Dyn") || (string)Field(sample, "W") != cfg.width_property || (string)Field(sample, "H") != cfg.height_property)
                                throw new InvalidOperationException("Production sample ProbeDyn does not match the explicit fixture properties.");
                            var sourceOutline = Outline(source, cfg.outline_layer);
                            CheckOutline(sourceOutline, sourceOutline.Min(p => p[0]), sourceOutline.Min(p => p[1]),
                                NumericProperty(source, cfg.width_property), NumericProperty(source, cfg.height_property), cfg.tolerance);
                            r["source_outline_vertices"] = sourceOutline;
                        }
                        else
                            adapter.ProbeDefinition(tr, ms, source.DynamicBlockTableRecord, cfg.width_property, cfg.height_property);
                        var proto = Activator.CreateInstance(adapter.Proto, true);
                        Set(proto, "Def", sample != null ? Field(sample, "Def") : source.DynamicBlockTableRecord); Set(proto, "Te", sample);
                        Set(proto, "Layer", source.Layer); Set(proto, "PW", cfg.width_property); Set(proto, "PH", cfg.height_property);
                        Set(proto, "W", c.width); Set(proto, "H", c.height); Set(proto, "X", c.x); Set(proto, "Y", c.y);
                        Set(proto, "Ox", sample != null ? Field(sample, "OffX") : 0.0);
                        Set(proto, "Oy", sample != null ? Field(sample, "OffY") : 0.0); Set(proto, "Root", "NATIVE_CHECK");
                        object[] args = { tr, ms, proto, c.x, c.y, new Dictionary<ObjectId, List<ObjectId>>(), false };
                        BlockReference made;
                        try { made = (BlockReference)adapter.Make.Invoke(null, args); }
                        catch
                        {
                            // Preserve real native property diagnostics even if MakeDynRef throws before returning.
                            try
                            {
                                var beforeHandles = new HashSet<string>(members.Split('|'));
                                var partial = new List<object>();
                                foreach (ObjectId id in ms)
                                    if (!id.IsErased && !beforeHandles.Contains(id.Handle.ToString()))
                                    {
                                        var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                                        if (br != null) partial.Add(new object[] { br.Handle.ToString(), Properties(br) });
                                    }
                                r["partial_native_insertions"] = partial;
                            }
                            catch (System.Exception diagnosticError) { r["partial_diagnostic_error"] = diagnosticError.Message; }
                            throw;
                        }
                        if (!(bool)args[6] || made == null) throw new InvalidOperationException("MakeDynRef did not return an accepted insertion.");
                        r["actual_properties"] = Properties(made);
                        if (sample != null)
                        {
                            CheckAppearance(source, made, cfg);
                            r["appearance"] = "PASS";
                        }
                        else r["appearance"] = "NOT_APPLICABLE_DEFINITION_MODE";
                        r["attributes"] = CheckAttributes(tr, made);
                        double w = NumericProperty(made, cfg.width_property), h = NumericProperty(made, cfg.height_property);
                        var ext = adapter.Cell(tr, made);
                        r["cell_extents"] = Bounds(ext);
                        r["actual_width"] = w; r["actual_height"] = h;
                        var outline = Outline(made, cfg.outline_layer);
                        r["native_outline_vertices"] = outline;
                        CheckOutline(outline, c.x, c.y, c.width, c.height, cfg.tolerance);
                        RequireNear("width property", w, c.width, cfg.tolerance);
                        RequireNear("height property", h, c.height, cfg.tolerance);
                        RequireNear("cell left", ext.MinPoint.X, c.x, cfg.tolerance);
                        RequireNear("cell bottom", ext.MinPoint.Y, c.y, cfg.tolerance);
                        RequireNear("cell width", ext.MaxPoint.X - ext.MinPoint.X, c.width, cfg.tolerance);
                        RequireNear("cell height", ext.MaxPoint.Y - ext.MinPoint.Y, c.height, cfg.tolerance);
                        if (SourceFingerprint(tr, source) != before)
                            throw new InvalidOperationException("Source geometry/properties/attributes changed during insertion.");
                        r["source_fingerprint_unchanged_during"] = true;
                        r["result_signature"] = HashText(Json.Serialize(new object[] { w, h, Bounds(ext), Properties(made) }));
                        r["status"] = "PASS";
                    }
                    finally { tr.Abort(); }
                }
            }
            catch (System.Exception ex) { r["status"] = "FAIL"; r["error"] = Unwrap(ex).ToString(); }
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    if (Members(tr, db) != members || SourceFingerprint(tr, (BlockReference)tr.GetObject(sourceId, OpenMode.ForRead)) != before)
                        throw new InvalidOperationException("Abort did not restore ModelSpace membership and source fingerprint.");
                    r["rollback"] = "PASS";
                    r["source_fingerprint_unchanged_after_abort"] = true;
                    tr.Abort();
                }
            }
            catch (System.Exception ex) { r["status"] = "FAIL"; r["rollback"] = "FAIL"; r["rollback_error"] = Unwrap(ex).ToString(); }
            r["elapsed_ms"] = watch.ElapsedMilliseconds;
            return r;
        }

        private sealed class Adapter
        {
            internal readonly Type Proto;
            internal readonly MethodInfo Make;
            private readonly MethodInfo cell, from, probe, probeDefinition;
            internal Adapter(Assembly assembly)
            {
                var tile = assembly.GetType("ACladPlugin.TilePatternCommand", true);
                Proto = tile.GetNestedType("Proto", Any);
                var sample = tile.GetNestedType("SampleElem", Any);
                Make = tile.GetMethod("MakeDynRef", Any);
                cell = assembly.GetType("ACladPlugin.CladCommand", true).GetMethod("CellExtents", Any);
                from = sample?.GetMethod("From", Any); probe = sample?.GetMethod("Probe", Any);
                probeDefinition = tile.GetMethod("ProbeDyn", Any);
                if (Proto == null || Make == null || cell == null || from == null || probe == null || probeDefinition == null)
                    throw new InvalidOperationException("Production adapter contract was not found. Harness must be reviewed against this DLL.");
            }
            internal Extents3d Cell(Transaction tr, BlockReference br) { return (Extents3d)cell.Invoke(null, new object[] { tr, br }); }
            internal object From(Transaction tr, BlockReference br, Extents3d ext) { return from.Invoke(null, new object[] { tr, br, ext }); }
            internal void Probe(object sample, Transaction tr, BlockTableRecord ms) { probe.Invoke(sample, new object[] { tr, ms }); }
            internal void ProbeDefinition(Transaction tr, BlockTableRecord ms, ObjectId def, string width, string height)
            {
                object[] args = { tr, ms, def, null, null };
                if (!(bool)probeDefinition.Invoke(null, args) || (string)args[3] != width || (string)args[4] != height)
                    throw new InvalidOperationException("Production definition ProbeDyn does not match the explicit fixture properties.");
            }
        }

        private static object Field(object obj, string name) { return obj.GetType().GetField(name, Any).GetValue(obj); }
        private static void Set(object obj, string name, object value) { obj.GetType().GetField(name, Any).SetValue(obj, value); }
        private static System.Exception Unwrap(System.Exception ex)
        { return ex is TargetInvocationException && ex.InnerException != null ? Unwrap(ex.InnerException) : ex; }
        private static double[] Point(Point3d p) { return new[] { p.X, p.Y, p.Z }; }
        private static double[] Bounds(Extents3d e) { return new[] { e.MinPoint.X, e.MinPoint.Y, e.MinPoint.Z, e.MaxPoint.X, e.MaxPoint.Y, e.MaxPoint.Z }; }
        private static object Scalar(object value)
        {
            if (value == null || value is string || value is bool || value is double || value is float ||
                value is decimal || value is short || value is int || value is long || value is byte) return value;
            if (value is Point3d) return Point((Point3d)value);
            throw new InvalidOperationException("Unsupported dynamic property value type: " + value.GetType().FullName);
        }
        private static object[] Properties(BlockReference br)
        {
            var result = new List<object>();
            if (!br.IsDynamicBlock) return result.ToArray();
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
            {
                object value = p.Value;
                result.Add(new Dictionary<string, object> {
                    { "name", p.PropertyName }, { "value", Scalar(value) }, { "value_type", value?.GetType().FullName },
                    { "property_type_code", p.PropertyTypeCode }, { "read_only", p.ReadOnly },
                    { "visible", p.VisibleInCurrentVisibilityState }, { "units", p.UnitsType.ToString() },
                    { "allowed_values", p.GetAllowedValues().Select(Scalar).ToArray() }
                });
            }
            return result.ToArray();
        }
        // Independent native geometry oracle: full AutoCAD explosion, then every outline vertex.
        // Deliberately supports only an explicitly mapped rectangular polyline; no silent fallback.
        private static double[][] Outline(BlockReference br, string layer)
        {
            using (var objects = new DBObjectCollection())
            {
                try
                {
                    br.Explode(objects);
                    var outlines = objects.Cast<DBObject>().OfType<Polyline>()
                        .Where(p => p.Visible && p.Layer == layer && p.Closed).ToArray();
                    if (outlines.Length != 1 || outlines[0].NumberOfVertices != 4)
                        throw new InvalidOperationException("Fixture requires exactly one closed four-vertex outline polyline on layer: " + layer);
                    var poly = outlines[0];
                    var points = new double[4][];
                    for (int i = 0; i < 4; i++)
                    {
                        if (poly.GetBulgeAt(i) != 0 || poly.GetStartWidthAt(i) != 0 || poly.GetEndWidthAt(i) != 0)
                            throw new InvalidOperationException("Native outline oracle requires straight, zero-width polyline segments.");
                        points[i] = Point(poly.GetPoint3dAt(i));
                    }
                    return points;
                }
                finally { foreach (DBObject item in objects) item.Dispose(); }
            }
        }
        private static void CheckOutline(double[][] vertices, double x, double y, double width, double height, double tolerance)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                var a = vertices[i]; var b = vertices[(i + 1) % vertices.Length];
                bool sameX = Math.Abs(a[0] - b[0]) <= tolerance, sameY = Math.Abs(a[1] - b[1]) <= tolerance;
                if (sameX == sameY) throw new InvalidOperationException("Native outline has a diagonal or zero-length edge.");
            }
            var expected = new List<double[]> { new[] { x, y, 0.0 }, new[] { x + width, y, 0.0 },
                new[] { x + width, y + height, 0.0 }, new[] { x, y + height, 0.0 } };
            foreach (var point in vertices)
            {
                int index = expected.FindIndex(p => p.Zip(point, (a, b) => Finite(b) && Math.Abs(a - b) <= tolerance).All(b => b));
                if (index < 0) throw new InvalidOperationException("Native exploded outline differs from the exact requested rectangle.");
                expected.RemoveAt(index);
            }
            if (expected.Count != 0) throw new InvalidOperationException("Native outline has missing requested rectangle corners.");
        }
        private static DynamicBlockReferenceProperty RequireProperty(BlockReference br, string name)
        {
            var found = new List<DynamicBlockReferenceProperty>();
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                if (p.PropertyName == name) found.Add(p);
            if (found.Count != 1) throw new InvalidOperationException("Expected exactly one source property: " + name);
            return found[0];
        }
        private static void CheckAppearance(BlockReference source, BlockReference made, Configuration cfg)
        {
            if (made.Layer != source.Layer || made.Color.ToString() != source.Color.ToString())
                throw new InvalidOperationException("New insertion did not preserve source layer/color.");
            foreach (DynamicBlockReferenceProperty p in source.DynamicBlockReferencePropertyCollection)
            {
                if (p.ReadOnly || p.PropertyName == "Origin" || p.PropertyName == cfg.width_property || p.PropertyName == cfg.height_property) continue;
                if (Json.Serialize(Scalar(p.Value)) != Json.Serialize(Scalar(RequireProperty(made, p.PropertyName).Value)))
                    throw new InvalidOperationException("New insertion did not preserve non-size property: " + p.PropertyName);
            }
        }
        private static object CheckAttributes(Transaction tr, BlockReference made)
        {
            var expected = new List<string>();
            var definition = (BlockTableRecord)tr.GetObject(made.BlockTableRecord, OpenMode.ForRead);
            foreach (ObjectId id in definition)
            {
                var d = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (d == null || d.Constant) continue;
                string value = d.Tag.Trim().ToUpperInvariant() == "ЗАХВАТКА" ? "NATIVE_CHECK" : d.TextString;
                expected.Add(Json.Serialize(new[] { d.Tag, value }));
            }
            var actual = new List<string>();
            foreach (ObjectId id in made.AttributeCollection)
            {
                var a = (AttributeReference)tr.GetObject(id, OpenMode.ForRead);
                actual.Add(Json.Serialize(new[] { a.Tag, a.TextString }));
            }
            if (!expected.OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(actual.OrderBy(v => v, StringComparer.Ordinal)))
                throw new InvalidOperationException("New insertion attributes do not match native definitions/ЗАХВАТКА=NATIVE_CHECK.");
            return new Dictionary<string, object> { { "status", expected.Count == 0 ? "NOT_APPLICABLE" : "PASS" },
                { "count", actual.Count }, { "tag_and_text", actual } };
        }
        private static double NumericProperty(BlockReference br, string name)
        { return Convert.ToDouble(RequireProperty(br, name).Value, CultureInfo.InvariantCulture); }
        private static string Members(Transaction tr, Database db)
        {
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            return string.Join("|", ms.Cast<ObjectId>().Where(id => !id.IsErased).Select(id => id.Handle.ToString()).OrderBy(s => s));
        }
        private static string SourceFingerprint(Transaction tr, BlockReference br)
        { return Json.Serialize(EntityData(tr, br, new HashSet<ObjectId>(), 0)); }
        private static object EntityData(Transaction tr, Entity ent, HashSet<ObjectId> ancestry, int depth)
        {
            if (depth > 16) throw new InvalidOperationException("Source block nesting exceeds fingerprint limit.");
            var d = new Dictionary<string, object> { { "handle", ent.Handle.ToString() }, { "type", ent.GetType().FullName },
                { "layer", ent.Layer }, { "visible", ent.Visible }, { "color", ent.Color.ToString() },
                { "linetype", ent.Linetype }, { "lineweight", ent.LineWeight.ToString() } };
            try { d["extents"] = Bounds(ent.GeometricExtents); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) { d["extents_error"] = ex.ErrorStatus.ToString(); }
            var poly = ent as Polyline;
            if (poly != null)
            {
                var points = new List<object>();
                for (int i = 0; i < poly.NumberOfVertices; i++)
                    points.Add(new object[] { Point(poly.GetPoint3dAt(i)), poly.GetBulgeAt(i), poly.GetStartWidthAt(i), poly.GetEndWidthAt(i) });
                d["vertices"] = points; d["closed"] = poly.Closed;
            }
            else if (ent is Curve)
            {
                var curve = (Curve)ent; var points = new List<double[]>();
                for (int i = 0; i <= 16; i++)
                    points.Add(Point(curve.GetPointAtParameter(curve.StartParam + (curve.EndParam - curve.StartParam) * i / 16.0)));
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
                d["scale"] = new[] { block.ScaleFactors.X, block.ScaleFactors.Y, block.ScaleFactors.Z };
                d["normal"] = new[] { block.Normal.X, block.Normal.Y, block.Normal.Z };
                d["properties"] = Properties(block);
                d["definition"] = block.BlockTableRecord.Handle.ToString();
                var attrs = new List<object>();
                foreach (ObjectId id in block.AttributeCollection)
                    attrs.Add(EntityData(tr, (Entity)tr.GetObject(id, OpenMode.ForRead), ancestry, depth + 1));
                d["attributes"] = attrs;
                if (!ancestry.Add(block.BlockTableRecord)) throw new InvalidOperationException("Cyclic source block definition.");
                var btr = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                var elements = new List<object>();
                foreach (ObjectId id in btr)
                {
                    if (elements.Count >= 5000) throw new InvalidOperationException("Source definition exceeds fingerprint entity limit.");
                    var child = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (child != null) elements.Add(EntityData(tr, child, ancestry, depth + 1));
                }
                d["entities"] = elements;
                ancestry.Remove(block.BlockTableRecord);
            }
            return d;
        }
        private static bool SamePath(string a, string b)
        { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        private static string HashText(string value) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        private static string HashFile(string path)
        { using (var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var h = SHA256.Create()) return Hex(h.ComputeHash(s)); }
        private static string Hex(byte[] data) { return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }
        private static void RequireHash(string path, string expected)
        { if (!string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SHA256 mismatch: " + path); }
        private static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
        private static void RequireNear(string label, double actual, double expected, double tol)
        { if (!Finite(actual) || Math.Abs(actual - expected) > tol) throw new InvalidOperationException(label + ": " + actual.ToString("R", CultureInfo.InvariantCulture) + " != " + expected.ToString("R", CultureInfo.InvariantCulture)); }
        private static void Validate(Configuration c)
        {
            Guid parsed;
            if (c == null || c.schema_version != 1 || !Guid.TryParse(c.run_id, out parsed) || string.IsNullOrWhiteSpace(c.fixture_id))
                throw new InvalidOperationException("Invalid schema/run_id/fixture_id.");
            foreach (string p in new[] { c.source_dwg, c.working_dwg, c.production_dll })
                if (string.IsNullOrWhiteSpace(p) || !Path.IsPathRooted(p) || !File.Exists(p)) throw new InvalidOperationException("Missing absolute input path.");
            if (SamePath(c.source_dwg, c.working_dwg)) throw new InvalidOperationException("Refusing to test the original DWG: a private copy is mandatory.");
            foreach (string sha in new[] { c.source_sha256, c.working_sha256, c.production_sha256, c.harness_sha256 })
                if (sha == null || sha.Length != 64 || sha.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidOperationException("Expected explicit SHA256 provenance.");
            if (!string.Equals(c.source_sha256, c.working_sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Working copy must be byte-identical to its source before opening.");
            if (string.IsNullOrWhiteSpace(c.source_handle) || c.source_handle.Any(ch => !Uri.IsHexDigit(ch)) ||
                string.IsNullOrWhiteSpace(c.width_property) || string.IsNullOrWhiteSpace(c.height_property) ||
                string.IsNullOrWhiteSpace(c.outline_layer) || c.width_property == c.height_property)
                throw new InvalidOperationException("Explicit source handle and distinct property names are required.");
            if (!Finite(c.tolerance) || c.tolerance <= 0 || c.tolerance > 0.0001 || c.repeats < 2 || c.repeats > 10)
                throw new InvalidOperationException("Tolerance must be (0, 0.0001], repeats must be 2..10.");
            if (c.cases == null || c.cases.Length == 0 || c.cases.Length > 100) throw new InvalidOperationException("Explicit fixture-specific cases are required.");
            var ids = new HashSet<string>();
            foreach (var t in c.cases)
                if (t == null || string.IsNullOrWhiteSpace(t.id) || !ids.Add(t.id) ||
                    (t.mode != "sample" && t.mode != "definition") || !Finite(t.width) || !Finite(t.height) ||
                    !Finite(t.x) || !Finite(t.y) || t.width <= 0 || t.height <= 0)
                    throw new InvalidOperationException("Invalid or duplicate test case.");
        }
    }
}
