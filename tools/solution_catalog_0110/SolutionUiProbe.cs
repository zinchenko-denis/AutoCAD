using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AFramePlugin;

internal static class SolutionUiProbe
{
    private static int checks, failures;
    private static string output;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly List<string> images = new List<string>(), reasons = new List<string>();
    private static readonly List<object> layouts = new List<object>();
    private static void Check(bool value, string reason)
    {
        checks++;
        if (!value) { failures++; reasons.Add(reason); Console.WriteLine("FAIL " + reason); }
    }
    private static T Find<T>(Control owner, string name) where T : Control
    {
        var found = owner.Controls.Find(name, true);
        if (found.Length != 1 || !(found[0] is T)) throw new Exception("Control not found uniquely: " + name);
        return (T)found[0];
    }
    private static void Choose(Control form, string name, string caption)
    {
        var box = Find<ComboBox>(form, name);
        for (int i = 0; i < box.Items.Count; i++) if (box.Items[i].ToString() == caption) { box.SelectedIndex = i; return; }
        throw new Exception("Catalogue option not found: " + name + ": " + caption);
    }
    private static void Fill(FrameSolutionForm form)
    {
        Find<ComboBox>(form, "node").SelectedIndex = 0;
        Find<ComboBox>(form, "bracket_execution").SelectedIndex = 0;
        Choose(form, "bracket_width", "70"); Choose(form, "bracket_length", "200");
        Find<ComboBox>(form, "extender_execution").SelectedIndex = 1;
        Choose(form, "extender_width", "60"); Choose(form, "extender_length", "150"); Choose(form, "extender_thickness", "1.2");
        Find<ComboBox>(form, "profile_execution").SelectedIndex = 0; Choose(form, "profile_a", "40");
        Find<TextBox>(form, "profile_b").Text = "47,5"; Find<TextBox>(form, "profile_thickness").Text = "1,5";
    }
    private static void Show(Form form) { form.StartPosition = FormStartPosition.Manual; form.Location = new Point(10, 10); form.Show(); Application.DoEvents(); }
    private static void Shot(Form form, string name)
    {
        form.PerformLayout(); Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, name + ".png")); }
        images.Add(name + ".png");
    }
    private static IEnumerable<Control> All(Control owner)
    {
        foreach (Control control in owner.Controls) { yield return control; foreach (var child in All(control)) yield return child; }
    }
    private static void Layout(Form form, string scenario)
    {
        layouts.Add(new { scenario, window = form.Bounds.ToString(), client = form.ClientRectangle.ToString(),
            screen = Screen.FromControl(form).WorkingArea.ToString(), font_points = form.Font.SizeInPoints,
            controls = All(form).Where(c => c is TableLayoutPanel || (c is Button && c.Name.EndsWith("_selection")))
                .Select(c => new { type = c.GetType().Name, c.Name, bounds = c.Bounds.ToString(),
                    parent_client = c.Parent.ClientRectangle.ToString() }).ToArray() });
        foreach (var grid in All(form).OfType<TableLayoutPanel>())
        {
            var controls = grid.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (var control in controls)
                Check(control.Left >= 0 && control.Top >= 0 && control.Right <= grid.ClientSize.Width && control.Bottom <= grid.ClientSize.Height,
                    scenario + ": child exceeds table viewport " + control.Name + " " + control.Bounds + "/" + grid.ClientRectangle);
            for (int a = 0; a < controls.Count; ++a)
                for (int b = a + 1; b < controls.Count; ++b)
                    Check(!controls[a].Bounds.IntersectsWith(controls[b].Bounds), scenario + ": overlapping siblings " + controls[a].Name + "/" + controls[b].Name);
        }
        foreach (var button in All(form).OfType<Button>().Where(c => c.Visible && c.Name.EndsWith("_selection")))
        {
            Point top = form.PointToClient(button.PointToScreen(Point.Empty));
            Check(top.X >= 0 && top.Y >= 0 && top.X + button.Width <= form.ClientSize.Width && top.Y + button.Height <= form.ClientSize.Height,
                scenario + ": footer action inaccessible " + button.Name);
            Check(TextRenderer.MeasureText(button.Text, button.Font).Width + 8 <= button.Width, scenario + ": action caption clipped " + button.Name);
        }
        var scroll = Find<Panel>(form, "solution_scroll");
        Check(scroll.AutoScroll, scenario + ": long content does not scroll");
        Check(!scroll.HorizontalScroll.Visible, scenario + ": catalogue content exceeds available width");
        foreach (var note in All(form).OfType<Label>().Where(c => c.Text.StartsWith("b и c обязательны") || c.Text.StartsWith("Пустое поле означает")))
        {
            int height = TextRenderer.MeasureText(note.Text, note.Font, new Size(note.Width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            Check(note.Height >= height, scenario + ": wrapped explanation is clipped " + note.Text);
        }
        Check(Find<TextBox>(form, "source_summary").ReadOnly && Find<TextBox>(form, "source_summary").ScrollBars == ScrollBars.Vertical,
            scenario + ": provenance is editable or clipped without scrolling");
    }
    private static void Modal(FrameForm main, Action<FrameSolutionForm> action)
    {
        bool entered = false;
        Exception failure = null;
        using (var timer = new Timer { Interval = 50 })
        {
            timer.Tick += delegate
            {
                var child = Application.OpenForms.Cast<Form>().OfType<FrameSolutionForm>().FirstOrDefault();
                if (child == null) return;
                timer.Stop(); entered = true;
                try { action(child); } catch (Exception error) { failure = error; child.Close(); }
            };
            timer.Start(); Find<Button>(main, "solution_catalog").PerformClick(); timer.Stop();
        }
        if (failure != null) throw failure;
        Check(entered, "Main catalogue button did not open the actual modal form");
        Application.DoEvents();
    }
    [STAThread]
    public static int Main(string[] args)
    {
        output = args[0]; Directory.CreateDirectory(output);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        FrameSolutionSelection selected;
        using (var form = new FrameSolutionForm(null))
        {
            Show(form);
            Check(All(form).OfType<ComboBox>().All(c => c.SelectedIndex == -1), "A new form automatically selected a node, execution or dimension");
            Check(Find<TextBox>(form, "profile_b").Text == "" && Find<TextBox>(form, "profile_thickness").Text == "", "Project b/c received defaults");
            Check(Find<TextBox>(form, "cladding_front_offset").Text == "" && Find<TextBox>(form, "insulation_layers").Text == "", "Optional project planes/layers received defaults");
            Layout(form, "blank"); Shot(form, "solution_blank");
            Find<Button>(form, "apply_selection").PerformClick();
            Check(form.DialogResult != DialogResult.OK && Find<TextBox>(form, "selection_error").Text.Contains("Выберите"), "Apply accepted an empty declaration");
            Find<ComboBox>(form, "node").SelectedIndex = 0;
            Check(All(form).OfType<ComboBox>().Where(c => c.Name != "node").All(c => c.SelectedIndex == -1), "Choosing node silently assigned products");
            Fill(form);
            Find<TextBox>(form, "profile_b").Text = "NaN";
            Find<Button>(form, "apply_selection").PerformClick();
            Check(form.DialogResult != DialogResult.OK, "Nonfinite project dimension accepted");
            Find<TextBox>(form, "profile_b").Text = "47,5";
            Find<TextBox>(form, "insulation_layers").Text = "100;;50";
            Find<Button>(form, "apply_selection").PerformClick();
            Check(form.DialogResult != DialogResult.OK, "Missing interior layer silently became zero/omitted");
            Find<TextBox>(form, "insulation_layers").Text = "";
            Layout(form, "filled"); Shot(form, "solution_filled");
            Find<Panel>(form, "solution_scroll").AutoScrollPosition = new Point(0, 10000); Application.DoEvents(); Shot(form, "solution_source");
            Find<Button>(form, "apply_selection").PerformClick();
            selected = form.Result;
            Check(form.DialogResult == DialogResult.OK && selected != null && selected.Validate() == null, "Complete manual declaration refused");
            Check(selected.profile.b_mm == 47.5 && selected.profile.thickness_mm == 1.5, "Russian decimal input changed b/c");
            Check(selected.geometry.cladding_front_offset_mm == null && selected.geometry.insulation_layers_mm.Count == 0, "Unknown optional project data became numbers");
            Check(selected.bracket.nominal_width_mm == 70 && selected.extender.nominal_width_mm == 60 &&
                selected.bracket.execution != selected.extender.execution, "Independent catalogue declarations were silently harmonized");
            selected.geometry.insulation_layers_mm.Add(999);
            Check(form.Result.geometry.insulation_layers_mm.Count == 0, "Result exposes mutable dialog state");
            selected = form.Result;
        }
        string original = Json.Serialize(selected);
        using (var form = new FrameSolutionForm(selected))
        {
            Show(form); Find<TextBox>(form, "profile_b").Text = "99";
            Find<Button>(form, "cancel_selection").PerformClick();
            Check(form.DialogResult == DialogResult.Cancel && form.Result == null && Json.Serialize(selected) == original, "Cancel changed prior selection");
        }
        using (var form = new FrameSolutionForm(selected))
        {
            Show(form); Find<TextBox>(form, "cladding_front_offset").Text = "253.125"; Find<TextBox>(form, "insulation_layers").Text = "100;50,5";
            Find<Button>(form, "apply_selection").PerformClick();
            Check(form.Result.geometry.cladding_front_offset_mm == 253.125 && form.Result.geometry.insulation_layers_mm.SequenceEqual(new[] { 100.0, 50.5 }), "Explicit project layer/plane data lost");
            Check(Json.Serialize(selected) == original, "Apply modified the caller's selection in place");
        }
        using (var form = new FrameSolutionForm(selected))
        {
            Show(form); Find<Button>(form, "clear_selection").PerformClick();
            Check(form.DialogResult == DialogResult.OK && form.Result == null && Json.Serialize(selected) == original, "Explicit clear not distinct from cancel or modifies source");
        }
        using (var form = new FrameSolutionForm(selected, true))
        {
            Show(form); Layout(form, "readonly"); Shot(form, "solution_readonly");
            Check(All(form).OfType<ComboBox>().All(c => !c.Enabled) && Find<TextBox>(form, "profile_b").ReadOnly &&
                !Find<Button>(form, "apply_selection").Enabled && !Find<Button>(form, "clear_selection").Enabled, "Clamps-only view permits identity changes");
            Find<Button>(form, "apply_selection").PerformClick(); Find<Button>(form, "clear_selection").PerformClick();
            Check(form.DialogResult != DialogResult.OK && form.Result == null, "Disabled clamps-only actions produced a selection");
        }
        var wrongRevision = selected.Clone(); wrongRevision.catalog_revision = new string('0', 64);
        using (var form = new FrameSolutionForm(wrongRevision))
        {
            Show(form); Find<Button>(form, "apply_selection").PerformClick(); Shot(form, "solution_mismatch");
            Check(form.DialogResult != DialogResult.OK && Find<TextBox>(form, "selection_error").Text.Contains("catalog_revision"), "Stale catalogue revision was silently upgraded");
            Check(Find<TextBox>(form, "source_summary").Text.Contains(new string('0', 64)), "Mismatched loaded catalogue provenance hidden");
        }
        using (var form = new FrameSolutionForm(selected))
        {
            Show(form); form.Size = form.MinimumSize; Application.DoEvents();
            Layout(form, "small"); Shot(form, "solution_small");
            Find<Panel>(form, "solution_scroll").AutoScrollPosition = new Point(0, 10000); Application.DoEvents(); Shot(form, "solution_small_source");
        }
        using (var form = new FrameSolutionForm(selected))
        {
            Show(form); form.Font = new Font("Segoe UI", 12f); form.ClientSize = new Size(1100, 800); Application.DoEvents();
            Layout(form, "large_font"); Shot(form, "solution_large_font");
            Check(form.AutoScaleMode == AutoScaleMode.Font, "Dialog does not scale by font");
        }
        var settings = new FrameSettings { Profile = "ГП-60-40" };
        string settingsBefore = Json.Serialize(settings.ToDict());
        using (var main = new FrameForm(settings, true))
        {
            Show(main); Shot(main, "frame_main_before");
            Check(main.ClientSize == new Size(860, 714), "Main form size changed");
            var directButtons = main.Controls.OfType<Button>().ToList();
            for (int a = 0; a < directButtons.Count; ++a)
                for (int b = a + 1; b < directButtons.Count; ++b)
                    Check(!directButtons[a].Bounds.IntersectsWith(directButtons[b].Bounds), "Main form footer actions overlap");
            Modal(main, child => { Fill(child); Find<Button>(child, "apply_selection").PerformClick(); });
            Check(main.Result.SolutionSelection != null && main.Result.Steps == "calc", "Solution apply auto-changed calculation/manual mode");
            Check(main.Result.Profile == "ГП-60-40", "Explicit declaration overwrote preserved legacy profile");
            Check(main.Result.Validate(true) != null, "Unsupported calculation became available through UI");
            Check(All(main).OfType<ComboBox>().Any(c => !c.Enabled && c.Text == "Задан в решении и каталоге"), "Main profile still suggests legacy automatic selection");
            Shot(main, "frame_main_selected");
            string beforeCancel = Json.Serialize(main.Result.ToDict());
            Modal(main, child => { Find<TextBox>(child, "profile_b").Text = "88"; Find<Button>(child, "cancel_selection").PerformClick(); });
            Check(Json.Serialize(main.Result.ToDict()) == beforeCancel, "Nested cancel changed main draft");
            main.Controls.OfType<Button>().Single(b => b.Text == "Сброс").PerformClick();
            Check(main.Result.SolutionSelection != null, "Main reset silently removed explicit solution");
            Modal(main, child => Find<Button>(child, "clear_selection").PerformClick());
            Check(main.Result.SolutionSelection == null, "Explicit clear did not restore legacy mode");
            ((Button)main.CancelButton).PerformClick();
            Check(Json.Serialize(settings.ToDict()) == settingsBefore, "Main cancel mutated caller settings");
        }
        var manifest = new { status = failures == 0 ? "PASS" : "FAIL", checks, failures, reasons, images, layouts,
            runtime = Environment.OSVersion.ToString(), live_autocad_checked = false,
            scope = "Actual WinForms dialogs, controls, modal apply/cancel, read-only mode, font scaling and bitmap rendering; no AutoCAD host" };
        File.WriteAllText(Path.Combine(output, "ui_checks.json"), Json.Serialize(manifest));
        Console.WriteLine("Solution actual WinForms: " + checks + " checks, " + failures + " failures; " + images.Count + " images");
        return failures == 0 ? 0 : 1;
    }
}
