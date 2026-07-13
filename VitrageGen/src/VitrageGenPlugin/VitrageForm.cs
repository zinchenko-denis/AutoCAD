using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace VitrageGenPlugin
{
    /// <summary>
    /// Диалог параметров витража (Э1). Все размеры — мм чертежа.
    /// Дефолты — из эталона Проба_штапики_2 (шаг 705, тела 54.4/45.6, fold 15, зазор 10).
    /// Списки («45; 595; 955») — разделитель «;», десятичная запятая допустима.
    /// </summary>
    internal sealed class VitrageForm : Form
    {
        private readonly RadioButton _rbStep = new RadioButton { Text = "Шаг стоек, мм:", Checked = true };
        private readonly RadioButton _rbCols = new RadioButton { Text = "Число долей:" };
        private readonly TextBox _tStep = new TextBox { Text = "705" };
        private readonly TextBox _tCols = new TextBox { Text = "3", Enabled = false };
        private readonly TextBox _tRails = new TextBox { Text = "" };
        private readonly TextBox _tTiers = new TextBox { Text = "" };
        private readonly TextBox _tGap = new TextBox { Text = "10" };
        private readonly ComboBox _cStand = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly ComboBox _cRigel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly ComboBox _cFill = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly TextBox _tBodyStand = new TextBox { Text = "54.4" };
        private readonly TextBox _tBodyRigel = new TextBox { Text = "45.6" };
        private readonly TextBox _tFold = new TextBox { Text = "15" };
        private readonly TextBox _tMarkStand = new TextBox { Text = "С{n}" };
        private readonly TextBox _tMarkRigel = new TextBox { Text = "Р{n}" };
        private readonly TextBox _tMarkFill = new TextBox { Text = "Сп{n}" };
        private readonly Label _lbSize = new Label { AutoSize = true, ForeColor = Color.DimGray };

        public VitrageForm(IList<string> blockDefs, double openingW, double openingH)
        {
            Text = "VitrageGen — параметры витража (Э1)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 396);
            Font = new Font("Segoe UI", 9f);

            _lbSize.Text = string.Format(CultureInfo.InvariantCulture,
                "Проём: {0:0.#} × {1:0.#} мм", openingW, openingH);

            foreach (var n in blockDefs)
            {
                _cStand.Items.Add(n); _cRigel.Items.Add(n); _cFill.Items.Add(n);
            }
            // предвыбор эталонных определений, если есть в чертеже
            Preselect(_cStand, "17_07_24"); Preselect(_cRigel, "17_06_01"); Preselect(_cFill, "СТП");

            _rbStep.CheckedChanged += (s, e) =>
            {
                _tStep.Enabled = _rbStep.Checked;
                _tCols.Enabled = !_rbStep.Checked;
            };

            int y = 10;
            Add(_lbSize, 12, ref y, 26);
            AddPair(_rbStep, _tStep, ref y);
            AddPair(_rbCols, _tCols, ref y);
            AddRow("Отметки ригелей от низа (осевые, через ;):", _tRails, ref y);
            AddRow("Ярусы стоек, длины через ; (пусто = вся высота):", _tTiers, ref y);
            AddRow("Зазор между ярусами (терморазрыв), мм:", _tGap, ref y);
            AddRow("Блок стойки:", _cStand, ref y);
            AddRow("Блок ригеля:", _cRigel, ref y);
            AddRow("Блок заполнения (пусто — не ставить):", _cFill, ref y);
            AddRow("Тело стойки, мм:", _tBodyStand, ref y);
            AddRow("Тело ригеля, мм:", _tBodyRigel, ref y);
            AddRow("Заход в фальц (к РАЗМЕР_ЗАП), мм:", _tFold, ref y);
            AddRow("Марки (стойка / ригель / заполнение):", _tMarkStand, ref y, 90);
            _tMarkRigel.SetBounds(196, _tMarkStand.Top, 90, 23);
            _tMarkFill.SetBounds(292, _tMarkStand.Top, 90, 23);
            Controls.Add(_tMarkRigel); Controls.Add(_tMarkFill);

            var okB = new Button { Text = "Построить", DialogResult = DialogResult.OK };
            var noB = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel };
            okB.SetBounds(230, y + 6, 96, 28);
            noB.SetBounds(332, y + 6, 86, 28);
            Controls.Add(okB); Controls.Add(noB);
            AcceptButton = okB; CancelButton = noB;
            ClientSize = new Size(430, y + 44);

            okB.Click += (s, e) => { if (!ValidateInput(out var msg)) { MessageBox.Show(msg, "VitrageGen"); DialogResult = DialogResult.None; } };
        }

        private static void Preselect(ComboBox c, string name)
        {
            int i = c.Items.IndexOf(name);
            if (i >= 0) c.SelectedIndex = i;
        }

        private void Add(Control c, int x, ref int y, int h)
        {
            c.SetBounds(x, y, ClientSize.Width - 24, h - 4);
            Controls.Add(c); y += h;
        }

        private void AddPair(RadioButton rb, TextBox t, ref int y)
        {
            rb.SetBounds(12, y, 170, 23); t.SetBounds(196, y, 90, 23);
            Controls.Add(rb); Controls.Add(t); y += 27;
        }

        /// <summary>Строка «label + контрол». w>0 — label сверху, контрол под ним
        /// (для ряда из нескольких полей: первый ставится здесь, остальные — вручную).</summary>
        private void AddRow(string label, Control c, ref int y, int w = 0)
        {
            var lb = new Label { Text = label, AutoSize = false };
            if (w > 0)
            {
                lb.SetBounds(12, y + 3, 406, 21);
                c.SetBounds(100, y + 24, w, 23);
                y += 52;
            }
            else if (c is ComboBox)
            {
                lb.SetBounds(12, y + 3, 180, 21);
                c.SetBounds(196, y, 222, 23);
                y += 27;
            }
            else
            {
                lb.SetBounds(12, y + 3, 292, 21);
                c.SetBounds(310, y, 108, 23);
                y += 27;
            }
            Controls.Add(lb); Controls.Add(c);
        }

        // ── парсинг ──
        internal static double ParseD(string s, string what)
        {
            s = (s ?? "").Trim().Replace(',', '.');
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new FormatException("не число: " + what + " = «" + s + "»");
            return v;
        }

        internal static List<double> ParseList(string s, string what)
        {
            var res = new List<double>();
            foreach (var part in (s ?? "").Split(';'))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                res.Add(ParseD(p, what));
            }
            return res;
        }

        private bool ValidateInput(out string msg)
        {
            msg = null;
            try
            {
                if (_rbStep.Checked && ParseD(_tStep.Text, "шаг") <= 0) { msg = "Шаг стоек должен быть > 0."; return false; }
                if (_rbCols.Checked && (int)ParseD(_tCols.Text, "доли") < 1) { msg = "Число долей должно быть ≥ 1."; return false; }
                ParseList(_tRails.Text, "отметки ригелей");
                ParseList(_tTiers.Text, "ярусы");
                ParseD(_tGap.Text, "зазор");
                ParseD(_tBodyStand.Text, "тело стойки");
                ParseD(_tBodyRigel.Text, "тело ригеля");
                ParseD(_tFold.Text, "фальц");
            }
            catch (FormatException ex) { msg = ex.Message; return false; }
            if (StandBlock.Length == 0) { msg = "Не выбран блок стойки."; return false; }
            if (Rails.Count > 0 && RigelBlock.Length == 0) { msg = "Заданы отметки ригелей — выберите блок ригеля."; return false; }
            return true;
        }

        // ── результаты ──
        public bool UseCols { get { return _rbCols.Checked; } }
        public double StepX { get { return ParseD(_tStep.Text, "шаг"); } }
        public int NCols { get { return (int)ParseD(_tCols.Text, "доли"); } }
        public List<double> Rails { get { return ParseList(_tRails.Text, "отметки"); } }
        public List<double> Tiers { get { return ParseList(_tTiers.Text, "ярусы"); } }
        public double TierGap { get { return ParseD(_tGap.Text, "зазор"); } }
        public string StandBlock { get { return (_cStand.Text ?? "").Trim(); } }
        public string RigelBlock { get { return (_cRigel.Text ?? "").Trim(); } }
        public string FillBlock { get { return (_cFill.Text ?? "").Trim(); } }
        public double BodyStand { get { return ParseD(_tBodyStand.Text, "тело стойки"); } }
        public double BodyRigel { get { return ParseD(_tBodyRigel.Text, "тело ригеля"); } }
        public double Fold { get { return ParseD(_tFold.Text, "фальц"); } }
        public string MarkStand { get { return _tMarkStand.Text.Trim(); } }
        public string MarkRigel { get { return _tMarkRigel.Text.Trim(); } }
        public string MarkFill { get { return _tMarkFill.Text.Trim(); } }
    }
}
