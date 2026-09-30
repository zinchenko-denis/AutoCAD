using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using FacadeSafety;

namespace AFacadesPlugin
{
    internal sealed class ManualQuantityForm : Form
    {
        private sealed class Choice
        {
            internal string Value, Label;
            internal Choice(string value, string label) { Value = value; Label = label; }
            public override string ToString() { return Label; }
        }
        private sealed class FieldEditor
        { internal string Key; internal ComboBox Source, Value; }

        private readonly IList<ManualQuantityObservation> _observations;
        private readonly string _zoneId;
        private readonly ComboBox _sample = Select(), _kind = Select(), _role = Select(), _adapter = Select(), _axis = Select();
        private readonly CheckBox _onePart = new CheckBox { Text = "Каждый выбранный объект этого образца — одна физическая деталь", AutoSize = true };
        private readonly List<FieldEditor> _fields = new List<FieldEditor>();
        private readonly DataGridView _inventory;
        private readonly TextBox _status = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly TextBox _details = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly Dictionary<string, ManualQuantityEvaluation> _evaluations = new Dictionary<string, ManualQuantityEvaluation>(StringComparer.OrdinalIgnoreCase);
        private readonly Button _register = new Button { AutoSize = true, Enabled = false, Text = "Сначала проверьте соответствие" };
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };
        private List<object[]> _rows;
        private bool _loading;
        internal ManualQuantityPreview ApprovedPreview { get; private set; }

        internal ManualQuantityForm(IList<ManualQuantityObservation> observations, string zoneId)
        {
            _observations = observations; _zoneId = zoneId;
            Text = "ATFTABLE — регистрация ручных образцов";
            Font = new Font("Segoe UI", 9f); StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1220, 850); MinimumSize = new Size(1020, 740);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Зона: " + zoneId + ". Выбрано объектов: " + observations.Count + ". Геометрия измеряется в миллиметрах.\nРегистрация задаёт учёт деталей; статическая пригодность и комплектность узлов этим не подтверждаются." });
            var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 3 };
            for (int c = 0; c < 6; c++) settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, c % 2 == 0 ? 11 : 22));
            AddSetting(settings, "Образец", _sample, 0, 0); settings.SetColumnSpan(_sample, 5);
            AddSetting(settings, "Ведомость", _kind, 0, 1); AddSetting(settings, "Категория", _role, 2, 1);
            AddSetting(settings, "Геометрия", _adapter, 0, 2); AddSetting(settings, "Ось блока", _axis, 2, 2);
            layout.Controls.Add(settings); layout.Controls.Add(_onePart);
            var knownSamples = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in observations)
                if (!string.IsNullOrEmpty(source.sample_key) && knownSamples.Add(source.sample_key))
                    _sample.Items.Add(new Choice(source.sample_key, (_sample.Items.Count + 1) + ". " + ManualQuantityView.Label(source)));
            _kind.Items.Add(new Choice("cladding", "Облицовка")); _kind.Items.Add(new Choice("frame", "Подсистема"));
            _adapter.Items.Add(new Choice("boundary", "Замкнутый контур"));
            _adapter.Items.Add(new Choice("linear", "Линейная деталь"));
            _adapter.Items.Add(new Choice("symbol", "Условный знак, 1 шт."));
            _axis.Items.Add(new Choice("X", "Локальная X")); _axis.Items.Add(new Choice("Y", "Локальная Y"));
            var mappingPage = new TabPage("Соответствие полей");
            var mapping = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
            mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mapping.Controls.Add(new Label { Text = "Поле ведомости", AutoSize = true });
            mapping.Controls.Add(new Label { Text = "Источник значения", AutoSize = true });
            mapping.Controls.Add(new Label { Text = "Текст или точное имя свойства выбранного образца", AutoSize = true });
            AddField(mapping, "mark", "Марка / артикул (не каталог)"); AddField(mapping, "type", "Тип");
            AddField(mapping, "material", "Материал"); AddField(mapping, "coating", "Покрытие");
            AddField(mapping, "system", "Система"); AddField(mapping, "color", "Цвет");
            AddField(mapping, "width", "Сверка ширины, мм"); AddField(mapping, "height", "Сверка высоты, мм");
            AddField(mapping, "length", "Сверка длины, мм");
            mapping.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(1000, 0),
                Text = "Не заданные характеристики остаются неизвестными. Размеры берутся из выбранного способа измерения; поля сверки не заменяют геометрию. Имена атрибутов и динамических свойств выбираются явно; имя слоя не определяет изделие." });
            mapping.SetColumnSpan(mapping.Controls[mapping.Controls.Count - 1], 3);
            mappingPage.AutoScroll = true; mappingPage.Controls.Add(mapping); _tabs.TabPages.Add(mappingPage);
            var inventoryPage = new TabPage("Все выбранные объекты (" + observations.Count + ")");
            _inventory = CreateInventory();
            var inventoryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            inventoryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); inventoryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            inventoryLayout.Controls.Add(_inventory); inventoryLayout.Controls.Add(_details);
            inventoryPage.Controls.Add(inventoryLayout); _tabs.TabPages.Add(inventoryPage);
            layout.Controls.Add(_tabs); layout.Controls.Add(_status);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
            var check = new Button { Text = "Проверить", AutoSize = true };
            buttons.Controls.Add(cancel); buttons.Controls.Add(_register); buttons.Controls.Add(check); layout.Controls.Add(buttons);
            Controls.Add(layout); CancelButton = cancel; AcceptButton = check;
            check.Click += (s, e) => CheckMapping();
            _register.Click += (s, e) => { if (ApprovedPreview != null && ApprovedPreview.accepted_count > 0) { DialogResult = DialogResult.OK; Close(); } };
            _sample.SelectedIndexChanged += (s, e) => { RefreshFieldChoices(); InvalidatePreview(); };
            _kind.SelectedIndexChanged += (s, e) => { ChangeRoles(); InvalidatePreview(); };
            _role.SelectedIndexChanged += (s, e) => InvalidatePreview();
            _adapter.SelectedIndexChanged += (s, e) => InvalidatePreview();
            _axis.SelectedIndexChanged += (s, e) => InvalidatePreview();
            _onePart.CheckedChanged += (s, e) => InvalidatePreview();
            InvalidatePreview();
        }

        private void AddField(TableLayoutPanel panel, string key, string label)
        {
            var field = new FieldEditor { Key = key, Source = Select(), Value = new ComboBox { Dock = DockStyle.Fill, Enabled = false } };
            field.Source.Items.Add(new Choice("", "Не задано")); field.Source.Items.Add(new Choice("literal", "Текст"));
            field.Source.Items.Add(new Choice("attribute", "Атрибут")); field.Source.Items.Add(new Choice("dynamic", "Динамическое свойство"));
            field.Source.SelectedIndex = 0;
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            panel.Controls.Add(field.Source); panel.Controls.Add(field.Value); _fields.Add(field);
            field.Source.SelectedIndexChanged += (s, e) => { RefreshField(field); InvalidatePreview(); };
            field.Value.TextChanged += (s, e) => InvalidatePreview();
            field.Value.SelectedIndexChanged += (s, e) => InvalidatePreview();
        }
        private void RefreshFieldChoices() { foreach (var field in _fields) RefreshField(field); }
        private void RefreshField(FieldEditor field)
        {
            _loading = true;
            try
            {
                string prior = field.Value.Text, source = Value(field.Source);
                field.Value.Items.Clear(); field.Value.Enabled = source != "";
                field.Value.DropDownStyle = source == "literal" || source == "" ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
                var names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var item in _observations)
                {
                    if (item.sample_key != Value(_sample)) continue;
                    if (source == "attribute") foreach (string name in item.attributes.Keys) names.Add(name);
                    if (source == "dynamic") foreach (string name in item.dynamic_properties.Keys) names.Add(name);
                }
                foreach (string name in names) field.Value.Items.Add(name);
                if (source == "literal") field.Value.Text = prior;
                else if (names.Contains(prior)) field.Value.SelectedItem = prior;
                else field.Value.SelectedIndex = -1;
            }
            finally { _loading = false; }
        }
        private void ChangeRoles()
        {
            _role.Items.Clear();
            if (Value(_kind) == "cladding") _role.Items.Add(new Choice("cladding", "Деталь облицовки"));
            else if (Value(_kind) == "frame")
            {
                string[] values = { "rail", "hrail", "shina", "bracket", "clamp", "fitting" };
                string[] labels = { "Вертикальный профиль", "Горизонтальный профиль", "Шина", "Кронштейн", "Кляммер", "Соединительная деталь" };
                for (int i = 0; i < values.Length; i++) _role.Items.Add(new Choice(values[i], labels[i]));
            }
        }
        private void CheckMapping()
        {
            if (Value(_sample) == "" || Value(_kind) == "" || Value(_role) == "" || Value(_adapter) == "" || !_onePart.Checked)
            { _status.Text = "Выберите образец, ведомость, категорию и способ измерения. Подтвердите, что один объект соответствует одной физической детали."; return; }
            var rule = new ManualQuantityRule { rule_id = Guid.NewGuid().ToString("N"), revision = "1", kind = Value(_kind),
                role = Value(_role), adapter = Value(_adapter), axis = Value(_axis), sample_key = Value(_sample), one_physical_part = true };
            foreach (var field in _fields)
            {
                string source = Value(field.Source);
                if (source == "") continue;
                rule.fields[field.Key] = new ManualQuantityField { source = source,
                    name = source == "literal" ? null : field.Value.Text, value = source == "literal" ? field.Value.Text : null };
            }
            ApprovedPreview = ManualQuantitiesCore.BuildPreview(rule, _observations, _zoneId);
            _evaluations.Clear();
            foreach (var item in ApprovedPreview.items) if (item != null && item.handle != null) _evaluations[item.handle] = item;
            SetRows(ManualQuantityView.Rows(_observations, ApprovedPreview));
            var messages = new List<string> { "Будет зарегистрировано: " + ApprovedPreview.accepted_count + " из " + _observations.Count +
                ". Остальные объекты останутся без изменений; причины показаны в списке.",
                "Повторная регистрация обновляет только принятые выбранные объекты. Подозрительные совпадения не удаляются автоматически." };
            foreach (var issue in ApprovedPreview.issues) if (issue != null && !string.IsNullOrEmpty(issue.message)) messages.Add(issue.message);
            _status.Text = string.Join(Environment.NewLine, messages.ToArray());
            _register.Text = "Зарегистрировать " + ApprovedPreview.accepted_count + " из " + _observations.Count;
            _register.Enabled = ApprovedPreview.accepted_count > 0;
            _tabs.SelectedIndex = 1;
        }
        private void InvalidatePreview()
        {
            if (_loading) return;
            bool restoreInventory = ApprovedPreview != null || _rows == null;
            ApprovedPreview = null; _register.Enabled = false; _register.Text = "Сначала проверьте соответствие";
            _evaluations.Clear();
            _status.Text = "Соответствие не проверено. Неизвестные значения не заменяются нулями. Изменение любого параметра требует новой проверки.";
            if (_inventory != null && restoreInventory) SetRows(ManualQuantityView.Rows(_observations, null));
        }
        private DataGridView CreateInventory()
        {
            var grid = new DataGridView { Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, ShowCellToolTips = true };
            grid.RowTemplate.Height = 24;
            for (int i = 0; i < ManualQuantityView.Headers.Length; i++)
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = ManualQuantityView.Headers[i], Width = ManualQuantityView.Widths[i], SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.CellValueNeeded += (s, e) => e.Value = Cell(e.RowIndex, e.ColumnIndex);
            grid.CellToolTipTextNeeded += (s, e) => e.ToolTipText = Cell(e.RowIndex, e.ColumnIndex);
            grid.RowEnter += (s, e) => ShowDetails(e.RowIndex);
            return grid;
        }
        private void ShowDetails(int index)
        {
            if (index < 0 || index >= _observations.Count) { _details.Text = ""; return; }
            var source = _observations[index];
            var lines = new List<string> { "Объект " + (index + 1) + ", Handle " + source.handle + ": " + ManualQuantityView.Label(source) + "; слой «" + source.layer + "»." };
            ManualQuantityEvaluation item;
            if (source.handle != null && _evaluations.TryGetValue(source.handle, out item))
            {
                if (!string.IsNullOrEmpty(item.reason)) lines.Add(item.reason);
                foreach (var issue in item.issues) if (issue != null && !string.IsNullOrEmpty(issue.message)) lines.Add(issue.message);
                var part = item.element;
                if (part != null) lines.Add("Марка: " + Known(part.mark) + "; тип: " + Known(part.type) + "; система: " + Known(part.system) +
                    "; материал: " + Known(part.material) + "; покрытие: " + Known(part.coating) + "; цвет: " + Known(part.color) + ".");
            }
            else if (!string.IsNullOrEmpty(source.extraction_reason)) lines.Add(source.extraction_reason);
            var names = new List<string>(source.attributes.Keys); names.Sort(StringComparer.Ordinal);
            foreach (var name in names) lines.Add("Атрибут «" + name + "»: " + source.attributes[name]);
            names = new List<string>(source.dynamic_properties.Keys); names.Sort(StringComparer.Ordinal);
            foreach (var name in names) lines.Add("Динамическое свойство «" + name + "»: " + CladdingTableData.Display(source.dynamic_properties[name]));
            _details.Text = string.Join(Environment.NewLine, lines.ToArray());
        }
        private static string Known(string text) { return string.IsNullOrWhiteSpace(text) ? "не задано" : text; }
        private string Cell(int row, int col) { return _rows != null && row >= 0 && row < _rows.Count && col >= 0 && col < _rows[row].Length ? CladdingTableData.Display(_rows[row][col]) : ""; }
        private void SetRows(List<object[]> rows)
        {
            _rows = rows; _inventory.RowCount = rows.Count; _inventory.Invalidate();
            ShowDetails(_inventory.CurrentCell == null ? (rows.Count > 0 ? 0 : -1) : _inventory.CurrentCell.RowIndex);
        }
        private static ComboBox Select() { return new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; }
        private static string Value(ComboBox combo) { var item = combo.SelectedItem as Choice; return item == null ? "" : item.Value; }
        private static void AddSetting(TableLayoutPanel panel, string label, Control value, int col, int row)
        { panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, col, row); panel.Controls.Add(value, col + 1, row); }
    }
}
