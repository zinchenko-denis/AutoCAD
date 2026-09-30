using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;
using AFramePlugin;

internal static class ConnectionStoreProbe
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly List<object> Cases = new List<object>();
    static int Failed;
    static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    static T Clone<T>(T value) { return Json.Deserialize<T>(Json.Serialize(value)); }
    static void Check(string name, Action action)
    {
        try { action(); Cases.Add(new { name, status = "PASS" }); }
        catch (Exception e) { Failed++; Cases.Add(new { name, status = "FAIL", reason = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static QuantityElement Part(string id, string role, string zone = "Z1", double? length = null)
    {
        return new QuantityElement { element_id = id, role = role, zone_ids = new List<string> { zone },
            mark = "ГП-40-40-1,2", product_id = null, length_mm = length, type = role,
            orientation = "vertical", cad_entities = new List<QuantityCadEntity> {
                new QuantityCadEntity { handle = "H" + id, role = "primary", fingerprint = "fingerprint:" + id } } };
    }
    static QuantityReport Fixture()
    {
        var p = new QuantityConnectionPassport { passport_id = "run:connections", source_run_id = "run", zone_ids = new List<string> { "Z1" },
            status = "inventory_only", scheme = "vertical", catalog_scope = "reference_project_only", catalog_revision = new string('a', 64) };
        p.sources.Add(new QuantityConnectionSource { source_id = "source", sha256 = new string('b', 64), kind = "private_project_calculation", edition = "2026", locator = "PDF 3" });
        p.references.Add(new QuantityConnectionReference { reference_id = "reference", kind = "project_calculation_section", designation = "ГП-40-40-1,2", role = "rail", source_id = "source", properties_status = "not_imported", pdf_pages = new List<int> { 3 } });
        p.members.Add(new QuantityConnectionMember { rail_element_id = "run:rails:0", zone_id = "Z1", support_count = 2, span_count = 1,
            bottom_free_mm = 200, top_free_mm = 800, intervals_mm = new List<double> { 2000 },
            supports = new List<QuantityConnectionSupport> {
                new QuantityConnectionSupport { offset_mm = 200, bracket_element_ids = new List<string> { "run:brackets:0" } },
                new QuantityConnectionSupport { offset_mm = 2200, bracket_element_ids = new List<string> { "run:brackets:1" } } },
            profile_match = new QuantityConnectionProfileMatch { status = "matched_source_identity", reference_ids = new List<string> { "reference" } } });
        p.summary = new QuantityConnectionSummary { members = 1, support_positions = 2, support_links = 2, matched_profile_references = 1 };
        return new QuantityReport { kind = "frame", document_id = "D1", report_id = "R", run_id = "run", algorithm = "fixture/1", zone_ids = new List<string> { "Z1" },
            elements = new List<QuantityElement> { Part("run:rails:0", "rail", "Z1", 3000), Part("run:brackets:0", "bracket"), Part("run:brackets:1", "bracket") },
            connection_passports = new List<QuantityConnectionPassport> { p } };
    }
    static QuantityResult Build(QuantityReport r, params string[] selected)
    { return FacadeQuantitiesCore.BuildRows(new[] { r }, selected.Length == 0 ? null : selected, true, false); }
    static void Refused(QuantityReport r)
    {
        var result = Build(r);
        Need(!result.ok && result.rows.Count == 0 && result.issues.Any(x => x.code == "Q_CONNECTION_INVALID"), Json.Serialize(result.issues));
    }
    public static int Main(string[] args)
    {
        if (args[0] == "--produce")
        {
            var payload = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(args[1]));
            try
            {
                var mapping = new Dictionary<string, string>();
                foreach (var item in (Dictionary<string, object>)payload["mapping"]) mapping[item.Key] = (string)item.Value;
                var report = FrameQuantities.BuildReport((Dictionary<string, object>)payload["response"], (Dictionary<string, object>)payload["request"], mapping);
                report.document_id = "connection-producer-fixture";
                for (int i = 0; i < report.elements.Count; i++) report.elements[i].cad_entities.Add(new QuantityCadEntity {
                    handle = (i + 1).ToString("X"), role = "primary", fingerprint = "fixture:" + i });
                var rows = Build(report);
                File.WriteAllText(args[2], Json.Serialize(new { ok = rows.ok, report, rows }));
            }
            catch (InvalidOperationException e) { File.WriteAllText(args[2], Json.Serialize(new { ok = false, error = e.Message })); }
            return 0;
        }
        Check("typed report roundtrip and duplicate conflict identity", delegate {
            var r = Fixture(); var roundtrip = Clone(r);
            Need(Build(r).ok && FacadeQuantitiesCore.BuildRows(new[] { r, roundtrip }, null, true, false).ok, "roundtrip conflict");
            Need(roundtrip.elements.All(x => x.product_id == null), "project reference became SKU");
            Need(!roundtrip.engine_summary.ContainsKey("connection_passport"), "duplicate full passport");
        });
        Check("published legacy null and missing connection field remain readable", delegate {
            var r = Fixture(); r.connection_passports = null;
            Need(Build(Clone(r)).ok, "null legacy rejected");
            string old = Json.Serialize(r).Replace(",\"connection_passports\":null", "");
            Need(Json.Deserialize<QuantityReport>(old).connection_passports == null && Build(Json.Deserialize<QuantityReport>(old)).ok, "missing old field changed meaning");
        });
        Action<QuantityReport>[] invalid = {
            r => r.connection_passports[0].members[0].rail_element_id = "missing",
            r => r.connection_passports[0].members[0].supports[0].bracket_element_ids[0] = "missing",
            r => r.connection_passports[0].members[0].supports[1].bracket_element_ids[0] = "run:brackets:0",
            r => r.connection_passports[0].members[0].supports[0].bracket_element_ids[0] = "run:rails:0",
            r => r.connection_passports[0].members[0].span_count = 3,
            r => r.connection_passports[0].members[0].intervals_mm[0] = 1900,
            r => r.connection_passports[0].members[0].bottom_free_mm = 0,
            r => r.connection_passports[0].members[0].supports[0].offset_mm = -1,
            r => r.connection_passports[0].summary.support_links = 99,
            r => r.connection_passports[0].coverage.fixed_sliding = "fixed",
            r => r.connection_passports[0].coverage.strength = "verified",
            r => r.connection_passports[0].sources[0].sha256 = "unconfirmed",
            r => r.connection_passports[0].references[0].source_id = "absent",
            r => r.connection_passports[0].references[0].designation = "ГП",
            r => r.connection_passports[0].references[0].role = "bracket",
            r => r.connection_passports[0].references[0].properties_status = "approved",
            r => r.connection_passports[0].members[0].profile_match.reference_ids[0] = "missing",
            r => r.connection_passports[0].members[0].profile_match.status = "unknown",
            r => r.connection_passports[0].status = "verified",
            r => r.connection_passports[0].members.Clear(),
        };
        for (int i = 0; i < invalid.Length; i++) { int index = i; Check("invalid geometry/provenance/engineering contract " + i, delegate { var r = Fixture(); invalid[index](r); Refused(r); }); }
        Check("connection content participates in same-report conflict key", delegate {
            var mutations = new Action<QuantityConnectionPassport>[] {
                p => p.sources[0].locator = "different page",
                p => p.references[0].pdf_pages[0] = 4,
                p => p.members[0].supports[0].offset_mm += 1,
                p => p.coverage.strength = "verified",
                p => p.issues.Add(new QuantityConnectionIssue { code = "changed", message = "changed", count = 1 }),
                p => p.status = "unavailable" };
            foreach (var mutation in mutations) {
                var original = Fixture(); var changed = Clone(original); mutation(changed.connection_passports[0]);
                var result = FacadeQuantitiesCore.BuildRows(new[] { original, changed }, null, true, false);
                Need(!result.ok && result.issues.Any(x => x.code == "Q_REPORT_CONFLICT"), "connection conflict invisible");
            }
        });
        Check("missing member or invented zero-member zone cannot become full inventory", delegate {
            var r = Fixture(); r.elements.Add(Part("run:rails:1", "rail", "Z1", 1000)); Refused(r);
            r = Fixture(); r.zone_ids.Add("Z2"); r.connection_passports[0].zone_ids.Add("Z2"); Refused(r);
        });
        Check("same connection ID in different reports detects changed inventory", delegate {
            var a = Fixture(); var b = Clone(a); b.run_id = b.report_id = "other";
            b.connection_passports[0].sources[0].locator = "changed locator";
            var result = FacadeQuantitiesCore.BuildRows(new[] { a, b }, null, true, false);
            Need(!result.ok && result.issues.Any(x => x.code == "Q_CONNECTION_CONFLICT"), "cross-report connection ID conflict hidden");
        });
        Check("multiple retained origins in one zone stay distinct and complete", delegate {
            var r = Fixture(); var other = Clone(r.connection_passports[0]); other.passport_id = "run2:connections"; other.source_run_id = "run2";
            foreach (var m in other.members) {
                m.rail_element_id = m.rail_element_id.Replace("run:", "run2:");
                foreach (var support in m.supports) support.bracket_element_ids = support.bracket_element_ids.Select(x => x.Replace("run:", "run2:")).ToList();
            }
            var added = r.elements.Select(x => Clone(x)).ToList();
            foreach (var e in added) { e.element_id = e.element_id.Replace("run:", "run2:"); e.cad_entities[0].handle = "OTHER" + e.cad_entities[0].handle; }
            r.elements.AddRange(added); r.connection_passports.Add(other);
            Need(Build(r).ok, "retained origins wrongly merged or rejected");
            other.members.Clear(); other.summary = new QuantityConnectionSummary(); Refused(r);
        });
        Check("selected zone cannot hide invalid unselected connections", delegate {
            var r = Fixture(); r.zone_ids.Add("Z2"); r.elements.Add(Part("run:rails:1", "rail", "Z2", 1000));
            var other = Clone(r.connection_passports[0]); other.passport_id = "other"; other.zone_ids = new List<string> { "Z2" };
            other.members[0].rail_element_id = "run:rails:1"; other.members[0].zone_id = "Z2"; r.connection_passports.Add(other);
            var result = Build(r, "Z1"); Need(!result.ok && result.issues.Any(x => x.code == "Q_CONNECTION_INVALID"), "foreign bracket accepted");
        });
        Check("end tolerance candidates remain geometric and nonnegative free lengths", delegate {
            var r = Fixture(); var m = r.connection_passports[0].members[0];
            m.supports[0].offset_mm = -0.5; m.supports[1].offset_mm = 3000.5;
            m.intervals_mm[0] = 3001; m.bottom_free_mm = m.top_free_mm = 0;
            Need(Build(r).ok, "legacy half-millimetre candidate tolerance changed");
        });
        Check("zero supports preserves whole length without inventing support or span", delegate {
            var r = Fixture(); var p = r.connection_passports[0]; var m = p.members[0];
            m.supports.Clear(); m.intervals_mm.Clear(); m.support_count = m.span_count = 0;
            m.bottom_free_mm = m.top_free_mm = 3000; p.summary.support_positions = p.summary.support_links = 0;
            Need(Build(r).ok, "unsupported member inventory rejected");
        });
        Check("clamps-only retains full passport IDs without nested copy or geometry", delegate {
            var old = Fixture(); var into = new List<QuantityConnectionPassport>();
            FrameQuantities.AppendRetainedConnections(old, new HashSet<string> { "run:rails:0", "run:brackets:0", "run:brackets:1" }, into);
            Need(into.Count == 1 && ReferenceEquals(into[0], old.connection_passports[0]), "passport unnecessarily reconstructed");
            var after = Clone(old); after.connection_passports = into; var second = new List<QuantityConnectionPassport>();
            FrameQuantities.AppendRetainedConnections(after, new HashSet<string> { "run:rails:0", "run:brackets:0", "run:brackets:1" }, second);
            Need(Json.Serialize(into) == Json.Serialize(second), "repeated clamp update changes IDs or grows chain");
        });
        Check("clamps-only incomplete and legacy source return explicit unavailable", delegate {
            var old = Fixture(); var into = new List<QuantityConnectionPassport>();
            FrameQuantities.AppendRetainedConnections(old, new HashSet<string> { "run:rails:0", "run:brackets:0" }, into);
            Need(into.Single().status == "unavailable" && into[0].members.Count == 0 && !string.IsNullOrEmpty(into[0].reason), "partial links represented as complete");
            old.connection_passports = null; into.Clear(); FrameQuantities.AppendRetainedConnections(old, new HashSet<string> { "run:rails:0" }, into);
            Need(into.Single().status == "unavailable" && !string.IsNullOrEmpty(into[0].reason), "legacy null became empty verified inventory");
        });
        Check("empty retained set produces no stale connection references", delegate {
            var into = new List<QuantityConnectionPassport>(); FrameQuantities.AppendRetainedConnections(Fixture(), new HashSet<string>(), into);
            Need(into.Count == 0, "deleted frame retained");
        });
        var performance = new List<object>();
        foreach (int count in new[] { 1000, 3000 }) {
            int countLocal = count;
            Check("connection inventory linear validation/roundtrip " + count, delegate {
                var r = Fixture(); var p = r.connection_passports[0]; r.elements.Clear(); p.members.Clear();
                for (int i = 0; i < countLocal; i++) {
                    string rail = "run:rails:" + i, b1 = "run:brackets:" + (2 * i), b2 = "run:brackets:" + (2 * i + 1);
                    r.elements.Add(Part(rail, "rail", "Z1", 3000)); r.elements.Add(Part(b1, "bracket")); r.elements.Add(Part(b2, "bracket"));
                    p.members.Add(new QuantityConnectionMember { rail_element_id = rail, zone_id = "Z1", support_count = 2, span_count = 1,
                        bottom_free_mm = 200, top_free_mm = 800, intervals_mm = new List<double> { 2000 },
                        supports = new List<QuantityConnectionSupport> {
                            new QuantityConnectionSupport { offset_mm = 200, bracket_element_ids = new List<string> { b1 } },
                            new QuantityConnectionSupport { offset_mm = 2200, bracket_element_ids = new List<string> { b2 } } },
                        profile_match = new QuantityConnectionProfileMatch { status = "matched_source_identity", reference_ids = new List<string> { "reference" } } });
                }
                p.summary = new QuantityConnectionSummary { members = countLocal, support_positions = countLocal * 2, support_links = countLocal * 2, matched_profile_references = countLocal };
                var counters = new QuantityDiagnostics(); var watch = Stopwatch.StartNew();
                var result = FacadeQuantitiesCore.BuildRows(new[] { r }, null, true, false, counters); watch.Stop();
                double validationMs = watch.Elapsed.TotalMilliseconds;
                Need(result.ok && counters.connection_elements_indexed == countLocal * 3 &&
                    counters.connection_members_validated == countLocal && counters.connection_supports_validated == countLocal * 2 &&
                    counters.connection_links_validated == countLocal * 2, "nonlinear inventory validation or missing links");
                watch.Restart(); string json = Json.Serialize(r); var roundtrip = Json.Deserialize<QuantityReport>(json); watch.Stop();
                Need(Build(roundtrip).ok, "large roundtrip invalid");
                performance.Add(new { members = countLocal, physical_elements = countLocal * 3, validation_ms = validationMs,
                    roundtrip_ms = watch.Elapsed.TotalMilliseconds, utf8_bytes = Encoding.UTF8.GetByteCount(json), counters });
            });
        }
        File.WriteAllText(args[1], Json.Serialize(new { status = Failed == 0 ? "PASS" : "FAIL", checks = Cases.Count, cases = Cases, performance, live_autocad_checked = false }));
        Console.WriteLine("Connection store pure checks: " + Cases.Count + (Failed == 0 ? " PASS" : " FAIL"));
        return Failed == 0 ? 0 : 1;
    }
}
