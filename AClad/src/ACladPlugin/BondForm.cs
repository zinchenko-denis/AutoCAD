using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ACladPlugin
{
    /// <summary>
    /// Окно ATTILE — универсальная разбежка (23.09). Все параметры
    /// раскладки на одном экране + живой просмотр рисунка (тот же сдвиг
    /// рядов, что у движка: BondSettings.ShiftAt). Собрано кодом, без
    /// дизайнера и .resx (конвенция репо); без AutoCAD API — проверяется
    /// под mono (tools/attile_ui).
    /// </summary>
    public class BondForm : Form
    {
        private BondSettings _s;
        private bool _loading;

        private readonly ComboBox _material = new ComboBox();
        private readonly ComboBox _name = new ComboBox();
        private readonly ComboBox _element = new ComboBox();
        private readonly CheckBox _colors = new CheckBox();
        private readonly NumericUpDown _w = Num(1, 20000, 0);
        private readonly NumericUpDown _h = Num(1, 20000, 0);
        private readonly Button _rot = new Button();
        private readonly ComboBox _formats = new ComboBox();
        private readonly NumericUpDown _gv = Num(0, 100, 1);
        private readonly NumericUpDown _gh = Num(0, 100, 1);
        private readonly RadioButton _rows = new RadioButton();
        private readonly RadioButton _cols = new RadioButton();
        private readonly ComboBox _kind = new ComboBox();
        private readonly ComboBox _value = new ComboBox();
        private readonly ComboBox _units = new ComboBox();
        private readonly ComboBox _dir = new ComboBox();
        private readonly TextBox _seq = new TextBox();
        private readonly RadioButton[] _anchor = new RadioButton[9];
        private readonly ComboBox _center = new ComboBox();
        private readonly ComboBox _ref = new ComboBox();
        private readonly CheckBox _point = new CheckBox();
        private readonly CheckBox _gap = new CheckBox();
        private readonly CheckBox _merge = new CheckBox();
        private readonly CheckBox _forcedV = new CheckBox();
        private readonly CheckBox _forcedH = new CheckBox();
        private readonly ComboBox _shaped = new ComboBox();
        private readonly NumericUpDown _min = Num(0, 500, 0);
        private readonly NumericUpDown _kerf = Num(0, 20, 1);
        private readonly NumericUpDown _warn = Num(0, 3000, 0);
        private readonly BondPreview _pv = new BondPreview();
        private readonly Label _desc = new Label();

        private static readonly string[] KindCodes = { "none", "alternate", "step", "sequence", "pattern" };
        private static readonly string[] KindTexts =
        {
            "Без смещения — шов в шов",
            "Через ряд (0, s, 0, s …)",
            "Лесенкой (каждый ряд +s к предыдущему)",
            "Своя последовательность сдвигов",
            "По образцу из чертежа",
        };
        private static readonly string[] ShapedCodes = { "keep", "split", "split_joint" };
        private static readonly string[] AnchorCodes = { "LT", "CT", "RT", "LC", "CC", "RC", "LB", "CB", "RB" };
        private static readonly string[] AnchorTexts = { "ЛВ", "ЦВ", "ПВ", "ЛЦ", "Ц", "ПЦ", "ЛН", "ЦН", "ПН" };
        private const string RectElement = "Прямоугольник — блоки создаст программа";

        /// <summary>Итог (после ОК).</summary>
        public BondSettings Result { get { return _s; } }

        private static NumericUpDown Num(decimal min, decimal max, int dec)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = dec,
                                       Increment = 1 };
        }

        private static Label L(string text, int x, int y, int w = 110)
        {
            return new Label { Text = text, Left = x, Top = y + 3, Width = w, AutoSize = false,
                               Height = 18 };
        }

        public BondForm(BondSettings initial, IEnumerable<string> layers,
                        IEnumerable<string> dynBlocks)
        {
            _s = (initial ?? new BondSettings()).Clone();
            Text = "ATTILE — раскладка облицовки с разбежкой (шахматный порядок)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(846, 764);

            // ── 1. облицовка ──
            var g1 = new GroupBox { Text = "1. Облицовка", Left = 10, Top = 6, Width = 470, Height = 134 };
            _material.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (var p in BondSettings.Presets) _material.Items.Add(p.Title);
            Place(_material, 120, 18, 340);
            _name.DropDownStyle = ComboBoxStyle.DropDown;
            var seen = new HashSet<string>();
            foreach (var s in layers ?? new string[0])
                if (!string.IsNullOrEmpty(s) && seen.Add(s)) _name.Items.Add(s);
            Place(_name, 120, 46, 340);
            _element.DropDownStyle = ComboBoxStyle.DropDownList;
            _element.Items.Add(RectElement);
            foreach (var b in dynBlocks ?? new string[0])
                if (!string.IsNullOrEmpty(b)) _element.Items.Add(b);
            Place(_element, 120, 74, 340);
            _colors.Text = "Раскраска по образцу из чертежа (тип = слой плиток образца)";
            Place(_colors, 10, 104, 450);
            g1.Controls.AddRange(new Control[] { L("Материал:", 10, 18), _material,
                L("Наименование:", 10, 46), _name, L("Элемент:", 10, 74), _element, _colors });

            // ── 2. формат и швы ──
            var g2 = new GroupBox { Text = "2. Формат и швы", Left = 10, Top = 144, Width = 470, Height = 80 };
            Place(_w, 120, 20, 78);
            Place(_h, 216, 20, 78);
            _rot.Text = "⇄";
            _rot.Left = 300; _rot.Top = 19; _rot.Width = 30; _rot.Height = 24;
            _formats.DropDownStyle = ComboBoxStyle.DropDownList;
            Place(_formats, 336, 20, 124);
            Place(_gv, 120, 48, 70);
            Place(_gh, 318, 48, 70);
            g2.Controls.AddRange(new Control[] { L("Формат Ш×В, мм:", 10, 20),
                _w, new Label { Text = "×", Left = 200, Top = 23, Width = 14 }, _h, _rot, _formats,
                L("Шов вертик., мм:", 10, 48), _gv, L("горизонт., мм:", 200, 48, 115), _gh });

            // ── 3. разбежка ──
            var g3 = new GroupBox { Text = "3. Разбежка (шахматный порядок)", Left = 10, Top = 228, Width = 470, Height = 152 };
            _rows.Text = "горизонтальные ряды";
            Place(_rows, 120, 18, 160);
            _cols.Text = "вертикальные столбцы";
            Place(_cols, 290, 18, 170);
            _kind.DropDownStyle = ComboBoxStyle.DropDownList;
            _kind.Items.AddRange(KindTexts);
            Place(_kind, 120, 46, 340);
            _value.DropDownStyle = ComboBoxStyle.DropDown;
            _value.Items.AddRange(new object[] { "1/2", "1/3", "1/4", "1/5", "2/3", "3/4" });
            Place(_value, 120, 74, 80);
            _units.DropDownStyle = ComboBoxStyle.DropDownList;
            _units.Items.AddRange(new object[] { "доля модуля", "мм" });
            Place(_units, 206, 74, 120);
            _dir.DropDownStyle = ComboBoxStyle.DropDownList;
            Place(_dir, 332, 74, 128);
            Place(_seq, 150, 102, 310);
            var hint = new Label
            {
                Text = "Модуль = плитка + шов; 1/2 — стык по центру плитки соседнего ряда (столбца). " +
                       "Последовательность — через «;» в выбранных единицах.",
                Left = 10, Top = 126, Width = 452, Height = 24, ForeColor = Color.DimGray,
                Font = new Font(Font.FontFamily, Font.Size - 0.75f)
            };
            g3.Controls.AddRange(new Control[] { L("Ряды:", 10, 18), _rows, _cols,
                L("Смещение:", 10, 46), _kind, L("Величина s:", 10, 74), _value, _units, _dir,
                L("Последовательность:", 10, 102, 140), _seq, hint });

            // ── 4. отсчёт ──
            var g4 = new GroupBox { Text = "4. Отсчёт — где стоит целая плитка", Left = 10, Top = 384, Width = 470, Height = 112 };
            for (int k = 0; k < 9; k++)
            {
                var rb = new RadioButton
                {
                    Text = AnchorTexts[k], Appearance = Appearance.Button,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Left = 12 + (k % 3) * 50, Top = 20 + (k / 3) * 28, Width = 46, Height = 25
                };
                _anchor[k] = rb;
                g4.Controls.Add(rb);
            }
            _center.DropDownStyle = ComboBoxStyle.DropDownList;
            _center.Items.AddRange(new object[] { "плитка по оси", "шов по оси" });
            Place(_center, 250, 20, 210);
            _ref.DropDownStyle = ComboBoxStyle.DropDownList;
            _ref.Items.AddRange(new object[] { "габарита зоны", "основной стены (без выступов)" });
            Place(_ref, 250, 48, 210);
            _point.Text = "общая точка для всех зон (указать после ОК)";
            Place(_point, 170, 80, 292);
            g4.Controls.AddRange(new Control[] { L("По центру:", 170, 20, 80), _center,
                L("Считать от:", 170, 48, 80), _ref, _point });

            // ── 5. подрезка и проёмы ──
            var g5 = new GroupBox { Text = "5. Подрезка и проёмы", Left = 10, Top = 500, Width = 470, Height = 140 };
            _gap.Text = "руст вокруг проёмов";
            Place(_gap, 10, 20, 170);
            _merge.Text = "смежные контуры — одна плоскость";
            Place(_merge, 200, 20, 262);
            _shaped.DropDownStyle = ComboBoxStyle.DropDownList;
            _shaped.Items.AddRange(new object[] { "оставить фигурными",
                "резать на прямоугольники", "резать, руст по продолжению грани проёма" });
            Place(_shaped, 140, 46, 320);
            Place(_min, 226, 76, 72);
            Place(_kerf, 392, 76, 68);
            Place(_warn, 290, 106, 72);
            g5.Controls.AddRange(new Control[] { _gap, _merge, L("Г-образные куски:", 10, 46, 130),
                _shaped, L("Не класть куски тоньше, мм:", 10, 76, 212), _min,
                L("Пропил, мм:", 306, 76, 84), _kerf,
                L("Предупреждать о подрезке меньше, мм:", 10, 106, 280), _warn });

            // ── 6. принудительные русты (Герман 23.09: как в ATCLAD) ──
            var g6 = new GroupBox { Text = "6. Принудительные русты (как в ATCLAD)", Left = 10, Top = 644,
                                    Width = 470, Height = 70 };
            _forcedV.Text = "вертикальные — точки на оси руста (указать после «Разложить»)";
            Place(_forcedV, 10, 20, 452);
            _forcedH.Text = "горизонтальные — точка = низ руста (по верху окна — руст над окном)";
            Place(_forcedH, 10, 44, 452);
            g6.Controls.AddRange(new Control[] { _forcedV, _forcedH });

            // ── просмотр ──
            var gp = new GroupBox { Text = "Просмотр рисунка", Left = 490, Top = 6, Width = 346, Height = 708 };
            _pv.Left = 10; _pv.Top = 20; _pv.Width = 326; _pv.Height = 410;
            _desc.Left = 10; _desc.Top = 436; _desc.Width = 326; _desc.Height = 214;
            _desc.AutoSize = false;
            var legend = new Label
            {
                Text = "светлые — целые, тёмные — подрезка (у рамки и окна); красная " +
                       "точка — отсчёт, жирная рамка — базовая плитка; белая кайма " +
                       "у окна — руст вокруг проёма",
                Left = 10, Top = 656, Width = 326, Height = 44, ForeColor = Color.DimGray,
                Font = new Font(Font.FontFamily, Font.Size - 0.75f)
            };
            gp.Controls.AddRange(new Control[] { _pv, _desc, legend });

            var reset = new Button { Text = "Сброс к пресету", Left = 10, Top = 724, Width = 140, Height = 30 };
            var ok = new Button { Text = "Разложить", Left = 616, Top = 724, Width = 106, Height = 30 };
            var cancel = new Button { Text = "Отмена", Left = 730, Top = 724, Width = 106, Height = 30,
                                      DialogResult = DialogResult.Cancel };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { g1, g2, g3, g4, g5, g6, gp, reset, ok, cancel });

            LoadControls();

            // ── события ──
            _material.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                ReadControls();
                _s.ApplyPreset(BondSettings.FindPreset((string)_material.SelectedItem));
                LoadControls();
            };
            _formats.SelectedIndexChanged += delegate
            {
                if (_loading || _formats.SelectedIndex < 1) return;
                double w, h;
                if (!BondSettings.TryParseFormat((string)_formats.SelectedItem, out w, out h)) return;
                ReadControls();
                RenameForFormat(w, h);
                _s.W = w; _s.H = h;
                LoadControls();
            };
            _rot.Click += delegate
            {
                ReadControls();
                double w = _s.H, h = _s.W;
                RenameForFormat(w, h);
                _s.W = w; _s.H = h;
                LoadControls();
            };
            reset.Click += delegate
            {
                ReadControls();
                _s.ApplyPreset(BondSettings.FindPreset(_s.Material));
                LoadControls();
            };
            ok.Click += delegate
            {
                ReadControls();
                string err = _s.Validate();
                if (err != null)
                {
                    MessageBox.Show(this, err, "ATTILE", MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            EventHandler upd = delegate { if (!_loading) { ReadControls(); Refresh2(); } };
            foreach (var c in new Control[] { _name, _element, _w, _h, _gv, _gh, _kind, _value,
                                              _units, _dir, _seq, _center, _ref, _shaped, _min,
                                              _kerf, _warn })
            {
                c.TextChanged += upd;
                var cb = c as ComboBox;
                if (cb != null) cb.SelectedIndexChanged += upd;
                var nu = c as NumericUpDown;
                if (nu != null) nu.ValueChanged += upd;
            }
            foreach (var c in new CheckBox[] { _colors, _point, _gap, _merge, _forcedV, _forcedH })
                c.CheckedChanged += upd;
            _rows.CheckedChanged += upd;
            _cols.CheckedChanged += upd;
            foreach (var rb in _anchor) rb.CheckedChanged += upd;
        }

        private static void Place(Control c, int x, int y, int w)
        {
            c.Left = x; c.Top = y; c.Width = w;
        }

        /// <summary>Имя «Керамогранит 600×1200» следует за форматом.</summary>
        private void RenameForFormat(double w, double h)
        {
            string old = BondSettings.F(_s.W) + "×" + BondSettings.F(_s.H);
            string nw = BondSettings.F(w) + "×" + BondSettings.F(h);
            if (_s.Name != null && _s.Name.EndsWith(old))
                _s.Name = _s.Name.Substring(0, _s.Name.Length - old.Length) + nw;
        }

        private static decimal Dec(double v, NumericUpDown n)
        {
            decimal d = (decimal)Math.Round(v, 1);
            if (d < n.Minimum) d = n.Minimum;
            if (d > n.Maximum) d = n.Maximum;
            return d;
        }

        private void LoadControls()
        {
            _loading = true;
            try
            {
                var p = BondSettings.FindPreset(_s.Material);
                _material.SelectedItem = p.Title;
                _name.Text = _s.Name;
                int ei = _s.Element.Length > 0 ? _element.Items.IndexOf(_s.Element) : 0;
                _element.SelectedIndex = ei >= 0 ? ei : 0;
                _colors.Checked = _s.ColorsBySample;
                _w.Value = Dec(_s.W, _w);
                _h.Value = Dec(_s.H, _h);
                _formats.Items.Clear();
                _formats.Items.Add("типовые…");
                foreach (var f in p.Formats) _formats.Items.Add(f);
                _formats.SelectedIndex = 0;
                _gv.Value = Dec(_s.Gv, _gv);
                _gh.Value = Dec(_s.Gh, _gh);
                _rows.Checked = _s.Axis != "cols";
                _cols.Checked = _s.Axis == "cols";
                _kind.SelectedIndex = Math.Max(0, Array.IndexOf(KindCodes, _s.Kind));
                _value.Text = _s.Value;
                _units.SelectedIndex = _s.Units == "mm" ? 1 : 0;
                FillDir();
                _seq.Text = _s.Sequence;
                string ac = _s.AnchorH + _s.AnchorV;
                for (int k = 0; k < 9; k++) _anchor[k].Checked = AnchorCodes[k] == ac;
                _center.SelectedIndex = _s.Center == "joint" ? 1 : 0;
                _ref.SelectedIndex = _s.Ref == "wall" ? 1 : 0;
                _point.Checked = _s.CommonPoint;
                _gap.Checked = _s.GapAround;
                _merge.Checked = _s.Merge;
                _forcedV.Checked = _s.ForcedV;
                _forcedH.Checked = _s.ForcedH;
                _shaped.SelectedIndex = Math.Max(0, Array.IndexOf(ShapedCodes, _s.Shaped));
                _min.Value = Dec(_s.MinPiece, _min);
                _kerf.Value = Dec(_s.Kerf, _kerf);
                _warn.Value = Dec(_s.WarnCut, _warn);
            }
            finally { _loading = false; }
            Refresh2();
        }

        private void FillDir()
        {
            bool cols = _s.Axis == "cols";
            _dir.Items.Clear();
            _dir.Items.AddRange(cols ? new object[] { "вверх", "вниз" } : new object[] { "вправо", "влево" });
            _dir.SelectedIndex = _s.Dir == "-" ? 1 : 0;
        }

        private void ReadControls()
        {
            _s.Material = (string)_material.SelectedItem ?? _s.Material;
            _s.Name = (_name.Text ?? "").Trim();
            _s.Element = _element.SelectedIndex > 0 ? (string)_element.SelectedItem : "";
            _s.ColorsBySample = _colors.Checked;
            _s.W = (double)_w.Value;
            _s.H = (double)_h.Value;
            _s.Gv = (double)_gv.Value;
            _s.Gh = (double)_gh.Value;
            string axis = _cols.Checked ? "cols" : "rows";
            bool axisChanged = axis != _s.Axis;
            _s.Axis = axis;
            if (_kind.SelectedIndex >= 0) _s.Kind = KindCodes[_kind.SelectedIndex];
            _s.Value = (_value.Text ?? "").Trim();
            _s.Units = _units.SelectedIndex == 1 ? "mm" : "frac";
            _s.Dir = _dir.SelectedIndex == 1 ? "-" : "+";
            _s.Sequence = _seq.Text ?? "";
            for (int k = 0; k < 9; k++)
                if (_anchor[k].Checked)
                {
                    _s.AnchorH = AnchorCodes[k].Substring(0, 1);
                    _s.AnchorV = AnchorCodes[k].Substring(1, 1);
                }
            _s.Center = _center.SelectedIndex == 1 ? "joint" : "tile";
            _s.Ref = _ref.SelectedIndex == 1 ? "wall" : "bbox";
            _s.CommonPoint = _point.Checked;
            _s.GapAround = _gap.Checked;
            _s.Merge = _merge.Checked;
            _s.ForcedV = _forcedV.Checked;
            _s.ForcedH = _forcedH.Checked;
            if (_shaped.SelectedIndex >= 0) _s.Shaped = ShapedCodes[_shaped.SelectedIndex];
            if (_s.Element.Length > 0 && _s.Shaped == "keep")
            {
                // блок из чертежа — только прямоугольники
                _s.Shaped = "split_joint";
                _loading = true;
                _shaped.SelectedIndex = 2;
                _loading = false;
            }
            _s.MinPiece = (double)_min.Value;
            _s.Kerf = (double)_kerf.Value;
            _s.WarnCut = (double)_warn.Value;
            if (axisChanged)
            {
                _loading = true;
                FillDir();
                _loading = false;
            }
        }

        private void Refresh2()
        {
            bool val = _s.Kind == "alternate" || _s.Kind == "step";
            _value.Enabled = val;
            _units.Enabled = val || _s.Kind == "sequence";
            _dir.Enabled = _s.Kind != "none" && _s.Kind != "pattern";
            _seq.Enabled = _s.Kind == "sequence";
            _center.Enabled = _s.AnchorH == "C" || _s.AnchorV == "C";
            string err = _s.Validate();
            _desc.ForeColor = err == null ? SystemColors.ControlText : Color.Firebrick;
            _desc.Text = err == null
                ? _s.Describe() + (_s.ColorsBySample || _s.Kind == "pattern"
                    ? " После ОК — выбрать образец в чертеже." : "") +
                  (_s.CommonPoint ? " После ОК — указать общую точку." : "") +
                  (_s.ForcedV || _s.ForcedH ? " Затем — точки принудительных рустов (" +
                      (_s.ForcedV && _s.ForcedH ? "вертикальных и горизонтальных"
                       : _s.ForcedV ? "вертикальных" : "горизонтальных") +
                      "): за рустом раскладка начинается заново от руста." : "")
                : err;
            for (int k = 0; k < 9; k++)
                _anchor[k].BackColor = _anchor[k].Checked ? Color.FromArgb(233, 186, 120)
                                                           : SystemColors.Control;
            _pv.S = _s;
            _pv.Invalidate();
        }
    }

    /// <summary>Просмотр рисунка разбежки: рамка ~5×6 модулей, окно в
    /// середине, целые/подрезка, базовая плитка и точка отсчёта.</summary>
    public class BondPreview : Panel
    {
        public BondSettings S;

        public BondPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (S == null || !(S.W > 0) || !(S.H > 0)) return;
            var g = e.Graphics;
            bool cols = S.Axis == "cols";
            double MX = S.W + S.Gv, MY = S.H + S.Gh;
            double FW = cols ? 6.35 * MX : 5.35 * MX;
            double FH = cols ? 4.4 * MY : 7.35 * MY;
            // вытянутые форматы: держим рамку не слишком узкой
            if (FW / FH > 1.6) FH = FW / 1.6;
            if (FH / FW > 1.6) FW = FH / 1.6;
            const int pad = 12;
            double sc = Math.Min((Width - 2 * pad) / FW, (Height - 2 * pad) / FH);
            double ox = (Width - FW * sc) / 2.0, oy = (Height - FH * sc) / 2.0;
            Func<double, float> X = x => (float)(ox + x * sc);
            Func<double, float> Y = y => (float)(oy + (FH - y) * sc);

            // окно (проём) — для наглядности подрезки и руста
            double wx0 = FW * 0.47, wy0 = FH * 0.34;
            double ww = Math.Max(Math.Min(1.3 * MX, FW * 0.30), FW * 0.18);
            double wh = Math.Max(Math.Min(1.25 * MY, FH * 0.30), FH * 0.16);
            double gx = S.GapAround ? S.Gv : 0, gy = S.GapAround ? S.Gh : 0;
            double ex0 = wx0 - gx, ey0 = wy0 - gy, ex1 = wx0 + ww + gx, ey1 = wy0 + wh + gy;

            double bx = S.AnchorH == "L" ? 0 : S.AnchorH == "R" ? FW - S.W :
                (S.Center == "joint" ? FW / 2 + S.Gv / 2 : FW / 2 - S.W / 2);
            double by = S.AnchorV == "B" ? 0 : S.AnchorV == "T" ? FH - S.H :
                (S.Center == "joint" ? FH / 2 + S.Gh / 2 : FH / 2 - S.H / 2);

            var frame = RectangleF.FromLTRB(X(0), Y(FH), X(FW), Y(0));
            g.SetClip(frame);
            var full = new SolidBrush(Color.FromArgb(233, 216, 184));
            var cut = new SolidBrush(Color.FromArgb(201, 164, 106));
            var edge = new Pen(Color.FromArgb(122, 106, 85), 1f);
            float jmin = 1.0f;
            Action<double, double, bool> tile = (x0, y0, isBase) =>
            {
                double x1 = x0 + S.W, y1 = y0 + S.H;
                if (x1 <= 0 || x0 >= FW || y1 <= 0 || y0 >= FH) return;
                bool inFrame = x0 >= -1e-6 && y0 >= -1e-6 && x1 <= FW + 1e-6 && y1 <= FH + 1e-6;
                bool hitsWin = !(x1 <= ex0 || x0 >= ex1 || y1 <= ey0 || y0 >= ey1);
                var r = RectangleF.FromLTRB(X(x0), Y(y1), X(x1), Y(y0));
                // шов виден хотя бы в 1 px
                float sx = (float)Math.Max(0, (jmin - S.Gv * sc) / 2), sy = (float)Math.Max(0, (jmin - S.Gh * sc) / 2);
                r.Inflate(-sx, -sy);
                g.FillRectangle(inFrame && !hitsWin ? full : cut, r);
                g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height);
                if (isBase)
                    using (var bp = new Pen(Color.FromArgb(60, 40, 20), 2.2f))
                        g.DrawRectangle(bp, r.X, r.Y, r.Width, r.Height);
            };
            if (!cols)
            {
                int j0 = (int)Math.Floor(-by / MY) - 1, j1 = (int)Math.Ceiling((FH - by) / MY) + 1;
                for (int j = j0; j <= j1; j++)
                {
                    double y0 = by + j * MY;
                    double s = S.ShiftAt(j) * MX;
                    int k0 = (int)Math.Floor((-bx - s) / MX) - 1, k1 = (int)Math.Ceiling((FW - bx - s) / MX) + 1;
                    for (int k = k0; k <= k1; k++)
                        tile(bx + k * MX + s, y0, j == 0 && k == 0);
                }
            }
            else
            {
                int j0 = (int)Math.Floor(-bx / MX) - 1, j1 = (int)Math.Ceiling((FW - bx) / MX) + 1;
                for (int j = j0; j <= j1; j++)
                {
                    double x0 = bx + j * MX;
                    double s = S.ShiftAt(j) * MY;
                    int k0 = (int)Math.Floor((-by - s) / MY) - 1, k1 = (int)Math.Ceiling((FH - by - s) / MY) + 1;
                    for (int k = k0; k <= k1; k++)
                        tile(x0, by + k * MY + s, j == 0 && k == 0);
                }
            }
            // руст вокруг проёма — белая кайма, затем само окно
            var er = RectangleF.FromLTRB(X(ex0), Y(ey1), X(ex1), Y(ey0));
            if (S.GapAround)
            {
                float m = (float)Math.Max(0, 2.0 - S.Gv * sc);
                er.Inflate(m, m);
            }
            g.FillRectangle(Brushes.White, er);
            var wr = RectangleF.FromLTRB(X(wx0), Y(wy0 + wh), X(wx0 + ww), Y(wy0));
            using (var wb = new SolidBrush(Color.FromArgb(214, 226, 238)))
                g.FillRectangle(wb, wr);
            using (var wp = new Pen(Color.FromArgb(70, 80, 100), 1f))
                g.DrawRectangle(wp, wr.X, wr.Y, wr.Width, wr.Height);
            g.ResetClip();
            using (var fp = new Pen(Color.FromArgb(34, 34, 34), 1.5f))
                g.DrawRectangle(fp, frame.X, frame.Y, frame.Width, frame.Height);
            // точка отсчёта
            double ax = S.AnchorH == "L" ? 0 : S.AnchorH == "R" ? FW : FW / 2;
            double ay = S.AnchorV == "B" ? 0 : S.AnchorV == "T" ? FH : FH / 2;
            using (var rb = new SolidBrush(Color.FromArgb(179, 38, 30)))
                g.FillEllipse(rb, X(ax) - 4.5f, Y(ay) - 4.5f, 9f, 9f);
            full.Dispose(); cut.Dispose(); edge.Dispose();
        }
    }
}
