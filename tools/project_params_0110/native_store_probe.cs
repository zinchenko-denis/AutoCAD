using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using FacadeSafety;
using Store = FacadeSafety.FacadeProjectParameterStore;

internal static class ProjectStoreProbe
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly List<object> Cases = new List<object>();
    private static readonly List<object> Performance = new List<object>();
    private static int failed;
    private static void Need(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static void Refuses(Action action)
    {
        bool refused = false;
        try { action(); } catch (InvalidOperationException) { refused = true; }
        Need(refused, "expected an explicit refusal");
    }
    private static void Check(string name, Action action)
    {
        try { action(); Cases.Add(new { name, status = "PASS" }); }
        catch (Exception error) { failed++; Cases.Add(new { name, status = "FAIL", reason = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static string Hash(string value)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
    }
    private static Dictionary<string, object> Project(long revision = 1)
    {
        return new Dictionary<string, object> {
            { "schema", "aframe_project_parameters/1" },
            { "project_id", "11111111111111111111111111111111" },
            { "revision", revision }, { "content_digest", new string('a', 64) },
            { "selection", new Dictionary<string, object> { { "test_fixture", "opaque validated payload" } } }
        };
    }
    private static Dictionary<string, object> Override(object value)
    {
        return new Dictionary<string, object> {
            { "schema", "afacade_zone_parameters/1" },
            { "project_id", "11111111111111111111111111111111" },
            { "revision", 1 },
            { "overrides", new Dictionary<string, object> { { "geometry.cladding_front_offset_mm", value } } }
        };
    }
    private static Store.Record ReadProject(Database db)
    { using (var tr = new Transaction(db)) return Store.ReadProject(tr, db); }
    private static Store.Record ReadZone(Database db, Hatch hatch, string fingerprint = "fz1:fixture:original")
    { using (var tr = new Transaction(db)) return Store.ReadZone(tr, hatch, fingerprint); }
    private static void WriteProject(Database db, Dictionary<string, object> payload)
    {
        using (var tr = new Transaction(db)) { var before = Store.ReadProject(tr, db); Store.WriteProject(tr, db, before, payload); tr.Commit(); }
    }
    private static void WriteZone(Database db, Hatch hatch, Dictionary<string, object> payload)
    {
        using (var tr = new Transaction(db)) { var before = Store.ReadZone(tr, hatch, "fz1:fixture:original"); Store.WriteZone(tr, hatch, "fz1:fixture:original", before, payload); tr.Commit(); }
    }
    private static DBDictionary Root(Database db)
    {
        var nod = (DBDictionary)db.NamedObjectsDictionaryId.Item;
        return (DBDictionary)nod.Items[Store.ProjectRootKey].Item;
    }
    private static Xrecord ProjectRecord(Database db)
    { return (Xrecord)Root(db).Items[Store.ProjectRecordKey].Item; }
    private static Xrecord ZoneRecord(Hatch hatch)
    { return (Xrecord)((DBDictionary)hatch.ExtensionDictionary.Item).Items[Store.ZoneKey].Item; }
    private static void ReplaceText(Xrecord record, string raw)
    {
        var refs = (record.Values ?? new TypedValue[0]).Where(value => value.TypeCode != (int)DxfCode.Text);
        record.Values = new[] { new TypedValue((int)DxfCode.Text, raw) }.Concat(refs).ToArray();
    }
    private static void ChangeEnvelope(Hatch hatch, string key, object value)
    {
        var record = ZoneRecord(hatch);
        string raw = string.Concat(record.Values.Where(v => v.TypeCode == (int)DxfCode.Text).Select(v => Convert.ToString(v.Value)));
        var body = (Dictionary<string, object>)Json.DeserializeObject(raw); body[key] = value;
        ReplaceText(record, Json.Serialize(body));
    }
    private static Hatch CopyRecord(Database db, Hatch original)
    {
        var copy = db.Add(new Hatch()); copy.CreateExtensionDictionary();
        var ext = (DBDictionary)copy.ExtensionDictionary.Item;
        ext.SetAt(Store.ZoneKey, new Xrecord { Values = ZoneRecord(original).Values.ToArray(), XlateReferences = true });
        return copy;
    }
    public static int Main(string[] args)
    {
        Check("absent project and zone reads create no persistent records", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); ProjectCadCounters.Reset();
            using (var tr = new Transaction(db)) {
                Need(!Store.ReadProject(tr, db).Present && !Store.ReadZone(tr, hatch).Present, "absent became explicit");
                Need(hatch.ExtensionDictionary.IsNull, "read created extension dictionary");
                Need(((DBDictionary)db.NamedObjectsDictionaryId.Item).Items.Count == 0, "read created NOD project");
                Need(ProjectCadCounters.DictionaryWrites == 0 && ProjectCadCounters.DataWrites == 0, "read wrote persistent state");
            }
        });
        Check("project read context caches an absent snapshot", delegate {
            var db = new Database(); var context = new Store.ReadContext(); ProjectCadCounters.Reset();
            using (var tr = new Transaction(db)) for (int i = 0; i < 1000; i++) Need(!Store.ReadProject(tr, db, context).Present, "absent snapshot changed");
            Need(context.ProjectReads == 1 && ProjectCadCounters.NodReads == 1 && ProjectCadCounters.ModelSpaceReads == 0, "absent context reread NOD");
        });
        Check("project raw digest is exact JSON and committed payload survives", delegate {
            var db = new Database(); WriteProject(db, Project()); var value = ReadProject(db);
            Need(value.Present && value.Payload != null && value.RecordDigest == Hash(value.Json), "raw digest or payload missing");
            var dependency = Store.Dependency(value);
            Need(dependency.project_id == (string)Project()["project_id"] && dependency.revision == 1 &&
                dependency.content_digest == new string('a', 64) && dependency.record_digest == value.RecordDigest,
                "dependency conflated content and raw record digests");
        });
        Check("existing malformed project does not become absent", delegate {
            var db = new Database(); WriteProject(db, Project());
            foreach (var raw in new[] { "", "{broken", "null", "[]", "42" }) {
                ReplaceText(ProjectRecord(db), raw); Refuses(() => ReadProject(db));
            }
            ProjectRecord(db).Values = null; Refuses(() => ReadProject(db));
            ProjectRecord(db).Values = new[] { new TypedValue(999, "ignored") }; Refuses(() => ReadProject(db));
        });
        Check("project root and parameters must have correct CAD object types", delegate {
            var db = new Database(); var nod = (DBDictionary)db.NamedObjectsDictionaryId.Item;
            nod.SetAt(Store.ProjectRootKey, db.Add(new Xrecord())); Refuses(() => ReadProject(db));
            var root = db.Add(new DBDictionary()); nod.SetAt(Store.ProjectRootKey, root);
            root.SetAt(Store.ProjectRecordKey, db.Add(new Hatch())); Refuses(() => ReadProject(db));
        });
        Check("malformed project identity and dependency header refuse", delegate {
            foreach (var key in new[] { "schema", "project_id", "revision", "content_digest" }) {
                var db = new Database(); WriteProject(db, Project()); var bad = Project();
                bad[key] = key == "revision" ? (object)true : "invalid";
                ReplaceText(ProjectRecord(db), Json.Serialize(bad)); Refuses(() => ReadProject(db));
            }
            var absent = new Store.Record(); Refuses(() => Store.Dependency(absent));
            var current = new Database(); WriteProject(current, Project());
            using (var tr = new Transaction(current)) Refuses(() => Store.VerifyProject(tr, current,
                new Store.ProjectDependency { project_id = "not-guid", revision = 1, content_digest = new string('a', 64), record_digest = new string('a', 64) }));
        });
        Check("unbound legacy dependency does not inspect unrelated project state", delegate {
            var db = new Database(); var nod = (DBDictionary)db.NamedObjectsDictionaryId.Item;
            nod.SetAt(Store.ProjectRootKey, db.Add(new Xrecord())); ProjectCadCounters.Reset();
            using (var tr = new Transaction(db)) Store.VerifyProject(tr, db, null);
            Need(ProjectCadCounters.NodReads == 0 && ProjectCadCounters.ObjectReads == 0, "unbound legacy became project-dependent");
        });
        Check("project CAS rejects whitespace-only raw change before writing", delegate {
            var db = new Database(); WriteProject(db, Project()); var expected = ReadProject(db);
            ReplaceText(ProjectRecord(db), " \n" + expected.Json);
            var sameContent = ReadProject(db); Need(sameContent.RecordDigest != expected.RecordDigest, "raw mutation disappeared");
            ProjectCadCounters.Reset();
            using (var tr = new Transaction(db)) Refuses(() => Store.WriteProject(tr, db, expected, Project(2)));
            Need(ProjectCadCounters.DataWrites == 0 && ProjectCadCounters.DictionaryWrites == 0, "stale CAS wrote data");
            Need(ReadProject(db).RecordDigest == sameContent.RecordDigest, "stale CAS changed record");
        });
        Check("project CAS checks absent-to-present and present-to-absent changes", delegate {
            var db = new Database(); var missing = ReadProject(db); WriteProject(db, Project());
            using (var tr = new Transaction(db)) Refuses(() => Store.WriteProject(tr, db, missing, Project(2)));
            var existing = ReadProject(db); Root(db).Remove(Store.ProjectRecordKey);
            using (var tr = new Transaction(db)) Refuses(() => Store.WriteProject(tr, db, existing, Project(2)));
        });
        Check("uncommitted writes roll back in the explicit transaction model", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch());
            using (var tr = new Transaction(db)) {
                Store.WriteProject(tr, db, Store.ReadProject(tr, db), Project());
                Store.WriteZone(tr, hatch, "fz1:fixture:original", Store.ReadZone(tr, hatch), Override(340));
                Need(Store.ReadProject(tr, db).Present && Store.ReadZone(tr, hatch).Present, "fixture did not stage writes");
            }
            Need(!ReadProject(db).Present && !ReadZone(db, hatch).Present && hatch.ExtensionDictionary.IsNull,
                "uncommitted transaction left parameter state");
        });
        Check("later storage failure rolls back earlier writes in the transaction model", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteProject(db, Project()); var before = ReadProject(db);
            bool failedAsExpected = false;
            try {
                using (var tr = new Transaction(db)) {
                    Store.WriteProject(tr, db, before, Project(2));
                    ProjectCadCounters.FailAfterMutations = 0;
                    Store.WriteZone(tr, hatch, "fz1:fixture:original", Store.ReadZone(tr, hatch), Override(340));
                    tr.Commit();
                }
            } catch (InvalidOperationException) { failedAsExpected = true; }
            finally { ProjectCadCounters.FailAfterMutations = -1; }
            Need(failedAsExpected && ReadProject(db).RecordDigest == before.RecordDigest && !ReadZone(db, hatch).Present,
                "failed batch left committed parameter changes");
        });
        Check("canonical zone roundtrip verifies owner self-pointer and generation", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            var record = ReadZone(db, hatch);
            Need(record.Present && record.Owner == hatch.Handle.ToString() && record.ZoneFingerprint == "fz1:fixture:original" &&
                record.RecordDigest == Hash(record.Json), "zone envelope identity incomplete");
            Refuses(() => ReadZone(db, hatch, "fz1:new-generation:same-shape"));
            Need(ZoneRecord(hatch).Values.Count(v => v.TypeCode == (int)DxfCode.SoftPointerId) == 1, "zone must have exactly one self-pointer");
        });
        Check("copied zone record does not bind to the copy", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            var copy = CopyRecord(db, hatch); Refuses(() => ReadZone(db, copy));
            ChangeEnvelope(copy, "owner", copy.Handle.ToString()); Refuses(() => ReadZone(db, copy));
            var refs = ZoneRecord(copy).Values;
            for (int i = 0; i < refs.Length; i++) if (refs[i].TypeCode == (int)DxfCode.SoftPointerId) refs[i] = new TypedValue((int)DxfCode.SoftPointerId, copy.ObjectId);
            ChangeEnvelope(copy, "owner", hatch.Handle.ToString()); Refuses(() => ReadZone(db, copy));
        });
        Check("zone rejects missing duplicate null and foreign self-pointers", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            var record = ZoneRecord(hatch); var text = record.Values.Where(v => v.TypeCode == (int)DxfCode.Text).ToArray();
            foreach (var refs in new[] {
                new TypedValue[0],
                new[] { new TypedValue((int)DxfCode.SoftPointerId, ObjectId.Null) },
                new[] { new TypedValue((int)DxfCode.SoftPointerId, hatch.ObjectId), new TypedValue((int)DxfCode.SoftPointerId, hatch.ObjectId) },
                new[] { new TypedValue((int)DxfCode.SoftPointerId, new Database().Add(new Hatch()).ObjectId) } }) {
                record.Values = text.Concat(refs).ToArray(); Refuses(() => ReadZone(db, hatch));
            }
        });
        Check("zone malformed existing data never becomes inherited absence", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            foreach (var raw in new[] { "", "{broken", "null", "[]", "42" }) { ReplaceText(ZoneRecord(hatch), raw); Refuses(() => ReadZone(db, hatch)); }
            ZoneRecord(hatch).Values = null; Refuses(() => ReadZone(db, hatch));
        });
        Check("existing invalid extension dictionary never becomes absent binding", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch());
            hatch.ExtensionDictionary = db.Add(new Xrecord()).ObjectId;
            Refuses(() => ReadZone(db, hatch));
        });
        Check("zone write and clear require an explicit verified fingerprint", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            var before = ReadZone(db, hatch);
            using (var tr = new Transaction(db)) {
                Refuses(() => Store.WriteZone(tr, hatch, null, before, Override(350)));
                Refuses(() => Store.WriteZone(tr, hatch, "fz1:fixture:original", before, null));
                Refuses(() => Store.ClearZone(tr, hatch, null, before));
            }
        });
        Check("zone create and clear CAS reject concurrent record changes", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); var absent = ReadZone(db, hatch);
            WriteZone(db, hatch, Override(340));
            using (var tr = new Transaction(db)) Refuses(() => Store.WriteZone(tr, hatch, "fz1:fixture:original", absent, Override(350)));
            var before = ReadZone(db, hatch); ReplaceText(ZoneRecord(hatch), " " + before.Json);
            using (var tr = new Transaction(db)) Refuses(() => Store.ClearZone(tr, hatch, "fz1:fixture:original", before));
            var another = db.Add(new Hatch()); var missing = ReadZone(db, another); ProjectCadCounters.Reset();
            using (var tr = new Transaction(db)) { Store.ClearZone(tr, another, "fz1:fixture:original", missing); tr.Commit(); }
            Need(ProjectCadCounters.DataWrites == 0 && ProjectCadCounters.DictionaryWrites == 0 && another.ExtensionDictionary.IsNull,
                "clearing absent record created metadata");
        });
        Check("zone CAS checks exact raw record even during explicit generation rebind", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340)); var expected = ReadZone(db, hatch);
            ReplaceText(ZoneRecord(hatch), " \n" + expected.Json); ProjectCadCounters.Reset();
            using (var tr = new Transaction(db)) Refuses(() => Store.WriteZone(tr, hatch, "fz1:fixture:original", expected, Override(360)));
            Need(ProjectCadCounters.DataWrites == 0 && ProjectCadCounters.DictionaryWrites == 0, "stale zone CAS wrote data");
            using (var tr = new Transaction(db)) Refuses(() => Store.WriteZone(tr, hatch, "fz1:changed:generation", expected, Override(360)));
        });
        Check("explicit same-owner generation rebind differs from ordinary stale consumption", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            var previous = ReadZone(db, hatch);
            Refuses(() => ReadZone(db, hatch, "fz1:changed:generation"));
            using (var tr = new Transaction(db)) {
                Store.WriteZone(tr, hatch, "fz1:changed:generation", previous, Override(350)); tr.Commit();
            }
            Need(ReadZone(db, hatch, "fz1:changed:generation").ZoneFingerprint == "fz1:changed:generation", "explicit rebind did not preserve new verified generation");
            Refuses(() => ReadZone(db, hatch, "fz1:fixture:original"));
            var copy = CopyRecord(db, hatch);
            using (var tr = new Transaction(db)) Refuses(() => Store.WriteZone(tr, copy, "fz1:copy:generation", previous, Override(350)));
        });
        Check("explicit null empty array and inherit remain distinct in storage", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch());
            WriteZone(db, hatch, Override(null)); var explicitNull = ReadZone(db, hatch);
            Need(explicitNull.Present && ((Dictionary<string, object>)explicitNull.Payload["overrides"]).ContainsKey("geometry.cladding_front_offset_mm"), "null override lost");
            var emptyLayers = Override(null); emptyLayers["overrides"] = new Dictionary<string, object> { { "geometry.insulation_layers_mm", new object[0] } };
            WriteZone(db, hatch, emptyLayers); var explicitEmpty = ReadZone(db, hatch);
            Need(explicitEmpty.Present && ((object[])((Dictionary<string, object>)explicitEmpty.Payload["overrides"])["geometry.insulation_layers_mm"]).Length == 0,
                "empty value became inheritance");
            using (var tr = new Transaction(db)) { Store.ClearZone(tr, hatch, "fz1:fixture:original", Store.ReadZone(tr, hatch)); tr.Commit(); }
            Need(!ReadZone(db, hatch).Present, "clear failed to restore absent binding");
        });
        foreach (int count in new[] { 100, 1000 }) { int n = count; Check("project dependency read caching " + n, delegate {
            var db = new Database(); WriteProject(db, Project()); var dependency = Store.Dependency(ReadProject(db));
            var context = new Store.ReadContext(); ProjectCadCounters.Reset(); var watch = Stopwatch.StartNew();
            using (var tr = new Transaction(db)) for (int i = 0; i < n; i++) Store.VerifyProject(tr, db, dependency, context);
            watch.Stop();
            Need(context.ProjectReads == 1 && ProjectCadCounters.NodReads == 1 && ProjectCadCounters.DataReads == 1 &&
                ProjectCadCounters.ModelSpaceReads == 0, "project dependency was reread per consumer");
            Performance.Add(new { scenario = "shared_project_dependency", consumers = n, elapsed_ms = watch.Elapsed.TotalMilliseconds,
                counters = ProjectCadCounters.Snapshot(), context_project_reads = context.ProjectReads });
        }); }
        foreach (int count in new[] { 100, 1000 }) { int n = count; Check("distinct zone record reads scale linearly " + n, delegate {
            var db = new Database(); var zones = new List<Hatch>();
            using (var tr = new Transaction(db)) {
                for (int i = 0; i < n; i++) { var hatch = db.Add(new Hatch()); zones.Add(hatch);
                    Store.WriteZone(tr, hatch, "fz1:fixture:original", Store.ReadZone(tr, hatch), Override(340)); }
                tr.Commit();
            }
            ProjectCadCounters.Reset(); var watch = Stopwatch.StartNew();
            using (var tr = new Transaction(db)) foreach (var hatch in zones) Need(Store.ReadZone(tr, hatch, "fz1:fixture:original").Present, "zone disappeared");
            watch.Stop();
            Need(ProjectCadCounters.DataReads == n && ProjectCadCounters.BufferDisposals == n &&
                ProjectCadCounters.ObjectReads == 2 * n && ProjectCadCounters.NodReads == 0 && ProjectCadCounters.ModelSpaceReads == 0,
                "zone read access is not bounded by unique selected owners");
            Performance.Add(new { scenario = "distinct_zone_records", zones = n, elapsed_ms = watch.Elapsed.TotalMilliseconds,
                counters = ProjectCadCounters.Snapshot() });
        }); }
        foreach (int count in new[] { 100, 1000 }) { int n = count; Check("same canonical zone read caching " + n, delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            var context = new Store.ReadContext(); ProjectCadCounters.Reset(); var watch = Stopwatch.StartNew();
            using (var tr = new Transaction(db)) for (int i = 0; i < n; i++)
                Need(Store.ReadZone(tr, hatch, "fz1:fixture:original", context).Present, "cached binding disappeared");
            watch.Stop();
            Need(context.ZoneReads == 1 && ProjectCadCounters.DataReads == 1 && ProjectCadCounters.BufferDisposals == 1 &&
                ProjectCadCounters.ObjectReads == 2 && ProjectCadCounters.NodReads == 0 && ProjectCadCounters.ModelSpaceReads == 0,
                "canonical binding was reread per consumer");
            Performance.Add(new { scenario = "same_canonical_zone", consumers = n, elapsed_ms = watch.Elapsed.TotalMilliseconds,
                counters = ProjectCadCounters.Snapshot(), context_zone_reads = context.ZoneReads });
        }); }
        Check("cached zone snapshot still checks every requested generation", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            var context = new Store.ReadContext(); ProjectCadCounters.Reset();
            using (var tr = new Transaction(db)) {
                Need(Store.ReadZone(tr, hatch, null, context).Present, "snapshot missing");
                Refuses(() => Store.ReadZone(tr, hatch, "fz1:changed:generation", context));
                Need(Store.ReadZone(tr, hatch, "fz1:fixture:original", context).Present, "valid cached generation refused");
            }
            Need(context.ZoneReads == 1 && ProjectCadCounters.DataReads == 1, "cached generation check reread data");
        });
        Check("zone absence is cached only within its phase and fresh context sees binding", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); var context = new Store.ReadContext();
            using (var tr = new Transaction(db)) {
                for (int i = 0; i < 1000; i++) Need(!Store.ReadZone(tr, hatch, null, context).Present, "absent became bound");
                Need(context.ZoneReads == 1 && context.Zones.Count == 1, "absence was not cached");
                Store.WriteZone(tr, hatch, "fz1:fixture:original", context.Zones[hatch.ObjectId], Override(340));
                Need(!Store.ReadZone(tr, hatch, null, context).Present, "phase snapshot mutated implicitly");
                Need(Store.ReadZone(tr, hatch, "fz1:fixture:original", new Store.ReadContext()).Present, "fresh phase reused old absence");
                tr.Commit();
            }
        });
        Check("fresh zone context and writer CAS detect a change after cached snapshot", delegate {
            var db = new Database(); var hatch = db.Add(new Hatch()); WriteZone(db, hatch, Override(340));
            using (var tr = new Transaction(db)) {
                var context = new Store.ReadContext(); var before = Store.ReadZone(tr, hatch, "fz1:fixture:original", context);
                ReplaceText(ZoneRecord(hatch), " " + before.Json);
                var fresh = Store.ReadZone(tr, hatch, "fz1:fixture:original", new Store.ReadContext());
                Need(fresh.RecordDigest != before.RecordDigest, "fresh phase hid raw mutation");
                ProjectCadCounters.Reset();
                Refuses(() => Store.WriteZone(tr, hatch, "fz1:fixture:original", before, Override(350)));
                Refuses(() => Store.ClearZone(tr, hatch, "fz1:fixture:original", before));
                Need(ProjectCadCounters.DataWrites == 0 && ProjectCadCounters.DictionaryWrites == 0, "writer trusted cached CAS");
            }
        });
        Check("result buffers are read once and disposed on accepted and refused data", delegate {
            var db = new Database(); WriteProject(db, Project()); ProjectCadCounters.Reset(); ReadProject(db);
            Need(ProjectCadCounters.DataReads == 1 && ProjectCadCounters.BufferDisposals == 1, "accepted buffer reread or leaked");
            ReplaceText(ProjectRecord(db), "{broken"); ProjectCadCounters.Reset(); Refuses(() => ReadProject(db));
            Need(ProjectCadCounters.DataReads == 1 && ProjectCadCounters.BufferDisposals == 1, "refused buffer reread or leaked");
        });
        Check("new validation context detects raw or semantic project change", delegate {
            var db = new Database(); WriteProject(db, Project()); var original = ReadProject(db); var dependency = Store.Dependency(original);
            ReplaceText(ProjectRecord(db), " " + original.Json);
            using (var tr = new Transaction(db)) Refuses(() => Store.VerifyProject(tr, db, dependency, new Store.ReadContext()));
            WriteProject(db, Project(2));
            using (var tr = new Transaction(db)) Refuses(() => Store.VerifyProject(tr, db, dependency, new Store.ReadContext()));
        });
        File.WriteAllText(args[0], Json.Serialize(new { status = failed == 0 ? "PASS" : "FAIL", checks = Cases.Count,
            cases = Cases, performance = Performance, live_autocad_checked = false,
            rollback_scope = "explicit in-memory transaction model only" }));
        Console.WriteLine("Project store native checks: " + Cases.Count + (failed == 0 ? " PASS" : " FAIL"));
        return failed == 0 ? 0 : 1;
    }
}
