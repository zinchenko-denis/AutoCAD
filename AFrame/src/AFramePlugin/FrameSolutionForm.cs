using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace AFramePlugin
{
    // Operation-local UI data only. Commands own DWG reads, freshness and writes.
    public sealed class FrameSolutionEditorContext
    {
        public string Title, Instruction, ReadOnlyReason, ScopeCaption;
        public string ApplyText = "Проверить изменения", SaveText = "Сохранить параметры", ClearText;
        public bool LockIdentity, HideClear, OverrideMode;
        public FrameSolutionSelection ProjectDefaults;
        public HashSet<string> OverridePaths = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, string> Origins = new Dictionary<string, string>(StringComparer.Ordinal);
        public Func<FrameSolutionSelection, HashSet<string>, bool, string> Review;

        internal FrameSolutionEditorContext Clone()
        {
            var copy = (FrameSolutionEditorContext)MemberwiseClone();
            copy.ProjectDefaults = ProjectDefaults == null ? null : ProjectDefaults.Clone();
            copy.OverridePaths = new HashSet<string>(OverridePaths, StringComparer.Ordinal);
            copy.Origins = new Dictionary<string, string>(Origins, StringComparer.Ordinal);
            return copy;
        }
    }

    // Edits a detached declaration. Lists come from the embedded canonical
    // catalogue snapshot; the dialog does no CAD access or engineering selection.
    public class FrameSolutionForm : Form
    {
        private readonly FrameSolutionSelection _draft;
        private readonly Dictionary<string, object> _catalog;
        private readonly bool _readOnly;
        private readonly ComboBox _node = Choice("node"), _bracketExecution = Choice("bracket_execution"),
            _bracketWidth = Choice("bracket_width"), _bracketLength = Choice("bracket_length"),
            _extenderExecution = Choice("extender_execution"), _extenderWidth = Choice("extender_width"),
            _extenderLength = Choice("extender_length"), _extenderThickness = Choice("extender_thickness"),
            _profileExecution = Choice("profile_execution"), _profileA = Choice("profile_a");
        private readonly TextBox _profileB = Input("profile_b"), _profileThickness = Input("profile_thickness"),
            _offset = Input("cladding_front_offset"), _layers = Input("insulation_layers");
        private readonly TextBox _source = new TextBox(), _error = new TextBox();
        private readonly Button _apply = new Button(), _clear = new Button();
        private readonly Panel _scroll = new Panel();
        private readonly FrameSolutionEditorContext _context;
        private readonly Dictionary<string, Control> _fields = new Dictionary<string, Control>(StringComparer.Ordinal);
        private readonly Dictionary<string, CheckBox> _ownFields = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly Dictionary<string, Label> _origins = new Dictionary<string, Label>(StringComparer.Ordinal);
        private Control _editorContent;
        private TextBox _review;
        private FrameSolutionSelection _pendingSelection;
        private HashSet<string> _pendingOverrides;
        private bool _pendingClear, _wasCleared;
        private FrameSolutionSelection _result;
        private readonly string _loadedInvalidReason;
        private bool _loading;
        private bool _constrainingWindow;

        public FrameSolutionSelection Result { get { return _result == null ? null : _result.Clone(); } }
        public bool WasCleared { get { return _wasCleared; } }
        public HashSet<string> AcceptedOverridePaths { get { return new HashSet<string>(_pendingOverrides ?? CurrentOverrides(), StringComparer.Ordinal); } }

        // WinForms can retain the requested ClientSize when Windows has already
        // limited the HWND to the monitor's maximum tracking size. That leaves
        // managed layout/DrawToBitmap believing invisible space is available.
        // Limit the request before the base class records its client-size cache.
        protected override void SetClientSizeCore(int width, int height)
        {
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            FitMinimum(work.Size);
            Size border = SizeFromClientSize(Size.Empty);
            base.SetClientSizeCore(Math.Min(width, Math.Max(1, work.Width - border.Width)),
                Math.Min(height, Math.Max(1, work.Height - border.Height)));
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (_constrainingWindow) { base.SetBoundsCore(x, y, width, height, specified); return; }
            Rectangle work = Screen.FromPoint(new Point(x, y)).WorkingArea;
            FitMinimum(work.Size);
            width = Math.Min(Math.Max(width, MinimumSize.Width), work.Width);
            height = Math.Min(Math.Max(height, MinimumSize.Height), work.Height);
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            base.SetBoundsCore(x, y, width, height, specified);
        }

        private void FitMinimum(Size available)
        {
            // Font autoscaling also scales MinimumSize. A minimum larger than
            // the working area must not force the action buttons off-screen.
            if (_constrainingWindow) return;
            Size minimum = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
            if (minimum == MinimumSize) return;
            _constrainingWindow = true;
            try { MinimumSize = minimum; }
            finally { _constrainingWindow = false; }
        }

        private sealed class Option
        {
            internal string Id, Caption;
            internal double Number;
            public override string ToString() { return Caption; }
        }

        public FrameSolutionForm(FrameSolutionSelection selection, bool readOnly = false, FrameSolutionEditorContext context = null)
        {
            _context = context == null ? null : context.Clone();
            _draft = selection == null ? FrameSolutionSelection.CreateDefault() : selection.Clone();
            _catalog = FrameSolutionSelection.CatalogSnapshot();
            _readOnly = readOnly;
            _loadedInvalidReason = selection == null ? null : selection.Validate();
            Text = _context != null && _context.Title != null ? _context.Title : "ATFRAME — решение и каталог";
            Name = "frame_solution";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(900, 650);
            MinimumSize = new Size(760, 530);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Padding = new Padding(10) };
            // An implicit AutoSize column can keep the pre-scale preferred width
            // of a child after the font/window changes. The viewport must own
            // the width; only the middle panel is allowed to scroll.
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            heading.Controls.Add(new Label { Dock = DockStyle.Fill, AutoSize = false,
                Text = _context != null && _context.Instruction != null ? _context.Instruction : readOnly ? "Только кляммеры: решение существующего каркаса доступно для просмотра. Изменять или снимать его в этом режиме нельзя." :
                    "Выберите решение и каждое исполнение явно. Принадлежность исторической номенклатуре не подтверждает совместимость сочетания, прочность или доступность современного изделия." });
            var solution = Object(_catalog, "solution");
            var source = Object(_catalog, "source");
            _node.Items.Add(new Option { Id = TextValue(solution, "solution_id"),
                Caption = TextValue(source, "title") + " — " + TextValue(solution, "title") });
            heading.Controls.Add(_node);
            layout.Controls.Add(heading, 0, 0);

            _scroll.Name = "solution_scroll";
            _scroll.Dock = DockStyle.Fill;
            _scroll.AutoScroll = true;
            _scroll.Margin = new Padding(0, 4, 0, 0);
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, RowCount = 3, Margin = Padding.Empty, Padding = new Padding(0, 0, 8, 0) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var bracket = Group("Кронштейн КР2", "kr2");
            Row(bracket.Item2, "Исполнение", _bracketExecution);
            Row(bracket.Item2, "Номинальная ширина, мм", _bracketWidth);
            Row(bracket.Item2, "Длина L, мм", _bracketLength);
            Row(bracket.Item2, "Толщина по эскизу, мм", new Label { AutoSize = true, Text =
                Number(Convert.ToDouble(Object(Object(Family("kr2"), "dimensions"), "thickness_mm")["value"], CultureInfo.InvariantCulture)) });
            var extender = Group("Кронштейн-удлинитель УК", "uk");
            Row(extender.Item2, "Исполнение", _extenderExecution);
            Row(extender.Item2, "Номинальная ширина, мм", _extenderWidth);
            Row(extender.Item2, "Длина L, мм", _extenderLength);
            Row(extender.Item2, "Толщина c, мм", _extenderThickness);
            var profile = Group("Направляющая ГП", "gp");
            Row(profile.Item2, "Исполнение", _profileExecution);
            Row(profile.Item2, "Полка a из альбома, мм", _profileA);
            Row(profile.Item2, "Полка b по проекту, мм", _profileB);
            Row(profile.Item2, "Толщина c по проекту, мм", _profileThickness);
            Note(profile.Item2, "b и c обязательны. Их ввод не подтверждает расчётные характеристики сечения.");
            var geometry = Group("Параметры проекта — необязательно", null);
            Row(geometry.Item2, "Вынос наружной поверхности облицовки от конструктивной стены, мм", _offset);
            Row(geometry.Item2, "Слои утеплителя, мм (через ;)", _layers);
            Note(geometry.Item2, "Пустое поле означает «не задано». Вынос не равен плечу расчёта. Длина кронштейна по этим данным не подбирается.");
            var provenance = Group("Источник и границы применения", null);
            _source.Name = "source_summary";
            _source.ReadOnly = true; _source.Multiline = true; _source.ScrollBars = ScrollBars.Vertical;
            _source.Dock = DockStyle.Fill; _source.Height = 176; _source.BackColor = SystemColors.Window;
            provenance.Item2.Controls.Add(_source, 0, 0); provenance.Item2.SetColumnSpan(_source, 2);
            content.Controls.Add(bracket.Item1, 0, 0); content.Controls.Add(extender.Item1, 1, 0);
            content.Controls.Add(profile.Item1, 0, 1); content.Controls.Add(geometry.Item1, 1, 1);
            content.Controls.Add(provenance.Item1, 0, 2); content.SetColumnSpan(provenance.Item1, 2);
            _editorContent = content;
            _scroll.Controls.Add(content); layout.Controls.Add(_scroll, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            _error.Name = "selection_error"; _error.Dock = DockStyle.Fill; _error.ReadOnly = true; _error.Multiline = true;
            _error.BorderStyle = BorderStyle.None; _error.BackColor = SystemColors.Control;
            _error.ForeColor = Color.Firebrick; _error.ScrollBars = ScrollBars.Vertical;
            footer.Controls.Add(_error, 0, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var cancel = new Button { Name = "cancel_selection", Text = readOnly ? "Закрыть" : "Отмена", AutoSize = true,
                MinimumSize = new Size(95, 30), DialogResult = DialogResult.Cancel };
            _apply.Name = "apply_selection"; _apply.Text = "Применить выбор"; _apply.AutoSize = true; _apply.MinimumSize = new Size(145, 30);
            _clear.Name = "clear_selection"; _clear.Text = "Снять выбор решения"; _clear.AutoSize = true; _clear.MinimumSize = new Size(175, 30);
            if (_context != null)
            {
                _apply.Text = _context.ApplyText;
                if (_context.ClearText != null) _clear.Text = _context.ClearText;
                _clear.Visible = !_context.HideClear;
            }
            buttons.Controls.Add(cancel); buttons.Controls.Add(_apply); buttons.Controls.Add(_clear);
            footer.Controls.Add(buttons, 0, 1); layout.Controls.Add(footer, 0, 2);
            Controls.Add(layout); CancelButton = cancel; AcceptButton = readOnly ? cancel : _apply;

            FillCatalog();
            LoadSelection(selection);
            foreach (var combo in Choices()) combo.SelectedIndexChanged += delegate { if (!_loading) UpdateSummary(); };
            foreach (var input in new[] { _profileB, _profileThickness, _offset, _layers })
                input.TextChanged += delegate { if (!_loading) UpdateSummary(); };
            _apply.Click += delegate { ApplySelection(); };
            _clear.Click += delegate
            {
                if (_readOnly) return;
                if (_review != null) { BackToEditor(); return; }
                if (_context != null && _context.Review != null) BeginReview(null, true);
                else { _wasCleared = true; _result = null; DialogResult = DialogResult.OK; Close(); }
            };
            if (readOnly)
            {
                foreach (var combo in Choices()) combo.Enabled = false;
                foreach (var input in new[] { _profileB, _profileThickness, _offset, _layers }) input.ReadOnly = true;
                _apply.Enabled = false; _clear.Enabled = false;
            }
            if (_context != null && _context.LockIdentity) _node.Enabled = false;
            RefreshOrigins();
            UpdateSummary();
        }

        private IEnumerable<ComboBox> Choices()
        { return new[] { _node, _bracketExecution, _bracketWidth, _bracketLength, _extenderExecution, _extenderWidth,
            _extenderLength, _extenderThickness, _profileExecution, _profileA }; }
        private static ComboBox Choice(string name)
        { return new ComboBox { Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill,
            DropDownWidth = 480, IntegralHeight = true, MaxDropDownItems = 12 }; }
        private static TextBox Input(string name) { return new TextBox { Name = name, Dock = DockStyle.Fill }; }
        private Dictionary<string, object> Family(string id) { return Object(Object(_catalog, "families"), id); }
        private Tuple<GroupBox, TableLayoutPanel> Group(string title, string family)
        {
            if (family != null)
            {
                var source = Object(Family(family), "source");
                title += " — PDF " + Convert.ToString(source["pdf_page"], CultureInfo.InvariantCulture) + " / " + TextValue(source, "sheet");
            }
            var group = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 6, 8), Padding = new Padding(8, 20, 8, 8) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            group.Controls.Add(grid); return Tuple.Create(group, grid);
        }
        private void Row(TableLayoutPanel grid, string caption, Control input)
        {
            string path = FieldPath(input.Name);
            if (path != null)
            {
                _fields.Add(path, input);
                if (_context != null && (_context.OverrideMode || _context.Origins.Count > 0))
                {
                    var cell = new TableLayoutPanel { Name = input.Name + "_cell", Dock = DockStyle.Fill, AutoSize = true,
                        ColumnCount = 1, Margin = Padding.Empty };
                    cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    cell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); cell.Controls.Add(input, 0, 0);
                    var origin = new Label { Name = input.Name + "_origin", AutoSize = true, Dock = DockStyle.Fill,
                        ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 3) };
                    _origins.Add(path, origin);
                    if (_context.OverrideMode)
                    {
                        var own = new CheckBox { Name = input.Name + "_own", Text = "Своё значение", AutoSize = true,
                            Checked = _context.OverridePaths.Contains(path), Enabled = !_readOnly,
                            Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0) };
                        _ownFields.Add(path, own);
                        cell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); cell.Controls.Add(own, 0, 1);
                        own.CheckedChanged += delegate
                        {
                            if (_loading) return;
                            if (!own.Checked) LoadFieldFromProject(path);
                            RefreshOrigins(); UpdateSummary();
                        };
                    }
                    int originRow = _context.OverrideMode ? 2 : 1;
                    cell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); cell.Controls.Add(origin, 0, originRow);
                    EventHandler wrapOrigin = delegate
                    {
                        int width = Math.Max(1, cell.ClientSize.Width - origin.Margin.Horizontal);
                        if (origin.MaximumSize.Width != width) origin.MaximumSize = new Size(width, 0);
                    };
                    cell.ClientSizeChanged += wrapOrigin; origin.FontChanged += wrapOrigin;
                    input = cell;
                }
            }
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 5, 5, 5) }, 0, row);
            input.Margin = new Padding(3, 3, 3, 5); grid.Controls.Add(input, 1, row);
        }
        private static string FieldPath(string name)
        {
            switch (name)
            {
                case "bracket_execution": return "bracket.execution";
                case "bracket_width": return "bracket.nominal_width_mm";
                case "bracket_length": return "bracket.L_mm";
                case "extender_execution": return "extender.execution";
                case "extender_width": return "extender.nominal_width_mm";
                case "extender_length": return "extender.L_mm";
                case "extender_thickness": return "extender.thickness_mm";
                case "profile_execution": return "profile.execution";
                case "profile_a": return "profile.a_mm";
                case "profile_b": return "profile.b_mm";
                case "profile_thickness": return "profile.thickness_mm";
                case "cladding_front_offset": return "geometry.cladding_front_offset_mm";
                case "insulation_layers": return "geometry.insulation_layers_mm";
                default: return null;
            }
        }
        private HashSet<string> CurrentOverrides()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in _ownFields) if (pair.Value.Checked) paths.Add(pair.Key);
            return paths;
        }
        private void LoadFieldFromProject(string path)
        {
            if (_context == null || _context.ProjectDefaults == null) return;
            string[] parts = path.Split('.');
            object value = Object(_context.ProjectDefaults.ToDict(), parts[0])[parts[1]];
            Control field = _fields[path];
            var combo = field as ComboBox;
            if (combo != null)
            {
                combo.SelectedIndex = -1;
                if (value is string) SelectId(combo, (string)value);
                else if (value != null) SelectNumber(combo, Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }
            else if (path == "geometry.insulation_layers_mm")
            {
                var values = new List<string>();
                foreach (object item in (IEnumerable)value) values.Add(Number(Convert.ToDouble(item, CultureInfo.InvariantCulture)));
                field.Text = string.Join("; ", values.ToArray());
            }
            else field.Text = value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        private void RefreshOrigins()
        {
            foreach (var pair in _origins)
            {
                string origin; CheckBox own;
                bool local = _ownFields.TryGetValue(pair.Key, out own) && own.Checked;
                bool localEmpty = local && pair.Key.StartsWith("geometry.", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(_fields[pair.Key].Text);
                pair.Value.Text = local ? localEmpty ? "В зоне не задано" : "Зона: своё значение" :
                    _context.Origins.TryGetValue(pair.Key, out origin) ? origin : "Из параметров проекта";
                if (own != null)
                {
                    var box = _fields[pair.Key] as TextBox;
                    if (box != null) box.ReadOnly = _readOnly || !local;
                    else _fields[pair.Key].Enabled = !_readOnly && local;
                }
            }
        }
        private static void Note(TableLayoutPanel grid, string text)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var note = new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 7, 3, 3) };
            grid.Controls.Add(note, 0, row); grid.SetColumnSpan(note, 2);
            // A label spanning percentage columns otherwise measures itself as
            // one line before the final column width is known. Constrain its
            // preferred width so AutoSize reserves every wrapped line.
            EventHandler wrap = delegate
            {
                int width = Math.Max(1, grid.ClientSize.Width - grid.Padding.Horizontal - note.Margin.Horizontal);
                if (note.MaximumSize.Width != width) note.MaximumSize = new Size(width, 0);
            };
            grid.ClientSizeChanged += wrap;
            note.FontChanged += wrap;
            wrap(null, EventArgs.Empty);
        }
        private void FillCatalog()
        {
            foreach (var item in (IEnumerable)_catalog["executions"])
            {
                var execution = (Dictionary<string, object>)item;
                foreach (var combo in new[] { _bracketExecution, _extenderExecution, _profileExecution })
                    combo.Items.Add(new Option { Id = TextValue(execution, "id"), Caption = TextValue(execution, "title") });
            }
            Dimension(_bracketWidth, "kr2", "nominal_width_mm"); Dimension(_bracketLength, "kr2", "L_mm");
            Dimension(_extenderWidth, "uk", "nominal_width_mm"); Dimension(_extenderLength, "uk", "L_mm");
            Dimension(_extenderThickness, "uk", "thickness_mm"); Dimension(_profileA, "gp", "a_mm");
        }
        private void Dimension(ComboBox combo, string family, string dimension)
        {
            var rule = Object(Object(Family(family), "dimensions"), dimension);
            object values;
            if (rule.TryGetValue("values", out values))
                foreach (object value in (IEnumerable)values) AddNumber(combo, Convert.ToDouble(value, CultureInfo.InvariantCulture));
            else
            {
                double minimum = Convert.ToDouble(rule["minimum"], CultureInfo.InvariantCulture),
                    maximum = Convert.ToDouble(rule["maximum"], CultureInfo.InvariantCulture),
                    step = Convert.ToDouble(rule["step"], CultureInfo.InvariantCulture);
                for (double value = minimum; value <= maximum; value += step) AddNumber(combo, value);
            }
        }
        private static void AddNumber(ComboBox combo, double value) { combo.Items.Add(new Option { Number = value, Caption = Number(value) }); }
        private void LoadSelection(FrameSolutionSelection selection)
        {
            if (selection == null) return;
            _loading = true;
            try
            {
                SelectId(_node, selection.solution_id);
                if (selection.bracket != null)
                { SelectId(_bracketExecution, selection.bracket.execution); SelectNumber(_bracketWidth, selection.bracket.nominal_width_mm); SelectNumber(_bracketLength, selection.bracket.L_mm); }
                if (selection.extender != null)
                { SelectId(_extenderExecution, selection.extender.execution); SelectNumber(_extenderWidth, selection.extender.nominal_width_mm);
                    SelectNumber(_extenderLength, selection.extender.L_mm); SelectNumber(_extenderThickness, selection.extender.thickness_mm); }
                if (selection.profile != null)
                { SelectId(_profileExecution, selection.profile.execution); SelectNumber(_profileA, selection.profile.a_mm);
                    _profileB.Text = OptionalNumber(selection.profile.b_mm); _profileThickness.Text = OptionalNumber(selection.profile.thickness_mm); }
                if (selection.geometry != null)
                {
                    _offset.Text = OptionalNumber(selection.geometry.cladding_front_offset_mm);
                    var layers = new List<string>();
                    foreach (double value in selection.geometry.insulation_layers_mm ?? new List<double>()) layers.Add(Number(value));
                    _layers.Text = string.Join("; ", layers.ToArray());
                }
            }
            finally { _loading = false; }
        }
        private static void SelectId(ComboBox combo, string id)
        { for (int i = 0; i < combo.Items.Count; i++) if (((Option)combo.Items[i]).Id == id) { combo.SelectedIndex = i; return; } }
        private static void SelectNumber(ComboBox combo, double value)
        { for (int i = 0; i < combo.Items.Count; i++) if (((Option)combo.Items[i]).Number == value) { combo.SelectedIndex = i; return; } }
        private static string Id(ComboBox combo, string name)
        { var item = combo.SelectedItem as Option; if (item == null) throw new FrameSolutionSelectionException("Выберите «" + name + "»."); return item.Id; }
        private static double SelectedNumber(ComboBox combo, string name)
        { var item = combo.SelectedItem as Option; if (item == null) throw new FrameSolutionSelectionException("Выберите «" + name + "»."); return item.Number; }
        private static double? ParseNumber(string text, string name, bool optional)
        {
            if (string.IsNullOrWhiteSpace(text) && optional) return null;
            double value;
            if (!double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new FrameSolutionSelectionException("«" + name + "»: введите положительное конечное число в миллиметрах.");
            return value;
        }
        private FrameSolutionSelection Candidate()
        {
            Id(_node, "Решение по альбому");
            var candidate = _draft.Clone();
            if (candidate.bracket == null || candidate.extender == null || candidate.profile == null || candidate.geometry == null)
                throw new FrameSolutionSelectionException("Сохранённое решение неполное. Снимите выбор и задайте его заново.");
            candidate.bracket.execution = Id(_bracketExecution, "Исполнение КР2");
            candidate.bracket.nominal_width_mm = SelectedNumber(_bracketWidth, "Номинальная ширина КР2");
            candidate.bracket.L_mm = SelectedNumber(_bracketLength, "Длина КР2");
            candidate.extender.execution = Id(_extenderExecution, "Исполнение УК");
            candidate.extender.nominal_width_mm = SelectedNumber(_extenderWidth, "Номинальная ширина УК");
            candidate.extender.L_mm = SelectedNumber(_extenderLength, "Длина УК");
            candidate.extender.thickness_mm = SelectedNumber(_extenderThickness, "Толщина УК");
            candidate.profile.execution = Id(_profileExecution, "Исполнение ГП");
            candidate.profile.a_mm = SelectedNumber(_profileA, "Полка a профиля");
            candidate.profile.b_mm = ParseNumber(_profileB.Text, "Полка b по проекту", false);
            candidate.profile.thickness_mm = ParseNumber(_profileThickness.Text, "Толщина c по проекту", false);
            candidate.geometry.cladding_front_offset_mm = ParseNumber(_offset.Text, "Вынос до наружной поверхности облицовки", true);
            candidate.geometry.insulation_layers_mm = new List<double>();
            if (!string.IsNullOrWhiteSpace(_layers.Text))
                foreach (string layer in _layers.Text.Split(';'))
                    candidate.geometry.insulation_layers_mm.Add(ParseNumber(layer, "Толщина слоя утеплителя", false).Value);
            string invalid = candidate.Validate();
            if (invalid != null) throw new FrameSolutionSelectionException(invalid);
            return candidate;
        }
        private void ApplySelection()
        {
            if (_readOnly) return;
            try
            {
                if (_review != null)
                {
                    _result = _pendingSelection == null ? null : _pendingSelection.Clone();
                    _wasCleared = _pendingClear;
                    DialogResult = DialogResult.OK; Close(); return;
                }
                var candidate = Candidate();
                if (_context != null && _context.Review != null) { BeginReview(candidate, false); return; }
                _result = candidate; DialogResult = DialogResult.OK; Close();
            }
            catch (FrameSolutionSelectionException error) { _error.Text = error.Message; }
        }
        private void BeginReview(FrameSolutionSelection candidate, bool clear)
        {
            try
            {
                var paths = CurrentOverrides();
                string preview = _context.Review(candidate, paths, clear);
                _pendingSelection = candidate; _pendingOverrides = paths; _pendingClear = clear;
                _review = new TextBox { Name = "parameter_review", Multiline = true, ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window, Text = preview };
                _scroll.AutoScrollPosition = Point.Empty;
                _editorContent.Visible = false; _scroll.Controls.Add(_review); _review.BringToFront();
                _node.Enabled = false;
                _apply.Text = _context.SaveText;
                _clear.Visible = true; _clear.Text = "Назад";
                _error.Text = "Проверьте изменения и последствия. Пока они не записаны в чертёж.";
            }
            catch (FrameSolutionSelectionException error) { _error.Text = error.Message; }
        }
        private void BackToEditor()
        {
            _scroll.Controls.Remove(_review); _review.Dispose(); _review = null;
            _editorContent.Visible = true;
            _apply.Text = _context.ApplyText; _clear.Text = _context.ClearText ?? "Снять выбор решения";
            _clear.Visible = !_context.HideClear;
            _node.Enabled = !_readOnly && !_context.LockIdentity;
            _pendingSelection = null; _pendingOverrides = null; _pendingClear = false;
            UpdateSummary();
        }
        private void UpdateSummary()
        {
            RefreshOrigins();
            var source = Object(_catalog, "source"); var solution = Object(_catalog, "solution");
            var sb = new StringBuilder();
            if (_context != null && !string.IsNullOrWhiteSpace(_context.ScopeCaption)) sb.AppendLine(_context.ScopeCaption);
            sb.AppendLine(TextValue(source, "title") + ". " + TextValue(source, "issuer_as_printed"));
            if (_loadedInvalidReason != null)
                sb.AppendLine("Сохранённый выбор не соответствует текущему каталогу. Его редакция: " + (_draft.catalog_revision ?? "не указана") + ".");
            sb.AppendLine("Узел: PDF " + Convert.ToString(Object(solution, "node")["pdf_page"], CultureInfo.InvariantCulture) +
                ", лист " + TextValue(Object(solution, "node"), "sheet") + ". Только вертикальная схема под керамогранит.");
            foreach (var pair in new[] { Tuple.Create("КР2", _bracketExecution), Tuple.Create("УК", _extenderExecution), Tuple.Create("ГП", _profileExecution) })
                sb.AppendLine(pair.Item1 + ": " + (pair.Item2.SelectedItem == null ? "исполнение не выбрано" : pair.Item2.SelectedItem.ToString()) + ".");
            sb.AppendLine("Совместимость сочетания, сечения, неподвижные/подвижные соединения, стыки и комплект метизов не проверены. Автоматический подбор недоступен.");
            sb.AppendLine("Местный просвет по узлу не равен выносу облицовки. Диапазон регулировки узла и формула длины кронштейна не подтверждены.");
            sb.AppendLine(TextValue(solution, "note"));
            sb.AppendLine("SHA-256 источника: " + TextValue(source, "sha256"));
            sb.Append("Редакция реестра: " + TextValue(_catalog, "revision"));
            _source.Text = sb.ToString();
            if (_loadedInvalidReason != null) _error.Text = _loadedInvalidReason + " Снимите выбор и задайте его заново.";
            else if (_readOnly) _error.Text = _context != null && _context.ReadOnlyReason != null ? _context.ReadOnlyReason :
                "Просмотр. Изменение решения существующего каркаса в режиме «только кляммеры» запрещено.";
            else if (_node.SelectedIndex < 0) _error.Text = "Решение не выбрано. Автоматических назначений нет.";
            else if (_context != null && _context.OverrideMode) _error.Text = "«Своё значение» сохраняется как переопределение, даже если совпадает с проектом. Снимите флажок, чтобы наследовать. Пустое своё необязательное поле означает «не задано».";
            else if (_context != null && _context.Review != null) _error.Text = "Изменения вступят в силу только после просмотра и сохранения. Построение каркаса здесь не выполняется.";
            else _error.Text = "Применение сохранит декларацию. Шаги по расчёту для этого каталожного выбора недоступны; выберите ручную расстановку по проекту в основном окне.";
        }
        private static Dictionary<string, object> Object(Dictionary<string, object> source, string name)
        { return (Dictionary<string, object>)source[name]; }
        private static string TextValue(Dictionary<string, object> source, string name)
        { object value; return source.TryGetValue(name, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string OptionalNumber(double? value) { return value.HasValue ? Number(value.Value) : ""; }
    }
}
