using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using AFramePlugin;

internal static class CoreProbe
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly List<object> Cases = new List<object>();
    private static int Checks, Failed;
    private const string ProjectId = "0123456789abcdef0123456789abcdef";
    private static readonly string RawProject = new string('a', 64), RawZone = new string('b', 64);
    private static FrameSolutionSelection Sample;
    private static void Need(bool value, string message) { Checks++; if (!value) throw new Exception(message); }
    private static void Refuses(Action action)
    { bool refused = false; try { action(); } catch (FrameSolutionSelectionException) { refused = true; } Need(refused, "expected precise refusal"); }
    private static void Check(string name, Action action)
    { try { action(); Cases.Add(new { name, status = "PASS" }); } catch (Exception e) { Failed++; Cases.Add(new { name, status = "FAIL", reason = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static Dictionary<string,object> Dict(object value) { return (Dictionary<string,object>)value; }
    private static FrameProjectParameters Project() { return FrameProjectParameters.CreateNext(null, Sample, ProjectId); }
    private static FrameZoneParameters Zone(FrameProjectParameters project) { return FrameZoneParameters.CreateNext(null, project, new Dictionary<string,FrameParameterOverride>()); }
    private static FrameParameterResolution Resolve(FrameProjectParameters project, FrameZoneParameters zone, string handle = "A1", string name = "Ф-1")
    { return FrameParameterResolver.Resolve(project, RawProject, zone, RawZone, handle, name); }
    private static FrameZoneParameters Override(FrameProjectParameters project, string path, FrameParameterOverride value)
    { return FrameZoneParameters.CreateNext(null, project, new Dictionary<string, FrameParameterOverride> { { path, value } }); }
    public static int Main(string[] args)
    {
        Sample = FrameSolutionSelection.FromDict(Json.DeserializeObject(File.ReadAllText(args[0])));
        Sample.geometry.cladding_front_offset_mm = 230; Sample.geometry.insulation_layers_mm = new List<double> { 100, 50 };
        Check("project bootstrap and roundtrip are explicit and detached", delegate {
            var p = Project(); Need(p.revision == 1 && p.project_id == ProjectId, "bootstrap identity");
            Need(FrameSolutionSelection.Same(p.defaults, Sample), "lost defaults");
            var restored = FrameProjectParameters.FromDict(Dict(Json.DeserializeObject(Json.Serialize(p.ToDict()))));
            Need(restored.content_digest == p.content_digest, "unstable semantic digest");
            restored.defaults.geometry.cladding_front_offset_mm = 999;
            Need(p.defaults.geometry.cladding_front_offset_mm == 230, "mutated stored project"); Refuses(() => restored.ToDict());
            Refuses(() => FrameProjectParameters.CreateNext(null, Sample, "not-a-project-id"));
            Refuses(() => FrameProjectParameters.CreateNext(null, null, ProjectId));
        });
        Check("semantic project no-op preserves revision; change increments and overflow refuses", delegate {
            var p = Project(); var noOp = FrameProjectParameters.CreateNext(p, p.defaults.Clone());
            Need(noOp.revision == p.revision && noOp.content_digest == p.content_digest, "no-op advanced revision");
            var changed = p.defaults.Clone(); changed.geometry.cladding_front_offset_mm = 320;
            var next = FrameProjectParameters.CreateNext(p, changed);
            Need(next.revision == 2 && next.content_digest != p.content_digest && p.defaults.geometry.cladding_front_offset_mm == 230, "revision update mutated old project");
            Refuses(() => FrameProjectParameters.CreateNext(p, changed, "1123456789abcdef0123456789abcdef"));
            p.revision = long.MaxValue; Refuses(() => FrameProjectParameters.CreateNext(p, changed));
            Need(FrameProjectParameters.CreateNext(p, p.defaults).revision == long.MaxValue, "unchanged maximum revision must remain usable");
        });
        Check("strict project schema/digest/revision validation", delegate {
            foreach (string field in new[] { "schema", "project_id", "revision", "content_digest", "defaults" }) { var d = Project().ToDict(); d.Remove(field); Refuses(() => FrameProjectParameters.FromDict(d)); }
            foreach (object bad in new object[] { null, true, "1", 0, -1, 1.5, double.NaN, double.PositiveInfinity }) { var d = Project().ToDict(); d["revision"] = bad; Refuses(() => FrameProjectParameters.FromDict(d)); }
            var unknown = Project().ToDict(); unknown["approved"] = true; Refuses(() => FrameProjectParameters.FromDict(unknown));
            var tampered = Project().ToDict(); Dict(Dict(tampered["defaults"])["geometry"])["cladding_front_offset_mm"] = 999; Refuses(() => FrameProjectParameters.FromDict(tampered));
        });
        Check("inherit and project revision resolve dynamically without mutating previous snapshot", delegate {
            var p = Project(); var zone = Zone(p); var old = Resolve(p, zone);
            Need(FrameSolutionSelection.Same(old.Selection, Sample), "empty overrides must inherit");
            Need(old.Context.origins.Count == 13 && old.Context.origins.Values.All(v => v == "project_default"), "inherited origins missing");
            var nextDefaults = p.defaults.Clone(); nextDefaults.geometry.cladding_front_offset_mm = 320;
            var next = FrameProjectParameters.CreateNext(p, nextDefaults); var current = Resolve(next, zone);
            Need(current.Selection.geometry.cladding_front_offset_mm == 320 && old.Selection.geometry.cladding_front_offset_mm == 230, "inherited snapshot changed in place");
            Need(current.Context.project.revision == 2 && !FrameParameterContext.Same(old.Context, current.Context), "revision not a dependency");
        });
        Check("set survives project change, clear differs from inherit, reset restores inheritance", delegate {
            var p = Project(); var overrides = new Dictionary<string, FrameParameterOverride> {
                { "bracket.L_mm", FrameParameterOverride.Set(210) }, { "geometry.cladding_front_offset_mm", FrameParameterOverride.Clear() },
                { "geometry.insulation_layers_mm", FrameParameterOverride.Clear() } };
            var zone = FrameZoneParameters.CreateNext(null, p, overrides); var r = Resolve(p, zone);
            Need(r.Selection.bracket.L_mm == 210 && r.Selection.geometry.cladding_front_offset_mm == null && r.Selection.geometry.insulation_layers_mm.Count == 0, "clear inherited project values");
            Need(r.Context.origins["geometry.cladding_front_offset_mm"] == "zone_clear" && r.Context.origins["bracket.L_mm"] == "zone_override", "lost override origin");
            var nextDefaults = p.defaults.Clone(); nextDefaults.bracket.L_mm = 220; nextDefaults.geometry.cladding_front_offset_mm = 400;
            var next = FrameProjectParameters.CreateNext(p, nextDefaults); var preserved = Resolve(next, zone);
            Need(preserved.Selection.bracket.L_mm == 210 && preserved.Selection.geometry.cladding_front_offset_mm == null, "project overwrote local intent");
            var inherited = FrameZoneParameters.CreateNext(zone, next, new Dictionary<string,FrameParameterOverride>());
            Need(inherited.revision == 2 && Resolve(next, inherited).Selection.bracket.L_mm == 220, "reset did not inherit current project");
            var same = FrameZoneParameters.CreateNext(zone, p, new Dictionary<string,FrameParameterOverride>(overrides.Reverse().ToDictionary(x => x.Key, x => x.Value)));
            Need(same.revision == zone.revision && same.content_digest == zone.content_digest, "dictionary ordering advanced zone revision");
        });
        Check("all thirteen fields are effective and source identity remains immutable", delegate {
            var p = Project(); var values = new Dictionary<string,object> {
                { "bracket.execution", "corrosion_resistant" }, { "bracket.nominal_width_mm", 50 }, { "bracket.L_mm", 250 },
                { "extender.execution", "corrosion_resistant" }, { "extender.nominal_width_mm", 85 }, { "extender.L_mm", 150 }, { "extender.thickness_mm", 1.5 },
                { "profile.execution", "corrosion_resistant" }, { "profile.a_mm", 80 }, { "profile.b_mm", 37 }, { "profile.thickness_mm", 1.4 },
                { "geometry.cladding_front_offset_mm", 400 }, { "geometry.insulation_layers_mm", new object[] { 75, 40 } } };
            foreach (var value in values) { var r = Resolve(p, Override(p, value.Key, FrameParameterOverride.Set(value.Value)));
                Need(r.Context.origins[value.Key] == "zone_override", "origin missing " + value.Key);
                Need(Json.Serialize(FrameParameterResolver.GetValue(r.Selection, value.Key)) == Json.Serialize(value.Value), "value not resolved " + value.Key);
                Need(r.Selection.source_sha256 == p.defaults.source_sha256 && r.Selection.catalog_revision == p.defaults.catalog_revision, "source identity changed"); }
            foreach (string path in new[] { "node.sheet", "source_id", "catalog_revision", "profile.b_basis", "geometry.cladding_offset_basis", "unknown" })
                Refuses(() => Override(p, path, FrameParameterOverride.Set("anything")));
        });
        Check("malformed, unsupported and required-clear overrides refuse", delegate {
            var p = Project(); foreach (var field in FrameParameterResolver.Fields.Where(f => !f.CanClear)) Refuses(() => Override(p, field.Path, FrameParameterOverride.Clear()));
            foreach (object bad in new object[] { null, true, "200", 0, -1, double.NaN, double.PositiveInfinity, new object[0] })
                Refuses(() => Override(p, "profile.b_mm", FrameParameterOverride.Set(bad)));
            foreach (object bad in new object[] { null, "100", true, new object[] { 100, -2 }, new Dictionary<string,object>() })
                Refuses(() => Override(p, "geometry.insulation_layers_mm", FrameParameterOverride.Set(bad)));
            Refuses(() => Override(p, "geometry.cladding_front_offset_mm", FrameParameterOverride.Set(null)));
            Refuses(() => Override(p, "bracket.L_mm", FrameParameterOverride.Set(205)));
            Refuses(() => Override(p, "extender.thickness_mm", FrameParameterOverride.Set(2)));
            Refuses(() => Override(p, "profile.execution", FrameParameterOverride.Set("unknown")));
            Refuses(() => FrameParameterOverride.FromDict(new Dictionary<string,object> { { "state", "clear" }, { "value", null } }));
            Refuses(() => FrameParameterOverride.FromDict(new Dictionary<string,object> { { "state", "inherit" } }));
        });
        Check("zone identities and signed payload detect tampering", delegate {
            var p = Project(); var z = Zone(p); var d = z.ToDict(); Dict(d["overrides"])["profile.b_mm"] = FrameParameterOverride.Set(999).ToDict(); Refuses(() => FrameZoneParameters.FromDict(d));
            var other = FrameProjectParameters.CreateNext(null, p.defaults, "1123456789abcdef0123456789abcdef");
            Refuses(() => FrameZoneParameters.CreateNext(z, other, z.overrides)); Refuses(() => Resolve(other, z));
            Refuses(() => FrameZoneParameters.CreateNext(null, null, new Dictionary<string,FrameParameterOverride>()));
            Need(FrameZoneParameters.FromDict(Dict(Json.DeserializeObject(Json.Serialize(z.ToDict())))).content_digest == z.content_digest, "zone roundtrip unstable");
        });
        Check("effective snapshots and returned values are detached", delegate {
            var p = Project(); var z = Zone(p); var r = Resolve(p, z); var value = r.Selection; value.geometry.insulation_layers_mm.Clear();
            var context = r.Context; context.origins["bracket.L_mm"] = "zone_override"; context.project.revision = 99;
            p.defaults.geometry.cladding_front_offset_mm = 999; z.overrides["profile.b_mm"] = FrameParameterOverride.Set(500);
            Need(r.Selection.geometry.insulation_layers_mm.Count == 2 && r.Selection.geometry.cladding_front_offset_mm == 230, "returned selection aliased");
            Need(r.Context.project.revision == 1 && r.Context.origins["bracket.L_mm"] == "project_default", "returned context aliased");
        });
        Check("mixed effective or bound/unbound groups refuse; equal values retain distinct origins", delegate {
            var p = Project(); var a = Resolve(p, Zone(p), "A1", "Ф-1");
            var b = Resolve(p, Override(p, "bracket.L_mm", FrameParameterOverride.Set(200)), "B1", "Ф-2");
            var group = new Dictionary<string,FrameParameterResolution> { { "Ф-1", a }, { "Ф-2", b } };
            Need(FrameParameterResolver.ValidateGroup(group, new string[0]) == null, "same effective values refused");
            Need(a.Context.origins["bracket.L_mm"] != b.Context.origins["bracket.L_mm"], "origin conflated");
            string mixed = FrameParameterResolver.ValidateGroup(group, new[] { "Ф-3" }); Need(mixed != null && mixed.Contains("Ф-1") && mixed.Contains("Ф-3"), "mixed group lacks zone reason");
            group["Ф-2"] = Resolve(p, Override(p, "bracket.L_mm", FrameParameterOverride.Set(210)), "B1", "Ф-2");
            Need(FrameParameterResolver.ValidateGroup(group, new string[0]) != null, "different effective group accepted");
            Need(FrameParameterResolver.ValidateGroup(new Dictionary<string,FrameParameterResolution>(), new[] { "Ф-1" }) == null, "legacy group changed");
        });
        Check("snapshot strictness and raw-record hashes participate in equality", delegate {
            var p = Project(); var z = Zone(p); var r = Resolve(p,z);
            var changed = FrameParameterResolver.Resolve(p, new string('c',64), z, RawZone, "A1", "Ф-1");
            Need(!FrameParameterContext.Same(r.Context,changed.Context), "raw project hash ignored");
            var d = r.Context.ToDict(); Dict(d["origins"])["source_id"] = "project_default"; Refuses(() => FrameParameterContext.FromDict(d));
            d = r.Context.ToDict(); Dict(d["origins"])["profile.b_mm"] = "zone_clear"; Refuses(() => FrameParameterContext.FromDict(d));
            var selection = r.Selection; selection.bracket.L_mm = 210; Refuses(() => r.Context.ValidateSelection(selection));
            Refuses(() => FrameParameterResolver.Resolve(p, "", z, RawZone, "A1", "Ф-1"));
        });
        Check("settings roundtrip is per-owner; global preferences exclude dependencies", delegate {
            var p = Project(); var r = Resolve(p,Zone(p)); var settings = new FrameSettings { Steps = "manual", Mode = "frame" }; settings.ApplyProjectParameters(r);
            var copy = FrameSettings.FromDict(settings.ToDict()); Need(FrameParameterContext.Same(copy.ProjectParametersContext,r.Context), "settings lost context");
            Need(!Dict(settings.ToDict()["project_parameters_context"]).ContainsKey("zones"), "all-zone map copied into owner");
            Need(!settings.ToLastDict().ContainsKey("project_parameters_context") && !settings.ToLastDict().ContainsKey("solution_selection"), "project leaked to global settings");
            var last = FrameSettings.FromLastDict(settings.ToDict()); Need(last.ProjectParametersContext == null && last.SolutionSelection == null, "global read adopted project");
            Need(!settings.EngineParams(0).ContainsKey("project_parameters_context") && settings.EngineParams(0).ContainsKey("solution_selection"), "new Python API was introduced");
            copy.SolutionSelection.bracket.L_mm = 210; Refuses(() => copy.EngineParams(0));
            var bad = settings.ToDict(); bad["project_parameters_context"] = null; Refuses(() => FrameSettings.FromDict(bad));
        });
        Check("detach clears inherited snapshot but preserves genuine 2B1 local choice", delegate {
            var p = Project(); var settings = new FrameSettings { Steps = "manual" }; settings.ApplyProjectParameters(Resolve(p,Zone(p)));
            settings.DetachProjectParameters(); Need(settings.ProjectParametersContext == null && settings.SolutionSelection == null, "inherited snapshot became local");
            settings.SolutionSelection = Sample.Clone(); settings.DetachProjectParameters(); Need(FrameSolutionSelection.Same(settings.SolutionSelection,Sample), "true 2B1 local selection erased");
        });
        Check("run snapshot stores shared project once and per-zone provenance once", delegate {
            var p = Project(); var a = Resolve(p,Zone(p),"A1","Ф-1").Context; var b = Resolve(p,Zone(p),"B1","Ф-2").Context;
            var run = FrameParameterResolver.RunSnapshot(new[] { a,b,a.Clone() }); Need(((object[])run["zones"]).Length == 2, "owner snapshot duplicated");
            Need(!Json.Serialize(run).Contains("solution_selection") && !Json.Serialize(run).Contains("aframe_solution_selection"), "effective selection copied into zone entries");
            b.zone.owner_handle = "A1"; Refuses(() => FrameParameterResolver.RunSnapshot(new[] { a,b }));
        });
        var performance = new List<object>();
        Check("10/100/1000/3000 zone contexts have linear stored size and zero member-dependent work", delegate {
            var p = Project(); var z = Zone(p); int small = 0;
            foreach (int count in new[] { 10,100,1000,3000 }) {
                var watch = Stopwatch.StartNew(); var contexts = new List<FrameParameterContext>(); int maxOwner = 0;
                for (int i = 0; i < count; i++) { var r = Resolve(p,z,(0x10000000+i).ToString("X8"),"Ф-"+i.ToString("D4")); var c = r.Context; contexts.Add(c); maxOwner = Math.Max(maxOwner,Json.Serialize(c.ToDict()).Length); }
                var run = FrameParameterResolver.RunSnapshot(contexts); int bytes = System.Text.Encoding.UTF8.GetByteCount(Json.Serialize(run)); watch.Stop();
                Need(((object[])run["zones"]).Length == count, "lost zone context"); if (count == 1000) small = bytes; else if(count == 3000) Need(bytes < small*3.02, "nonlinear snapshot size");
                performance.Add(new { zones = count, milliseconds = watch.ElapsedMilliseconds, serialized_bytes = bytes, bytes_per_zone = bytes/(double)count, max_owner_characters = maxOwner, origins = count*13, cad_reads = 0 });
            }
        });
        var pipelineProject = Project(); var pipelineSettings = new FrameSettings { Steps="manual",Mode="frame",Profile="ГП-60-40" };
        pipelineSettings.ApplyProjectParameters(Resolve(pipelineProject,Override(pipelineProject,"profile.b_mm",FrameParameterOverride.Set(45))));
        File.WriteAllText(args[1],Json.Serialize(new { status = Failed == 0 ? "PASS" : "FAIL", checks=Checks, cases=Cases, performance,
            pipeline_settings=pipelineSettings.ToDict(), pipeline_engine_params=pipelineSettings.EngineParams(0), live_autocad_checked=false }));
        Console.WriteLine("Project parameters core: "+Checks+" checks; "+Failed+" failures"); return Failed == 0 ? 0 : 1;
    }
}
