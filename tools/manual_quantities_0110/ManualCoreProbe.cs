// Independent manual adapter oracles. Pure C# only: no DWG, transaction or AutoCAD host.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;

namespace ManualQuantitiesChecks
{
    internal static class ManualCoreProbe
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly List<object> Cases = new List<object>();
        private static int Failed;
        private static void Check(string name, Action test)
        {
            try { test(); Cases.Add(new { name = name, status = "PASS" }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { Failed++; Cases.Add(new { name = name, status = "FAIL", reason = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        private static void Need(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        private static void Near(double actual, double expected, string reason)
        { Need(Math.Abs(actual - expected) <= Math.Max(1, Math.Abs(expected)) * 1e-10, reason + ": " + actual + " != " + expected); }
        private static T Clone<T>(T value) { return Json.Deserialize<T>(Json.Serialize(value)); }
        private static ManualQuantityField Literal(string value) { return new ManualQuantityField { source = "literal", value = value }; }
        private static ManualQuantityRule Rule(string adapter = "linear", string role = "rail")
        {
            return new ManualQuantityRule { rule_id = "rule-01", revision = "1", kind = adapter == "boundary" ? "cladding" : "frame",
                role = role, adapter = adapter, sample_key = "sample-01", geometry_unit = "mm", one_physical_part = true, axis = "X",
                fields = new Dictionary<string, ManualQuantityField> {
                    { "mark", Literal("M-001") }, { "type", Literal("T-01") }, { "material", Literal("steel") },
                    { "coating", Literal("zinc") }, { "system", Literal("SYS-01") }, { "color", Literal("white") },
                    { "orientation", Literal("front") }, { "piece_kind", Literal("full") } } };
        }
        private static ManualQuantityObservation Line(string handle = "A1", double x1 = 100, double y1 = 200, double x2 = 1600, double y2 = 2200)
        {
            return new ManualQuantityObservation { handle = handle, entity_type = "Line", sample_key = "sample-01", fingerprint = "fp:" + handle, cad_fingerprint = "cad:" + handle,
                line_start = new[] { x1, y1 }, line_end = new[] { x2, y2 } };
        }
        private static ManualQuantityObservation Boundary(string handle = "A1", double dx = 0, double dy = 0)
        {
            return new ManualQuantityObservation { handle = handle, entity_type = "Polyline", sample_key = "sample-01", fingerprint = "fp:" + handle, cad_fingerprint = "cad:" + handle,
                boundary_points = new[] { new[] { 100.0 + dx, 200.0 + dy }, new[] { 700.0 + dx, 200.0 + dy },
                    new[] { 700.0 + dx, 500.0 + dy }, new[] { 100.0 + dx, 500.0 + dy } } };
        }
        private static ManualQuantityObservation Symbol(string handle = "A1", double x = 100, double y = 200)
        {
            return new ManualQuantityObservation { handle = handle, entity_type = "BlockReference", is_block = true,
                effective_block_name = "BracketSample", sample_key = "sample-01", fingerprint = "fp:" + handle, cad_fingerprint = "cad:" + handle,
                position = new[] { x, y }, rotation = 0, scale_x = 1, scale_y = 1, scale_z = 1 };
        }
        private static ManualQuantityObservation LinearBlock(string handle = "A1")
        {
            var obs = Symbol(handle);
            obs.axis_x_start = new[] { 100.0, 200.0 }; obs.axis_x_end = new[] { 1600.0, 2200.0 };
            obs.axis_y_start = new[] { 100.0, 200.0 }; obs.axis_y_end = new[] { 100.0, 1400.0 };
            return obs;
        }
        private static QuantityElement Accepted(ManualQuantityRule rule, ManualQuantityObservation obs, string zone = "Z1")
        {
            var evaluation = ManualQuantitiesCore.Evaluate(rule, obs, zone);
            Need(evaluation.status == "accepted" && evaluation.element != null, "expected accepted: " + Json.Serialize(evaluation));
            return evaluation.element;
        }
        private static void Rejected(ManualQuantityRule rule, ManualQuantityObservation obs, string zone = "Z1")
        {
            var evaluation = ManualQuantitiesCore.Evaluate(rule, obs, zone);
            Need(evaluation.status == "rejected" && evaluation.element == null && !string.IsNullOrWhiteSpace(evaluation.reason), "expected explicit rejection: " + Json.Serialize(evaluation));
        }
        private static QuantityReport Report(string kind, params QuantityElement[] elements)
        {
            return new QuantityReport { kind = kind, report_id = "manual-report", run_id = "manual-run", document_id = "doc1", algorithm = "manual-fixture/1",
                completeness = "partial", zone_ids = new List<string> { "Z1" }, elements = elements.ToList() };
        }

        public static int Main(string[] args)
        {
            Check("actual line endpoints give independent 3-4-5 length and one physical CAD link", delegate {
                var element = Accepted(Rule(), Line());
                Near(element.length_mm.Value, 2500, "endpoint length");
                Need(element.area_mm2 == null && element.shape_id == null && element.rings.Count == 0, "line invented plate geometry");
                Need(element.cad_entities.Count == 1 && element.cad_entities[0].handle == "A1" && element.cad_entities[0].fingerprint == "cad:A1", "physical CAD provenance lost");
                Need(element.product_id == null && !string.IsNullOrWhiteSpace(element.identity_group) && element.origin.Contains("manual"), "catalog invented or manual provenance lost");
            });
            Check("boundary uses world polygon area and dimensions", delegate {
                var element = Accepted(Rule("boundary", "cladding"), Boundary());
                Near(element.width_mm.Value, 600, "world width"); Near(element.height_mm.Value, 300, "world height"); Near(element.area_mm2.Value, 180000, "world area");
                Need(element.rings.Count == 1 && !string.IsNullOrWhiteSpace(element.shape_id) && element.length_mm == null, "boundary representation lost");
            });
            Check("nonrectangular boundary uses exact area not bounding rectangle", delegate {
                var obs = Boundary(); obs.boundary_points = new[] { new[] { 0.0, 0.0 }, new[] { 600.0, 0.0 }, new[] { 600.0, 300.0 } };
                var element = Accepted(Rule("boundary", "cladding"), obs); Near(element.area_mm2.Value, 90000, "triangle area");
                var result = FacadeQuantitiesCore.BuildRows(new[] { Report("cladding", element) }, null, true, false);
                Need(result.ok && result.rows.Single().quantity == 1, "manual boundary rejected by aggregate"); Near(result.rows[0].area_m2.Value, 0.09, "manual area units");
            });
            Check("literal attribute and dynamic strings preserve exact mark text", delegate {
                const string mark = "001-A/02.0300";
                foreach (string source in new[] { "literal", "attribute", "dynamic" }) {
                    var rule = Rule(); rule.fields["mark"] = new ManualQuantityField { source = source, name = "MARK", value = mark };
                    var obs = Line(); obs.attributes["MARK"] = mark; obs.dynamic_properties["MARK"] = mark;
                    Need(Accepted(rule, obs).mark == mark, "mark normalized or rounded for " + source);
                }
            });
            Check("missing optional string property stays null with a visible issue", delegate {
                var rule = Rule(); rule.fields["mark"] = new ManualQuantityField { source = "attribute", name = "MISSING" };
                var evaluation = ManualQuantitiesCore.Evaluate(rule, Line(), "Z1");
                Need(evaluation.status == "accepted" && evaluation.element.mark == null && evaluation.issues.Any(x => x.code == "MQ_FIELD_UNKNOWN"), "missing property invented or hidden");
            });
            Check("present but empty source properties stay unknown and visibly incomplete", delegate {
                foreach (string source in new[] { "attribute", "dynamic" }) foreach (string value in new string[] { null, "", "  " }) {
                    var rule = Rule(); rule.fields["mark"] = new ManualQuantityField { source = source, name = "MARK" }; var obs = Line();
                    obs.attributes["MARK"] = value; obs.dynamic_properties["MARK"] = value;
                    var evaluation = ManualQuantitiesCore.Evaluate(rule, obs, "Z1");
                    Need(evaluation.status == "accepted" && evaluation.element.mark == null && evaluation.issues.Count(x => x.code == "MQ_FIELD_UNKNOWN") == 1, "empty " + source + " treated as known");
                }
            });
            Check("many missing fields aggregate by pattern while retaining each physical source", delegate {
                var rule = Rule(); foreach (string key in rule.fields.Keys.ToArray()) rule.fields[key] = new ManualQuantityField { source = "attribute", name = key };
                var preview = ManualQuantitiesCore.BuildPreview(rule, new[] { Line("A1", 0, 0, 2500, 0), Line("A2", 0, 10, 2500, 10) }, "Z1");
                var warnings = preview.issues.Where(x => x.code == "MQ_FIELD_UNKNOWN").ToList();
                Need(preview.accepted_count == 2 && preview.items.All(x => x.issues.Count(y => y.code == "MQ_FIELD_UNKNOWN") == 1), "missing fields multiplied per-object warnings");
                Need(warnings.Count == 1 && warnings[0].element_count == 2 && warnings[0].element_ids.OrderBy(x => x).SequenceEqual(new[] { "manual:A1", "manual:A2" }), "aggregated unknown pattern loses physical provenance");
            });
            Check("all configured string mappings reach physical element", delegate {
                var rule = Rule(); var obs = Line();
                foreach (string key in rule.fields.Keys.ToArray()) { rule.fields[key] = new ManualQuantityField { source = "attribute", name = key }; obs.attributes[key] = "value:" + key; }
                var e = Accepted(rule, obs);
                Need(e.mark == "value:mark" && e.type == "value:type" && e.material == "value:material" && e.coating == "value:coating" && e.system == "value:system" && e.color == "value:color" && e.orientation == "value:orientation" && e.piece_kind == "value:piece_kind", "one mapped string field was discarded");
            });
            Check("linear roles all derive actual length independently", delegate {
                foreach (string role in new[] { "rail", "hrail", "shina" }) {
                    var e = Accepted(Rule("linear", role), Line()); Need(e.role == role, "role changed"); Near(e.length_mm.Value, 2500, role + " length");
                }
            });
            Check("symbol requires explicit one physical part and yields no fabricated measures", delegate {
                foreach (string role in new[] { "bracket", "clamp", "fitting" }) {
                    var e = Accepted(Rule("symbol", role), Symbol());
                    Need(e.role == role && e.length_mm == null && e.area_mm2 == null && e.width_mm == null && e.height_mm == null, "symbol invented measures");
                }
                var rule = Rule("symbol", "bracket"); rule.one_physical_part = false; Rejected(rule, Symbol());
                Rejected(Rule("symbol", "bracket"), Line());
            });
            Check("different explicit sample is unrecognized without guessed classification", delegate {
                var obs = Line(); obs.sample_key = "different-sample";
                var evaluation = ManualQuantitiesCore.Evaluate(Rule(), obs, "Z1");
                Need(evaluation.status == "unrecognized" && evaluation.element == null && !string.IsNullOrWhiteSpace(evaluation.reason), "unmatched sample guessed");
            });
            Check("generated ownership conflict cannot become accepted manual geometry", delegate {
                var obs = Line(); obs.generated_conflict = true;
                var evaluation = ManualQuantitiesCore.Evaluate(Rule(), obs, "Z1");
                Need(evaluation.status == "conflict" && evaluation.element == null, "generated object double counted");
            });
            Check("explicit zone is retained and absent zone is never fabricated", delegate {
                var e = Accepted(Rule(), Line(), "Zone from mapping");
                Need(e.zone_ids.SequenceEqual(new[] { "Zone from mapping" }), "mapped zone changed");
                Rejected(Rule(), Line(), null); Rejected(Rule(), Line(), " ");
            });
            Check("millimetres are explicit and unsupported units rejected", delegate {
                foreach (string unit in new[] { "m", "cm", "inch", "" }) { var rule = Rule(); rule.geometry_unit = unit; Rejected(rule, Line()); }
            });
            Check("line numeric verification preserves precision and rejects mismatched declared length", delegate {
                var rule = Rule(); rule.fields["length"] = Literal("3000.123456789");
                var e = Accepted(rule, Line("A1", 0, 0, 3000.123456789, 0)); Near(e.length_mm.Value, 3000.123456789, "length precision");
                rule.fields["length"] = Literal("2500"); Rejected(rule, Line("A1", 0, 0, 3000, 0));
            });
            Check("declared dimensions verify boundary geometry instead of replacing it", delegate {
                var rule = Rule("boundary", "cladding"); rule.fields["width"] = Literal("600"); rule.fields["height"] = Literal("300");
                Near(Accepted(rule, Boundary()).area_mm2.Value, 180000, "verified boundary area");
                rule.fields["width"] = Literal("601"); Rejected(rule, Boundary());
            });
            Check("missing required numeric mapping rejects linear block", delegate {
                var rule = Rule(); Rejected(rule, LinearBlock());
                rule.fields["length"] = new ManualQuantityField { source = "dynamic", name = "LENGTH" }; Rejected(rule, LinearBlock());
            });
            Check("linear block X and Y endpoints have independent numeric oracles", delegate {
                var rule = Rule(); rule.fields["length"] = new ManualQuantityField { source = "dynamic", name = "LENGTH" };
                var obs = LinearBlock(); obs.dynamic_properties["LENGTH"] = 2500.0; obs.dynamic_units["LENGTH"] = "Distance";
                Near(Accepted(rule, obs).length_mm.Value, 2500, "X actual world length");
                rule.axis = "Y"; obs.dynamic_properties["LENGTH"] = 1200.0;
                Near(Accepted(rule, obs).length_mm.Value, 1200, "Y actual world length");
                rule.axis = "Z"; Rejected(rule, obs);
            });
            Check("linear block length checks absolute scale but keeps actual world length", delegate {
                var rule = Rule(); rule.fields["length"] = Literal("1250"); var obs = LinearBlock(); obs.scale_x = 2;
                Near(Accepted(rule, obs).length_mm.Value, 2500, "scaled world length");
                obs.scale_x = -2; Near(Accepted(rule, obs).length_mm.Value, 2500, "mirrored world length");
                obs.scale_x = 1; Rejected(rule, obs);
            });
            Check("invalid numeric mappings and physical geometry reject explicitly", delegate {
                foreach (string value in new[] { "0", "-5", "NaN", "Infinity", "not-a-number" }) { var rule = Rule(); rule.fields["length"] = Literal(value); Rejected(rule, LinearBlock()); }
                Rejected(Rule(), Line("A1", 0, 0, 0, 0)); Rejected(Rule(), Line("A1", 0, 0, double.NaN, 0));
                var boundary = Boundary(); boundary.boundary_points = new[] { new[] { 0.0, 0.0 }, new[] { 100.0, 100.0 }, new[] { 0.0, 100.0 }, new[] { 100.0, 0.0 } };
                Rejected(Rule("boundary", "cladding"), boundary);
            });
            Check("block scales must be finite nonzero and supplied", delegate {
                foreach (double? scale in new double?[] { null, 0, double.NaN, double.PositiveInfinity }) { var obs = Symbol(); obs.scale_x = scale; Rejected(Rule("symbol", "bracket"), obs); }
            });
            Check("capture adapter rejection reasons cannot be bypassed", delegate {
                var boundary = Boundary(); boundary.boundary_reason = "unsupported curved boundary"; Rejected(Rule("boundary", "cladding"), boundary);
                var line = Line(); line.linear_reason = "unsupported linear geometry"; Rejected(Rule(), line);
                var symbol = Symbol(); symbol.symbol_reason = "unsupported symbol"; Rejected(Rule("symbol", "bracket"), symbol);
            });
            Check("explicit unsupported extraction reason survives missing fingerprints", delegate {
                const string reason = "unsupported compound boundary from capture";
                var obs = Boundary(); obs.extraction_reason = reason; obs.fingerprint = null; obs.cad_fingerprint = null;
                var evaluation = ManualQuantitiesCore.Evaluate(Rule("boundary", "cladding"), obs, "Z1");
                Need(evaluation.status == "rejected" && evaluation.element == null && evaluation.reason == reason, "capture failure replaced by generic fingerprint rejection");
            });
            Check("missing physical identity or freshness fingerprint rejects", delegate {
                var obs = Line(); obs.handle = null; Rejected(Rule(), obs);
                obs = Line(); obs.fingerprint = null; Rejected(Rule(), obs);
                obs = Line(); obs.cad_fingerprint = null; Rejected(Rule(), obs);
            });
            Check("invalid rule schema source and role do not produce elements", delegate {
                var rule = Rule(); rule.schema = "facade_manual_rule/99"; Rejected(rule, Line());
                rule = Rule(); rule.fields["mark"] = new ManualQuantityField { source = "guess", value = "M" }; Rejected(rule, Line());
                rule = Rule(); rule.role = "unrecognized-role"; Rejected(rule, Line());
                rule = Rule("symbol", "rail"); Rejected(rule, Symbol());
            });
            Check("different sample or mapping content cannot merge unknown catalog rows", delegate {
                foreach (string change in new[] { "sample", "mapping" }) {
                    var a = Rule(); var b = Clone(a); b.rule_id = "rule-02"; var other = Line("A2");
                    if (change == "sample") { b.sample_key = "sample-02"; other.sample_key = "sample-02"; }
                    else { b.fields["mark"] = new ManualQuantityField { source = "attribute", name = "MARK" }; other.attributes["MARK"] = "M-001"; }
                    var one = Accepted(a, Line("A1")); var two = Accepted(b, other);
                    Need(one.product_id == null && two.product_id == null && one.identity_group != two.identity_group, "manual classification identity lost for " + change);
                    var result = FacadeQuantitiesCore.BuildRows(new[] { Report("frame", one, two) }, null, true, false);
                    Need(result.ok && result.completeness == "partial" && result.rows.Count == 2 && result.rows.Sum(x => x.quantity) == 2, "different unknown " + change + " rows merged");
                }
            });
            Check("recreated identical mapping has same grouping but distinct registry fingerprint", delegate {
                var a = Rule(); var b = Clone(a); b.rule_id = "recreated-guid"; b.revision = "42";
                Need(Accepted(a, Line("A1")).identity_group == Accepted(b, Line("A2")).identity_group, "registry GUID incorrectly changes physical grouping");
                Need(ManualQuantitiesCore.RuleFingerprint(a) != ManualQuantitiesCore.RuleFingerprint(b), "registry identity omitted from freshness fingerprint");
            });
            Check("same rule combines distinct physical instances but never deduplicates geometry", delegate {
                var rule = Rule(); var one = Accepted(rule, Line("A1")); var two = Accepted(rule, Line("A2"));
                Need(one.element_id != two.element_id && one.identity_group == two.identity_group, "instance or classification identity wrong");
                var result = FacadeQuantitiesCore.BuildRows(new[] { Report("frame", one, two) }, null, true, false);
                Need(result.ok && result.rows.Single().quantity == 2, "distinct instances merged into one physical part"); Near(result.rows[0].total_length_m.Value, 5, "actual combined length");
            });
            Check("same CAD object repeated in selection is evaluated and counted once", delegate {
                var obs = Line(); var preview = ManualQuantitiesCore.BuildPreview(Rule(), new[] { obs, Clone(obs) }, "Z1");
                Need(preview.accepted_count == 1 && preview.repeated_count == 1 && preview.items.Count == 1 && preview.diagnostics.observations_seen == 2 && preview.diagnostics.observations_evaluated == 1, "repeated selection counted twice");
                Need(preview.issues.Any(x => x.code == "MQ_SELECTION_REPEATED" && x.severity == "info"), "repeated selection unexplained");
            });
            Check("changed fingerprint for same handle invalidates the previously accepted candidate", delegate {
                var a = Line(); var b = Clone(a); b.fingerprint = "changed";
                var preview = ManualQuantitiesCore.BuildPreview(Rule(), new[] { a, b }, "Z1");
                Need(preview.accepted_count == 0 && preview.rejected_count >= 1 && preview.items.Any(x => x.status == "conflict") && preview.items.All(x => x.element == null), "ambiguous freshness leaves counted candidate");
            });
            Check("same absolute geometry with distinct handles warns but counts both", delegate {
                var preview = ManualQuantitiesCore.BuildPreview(Rule(), new[] { Line("A1"), Line("A2") }, "Z1");
                Need(preview.accepted_count == 2 && preview.repeated_count == 0 && preview.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE" && x.severity == "warning"), "suspect duplicates hidden or silently removed");
            });
            Check("actual LINE and reversed block axis share duplicate geometry but equal centers alone do not", delegate {
                var rule = Rule(); rule.fields["length"] = Literal("2500");
                var line = Line("A1"); var block = LinearBlock("A2");
                block.axis_x_start = new[] { 1600.0, 2200.0 }; block.axis_x_end = new[] { 100.0, 200.0 };
                string lineKey = ManualQuantitiesCore.DuplicateKey("frame", "rail", "linear", "X", line);
                Need(lineKey == ManualQuantitiesCore.DuplicateKey("frame", "rail", "linear", "X", block), "physical endpoints depend on LINE or block-axis representation");
                var preview = ManualQuantitiesCore.BuildPreview(rule, new[] { line, block }, "Z1");
                Need(preview.accepted_count == 2 && preview.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE" && x.element_count == 2), "mixed representation duplicate hidden or deduplicated");
                var longerSameCenter = Line("A3", -650, -800, 2350, 3200);
                Need(lineKey != ManualQuantitiesCore.DuplicateKey("frame", "rail", "linear", "X", longerSameCenter), "only center was compared instead of physical endpoints");
            });
            Check("linear warning buckets absorb transform noise but preserve a real displacement", delegate {
                var line = Line("A1"); var noisy = Line("A2", 100 + 1e-8, 200 + 1e-8, 1600 + 1e-8, 2200 + 1e-8);
                var displaced = Line("A3", 100.01, 200, 1600.01, 2200);
                string lineKey = ManualQuantitiesCore.DuplicateKey("frame", "rail", line);
                Need(lineKey == ManualQuantitiesCore.DuplicateKey("frame", "rail", noisy), "tiny coordinate noise splits warning bucket");
                Need(lineKey != ManualQuantitiesCore.DuplicateKey("frame", "rail", displaced), "0.01 mm displacement erased from warning bucket");
                var near = ManualQuantitiesCore.BuildPreview(Rule(), new[] { line, noisy }, "Z1");
                var far = ManualQuantitiesCore.BuildPreview(Rule(), new[] { line, displaced }, "Z1");
                Need(near.accepted_count == 2 && near.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE") && far.accepted_count == 2 && !far.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE"), "warning bucket changed physical counts or ignored displacement");
            });
            Check("boundary warning snapping never rounds stored shape dimensions or quantity area", delegate {
                var a = Boundary("A1"); var b = Boundary("A2", 1e-8, 1e-8);
                b.boundary_points[1][0] += 2e-8; b.boundary_points[2][0] += 2e-8;
                var preview = ManualQuantitiesCore.BuildPreview(Rule("boundary", "cladding"), new[] { a, b }, "Z1");
                Need(preview.accepted_count == 2 && preview.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE"), "near boundary warning missing or physical count changed");
                var first = preview.items.Single(x => x.handle == "A1").element;
                var second = preview.items.Single(x => x.handle == "A2").element;
                // Existing shape IDs have their own 0.001 mm canonical grid; raw rings and
                // scalar quantities must retain finer data regardless of either warning bucket.
                Need(second.width_mm.Value > 600 && second.area_mm2.Value > 180000 && !string.IsNullOrWhiteSpace(second.shape_id), "warning snap leaked into shape or measures");
                Need(Math.Abs(second.width_mm.Value - 600.00000002) < 1e-10, "stored width differs from world fixture");
                Need(Math.Abs(second.area_mm2.Value - 180000.000006) < 1e-7, "stored area differs from world fixture");
                Need(second.rings[0].points[0][0] == b.boundary_points[0][0], "stored polygon rounded");
                var result = FacadeQuantitiesCore.BuildRows(new[] { Report("cladding", first, second) }, null, true, false);
                Need(result.ok && result.rows.Count == 2 && result.rows.Sum(x => x.quantity) == 2 && result.rows.Sum(x => x.area_m2.Value) > 0.36, "warning snapping merged exact quantity rows or rounded area");
            });
            Check("same bounds with a 0.0001 mm corner difference use distinct boundary warning buckets", delegate {
                var a = Boundary("A1"); var b = Boundary("A2"); b.boundary_points[2][0] -= 0.0001;
                string firstKey = ManualQuantitiesCore.DuplicateKey("cladding", "cladding", a);
                Need(firstKey != ManualQuantitiesCore.DuplicateKey("cladding", "cladding", b), "coarser quantity shape identity erased a boundary warning difference");
                var preview = ManualQuantitiesCore.BuildPreview(Rule("boundary", "cladding"), new[] { a, b }, "Z1");
                Need(preview.accepted_count == 2 && !preview.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE"), "different corners with same bounds flagged as identical warning geometry");
            });
            Check("boundary warning identity ignores vertex start winding closure and redundant collinear points", delegate {
                var a = Boundary("A1"); var b = Boundary("A2");
                b.boundary_points = new[] { new[] { 700.0, 500.0 }, new[] { 700.0, 200.0 }, new[] { 400.0, 200.0 },
                    new[] { 100.0, 200.0 }, new[] { 100.0, 500.0 }, new[] { 700.0, 500.0 } };
                Need(ManualQuantitiesCore.DuplicateKey("cladding", "cladding", a) == ManualQuantitiesCore.DuplicateKey("cladding", "cladding", b), "equivalent absolute boundaries split warning buckets");
                var preview = ManualQuantitiesCore.BuildPreview(Rule("boundary", "cladding"), new[] { a, b }, "Z1");
                Need(preview.accepted_count == 2 && preview.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE"), "equivalent absolute boundary duplicate not shown");
            });
            Check("translated equal shapes are not possible absolute-geometry duplicates", delegate {
                var preview = ManualQuantitiesCore.BuildPreview(Rule("boundary", "cladding"), new[] { Boundary("A1"), Boundary("A2", 1000, 0) }, "Z1");
                Need(preview.accepted_count == 2 && !preview.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE"), "shape identity confused with physical overlap");
            });
            Check("symbol position separates actual instances", delegate {
                var same = ManualQuantitiesCore.BuildPreview(Rule("symbol", "bracket"), new[] { Symbol("A1"), Symbol("A2") }, "Z1");
                var separated = ManualQuantitiesCore.BuildPreview(Rule("symbol", "bracket"), new[] { Symbol("A1"), Symbol("A2", 101, 200) }, "Z1");
                Need(same.accepted_count == 2 && same.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE") && separated.accepted_count == 2 && !separated.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE"), "symbol duplicate geometry ignores position");
            });
            Check("different evaluated dynamic symbol geometry is not conflated by base block family", delegate {
                var a = Symbol("A1"); var b = Symbol("A2"); a.symbol_key = "evaluated-shape-one"; b.symbol_key = "evaluated-shape-two";
                var preview = ManualQuantitiesCore.BuildPreview(Rule("symbol", "bracket"), new[] { a, b }, "Z1");
                Need(preview.accepted_count == 2 && !preview.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE"), "different evaluated geometry flagged merely because base family agrees");
            });
            Check("dynamic numeric mapping requires explicit distance unit metadata", delegate {
                var rule = Rule(); rule.fields["length"] = new ManualQuantityField { source = "dynamic", name = "LENGTH" };
                foreach (string unit in new[] { "NoUnits", "Angle", "Area", "" }) {
                    var obs = LinearBlock(); obs.dynamic_properties["LENGTH"] = 2500.0;
                    if (unit != "") obs.dynamic_units["LENGTH"] = unit;
                    Rejected(rule, obs);
                }
                var valid = LinearBlock(); valid.dynamic_properties["LENGTH"] = 2500.0; valid.dynamic_units["LENGTH"] = "Distance";
                Near(Accepted(rule, valid).length_mm.Value, 2500, "confirmed dynamic distance");
            });
            Check("preview includes rejected and unrecognized observations explicitly", delegate {
                var valid = Line("A1"); var unknown = Line("A2"); unknown.sample_key = "unmatched"; var invalid = Line("A3", 0, 0, 0, 0);
                var preview = ManualQuantitiesCore.BuildPreview(Rule(), new[] { valid, unknown, invalid }, "Z1");
                Need(preview.items.Count == 3 && preview.accepted_count == 1 && preview.rejected_count == 2 && preview.items.Any(x => x.status == "unrecognized") && preview.items.Any(x => x.status == "rejected"), "unsupported manual objects disappear");
            });
            Check("rule fingerprint is stable across dictionary insertion order and changes with revision", delegate {
                var rule = Rule(); var reordered = Clone(rule); reordered.fields = new Dictionary<string, ManualQuantityField>();
                foreach (var pair in rule.fields.Reverse()) reordered.fields.Add(pair.Key, Clone(pair.Value));
                string before = ManualQuantitiesCore.RuleFingerprint(rule);
                Need(before == ManualQuantitiesCore.RuleFingerprint(reordered), "rule fingerprint depends on insertion order");
                reordered.revision = "2"; Need(before != ManualQuantitiesCore.RuleFingerprint(reordered), "rule revision absent from fingerprint");
                reordered = Clone(rule); reordered.fields["mark"].value = "different";
                Need(before != ManualQuantitiesCore.RuleFingerprint(reordered), "mapping change absent from fingerprint");
            });
            Check("preview fingerprint covers zone geometry source and rule", delegate {
                var rule = Rule(); var obs = Line(); string before = ManualQuantitiesCore.BuildPreview(rule, new[] { obs }, "Z1").fingerprint;
                Need(!string.IsNullOrWhiteSpace(before), "preview fingerprint missing");
                Need(before != ManualQuantitiesCore.BuildPreview(rule, new[] { obs }, "Z2").fingerprint, "zone absent from preview identity");
                var changed = Clone(obs); changed.fingerprint = "changed";
                Need(before != ManualQuantitiesCore.BuildPreview(rule, new[] { changed }, "Z1").fingerprint, "captured freshness absent from preview identity");
                var newRule = Clone(rule); newRule.revision = "2";
                Need(before != ManualQuantitiesCore.BuildPreview(newRule, new[] { obs }, "Z1").fingerprint, "rule absent from preview identity");
            });
            Check("manual evaluation and preview never mutate mapping or captured observations", delegate {
                var rule = Rule(); var observations = new[] { Line("A2"), Line("A1") }; string beforeRule = Json.Serialize(rule), beforeObs = Json.Serialize(observations);
                ManualQuantitiesCore.Evaluate(rule, observations[0], "Z1"); ManualQuantitiesCore.BuildPreview(rule, observations, "Z1");
                Need(beforeRule == Json.Serialize(rule) && beforeObs == Json.Serialize(observations), "input snapshots mutated");
            });
            Check("prepared evaluation matches direct evaluation for accepted unknown rejected and conflict observations", delegate {
                var rule = Rule(); rule.fields["mark"] = new ManualQuantityField { source = "attribute", name = "MARK" };
                var context = ManualQuantitiesCore.Prepare(rule);
                var accepted = Line("A1"); accepted.attributes["MARK"] = "001-A";
                var unknown = Line("A2"); var invalid = Line("A3", 0, 0, 0, 0); var conflict = Line("A4"); conflict.generated_conflict = true;
                var directCounts = new ManualQuantityDiagnostics(); var preparedCounts = new ManualQuantityDiagnostics();
                foreach (var observation in new[] { accepted, unknown, invalid, conflict }) {
                    var direct = ManualQuantitiesCore.Evaluate(rule, observation, "Z1", directCounts);
                    var prepared = ManualQuantitiesCore.Evaluate(context, observation, "Z1", preparedCounts);
                    Need(Json.Serialize(direct) == Json.Serialize(prepared), "prepared context changes semantic evaluation for " + observation.handle);
                }
                Need(Json.Serialize(directCounts) == Json.Serialize(preparedCounts), "prepared context skips observable validation work");
            });
            Check("prepared context owns a deep rule snapshot and new preparation sees changed mapping", delegate {
                var rule = Rule(); var original = Clone(rule); var context = ManualQuantitiesCore.Prepare(rule); string originalFingerprint = context.rule_fingerprint;
                rule.fields["mark"].value = "CHANGED-MARK"; rule.fields.Remove("material"); rule.revision = "2";
                var frozen = ManualQuantitiesCore.Evaluate(context, Line("A1"), "Z1");
                var baseline = ManualQuantitiesCore.Evaluate(original, Line("A1"), "Z1");
                Need(Json.Serialize(frozen) == Json.Serialize(baseline) && context.rule_fingerprint == originalFingerprint, "original mapping mutation changed prepared snapshot");
                var freshContext = ManualQuantitiesCore.Prepare(rule); var fresh = ManualQuantitiesCore.Evaluate(freshContext, Line("A1"), "Z1");
                Need(fresh.status == "accepted" && fresh.element.mark == "CHANGED-MARK" && fresh.element.material == null && fresh.element.identity_group != frozen.element.identity_group,
                    "new preparation did not observe changed nested field and dictionary");
                Need(freshContext.rule_fingerprint != originalFingerprint, "new registry revision omitted from prepared fingerprint");
            });
            Check("empty reviewed selection is explicit zero without fabricated items", delegate {
                var preview = ManualQuantitiesCore.BuildPreview(Rule(), new ManualQuantityObservation[0], "Z1");
                Need(preview.items.Count == 0 && preview.accepted_count == 0 && preview.rejected_count == 0 && preview.repeated_count == 0 && preview.diagnostics.observations_seen == 0, "empty selection invented quantity");
            });

            string path = null; for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--report") path = args[i + 1];
            var summary = new { total = Cases.Count, passed = Cases.Count - Failed, failed = Failed,
                scope = "Pure manual mapping, identity, geometry and preview contract; no AutoCAD host, transaction, live freshness or UI.", cases = Cases };
            if (path != null) File.WriteAllText(path, Json.Serialize(summary));
            Console.WriteLine("Manual core: " + (Cases.Count - Failed) + "/" + Cases.Count + " passed");
            return Failed == 0 ? 0 : 1;
        }
    }
}
