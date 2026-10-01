using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace AFramePlugin
{
    // A detached, explicitly reviewed input for one dimensional sketch.
    // No CAD, renderer, process launch, catalogue selection or calculation here.
    public sealed class FrameNodeForm : FrameBoundedForm
    {
        private readonly FrameSolutionSelection _selection;
        private readonly FrameParameterContext _context;
        private readonly TextBox _profile = Input("profile_near_x"), _membrane = Input("membrane_outer_x");
        private readonly ComboBox _surface = new ComboBox { Name = "clearance_surface", Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 420 };
        private readonly Label _surfaceNote = new Label();
        private readonly Panel _scroll = new Panel { Name = "node_scroll", Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TextBox _review = ReadOnlyText("node_review"), _status = ReadOnlyText("node_status");
        private readonly Button _check = Action("check_node", "Проверить схему"), _back = Action("back_node", "Назад"),
            _insert = Action("insert_node", "Вставить схему");
        private Control _editor;
        private FrameNodeGeometryInput _reviewedInput, _acceptedInput;
        private FrameNodeGeometryResult _geometry;
        private bool _loading;

        public FrameNodeGeometryInput Result { get { return _acceptedInput == null ? null : _acceptedInput.Clone(); } }
        public FrameNodeGeometryResult Geometry { get { return _geometry; } }

        private sealed class SurfaceOption
        {
            internal string Kind, Caption;
            public override string ToString() { return Caption; }
        }

        public FrameNodeForm(FrameSolutionSelection selection, FrameParameterContext context, FrameNodeGeometryInput initialInput = null)
        {
            if (selection == null || context == null)
                throw new FrameNodeGeometryException("E_NODE_CONTEXT", "Для схемы нужны параметры одной явно привязанной зоны.");
            _selection = FrameNodeGeometry.CloneSelection(selection); _context = context.Clone(); _context.ValidateSelection(_selection);
            var initial = initialInput == null ? FrameNodeGeometryInput.CreateDefault() : initialInput.Clone();
            Name = "frame_node"; Text = "ATFNODE — размерная схема";
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Font; Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(900, 650); MinimumSize = new Size(760, 530);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            var heading = Wrapped("Размерная схема известных плоскостей. Основание стены: x = 0; положительное x направлено наружу. Это подготовительная схема, не рабочий узел и не динамический блок.");
            heading.Name = "node_heading"; layout.Controls.Add(heading, 0, 0); WrapToWidth(layout, heading);

            _editor = BuildEditor(); _scroll.Controls.Add(_editor);
            _review.Visible = false; _review.Dock = DockStyle.Fill; _scroll.Controls.Add(_review);
            layout.Controls.Add(_scroll, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            _status.Dock = DockStyle.Fill; _status.BorderStyle = BorderStyle.None; _status.BackColor = SystemColors.Control;
            footer.Controls.Add(_status, 0, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var cancel = Action("cancel_node", "Отмена"); cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancel); buttons.Controls.Add(_insert); buttons.Controls.Add(_check); buttons.Controls.Add(_back);
            footer.Controls.Add(buttons, 0, 1); layout.Controls.Add(footer, 0, 2); Controls.Add(layout);
            CancelButton = cancel; AcceptButton = _check;

            _loading = true;
            try
            {
                _surface.Items.Add(new SurfaceOption { Kind = FrameNodeClearanceSurface.Unknown, Caption = "Не задана" });
                _surface.Items.Add(new SurfaceOption { Kind = FrameNodeClearanceSurface.Layers, Caption = "Граница слоёв" });
                _surface.Items.Add(new SurfaceOption { Kind = FrameNodeClearanceSurface.Membrane, Caption = "Заданная мембрана" });
                for (int i = 0; i < _surface.Items.Count; i++)
                    if (((SurfaceOption)_surface.Items[i]).Kind == initial.clearance_surface.kind) _surface.SelectedIndex = i;
                _profile.Text = Number(initial.profile_near_face_x_mm); _membrane.Text = Number(initial.clearance_surface.membrane_outer_x_mm);
            }
            finally { _loading = false; }
            _profile.TextChanged += delegate { InvalidateReview(); };
            _membrane.TextChanged += delegate { InvalidateReview(); };
            _surface.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                if (SurfaceKind != FrameNodeClearanceSurface.Membrane) _membrane.Text = "";
                SurfaceState(); InvalidateReview();
            };
            _check.Click += delegate { EvaluateInput(); };
            _back.Click += delegate { InvalidateReview(); };
            _insert.Click += delegate
            {
                if (_reviewedInput == null || _geometry == null || !_geometry.CanInsert || !_review.Visible) return;
                _acceptedInput = _reviewedInput.Clone(); DialogResult = DialogResult.OK; Close();
            };
            SurfaceState(); InvalidateReview();
        }

        private Control BuildEditor()
        {
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 0, 8, 0) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var known = Group("Параметры выбранной зоны — только просмотр");
            var values = ReadOnlyText("node_project_parameters"); values.Height = 116; values.Dock = DockStyle.Top;
            values.Text = ProjectText(); known.Controls.Add(values);
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.Controls.Add(known, 0, 0);

            var local = Group("Данные только для этого экземпляра схемы");
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            Row(grid, "Координата ближайшей поверхности ГП x, мм (необязательно)", _profile);
            Row(grid, "Поверхность для проверки просвета", _surface);
            Row(grid, "Наружная поверхность мембраны x, мм", _membrane);
            _surfaceNote.AutoSize = true; _surfaceNote.Dock = DockStyle.Fill; _surfaceNote.Name = "surface_note";
            _surfaceNote.Margin = new Padding(3, 8, 3, 6);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.Controls.Add(_surfaceNote, 0, grid.RowCount++); grid.SetColumnSpan(_surfaceNote, 2);
            WrapToWidth(grid, _surfaceNote);
            var localNote = Wrapped("Эти координаты заявляются пользователем только для схемы. Параметры проекта, исключения зоны и каркас не меняются. Пустая координата остаётся неизвестной.");
            localNote.Name = "local_input_note";
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.Controls.Add(localNote, 0, grid.RowCount++); grid.SetColumnSpan(localNote, 2);
            WrapToWidth(grid, localNote); local.Controls.Add(grid);
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.Controls.Add(local, 0, 1);

            var source = Group("Источник и ограничения");
            var text = ReadOnlyText("node_source"); text.Height = 150; text.Dock = DockStyle.Top;
            text.Text = "АТР Вектор-1, 2015. Тип 1, керамогранит; КР2 + УК + ГП. PDF " + _selection.node.pdf_page + ", лист " + _selection.node.sheet + ".\r\n" +
                "SHA-256 источника: " + _selection.source_sha256 + "\r\nРедакция каталога: " + _selection.catalog_revision + "\r\n" +
                "Вынос наружной поверхности облицовки не равен просвету до ГП или плечу расчёта. Не заданы посадка КР2/УК, отверстия, анкеры, крепление облицовки и регулировка. Автоматический подбор, монтаж и прочность не подтверждаются.\r\n" +
                "Схема вставляется в плоскости XY МСК. Точка выбирается после просмотра; сейчас в DWG ничего не записывается.";
            source.Controls.Add(text); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.Controls.Add(source, 0, 2);
            return content;
        }

        private string ProjectText()
        {
            var text = new StringBuilder();
            text.AppendLine("Зона: " + _context.zone.zone_id + ". Проект: " + _context.project.project_id + ".");
            text.AppendLine("Редакция проекта: " + _context.project.revision + "; привязки зоны: " + _context.zone.revision + ".");
            text.AppendLine("Вынос наружной поверхности облицовки от конструктивной стены: " +
                (_selection.geometry.cladding_front_offset_mm.HasValue ? Number(_selection.geometry.cladding_front_offset_mm) + " мм" : "не задано") +
                " [" + Origin("geometry.cladding_front_offset_mm") + "].");
            var layers = new List<string>(); foreach (double value in _selection.geometry.insulation_layers_mm) layers.Add(FrameNodeGeometry.FormatMm(value));
            text.Append("Слои утеплителя: " + (layers.Count == 0 ? "не заданы" : string.Join("; ", layers.ToArray()) + " мм") +
                " [" + Origin("geometry.insulation_layers_mm") + "].");
            return text.ToString();
        }
        private string Origin(string path)
        {
            string origin = _context.origins[path];
            return origin == "project_default" ? "из проекта" : origin == "zone_clear" ? "в зоне явно не задано" : "своё значение зоны";
        }
        private string SurfaceKind { get { var item = _surface.SelectedItem as SurfaceOption; return item == null ? FrameNodeClearanceSurface.Unknown : item.Kind; } }
        private void SurfaceState()
        {
            bool membrane = SurfaceKind == FrameNodeClearanceSurface.Membrane; _membrane.Enabled = membrane;
            _surfaceNote.Text = SurfaceKind == FrameNodeClearanceSurface.Layers ?
                "Выбор «Граница слоёв» означает ваше явное заявление: наружная поверхность мембраны совпадает с наружной границей всех указанных слоёв утеплителя. Нужен непустой список слоёв." : membrane ?
                "Задайте координату наружной поверхности мембраны от основания стены. Её толщина не назначается автоматически; допустимость положения проверяется по известным слоям." :
                "Поверхность для проверки не заявлена. Даже при известных слоях локальный просвет не проверяется автоматически.";
        }
        private void InvalidateReview()
        {
            if (_loading) return;
            _reviewedInput = null; _acceptedInput = null; _geometry = null;
            _review.Clear();
            _review.Visible = false; _editor.Visible = true;
            _check.Visible = true; _back.Visible = false; _insert.Enabled = false;
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = "Проверьте текущие данные перед вставкой. Изменение любого ввода требует нового просмотра. Неизвестные координаты не подставляются.";
            AcceptButton = _check;
        }
        private FrameNodeGeometryInput ReadInput()
        {
            var input = FrameNodeGeometryInput.CreateDefault();
            input.profile_near_face_x_mm = Parse(_profile.Text, "Координата ближайшей поверхности ГП", true, false);
            input.clearance_surface.kind = SurfaceKind;
            input.clearance_surface.membrane_outer_x_mm = SurfaceKind == FrameNodeClearanceSurface.Membrane ?
                Parse(_membrane.Text, "Наружная поверхность мембраны", false, true) : null;
            return input.Clone();
        }
        private void EvaluateInput()
        {
            try
            {
                var input = ReadInput(); var result = FrameNodeGeometry.Evaluate(_selection, input);
                var mounting = FrameMountingAssessment.Evaluate(_selection, result);
                _reviewedInput = input; _geometry = result;
                // Native multiline TextBox requires CRLF; the numerical core remains platform-neutral.
                _review.Text = (ProjectText() + "\r\n\r\n" + result.ReviewText() + "\r\n" + mounting.ReviewText())
                    .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                _scroll.AutoScrollPosition = Point.Empty; _editor.Visible = false; _review.Visible = true; _review.BringToFront();
                _review.Select(0, 0);
                _check.Visible = false; _back.Visible = true; _insert.Enabled = result.CanInsert;
                _status.ForeColor = result.CanInsert ? SystemColors.ControlText : Color.Firebrick;
                _status.Text = !result.CanInsert ? "Вставка недоступна. Причины и числа приведены в просмотре; вернитесь к вводу для исправления." :
                    result.ClearanceStatus == "pass" ? "Подтверждена только проверка локального просвета: " + result.GapText + " мм. Монтаж и прочность не проверены." :
                    "Частичная размерная схема. Проверка локального просвета не выполнена. Это не рабочий узел.";
                AcceptButton = result.CanInsert ? _insert : _back;
            }
            catch (FrameNodeGeometryException error) { InvalidateReview(); _status.ForeColor = Color.Firebrick; _status.Text = error.Message; }
        }
        private static double? Parse(string text, string title, bool optional, bool allowZero)
        {
            if (optional && string.IsNullOrWhiteSpace(text)) return null;
            string normalized = (text ?? "").Trim().Replace(',', '.');
            double value;
            if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value) || (allowZero ? value < 0 : value <= 0))
                throw new FrameNodeGeometryException("E_NODE_INPUT_NUMBER", title + ": введите конечное число " + (allowZero ? "не меньше нуля" : "больше нуля") + " в миллиметрах.");
            try
            {
                // Preserve the user's decimal declaration before it becomes a
                // double DTO. The core then owns all dimensional arithmetic.
                if (FrameNodeJson.Normalized(normalized) != FrameNodeJson.Normalized(FrameNodeGeometry.FormatMm(value)))
                    throw new FrameNodeGeometryException("E_NODE_INPUT_PRECISION", title + ": введённое число невозможно сохранить без округления. Проверьте точность исходных данных.");
            }
            catch (FormatException) { throw new FrameNodeGeometryException("E_NODE_INPUT_PRECISION", title + ": неподдерживаемый числовой ввод. Проверьте исходные данные."); }
            catch (OverflowException) { throw new FrameNodeGeometryException("E_NODE_INPUT_PRECISION", title + ": избыточная точность числового ввода. Проверьте исходные данные."); }
            return value;
        }
        private static string Number(double? value) { return value.HasValue ? FrameNodeGeometry.FormatMm(value.Value) : ""; }
        private static TextBox Input(string name) { return new TextBox { Name = name, Dock = DockStyle.Fill }; }
        private static TextBox ReadOnlyText(string name)
        { return new TextBox { Name = name, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window }; }
        private static Button Action(string name, string caption)
        { return new Button { Name = name, Text = caption, AutoSize = true, MinimumSize = new Size(100, 30) }; }
        private static GroupBox Group(string caption)
        { return new GroupBox { Text = caption, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 20, 10, 10), Margin = new Padding(0, 5, 0, 6) }; }
        private static Label Wrapped(string text)
        { return new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 8) }; }
        private static void Row(TableLayoutPanel grid, string title, Control input)
        {
            int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = Wrapped(title); input.Margin = new Padding(3, 6, 3, 8);
            grid.Controls.Add(label, 0, row); grid.Controls.Add(input, 1, row);
        }
        private static void WrapToWidth(Control owner, Label label)
        {
            EventHandler update = delegate
            {
                int width = Math.Max(1, owner.ClientSize.Width - owner.Padding.Horizontal - label.Margin.Horizontal);
                if (label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
            };
            owner.ClientSizeChanged += update; label.FontChanged += update;
        }
    }
}
