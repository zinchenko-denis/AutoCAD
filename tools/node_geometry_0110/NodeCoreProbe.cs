using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using AFramePlugin;

internal static class NodeCoreProbe
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly List<object> Cases = new List<object>(), Numeric = new List<object>(), Performance = new List<object>();
    private static FrameSolutionSelection Sample; private static int Checks, Failed;
    private static void Need(bool value, string reason) { Checks++; if (!value) throw new Exception(reason); }
    private static void Case(string name, Action action)
    { try { action(); Cases.Add(new { name, status = "PASS" }); } catch (Exception e) { Failed++; Cases.Add(new { name, status = "FAIL", error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static void Refuses(Action action, string code = null)
    { try { action(); } catch (FrameNodeGeometryException e) { Need(code == null || e.Code == code, "wrong refusal " + e.Code + " wanted " + code); return; } catch (FrameSolutionSelectionException) { Need(code == null, "unexpected selection refusal"); return; } throw new Exception("accepted invalid input"); }
    private static FrameSolutionSelection Selection(double? front = 230, params double[] layers)
    { var s = Sample.Clone(); s.geometry.cladding_front_offset_mm = front; s.geometry.insulation_layers_mm = new List<double>(layers); return s; }
    private static FrameNodeGeometryInput Input(double? profile = 170, string kind = FrameNodeClearanceSurface.Layers, double? membrane = null)
    { return new FrameNodeGeometryInput { profile_near_face_x_mm = profile, clearance_surface = new FrameNodeClearanceSurface { kind = kind, membrane_outer_x_mm = membrane } }; }
    private static FrameParameterContext Context(FrameSolutionSelection s)
    { var p = FrameProjectParameters.CreateNext(null, s, "0123456789abcdef0123456789abcdef"); var z = FrameZoneParameters.CreateNext(null, p, new Dictionary<string, FrameParameterOverride>());
      return FrameParameterResolver.Resolve(p, new string('a', 64), z, new string('b', 64), "A1", "Ф-1").Context; }
    private static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
    private static FrameNodeSnapshot Snapshot(FrameSolutionSelection s = null)
    { s = s ?? Selection(230, 100, 50); return FrameNodeSnapshot.Create(s, Context(s), Input(), "2026-10-01T00:00:00.0000000Z"); }
    private static void NumericCase(string name, double[] layers, double profile, string gap, string status, double? front = 230)
    {
        var result = FrameNodeGeometry.Evaluate(Selection(front, layers), Input(profile));
        Need(result.GapText == gap && result.ClearanceStatus == status, name + ": " + result.GapText + " " + result.ClearanceStatus);
        Need(result.CanInsert == (status == "pass"), "wrong insertion boundary");
        Need(result.Dimensions.Single(d => d.Id == "local_clearance").ValueText == gap, "dimension rounded gap");
        Need(result.ReviewText().Contains(gap + " мм"), "review did not show exact value");
        Numeric.Add(new { name, gap_text = result.GapText, clearance_status = result.ClearanceStatus, can_insert = result.CanInsert, result_digest = result.ResultDigest, input_digest = result.InputDigest });
    }
    public static int Main(string[] args)
    {
        Sample = FrameSolutionSelection.FromDict(Json.DeserializeObject(File.ReadAllText(args[0])));
        Case("engineer: exact decimal min20 without epsilon", () => {
            NumericCase("decimal_108_95_53_34", new[] {108.95,53.34},182.29,"20","pass");
            NumericCase("decimal_85_03_37_89",new[] {85.03,37.89},142.92,"20","pass");
            NumericCase("decimal_85_37_64",new[] {85.0,37.64},142.64,"20","pass");
            NumericCase("next_down_170",new[] {150.0},BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(170.0)-1),"19.99999999999997","fail");
            NumericCase("exact_170",new[] {150.0},170,"20","pass");
            NumericCase("next_up_170",new[] {150.0},BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(170.0)+1),"20.00000000000003","pass");
            NumericCase("gap_19_999",new[] {150.0},169.999,"19.999","fail");
            NumericCase("gap_20_001",new[] {150.0},170.001,"20.001","pass");
            NumericCase("next_up_479_82",new[] {76.53,58.41,186.72,120.77,17.39},BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(479.82)+1),"20.00000000000005","pass",null);
            var fractions = FrameNodeGeometry.Evaluate(Selection(100,0.1,0.2),Input(20.3));
            Need(fractions.InsulationOuterXMm == 0.3 && fractions.GapText == "20", "decimal chain changed");
        });
        Case("novice: unknown surface is an explicit partial result", () => {
            var result = FrameNodeGeometry.Evaluate(Selection(230,100,50),FrameNodeGeometryInput.CreateDefault());
            Need(result.CanInsert && result.Status == "partial" && result.ClearanceStatus == "not_evaluated" && result.GapMm == null, "unknown was inferred");
            Need(result.Missing.Count == 2 && result.Limitations.Count >= 4, "missing/limits lost");
            Need(result.Planes.Count == 4 && result.Layers.Count == 2 && result.Layers[1].StartXMm == 100 && result.Layers[1].EndXMm == 150, "wrong known planes");
            Need(!FrameNodeGeometry.Evaluate(Selection(null),FrameNodeGeometryInput.CreateDefault()).CanInsert, "empty scheme accepted");
            var empty = FrameNodeGeometry.Evaluate(Selection(230),Input());
            Need(!empty.CanInsert && empty.ClearanceStatus == "not_evaluated" && empty.InsulationOuterXMm == null, "empty layers treated as zero");
            var profileOnly = FrameNodeGeometry.Evaluate(Selection(null),Input(170,FrameNodeClearanceSurface.Unknown));
            Need(profileOnly.CanInsert && profileOnly.Status == "partial", "explicit one-plane partial lost");
        });
        Case("engineer: contradictory planes and gap are separate precise refusals", () => {
            var bad = FrameNodeGeometry.Evaluate(Selection(180,150,50),Input());
            Need(!bad.CanInsert && bad.Issues.Any(i=>i.Code=="E_NODE_INSULATION_FRONT"), "baseline 180/200 accepted");
            Need(!FrameNodeGeometry.Evaluate(Selection(200,150,50),Input()).CanInsert, "equality outer front accepted");
            var inside = FrameNodeGeometry.Evaluate(Selection(230,100,50),Input(140));
            Need(inside.GapText == "-10" && inside.ClearanceStatus == "fail", "negative gap disappeared");
            var membrane = FrameNodeGeometry.Evaluate(Selection(230,100,50),Input(172,FrameNodeClearanceSurface.Membrane,152));
            Need(membrane.CanInsert && membrane.GapText == "20", "membrane position ignored");
            Need(!FrameNodeGeometry.Evaluate(Selection(230,100,50),Input(170,FrameNodeClearanceSurface.Membrane,149)).CanInsert, "membrane inside insulation accepted");
            Need(!FrameNodeGeometry.Evaluate(Selection(230,100,50),Input(250,FrameNodeClearanceSurface.Membrane,230)).CanInsert, "membrane at front accepted");
            Need(!FrameNodeGeometry.Evaluate(Selection(170,100,50),Input(170)).CanInsert, "profile at front accepted");
            foreach(double gp in new[]{100.0,150.0}) { var unknown=FrameNodeGeometry.Evaluate(Selection(230,150),Input(gp,FrameNodeClearanceSurface.Unknown));
                Need(!unknown.CanInsert && unknown.ClearanceStatus=="not_evaluated" && unknown.GapText==null && unknown.Issues.Any(i=>i.Code=="E_NODE_PROFILE_INSULATION"),"unknown surface hid known intersection"); }
            var noFront = FrameNodeGeometry.Evaluate(Selection(null,100,50),Input());
            Need(noFront.CanInsert && noFront.ClearanceStatus=="pass" && noFront.Missing.Any(m=>m.Contains("облицовки")), "local pass hid missing front");
        });
        Case("strict input and source identity; no legacy schema extension", () => {
            foreach (var field in Input().ToDict().Keys) { var d=Input().ToDict(); d.Remove(field); Refuses(()=>FrameNodeGeometryInput.FromDict(d)); }
            var extra=Input().ToDict(); extra["auto_approved"]=true; Refuses(()=>FrameNodeGeometryInput.FromDict(extra),"E_NODE_SCHEMA");
            foreach(object value in new object[]{true,"170",0,-1,double.NaN,double.PositiveInfinity}) { var d=Input().ToDict(); d["profile_near_face_x_mm"]=value; Refuses(()=>FrameNodeGeometryInput.FromDict(d)); }
            foreach(object value in new object[]{169.999999999999999999m,9007199254740993L}) { var d=Input().ToDict(); d["profile_near_face_x_mm"]=value; Refuses(()=>FrameNodeGeometryInput.FromDict(d),"E_NODE_NUMERIC_PRECISION"); }
            Refuses(()=>FrameNodeGeometryInput.FromDict(null)); Refuses(()=>Input(170,FrameNodeClearanceSurface.Unknown,150).ToDict());
            Refuses(()=>Input(170,FrameNodeClearanceSurface.Membrane).ToDict());
            foreach(string field in new[]{"source_sha256","catalog_revision","solution_id","schema"}) { var d=Sample.ToDict(); d[field]="unknown"; Refuses(()=>FrameNodeGeometry.Evaluate(FrameSolutionSelection.FromDict(d),Input())); }
            var bad=Sample.Clone(); bad.node.pdf_page=19; Refuses(()=>FrameNodeGeometry.Evaluate(bad,Input()));
            Need(FrameParameterResolver.Fields.Count==13,"override contract expanded");
            Need(!(bool)Dict(Dict(FrameSolutionSelection.CatalogSnapshot()["solution"])["local_clearance"])["checked_by_this_increment"],"historical catalog changed");
        });
        Case("numeric backend refuses loss instead of silently rounding", () => {
            Refuses(()=>FrameNodeGeometry.Evaluate(Selection(null,1e-29),FrameNodeGeometryInput.CreateDefault()),"E_NODE_NUMERIC_RANGE");
            var tiny=FrameNodeGeometry.Evaluate(Selection(null,1e-28),FrameNodeGeometryInput.CreateDefault()); Need(tiny.CanInsert,"representable tiny declaration refused");
            Refuses(()=>FrameNodeGeometry.Evaluate(Selection(null,1e29),FrameNodeGeometryInput.CreateDefault()),"E_NODE_NUMERIC_RANGE");
            Refuses(()=>FrameNodeGeometry.Evaluate(Selection(null,1e20,1e-28),FrameNodeGeometryInput.CreateDefault()),"E_NODE_NUMERIC_PRECISION");
            Refuses(()=>FrameNodeGeometry.Evaluate(Selection(null,5e28,5e28),FrameNodeGeometryInput.CreateDefault()),"E_NODE_NUMERIC_PRECISION");
            Refuses(()=>FrameNodeGeometry.Evaluate(Selection(null,1e15,0.01),FrameNodeGeometryInput.CreateDefault()),"E_NODE_CAD_PRECISION");
            var d=Input().ToDict(); d["unit"]="inch"; Refuses(()=>FrameNodeGeometryInput.FromDict(d));
        });
        Case("reviewer: immutable snapshot, strict digests and full provenance", () => {
            var snap=Snapshot(); var restored=FrameNodeSnapshot.FromDict(Json.DeserializeObject(Json.Serialize(snap.ToDict())));
            Need(restored.SnapshotDigest==snap.SnapshotDigest && restored.Result.ResultDigest==snap.Result.ResultDigest,"roundtrip altered snapshot");
            var detached=snap.Selection; detached.geometry.cladding_front_offset_mm=999; var detachedInput=snap.Input; detachedInput.profile_near_face_x_mm=999;
            var detachedContext=snap.Context; detachedContext.origins["bracket.L_mm"]="zone_override";
            Need(snap.Selection.geometry.cladding_front_offset_mm==230 && snap.Input.profile_near_face_x_mm==170 && snap.Context.origins["bracket.L_mm"]=="project_default","mutable snapshot exposure");
            foreach(string field in snap.ToDict().Keys) { var d=snap.ToDict(); d.Remove(field); Refuses(()=>FrameNodeSnapshot.FromDict(d)); }
            foreach(string field in new[]{"input_digest","result_digest","snapshot_digest","algorithm_revision"}) { var d=snap.ToDict(); d[field]=new string('f',64); Refuses(()=>FrameNodeSnapshot.FromDict(d)); }
            var changed=snap.ToDict(); Dict(Dict(changed["selection"])["geometry"])["cladding_front_offset_mm"]=240; Refuses(()=>FrameNodeSnapshot.FromDict(changed));
            var origin=snap.ToDict(); Dict(Dict(origin["context"])["origins"])["bracket.L_mm"]="zone_override"; Refuses(()=>FrameNodeSnapshot.FromDict(origin),"E_NODE_DIGEST");
            var revision=snap.ToDict(); Dict(Dict(revision["context"])["project"])["revision"]=2; Refuses(()=>FrameNodeSnapshot.FromDict(revision),"E_NODE_DIGEST");
            var falsePass=snap.ToDict(); falsePass["pass"]=true; Refuses(()=>FrameNodeSnapshot.FromDict(falsePass));
            var s=Selection(230,100,50); Refuses(()=>FrameNodeSnapshot.Create(s,Context(s),Input(169.999),"2026-10-01T00:00:00.0000000Z"),"E_NODE_REJECTED");
            Refuses(()=>FrameNodeSnapshot.Create(s,Context(s),Input(),"2026-10-01T00:00:00"),"E_NODE_TIMESTAMP");
            Need(snap.CaptionLines().Any(c=>c.Contains("Актуальность")) && snap.CaptionLines().Any(c=>c.Contains("Не рабочий")),"stamp claims unbounded validity");
            var corner=Selection(null,76.53,58.41,186.72,120.77,17.39);
            var cornerInput=Input(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(479.82)+1));
            var exactSnap=FrameNodeSnapshot.Create(corner,Context(corner),cornerInput,"2026-10-01T00:00:00.0000000Z");
            var exactRead=FrameNodeSnapshot.FromDict(Json.DeserializeObject(Json.Serialize(exactSnap.ToDict())));
            Need(exactRead.SnapshotDigest==exactSnap.SnapshotDigest && exactRead.Input.profile_near_face_x_mm==cornerInput.profile_near_face_x_mm && exactRead.Result.GapText=="20.00000000000005","decimal JSON roundtrip changed one ULP");
        });
        Case("calculator: declared members do not imply placement, capacity or min30", () => {
            var s=Selection(230,100,50); var a=FrameNodeGeometry.Evaluate(s,Input()); s.bracket.L_mm=350; s.extender.L_mm=150;
            var b=FrameNodeGeometry.Evaluate(s,Input());
            Need(a.GapText==b.GapText && a.Planes.Select(p=>p.XMm).SequenceEqual(b.Planes.Select(p=>p.XMm)),"member lengths moved planes");
            Need(a.SelectionDigest!=b.SelectionDigest,"member identity lost");
            Need(a.ClearanceRule.MinimumMm==20 && a.ClearanceRule.PdfPage==20 && a.ClearanceRule.Sheet=="4.2.1","wrong rule source");
            Need(a.Limitations.Any(l=>l.Contains("статический")) && a.Limitations.Any(l=>l.Contains("Автоподбор")),"calculations implied");
            var c=FrameNodeGeometry.Evaluate(Selection(300,100,50),Input()); Need(c.GapText==a.GapText,"cladding front substituted for near profile");
        });
        Case("bound numeric source guard never silently adopts resolver rounding", () => {
            var s=Selection(230,100,50); var p=FrameProjectParameters.CreateNext(null,s,"0123456789abcdef0123456789abcdef");
            var z=FrameZoneParameters.CreateNext(null,p,new Dictionary<string,FrameParameterOverride>());
            var r=FrameParameterResolver.Resolve(p,new string('a',64),z,new string('b',64),"A1","Ф-1");
            FrameNodeGeometry.ValidateBoundSelection(r.Selection,r.Context,p.ToDict(),z.ToDict()); Need(true,"exact inherited binding");
            var wrong=r.Selection; wrong.geometry.cladding_front_offset_mm=240;
            Refuses(()=>FrameNodeGeometry.ValidateBoundSelection(wrong,r.Context,p.ToDict(),z.ToDict()),"E_NODE_SOURCE_PRECISION");
            var local=FrameZoneParameters.CreateNext(null,p,new Dictionary<string,FrameParameterOverride>{{"geometry.cladding_front_offset_mm",FrameParameterOverride.Set(240)}});
            var lr=FrameParameterResolver.Resolve(p,new string('a',64),local,new string('c',64),"A1","Ф-1");
            FrameNodeGeometry.ValidateBoundSelection(lr.Selection,lr.Context,p.ToDict(),local.ToDict()); Need(true,"exact override");
            var clear=FrameZoneParameters.CreateNext(null,p,new Dictionary<string,FrameParameterOverride>{{"geometry.cladding_front_offset_mm",FrameParameterOverride.Clear()}});
            var cr=FrameParameterResolver.Resolve(p,new string('a',64),clear,new string('d',64),"A1","Ф-1");
            FrameNodeGeometry.ValidateBoundSelection(cr.Selection,cr.Context,p.ToDict(),clear.ToDict()); Need(true,"exact clear");
        });
        Case("linear output cardinality and independent operation measurements", () => {
            int previousBytes=0;
            foreach(int count in new[]{1,100,1000}) {
                var s=Selection(count+100,Enumerable.Repeat(1.0,count).ToArray()); var input=Input(count+20); var watch=Stopwatch.StartNew();
                var result=FrameNodeGeometry.Evaluate(s,input); watch.Stop(); int bytes=System.Text.Encoding.UTF8.GetByteCount(Json.Serialize(result.ToDict()));
                Need(result.Layers.Count==count && result.Planes.Count==count+3 && result.Dimensions.Count==count+3,"nonlinear or missing output cardinality");
                Need(result.GapText=="20","large layer chain failed");
                if(count==1000) Need(bytes<previousBytes*11,"serialized output grew quadratically"); previousBytes=bytes;
                Performance.Add(new { kind="layers",count,elapsed_ms=watch.Elapsed.TotalMilliseconds,bytes,planes=result.Planes.Count,dimensions=result.Dimensions.Count });
            }
            foreach(int count in new[]{1,100,1000}) { var s=Selection(230,100,50); var input=Input(); var watch=Stopwatch.StartNew(); for(int i=0;i<count;i++) Need(FrameNodeGeometry.Evaluate(s,input).GapText=="20","operation leaked state"); watch.Stop(); Performance.Add(new {kind="independent_operations",count,elapsed_ms=watch.Elapsed.TotalMilliseconds}); }
        });
        var output=new { status=Failed==0?"PASS":"FAIL",checks=Checks,failed=Failed,cases=Cases,numeric=Numeric,performance=Performance,snapshot=Snapshot().ToDict(),live_autocad_checked=false };
        File.WriteAllText(args[1],Json.Serialize(output)); Console.WriteLine("Node core: "+Checks+" checks, "+Failed+" failures"); return Failed==0?0:1;
    }
}
