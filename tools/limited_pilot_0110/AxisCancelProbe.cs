using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using AFramePlugin;

// Explicit prompt doubles and an unmodified extracted command segment.
// This is not FrameCommand.Run, an AutoCAD host, or a transaction rollback test.
internal enum PromptStatus { OK, None, Cancel }
internal sealed class PromptPointOptions
{
    internal string Message;
    internal bool AllowNone;
    internal PromptPointOptions(string message) { Message = message; }
}
internal sealed class PromptPointResult
{
    internal PromptStatus Status;
    internal Point Value = new Point();
    internal sealed class Point { internal double X; }
}
internal sealed class PromptEditor
{
    internal readonly Queue<PromptPointResult> Results = new Queue<PromptPointResult>();
    internal readonly List<string> Messages = new List<string>();
    internal readonly List<object> Prompts = new List<object>();
    internal PromptPointResult GetPoint(PromptPointOptions options)
    {
        Prompts.Add(new { message = options.Message, allow_none = options.AllowNone });
        if (Results.Count == 0) throw new InvalidOperationException("Unexpected extra prompt");
        return Results.Dequeue();
    }
    internal void WriteMessage(string message) { Messages.Add(message); }
}
internal static class AxisCancelProbe
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static string F0(double value) { return value.ToString("0", CultureInfo.InvariantCulture); }
    private static void Segment(FrameSettings fs, PromptEditor ed, List<double> joints,
        List<double> rowsY, List<Dictionary<string, object>> axisInputContours, Action continuation)
    {
/* ACTUAL_AXIS_SEGMENT */
        continuation();
    }
    public static int Main(string[] args)
    {
        var input = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(args[0]));
        var fs = new FrameSettings { Steps = "manual", Mode = "frame", SubType = "vertical", Cladding = "porcelain",
            Profile = "ГП-40-40", StepMain = 800, StepCorner = 800, RailGap = 10, Axes = "points",
            AskCorners = false, AskFloors = false, Signs = "cond" };
        fs.SolutionSelection = FrameSolutionSelection.FromDict(input["selection"]);
        var stored = fs.ToDict();
        fs = FrameSettings.FromDict((Dictionary<string, object>)Json.DeserializeObject(Json.Serialize(stored)));
        string valid = fs.Validate(false);
        if (valid != null) throw new InvalidOperationException(valid);
        var pts = new List<object> { new double[] { 0, 0 }, new double[] { 1200, 0 },
            new double[] { 1200, 3000 }, new double[] { 0, 3000 } };
        var contours = new List<Dictionary<string, object>> { new Dictionary<string, object> { { "pts", pts } } };
        var results = new List<object>();
        foreach (Dictionary<string, object> scenario in (object[])input["scenarios"])
        {
            var ed = new PromptEditor();
            foreach (Dictionary<string, object> point in (object[])scenario["points"])
                ed.Results.Enqueue(new PromptPointResult { Status = (PromptStatus)Enum.Parse(typeof(PromptStatus), (string)point["status"]),
                    Value = new PromptPointResult.Point { X = Convert.ToDouble(point["x"], CultureInfo.InvariantCulture) } });
            var joints = new List<double>(); var rows = new List<double>();
            bool continued = false;
            Segment(fs, ed, joints, rows, contours, delegate { continued = true; });
            var rowsAfterAxes = new List<double>(rows);
            Dictionary<string, object> request = null;
            if (continued)
            {
                // The real command clears cladding rows in frame-only mode
                // before building its payload. Execute those exact lines too.
                var rowsY = rows;
/* ACTUAL_ROWS_MODE_SEGMENT */
                // Only the test harness reaches the real engine via this DTO.
                // Later source capture, CAD prewrite and drawing are not simulated.
                request = fs.EngineParams(0);
                request["op"] = "frame"; request["joints_x"] = joints;
                request["floors_y"] = new object[0]; request["rows_y"] = rowsY;
                request["zones"] = new object[] { new Dictionary<string, object> {
                    { "zone_id", "Z0" }, { "zone", new Dictionary<string, object> {
                        { "contour", contours[0] }, { "openings", new object[0] } } } } };
            }
            results.Add(new { name = scenario["name"], continued, joints, rows, rows_after_axes = rowsAfterAxes, request,
                remaining_prompts = ed.Results.Count, messages = ed.Messages, prompts = ed.Prompts });
        }
        File.WriteAllText(args[1], Json.Serialize(new { validation_error = valid,
            settings = stored, settings_roundtrip = Json.Serialize(stored) == Json.Serialize(fs.ToDict()), results }));
        return 0;
    }
}
