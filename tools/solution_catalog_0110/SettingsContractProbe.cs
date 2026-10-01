using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using AFramePlugin;
using FacadeSafety;

internal static class SettingsContractProbe
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly List<object> Cases = new List<object>();
    static int Failed;
    static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    static void Check(string name, Action action) { try { action(); Cases.Add(new { name, status = "PASS" }); } catch (Exception e) { Failed++; Cases.Add(new { name, status = "FAIL", reason = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    static void Refuses(Action action) { bool refused = false; try { action(); } catch (FrameSolutionSelectionException) { refused = true; } Need(refused, "expected exact source-selection refusal"); }
    static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
    static FrameSettings Settings(FrameSolutionSelection selection) { return new FrameSettings { Steps = "manual", Profile = "ГП-60-40", SolutionSelection = selection }; }
    public static int Main(string[] args)
    {
        var input = args.Length > 2 ? Dict(Json.DeserializeObject(File.ReadAllText(args[1]))) : null;
        if (args[0] == "--catalog") { File.WriteAllText(args[1], Json.Serialize(FrameSolutionSelection.CatalogSnapshot())); return 0; }
        if (args[0] == "--settings") {
            try {
                var settings = FrameSettings.FromDict(input);
                var validation = settings.Validate(false);
                File.WriteAllText(args[2], Json.Serialize(new { ok = validation == null, error = validation,
                    settings = settings.ToDict(), last = settings.ToLastDict(), engine_params = settings.EngineParams(0), description = settings.Describe(false) }));
            } catch (Exception e) { File.WriteAllText(args[2], Json.Serialize(new { ok = false, error = e.Message })); }
            return 0;
        }
        if (args[0] == "--consume") {
            try {
                var settings = FrameSettings.FromDict(Dict(input["settings"]));
                var response = Dict(input["response"]); var request = Dict(input["request"]);
                settings.SolutionSelection.ValidateEngineReport(response.ContainsKey("solution_report") ? response["solution_report"] : null, settings.ClampsOnly);
                var report = FrameQuantities.BuildReport(response, request, new Dictionary<string, string>());
                File.WriteAllText(args[2], Json.Serialize(new { ok = true, report }));
            } catch (Exception e) { File.WriteAllText(args[2], Json.Serialize(new { ok = false, error = e.Message })); }
            return 0;
        }
        var selected = FrameSolutionSelection.FromDict(input["selection"]);
        var selectedSettings = Settings(selected).ToDict();
        Check("legacy null payload and preset behavior unchanged", delegate {
            var s = new FrameSettings { Steps = "manual", Profile = "ГП-60-40" }; var d = s.EngineParams(0);
            Need(s.SolutionSelection == null && !d.ContainsKey("solution_selection") && (string)Dict(d["system"])["name"] == "Standart" && (string)d["rail_profile"] == "ГП-60-40", "legacy manual behavior changed");
            s.Steps = "calc"; Need((string)Dict(s.EngineParams(0)["system"])["name"] == "Вектор-1", "legacy calculation preset changed");
        });
        Check("explicit payload suppresses legacy profile but preserves it for clearing", delegate {
            var s = Settings(selected.Clone()); var d = s.EngineParams(0);
            Need(s.SysName == "Вектор-1" && (string)Dict(d["system"])["name"] == "Вектор-1" && d["rail_profile"] == null && !d.ContainsKey("calc") && d.ContainsKey("solution_selection"), "ambiguous explicit payload");
            Need(!s.Describe(true).Contains("подбором") && !s.Describe(true).Contains("ГП-60-40"), "legacy profile shown as selected");
            s.SolutionSelection = null; Need((string)s.EngineParams(0)["rail_profile"] == "ГП-60-40", "clear destroyed old profile choice");
        });
        Check("explicit catalog calculation and unsupported scope refuse at settings and payload", delegate {
            var s = Settings(selected.Clone()); s.Steps = "calc";
            Need(s.Validate(true) != null, "window would accept unconfirmed calculation"); Refuses(() => s.EngineParams(0));
            s.Steps = "manual"; s.SubType = "ortho"; Refuses(() => s.EngineParams(0));
            s.SubType = "vertical"; s.Cladding = "composite"; s.Mode = "frame"; Refuses(() => s.EngineParams(0));
        });
        Check("complete metadata roundtrip detached from window and payload", delegate {
            var s = Settings(selected.Clone()); var roundtrip = FrameSettings.FromDict(Dict(Json.DeserializeObject(Json.Serialize(s.ToDict()))));
            Need(FrameSolutionSelection.Same(s.SolutionSelection, roundtrip.SolutionSelection), "metadata identity changed");
            roundtrip.SolutionSelection.bracket.L_mm += 10; Need(s.SolutionSelection.bracket.L_mm != roundtrip.SolutionSelection.bracket.L_mm, "window clone mutated original");
            var d = s.EngineParams(0); Dict(Dict(d["solution_selection"])["bracket"])["L_mm"] = 350;
            Need(s.SolutionSelection.bracket.L_mm != 350, "engine request mutated settings");
        });
        Check("global defaults never transfer project selection", delegate {
            var s = Settings(selected.Clone()); Need(!s.ToLastDict().ContainsKey("solution_selection"), "selection saved globally");
            Need(FrameSettings.FromLastDict(s.ToDict()).SolutionSelection == null, "old global selection imported into new drawing");
            Need(FrameSettings.FromLastDict(s.ToDict()).StepMain == s.StepMain, "ordinary convenience value lost");
        });
        Check("project offset remains distinct from calculation lever arm", delegate {
            var s = Settings(selected.Clone()); s.Offset = 987; s.SolutionSelection.geometry.cladding_front_offset_mm = 444;
            var roundtrip = FrameSettings.FromDict(s.ToDict()); Need(roundtrip.Offset == 987 && roundtrip.SolutionSelection.geometry.cladding_front_offset_mm == 444, "geometric offset overwrote lever arm");
        });
        Check("default selection has no invented product dimensions or execution", delegate {
            var s = FrameSolutionSelection.CreateDefault(); Need(s.bracket.execution == null && s.bracket.L_mm == 0 && s.profile.b_mm == null && s.Validate() != null, "default created ready engineering choice");
        });
        var corruptions = new Action<Dictionary<string, object>>[] {
            d => d["schema"] = "other/1", d => d["catalog_revision"] = "older", d => d["source_sha256"] = new string('a', 64),
            d => d["extra"] = 1, d => d.Remove("geometry"), d => Dict(d["node"])["pdf_page"] = true,
            d => Dict(d["bracket"])["L_mm"] = "200", d => Dict(d["bracket"])["L_mm"] = 205,
            d => Dict(d["bracket"])["nominal_width_mm"] = 90, d => Dict(d["extender"])["thickness_mm"] = 2,
            d => Dict(d["profile"])["b_mm"] = null, d => Dict(d["profile"])["b_mm"] = double.NaN,
            d => Dict(d["profile"])["thickness_mm"] = double.PositiveInfinity, d => Dict(d["profile"])["b_basis"] = "catalog_verified",
            d => Dict(d["geometry"])["cladding_front_offset_mm"] = true, d => Dict(d["geometry"])["insulation_layers_mm"] = new object[] { 100, -1 },
            d => Dict(d["geometry"])["cladding_offset_basis"] = "calculation_lever_arm", d => Dict(d["profile"])["execution"] = null,
        };
        for (int i = 0; i < corruptions.Length; i++) { int n = i; Check("malformed explicit source does not become null " + n, delegate { var d = selected.Clone().ToDict(); corruptions[n](d); Refuses(() => FrameSettings.FromDict(new Dictionary<string, object> { { "solution_selection", d } })); }); }
        Check("independent allowed executions and dimensions never prove assembly", delegate {
            var s = selected.Clone(); s.bracket.nominal_width_mm = 50; s.extender.nominal_width_mm = 85; s.extender.execution = "corrosion_resistant";
            Need(s.Validate() == null, "invented equal-width/equal-material assembly rule");
        });
        Check("homogeneous metadata and legacy/explicit mixed roots", delegate {
            var c = new FrameSolutionSelectionContext(); c.Add(selectedSettings); c.Add(Settings(selected.Clone()).ToDict());
            Need(c.Observations == 2 && FrameSolutionSelection.Same(c.Baseline, selected), "same selection lost");
            Refuses(() => c.Add(null));
            var legacy = new FrameSolutionSelectionContext(); legacy.Add(null); Refuses(() => legacy.Add(selectedSettings));
            var changed = selected.Clone(); changed.bracket.L_mm += 10;
            var other = new FrameSolutionSelectionContext(); other.Add(selectedSettings); Refuses(() => other.Add(Settings(changed).ToDict()));
        });
        Check("clamps retain only exact saved identity", delegate {
            var context = new FrameSolutionSelectionContext(); context.Add(selectedSettings);
            Need(context.ValidateClamps(selected.Clone()) == null, "unchanged catalog clamps refused");
            Need(context.ValidateClamps(null) != null, "clamps cleared saved frame declaration");
            var changed = selected.Clone(); changed.bracket.L_mm += 10; Need(context.ValidateClamps(changed) != null, "clamps reassigned saved frame");
            var legacy = new FrameSolutionSelectionContext(); legacy.Add(null); Need(legacy.ValidateClamps(selected) != null, "new catalog assigned to legacy rails");
        });
        Check("actual engine outer plus opening does not inherit onto another outer", delegate {
            var scope = new FrameSolutionSelectionScope(); scope.Add("контур A", selectedSettings, true); scope.Add("контур H", null, true);
            var roots = FrameSolutionSelectionScope.EngineRoots(input["hole_per_zone"], new Dictionary<string, string>());
            Need(FrameSolutionSelection.Same(scope.Resolve(roots).Baseline, selected), "opening treated as new facade zone");
            scope.Add("контур B", null, true);
            var disjoint = FrameSolutionSelectionScope.EngineRoots(input["disjoint_per_zone"], new Dictionary<string, string>());
            Refuses(() => scope.Resolve(disjoint));
        });
        Check("same physical zone restores empty alias from verified canonical before form", delegate {
            var scope = new FrameSolutionSelectionScope(); scope.AddAlias("Ф-1", null);
            scope.AddCanonical("Ф-1", selectedSettings);
            Need(FrameSolutionSelection.Same(scope.Baseline, selected), "mark without ATFRAME masked canonical catalog");
            Need(FrameSolutionSelection.Same(scope.Resolve(new[] { "Ф-1" }).Baseline, selected), "canonical identity not resolved");
            var reverse = new FrameSolutionSelectionScope(); reverse.AddCanonical("Ф-1", selectedSettings); reverse.AddAlias("Ф-1", null);
            Need(FrameSolutionSelection.Same(reverse.Baseline, selected), "alias/canonical selection order changed result");
        });
        Check("canonical window restores complete first explicit settings over legacy alias", delegate {
            var legacy = new FrameSettings { Steps = "calc", Height = 91, StepMain = 1111, Offset = 777 }.ToDict();
            var canonical = Settings(selected.Clone()); canonical.Height = 23; canonical.StepMain = 725; canonical.Offset = 245;
            bool chosen = false;
            var first = FrameSolutionSelectionScope.PreferCanonicalWindowSettings(legacy, canonical.ToDict(), ref chosen);
            var restored = FrameSettings.FromDict(first);
            Need(chosen && restored.Manual && restored.Height == 23 && restored.StepMain == 725 && restored.Offset == 245 &&
                FrameSolutionSelection.Same(restored.SolutionSelection, selected), "explicit canonical kept stale alias settings");
            var later = Settings(selected.Clone()); later.Height = 61; later.StepMain = 950;
            var retained = FrameSolutionSelectionScope.PreferCanonicalWindowSettings(first, later.ToDict(), ref chosen);
            Need(Object.ReferenceEquals(first, retained) && FrameSettings.FromDict(retained).StepMain == 725,
                "later canonical silently replaced first canonical convenience values");
            chosen = false;
            Need(Object.ReferenceEquals(legacy, FrameSolutionSelectionScope.PreferCanonicalWindowSettings(legacy, null, ref chosen)) && !chosen,
                "absent canonical replaced legacy convenience settings");
        });
        Check("canonical aliases do not conceal conflicting or genuinely new zones", delegate {
            var changed = selected.Clone(); changed.bracket.L_mm += 10;
            Refuses(() => { var scope = new FrameSolutionSelectionScope(); scope.AddAlias("Ф-1", Settings(changed).ToDict()); scope.AddCanonical("Ф-1", selectedSettings); var unused = scope.Baseline; });
            Refuses(() => { var scope = new FrameSolutionSelectionScope(); scope.AddAlias("Ф-1", selectedSettings); scope.AddCanonical("Ф-1", null); var unused = scope.Baseline; });
            Refuses(() => { var scope = new FrameSolutionSelectionScope(); scope.AddCanonical("Ф-1", selectedSettings); scope.AddCanonical("Ф-2", null); var unused = scope.Baseline; });
        });
        Check("clamps refuse excluded previous root before any metadata rewrite", delegate {
            var scope = new FrameSolutionSelectionScope(); scope.Add("контур A", selectedSettings, true); scope.Add("контур H", new FrameSettings().ToDict(), true);
            var roots = FrameSolutionSelectionScope.EngineRoots(input["hole_per_zone"], new Dictionary<string, string>());
            Need(scope.Resolve(roots).ValidateClamps(selected) == null, "test fixture should preserve actual outer selection");
            FrameSolutionSelectionScope.ValidateClampsRoots(roots, new[] { "контур A" });
            Refuses(() => FrameSolutionSelectionScope.ValidateClampsRoots(roots, new[] { "контур A", "контур H" }));
        });
        Check("actual command clamps gate blocks metadata rewrite of excluded hole", delegate {
            var scope = new FrameSolutionSelectionScope(); scope.Add("контур A", selectedSettings, true); scope.Add("контур H", new FrameSettings().ToDict(), true);
            var settings = Settings(selected.Clone()); settings.Mode = "clamps";
            var response = Dict(input["clamps_guard_response"]);
            var old = new Dictionary<string, List<string>> { { "контур A", new List<string> { "old-rail" } } };
            FrameGuardBridge.SolutionGate(settings, response, scope, old);
            old["контур H"] = new List<string> { "old-hole-rail" };
            Refuses(() => FrameGuardBridge.SolutionGate(settings, response, scope, old));
        });
        Check("manual axis bounds keep selected contours after verified zone dedup", delegate {
            var raw = new Dictionary<string, Dictionary<string, object>> {
                { "A", new Dictionary<string, object> { { "pts", new List<object> { new double[] { -700, 100 }, new double[] { 3300, 100 }, new double[] { 3300, 4100 }, new double[] { -700, 4100 } } } } },
                { "H", new Dictionary<string, object> { { "pts", new List<object> { new double[] { 300, 500 }, new double[] { 1300, 500 }, new double[] { 1300, 1800 }, new double[] { 300, 1800 } } } } }
            };
            var bounds = FrameGuardBridge.AxisBoundsAfterZoneDedup(raw);
            Need(raw.Count == 0 && bounds.SequenceEqual(new double[] { -700, 3300, 100, 4100 }), "moving geometry verification before form lost manual axis extent");
        });
        Check("actual prewrite canonical guard allows empty alias but rejects changed and deleted canonical", delegate {
            var tr = new AFramePlugin.Transaction();
            tr.Entities[1] = new AFramePlugin.Entity { Json = null };
            tr.Entities[2] = new AFramePlugin.Entity { Json = Json.Serialize(new { settings = selectedSettings }) };
            tr.Entities[999] = new AFramePlugin.Entity { Json = "{unrelated corrupt" };
            var owners = new Dictionary<int, Tuple<string, bool>> { { 1, Tuple.Create("Ф-1", false) }, { 2, Tuple.Create("Ф-1", false) } };
            var canonical = new HashSet<int> { 2 };
            var baseline = new FrameSolutionSelectionContext(); baseline.Add(selectedSettings);
            FrameGuardBridge.Fresh(tr, Json, owners, canonical, new List<string> { "Ф-1" }, baseline);
            Need(tr.Reads == 2 && tr.ModelSpaceScans == 0, "canonical guard scanned unrelated drawing");
            var changed = selected.Clone(); changed.bracket.L_mm += 10;
            tr.Entities[2].Json = Json.Serialize(new { settings = Settings(changed).ToDict() });
            Refuses(() => FrameGuardBridge.Fresh(tr, Json, owners, canonical, new List<string> { "Ф-1" }, baseline));
            tr.Entities[2].Json = Json.Serialize(new { settings = selectedSettings }); tr.Entities[2].IsErased = true;
            Refuses(() => FrameGuardBridge.Fresh(tr, Json, owners, canonical, new List<string> { "Ф-1" }, baseline));
            Need(tr.ModelSpaceScans == 0, "failure fallback scanned ModelSpace");
        });
        Check("actual metadata reader rejects unknown/corrupt explicit metadata", delegate {
            var tr = new AFramePlugin.Transaction(); var entity = new AFramePlugin.Entity { Json = Json.Serialize(new { settings = selectedSettings }) };
            Need(FrameSolutionSelection.Same(FrameSettings.FromDict(FrameGuardBridge.Read(tr, Json, entity)).SolutionSelection, selected), "valid metadata not read");
            entity.Json = "{broken"; Refuses(() => FrameGuardBridge.Read(tr, Json, entity));
            entity.Json = Json.Serialize(new { settings = new { solution_selection = "broken" } }); Refuses(() => FrameGuardBridge.Read(tr, Json, entity));
        });
        Check("actual Xrecord reader distinguishes absent from empty corrupt metadata", delegate {
            var tr = new AFramePlugin.Transaction();
            Need(FrameGuardBridge.Read(tr, Json, new AFramePlugin.Entity()) == null, "genuinely absent legacy metadata refused");
            Refuses(() => FrameGuardBridge.Read(tr, Json, new AFramePlugin.Entity { FrameRecordExists = true, NullRecordData = true }));
            Refuses(() => FrameGuardBridge.Read(tr, Json, new AFramePlugin.Entity { FrameRecordExists = true, Json = "" }));
            Refuses(() => FrameGuardBridge.Read(tr, Json, new AFramePlugin.Entity { FrameRecordExists = true, OnlyNonTextData = true }));
        });
        Check("actual Xrecord buffer is read once and disposed on success and refusal", delegate {
            var tr = new AFramePlugin.Transaction();
            var valid = new AFramePlugin.Entity { Json = Json.Serialize(new { settings = selectedSettings }) };
            FrameGuardBridge.Read(tr, Json, valid);
            Need(valid.DataReads == 1 && valid.DataDisposals == 1, "valid ResultBuffer not released exactly once");
            foreach (var corrupt in new[] {
                new AFramePlugin.Entity { FrameRecordExists = true, Json = "" },
                new AFramePlugin.Entity { FrameRecordExists = true, OnlyNonTextData = true },
                new AFramePlugin.Entity { Json = "{broken" } }) {
                Refuses(() => FrameGuardBridge.Read(tr, Json, corrupt));
                Need(corrupt.DataReads == 1 && corrupt.DataDisposals == 1, "refused ResultBuffer leaked or re-read");
            }
            var absentData = new AFramePlugin.Entity { FrameRecordExists = true, NullRecordData = true };
            Refuses(() => FrameGuardBridge.Read(tr, Json, absentData));
            Need(absentData.DataReads == 1 && absentData.DataDisposals == 0, "null ResultBuffer access changed");
        });
        Check("actual prewrite freshness reads selected owners only and rejects changed source", delegate {
            var tr = new AFramePlugin.Transaction(); tr.Entities[1] = new AFramePlugin.Entity { Json = Json.Serialize(new { settings = selectedSettings }) };
            tr.Entities[2] = new AFramePlugin.Entity { Json = null }; tr.Entities[999] = new AFramePlugin.Entity { Json = "{unrelated corrupt" };
            var owners = new Dictionary<int, Tuple<string, bool>> { { 1, Tuple.Create("контур A", true) }, { 2, Tuple.Create("контур H", true) } };
            var baseline = new FrameSolutionSelectionContext(); baseline.Add(selectedSettings);
            FrameGuardBridge.Fresh(tr, Json, owners, new HashSet<int>(), new List<string> { "контур A" }, baseline);
            Need(tr.Reads == 2 && tr.ModelSpaceScans == 0, "freshness scanned unrelated drawing");
            var changed = selected.Clone(); changed.bracket.L_mm += 10;
            tr.Entities[1].Json = Json.Serialize(new { settings = Settings(changed).ToDict() });
            Refuses(() => FrameGuardBridge.Fresh(tr, Json, owners, new HashSet<int>(), new List<string> { "контур A" }, baseline));
            Need(tr.Reads == 4 && tr.ModelSpaceScans == 0, "retry reads became global scan");
        });
        var performance = new List<object>();
        foreach (int count in new[] { 100, 1000 }) { int n = count; Check("selection-context operation scaling " + n, delegate {
            var watch = Stopwatch.StartNew(); var scope = new FrameSolutionSelectionScope(); var roots = new List<string>();
            for (int i = 0; i < n; i++) { string root = "Z" + i; roots.Add(root); scope.Add(root, selectedSettings, false); }
            var resolved = scope.Resolve(roots); watch.Stop();
            Need(scope.Observations == n && resolved.Observations == n, "source observation growth is not linear");
            performance.Add(new { owners = n, capture_observations = scope.Observations, resolve_observations = resolved.Observations,
                elapsed_ms = watch.Elapsed.TotalMilliseconds, cad_calls = 0, modelspace_scans = 0 });
        }); }
        foreach (int count in new[] { 100, 1000 }) { int n = count; Check("same-root alias canonical comparisons scale linearly " + n, delegate {
            var watch = Stopwatch.StartNew(); var scope = new FrameSolutionSelectionScope();
            for (int i = 0; i < n; i++) scope.AddAlias("merged-root", selectedSettings);
            for (int i = 0; i < n; i++) scope.AddCanonical("merged-root", selectedSettings);
            var resolved = scope.Resolve(new[] { "merged-root" }); watch.Stop();
            Need(scope.Observations == 2 * n && scope.AliasComparisons == 2 * n - 1, "same-root alias/canonical matching became quadratic");
            Need(resolved.Observations == 1 && FrameSolutionSelection.Same(resolved.Baseline, selected), "repeated aliases lost canonical identity");
            performance.Add(new { scenario = "same_root_aliases_then_canonicals", aliases = n, canonical_observations = n,
                capture_observations = scope.Observations, alias_comparisons = scope.AliasComparisons,
                resolve_observations = resolved.Observations, elapsed_ms = watch.Elapsed.TotalMilliseconds,
                cad_calls = 0, modelspace_scans = 0 });
        }); }
        File.WriteAllText(args[2], Json.Serialize(new { status = Failed == 0 ? "PASS" : "FAIL", checks = Cases.Count, cases = Cases, performance, live_autocad_checked = false }));
        Console.WriteLine("Solution settings/guard native checks: " + Cases.Count + (Failed == 0 ? " PASS" : " FAIL"));
        return Failed == 0 ? 0 : 1;
    }
}

namespace AFramePlugin
{
    internal enum OpenMode { ForRead }
    internal enum DxfCode { Text = 1 }
    internal sealed class FakeObjectId {
        internal Entity Owner; internal int Kind;
        internal bool IsNull { get { return Kind == 0; } }
    }
    internal sealed class TypedValue { internal int TypeCode; internal object Value; }
    internal sealed class Entity {
        internal string Json; internal bool IsErased, FrameRecordExists, NullRecordData, OnlyNonTextData;
        internal int DataReads, DataDisposals;
        internal FakeObjectId ExtensionDictionary { get { return new FakeObjectId { Owner = this, Kind = Json != null || FrameRecordExists ? 1 : 0 }; } }
    }
    internal sealed class DBDictionary {
        internal Entity Owner;
        internal bool Contains(string key) { return key == "ATFRAME" && (Owner.Json != null || Owner.FrameRecordExists); }
        internal FakeObjectId GetAt(string key) { return new FakeObjectId { Owner = Owner, Kind = 2 }; }
    }
    internal sealed class ResultBuffer : IEnumerable<TypedValue>, IDisposable {
        internal Entity Owner; internal TypedValue[] Values;
        public IEnumerator<TypedValue> GetEnumerator() { return ((IEnumerable<TypedValue>)Values).GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return Values.GetEnumerator(); }
        public void Dispose() { Owner.DataDisposals++; }
    }
    internal sealed class Xrecord {
        internal Entity Owner;
        internal ResultBuffer Data { get {
            Owner.DataReads++;
            return Owner.NullRecordData ? null : new ResultBuffer { Owner = Owner, Values = new[] {
                new TypedValue { TypeCode = Owner.OnlyNonTextData ? 999 : (int)DxfCode.Text, Value = Owner.Json } } };
        } }
    }
    internal sealed class Transaction {
        internal int Reads, MetadataReads, ModelSpaceScans;
        internal readonly Dictionary<int, Entity> Entities = new Dictionary<int, Entity>();
        internal object GetObject(int id, OpenMode mode) { Reads++; Entity entity; return Entities.TryGetValue(id, out entity) ? entity : null; }
        internal object GetObject(FakeObjectId id, OpenMode mode) {
            MetadataReads++;
            if (id.Kind == 1) return new DBDictionary { Owner = id.Owner };
            if (id.Kind == 2) return new Xrecord { Owner = id.Owner };
            return null;
        }
    }
}
