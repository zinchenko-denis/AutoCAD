// Independent contract checks against the production pure core. No AutoCAD host or live DWG.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;

namespace FrameQuantitiesChecks
{
    internal static class FrameCoreProbe
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly List<object> Cases = new List<object>();
        private static int Failed;

        private static void Check(string name, Action check)
        {
            try { check(); Cases.Add(new { name = name, status = "PASS" }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { Failed++; Cases.Add(new { name = name, status = "FAIL", reason = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        private static void Need(bool value, string reason) { if (!value) throw new Exception(reason); }
        private static void Near(double actual, double expected, string reason)
        { Need(Math.Abs(actual - expected) <= 1e-10 * Math.Max(1, Math.Abs(expected)), reason + ": " + actual + " != " + expected); }
        private static T Clone<T>(T value) { return Json.Deserialize<T>(Json.Serialize(value)); }
        private static QuantityElement Part(string id, string role, string zone = "Z1", double? length = null)
        {
            return new QuantityElement { element_id = id, role = role, zone_ids = new List<string> { zone },
                product_id = "CAT-01", mark = "M1", type = "T1", material = "steel", coating = "zinc", system = "SYS-01",
                orientation = "vertical", origin = "generated:frame-fixture", length_mm = length,
                cad_entities = new List<QuantityCadEntity> { new QuantityCadEntity { handle = "H" + id, role = "primary", fingerprint = "fp:" + id } } };
        }
        private static QuantityElement Segment(string id, string role, double x1, double y1, double x2, double y2)
        {
            // A fixture's endpoint-to-length conversion is intentionally independent of production grouping.
            return Part(id, role, "Z1", Math.Sqrt((x2-x1)*(x2-x1) + (y2-y1)*(y2-y1)));
        }
        private static QuantityReport Report(params QuantityElement[] parts)
        {
            var zones = parts.SelectMany(x => x.zone_ids).Distinct().ToList();
            if (zones.Count == 0) zones.Add("Z1");
            return new QuantityReport { kind = "frame", report_id = "frame-r1", run_id = "frame-run1", document_id = "doc1",
                algorithm = "frame-fixture/1", completeness = "complete", zone_ids = zones, elements = parts.ToList() };
        }
        private static QuantityResult Build(QuantityReport report, string[] selected = null, bool byZone = true, bool extras = false)
        { return FacadeQuantitiesCore.BuildRows(new[] { report }, selected, byZone, extras); }
        private static void Refused(QuantityResult result, string code)
        { Need(!result.ok && result.rows.Count == 0 && result.issues.Any(x => x.code == code && x.severity == "error"), "expected empty typed refusal " + code + "; " + Json.Serialize(result.issues)); }
        private static QuantityEstimateGroup Estimate(string id, params string[] zones)
        {
            return new QuantityEstimateGroup { group_id = id, scope_zone_ids = zones.ToList(),
                parameters = new Dictionary<string, object> { { "spacing_mm", 600.0 }, { "formula", "fixture-only" } },
                rows = new List<QuantityRow> { new QuantityRow { basis = "estimate", role = "rail_stock_est", type = "stock",
                    product_id = "EST-STOCK", mark = "ES", system = "SYS-01", material = "steel", coating = "zinc", quantity = 90, unit = "шт." } } };
        }
        private static QuantityReport Golden()
        {
            var parts = new List<QuantityElement> {
                Segment("rail1", "rail", 0, 0, 3000, 0), Segment("rail2", "rail", 100, 200, 100, 3200),
                Segment("hrail1", "hrail", 0, 0, 1500, 2000), Segment("shina1", "shina", 0, 0, 720, 960) };
            for (int i = 0; i < 4; i++) parts.Add(Part("bracket" + i, "bracket"));
            for (int i = 0; i < 6; i++) parts.Add(Part("clamp" + i, "clamp"));
            for (int i = 0; i < 2; i++) parts.Add(Part("fitting" + i, "fitting"));
            return Report(parts.ToArray());
        }

        public static int Main(string[] args)
        {
            Check("six physical roles: independent endpoint lengths, counts and no invented cladding geometry", delegate {
                var result = Build(Golden());
                Need(result.ok && result.completeness == "complete" && result.rows.Count == 6, Json.Serialize(result.issues));
                var counts = new Dictionary<string, double> { { "rail", 2 }, { "hrail", 1 }, { "shina", 1 }, { "bracket", 4 }, { "clamp", 6 }, { "fitting", 2 } };
                foreach (var row in result.rows) {
                    Near(row.quantity, counts[row.role], row.role + " count");
                    Need(row.unit == "шт." && row.basis == "installed", "physical pieces must stay pieces");
                    Need(row.area_m2 == null && row.shape_id == null && row.width_mm == null && row.height_mm == null, "invented cladding geometry");
                }
                Near(result.rows.Single(x => x.role == "rail").total_length_m.Value, 6, "rail metres");
                Near(result.rows.Single(x => x.role == "hrail").total_length_m.Value, 2.5, "hrail metres");
                Near(result.rows.Single(x => x.role == "shina").total_length_m.Value, 1.2, "shina metres");
                Near(result.rows.Sum(x => x.quantity), 16, "physical total");
                Near(result.rows.Sum(x => x.total_length_m ?? 0), 9.7, "actual installed metres");
                Need(result.rows.Where(x => !new[] { "rail", "hrail", "shina" }.Contains(x.role)).All(x => x.total_length_m == null), "point parts fabricated metres");
            });
            Check("helpers belong to one physical part and every link is checked", delegate {
                var part = Part("p1", "bracket");
                part.cad_entities.Add(new QuantityCadEntity { handle = "HELPER1", role = "label", fingerprint = "helper1" });
                part.cad_entities.Add(new QuantityCadEntity { handle = "HELPER2", role = "warning", fingerprint = "helper2" });
                var diagnostics = new QuantityDiagnostics();
                var result = FacadeQuantitiesCore.BuildRows(new[] { Report(part) }, null, true, false, diagnostics);
                Need(result.ok && result.rows.Single().quantity == 1 && diagnostics.cad_links_checked == 3 && diagnostics.elements_validated == 1, "helpers counted or skipped validation");
            });
            Check("all catalog and geometric grouping dimensions remain distinct", delegate {
                var parts = Enumerable.Range(0, 10).Select(i => Part("p" + i, "rail", "Z1", 3000)).ToArray();
                parts[1].role = "hrail"; parts[2].type = "T2"; parts[3].mark = "M2"; parts[4].product_id = "CAT-02";
                parts[5].system = "SYS-02"; parts[6].material = "aluminium"; parts[7].coating = "powder";
                parts[8].length_mm = 2999.999999; parts[9].orientation = "horizontal";
                var result = Build(Report(parts));
                Need(result.ok && result.rows.Count == 10 && result.rows.All(x => x.quantity == 1), "distinct attributes merged");
            });
            Check("length is actual installed length without rounding or stock substitution", delegate {
                var report = Report(Part("p1", "rail", "Z1", 3000.123456789), Part("p2", "rail", "Z1", 3000.123456789));
                report.parameters["stock_length_mm"] = 6000;
                var result = Build(Clone(report));
                Need(result.ok && result.rows.Count == 1, "same exact length failed to group");
                Near(result.rows[0].length_mm.Value, 3000.123456789, "actual length rounded");
                Near(result.rows[0].total_length_m.Value, 6.000246913578, "stock or rounding replaced actual sum");
            });
            Check("missing linear length stays null and partial", delegate {
                foreach (string role in new[] { "rail", "hrail", "shina" }) {
                    var result = Build(Report(Part("unknown", role)));
                    Need(result.ok && result.completeness == "partial" && result.rows.Single().quantity == 1 && result.rows[0].length_mm == null && result.rows[0].total_length_m == null && result.issues.Any(x => x.code == "Q_VALUE_UNKNOWN"), role + " missing length became zero or complete");
                }
            });
            Check("known and unknown lengths cannot silently produce a complete total", delegate {
                var result = Build(Report(Part("known", "rail", "Z1", 3000), Part("unknown", "rail")));
                Need(result.ok && result.completeness == "partial" && result.rows.Count == 2 && result.rows.Sum(x => x.quantity) == 2, "unknown lost");
                Need(result.rows.Single(x => !x.length_mm.HasValue).total_length_m == null, "unknown length replaced by zero");
                Near(result.rows.Single(x => x.length_mm.HasValue).total_length_m.Value, 3, "known actual length lost");
            });
            Check("missing catalog properties remain explicit and partial", delegate {
                var part = Part("p1", "rail", "Z1", 3000);
                part.mark = null; part.product_id = null; part.material = null; part.coating = null; part.system = null;
                var result = Build(Report(part)); var row = result.rows.Single();
                Need(result.ok && result.completeness == "partial" && row.mark == null && row.product_id == null && row.material == null && row.coating == null && row.system == null && result.issues.Any(x => x.code == "Q_VALUE_UNKNOWN"), "unknown catalog value invented");
            });
            Check("unknown warnings aggregate with all physical source identities", delegate {
                var a = Part("a", "clamp"); var b = Part("b", "clamp"); a.mark = b.mark = null;
                var result = Build(Report(a, b)); var warnings = result.issues.Where(x => x.code == "Q_VALUE_UNKNOWN").ToList();
                Need(result.ok && warnings.Count == 1 && warnings[0].element_count == 2 && warnings[0].element_ids.OrderBy(x => x).SequenceEqual(new[] { "a", "b" }), "unknown provenance lost");
            });
            Check("nonpositive and nonfinite physical lengths refuse", delegate {
                foreach (double length in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                    Refused(Build(Report(Part("p1", "rail", "Z1", length))), "Q_MEASURE_INVALID");
            });
            Check("unsupported role refuses instead of becoming a generic piece", delegate {
                Refused(Build(Report(Part("p1", "unknown-frame-role"))), "Q_FRAME_ROLE_INVALID");
            });
            Check("frame passports reject invented cladding shape and area", delegate {
                var part = Part("p1", "rail", "Z1", 3000); part.area_mm2 = 600000;
                Refused(Build(Report(part)), "Q_FRAME_GEOMETRY_INVALID");
                part = Part("p1", "rail", "Z1", 3000); part.shape_id = "fabricated-shape";
                Refused(Build(Report(part)), "Q_FRAME_GEOMETRY_INVALID");
                part = Part("p1", "rail", "Z1", 3000); part.rings.Add(new QuantityRing { points = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 } } });
                Refused(Build(Report(part)), "Q_FRAME_GEOMETRY_INVALID");
            });
            Check("same position or equal dimensions with different physical identities count twice", delegate {
                var result = Build(Report(Part("p1", "rail", "Z1", 3000), Part("p2", "rail", "Z1", 3000)));
                Need(result.ok && result.rows.Count == 1 && result.rows[0].quantity == 2 && result.rows[0].element_ids.SequenceEqual(new[] { "p1", "p2" }), "overlap deduplicated real parts");
            });
            Check("identical physical and report duplicates count once", delegate {
                var part = Part("p1", "rail", "Z1", 3000); var report = Report(part, Clone(part));
                var result = FacadeQuantitiesCore.BuildRows(new[] { report, Clone(report) }, new[] { "Z1", "Z1" }, true, false);
                Need(result.ok && result.rows.Single().quantity == 1 && result.rows[0].element_ids.Count == 1, "duplicate physical quantity");
            });
            Check("reused physical identity with changed length coating or system refuses", delegate {
                foreach (string change in new[] { "length", "coating", "system" }) {
                    var part = Part("p1", "rail", "Z1", 3000); var other = Clone(part);
                    if (change == "length") other.length_mm = 2500; if (change == "coating") other.coating = "changed"; if (change == "system") other.system = "changed";
                    Refused(Build(Report(part, other)), "Q_ELEMENT_CONFLICT");
                }
            });
            Check("reused report identity detects changed fingerprint and source revision", delegate {
                foreach (string change in new[] { "fingerprint", "revision", "length", "estimate" }) {
                    var report = Report(Part("p1", "rail", "Z1", 3000)); report.source_revisions["Z1"] = "revision1"; report.estimates.Add(Estimate("EG", "Z1"));
                    var other = Clone(report);
                    if (change == "fingerprint") other.elements[0].cad_entities[0].fingerprint = "stale";
                    if (change == "revision") other.source_revisions["Z1"] = "revision2";
                    if (change == "length") other.elements[0].length_mm = 2500;
                    if (change == "estimate") other.estimates[0].rows[0].quantity = 91;
                    Refused(FacadeQuantitiesCore.BuildRows(new[] { report, other }, null, true, false), "Q_REPORT_CONFLICT");
                }
            });
            Check("same CAD handle is rejected case insensitively", delegate {
                var a = Part("a", "bracket"); var b = Part("b", "clamp"); b.cad_entities[0].handle = a.cad_entities[0].handle.ToLowerInvariant();
                Refused(Build(Report(a, b)), "Q_CAD_LINK_DUPLICATE");
            });
            Check("missing and malformed CAD links refuse", delegate {
                var part = Part("p1", "bracket"); part.cad_entities.Clear(); Refused(Build(Report(part)), "Q_CAD_LINK_MISSING");
                part = Part("p1", "bracket"); part.cad_entities[0].fingerprint = null; Refused(Build(Report(part)), "Q_CAD_LINK_INVALID");
            });
            Check("unselected physical members and their CAD links are still validated", delegate {
                var a = Part("a", "rail", "Z1", 3000); var b = Part("b", "rail", "Z2", 0);
                Refused(Build(Report(a, b), new[] { "Z1" }), "Q_MEASURE_INVALID");
                b.length_mm = 3000; b.cad_entities[0].fingerprint = null;
                Refused(Build(Report(a, b), new[] { "Z1" }), "Q_CAD_LINK_INVALID");
            });
            Check("zone selection and whole-selection grouping preserve exact provenance", delegate {
                var report = Report(Part("a", "rail", "Z1", 3000), Part("b", "rail", "Z2", 3000));
                var byZone = Build(report); var together = Build(report, null, false); var subset = Build(report, new[] { "Z1" });
                Need(byZone.ok && byZone.rows.Count == 2 && together.ok && together.rows.Count == 1 && together.rows[0].zone_ids.SequenceEqual(new[] { "Z1", "Z2" }), "grouped zone provenance lost");
                Need(subset.ok && subset.rows.Single().element_ids.SequenceEqual(new[] { "a" }) && subset.rows[0].quantity == 1, "unselected part leaked");
                Near(together.rows[0].total_length_m.Value, 6, "whole selection length");
            });
            Check("shared physical part needs its whole scope", delegate {
                var part = Part("a", "rail", "Z1", 3000); part.zone_ids.Add("Z2");
                Refused(Build(Report(part), new[] { "Z1" }), "Q_SHARED_PIECE_PARTIAL");
                var full = Build(Report(part)); Need(full.ok && full.rows.Single().quantity == 1, "shared part double counted");
            });
            Check("zone display separators cannot collide", delegate {
                var a = Part("a", "bracket", "A + B"); var b = Part("b", "bracket", "A"); b.zone_ids.Add("B");
                var result = Build(Report(a, b)); Need(result.ok && result.rows.Count == 2, "one named zone merged with two zones");
            });
            Check("estimates remain separate from installed counts and stock lengths", delegate {
                var report = Report(Part("p1", "fitting"), Part("r1", "rail", "Z1", 3000)); report.estimates.Add(Estimate("EG", "Z1"));
                report.estimates[0].rows.Add(new QuantityRow { basis = "estimate", role = "rail_stock_est", type = "stock", unit = "шт.", quantity = 5, length_mm = 6000, total_length_m = 30 });
                var result = Build(report, null, true, true);
                Need(result.ok && result.rows.Where(x => x.basis == "installed").Sum(x => x.quantity) == 2 && result.rows.Where(x => x.basis == "estimate").Sum(x => x.quantity) == 95, "estimate mixed into installed pieces");
                Near(result.rows.Where(x => x.basis == "installed").Sum(x => x.total_length_m ?? 0), 3, "estimate stock length mixed into actual metres");
                Need(result.rows.Where(x => x.basis == "estimate").All(x => x.zone_ids.SequenceEqual(new[] { "Z1" })), "estimate scope missing");
            });
            Check("partial estimate group is omitted without blocking installed subset", delegate {
                var report = Report(Part("a", "bracket", "Z1"), Part("b", "bracket", "Z2")); report.estimates.Add(Estimate("EG", "Z1", "Z2"));
                var subset = Build(report, new[] { "Z1" }, true, true);
                Need(subset.ok && subset.rows.Count == 1 && subset.rows[0].basis == "installed" && subset.rows[0].quantity == 1, "partial estimate blocked installed or was prorated");
                Need(subset.issues.Any(x => x.code == "Q_ESTIMATE_SCOPE_PARTIAL" && x.severity == "info"), "partial estimate omission is not explicit");
                var full = Build(report, null, true, true);
                Need(full.ok && full.rows.Single(x => x.basis == "estimate").quantity == 90, "full estimate group changed");
            });
            Check("frame estimate inclusion is independent of cladding cutting toggle", delegate {
                var report = Report(Part("p1", "bracket")); report.estimates.Add(Estimate("EG", "Z1"));
                var withoutCutting = Build(report, null, true, false); var withCutting = Build(report, null, true, true);
                Need(withoutCutting.ok && withCutting.ok && withoutCutting.rows.Count(x => x.basis == "estimate") == 1 && Json.Serialize(withoutCutting.rows) == Json.Serialize(withCutting.rows), "cladding cutting flag incorrectly controls frame estimates");
            });
            Check("unselected estimate groups are omitted and separate complete groups survive", delegate {
                var report = Report(Part("a", "bracket", "Z1"), Part("b", "bracket", "Z2"));
                report.estimates.Add(Estimate("EG1", "Z1")); report.estimates.Add(Estimate("EG2", "Z2"));
                var result = Build(report, new[] { "Z2" }, true, true);
                Need(result.ok && result.rows.Count(x => x.basis == "estimate") == 1 && result.rows.Single(x => x.basis == "estimate").zone_ids.SequenceEqual(new[] { "Z2" }), "unselected estimate leaked");
            });
            Check("estimate numeric JSON roundtrip preserves duplicate identity", delegate {
                var report = Report(Part("p1", "fitting")); report.estimates.Add(Estimate("EG", "Z1")); report.parameters["step"] = 600.0;
                var result = FacadeQuantitiesCore.BuildRows(new[] { report, Clone(report) }, null, true, true);
                Need(result.ok && result.rows.Count(x => x.basis == "installed") == 1 && result.rows.Count(x => x.basis == "estimate") == 1, "estimate roundtrip changes identity or duplicates rows");
            });
            Check("invalid estimate rows and invalid group scopes refuse", delegate {
                var report = Report(Part("p1", "bracket")); report.estimates.Add(Estimate("EG", "Z1")); report.estimates[0].rows[0].quantity = double.NaN;
                Refused(Build(report, null, true, true), "Q_ESTIMATE_ROW_INVALID");
                report = Report(Part("p1", "bracket")); report.estimates.Add(Estimate("EG", "absent"));
                Refused(Build(report, null, true, true), "Q_ESTIMATE_GROUP_INVALID");
            });
            Check("stock estimate contract rejects unsupported fitting procurement", delegate {
                var report = Report(Part("p1", "bracket")); report.estimates.Add(Estimate("EG", "Z1")); report.estimates[0].rows[0].role = "fitting";
                Refused(Build(report), "Q_ESTIMATE_ROW_INVALID");
            });
            Check("unselected estimate data is validated before selection", delegate {
                var report = Report(Part("a", "bracket", "Z1"), Part("b", "bracket", "Z2"));
                report.estimates.Add(Estimate("EG", "Z2")); report.estimates[0].rows[0].quantity = -1;
                Refused(Build(report, new[] { "Z1" }), "Q_ESTIMATE_ROW_INVALID");
            });
            Check("source engineering limitations are retained without granting engineering approval", delegate {
                var a = Report(Part("a", "rail", "Z1", 3000)); a.engineering_coverage = "limited_static_chain";
                var b = Report(Part("b", "bracket", "Z2")); b.report_id = "frame-r2"; b.run_id = "frame-run2"; b.engineering_coverage = "manual";
                var result = FacadeQuantitiesCore.BuildRows(new[] { a, b }, null, true, false);
                Need(result.ok && result.engineering_coverage == "geometry_only" && new HashSet<string>(result.source_engineering_coverage).SetEquals(new[] { "limited_static_chain", "manual" }), "source engineering limit lost or promoted");
            });
            Check("source partial status and explicit source issues are never promoted", delegate {
                var report = Report(Part("p1", "bracket")); report.completeness = "partial";
                Need(Build(report).ok && Build(report).completeness == "partial", "source partial promoted");
                report.completeness = "complete"; report.issues.Add(new QuantityIssue { code = "FRAME_STATIC_NOT_CHECKED", message = "fixture warning", severity = "warning" });
                var result = Build(report); Need(result.ok && result.completeness == "partial" && result.issues.Any(x => x.code == "FRAME_STATIC_NOT_CHECKED"), "source issue lost");
            });
            Check("verified zero is an empty installed result with explicit information", delegate {
                var result = Build(Report());
                Need(result.ok && result.rows.Count == 0 && result.issues.Any(x => x.code == "Q_ZERO_INSTALLED_ELEMENTS" && x.severity == "info"), "verified zero invented a part or silently disappeared");
            });
            Check("bad schema document scope and selection refuse", delegate {
                var report = Report(Part("p1", "bracket")); report.schema = "facade_quantities/99"; Refused(Build(report), "Q_REPORT_INVALID");
                report = Report(Part("p1", "bracket")); Refused(Build(report, new[] { "absent" }), "Q_ZONE_NOT_FOUND");
                report.zone_ids.Clear(); Refused(Build(report), "Q_SCOPE_EMPTY");
                var a = Report(Part("a", "bracket")); var b = Report(Part("b", "bracket")); b.report_id = "r2"; b.document_id = "other";
                Refused(FacadeQuantitiesCore.BuildRows(new[] { a, b }, null, true, false), "Q_DOCUMENT_MISMATCH");
            });
            Check("frame and cladding reports cannot be silently mixed", delegate {
                var frame = Report(Part("a", "bracket")); var cladding = new QuantityReport { report_id = "clad-r", run_id = "clad-run", document_id = "doc1", algorithm = "fixture/1", zone_ids = new List<string> { "Z1" } };
                Refused(FacadeQuantitiesCore.BuildRows(new[] { frame, cladding }, null, true, false), "Q_KIND_MISMATCH");
            });
            Check("rows are deterministic and aggregation does not mutate passports", delegate {
                var report = Golden(); report.estimates.Add(Estimate("EG", "Z1"));
                string before = Json.Serialize(report); var first = Build(report, null, false, true);
                Need(before == Json.Serialize(report), "input passport mutated");
                report.elements.Reverse(); report.estimates[0].rows.Reverse(); var second = Build(report, null, false, true);
                Need(first.ok && second.ok && Json.Serialize(first.rows) == Json.Serialize(second.rows), "row order or totals depend on traversal");
            });
            Check("estimate row order remains deterministic when only total length differs", delegate {
                var report = Report(Part("p1", "bracket")); report.estimates.Add(Estimate("EG", "Z1"));
                var firstRow = report.estimates[0].rows[0]; firstRow.total_length_m = 500;
                var secondRow = Clone(firstRow); secondRow.total_length_m = 501; report.estimates[0].rows.Add(secondRow);
                var first = Build(report); report.estimates[0].rows.Reverse(); var second = Build(report);
                Need(first.ok && second.ok && Json.Serialize(first.rows) == Json.Serialize(second.rows), "estimate total length omitted from deterministic row order");
            });
            Check("unique identities require no canonical duplicate keys", delegate {
                var report = Golden(); var diagnostics = new QuantityDiagnostics();
                var result = FacadeQuantitiesCore.BuildRows(new[] { report }, null, true, true, diagnostics);
                Need(result.ok && diagnostics.reports_seen == 1 && diagnostics.elements_seen == 16 && diagnostics.elements_validated == 16 && diagnostics.cad_links_checked == 16, "physical validation coverage is incomplete");
                Need(diagnostics.reports_compared == 0 && diagnostics.elements_compared == 0 && diagnostics.canonical_report_keys == 0 && diagnostics.canonical_element_keys == 0 && diagnostics.shapes_validated == 0 && diagnostics.aggregate_keys_built == 16, "unique path does unnecessary duplicate or cladding geometry work");
            });
            Check("canonical identity work is triggered by a repeated identity only", delegate {
                var report = Golden(); report.elements.Add(Clone(report.elements[0])); var diagnostics = new QuantityDiagnostics();
                var result = FacadeQuantitiesCore.BuildRows(new[] { report }, null, true, false, diagnostics);
                Need(result.ok && result.rows.Sum(x => x.quantity) == 16 && diagnostics.elements_seen == 17 && diagnostics.elements_validated == 16 && diagnostics.cad_links_checked == 16, "duplicate changes physical validation count");
                Need(diagnostics.elements_compared == 1 && diagnostics.canonical_element_keys == 2 && diagnostics.canonical_report_keys == 0, "duplicate identity keys computed outside comparison");
            });

            string path = null;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--report") path = args[i + 1];
            var summary = new { total = Cases.Count, passed = Cases.Count - Failed, failed = Failed,
                scope = "Production pure frame quantity contract; not live CAD freshness, DWG integration, UI or installer.", cases = Cases };
            if (path != null) File.WriteAllText(path, Json.Serialize(summary));
            Console.WriteLine("Frame core: " + (Cases.Count - Failed) + "/" + Cases.Count + " passed");
            return Failed == 0 ? 0 : 1;
        }
    }
}
