using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AFacadesPlugin
{
    /// <summary>
    /// Диалог ATFZONE: наименование облицовки (= имя слоя), марка зон,
    /// штриховка, оформление. Минимальный WinForms-диалог кодом
    /// (паттерн VitrageForm ABlockGen).
    /// </summary>
    internal class ZoneForm : Form
    {
        // история наименований в пределах сессии AutoCAD
        private static readonly List<string> History = new List<string>();
        internal static int NextStart = 1;

        private readonly ComboBox _cladding = new ComboBox();
        private readonly TextBox _prefix = new TextBox();
        private readonly NumericUpDown _start = new NumericUpDown();
        private readonly ComboBox _pattern = new ComboBox();
        private readonly TextBox _scale = new TextBox();
        private readonly TextBox _color = new TextBox();
        private readonly TextBox _textH = new TextBox();
        private readonly CheckBox _table = new CheckBox();
        private readonly CheckBox _json = new CheckBox();
        private readonly CheckBox _merge = new CheckBox();
        private readonly CheckBox _dims = new CheckBox();
        private readonly Button _add = new Button();

        /// <summary>Колбэк «+ Добавить контуры» (возвращает новое общее
        /// число контуров); задаётся командой до показа формы.</summary>
        internal Func<int> AddPicker;

        internal string Cladding { get { return _cladding.Text.Trim(); } }
        internal string Prefix { get { return _prefix.Text.Trim(); } }
        internal int StartIndex { get { return (int)_start.Value; } }
        internal string Pattern { get { return _pattern.Text.Trim().ToUpperInvariant(); } }
        internal double PatternScale { get { return ParseD(_scale.Text, 25.0); } }
        internal short ColorAci
        {
            get
            {
                short v;
                if (short.TryParse(_color.Text.Trim(), out v) && v >= 1 && v <= 255)
                    return v;
                return 8;
            }
        }
        internal double TextHeight { get { return ParseD(_textH.Text, 250.0); } }
        internal bool MakeTable { get { return _table.Checked; } }
        internal bool WriteJson { get { return _json.Checked; } }
        internal bool MergeZones { get { return _merge.Checked; } }
        internal bool MakeDims { get { return _dims.Checked; } }

        private static double ParseD(string s, double dflt)
        {
            double v;
            s = (s ?? "").Trim().Replace(',', '.');
            if (double.TryParse(s, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out v) && v > 0)
                return v;
            return dflt;
        }

        internal ZoneForm(int contourCount,
                          IEnumerable<string> layerNames = null)
        {
            Text = "ATFZONE — зоны облицовки (контуров: " + contourCount + ")";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(430, 385);
            Font = new Font("Segoe UI", 9f);

            int y = 12;
            AddLabel("Наименование облицовки (= имя слоя):", 12, ref y);
            _cladding.SetBounds(12, y, 406, 24);
            _cladding.DropDownStyle = ComboBoxStyle.DropDown;
            foreach (var h in History) _cladding.Items.Add(h);
            // 07.08 (просьба Германа): существующие слои чертежа в
            // списке — история сессии сверху, слои ниже
            if (layerNames != null)
                foreach (var ln in layerNames)
                    if (!string.IsNullOrEmpty(ln) &&
                        !_cladding.Items.Contains(ln))
                        _cladding.Items.Add(ln);
            if (History.Count > 0) _cladding.Text = History[0];
            else _cladding.Text = "керамогранит 600х600";
            Controls.Add(_cladding);
            y += 32;

            AddLabel("Префикс марки:", 12, ref y, false);
            AddLabel("Начальный №:", 150, ref y, false);
            AddLabel("Высота текста, мм:", 280, ref y);
            _prefix.SetBounds(12, y, 120, 24); _prefix.Text = "Ф-";
            _start.SetBounds(150, y, 110, 24);
            _start.Minimum = 1; _start.Maximum = 9999;
            _start.Value = Math.Max(1, Math.Min(9999, NextStart));
            _textH.SetBounds(280, y, 138, 24); _textH.Text = "250";
            Controls.Add(_prefix); Controls.Add(_start); Controls.Add(_textH);
            y += 32;

            AddLabel("Штриховка:", 12, ref y, false);
            AddLabel("Масштаб:", 150, ref y, false);
            AddLabel("Цвет (ACI 1–255):", 280, ref y);
            _pattern.SetBounds(12, y, 120, 24);
            _pattern.DropDownStyle = ComboBoxStyle.DropDown;
            // полный стандартный набор образцов AutoCAD (acadiso.pat) —
            // фидбэк Германа 21.07 п.2; ходовые сверху, дальше алфавит;
            // свободный ввод любого имени остаётся (DropDown)
            _pattern.Items.AddRange(new object[]
            {
                "ANSI31", "SOLID", "NET", "DOTS", "LINE",
                "ANGLE", "ANSI32", "ANSI33", "ANSI34", "ANSI35",
                "ANSI36", "ANSI37", "ANSI38",
                "AR-B816", "AR-B816C", "AR-B88", "AR-BRELM", "AR-BRSTD",
                "AR-CONC", "AR-HBONE", "AR-PARQ1", "AR-RROOF", "AR-RSHKE",
                "AR-SAND",
                "BOX", "BRASS", "BRICK", "BRSTONE", "CLAY", "CORK",
                "CROSS", "DASH", "DOLMIT", "EARTH", "ESCHER", "FLEX",
                "GOST_GLASS", "GOST_GROUND", "GOST_WOOD",
                "GRASS", "GRATE", "GRAVEL", "HEX", "HONEY", "HOUND",
                "INSUL", "MUDST", "NET3", "PLAST", "PLASTI", "SACNCR",
                "SQUARE", "STARS", "STEEL", "SWAMP", "TRANS", "TRIANG",
                "ZIGZAG",
            });
            _pattern.DropDownHeight = 320;
            _pattern.Text = "ANSI31";
            _scale.SetBounds(150, y, 110, 24); _scale.Text = "25";
            _color.SetBounds(280, y, 138, 24); _color.Text = "8";
            Controls.Add(_pattern); Controls.Add(_scale); Controls.Add(_color);
            y += 34;

            _merge.SetBounds(12, y, 406, 22);
            _merge.Text = "Объединить выбранные контуры в ОДНУ зону " +
                          "(общая площадь и откосы)";
            _merge.Checked = false;
            Controls.Add(_merge);
            y += 26;
            _dims.SetBounds(12, y, 406, 22);
            _dims.Text = "Проставить линейные размеры (слой _РАЗМЕРЫ)";
            _dims.Checked = false;
            Controls.Add(_dims);
            y += 26;
            _table.SetBounds(12, y, 406, 22);
            _table.Text = "Вставить таблицу площадей и погонажей";
            _table.Checked = true;
            Controls.Add(_table);
            y += 26;
            _json.SetBounds(12, y, 406, 22);
            _json.Text = "Сохранить зоны в JSON рядом с чертежом (*_fzones.json)";
            _json.Checked = true;
            Controls.Add(_json);
            y += 34;

            _add.Text = "+ Добавить контуры";
            _add.SetBounds(12, y, 150, 28);
            _add.Click += OnAddContours;
            Controls.Add(_add);
            var ok = new Button
            { Text = "OK", DialogResult = DialogResult.OK };
            ok.SetBounds(222, y, 92, 28);
            var cancel = new Button
            { Text = "Отмена", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(324, y, 94, 28);
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        private void OnAddContours(object sender, EventArgs e)
        {
            if (AddPicker == null) return;
            try
            {
                int n = AddPicker();
                Text = "ATFZONE — зоны облицовки (контуров: " + n + ")";
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Не удалось добавить: " + ex.Message,
                                "ATFZONE");
            }
        }

        private void AddLabel(string text, int x, ref int y, bool advance = true)
        {
            var l = new Label { Text = text, AutoSize = true };
            l.SetBounds(x, y, 10, 17);
            Controls.Add(l);
            if (advance) y += 20;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (DialogResult == DialogResult.OK)
            {
                if (Cladding.Length == 0)
                {
                    MessageBox.Show("Укажите наименование облицовки.",
                                    "ATFZONE");
                    e.Cancel = true;
                    return;
                }
                History.Remove(Cladding);
                History.Insert(0, Cladding);
                if (History.Count > 12) History.RemoveAt(History.Count - 1);
            }
        }
    }
}
