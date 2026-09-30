using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;

internal static class QuantitiesCoreCheck
{
    private static readonly List<object> Cases = new List<object>();
    private static int failed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    private static void Check(string name, Action action)
    {
        string error = null;
        try { action(); } catch (Exception ex) { failed++; error = ex.Message; }
        Cases.Add(new { name = name, passed = error == null, error = error });
        Console.WriteLine((error == null ? "PASS " : "FAIL ") + name + (error == null ? "" : ": " + error));
    }
    private static void Need(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Near(double a, double b, string message) { Need(Math.Abs(a - b) < 1e-10, message + ": " + a + " != " + b); }
    private static T Clone<T>(T value) { return Json.Deserialize<T>(Json.Serialize(value)); }
    private static QuantityRing Ring(string role, params double[] xy)
    {
        var points = new List<double[]>();
        for (int i = 0; i < xy.Length; i += 2) points.Add(new[] { xy[i], xy[i + 1] });
        return new QuantityRing { role = role, points = points.ToArray() };
    }
    private static QuantityRing Rectangle(double x, double y, double w, double h)
    { return Ring("outer", x, y, x + w, y, x + w, y + h, x, y + h); }
    private static QuantityShape Shape(params QuantityRing[] rings)
    {
        QuantityShape result; string reason;
        Need(FacadeQuantitiesCore.TryShape(rings, out result, out reason), reason);
        return result;
    }
    private static QuantityElement Element(string id, string zone, params QuantityRing[] rings)
    {
        if (rings.Length == 0) rings = new[] { Rectangle(0, 0, 600, 300) };
        var shape = Shape(rings);
        return new QuantityElement { element_id = id, zone_ids = new List<string> { zone },
            origin = "generated:test", role = "cladding", product_id = "P-600", mark = "M", material = "Керамика",
            type = "Тип A", color = "Бежевый", orientation = "front", piece_kind = "full",
            width_mm = shape.width_mm, height_mm = shape.height_mm, area_mm2 = shape.area_mm2,
            shape_id = shape.shape_id, rings = new List<QuantityRing>(rings),
            cad_entities = new List<QuantityCadEntity> { new QuantityCadEntity { handle = "H" + id, role = "outer", fingerprint = "fp" + id } } };
    }
    private static QuantityReport Report(params QuantityElement[] elements)
    {
        return new QuantityReport { report_id = "r1", run_id = "run1", document_id = "doc1", algorithm = "test/1",
            completeness = "complete", zone_ids = elements.SelectMany(x => x.zone_ids).Distinct().ToList(),
            elements = new List<QuantityElement>(elements) };
    }
    private static QuantityResult Build(QuantityReport r, string[] selected = null, bool byZone = true, bool cutting = false)
    { return FacadeQuantitiesCore.BuildRows(new[] { r }, selected, byZone, cutting); }
    private static void Refused(QuantityResult result, string code)
    { Need(!result.ok && result.rows.Count == 0 && result.issues.Any(x => x.code == code && x.severity == "error"), "expected empty typed refusal " + code + "; " + Json.Serialize(result.issues)); }
    private static QuantityCuttingGroup Cutting(string id, params string[] zones)
    {
        return new QuantityCuttingGroup { group_id = id, scope_zone_ids = zones.ToList(),
            parameters = new Dictionary<string, object> { { "kerf", 3.0 } }, rows = new List<QuantityRow> {
                new QuantityRow { basis = "cutting", role = "blanks_total", type = "Тип A", unit = "шт.", quantity = 4, area_m2 = 0.72 },
                new QuantityRow { basis = "cutting", role = "waste", type = "Тип A", unit = "м²", quantity = 0.19, area_m2 = 0.19 } } };
    }

    public static int Main(string[] args)
    {
        Check("golden installed count and exact area", delegate {
            var r = Build(Report(Element("1", "Z1"), Element("2", "Z1"), Element("3", "Z1", Rectangle(0, 0, 150, 300))));
            Need(r.ok && r.rows.Count == 2 && r.rows.Sum(x => x.quantity) == 3, "installed count mismatch");
            Near(r.rows.Sum(x => x.area_m2.Value), 0.405, "area expected 2*0.18+0.045");
        });
        Check("one holed physical piece despite outer inner fill warning", delegate {
            var e = Element("1", "Z1", Rectangle(100, 200, 600, 600), Ring("hole", 200, 300, 300, 300, 300, 400, 200, 400));
            foreach (string role in new[] { "inner", "fill", "warning" }) e.cad_entities.Add(new QuantityCadEntity { handle = role, role = role, fingerprint = role });
            var r = Build(Report(e)); Need(r.ok && r.rows.Count == 1 && r.rows[0].quantity == 1, "helper graphics counted");
            Near(r.rows[0].area_m2.Value, 0.35, "hole not subtracted");
        });
        Check("shape invariant to translation winding start and closure", delegate {
            var a = Shape(Rectangle(0, 0, 600, 300));
            var b = Shape(Ring("outer", 1600, 800, 1600, 500, 1000, 500, 1000, 800, 1600, 800));
            Need(a.shape_id == b.shape_id, "equivalent shape differs");
        });
        Check("holes sorted without losing relative positions", delegate {
            var a = Shape(Rectangle(0, 0, 1000, 1000), Ring("hole", 100, 100, 200, 100, 200, 200, 100, 200), Ring("hole", 500, 500, 600, 500, 600, 600, 500, 600));
            var b = Shape(Rectangle(0, 0, 1000, 1000), Ring("hole", 500, 500, 600, 500, 600, 600, 500, 600), Ring("hole", 100, 100, 200, 100, 200, 200, 100, 200));
            Need(a.shape_id == b.shape_id, "hole order changes identity"); Near(a.area_mm2, 980000, "holes area");
        });
        Check("same bounding box different contour distinct", delegate {
            var a = Element("1", "Z1", Ring("outer", 0, 0, 600, 0, 600, 300));
            var b = Element("2", "Z1", Ring("outer", 0, 0, 600, 0, 0, 300));
            var r = Build(Report(a, b)); Need(r.ok && r.rows.Count == 2, "mirror contours merged");
        });
        Check("rotation and significant surface orientation distinct", delegate {
            var a = Element("1", "Z1", Rectangle(0, 0, 600, 300));
            var b = Element("2", "Z1", Rectangle(0, 0, 300, 600));
            var c = Element("3", "Z1", Rectangle(0, 0, 600, 300)); c.orientation = "back";
            var r = Build(Report(a, b, c)); Need(r.ok && r.rows.Count == 3, "orientation lost");
        });
        Check("material type color and mark never merge", delegate {
            var es = new List<QuantityElement>(); for (int i = 0; i < 5; i++) es.Add(Element(i.ToString(), "Z1"));
            es[1].material = "АКП"; es[2].type = "Б"; es[3].color = "Красный"; es[4].mark = "Другая";
            var r = Build(Report(es.ToArray())); Need(r.ok && r.rows.Count == 5, "product attributes merged");
        });
        Check("unknown properties and area remain explicit nullable", delegate {
            var e = Element("1", "Z1"); e.material = null; e.product_id = null; e.color = null; e.area_mm2 = null; e.width_mm = null;
            var r = Build(Report(e)); Need(r.ok && r.completeness == "partial", "unknown becomes fatal or complete");
            Need(r.rows[0].area_m2 == null && r.rows[0].width_mm == null && r.rows[0].material == null && r.issues.Any(x => x.code == "Q_VALUE_UNKNOWN"), "unknown replaced by zero");
        });
        Check("unknown warnings aggregate while preserving every source element", delegate {
            var a = Element("1", "Z1"); var b = Element("2", "Z1"); a.product_id = b.product_id = null;
            var r = Build(Report(a, b)); var warnings = r.issues.Where(x => x.code == "Q_VALUE_UNKNOWN").ToList();
            Need(r.ok && warnings.Count == 1 && warnings[0].element_count == 2 && warnings[0].element_ids.SequenceEqual(new[] { "1", "2" }), "unknown provenance lost or duplicated");
        });
        Check("duplicate selection and identical passport count once", delegate {
            var r = Report(Element("1", "Z1")); var output = FacadeQuantitiesCore.BuildRows(new[] { r, Clone(r) }, new[] { "Z1", "Z1" }, true, false);
            Need(output.ok && output.rows.Sum(x => x.quantity) == 1, "duplicate selection counted twice");
        });
        Check("JSON numeric parameter roundtrip preserves passport identity", delegate {
            var r = Report(Element("1", "Z1")); r.parameters["kerf"] = 3.0; r.engine_summary["full"] = 1L;
            r.cutting.Add(Cutting("G", "Z1"));
            var output = FacadeQuantitiesCore.BuildRows(new[] { r, Clone(r) }, null, true, true);
            Need(output.ok && output.rows.Count == 3, "JSON scalar numeric type changed passport identity");
        });
        Check("identical repeated physical element count once", delegate {
            var e = Element("1", "Z1"); var r = Build(Report(e, Clone(e)));
            Need(r.ok && r.rows.Sum(x => x.quantity) == 1, "physical identity dedup failed");
        });
        Check("conflicting element identity refuses", delegate {
            var e = Element("1", "Z1"); var b = Clone(e); b.color = "Другой";
            Refused(Build(Report(e, b)), "Q_ELEMENT_CONFLICT");
        });
        Check("conflicting repeated passport refuses", delegate {
            var a = Report(Element("1", "Z1")); var b = Clone(a); b.elements[0].mark = "changed";
            Refused(FacadeQuantitiesCore.BuildRows(new[] { a, b }, null, true, false), "Q_REPORT_CONFLICT");
        });
        Check("shared CAD object refuses", delegate {
            var a = Element("1", "Z1"); var b = Element("2", "Z1"); b.cad_entities[0].handle = a.cad_entities[0].handle.ToLowerInvariant();
            Refused(Build(Report(a, b)), "Q_CAD_LINK_DUPLICATE");
        });
        Check("same coordinates separate real details still count twice", delegate {
            var r = Build(Report(Element("1", "Z1"), Element("2", "Z1")));
            Need(r.ok && r.rows[0].quantity == 2, "coordinate overlap wrongly deduplicated");
        });
        Check("by-zone and whole-selection grouping", delegate {
            var r = Report(Element("1", "Z1"), Element("2", "Z2"));
            var zone = Build(r); var all = Build(r, null, false);
            Need(zone.ok && zone.rows.Count == 2 && all.ok && all.rows.Count == 1 && all.rows[0].quantity == 2 && all.rows[0].zone_ids.SequenceEqual(new[] { "Z1", "Z2" }), "grouping scope lost");
        });
        Check("zone display separators cannot collide in group identity", delegate {
            var a = Element("1", "A + B"); var b = Element("2", "A"); b.zone_ids.Add("B");
            var r = Build(Report(a, b)); Need(r.ok && r.rows.Count == 2, "one named zone merged with two distinct zones");
        });
        Check("installed zone subset excludes other pieces", delegate {
            var r = Build(Report(Element("1", "Z1"), Element("2", "Z2")), new[] { "Z1" });
            Need(r.ok && r.rows.Count == 1 && r.rows[0].element_ids.SequenceEqual(new[] { "1" }), "unselected piece leaked");
        });
        Check("shared merged-zone piece partial selection refuses", delegate {
            var e = Element("1", "Z1"); e.zone_ids.Add("Z2");
            Refused(Build(Report(e), new[] { "Z1" }), "Q_SHARED_PIECE_PARTIAL");
            var all = Build(Report(e)); Need(all.ok && all.rows.Count == 1 && all.rows[0].quantity == 1 && all.rows[0].zone_id.Contains("Z2"), "shared piece should count once across full scope");
        });
        Check("cutting isolated and full-scope selection required", delegate {
            var r = Report(Element("1", "Z1"), Element("2", "Z2")); r.cutting.Add(Cutting("G", "Z1", "Z2"));
            var installed = Build(r, new[] { "Z1" }, true, false); Need(installed.ok && installed.rows.Count == 1, "installed subset incorrectly includes cutting");
            Refused(Build(r, new[] { "Z1" }, true, true), "Q_CUTTING_SCOPE_PARTIAL");
            var full = Build(r, null, true, true); Need(full.ok && full.rows.Count == 4 && full.rows.Where(x => x.basis == "installed").Sum(x => x.quantity) == 2, "installed and stock mixed");
            Need(full.rows.Where(x => x.basis == "cutting").All(x => x.zone_ids.Count == 2 && x.note.Contains("Z1") && x.note.Contains("Z2")), "cutting scope not explicit");
        });
        Check("cutting unknown or invalid count never becomes zero", delegate {
            var r = Report(Element("1", "Z1")); r.cutting.Add(Cutting("G", "Z1")); r.cutting[0].rows[0].quantity = double.NaN;
            Refused(Build(r, null, true, true), "Q_CUTTING_ROW_INVALID");
        });
        Check("cutting unknown type visible without internal group UUID", delegate {
            var r = Report(Element("1", "Z1")); r.cutting.Add(Cutting("internal-secret-id", "Z1")); r.cutting[0].rows[0].type = null;
            var result = Build(r, null, true, true);
            Need(result.ok && result.completeness == "partial" && result.issues.Any(x => x.code == "Q_CUTTING_VALUE_UNKNOWN"), "cutting unknown hidden");
            Need(result.rows.All(x => x.note == null || !x.note.Contains("internal-secret-id")) && result.issues.All(x => !x.message.Contains("internal-secret-id")), "internal group ID leaked into user text");
        });
        Check("invalid unselected member cannot hide in subset", delegate {
            var r = Report(Element("1", "Z1"), Element("2", "Z2")); r.elements[1].cad_entities.Clear();
            Refused(Build(r, new[] { "Z1" }), "Q_CAD_LINK_MISSING");
        });
        Check("geometry and scalar tamper refuse", delegate {
            var e = Element("1", "Z1"); e.area_mm2 = 999;
            Refused(Build(Report(e)), "Q_MEASURE_MISMATCH");
            e = Element("1", "Z1"); e.shape_id = "tampered";
            Refused(Build(Report(e)), "Q_SHAPE_MISMATCH");
        });
        Check("nan dimensions and unknown schema refuse", delegate {
            var r = Report(Element("1", "Z1")); r.elements[0].width_mm = double.NaN;
            Refused(Build(r), "Q_MEASURE_INVALID");
            r = Report(Element("1", "Z1")); r.schema = "facade_quantities/9"; Refused(Build(r), "Q_REPORT_INVALID");
        });
        Check("different documents and unknown selection refuse", delegate {
            var a = Report(Element("1", "Z1")); var b = Report(Element("2", "Z2")); b.document_id = "other"; b.report_id = "r2";
            Refused(FacadeQuantitiesCore.BuildRows(new[] { a, b }, null, true, false), "Q_DOCUMENT_MISMATCH");
            Refused(Build(a, new[] { "missing" }), "Q_ZONE_NOT_FOUND");
        });
        Check("source partial completeness is never promoted", delegate {
            var r = Report(Element("1", "Z1")); r.completeness = "partial";
            Need(Build(r).ok && Build(r).completeness == "partial", "source completeness lost");
        });
        Check("geometry report never becomes engineering approval", delegate {
            var r = Report(Element("1", "Z1")); r.engineering_coverage = "limited_static_chain";
            var outp = Build(r); Need(outp.ok && outp.engineering_coverage == "geometry_only", "quantities promoted engineering approval");
        });
        Check("rows deterministic after permutation and inputs immutable", delegate {
            var r = Report(Element("2", "Z2"), Element("1", "Z1")); r.cutting.Add(Cutting("G", "Z1", "Z2"));
            string before = Json.Serialize(r); var a = Build(r, null, false, true);
            Need(before == Json.Serialize(r), "aggregation mutated passport");
            r.elements.Reverse(); r.zone_ids.Reverse(); r.cutting[0].rows.Reverse();
            var b = Build(r, new[] { "Z2", "Z1" }, false, true);
            Need(a.ok && b.ok && Json.Serialize(a.rows) == Json.Serialize(b.rows), "rows depend on traversal order");
        });
        Check("typed JSON roundtrip preserves exact numeric values", delegate {
            var e = Element("1", "Z1", Rectangle(0, 0, 600.123456789, 300.987654321));
            var r = Build(Clone(Report(e)));
            Need(r.ok && r.rows[0].quantity is double, "quantity not numeric");
            Near(r.rows[0].width_mm.Value, 600.123456789, "dimension rounded");
            Near(r.rows[0].area_m2.Value, 600.123456789 * 300.987654321 / 1e6, "area rounded");
        });
        Check("bowtie outside hole touching hole and nested holes refuse", delegate {
            QuantityShape shape; string reason;
            Need(!FacadeQuantitiesCore.TryShape(new[] { Ring("outer", 0, 0, 100, 100, 0, 100, 100, 0) }, out shape, out reason), "bowtie accepted");
            Need(!FacadeQuantitiesCore.TryShape(new[] { Rectangle(0, 0, 100, 100), Ring("hole", 90, 90, 110, 90, 110, 110, 90, 110) }, out shape, out reason), "outside hole accepted");
            Need(!FacadeQuantitiesCore.TryShape(new[] { Rectangle(0, 0, 100, 100), Ring("hole", 0, 20, 20, 20, 20, 40, 0, 40) }, out shape, out reason), "boundary hole accepted");
            Need(!FacadeQuantitiesCore.TryShape(new[] { Rectangle(0, 0, 100, 100), Ring("hole", 10, 10, 80, 10, 80, 80, 10, 80), Ring("hole", 20, 20, 30, 20, 30, 30, 20, 30) }, out shape, out reason), "nested hole accepted");
        });
        Check("redundant collinear vertices preserve identity", delegate {
            var a = Shape(Rectangle(0, 0, 600, 300));
            var b = Shape(Ring("outer", 0, 0, 300, 0, 600, 0, 600, 300, 0, 300));
            Need(a.shape_id == b.shape_id, "collinear vertex changed identity");
        });
        Check("compact passport retained only with verified shape identity", delegate {
            var e = Element("1", "Z1"); e.rings.Clear();
            Need(Build(Report(e)).ok, "compact passport refused");
            e.shape_id = null; Refused(Build(Report(e)), "Q_GEOMETRY_MISSING");
        });
        Check("known zero run is complete data whereas null composition refuses", delegate {
            var r = Report(); r.zone_ids.Add("Z1");
            var output = Build(r);
            Need(output.ok && output.rows.Count == 0 && output.completeness == "complete" && output.issues.Any(x => x.code == "Q_ZERO_INSTALLED_ELEMENTS" && x.severity == "info"), "known zero is not explicit successful data");
            r.elements = null; Refused(Build(r), "Q_ELEMENTS_MISSING");
        });
        Check("known empty zone selected from mixed successful run", delegate {
            var r = Report(Element("1", "Z1")); r.zone_ids.Add("Z2");
            var output = Build(r, new[] { "Z2" });
            Need(output.ok && output.rows.Count == 0 && output.completeness == "complete" && output.issues.Any(x => x.severity == "info"), "known empty zone selected other zone pieces or refused");
        });

        var report = new { total = Cases.Count, passed = Cases.Count - failed, failed = failed, cases = Cases };
        if (args.Length == 2 && args[0] == "--report") File.WriteAllText(args[1], Json.Serialize(report));
        Console.WriteLine("Quantities core: " + (Cases.Count - failed) + "/" + Cases.Count + " PASS");
        return failed == 0 ? 0 : 1;
    }
}
