using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using AFramePlugin;

internal static class MountingCoreProbe
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly List<object> Cases = new List<object>(), Baseline = new List<object>(), Numeric = new List<object>(), Performance = new List<object>();
    private static FrameSolutionSelection Sample;
    private static int Checks, Failed;
    private static void Need(bool condition, string message) { Checks++; if (!condition) throw new Exception(message); }
    private static void Case(string name, Action action)
    { try { action(); Cases.Add(new { name, status = "PASS" }); } catch (Exception e) { Failed++; Cases.Add(new { name, status = "FAIL", error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static void Refuses(Action action, string code)
    { try { action(); } catch (FrameNodeGeometryException e) { Need(e.Code == code, "wrong refusal: " + e.Code); return; } throw new Exception("invalid report input accepted"); }
    private static FrameSolutionSelection Selected(double? front = 230, params double[] layers)
    { var s = FrameNodeGeometry.CloneSelection(Sample); s.geometry.cladding_front_offset_mm = front; s.geometry.insulation_layers_mm = new List<double>(layers.Length == 0 ? new[] { 100.0, 50.0 } : layers); return s; }
    private static FrameNodeGeometryInput Local(double? profile = 170, string kind = FrameNodeClearanceSurface.Layers)
    { return new FrameNodeGeometryInput { profile_near_face_x_mm = profile, clearance_surface = new FrameNodeClearanceSurface { kind = kind } }; }
    private static string GeometryText(FrameNodeGeometryResult r) { return FrameParameterJson.Canonical(r.ToDict()); }
    private static FrameMountingAssessmentResult Report(FrameSolutionSelection s, FrameNodeGeometryInput input = null)
    { return FrameMountingAssessment.Evaluate(s, FrameNodeGeometry.Evaluate(s, input ?? Local())); }
    private static void MustStayUnconfirmed(FrameMountingAssessmentResult r)
    {
        Need(r.MountingStatus == "not_confirmed" && !r.AutomaticBracketSelectionAllowed, "local geometry unlocked assembly/selection");
        Need(r.Dependencies.Count == 7 && r.Dependencies.All(d => d.State == "not_confirmed"), "mounting blockers lost");
        Need(r.DeclaredMembers.Count == 3, "declared members lost");
    }
    private static void ReadOnly(IList list)
    { bool refused = false; try { list.RemoveAt(0); } catch (NotSupportedException) { refused = true; } Need(refused, "collection exposes mutation"); }

    public static int Main(string[] args)
    {
        Sample = FrameSolutionSelection.FromDict(Json.DeserializeObject(File.ReadAllText(args[0])));
        Case("reviewer: five valid catalogue selections never imply mounting compatibility", () => {
            foreach (string id in new[] { "declared_reference", "short_bracket", "long_bracket", "different_nominal_widths", "mixed_executions" })
            {
                var s = Selected();
                if (id == "short_bracket") { s.bracket.L_mm = 50; s.extender.L_mm = 100; }
                if (id == "long_bracket") { s.bracket.L_mm = 350; s.extender.L_mm = 150; }
                if (id == "different_nominal_widths") { s.bracket.nominal_width_mm = 50; s.extender.nominal_width_mm = 85; }
                if (id == "mixed_executions") s.extender.execution = "corrosion_resistant";
                var g = FrameNodeGeometry.Evaluate(s, Local()); string before = GeometryText(g);
                var r = FrameMountingAssessment.Evaluate(s, g); MustStayUnconfirmed(r);
                Need(g.CanInsert && g.ClearanceStatus == "pass" && g.GapText == "20" && g.Missing.Count == 0, "baseline no longer reproduces");
                Need(r.CanInsertDimensionalScheme && r.LocalGeometryStatus == "clearance_pass" && r.LocalClearanceStatus == "pass" && r.LocalGapText == "20", "local success obscured");
                Need(before == GeometryText(g), "assessment modified existing geometry/digest");
                Need(r.DeclaredMembers[0].Dimensions.Single(d => d.Id == "L_mm").ValueMm == s.bracket.L_mm, "selected bracket changed");
                Need(r.DeclaredMembers[1].Execution == s.extender.execution, "selected execution inferred");
                Baseline.Add(new { case_id = id, bracket = s.bracket, extender = s.extender, profile = s.profile,
                    local_status = g.Status, gap_text = g.GapText, can_insert = g.CanInsert, geometry_missing = g.Missing,
                    mounting_status = r.MountingStatus, automatic_selection = r.AutomaticBracketSelectionAllowed,
                    dependencies = r.Dependencies.Select(d => d.Id).ToArray() });
            }
        });
        Case("novice: partial and rejected geometry retain their independent insertion boundary", () => {
            var s = Selected(); var partial = Report(s, Local(null, FrameNodeClearanceSurface.Unknown));
            MustStayUnconfirmed(partial); Need(partial.CanInsertDimensionalScheme && partial.LocalGeometryStatus == "partial" && partial.LocalClearanceStatus == "not_evaluated", "partial blocked or mislabelled");
            var fail = Report(s, Local(169.999)); MustStayUnconfirmed(fail);
            Need(!fail.CanInsertDimensionalScheme && fail.LocalGapText == "19.999" && fail.ReviewText().Contains("Размерная схема отклонена"), "clearance refusal hidden");
            var unknown = Report(s, Local(100, FrameNodeClearanceSurface.Unknown)); MustStayUnconfirmed(unknown);
            Need(!unknown.CanInsertDimensionalScheme && unknown.LocalClearanceStatus == "not_evaluated" && !unknown.ReviewText().Contains("размерные данные неполны"), "known contradiction misrepresented as missing data");
            s.geometry.cladding_front_offset_mm = null; s.geometry.insulation_layers_mm.Clear();
            var empty = Report(s, Local(null, FrameNodeClearanceSurface.Unknown));
            Need(!empty.CanInsertDimensionalScheme && empty.LocalGeometryStatus == "rejected", "empty scheme was unlocked");
            var conflicting = Selected(150); var g = FrameNodeGeometry.Evaluate(conflicting, Local());
            Need(g.ClearanceStatus == "pass" && !g.CanInsert, "local pass/known contradiction fixture incorrect");
            var mixed = FrameMountingAssessment.Evaluate(conflicting, g);
            Need(mixed.LocalClearanceStatus == "pass" && !mixed.CanInsertDimensionalScheme, "clearance pass overwrote independent geometry refusal");
        });
        Case("strict source and selection match, including declarations irrelevant to min20", () => {
            var s = Selected(); var g = FrameNodeGeometry.Evaluate(s, Local());
            Refuses(() => FrameMountingAssessment.Evaluate(null, g), "E_MOUNT_INPUT");
            Refuses(() => FrameMountingAssessment.Evaluate(s, null), "E_MOUNT_INPUT");
            s.bracket.L_mm = 210; Refuses(() => FrameMountingAssessment.Evaluate(s, g), "E_MOUNT_SELECTION");
            s = Selected(); s.geometry.insulation_layers_mm = new List<double> { 150 };
            Refuses(() => FrameMountingAssessment.Evaluate(s, g), "E_MOUNT_SELECTION");
            foreach (Action<FrameSolutionSelection> change in new Action<FrameSolutionSelection>[] {
                v => v.source_id = "other", v => v.source_sha256 = new string('0',64), v => v.solution_id = "other",
                v => v.catalog_revision = new string('0',64), v => v.node.pdf_page = 19, v => v.bracket.L_mm = 51 })
            {
                var bad = Selected(); change(bad); bool refused = false;
                try { FrameMountingAssessment.Evaluate(bad, g); } catch (FrameSolutionSelectionException) { refused = true; }
                catch (FrameNodeGeometryException) { refused = true; }
                Need(refused, "invalid catalogue/source accepted");
            }
        });
        Case("independent source locators and precise unconfirmed dependencies", () => {
            var r = Report(Selected());
            Need(r.SourceId == "vector1_2015" && r.SourceSha256 == "7386f4152de455a5e2622704d51a98d8f05afd20050807f634eea4572de37142" && r.SolutionId == "vector1_2015_type1_4_2_1", "wrong primary source");
            Need(r.DeclaredMembers.Select(m => m.Source.PdfPage).SequenceEqual(new[] { 6, 8, 9 }), "wrong member primary pages");
            Need(r.DeclaredMembers.Select(m => m.Source.Sheet).SequenceEqual(new[] { "3.2.1", "3.3", "3.4" }), "wrong member sheets");
            var ids = new[] { "wall_to_kr2_datum", "kr2_length_datums", "kr2_uk_engagement", "kr2_uk_connection", "uk_gp_datum", "gp_to_cladding", "assembly_scope" };
            Need(r.Dependencies.Select(d => d.Id).SequenceEqual(ids), "missing/duplicated dependency");
            Need(r.Dependencies.All(d => d.Sources.Any(p => p.PdfPage == 20 && p.Sheet == "4.2.1") && d.Sources.All(p => p.SourceSha256 == r.SourceSha256)), "source coverage not tied to pilot");
            Need(r.Dependencies[0].Sources.Any(p => p.PdfPage == 7 && p.Sheet == "3.2.2" && p.Position == "2.6") && r.Dependencies[0].ConfirmedPart.Contains("2 мм"), "known pad dimension lost or inferred");
            Need(r.Dependencies.All(d => !string.IsNullOrWhiteSpace(d.ConfirmedPart) && !string.IsNullOrWhiteSpace(d.NeededEvidence) && !string.IsNullOrWhiteSpace(d.Consequence)), "generic warning replaced actionable dependency");
            Need(r.Dependencies.All(d => d.Sources.All(p => p.PdfPage != 19)), "other node became mounting evidence");
            Need(r.OutsideCurrentAssessment.Any(x => x.Contains("Неподвижные/подвижные")) && r.OutsideCurrentAssessment.Any(x => x.Contains("Прочность")), "static contract silently claimed");
        });
        Case("immutable report survives caller mutation and shares no mutable selection", () => {
            var s = Selected(); var r = Report(s); string before = r.ReviewText();
            s.bracket.L_mm = 350; s.extender.execution = "corrosion_resistant"; s.profile.b_mm = 999; s.geometry.insulation_layers_mm.Clear();
            Need(before == r.ReviewText(), "original selection mutation leaks into report");
            ReadOnly((IList)r.DeclaredMembers); ReadOnly((IList)r.Dependencies); ReadOnly((IList)r.OutsideCurrentAssessment);
            ReadOnly((IList)r.DeclaredMembers[0].Dimensions); ReadOnly((IList)r.Dependencies[0].Sources);
            foreach (Type type in new[] { typeof(FrameMountingAssessmentResult), typeof(FrameMountingMember), typeof(FrameMountingDimension), typeof(FrameMountingDependency), typeof(FrameMountingSource) })
            {
                Need(type.GetFields(BindingFlags.Instance | BindingFlags.Public).Length == 0, "public mutable field in " + type.Name);
                Need(type.GetProperties().All(p => p.GetSetMethod() == null), "public setter in " + type.Name);
            }
            var later = Report(Selected()); Need(before == later.ReviewText(), "attempted collection mutation leaks into another report");
        });
        Case("project profile values stay declared or unknown without a new numeric envelope", () => {
            var s = Selected(); var validGeometry = FrameNodeGeometry.Evaluate(s, Local());
            s.profile.b_mm = null; s.profile.thickness_mm = null;
            bool oldRefuses = false, newRefuses = false;
            try { FrameNodeGeometry.Evaluate(s, Local()); } catch (FrameSolutionSelectionException) { oldRefuses = true; }
            try { FrameMountingAssessment.Evaluate(s, validGeometry); } catch (FrameSolutionSelectionException) { newRefuses = true; }
            Need(oldRefuses && newRefuses, "nullable DTO fields bypassed unchanged strict selection validation");
            FrameMountingAssessmentResult r; FrameMountingMember gp;
            foreach (double value in new[] { double.Epsilon, 1e-29, 0.1, BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(479.82) + 1), double.MaxValue })
            {
                s.profile.b_mm = value; s.profile.thickness_mm = value; var geometry = FrameNodeGeometry.Evaluate(s, Local()); string original = GeometryText(geometry);
                r = FrameMountingAssessment.Evaluate(s, geometry); gp = r.DeclaredMembers.Single(m => m.FamilyId == "gp");
                foreach (var d in gp.Dimensions.Where(d => d.Basis == "project_declared"))
                {
                    Need(d.ValueText == value.ToString("R", CultureInfo.InvariantCulture), "profile decimal text rounded");
                    Need(BitConverter.DoubleToInt64Bits(d.ValueMm.Value) == BitConverter.DoubleToInt64Bits(value), "profile bits changed");
                }
                Need(geometry.CanInsert && original == GeometryText(geometry), "new assessment narrows old profile numeric range");
            }
        });
        Case("existing exact local numeric fixtures survive assessment unchanged", () => {
            var fixtures = (object[])Json.DeserializeObject(File.ReadAllText(args[2]));
            foreach (var raw in fixtures)
            {
                var expected = (Dictionary<string, object>)raw; string name = (string)expected["name"];
                double[] layers; double profile; double? front = 230;
                if (name == "decimal_108_95_53_34") { layers = new[] { 108.95,53.34 }; profile=182.29; }
                else if (name == "decimal_85_03_37_89") { layers=new[] {85.03,37.89}; profile=142.92; }
                else if (name == "decimal_85_37_64") { layers=new[] {85.0,37.64}; profile=142.64; }
                else if (name == "next_up_479_82") { layers=new[] {76.53,58.41,186.72,120.77,17.39}; profile=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(479.82)+1); front=null; }
                else { layers=new[] {150.0}; profile=name == "next_down_170" ? BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(170.0)-1) : name == "next_up_170" ? BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(170.0)+1) : name == "gap_19_999" ? 169.999 : name == "gap_20_001" ? 170.001 : 170; }
                var s=Selected(front,layers); var g=FrameNodeGeometry.Evaluate(s,Local(profile)); string before=GeometryText(g); var r=FrameMountingAssessment.Evaluate(s,g);
                Need(r.LocalGapText==(string)expected["gap_text"] && r.LocalClearanceStatus==(string)expected["clearance_status"] && r.CanInsertDimensionalScheme==(bool)expected["can_insert"], "exact fixture changed: "+name);
                Need(before==GeometryText(g), "numeric digest changed: "+name); MustStayUnconfirmed(r);
                Numeric.Add(new {name, gap_text=r.LocalGapText, clearance_status=r.LocalClearanceStatus, can_insert=r.CanInsertDimensionalScheme, input_digest=g.InputDigest, result_digest=g.ResultDigest});
            }
        });
        Case("repeat calls stable across cultures and bounded to selected input", () => {
            var s=Selected(); var g=FrameNodeGeometry.Evaluate(s,Local()); string reference=FrameMountingAssessment.Evaluate(s,g).ReviewText();
            var culture=CultureInfo.CurrentCulture; try {
                foreach (string name in new[] {"en-US","ru-RU","de-DE"}) { CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(name); Need(FrameMountingAssessment.Evaluate(s,g).ReviewText()==reference,"culture changes report"); }
            } finally { CultureInfo.CurrentCulture=culture; }
            foreach (int count in new[] {1,100,1000}) {
                var watch=Stopwatch.StartNew(); FrameMountingAssessmentResult r=null;
                for(int i=0;i<count;i++) r=FrameMountingAssessment.Evaluate(s,g);
                watch.Stop(); Need(r.ReviewText()==reference,"repeat report drift");
                Performance.Add(new {kind="repeated_assessments",count,elapsed_ms=watch.Elapsed.TotalMilliseconds,declared_members=r.DeclaredMembers.Count,dependencies=r.Dependencies.Count});
            }
            foreach (int count in new[] {1,10,1000}) {
                s=Selected(count+100,Enumerable.Repeat(1.0,count).ToArray()); g=FrameNodeGeometry.Evaluate(s,Local(count+20)); var watch=Stopwatch.StartNew(); var r=FrameMountingAssessment.Evaluate(s,g); watch.Stop();
                Need(r.LocalGapText=="20" && r.DeclaredMembers.Count==3 && r.Dependencies.Count==7,"report grows with irrelevant layer detail");
                Performance.Add(new {kind="layer_input",count,elapsed_ms=watch.Elapsed.TotalMilliseconds,declared_members=r.DeclaredMembers.Count,dependencies=r.Dependencies.Count});
            }
        });
        var sampleReport=Report(Selected());
        File.WriteAllText(args[1],Json.Serialize(new {status=Failed==0?"PASS":"FAIL",checks=Checks,failed=Failed,cases=Cases,baseline=Baseline,numeric=Numeric,performance=Performance,
            sample_report=sampleReport,review_text=sampleReport.ReviewText(),live_autocad_checked=false}));
        Console.WriteLine("Mounting assessment core: " + Checks + " checks; " + Failed + " failures.");
        return Failed==0?0:1;
    }
}
