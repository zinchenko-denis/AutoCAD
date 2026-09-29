using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AFramePlugin
{
    /// <summary>
    /// Окно ATFRAME (Герман 23.09: «такое же диалоговое окно, как у ATTILE»):
    /// что раскладывать (подсистема и кляммеры / только подсистема / только
    /// кляммеры по существующей облицовке), тип и профиль, шаги по расчёту
    /// или вручную, оси и швы (если у зон нет раскладки), что указать после
    /// ОК, знаки. Заменяет ~15 вопросов командной строки; точки (углы,
    /// перекрытия, оси, образцы) по-прежнему указываются на чертеже после
    /// «Разложить». Собрано кодом, без дизайнера (конвенция репо).
    /// </summary>
    public class FrameForm : Form
    {
        private readonly FrameSettings _s;
        private readonly bool _hasLayout;
        private bool _loading;

        // 26.09 (Денис): первым пунктом — что облицовываем
        private readonly RadioButton _cPorc = new RadioButton(), _cComp = new RadioButton(),
                                     _cConc = new RadioButton(), _cClink = new RadioButton();
        // 29.09c (Герман): у плитки — шаг направляющих (рядовой/угловой), марка и хлыст шины
        private readonly NumericUpDown _tileH = Num(100, 3000, 0), _tileHC = Num(0, 3000, 0),
                                       _tileWhip = Num(300, 12000, 0);
        private readonly TextBox _brand = new TextBox();
        private readonly List<Control> _tileCtl = new List<Control>(), _axCtl = new List<Control>();
        private Label _rowLblAx, _rowLblTile, _railStepCLbl;
        private GroupBox _g1;
        private readonly RadioButton _mAll = new RadioButton(), _mFrame = new RadioButton(), _mClamps = new RadioButton();
        private readonly RadioButton _tVert = new RadioButton(), _tInter = new RadioButton(), _tOrtho = new RadioButton();
        private readonly ComboBox _profile = new ComboBox();
        private readonly RadioButton _calc = new RadioButton(), _manual = new RadioButton();
        private readonly ComboBox _wind = new ComboBox(), _terr = new ComboBox();
        private readonly NumericUpDown _height = Num(1, 500, 1), _qclad = Num(0.1m, 500, 1), _offset = Num(20, 1000, 0),
                                       _na = Num(100, 100000, 0);
        private readonly NumericUpDown _stepMain = Num(100, 3000, 0), _stepCorner = Num(100, 3000, 0),
                                       _railStepC = Num(0, 3000, 0), _startOff = Num(0, 1500, 0),
                                       _railGap = Num(0, 100, 0), _cornerZone = Num(0, 10000, 0);
        private readonly List<Control> _calcCtl = new List<Control>(), _manCtl = new List<Control>();
        private readonly Label _layoutInfo = new Label();
        private readonly RadioButton _axStep = new RadioButton(), _axPoints = new RadioButton();
        private readonly NumericUpDown _axisStep = Num(50, 20000, 0), _rowStep = Num(0, 20000, 0);
        private readonly CheckBox _corners = new CheckBox(), _floors = new CheckBox();
        private readonly NumericUpDown _floorStep = Num(0, 20000, 0);
        private readonly RadioButton _sCond = new RadioButton(), _sSamples = new RadioButton();
        private readonly Label _desc = new Label();
        private GroupBox _g2, _g3, _g4;

        public FrameSettings Result { get { return _s.Clone(); } }

        private static NumericUpDown Num(decimal min, decimal max, int dec)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = dec, Increment = dec > 0 ? 0.5m : 10m };
        }

        private static Label L(string text, int x, int y, int w)
        {
            return new Label { Text = text, Left = x, Top = y + 3, Width = w, Height = 18 };
        }

        private static void Place(Control c, int x, int y, int w)
        {
            c.Left = x; c.Top = y; c.Width = w;
        }

        public FrameForm(FrameSettings s, bool hasLayout)
        {
            _s = (s ?? new FrameSettings()).Clone();
            _hasLayout = hasLayout;
            Text = "ATFRAME — подсистема НВФ и кляммеры";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(860, 714);

            // ── 1. облицовка (26.09, Денис: «первым пунктом») ──
            var gc = new GroupBox { Text = "1. Облицовка", Left = 10, Top = 6, Width = 470, Height = 70 };
            _cPorc.Text = "керамогранит"; _cComp.Text = "композит";
            _cConc.Text = "бетонная плитка"; _cClink.Text = "клинкерная плитка";
            Place(_cPorc, 10, 18, 220); Place(_cComp, 240, 18, 220);
            Place(_cConc, 10, 42, 220); Place(_cClink, 240, 42, 220);
            gc.Controls.AddRange(new Control[] { _cPorc, _cComp, _cConc, _cClink });

            // ── 2. что раскладывать ──
            var g1 = new GroupBox { Text = "2. Что раскладывать", Left = 10, Top = 80, Width = 470, Height = 94 };
            _g1 = g1;
            _mAll.Text = "подсистему и кляммеры";
            _mFrame.Text = "только подсистему (без кляммеров)";
            _mClamps.Text = "только кляммеры (на существующие направляющие)";
            Place(_mAll, 10, 18, 450); Place(_mFrame, 10, 42, 450); Place(_mClamps, 10, 66, 450);
            g1.Controls.AddRange(new Control[] { _mAll, _mFrame, _mClamps });

            // ── 2. подсистема ──
            _g2 = new GroupBox { Text = "3. Подсистема", Left = 10, Top = 178, Width = 470, Height = 78 };
            _tVert.Text = "вертикальная"; _tInter.Text = "межэтажная"; _tOrtho.Text = "ортогональная";
            Place(_tVert, 10, 20, 130); Place(_tInter, 150, 20, 130); Place(_tOrtho, 290, 20, 160);
            _profile.DropDownStyle = ComboBoxStyle.DropDownList;
            Place(_profile, 150, 46, 310);
            _g2.Controls.AddRange(new Control[] { _tVert, _tInter, _tOrtho, L("Профиль:", 10, 46, 130), _profile });

            // ── 3. шаги кронштейнов ──
            _g3 = new GroupBox { Text = "4. Шаги кронштейнов", Left = 10, Top = 260, Width = 470, Height = 136 };
            _calc.Text = "по расчёту несущей способности"; _manual.Text = "вручную";
            Place(_calc, 10, 20, 240); Place(_manual, 260, 20, 200);
            _wind.DropDownStyle = ComboBoxStyle.DropDownList; _wind.Items.AddRange(FrameSettings.Winds);
            _terr.DropDownStyle = ComboBoxStyle.DropDownList; _terr.Items.AddRange(FrameSettings.Terrains);
            AddPair(_calcCtl, "Ветровой район:", _wind, 10, 48); AddPair(_calcCtl, "Тип местности:", _terr, 240, 48);
            AddPair(_calcCtl, "Высота здания, м:", _height, 10, 76); AddPair(_calcCtl, "Облицовка, кг/м²:", _qclad, 240, 76);
            AddPair(_calcCtl, "Вынос, мм:", _offset, 10, 104); AddPair(_calcCtl, "Анкер (вырыв), Н:", _na, 240, 104);
            AddPair(_manCtl, "Шаг рядовой, мм:", _stepMain, 10, 48); AddPair(_manCtl, "Шаг угловой, мм:", _stepCorner, 240, 48);
            AddPair(_manCtl, "Стойки в угл., мм:", _railStepC, 10, 76); AddPair(_manCtl, "Старт кронштейна:", _startOff, 240, 76);
            AddPair(_manCtl, "Зазор стыка, мм:", _railGap, 10, 104); AddPair(_manCtl, "Угловая зона, мм:", _cornerZone, 240, 104);
            _railStepCLbl = (Label)_manCtl[_manCtl.IndexOf(_railStepC) - 1];
            _g3.Controls.AddRange(new Control[] { _calc, _manual });
            foreach (var c in _calcCtl) _g3.Controls.Add(c);
            foreach (var c in _manCtl) _g3.Controls.Add(c);

            // ── 4. оси и швы (если у зон нет раскладки) ──
            _g4 = new GroupBox { Text = "5. Оси стоек и швы", Left = 10, Top = 400, Width = 470, Height = 130 };
            _layoutInfo.Left = 10; _layoutInfo.Top = 20; _layoutInfo.Width = 450; _layoutInfo.Height = 18;
            _layoutInfo.Text = hasLayout ? "Оси стоек и швы берутся из раскладки выбранных зон (ATTILE/ATCLAD)."
                                         : "У выбранных зон нет раскладки — оси и швы задать здесь:";
            _layoutInfo.ForeColor = hasLayout ? Color.DarkGreen : Color.Firebrick;
            _axStep.Text = "шагом от первой оси"; _axPoints.Text = "точками на чертеже";
            Place(_axStep, 10, 42, 220); Place(_axPoints, 240, 42, 220);
            Place(_axisStep, 150, 70, 80);
            _rowLblAx = L("Шаг швов (0 — нет):", 240, 70, 138);
            _axCtl.AddRange(new Control[] { _axStep, _axPoints, L("Шаг осей, мм:", 10, 70, 138), _axisStep, _rowLblAx });
            // плитка (29.09c): шаг вертикальных направляющих, марка и хлыст шины, ряды
            AddPair(_tileCtl, "Шаг направляющих:", _tileH, 10, 44);
            AddPair(_tileCtl, "В угловой зоне:", _tileHC, 240, 44);   // 0 — как рядовой (см. «Что будет»)
            _tileCtl.Add(L("Марка шины:", 10, 72, 138));
            Place(_brand, 150, 72, 310);
            _tileCtl.Add(_brand);
            AddPair(_tileCtl, "Хлыст шины, мм:", _tileWhip, 10, 100);
            _rowLblTile = L("Шаг рядов, мм:", 240, 100, 138);
            _tileCtl.Add(_rowLblTile);
            _g4.Controls.Add(_layoutInfo);
            foreach (var c in _axCtl) _g4.Controls.Add(c);
            foreach (var c in _tileCtl) _g4.Controls.Add(c);
            _g4.Controls.Add(_rowStep);

            // ── 5. после «Разложить» ──
            var g5 = new GroupBox { Text = "6. После «Разложить» указать на чертеже", Left = 10, Top = 534, Width = 470, Height = 76 };
            _corners.Text = "внешние углы здания (угловые зоны)";
            _floors.Text = "отметки перекрытий";
            Place(_corners, 10, 20, 450); Place(_floors, 10, 46, 200);
            Place(_floorStep, 380, 44, 80);
            g5.Controls.AddRange(new Control[] { _corners, _floors, L("Этаж без отметок, мм:", 230, 46, 148), _floorStep });

            // ── 6. знаки ──
            var g6 = new GroupBox { Text = "7. Знаки кронштейнов и кляммеров", Left = 10, Top = 614, Width = 470, Height = 50 };
            _sCond.Text = "условные"; _sSamples.Text = "образцы блоков с чертежа (указать после ОК)";
            Place(_sCond, 10, 20, 110); Place(_sSamples, 130, 20, 330);
            g6.Controls.AddRange(new Control[] { _sCond, _sSamples });

            // ── что будет ──
            var gd = new GroupBox { Text = "Что будет", Left = 490, Top = 6, Width = 360, Height = 658 };
            _desc.Left = 10; _desc.Top = 22; _desc.Width = 340; _desc.Height = 626; _desc.AutoSize = false;
            gd.Controls.Add(_desc);

            var reset = new Button { Text = "Сброс", Left = 10, Top = 674, Width = 110, Height = 30 };
            var ok = new Button { Text = "Разложить", Left = 630, Top = 674, Width = 106, Height = 30 };
            var cancel = new Button { Text = "Отмена", Left = 744, Top = 674, Width = 106, Height = 30,
                                      DialogResult = DialogResult.Cancel };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { gc, g1, _g2, _g3, _g4, g5, g6, gd, reset, ok, cancel });

            LoadControls();

            // ── события ──
            EventHandler upd = delegate { if (!_loading) { ReadControls(); LoadControls(); } };
            foreach (var rb in new[] { _mAll, _mFrame, _mClamps, _calc, _manual, _axStep, _axPoints, _sCond, _sSamples,
                                       _cPorc, _cComp, _cConc, _cClink })
                rb.CheckedChanged += upd;
            foreach (var rb in new[] { _tVert, _tInter, _tOrtho })
                rb.CheckedChanged += delegate(object o, EventArgs e)
                {
                    if (_loading || !((RadioButton)o).Checked) return;
                    ReadControls();
                    _s.SetSubType(o == _tInter ? "interfloor" : o == _tOrtho ? "ortho" : "vertical");
                    LoadControls();
                };
            foreach (var cb in new[] { _profile, _wind, _terr }) cb.SelectedIndexChanged += upd;
            foreach (var nu in new[] { _height, _qclad, _offset, _na, _stepMain, _stepCorner, _railStepC, _startOff,
                                       _railGap, _cornerZone, _axisStep, _rowStep, _floorStep, _tileH, _tileHC,
                                       _tileWhip })
                nu.ValueChanged += upd;
            _brand.TextChanged += upd;
            foreach (var ch in new[] { _corners, _floors }) ch.CheckedChanged += upd;
            reset.Click += delegate
            {
                var d = new FrameSettings();
                d.SetSubType(_s.SubType);
                d.Mode = _s.Mode;
                d.Cladding = _s.Cladding;
                // марки шин — проектные данные, «Сброс» их не стирает
                d.RailBrandConcrete = _s.RailBrandConcrete; d.RailBrandClinker = _s.RailBrandClinker;
                CopyInto(d, _s);
                LoadControls();
            };
            ok.Click += delegate
            {
                ReadControls();
                string err = _s.Validate(_hasLayout);
                if (err != null)
                {
                    MessageBox.Show(this, err, "ATFRAME", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
        }

        private static void CopyInto(FrameSettings src, FrameSettings dst)
        {
            var d = FrameSettings.FromDict(src.ToDict());
            foreach (var f in typeof(FrameSettings).GetFields())
                if (!f.IsStatic && !f.IsLiteral) f.SetValue(dst, f.GetValue(d));
        }

        private void AddPair(List<Control> into, string label, Control input, int x, int y)
        {
            into.Add(L(label, x, y, 138));
            Place(input, x + 140, y, 80);
            into.Add(input);
        }

        private static decimal Dec(double v, NumericUpDown n)
        {
            decimal d = (decimal)Math.Round(v, n.DecimalPlaces);
            if (d < n.Minimum) d = n.Minimum;
            if (d > n.Maximum) d = n.Maximum;
            return d;
        }

        private void LoadControls()
        {
            _loading = true;
            try
            {
                bool tile = _s.IsTile;
                _cPorc.Checked = _s.Cladding == "porcelain"; _cComp.Checked = _s.Cladding == "composite";
                _cConc.Checked = _s.Cladding == "concrete"; _cClink.Checked = _s.Cladding == "clinker";
                _tileH.Value = Dec(_s.TileStepH, _tileH); _tileHC.Value = Dec(_s.TileStepHCorner, _tileHC);
                _tileWhip.Value = Dec(_s.TileWhip, _tileWhip);
                if (_brand.Text != _s.RailBrand) _brand.Text = _s.RailBrand;
                _mAll.Checked = _s.Mode == "all"; _mFrame.Checked = _s.Mode == "frame"; _mClamps.Checked = _s.Mode == "clamps";
                _tVert.Checked = _s.EffSubType == "vertical"; _tInter.Checked = _s.InterFloor; _tOrtho.Checked = _s.Ortho;
                _profile.Items.Clear();
                string[] prs = FrameSettings.ProfilesFor(_s.EffSubType);
                if (prs.Length == 0) { _profile.Items.Add("ШП/ZП — автоматом"); _profile.SelectedIndex = 0; }
                else
                {
                    _profile.Items.AddRange(prs);
                    int k = Array.IndexOf(prs, _s.Profile);
                    _profile.SelectedIndex = k >= 0 ? k : 0;
                }
                _calc.Checked = _s.Steps != "manual"; _manual.Checked = _s.Steps == "manual";
                _wind.SelectedIndex = Math.Max(0, Array.IndexOf(FrameSettings.Winds, _s.WindRegion));
                _terr.SelectedIndex = Math.Max(0, Array.IndexOf(FrameSettings.Terrains, _s.Terrain));
                _height.Value = Dec(_s.Height, _height); _qclad.Value = Dec(_s.QClad, _qclad);
                _offset.Value = Dec(_s.Offset, _offset); _na.Value = Dec(_s.NaMax, _na);
                _stepMain.Value = Dec(_s.StepMain, _stepMain); _stepCorner.Value = Dec(_s.StepCorner, _stepCorner);
                _railStepC.Value = Dec(_s.RailStepCorner, _railStepC); _startOff.Value = Dec(_s.StartOff, _startOff);
                _railGap.Value = Dec(_s.RailGap, _railGap); _cornerZone.Value = Dec(_s.CornerZone, _cornerZone);
                _axStep.Checked = _s.Axes == "step"; _axPoints.Checked = _s.Axes == "points";
                _axisStep.Value = Dec(_s.AxisStep, _axisStep); _rowStep.Value = Dec(_s.RowStep, _rowStep);
                _corners.Checked = _s.AskCorners; _floors.Checked = _s.AskFloors;
                _floorStep.Value = Dec(_s.FloorStep, _floorStep);
                _sCond.Checked = _s.Signs == "cond"; _sSamples.Checked = _s.Signs == "samples";

                bool frameParts = !_s.ClampsOnly;
                // плитка (29.09c, Герман): кляммеров нет (что раскладывать — только
                // подсистема с шинами); тип и шаги кронштейнов — как у всех облицовок;
                // в п.5 вместо осей по швам — шаг направляющих, марка и хлыст шины
                _g1.Enabled = !tile;
                _profile.Enabled = frameParts && prs.Length > 0;
                _g3.Enabled = frameParts;
                foreach (var c in _calcCtl) c.Visible = !_s.Manual;
                foreach (var c in _manCtl) c.Visible = _s.Manual;
                _railStepC.Visible = _railStepCLbl.Visible = _s.Manual && !tile;   // у плитки — в п.5
                _g4.Text = tile ? "5. Направляющие и шины (плитка)" : "5. Оси стоек и швы";
                foreach (var c in _axCtl) { c.Visible = !tile; c.Enabled = !_hasLayout; }
                foreach (var c in _tileCtl) c.Visible = tile;
                _rowLblAx.Visible = !tile;
                _rowLblTile.Enabled = !_hasLayout;
                _axisStep.Enabled = !_hasLayout && _s.Axes == "step";
                _rowStep.Enabled = !_hasLayout;
                if (tile) Place(_rowStep, 380, 100, 80); else Place(_rowStep, 380, 70, 80);
                _layoutInfo.Text = tile
                    ? (_hasLayout ? "Рядовые шины — по рядам раскладки зон (ATTILE)."
                                  : "У зон нет раскладки — рядовые шины шагом рядов от низа зоны.")
                    : (_hasLayout ? "Оси стоек и швы берутся из раскладки выбранных зон (ATTILE/ATCLAD)."
                                  : "У выбранных зон нет раскладки — оси и швы задать здесь:");
                _layoutInfo.ForeColor = _hasLayout ? Color.DarkGreen : Color.Firebrick;
                _corners.Enabled = frameParts;
                _floorStep.Enabled = _s.InterFloor && frameParts;
                string err = _s.Validate(_hasLayout);
                _desc.ForeColor = err == null ? SystemColors.ControlText : Color.Firebrick;
                _desc.Text = err ?? _s.Describe(_hasLayout);
            }
            finally { _loading = false; }
        }

        private void ReadControls()
        {
            _s.Mode = _mClamps.Checked ? "clamps" : _mFrame.Checked ? "frame" : "all";
            // профиль — по списку, который сейчас в окне (облицовку меняем ниже)
            string[] prs = FrameSettings.ProfilesFor(_s.EffSubType);
            if (prs.Length > 0 && _profile.SelectedIndex >= 0 && _profile.SelectedIndex < prs.Length)
                _s.Profile = prs[_profile.SelectedIndex];
            _s.Steps = _manual.Checked ? "manual" : "calc";
            if (_wind.SelectedIndex >= 0) _s.WindRegion = FrameSettings.Winds[_wind.SelectedIndex];
            if (_terr.SelectedIndex >= 0) _s.Terrain = FrameSettings.Terrains[_terr.SelectedIndex];
            _s.Height = (double)_height.Value; _s.QClad = (double)_qclad.Value;
            _s.Offset = (double)_offset.Value; _s.NaMax = (double)_na.Value;
            _s.StepMain = (double)_stepMain.Value; _s.StepCorner = (double)_stepCorner.Value;
            _s.RailStepCorner = (double)_railStepC.Value; _s.StartOff = (double)_startOff.Value;
            _s.RailGap = (double)_railGap.Value; _s.CornerZone = (double)_cornerZone.Value;
            _s.Axes = _axPoints.Checked ? "points" : "step";
            _s.AxisStep = (double)_axisStep.Value; _s.RowStep = (double)_rowStep.Value;
            _s.AskCorners = _corners.Checked; _s.AskFloors = _floors.Checked;
            _s.FloorStep = (double)_floorStep.Value;
            _s.Signs = _sSamples.Checked ? "samples" : "cond";
            _s.TileStepH = (double)_tileH.Value; _s.TileStepHCorner = (double)_tileHC.Value;
            _s.TileWhip = (double)_tileWhip.Value;
            _s.RailBrand = _brand.Text;   // марка — той плитки, что сейчас в окне (облицовку меняем ниже)
            _s.Cladding = _cClink.Checked ? "clinker" : _cConc.Checked ? "concrete" : _cComp.Checked ? "composite" : "porcelain";
        }
    }
}
