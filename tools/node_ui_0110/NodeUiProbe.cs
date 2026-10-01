using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AFramePlugin;

internal static class NodeUiProbe
{
    private static int checks, failures;
    private static string output;
    private static readonly List<string> images = new List<string>(), reasons = new List<string>();
    private static readonly List<object> layouts = new List<object>();
    private static readonly List<object> numericDiagnostics = new List<object>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    { internal int Left, Top, Right, Bottom; internal Rectangle Bounds { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } } }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    private static Rectangle NativeWindow(Control control)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return control is Form ? control.Bounds : control.RectangleToScreen(control.ClientRectangle);
        NativeRect rect; if (!GetWindowRect(control.Handle, out rect)) throw new Exception("GetWindowRect failed"); return rect.Bounds;
    }
    private static Rectangle NativeClient(Form form)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return form.RectangleToScreen(form.ClientRectangle);
        NativeRect rect; var point = new NativePoint();
        if (!GetClientRect(form.Handle, out rect) || !ClientToScreen(form.Handle, ref point)) throw new Exception("Native client query failed");
        return new Rectangle(point.X, point.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    private static void Check(bool value, string reason)
    { checks++; if (!value) { failures++; reasons.Add(reason); Console.WriteLine("FAIL " + reason); } }
    private static T Find<T>(Control owner, string name) where T : Control
    {
        var found = owner.Controls.Find(name, true);
        if (found.Length != 1 || !(found[0] is T)) throw new Exception("Control not found uniquely: " + name);
        return (T)found[0];
    }
    private static IEnumerable<Control> All(Control parent)
    { foreach (Control item in parent.Controls) { yield return item; foreach (var child in All(item)) yield return child; } }
    private static void Show(Form form)
    { form.StartPosition = FormStartPosition.Manual; form.Location = new Point(10, 10); form.Show(); Application.DoEvents(); }
    private static void Click(Form form, string name) { Find<Button>(form, name).PerformClick(); Application.DoEvents(); }
    private static void Surface(Form form, int index) { Find<ComboBox>(form, "clearance_surface").SelectedIndex = index; }
    private static void Profile(Form form, string value) { Find<TextBox>(form, "profile_near_x").Text = value; }
    private static string Review(Form form) { return Find<TextBox>(form, "node_review").Text; }
    private static void MountingReview(FrameNodeForm form, FrameSolutionSelection selection)
    {
        var expected = FrameMountingAssessment.Evaluate(selection, form.Geometry);
        var box = Find<TextBox>(form, "node_review");
        string normalized = expected.ReviewText().Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        Check(box.Text.EndsWith(normalized, StringComparison.Ordinal), "Native review omitted or changed the mounting report for the current selection");
        Check(box.Lines.Any(line => line.StartsWith("Монтажная пригодность: не подтверждена.")), "Native review merges or hides separate mounting status line");
        Check(expected.Dependencies.Count == 7 && expected.DeclaredMembers.Count == 3 && !expected.AutomaticBracketSelectionAllowed,
            "Mounting review lost its seven data requests/three declared members or allowed automatic selection");
        Check(box.Lines.Any(line => line.StartsWith("КР2: ") && line.Contains("лист 3.2.1, PDF 6")) &&
            box.Lines.Any(line => line.StartsWith("УК: ") && line.Contains("лист 3.3, PDF 8")) &&
            box.Lines.Any(line => line.StartsWith("ГП: ") && line.Contains("лист 3.4, PDF 9")), "Declared members lost their individual source locators");
    }
    private static void ScrollReviewTo(FrameNodeForm form, string text)
    {
        var box = Find<TextBox>(form, "node_review"); int index = box.Text.IndexOf(text, StringComparison.Ordinal);
        Check(index >= 0 && box.Visible && box.ReadOnly && box.ScrollBars == ScrollBars.Vertical, "Mounting section is not available for read-only vertical review: " + text);
        if (index < 0) return;
        // Visit the end first, then return to the requested section. This makes
        // its beginning visible without relying on Windows-only scroll messages.
        box.Focus(); box.Select(box.TextLength, 0); box.ScrollToCaret();
        box.Select(index, 0); box.ScrollToCaret(); Application.DoEvents();
        Point position = box.GetPositionFromCharIndex(index);
        Check(position.Y >= 0 && position.Y + box.Font.Height <= box.ClientSize.Height, "Mounting section start cannot be scrolled into view: " + text);
    }
    private static object Numeric(double? value)
    { return value.HasValue ? new { roundtrip = value.Value.ToString("R", CultureInfo.InvariantCulture),
        ieee754_hex = BitConverter.DoubleToInt64Bits(value.Value).ToString("X16", CultureInfo.InvariantCulture) } : null; }
    private static void NumericDiagnostic(string scenario, double expected, double cloned, double direct, double? viewed, FrameNodeForm form)
    {
        var entry = new { scenario, expected = Numeric(expected), typed_clone = Numeric(cloned),
            direct_evaluation = Numeric(direct), form_evaluation = Numeric(viewed),
            form_has_geometry = form.Geometry != null, form_status = Find<TextBox>(form, "node_status").Text };
        numericDiagnostics.Add(entry); Console.WriteLine("NUMERIC " + Json.Serialize(entry));
    }
    private static void Layout(FrameNodeForm form, string name)
    {
        form.PerformLayout(); Application.DoEvents();
        Rectangle client = NativeClient(form), window = NativeWindow(form), work = Screen.FromControl(form).WorkingArea;
        Check(client.Size == form.ClientSize, name + ": native/cached client size mismatch");
        Check(work.Contains(window), name + ": actual HWND outside working area " + window + "/" + work);
        foreach (var control in All(form).OfType<Button>().Where(c => c.Visible && c.Name.EndsWith("_node")))
        {
            Rectangle button = NativeWindow(control);
            Check(client.Contains(button) && work.Contains(button), name + ": footer action clipped " + control.Name);
            Check(TextRenderer.MeasureText(control.Text, control.Font).Width + 8 <= control.Width, name + ": footer text clipped " + control.Name);
        }
        foreach (var grid in All(form).OfType<TableLayoutPanel>().Where(c => c.Visible))
        {
            var controls = grid.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (var child in controls)
                Check(child.Left >= 0 && child.Top >= 0 && child.Right <= grid.ClientSize.Width && child.Bottom <= grid.ClientSize.Height,
                    name + ": child exceeds table " + child.Name + " " + child.Bounds + "/" + grid.ClientRectangle);
            for (int i = 0; i < controls.Count; i++) for (int j = i + 1; j < controls.Count; j++)
                Check(!controls[i].Bounds.IntersectsWith(controls[j].Bounds), name + ": overlapping controls " + controls[i].Name + "/" + controls[j].Name);
        }
        var scroll = Find<Panel>(form, "node_scroll");
        Check(scroll.AutoScroll && !scroll.HorizontalScroll.Visible, name + ": body cannot scroll or exceeds width");
        foreach (var label in All(form).OfType<Label>().Where(c => c.Visible))
        {
            int height = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            Check(label.Height >= height, name + ": explanatory text clipped " + label.Name);
        }
        Check(Find<TextBox>(form, "node_project_parameters").ReadOnly && Find<TextBox>(form, "node_source").ReadOnly &&
            Find<TextBox>(form, "node_review").ReadOnly, name + ": inherited or calculated data editable");
        layouts.Add(new { scenario = name, window = window.ToString(), native_client = client.ToString(), cached_client = form.ClientRectangle.ToString(),
            screen = work.ToString(), font_points = form.Font.SizeInPoints, minimum_size = form.MinimumSize.ToString() });
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, name + ".png")); }
        images.Add(name + ".png");
    }
    // Explicit synthetic input, not recommended dimensions for a real project.
    private static FrameParameterResolution Fixture(double[] layers, double? cladding, int bracketLength = 200, int extenderLength = 100)
    {
        var selection = FrameSolutionSelection.CreateDefault();
        selection.bracket.execution = selection.extender.execution = selection.profile.execution = "galvanized_painted";
        selection.bracket.nominal_width_mm = selection.extender.nominal_width_mm = 70;
        selection.bracket.L_mm = bracketLength; selection.extender.L_mm = extenderLength; selection.extender.thickness_mm = 1.2;
        selection.profile.a_mm = 40; selection.profile.b_mm = 47.5; selection.profile.thickness_mm = 1.5;
        selection.geometry.cladding_front_offset_mm = cladding; selection.geometry.insulation_layers_mm = new List<double>(layers);
        var project = FrameProjectParameters.CreateNext(null, selection, "11111111111111111111111111111111");
        var zone = FrameZoneParameters.CreateNext(null, project, new Dictionary<string, FrameParameterOverride>());
        return FrameParameterResolver.Resolve(project, new string('1', 64), zone, new string('2', 64), "A1", "Учебная A");
    }
    private static FrameNodeForm Form(FrameParameterResolution resolution)
    { return new FrameNodeForm(resolution.Selection, resolution.Context); }
    [STAThread]
    public static int Main(string[] args)
    {
        output = args[0]; Directory.CreateDirectory(output);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var standard = Fixture(new[] { 100.0, 50.0 }, 230);
        var callerSelection = standard.Selection; var callerContext = standard.Context;
        string originalSelection = Json.Serialize(callerSelection.ToDict()), originalContext = Json.Serialize(callerContext.ToDict());
        using (var form = new FrameNodeForm(callerSelection, callerContext))
        {
            Show(form);
            Check(Find<TextBox>(form, "profile_near_x").Text == "" && Find<TextBox>(form, "membrane_outer_x").Text == "", "Hidden default coordinates assigned");
            Check(Find<ComboBox>(form, "clearance_surface").SelectedIndex == 0 && !Find<TextBox>(form, "membrane_outer_x").Enabled, "Unknown surface silently became declared membrane/layers");
            Check(!Find<Button>(form, "insert_node").Enabled && form.Result == null && form.Geometry == null, "Unreviewed input allowed insertion");
            Layout(form, "node_blank");
            Click(form, "check_node");
            Check(form.Geometry != null && form.Geometry.ClearanceStatus == "not_evaluated" && form.Geometry.CanInsert, "Known planes with unknown local inputs did not produce partial scheme");
            Check(Review(form).Contains("Проверка локального просвета не выполнена"), "Partial preview lacks permanent incomplete disclosure");
            MountingReview(form, standard.Selection);
            Layout(form, "node_partial"); Click(form, "insert_node");
            Check(form.DialogResult == DialogResult.OK && form.Result.profile_near_face_x_mm == null && form.Result.clearance_surface.kind == FrameNodeClearanceSurface.Unknown, "Partial input changed or failed explicit acceptance");
            var detached = form.Result; detached.profile_near_face_x_mm = 999;
            Check(form.Result.profile_near_face_x_mm == null, "Accepted input is not detached");
        }
        using (var form = Form(standard))
        {
            Show(form); Profile(form, "170"); Surface(form, 1); Click(form, "check_node");
            Check(form.Geometry.GapText == "20" && form.Geometry.ClearanceStatus == "pass" && form.Geometry.CanInsert, "Exact20 did not pass local-only check");
            Check(Review(form).Contains(form.Geometry.GapText + " мм") && Find<TextBox>(form, "node_status").Text.Contains("только"), "Exact preview overstates acceptance");
            var reviewLines = Find<TextBox>(form, "node_review").Lines;
            Check(reviewLines.Any(line => line.StartsWith("Размерная схема Вектор-1 ") && !line.Contains("Основание x")), "Native review merges the source and coordinate basis lines");
            Check(reviewLines.Any(line => line.StartsWith("Основание x = 0;") && !line.Contains("Слой утеплителя")), "Native review merges the coordinate basis and first layer lines");
            Check(reviewLines.Any(line => line == "Слой утеплителя 1: 100 мм."), "Native review lacks a separate first layer line");
            MountingReview(form, standard.Selection);
            Layout(form, "node_valid");
            ScrollReviewTo(form, "Монтажная проверка выбранного решения"); Layout(form, "node_mounting_members");
            ScrollReviewTo(form, "1. Основание — монтажная база КР2."); Layout(form, "node_mounting_requirements");
            Profile(form, "169,999"); // A real field edit must invalidate even while preview is displayed.
            Check(form.Geometry == null && !Find<Button>(form, "insert_node").Enabled && !Find<TextBox>(form, "node_review").Visible, "Input edit reused a previously valid preview");
            Check(Review(form) == "", "Input edit retained a stale hidden mounting report");
            Click(form, "check_node");
            Check(form.Geometry.GapText == "19.999" && form.Geometry.ClearanceStatus == "fail" && !form.Geometry.CanInsert, "Sub-minimum gap not rejected precisely");
            MountingReview(form, standard.Selection);
            Check(Review(form).Contains("Размерная схема отклонена. Монтажная диагностика не разрешает её вставку."), "Mounting report overrode a rejected local scheme");
            Layout(form, "node_rejected"); Click(form, "insert_node");
            Check(form.DialogResult != DialogResult.OK && form.Result == null, "Rejected preview inserted");
            Click(form, "back_node"); Check(Find<TextBox>(form, "profile_near_x").Text == "169,999" && Review(form) == "", "Back discarded user input or retained stale mounting report");
            double nextDown = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(170.0) - 1);
            Profile(form, nextDown.ToString("R", CultureInfo.InvariantCulture)); Click(form, "check_node");
            Check(form.Geometry.GapText == "19.99999999999997" && Review(form).Contains("19.99999999999997"), "NextDown gap rounded to20 in review");
            Layout(form, "node_exact_boundary"); Click(form, "cancel_node"); Check(form.Result == null, "Cancel kept accepted input");
        }
        using (var form = Form(standard))
        {
            Show(form); Surface(form, 2); Profile(form, "190"); Find<TextBox>(form, "membrane_outer_x").Text = "170";
            Check(Find<TextBox>(form, "membrane_outer_x").Enabled, "Explicit membrane coordinate cannot be entered");
            Click(form, "check_node"); Check(form.Geometry.GapText == "20" && form.Geometry.ClearanceStatus == "pass", "Explicit membrane coordinate not used");
            Layout(form, "node_membrane"); Surface(form, 0);
            Check(form.Geometry == null && Find<TextBox>(form, "membrane_outer_x").Text == "" && !Find<TextBox>(form, "membrane_outer_x").Enabled, "Surface change kept old review/hidden membrane coordinate");
            Click(form, "check_node"); Check(form.Geometry.ClearanceStatus == "not_evaluated", "Unknown surface reused former membrane declaration");
            Click(form, "cancel_node");
        }
        using (var form = Form(standard))
        {
            Show(form);
            foreach (string invalid in new[] { "NaN", "Infinity", "-1", "0", "abc" })
            {
                Profile(form, invalid); Click(form, "check_node");
                Check(form.Geometry == null && !Find<Button>(form, "insert_node").Enabled && Find<TextBox>(form, "node_status").Text.Contains("число"), "Invalid GP coordinate accepted: " + invalid);
            }
            Profile(form, "169.999999999999999999"); Click(form, "check_node");
            Check(form.Geometry == null && Find<TextBox>(form, "node_status").Text.Contains("без округления"), "Raw text silently rounded to170 before the exact core");
            Profile(form, "170"); Surface(form, 2); Click(form, "check_node");
            Check(form.Geometry == null && Find<TextBox>(form, "node_status").Text.Contains("мембраны"), "Selected membrane accepts missing coordinate");
            Surface(form, 1); form.ClientSize = new Size(620, 420); Layout(form, "node_small");
            Click(form, "check_node"); ScrollReviewTo(form, "1. Основание — монтажная база КР2.");
            Layout(form, "node_mounting_small"); Click(form, "back_node");
            form.Font = new Font("Segoe UI", 12f); form.ClientSize = new Size(1100, 800); Layout(form, "node_large_font");
            Click(form, "check_node"); Layout(form, "node_large_font_review");
            ScrollReviewTo(form, "7. Допустимые сочетания изделий."); Layout(form, "node_mounting_tail_large_font");
            Click(form, "cancel_node"); Check(form.Result == null, "Mounting review cancellation retained accepted input");
        }
        using (var form = Form(Fixture(new[] { 150.0, 50.0 }, 180)))
        {
            Show(form); Click(form, "check_node");
            Check(!form.Geometry.CanInsert && form.Geometry.Status == "rejected" && Review(form).Contains("180") && Review(form).Contains("200"), "Crossed insulation/cladding planes accepted or numbers hidden");
            Layout(form, "node_contradiction"); Click(form, "cancel_node");
        }
        using (var form = Form(Fixture(new double[0], null)))
        {
            Show(form); Click(form, "check_node"); Check(!form.Geometry.CanInsert, "Completely unknown scheme inserted as zero layers");
            Click(form, "back_node"); Surface(form, 1); Profile(form, "170"); Click(form, "check_node");
            Check(!form.Geometry.CanInsert, "Layers declaration with no layers silently used wall as layer boundary"); Click(form, "cancel_node");
        }
        using (var form = Form(Fixture(new[] { 108.95, 53.34 }, 230)))
        {
            Show(form); Profile(form, "182.29"); Surface(form, 1); Click(form, "check_node");
            Check(form.Geometry.GapText == "20" && form.Geometry.CanInsert, "Decimal layer case rounded below min20"); Click(form, "cancel_node");
        }
        foreach (int bracketLength in new[] { 50, 350 })
        {
            int extenderLength = bracketLength == 50 ? 100 : 150;
            var choice = Fixture(new[] { 100.0, 50.0 }, 230, bracketLength, extenderLength);
            using (var form = Form(choice))
            {
                Show(form); Profile(form, "170"); Surface(form, 1); Click(form, "check_node");
                MountingReview(form, choice.Selection);
                Check(form.Geometry.ClearanceStatus == "pass" && Find<Button>(form, "insert_node").Enabled &&
                    Review(form).Contains("Монтажная пригодность: не подтверждена."), "Declared catalogue length changed local clearance or became mounting approval");
                Check(Review(form).Contains("Длина L: " + bracketLength + " мм") && Review(form).Contains("Длина L: " + extenderLength + " мм"),
                    "Mounting review displayed a different selection's lengths");
                Profile(form, "invalid"); Click(form, "check_node");
                Check(form.Geometry == null && form.Result == null && Review(form) == "" && !Find<Button>(form, "insert_node").Enabled,
                    "Invalid input retained the former catalogue selection report");
                Click(form, "cancel_node");
            }
        }
        var initial = FrameNodeGeometryInput.CreateDefault(); initial.profile_near_face_x_mm = 170; initial.clearance_surface.kind = FrameNodeClearanceSurface.Layers;
        using (var form = new FrameNodeForm(standard.Selection, standard.Context, initial))
        {
            Show(form); Check(form.Geometry == null && form.Result == null && !Find<Button>(form, "insert_node").Enabled, "Restored input bypassed fresh review");
            Profile(form, "171"); Click(form, "cancel_node"); Check(initial.profile_near_face_x_mm == 170, "Cancel mutated caller local input");
        }
        var localProject = FrameProjectParameters.CreateNext(null, standard.Selection);
        var localBinding = FrameZoneParameters.CreateNext(null, localProject, new Dictionary<string, FrameParameterOverride> {
            { "geometry.cladding_front_offset_mm", FrameParameterOverride.Clear() },
            { "geometry.insulation_layers_mm", FrameParameterOverride.Set(new[] { 80.0, 50.0 }) } });
        var localResolution = FrameParameterResolver.Resolve(localProject, new string('1', 64), localBinding, new string('2', 64), "A2", "Учебная B");
        using (var form = Form(localResolution))
        {
            Show(form); string text = Find<TextBox>(form, "node_project_parameters").Text;
            Check(text.Contains("в зоне явно не задано") && text.Contains("своё значение зоны"), "Inherited display lost clear/local provenance");
            Click(form, "cancel_node");
        }
        var tinySelection = standard.Selection; tinySelection.geometry.cladding_front_offset_mm = null;
        tinySelection.geometry.insulation_layers_mm = new List<double> { 1e-28 };
        var tinyContext = standard.Context; tinyContext.effective_digest = FrameParameterResolver.SelectionDigest(tinySelection);
        using (var form = new FrameNodeForm(tinySelection, tinyContext))
        {
            Show(form); Click(form, "check_node");
            var direct = FrameNodeGeometry.Evaluate(tinySelection, FrameNodeGeometryInput.CreateDefault());
            NumericDiagnostic("tiny_declared_layer", 1e-28,
                FrameNodeGeometry.CloneSelection(tinySelection).geometry.insulation_layers_mm[0], direct.Layers[0].ThicknessMm,
                form.Geometry == null || form.Geometry.Layers.Count == 0 ? (double?)null : form.Geometry.Layers[0].ThicknessMm, form);
            Check(form.Geometry != null && form.Geometry.Layers.Count == 1 && form.Geometry.Layers[0].ThicknessMm == 1e-28,
                "UI selection cloning rounded a tiny declared layer before exact evaluation");
            Click(form, "cancel_node");
        }
        var adjacent = standard.Selection;
        adjacent.geometry.cladding_front_offset_mm = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(479.82) + 1);
        var adjacentContext = standard.Context; adjacentContext.effective_digest = FrameParameterResolver.SelectionDigest(adjacent);
        using (var form = new FrameNodeForm(adjacent, adjacentContext))
        {
            Show(form); Click(form, "check_node");
            var direct = FrameNodeGeometry.Evaluate(adjacent, FrameNodeGeometryInput.CreateDefault());
            NumericDiagnostic("adjacent_declared_cladding", adjacent.geometry.cladding_front_offset_mm.Value,
                FrameNodeGeometry.CloneSelection(adjacent).geometry.cladding_front_offset_mm.Value, direct.CladdingFrontXMm.Value,
                form.Geometry == null ? (double?)null : form.Geometry.CladdingFrontXMm, form);
            Check(form.Geometry != null && form.Geometry.CladdingFrontXMm == adjacent.geometry.cladding_front_offset_mm,
                "UI viewing changed the next representable declared cladding coordinate");
            Click(form, "cancel_node");
        }
        Check(Json.Serialize(callerSelection.ToDict()) == originalSelection && Json.Serialize(callerContext.ToDict()) == originalContext, "Node form changed inherited selection or provenance");
        File.WriteAllText(Path.Combine(output, "ui_checks.json"), Json.Serialize(new { status = failures == 0 ? "PASS" : "FAIL", checks, failures, reasons, images, layouts, numeric_diagnostics = numericDiagnostics, live_autocad_checked = false }));
        Console.WriteLine("Node UI: " + checks + " checks, " + failures + " failures"); return failures == 0 ? 0 : 1;
    }
}
