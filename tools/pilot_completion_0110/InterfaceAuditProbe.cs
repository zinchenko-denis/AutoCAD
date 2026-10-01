using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using AFramePlugin;
using AFacadesPlugin;
using FacadeSafety;

// Acceptance evidence only. Actual settings/producer/core/table, with explicit
// CAD identity doubles; this does not execute a WinForms click or an AutoCAD host.
internal static class InterfaceAuditProbe
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static Dictionary<string, object> Object(object value) { return (Dictionary<string, object>)value; }
    private static string[] Properties(Type type) { return type.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(); }
    private static object Settings(Dictionary<string, object> input)
    {
        var fs = new FrameSettings { Steps = "manual", Mode = "frame", SubType = "vertical", Cladding = "porcelain",
            Profile = "ГП-40-40", StepMain = 800, StepCorner = 800, RailGap = 10, Axes = "points" };
        if (input.ContainsKey("selection") && input["selection"] != null)
            fs.SolutionSelection = FrameSolutionSelection.FromDict(input["selection"]);
        if (input.ContainsKey("step_main")) fs.StepMain = Convert.ToDouble(input["step_main"]);
        if (input.ContainsKey("step_corner")) fs.StepCorner = Convert.ToDouble(input["step_corner"]);
        if (input.ContainsKey("steps")) fs.Steps = (string)input["steps"];
        string valid = fs.Validate(false);
        Dictionary<string, object> parameters = null;
        string engineError = null;
        try { parameters = fs.EngineParams(1); }
        catch (FrameSolutionSelectionException error) { engineError = error.Message; }
        var settings = fs.ToDict();
        var restored = FrameSettings.FromDict(Json.DeserializeObject(Json.Serialize(settings)) as Dictionary<string, object>);
        bool? roundtrip = parameters == null ? (bool?)null : Json.Serialize(restored.EngineParams(1)) == Json.Serialize(parameters);
        return new { name = input["name"], validation_error = valid, engine_error = engineError,
            settings, parameters, system_override = fs.SysOverride(), roundtrip,
            description = fs.Describe(false), has_layout = false, floors_picked = 1 };
    }
    private static object Inspect(QuantityReport report, Dictionary<string, double> milliseconds)
    {
        var watch = Stopwatch.StartNew();
        var result = FacadeQuantitiesCore.BuildRows(new[] { report }, report.zone_ids, true, true);
        milliseconds["core"] = watch.Elapsed.TotalMilliseconds;
        if (!result.ok) return new { core_ok = false, issues = result.issues, report, milliseconds };
        string before = Json.Serialize(report);
        watch.Restart();
        var table = ConnectionTableData.Build(result, new[] { report }, report.zone_ids, null, true);
        milliseconds["table"] = watch.Elapsed.TotalMilliseconds;
        return new { core_ok = true, report, source_unchanged = before == Json.Serialize(report),
            quantity_rows = result.rows, quantity_issues = result.issues,
            table_headers = ConnectionTableData.Headers, table_members = table.Members, table_messages = table.Messages,
            coverage = ConnectionTableData.Coverage, milliseconds,
            actual_dto_properties = new { passport = Properties(typeof(QuantityConnectionPassport)),
                member = Properties(typeof(QuantityConnectionMember)), support = Properties(typeof(QuantityConnectionSupport)),
                element = Properties(typeof(QuantityElement)) } };
    }
    private static object Run(Dictionary<string, object> input)
    {
        var times = new Dictionary<string, double>();
        var watch = Stopwatch.StartNew();
        var settings = FrameSettings.FromDict(Object(input["settings"]));
        var response = Object(input["response"]);
        var request = Object(input["request"]);
        string responseBefore = Json.Serialize(response), requestBefore = Json.Serialize(request);
        var parameters = settings.EngineParams(1);
        bool parametersExact = parameters.All(p => request.ContainsKey(p.Key) && Json.Serialize(request[p.Key]) == Json.Serialize(p.Value));
        if (settings.SolutionSelection != null)
            settings.SolutionSelection.ValidateEngineReport(response.ContainsKey("solution_report") ? response["solution_report"] : null, false);
        times["solution_response_validation"] = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var report = FrameQuantities.BuildReport(response, request, new Dictionary<string, string>());
        times["producer"] = watch.Elapsed.TotalMilliseconds;
        report.document_id = "pilot-interface-audit-cad-doubles";
        for (int i = 0; i < report.elements.Count; ++i)
            report.elements[i].cad_entities.Add(new QuantityCadEntity {
                handle = (i + 1).ToString("X"), role = "primary", fingerprint = "fixture:" + i });
        var observed = Object(Json.DeserializeObject(Json.Serialize(Inspect(report, times))));
        observed["response_unchanged"] = responseBefore == Json.Serialize(response);
        observed["request_unchanged"] = requestBefore == Json.Serialize(request);
        observed["native_parameters_exact"] = parametersExact;
        return observed;
    }
    public static int Main(string[] args)
    {
        object output;
        try
        {
            var value = Json.DeserializeObject(File.ReadAllText(args[1]));
            if (args[0] == "--settings") output = ((object[])value).Select(v => Settings(Object(v))).ToArray();
            else if (args[0] == "--run") output = Run(Object(value));
            else if (args[0] == "--inspect") output = Inspect(Json.Deserialize<QuantityReport>(Json.Serialize(value)), new Dictionary<string, double>());
            else throw new ArgumentException("Unknown probe mode");
        }
        catch (Exception error) { output = new { rejected = true, error = error.Message, exception_type = error.GetType().Name }; }
        File.WriteAllText(args[2], Json.Serialize(output));
        return 0;
    }
}
