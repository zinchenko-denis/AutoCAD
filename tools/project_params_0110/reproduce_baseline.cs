using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using AFramePlugin;

// Calls actual baseline settings/context and the extracted owner freshness
// method. Only storage entities are doubled; no AutoCAD host is simulated.
internal static class ProjectParametersBaseline
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly List<object> Results = new List<object>();
    private static void Check(string name, string meaning, bool value, object evidence)
    {
        Results.Add(new { name, meaning, reproduced = value, evidence });
        if (!value) throw new InvalidOperationException("Baseline changed: " + name);
    }
    private static Dictionary<string,object> Dict(object value) { return (Dictionary<string,object>)value; }
    public static int Main(string[] args)
    {
        var selected = FrameSolutionSelection.FromDict(Json.DeserializeObject(File.ReadAllText(args[0])));
        selected.geometry.cladding_front_offset_mm = 230;
        selected.geometry.insulation_layers_mm = new List<double> { 100, 50 };
        var first = new FrameSettings { Steps = "manual", Mode = "frame", SolutionSelection = selected.Clone() };
        var zone = first.Clone();
        first.SolutionSelection.geometry.cladding_front_offset_mm = 320;
        Check("independent_snapshots_do_not_inherit", "Settings clone stores a full independent declaration; there is no project dependency.",
            zone.SolutionSelection.geometry.cladding_front_offset_mm == 230, new {
                changed_default_candidate = first.SolutionSelection.geometry.cladding_front_offset_mm,
                prior_zone = zone.SolutionSelection.geometry.cladding_front_offset_mm });

        var context = new FrameSolutionSelectionContext(); context.Add(zone.ToDict());
        string reason = null;
        try { context.Add(first.ToDict()); } catch (FrameSolutionSelectionException e) { reason = e.Message; }
        Check("different_zone_values_refuse_as_different_solutions", "2B1 rejects the mixed selection; no override origin is available.",
            reason != null, new { refusal = reason });

        var serialized = zone.ToDict();
        var keys = serialized.Keys.OrderBy(k => k).ToArray();
        Check("no_project_or_zone_parameter_contract", "Current settings expose a complete selection but no project/zone dependency or origin.",
            !keys.Any(k => k.Contains("project") || k.Contains("override") || k.Contains("origin") || k.Contains("binding")), new { keys });

        var unknown = zone.ToDict();
        unknown["project_parameters"] = new Dictionary<string,object> { { "project_id", "synthetic-project" }, { "revision", 1 } };
        unknown["zone_parameters"] = new Dictionary<string,object> { { "mode", "inherit" } };
        var roundtrip = FrameSettings.FromDict(unknown).ToDict();
        Check("proposed_external_contract_is_not_persisted", "Illustrative future top-level data is ignored, not a supported schema or a legacy bug.",
            !roundtrip.ContainsKey("project_parameters") && !roundtrip.ContainsKey("zone_parameters"), new { after_keys = roundtrip.Keys.OrderBy(k => k).ToArray() });

        var emptyA = zone.Clone(); emptyA.SolutionSelection.geometry.cladding_front_offset_mm = null;
        emptyA.SolutionSelection.geometry.insulation_layers_mm.Clear();
        var emptyB = emptyA.Clone();
        Check("clear_and_inherit_have_no_distinct_encoding", "The same unset geometry cannot encode an explicit clear versus an inherited unset value.",
            FrameSolutionSelection.Same(emptyA.SolutionSelection, emptyB.SolutionSelection), new { geometry = Dict(emptyA.SolutionSelection.ToDict()["geometry"]) });

        var owner = new BaselineEntity { Handle = "A1", Metadata = Json.Serialize(new { owner = "A1", settings = zone.ToDict() }) };
        var transaction = new BaselineTransaction { ProjectRecord = Json.Serialize(new { revision = 1, defaults = zone.ToDict() }) };
        var state1 = ActualFrameOwnerState.Get(transaction, owner);
        transaction.ProjectRecord = Json.Serialize(new { revision = 2, defaults = first.ToDict() });
        var state2 = ActualFrameOwnerState.Get(transaction, owner);
        Check("external_project_change_is_not_a_freshness_dependency", "Actual FrameOwnerState hashes only saved ATFRAME; adding project storage requires an explicit freshness dependency.",
            state1.Ok && state2.Ok && state1.Revision == state2.Revision && transaction.ProjectReads == 0,
            new { before = state1.Revision, after = state2.Revision, owner_reads = transaction.OwnerReads, project_reads = transaction.ProjectReads });

        var cancel = zone.Clone(); cancel.SolutionSelection.geometry.cladding_front_offset_mm = 999;
        Check("existing_cancel_clone_is_isolated", "Existing detached-dialog behavior must be retained.",
            zone.SolutionSelection.geometry.cladding_front_offset_mm == 230, new { stored_value = zone.SolutionSelection.geometry.cladding_front_offset_mm });
        Check("global_convenience_profile_is_not_project_defaults", "2B1 intentionally strips declaration from global convenience settings.",
            !zone.ToLastDict().ContainsKey("solution_selection"), new { global_selection_present = zone.ToLastDict().ContainsKey("solution_selection") });

        File.WriteAllText(args[1], Json.Serialize(new { status = "REPRODUCED", observations = Results, live_autocad_checked = false }));
        return 0;
    }
}
