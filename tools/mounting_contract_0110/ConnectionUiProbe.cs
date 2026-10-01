using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AFacadesPlugin;
using FacadeSafety;

// Uses the actual preview and data adapters; the only input is a synthetic DWG
// snapshot created by the real engine and physical-element producer fixture.
internal static class ConnectionUiProbe
{
    private const string Ambiguous = "Монтажная принадлежность не определена:";
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly List<string> reasons = new List<string>(), images = new List<string>();
    private static readonly List<object> layouts = new List<object>();
    private static int checks, failures;
    private static string output;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    { internal int Left, Top, Right, Bottom; internal Rectangle Bounds { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } } }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    private static Rectangle NativeWindow(Control control)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return control is Form ? control.Bounds : control.RectangleToScreen(control.ClientRectangle);
        NativeRect rect;
        if (!GetWindowRect(control.Handle, out rect)) throw new Exception("GetWindowRect failed");
        return rect.Bounds;
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
    private static IEnumerable<Control> All(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in All(child)) yield return nested; } }
    private static void Show(Form form)
    { form.StartPosition = FormStartPosition.Manual; form.Location = new Point(10, 10); form.Show(); Application.DoEvents(); }
    private static void Shot(Form form, string name)
    {
        form.PerformLayout(); Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, name + ".png")); }
        images.Add(name + ".png");
    }
    private static void Layout(Form form, string scenario)
    {
        var client = NativeClient(form); var window = NativeWindow(form); var work = Screen.FromControl(form).WorkingArea;
        Check(client.Size == form.ClientSize, scenario + ": native/cached client size mismatch " + client.Size + "/" + form.ClientSize);
        Check(work.Contains(window), scenario + ": actual window outside monitor working area " + window + "/" + work);
        foreach (var control in All(form).Where(c => c.Visible && (c is TextBox || c is DataGridView || c is Button)))
        {
            var bounds = NativeWindow(control);
            Check(client.Contains(bounds) && work.Contains(bounds), scenario + ": control outside native visible area: " + control.GetType().Name + " " + bounds);
        }
        foreach (var layout in All(form).OfType<TableLayoutPanel>())
        {
            var children = layout.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (var child in children)
                Check(layout.ClientRectangle.Contains(child.Bounds), scenario + ": child exceeds table layout: " + child.GetType().Name);
            for (int a = 0; a < children.Count; a++)
                for (int b = a + 1; b < children.Count; b++)
                    Check(!children[a].Bounds.IntersectsWith(children[b].Bounds), scenario + ": sibling controls overlap");
        }
        foreach (var button in All(form).OfType<Button>())
        {
            Check(button.Visible && button.Enabled, scenario + ": action unavailable: " + button.Text);
            Check(TextRenderer.MeasureText(button.Text, button.Font).Width + 8 <= button.Width,
                scenario + ": action caption clipped: " + button.Text);
        }
        layouts.Add(new { scenario, window = form.Bounds.ToString(), client = form.ClientRectangle.ToString(),
            native_window = window.ToString(), native_client = client.ToString(), working_area = work.ToString(),
            font_points = form.Font.SizeInPoints, minimum_size = form.MinimumSize.ToString(),
            controls = All(form).Where(c => c is TextBox || c is DataGridView || c is Button)
                .Select(c => new { type = c.GetType().Name, bounds = c.Bounds.ToString(), native_bounds = NativeWindow(c).ToString() }).ToArray() });
    }
    private static void ScrollText(TextBox box, string text, string scenario)
    {
        int index = box.Text.IndexOf(text, StringComparison.Ordinal);
        Check(index >= 0 && box.ReadOnly && box.Multiline && box.ScrollBars == ScrollBars.Vertical,
            scenario + ": warning missing or cannot be read with vertical scrolling");
        if (index < 0) return;
        box.Focus(); box.Select(box.TextLength, 0); box.ScrollToCaret();
        box.Select(index, 0); box.ScrollToCaret(); Application.DoEvents();
        var start = box.GetPositionFromCharIndex(index);
        Check(start.Y >= 0 && start.Y + box.Font.Height <= box.ClientSize.Height, scenario + ": warning start cannot be scrolled into view");
        int last = index + text.Length - 1;
        box.Select(last, 0); box.ScrollToCaret(); Application.DoEvents();
        var end = box.GetPositionFromCharIndex(last);
        Check(end.Y >= 0 && end.Y + box.Font.Height <= box.ClientSize.Height, scenario + ": warning ending cannot be scrolled into view");
    }
    private static void Review(QuantityTablePreview form, QuantityTableView view, string scenario)
    {
        var grid = All(form).OfType<DataGridView>().Single();
        var messages = All(form).OfType<TextBox>().Single(c => c.ReadOnly && c.Text.Contains(Ambiguous));
        Check(grid.VirtualMode && grid.ReadOnly && grid.ShowCellToolTips, scenario + ": actual virtual read-only preview contract changed");
        Check(grid.ColumnCount == 11 && grid.ColumnCount == view.Headers.Length && grid.RowCount == view.PreviewRows.Count,
            scenario + ": existing connection rows/columns lost or extended");
        foreach (string message in view.Messages)
            Check(messages.Text.Contains(message), scenario + ": messages textbox omitted source text");
        for (int row = 0; row < grid.RowCount; row++)
        {
            for (int col = 0; col < grid.ColumnCount; col++)
                Check(Convert.ToString(grid.Rows[row].Cells[col].Value) == CladdingTableData.Display(view.PreviewRows[row][col]),
                    scenario + ": visible cell differs from adapter at " + row + "/" + col);
            string note = CladdingTableData.Display(view.PreviewRows[row][10]);
            Check(note.Contains(Ambiguous), scenario + ": affected physical rail row lost its warning");
            Check(grid.Rows[row].Cells[10].ToolTipText == note, scenario + ": clipped note unavailable as a full tooltip");
        }
        grid.CurrentCell = grid.Rows[0].Cells[10]; grid.FirstDisplayedScrollingColumnIndex = 10; Application.DoEvents();
        var cell = grid.GetCellDisplayRectangle(10, 0, true);
        Check(cell.Width > 0 && cell.Height >= grid.Font.Height && grid.ClientRectangle.Contains(cell),
            scenario + ": note column cannot be scrolled into the viewport");
        var warnings = view.Messages.Where(m => m.StartsWith(Ambiguous, StringComparison.Ordinal)).ToList();
        foreach (string warning in warnings) ScrollText(messages, warning, scenario);
        // Show the beginning of the first addressed warning in the evidence image.
        messages.Select(messages.TextLength, 0); messages.ScrollToCaret();
        messages.Select(messages.Text.IndexOf(warnings[0], StringComparison.Ordinal), 0); messages.ScrollToCaret(); Application.DoEvents();
        Shot(form, "connections_" + scenario + "_warnings");
    }
    [STAThread]
    public static int Main(string[] args)
    {
        output = args[0]; Directory.CreateDirectory(output);
        var report = Json.Deserialize<QuantityReport>(File.ReadAllText(Environment.GetEnvironmentVariable("FACADE_CONNECTION_UI_REPORT")));
        string sourceBefore = Json.Serialize(report);
        var result = FacadeQuantitiesCore.BuildRows(new[] { report }, report.zone_ids, true, true);
        if (!result.ok) throw new Exception("Actual producer fixture rejected by quantity core: " + Json.Serialize(result.issues));
        var view = QuantityTableView.FromConnections(ConnectionTableData.Build(result, new[] { report }, report.zone_ids, null, true));
        // Independently identify the expected ambiguity from physical DTO IDs.
        var shared = report.connection_passports.SelectMany(p => p.members)
            .SelectMany(m => m.supports.SelectMany(s => s.bracket_element_ids.Select(b => new { bracket = b, rail = m.rail_element_id })))
            .GroupBy(link => link.bracket).Where(group => group.Select(link => link.rail).Distinct().Count() > 1).ToList();
        Check(report.zone_ids.Count == 2 && view.PreviewRows.Count == 4 && shared.Count == 4,
            "Fixture no longer has two independent zones, four rails and four shared physical brackets");
        foreach (var group in shared)
        {
            var addressed = view.Messages.Where(m => m.StartsWith(Ambiguous, StringComparison.Ordinal) && m.Contains(group.Key)).ToList();
            Check(addressed.Count == 1 && group.All(link => addressed[0].Contains(link.rail)),
                "Physical bracket warning omits associated rails or is duplicated: " + group.Key);
        }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        foreach (string scenario in new[] { "default", "small", "large_font" })
            using (var form = new QuantityTablePreview(view, "Проверено инженером: требуется монтажная принадлежность."))
            {
                Show(form);
                if (scenario == "small") form.Size = form.MinimumSize;
                if (scenario == "large_font") form.Font = new Font("Segoe UI", 12f);
                Application.DoEvents(); Layout(form, scenario);
                if (scenario == "default") Shot(form, "connections_default_columns");
                Review(form, view, scenario);
                var note = All(form).OfType<TextBox>().Single(c => !c.ReadOnly);
                note.Text = "Согласование требуется; =1+1";
                Check(form.UserNote == note.Text, scenario + ": user note is inaccessible");
                var action = (Button)(scenario == "small" ? form.AcceptButton : form.CancelButton);
                action.PerformClick(); Application.DoEvents();
                Check(form.DialogResult == (scenario == "small" ? DialogResult.OK : DialogResult.Cancel),
                    scenario + ": footer action did not return the expected result");
            }
        Check(Json.Serialize(report) == sourceBefore, "Read-only preview changed physical source passports");
        var manifest = new { status = failures == 0 ? "PASS" : "FAIL", checks, failures, reasons, images, layouts,
            shared_physical_brackets = shared.Count, physical_rail_rows = view.PreviewRows.Count,
            runtime = Environment.OSVersion.ToString(), live_autocad_checked = false,
            scope = "Actual QuantityTablePreview/View with real engine/producer fixture; native window, rows, warnings, scroll, tooltips, actions; no AutoCAD host" };
        File.WriteAllText(Path.Combine(output, "ui_checks.json"), Json.Serialize(manifest));
        Console.WriteLine("Connection actual WinForms: " + checks + " checks, " + failures + " failures; " + images.Count + " images");
        return failures == 0 ? 0 : 1;
    }
}
