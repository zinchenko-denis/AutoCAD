using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AFramePlugin;

internal static class ProjectUiProbe
{
    private static int checks, failures;
    private static string output;
    private static readonly List<string> images = new List<string>(), reasons = new List<string>();
    private static readonly List<object> layouts = new List<object>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; internal Rectangle Bounds { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } } }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
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
    private static void Choose(Form form, string name, string value)
    {
        var combo = Find<ComboBox>(form, name);
        for (int i = 0; i < combo.Items.Count; i++) if (combo.Items[i].ToString() == value) { combo.SelectedIndex = i; return; }
        throw new Exception("Missing actual catalogue choice " + name + "/" + value);
    }
    private static void Fill(FrameSolutionForm form)
    {
        foreach (string name in new[] { "node", "bracket_execution", "extender_execution", "profile_execution" }) Find<ComboBox>(form, name).SelectedIndex = 0;
        Choose(form, "bracket_width", "70"); Choose(form, "bracket_length", "200");
        Choose(form, "extender_width", "70"); Choose(form, "extender_length", "100"); Choose(form, "extender_thickness", "1.2");
        Choose(form, "profile_a", "40"); Find<TextBox>(form, "profile_b").Text = "47,5"; Find<TextBox>(form, "profile_thickness").Text = "1,5";
        Find<TextBox>(form, "cladding_front_offset").Text = "250"; Find<TextBox>(form, "insulation_layers").Text = "90; 40";
    }
    private static void Layout(FrameSolutionForm form, string name)
    {
        form.PerformLayout(); Application.DoEvents();
        Rectangle client = NativeClient(form), window = NativeWindow(form), work = Screen.FromControl(form).WorkingArea;
        Check(client.Size == form.ClientSize, name + ": native/cached client size mismatch");
        Check(work.Contains(window), name + ": actual HWND outside working area " + window + "/" + work);
        foreach (var control in All(form).OfType<Button>().Where(c => c.Visible && c.Name.EndsWith("_selection")))
        {
            Rectangle button = NativeWindow(control);
            Check(client.Contains(button) && work.Contains(button), name + ": footer action is clipped " + control.Name);
            Check(TextRenderer.MeasureText(control.Text, control.Font).Width + 8 <= control.Width, name + ": footer text is clipped " + control.Name);
        }
        foreach (var grid in All(form).OfType<TableLayoutPanel>().Where(c => c.Visible))
        {
            var controls = grid.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (var child in controls)
                Check(child.Left >= 0 && child.Top >= 0 && child.Right <= grid.ClientSize.Width && child.Bottom <= grid.ClientSize.Height,
                    name + ": child exceeds table " + child.Name);
            for (int i = 0; i < controls.Count; i++) for (int j = i + 1; j < controls.Count; j++)
                Check(!controls[i].Bounds.IntersectsWith(controls[j].Bounds), name + ": overlapping controls " + controls[i].Name + "/" + controls[j].Name);
        }
        var scroll = Find<Panel>(form, "solution_scroll");
        Check(scroll.AutoScroll && !scroll.HorizontalScroll.Visible, name + ": body cannot scroll or exceeds width");
        foreach (var label in All(form).OfType<Label>().Where(c => c.Visible && (c.Name.EndsWith("_origin") || c.Text.StartsWith("b и c обязательны"))))
        {
            int height = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            Check(label.Height >= height, name + ": origin/note text clipped " + label.Name);
        }
        layouts.Add(new { scenario = name, window = window.ToString(), native_client = client.ToString(), cached_client = form.ClientRectangle.ToString(),
            screen = work.ToString(), font_points = form.Font.SizeInPoints, minimum_size = form.MinimumSize.ToString() });
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, name + ".png")); }
        images.Add(name + ".png");
    }
    private static FrameParameterResolution Resolve(FrameProjectParameters project, FrameZoneParameters zone, string id)
    { return FrameParameterResolver.Resolve(project, new string('1', 64), zone, new string('2', 64), id, "Zone " + id); }
    private static void Modal(FrameForm main, Action<FrameSolutionForm> action)
    {
        Exception failure = null; bool entered = false;
        using (var timer = new Timer { Interval = 50 })
        {
            timer.Tick += delegate
            {
                var child = Application.OpenForms.Cast<Form>().OfType<FrameSolutionForm>().FirstOrDefault();
                if (child == null) return; timer.Stop(); entered = true;
                try { action(child); } catch (Exception error) { failure = error; child.Close(); }
            };
            timer.Start(); Click(main, "solution_catalog"); timer.Stop();
        }
        if (failure != null) throw failure;
        Check(entered, "Actual bound ATFRAME catalogue was not opened");
    }
    [STAThread]
    public static int Main(string[] args)
    {
        output = args[0]; Directory.CreateDirectory(output);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        FrameProjectParameters project;
        using (var form = new FrameProjectForm(null))
        {
            Show(form); Check(All(form).OfType<ComboBox>().All(c => c.SelectedIndex == -1), "Creating project silently assigned catalogue values");
            Click(form, "apply_selection"); Check(form.DialogResult != DialogResult.OK, "Empty project accepted");
            Fill(form); Layout(form, "project_filled"); Click(form, "apply_selection");
            Check(form.DialogResult != DialogResult.OK && form.Result == null, "Preview already committed a project");
            Check(Find<TextBox>(form, "parameter_review").Text.Contains("Ни одна зона"), "Project bootstrap omitted explicit binding consequence");
            Layout(form, "project_review"); Click(form, "clear_selection");
            Check(Find<TextBox>(form, "profile_b").Text == "47,5", "Back discarded draft parameters");
            Click(form, "apply_selection"); Click(form, "apply_selection"); project = form.Result;
            Check(form.DialogResult == DialogResult.OK && project.revision == 1, "Project final save did not return revision 1");
            Check(project.project_id == form.Result.project_id, "Repeated Result creates a different project identity");
        }
        string original = Json.Serialize(project.ToDict());
        using (var form = new FrameProjectForm(project))
        {
            Show(form); Click(form, "apply_selection"); Click(form, "apply_selection");
            Check(form.Result.revision == 1 && Json.Serialize(form.Result.ToDict()) == original, "No-op project increments revision or changes values");
        }
        using (var form = new FrameProjectForm(project))
        {
            Show(form); Find<TextBox>(form, "profile_b").Text = "65"; Click(form, "apply_selection");
            Check(Find<TextBox>(form, "parameter_review").Text.Contains("47.5") && Find<TextBox>(form, "parameter_review").Text.Contains("65"), "Review does not show old and new values");
            Click(form, "cancel_selection"); Check(form.Result == null && Json.Serialize(project.ToDict()) == original, "Cancel mutated project");
        }
        var exhausted = project.Clone(); exhausted.revision = long.MaxValue;
        using (var form = new FrameProjectForm(exhausted))
        {
            Show(form); Find<TextBox>(form, "profile_b").Text = "65"; Click(form, "apply_selection");
            Check(form.Controls.Find("parameter_review", true).Length == 0 && Find<TextBox>(form, "selection_error").Text.Contains("диапазон"), "Project revision overflow is displayed as a valid preview");
            Click(form, "cancel_selection");
        }
        FrameZoneParameters zone;
        using (var form = new FrameZoneParametersForm(project, null, "Зона A"))
        {
            Show(form);
            Check(!Find<ComboBox>(form, "node").Enabled, "Zone editor permits source/node override");
            Check(All(form).OfType<CheckBox>().Count(c => c.Name.EndsWith("_own")) == FrameParameterResolver.Fields.Count, "Missing per-field origin controls");
            Check(All(form).OfType<CheckBox>().All(c => !c.Checked), "Zone silently created overrides");
            Check(Find<TextBox>(form, "profile_b").ReadOnly, "Inherited input editable");
            Find<CheckBox>(form, "profile_b_own").Checked = true;
            Check(!Find<TextBox>(form, "profile_b").ReadOnly, "Local input not editable");
            Find<TextBox>(form, "profile_b").Text = ""; Click(form, "apply_selection");
            Check(form.Controls.Find("parameter_review", true).Length == 0, "Required local dimension accepted as clear");
            Find<TextBox>(form, "profile_b").Text = "99"; Find<CheckBox>(form, "profile_b_own").Checked = false;
            Check(Find<TextBox>(form, "profile_b").Text == "47.5", "Return to inheritance kept local value");
            Find<CheckBox>(form, "profile_b_own").Checked = true; // Equal default remains explicit.
            foreach (string name in new[] { "cladding_front_offset", "insulation_layers" })
            { Find<CheckBox>(form, name + "_own").Checked = true; Find<TextBox>(form, name).Text = ""; }
            Layout(form, "zone_overrides"); Click(form, "apply_selection");
            string review = Find<TextBox>(form, "parameter_review").Text;
            Check(review.Contains("[из проекта]") && review.Contains("[своё значение]") && review.Contains("[в зоне не задано]"), "Preview lost equal override or clear/inherit distinction");
            Check(form.Result == null, "Zone review already committed intent");
            Layout(form, "zone_review"); Click(form, "apply_selection"); zone = form.Result;
            Check(zone.overrides.Count == 3 && zone.overrides["profile.b_mm"].state == "set", "Equal-to-default local override was dropped");
            Check(zone.overrides["geometry.cladding_front_offset_mm"].state == "clear" && zone.overrides["geometry.insulation_layers_mm"].state == "clear", "Optional clear became inherit");
        }
        string originalZone = Json.Serialize(zone.ToDict());
        using (var form = new FrameZoneParametersForm(project, zone, "Зона A"))
        {
            Show(form); Click(form, "apply_selection"); Click(form, "apply_selection");
            Check(Json.Serialize(form.Result.ToDict()) == originalZone, "No-op zone changes intent or revision");
        }
        using (var form = new FrameZoneParametersForm(project, zone, "Зоны A и B", 2, true))
        {
            Show(form); Check(Find<TextBox>(form, "source_summary").Text.Contains("своя у каждой зоны"), "Group displays the first zone revision as common");
            Click(form, "apply_selection"); string preview = Find<TextBox>(form, "parameter_review").Text;
            Check(preview.Contains("Редакция каждой привязки сохраняется") && preview.Contains("станут неактуальными"), "Generation dependency change hidden by intent no-op");
            Click(form, "cancel_selection");
        }
        var explicitEmpty = FrameZoneParameters.CreateNext(null, project, new Dictionary<string, FrameParameterOverride>
            { { "geometry.insulation_layers_mm", FrameParameterOverride.Set(new double[0]) } });
        using (var form = new FrameZoneParametersForm(project, explicitEmpty, "Зона C"))
        {
            Show(form); Click(form, "apply_selection"); Click(form, "apply_selection");
            Check(form.Result.overrides["geometry.insulation_layers_mm"].state == "set" && form.Result.revision == explicitEmpty.revision,
                "Untouched explicit empty layers converted to clear or incremented revision");
        }
        using (var form = new FrameZoneParametersForm(project, zone, "Зона A"))
        {
            Show(form); Find<CheckBox>(form, "profile_b_own").Checked = false; Click(form, "apply_selection"); Click(form, "cancel_selection");
            Check(form.Result == null && Json.Serialize(zone.ToDict()) == originalZone, "Cancel mutated zone binding");
        }
        using (var form = new FrameZoneParametersForm(project, zone, "Зона A"))
        {
            Show(form); Click(form, "clear_selection");
            Check(Find<TextBox>(form, "parameter_review").Text.Contains("не станут локальным решением"), "Detach preview hides implicit-local risk");
            Check(!form.WasCleared, "Detach changed intent before final save");
            Layout(form, "zone_detach_review"); Click(form, "clear_selection");
            Check(!form.WasCleared && Find<CheckBox>(form, "profile_b_own").Checked, "Back from detach lost binding draft");
            Click(form, "clear_selection"); Click(form, "apply_selection");
            Check(form.DialogResult == DialogResult.OK && form.WasCleared && form.Result == null, "Explicit final detach did not return removal");
        }
        using (var form = new FrameZoneParametersForm(project, zone, "Зона A"))
        {
            Show(form); form.ClientSize = new Size(620, 420); Layout(form, "zone_small");
            form.Font = new Font("Segoe UI", 12f); form.ClientSize = new Size(1100, 800); Layout(form, "zone_large_font");
            Click(form, "apply_selection"); Layout(form, "zone_large_font_review"); Click(form, "cancel_selection");
        }
        var inherited = FrameZoneParameters.CreateNext(null, project, new Dictionary<string, FrameParameterOverride>());
        var equal = FrameZoneParameters.CreateNext(null, project, new Dictionary<string, FrameParameterOverride> { { "profile.b_mm", FrameParameterOverride.Set(project.defaults.profile.b_mm.Value) } });
        var contexts = new[] { Resolve(project, inherited, "A").Context, Resolve(project, equal, "B").Context };
        using (var form = new FrameSolutionForm(project.defaults, true, FrameProjectForms.ReadOnlyContext(contexts)))
        {
            Show(form); string origin = Find<Label>(form, "profile_b_origin").Text;
            Check(origin.Contains("из проекта 1") && origin.Contains("своё 1"), "Group origins incorrectly claim a common source");
            Check(Find<TextBox>(form, "profile_b").ReadOnly && !Find<Button>(form, "apply_selection").Enabled, "Bound group can edit catalogue");
            Layout(form, "bound_group_origins");
            form.Font = new Font("Segoe UI", 12f); form.ClientSize = new Size(1100, 800); Layout(form, "bound_group_large_font");
            Click(form, "cancel_selection");
        }
        var settings = new FrameSettings { Steps = "manual", SubType = "vertical", Cladding = "porcelain" };
        settings.ApplyProjectParameters(Resolve(project, inherited, "A"));
        using (var main = new FrameForm(settings, false))
        {
            Show(main); Modal(main, delegate(FrameSolutionForm child)
            {
                Check(Find<TextBox>(child, "profile_b").ReadOnly && !Find<Button>(child, "apply_selection").Enabled, "Full ATFRAME can edit bound parameters");
                Check(!Find<TextBox>(child, "selection_error").Text.Contains("только кляммеры"), "Bound full-mode readonly reason claims clamps-only");
                Layout(child, "bound_atframe"); Click(child, "cancel_selection");
            });
            Check(FrameParameterContext.Same(settings.ProjectParametersContext, main.Result.ProjectParametersContext), "Bound catalogue view changed dependency snapshot"); main.Close();
        }
        File.WriteAllText(Path.Combine(output, "ui_checks.json"), Json.Serialize(new { status = failures == 0 ? "PASS" : "FAIL", checks, failures, reasons, images, layouts, live_autocad_checked = false }));
        Console.WriteLine("Project UI: " + checks + " checks, " + failures + " failures");
        return failures == 0 ? 0 : 1;
    }
}
