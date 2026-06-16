// Окно-построитель ведомости: либо готовый пресет, либо ручной запрос
// (источник по слоям, один фильтр атрибут/оператор/значение, поля группировки, меры).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AtSpecPlugin
{
    public class QueryForm : Form
    {
        private readonly List<string> _layers, _fields;
        private readonly Dictionary<string, List<string>> _values;
        private readonly List<KeyValuePair<string, string>> _presets; // name, title

        private ComboBox cbPreset, cbField, cbOp, cbValue;
        private CheckedListBox clLayers, clGroup;
        private CheckBox chkFilter, chkCount, chkSumLen;
        private TextBox txtTitle;

        public bool UsePreset { get; private set; }
        public string PresetName { get; private set; }
        public Dictionary<string, object> Query { get; private set; }

        public QueryForm(List<string> types, List<string> layers, List<string> fields,
                         Dictionary<string, List<string>> values,
                         List<KeyValuePair<string, string>> presets)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            _values = values ?? new Dictionary<string, List<string>>();
            _presets = presets ?? new List<KeyValuePair<string, string>>();
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "ATableSpec — построитель ведомости";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(560, 640);
            Font = new Font("Segoe UI", 9f);

            int x = 14, w = 532, y = 12;

            Controls.Add(new Label { Text = "Готовый пресет:", Location = new Point(x, y), AutoSize = true });
            cbPreset = new ComboBox { Location = new Point(x, y + 20), Width = w, DropDownStyle = ComboBoxStyle.DropDownList };
            cbPreset.Items.Add("— настроить вручную —");
            foreach (var p in _presets) cbPreset.Items.Add(p.Value);
            cbPreset.SelectedIndex = 0;
            cbPreset.SelectedIndexChanged += delegate { UpdateEnabled(); };
            Controls.Add(cbPreset);
            y += 54;

            Controls.Add(new Label { Text = "— или настроить вручную —", Location = new Point(x, y), AutoSize = true, ForeColor = Color.Gray });
            y += 22;

            Controls.Add(new Label { Text = "Источник (слои):", Location = new Point(x, y), AutoSize = true });
            clLayers = new CheckedListBox { Location = new Point(x, y + 18), Width = w, Height = 84, CheckOnClick = true };
            foreach (var l in _layers) clLayers.Items.Add(l, true);
            Controls.Add(clLayers);
            y += 110;

            chkFilter = new CheckBox { Text = "Фильтр:", Location = new Point(x, y + 2), AutoSize = true };
            chkFilter.CheckedChanged += delegate { UpdateEnabled(); };
            Controls.Add(chkFilter);
            cbField = new ComboBox { Location = new Point(x + 70, y), Width = 168, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var f in _fields) cbField.Items.Add(f);
            if (cbField.Items.Count > 0) cbField.SelectedIndex = 0;
            cbField.SelectedIndexChanged += delegate { FillValues(); };
            Controls.Add(cbField);
            cbOp = new ComboBox { Location = new Point(x + 244, y), Width = 92, DropDownStyle = ComboBoxStyle.DropDownList };
            cbOp.Items.AddRange(new object[] { "=", "≠", "содержит" });
            cbOp.SelectedIndex = 0;
            Controls.Add(cbOp);
            cbValue = new ComboBox { Location = new Point(x + 342, y), Width = 190, DropDownStyle = ComboBoxStyle.DropDown };
            Controls.Add(cbValue);
            y += 34;

            Controls.Add(new Label { Text = "Группировать по (отметить хотя бы одно):", Location = new Point(x, y), AutoSize = true });
            clGroup = new CheckedListBox { Location = new Point(x, y + 18), Width = w, Height = 150, CheckOnClick = true };
            foreach (var f in _fields) clGroup.Items.Add(f);
            Controls.Add(clGroup);
            y += 176;

            Controls.Add(new Label { Text = "Считать:", Location = new Point(x, y), AutoSize = true });
            chkCount = new CheckBox { Text = "Количество", Location = new Point(x + 70, y - 2), AutoSize = true, Checked = true, Enabled = false };
            Controls.Add(chkCount);
            chkSumLen = new CheckBox { Text = "Сумма длин", Location = new Point(x + 200, y - 2), AutoSize = true };
            Controls.Add(chkSumLen);
            y += 28;

            Controls.Add(new Label { Text = "Заголовок:", Location = new Point(x, y + 2), AutoSize = true });
            txtTitle = new TextBox { Location = new Point(x + 80, y - 1), Width = w - 80, Text = "Ведомость" };
            Controls.Add(txtTitle);
            y += 38;

            var ok = new Button { Text = "Построить", Location = new Point(x + w - 188, y), Width = 100 };
            ok.Click += OnOk;
            var cancel = new Button { Text = "Отмена", Location = new Point(x + w - 82, y), Width = 82, DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            UpdateEnabled();
            FillValues();
        }

        private void UpdateEnabled()
        {
            bool manual = cbPreset.SelectedIndex == 0;
            clLayers.Enabled = manual;
            chkFilter.Enabled = manual;
            clGroup.Enabled = manual;
            chkSumLen.Enabled = manual;
            txtTitle.Enabled = manual;
            bool flt = manual && chkFilter.Checked;
            cbField.Enabled = flt; cbOp.Enabled = flt; cbValue.Enabled = flt;
        }

        private void FillValues()
        {
            cbValue.Items.Clear();
            if (cbField.SelectedItem == null) return;
            List<string> vals;
            if (_values.TryGetValue(cbField.SelectedItem.ToString(), out vals))
                foreach (var v in vals) cbValue.Items.Add(v);
        }

        private void OnOk(object sender, EventArgs e)
        {
            if (cbPreset.SelectedIndex > 0)
            {
                UsePreset = true;
                PresetName = _presets[cbPreset.SelectedIndex - 1].Key;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            var group = new List<object>();
            foreach (var it in clGroup.CheckedItems) group.Add(it.ToString());
            if (group.Count == 0)
            {
                MessageBox.Show("Отметьте хотя бы одно поле для группировки.", "ATableSpec");
                return;
            }

            var layers = new List<object>();
            foreach (var it in clLayers.CheckedItems) layers.Add(it.ToString());
            if (layers.Count == clLayers.Items.Count) layers.Clear(); // все = без ограничения

            var filters = new List<object>();
            if (chkFilter.Checked && cbField.SelectedItem != null)
            {
                string op = cbOp.SelectedItem.ToString();
                op = op == "≠" ? "!=" : (op == "содержит" ? "contains" : "=");
                filters.Add(new Dictionary<string, object>
                {
                    { "field", cbField.SelectedItem.ToString() },
                    { "op", op },
                    { "value", cbValue.Text }
                });
            }

            var measures = new Dictionary<string, object> { { "Кол-во", "count" } };
            if (chkSumLen.Checked) measures["Сумма длин, мм"] = "sum_length";

            Query = new Dictionary<string, object>
            {
                { "title", string.IsNullOrEmpty(txtTitle.Text.Trim()) ? "Ведомость" : txtTitle.Text.Trim() },
                { "include_layers", layers },
                { "filters", filters },
                { "group_by", group },
                { "measures", measures },
                { "sort_by", group }
            };
            UsePreset = false;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
