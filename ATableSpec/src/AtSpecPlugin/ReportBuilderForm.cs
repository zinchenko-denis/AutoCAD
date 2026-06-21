// Окно-построитель отчёта (Фаза 2 + заход 21.06): несколько секций-отчётов в ОДНОЙ
// таблице. Каждая секция = свой набор столбцов (Заголовок|Выражение) + Источник (слой)
// + Фильтр (несколько условий, И) + Группировка/Сортировка.
//
// Изменения захода 21.06 (по фидбэку конструктора):
//   (1) выбор шаблона — НЕ отдельным окном, а выпадающим списком «Шаблон» вверху формы;
//       смена шаблона пересевает секции (с подтверждением). Окно TemplatePickerForm убрано.
//   (2) «Выражение» — обычный TextBox-столбец (свободный ввод коммитится всегда; combo
//       раньше терял введённый текст, которого нет в списке). Удобство: автодополнение
//       в ячейке + ПКМ «Вставить выражение».
//   (3) Фильтр — видимая мини-таблица прямо на карточке (вместо попапа): Поле|Условие|
//       Значение, несколько строк = И. Поле — только поля блоков выбранного слоя;
//       Значение — автодополнение реальными значениями поля у блоков слоя (+ свободный ввод).
//
// Формирует определение отчёта (ReportDef) для движка (action=report):
//   { title, hide_title, scale, sections:[ {section_title, hide_header, header,
//     header_merges, columns, filter, group_by, sort_by, total_row}, ... ] }
//
// Подсказка по выражениям (как в построителе СПДС):
//   =Object.«ИМЯ»  -> атрибут; =Object.Name -> имя блока; =Object.«Длина»-150 -> арифм.;
//   =«01 02 04» -> литерал; ="Опора "+Object.«ИМЯ» -> конкатенация;
//   =Count -> кол-во в группе; =row -> нумерация (1-базово, своя в каждой секции).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AtSpecPlugin
{
    public class ReportBuilderForm : Form
    {
        private readonly List<string> _layers, _fields;
        // карта значений: слой -> поле -> отсортированные уникальные значения; ключ "" = все блоки.
        private readonly Dictionary<string, Dictionary<string, List<string>>> _valuesByLayer;
        private readonly List<SectionCard> _cards = new List<SectionCard>();
        private TextBox txtTitle;
        private CheckBox chkHideTitle;
        private NumericUpDown nudScale;
        private ComboBox cbTemplate;
        private FlowLayoutPanel flow;

        private int _template;
        private bool _suppressTpl;
        public Dictionary<string, object> ReportDef { get; private set; }

        // template: 0 — Ручное (пусто), 1 — Спецификация, 2 — Раскрой, 3 — Заполнения.
        public ReportBuilderForm(List<string> layers, List<string> fields,
                                 Dictionary<string, Dictionary<string, List<string>>> valuesByLayer = null,
                                 int template = 1)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            _valuesByLayer = valuesByLayer;
            _template = (template >= 0 && template <= 3) ? template : 1;
            BuildUi();
            _suppressTpl = true;
            cbTemplate.SelectedIndex = _template;     // index == номер шаблона
            _suppressTpl = false;
            txtTitle.Text = DefaultTitleFor(_template);
            AddSection(PresetFor(_template, true));    // стартовая секция, засеяна под шаблон
        }

        private void BuildUi()
        {
            Text = "ATableSpec — построитель отчёта";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 720);
            MinimumSize = new Size(720, 520);
            MaximizeBox = true; MinimizeBox = false;

            int x = 12, y = 12, lblW = 90;

            Controls.Add(new Label { Left = x, Top = y + 4, Width = lblW, Text = "Заголовок:" });
            txtTitle = new TextBox { Left = x + lblW, Top = y, Width = 392, Text = "СПЕЦИФИКАЦИЯ ЭЛЕМЕНТОВ",
                                     Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(txtTitle);
            chkHideTitle = new CheckBox { Left = x + lblW + 404, Top = y + 2, Width = 230,
                                          Text = "Скрыть заголовок таблицы", Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Controls.Add(chkHideTitle);
            y += 32;

            Controls.Add(new Label { Left = x, Top = y + 4, Width = 62, Text = "Масштаб:" });
            nudScale = new NumericUpDown { Left = x + 64, Top = y, Width = 84, Minimum = 1, Maximum = 100000,
                                           Value = 100, DecimalPlaces = 0, Increment = 100 };
            Controls.Add(nudScale);
            try   // #5: новые таблицы открываются с последним применённым масштабом (HKCU\Software\ATableSpec)
            {
                using (var rk = Registry.CurrentUser.OpenSubKey(@"Software\ATableSpec"))
                {
                    object rv = rk == null ? null : rk.GetValue("Scale");
                    double sv;
                    if (rv != null && double.TryParse(Convert.ToString(rv), out sv) && sv >= 1)
                        nudScale.Value = (decimal)Math.Min(sv, 100000);
                }
            }
            catch { }

            // (1) выбор шаблона — выпадающим списком прямо в форме
            Controls.Add(new Label { Left = x + 162, Top = y + 4, Width = 60, Text = "Шаблон:" });
            cbTemplate = new ComboBox { Left = x + 224, Top = y, Width = 168, DropDownStyle = ComboBoxStyle.DropDownList };
            cbTemplate.Items.AddRange(new object[] { "Ручное (пусто)", "Спецификация", "Раскрой", "Заполнения" });
            cbTemplate.SelectedIndexChanged += (s, e) => ApplyTemplate(cbTemplate.SelectedIndex);
            Controls.Add(cbTemplate);
            Controls.Add(new Label { Left = x + 400, Top = y + 4, Width = 340,
                Text = "Шаблон засевает столбцы; всё редактируемо. «+ Добавить отчёт» — ещё секция.",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });
            y += 34;

            flow = new FlowLayoutPanel
            {
                Left = x, Top = y, Width = ClientSize.Width - 2 * x, Height = ClientSize.Height - y - 90,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                BorderStyle = BorderStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(flow);

            var btnAdd = new Button { Text = "+ Добавить отчёт", Left = x, Top = ClientSize.Height - 78, Width = 160,
                                      Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnAdd.Click += (s, e) => AddSection(PresetFor(_template, false));
            Controls.Add(btnAdd);

            var ok = new Button { Text = "Построить", Width = 100, Top = ClientSize.Height - 40,
                                  Left = ClientSize.Width - 222, DialogResult = DialogResult.OK,
                                  Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var cancel = new Button { Text = "Отмена", Width = 100, Top = ClientSize.Height - 40,
                                      Left = ClientSize.Width - 112, DialogResult = DialogResult.Cancel,
                                      Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            ok.Click += (s, e) => BuildDef();
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        // смена шаблона из списка: пересеять секции заготовкой (с подтверждением, чтобы
        // случайно не затереть наработанное).
        private void ApplyTemplate(int tpl)
        {
            if (_suppressTpl) return;
            if (tpl < 0 || tpl > 3) return;
            if (_cards.Count > 0)
            {
                var r = MessageBox.Show("Заменить все секции заготовкой шаблона?", "ATableSpec",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    _suppressTpl = true; cbTemplate.SelectedIndex = _template; _suppressTpl = false;
                    return;
                }
            }
            foreach (var c in new List<SectionCard>(_cards)) { flow.Controls.Remove(c); c.Dispose(); }
            _cards.Clear();
            _template = tpl;
            txtTitle.Text = DefaultTitleFor(tpl);
            AddSection(PresetFor(tpl, true));
        }

        private void AddSection(SectionSeed seed)
        {
            var card = new SectionCard(_layers, _fields, _valuesByLayer, seed);
            card.MoveUpRequested += MoveCardUp;
            card.MoveDownRequested += MoveCardDown;
            card.RemoveRequested += RemoveCard;
            _cards.Add(card);
            flow.Controls.Add(card);
            Renumber();
            flow.ScrollControlIntoView(card);
        }

        // Стартовое наполнение секции под шаблон. useFirstLayer — авто-подстановка первого
        // слоя как источника (только для самой первой секции; для добавляемых — false).
        private static SectionSeed PresetFor(int tpl, bool useFirstLayer)
        {
            var s = new SectionSeed { UseFirstLayer = useFirstLayer };
            switch (tpl)
            {
                case 1: // Спецификация (рабочий пресет)
                    s.Columns.Add(new[] { "№ п/п", "=row" });
                    s.Columns.Add(new[] { "НАИМЕНОВАНИЕ", "=Object.«ИМЯ»" });
                    s.Columns.Add(new[] { "Артикул", "=Object.Name" });
                    s.Columns.Add(new[] { "Длина, мм", "=Object.«Длина»" });
                    s.Columns.Add(new[] { "Колич.", "=Count" });
                    s.Columns.Add(new[] { "Ед. изм.", "=«шт.»" });
                    s.GroupIdx = 1; s.SortMode = 0;
                    break;
                case 2: // Раскрой (ЧЕРНОВИК — уточняется по DXF конструктора)
                    s.Columns.Add(new[] { "№ п/п", "=row" });
                    s.Columns.Add(new[] { "Профиль", "=Object.Name" });
                    s.Columns.Add(new[] { "Длина, мм", "=Object.«Длина»" });
                    s.Columns.Add(new[] { "Колич.", "=Count" });
                    s.GroupIdx = 2; s.SortMode = 0;   // группа по длине
                    break;
                case 3: // Заполнения (Ш/В из РАЗМЕР_ЗАП; Тип — динам. параметр Visibility1)
                    s.Columns.Add(new[] { "№ п/п", "=row" });
                    s.Columns.Add(new[] { "Тип заполнения", "=Object.«Visibility1»" });
                    s.Columns.Add(new[] { "Марка", "=Object.«МАРКИРОВКА»" });
                    s.Columns.Add(new[] { "Ширина, мм", "=Object.«Ширина»" });
                    s.Columns.Add(new[] { "Высота, мм", "=Object.«Высота»" });
                    s.Columns.Add(new[] { "Колич.", "=Count" });
                    s.Columns.Add(new[] { "Площадь, м²", "=Object.«Ширина»*Object.«Высота»*Count/1000000" });
                    s.GroupIdx = 2; s.SortMode = 0;     // группа по марке (столбец 2: №=0, Тип=1, Марка=2)
                    s.TotalRow = true;                  // строка ИТОГ (сумма кол-ва и площади)
                    if (useFirstLayer) s.SeedLayer = "RF-заполнения";   // авто-источник для шаблона
                    break;
                default: // 0 — Ручное: пустая секция
                    break;
            }
            return s;
        }

        private static string DefaultTitleFor(int tpl)
        {
            switch (tpl)
            {
                case 2: return "ВЕДОМОСТЬ РАСКРОЯ";
                case 3: return "СПЕЦИФИКАЦИЯ ЗАПОЛНЕНИЙ";
                case 0: return "";                       // ручное — без заголовка по умолчанию
                default: return "СПЕЦИФИКАЦИЯ ЭЛЕМЕНТОВ"; // спецификация
            }
        }

        private void RemoveCard(SectionCard card)
        {
            if (_cards.Count <= 1)
            {
                MessageBox.Show("Нужна хотя бы одна секция.", "ATableSpec",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _cards.Remove(card);
            flow.Controls.Remove(card);
            card.Dispose();
            Renumber();
        }

        private void MoveCardUp(SectionCard card)
        {
            int i = _cards.IndexOf(card);
            if (i <= 0) return;
            _cards[i] = _cards[i - 1]; _cards[i - 1] = card;
            SyncFlowOrder(); Renumber();
        }

        private void MoveCardDown(SectionCard card)
        {
            int i = _cards.IndexOf(card);
            if (i < 0 || i >= _cards.Count - 1) return;
            _cards[i] = _cards[i + 1]; _cards[i + 1] = card;
            SyncFlowOrder(); Renumber();
        }

        private void SyncFlowOrder()
        {
            for (int i = 0; i < _cards.Count; i++) flow.Controls.SetChildIndex(_cards[i], i);
        }
        private void Renumber()
        {
            for (int i = 0; i < _cards.Count; i++) _cards[i].SetIndex(i + 1);
        }

        private void BuildDef()
        {
            var sections = new List<object>();
            foreach (var card in _cards)
                if (card.HasColumns()) sections.Add(card.ToDef());
            if (sections.Count == 0)
            {
                MessageBox.Show("Добавьте хотя бы одну секцию со столбцами (Заголовок | Выражение).",
                    "ATableSpec", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;   // не закрывать форму
                return;
            }
            ReportDef = new Dictionary<string, object>
            {
                { "title", txtTitle.Text },
                { "hide_title", chkHideTitle.Checked },
                { "scale", (double)nudScale.Value },
                { "sections", sections }
            };
            try   // #5: запомнить масштаб — следующая таблица создастся с ним
            {
                using (var rk = Registry.CurrentUser.CreateSubKey(@"Software\ATableSpec"))
                    if (rk != null) rk.SetValue("Scale", ((int)nudScale.Value).ToString());
            }
            catch { }
        }
    }

    // ───────────────────────── карточка одной секции ─────────────────────────
    public class SectionCard : Panel
    {
        private readonly List<string> _layers, _fields;
        private readonly Dictionary<string, Dictionary<string, List<string>>> _valuesByLayer;
        private Label lblNum, lblSummary;
        private TextBox txtSecTitle;
        private CheckBox chkHideHeader, chkTotal;
        private DataGridView grid;     // столбцы (Заголовок | Выражение)
        private DataGridView fgrid;    // фильтр (Поле | Условие | Значение)
        private DataGridViewComboBoxColumn _colFField;
        private readonly List<int[]> _merges = new List<int[]>();   // [s,e] 0-базово
        private readonly List<string> _exprSuggest = new List<string>();

        private string _layer = "";
        private int _groupIdx = -1;     // 0-базовый индекс столбца группировки (-1 = нет)
        private int _sortMode = 0;      // 0 — по возрастанию, 1 — по убыванию, 2 — без сортировки

        public event Action<SectionCard> MoveUpRequested, MoveDownRequested, RemoveRequested;

        public SectionCard(List<string> layers, List<string> fields,
                           Dictionary<string, Dictionary<string, List<string>>> valuesByLayer, SectionSeed seed)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            _valuesByLayer = valuesByLayer;
            BorderStyle = BorderStyle.FixedSingle;
            Width = 712; Height = 358; Margin = new Padding(0, 0, 0, 8);

            _exprSuggest.AddRange(new[] { "=row", "=Count", "=Sum", "=«шт.»", "=Object.Name", "=Object.«ИМЯ»", "=Object.«Длина»" });
            foreach (var f in _fields) { string e = "=Object.«" + f + "»"; if (!_exprSuggest.Contains(e)) _exprSuggest.Add(e); }

            BuildUi(seed);
            if (seed != null)
            {
                _groupIdx = seed.GroupIdx; _sortMode = seed.SortMode;
                if (!string.IsNullOrEmpty(seed.SeedLayer) && _layers.Contains(seed.SeedLayer))
                    _layer = seed.SeedLayer;                       // авто-источник из пресета (RF-заполнения)
                else if (seed.UseFirstLayer && _layers.Count > 0)
                    _layer = _layers[0];
            }
            RefreshFilterFields();
            RefreshSummary();
        }

        private void BuildUi(SectionSeed seed)
        {
            int y = 8;
            lblNum = new Label { Left = 8, Top = y + 4, Width = 70, Text = "Отчёт", Font = new Font(Font, FontStyle.Bold) };
            Controls.Add(lblNum);

            var btnSrc = new Button { Left = 84, Top = y, Width = 100, Text = "Источник…" };
            var btnGrp = new Button { Left = 188, Top = y, Width = 116, Text = "Группировка…" };
            btnSrc.Click += (s, e) => OpenSource();
            btnGrp.Click += (s, e) => OpenGroup();
            Controls.Add(btnSrc); Controls.Add(btnGrp);

            var btnUp = new Button { Left = 610, Top = y, Width = 28, Text = "↑" };
            var btnDn = new Button { Left = 640, Top = y, Width = 28, Text = "↓" };
            var btnDel = new Button { Left = 670, Top = y, Width = 28, Text = "✕" };
            btnUp.Click += (s, e) => { var h = MoveUpRequested; if (h != null) h(this); };
            btnDn.Click += (s, e) => { var h = MoveDownRequested; if (h != null) h(this); };
            btnDel.Click += (s, e) => { var h = RemoveRequested; if (h != null) h(this); };
            Controls.Add(btnUp); Controls.Add(btnDn); Controls.Add(btnDel);
            y += 30;

            Controls.Add(new Label { Left = 8, Top = y + 4, Width = 104, Text = "Заголовок секции:" });
            txtSecTitle = new TextBox { Left = 114, Top = y, Width = 180 };
            Controls.Add(txtSecTitle);
            chkHideHeader = new CheckBox { Left = 300, Top = y + 2, Width = 174, Text = "Скрыть шапку столбцов" };
            Controls.Add(chkHideHeader);
            chkTotal = new CheckBox { Left = 480, Top = y + 2, Width = 190, Text = "Строка ИТОГ (сумма)" };
            if (seed != null) chkTotal.Checked = seed.TotalRow;
            Controls.Add(chkTotal);
            y += 28;

            Controls.Add(new Label { Left = 8, Top = y, Width = 360, Text = "Столбцы (Заголовок | Выражение):" });
            y += 18;
            grid = new DataGridView
            {
                Left = 8, Top = y, Width = 690, Height = 116,
                AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            var colHdr = new DataGridViewTextBoxColumn { Name = "hdr", HeaderText = "Заголовок", Width = 230 };
            // (2) «Выражение» — обычный TextBox-столбец (свободный текст коммитится всегда).
            var colExpr = new DataGridViewTextBoxColumn
            {
                Name = "expr", HeaderText = "Выражение",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            };
            grid.Columns.Add(colHdr); grid.Columns.Add(colExpr);
            grid.EditingControlShowing += Grid_EditingControlShowing;
            grid.DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };
            grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode)
                { DeleteSelectedRows(); e.Handled = true; }
            };
            if (seed != null)
                foreach (var c in seed.Columns)
                    if (c != null && c.Length >= 2) grid.Rows.Add(c[0], c[1]);

            var menu = new ContextMenuStrip();
            var miInsert = new ToolStripMenuItem("Вставить выражение");
            foreach (var sx in _exprSuggest)
            {
                string val = sx;
                var it = new ToolStripMenuItem(sx);
                it.Click += (s, e) => InsertExpr(val);
                miInsert.DropDownItems.Add(it);
            }
            var miDelRow = new ToolStripMenuItem("Удалить строку");
            var miMerge = new ToolStripMenuItem("Объединить шапку выделенных столбцов");
            var miUnmerge = new ToolStripMenuItem("Разъединить шапку");
            miDelRow.Click += (s, e) => DeleteSelectedRows();
            miMerge.Click += (s, e) => MergeSelectedHeader();
            miUnmerge.Click += (s, e) => UnmergeSelectedHeader();
            menu.Items.Add(miInsert);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miDelRow);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miMerge); menu.Items.Add(miUnmerge);
            grid.ContextMenuStrip = menu;
            Controls.Add(grid);
            y += 122;

            // (3) фильтр — видимая мини-таблица (несколько условий = И; значения из источника)
            Controls.Add(new Label { Left = 8, Top = y, Width = 680,
                Text = "Фильтр (несколько строк = И; поля и значения берутся из выбранного источника):" });
            y += 18;
            fgrid = new DataGridView
            {
                Left = 8, Top = y, Width = 690, Height = 96,
                AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            _colFField = new DataGridViewComboBoxColumn
            {
                Name = "ffield", HeaderText = "Поле", Width = 200,
                FlatStyle = FlatStyle.Flat, DropDownWidth = 200, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing
            };
            var colFOp = new DataGridViewComboBoxColumn
            {
                Name = "fop", HeaderText = "Условие", Width = 120,
                FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing
            };
            colFOp.Items.AddRange(new object[] { "=", "≠", "содержит", "не содержит", ">", "<", "≥", "≤" });
            var colFVal = new DataGridViewTextBoxColumn
            {
                Name = "fval", HeaderText = "Значение", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            };
            fgrid.Columns.Add(_colFField); fgrid.Columns.Add(colFOp); fgrid.Columns.Add(colFVal);
            fgrid.DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };
            fgrid.EditingControlShowing += Fgrid_EditingControlShowing;
            fgrid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == colFOp.Index) { }
                RefreshSummary();
            };
            fgrid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && !fgrid.IsCurrentCellInEditMode)
                {
                    var rows = new List<int>();
                    foreach (DataGridViewRow r in fgrid.SelectedRows) if (!r.IsNewRow) rows.Add(r.Index);
                    foreach (DataGridViewCell c in fgrid.SelectedCells)
                        if (c.OwningRow != null && !c.OwningRow.IsNewRow && !rows.Contains(c.RowIndex)) rows.Add(c.RowIndex);
                    if (rows.Count == 0 && fgrid.CurrentRow != null && !fgrid.CurrentRow.IsNewRow) rows.Add(fgrid.CurrentRow.Index);
                    rows.Sort(); rows.Reverse();
                    foreach (int i in rows) if (i >= 0 && i < fgrid.Rows.Count && !fgrid.Rows[i].IsNewRow) fgrid.Rows.RemoveAt(i);
                    RefreshSummary(); e.Handled = true;
                }
            };
            Controls.Add(fgrid);
            y += 102;

            lblSummary = new Label { Left = 8, Top = y, Width = 690,
                Text = "Источник: —    Фильтр: —    Группа: —" };
            Controls.Add(lblSummary);
        }

        public void SetIndex(int n) { lblNum.Text = "Отчёт " + n; }

        // ── вставка готового выражения (ПКМ-меню) в текущую строку столбцов ──
        private void InsertExpr(string expr)
        {
            var c = grid.CurrentCell;
            int row = (c != null) ? c.RowIndex : -1;
            if (row < 0 || row >= grid.Rows.Count || grid.Rows[row].IsNewRow)
            {
                int n = grid.Rows.Add();
                row = n;
            }
            grid.Rows[row].Cells["expr"].Value = expr;
        }

        // ── автодополнение в ячейке «Выражение» ──
        private void Grid_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var tb = e.Control as TextBox;
            if (tb == null) return;
            tb.AutoCompleteMode = AutoCompleteMode.None;
            tb.AutoCompleteSource = AutoCompleteSource.None;
            if (grid.CurrentCell != null && grid.CurrentCell.OwningColumn != null
                && grid.CurrentCell.OwningColumn.Name == "expr")
            {
                var ac = new AutoCompleteStringCollection();
                ac.AddRange(_exprSuggest.ToArray());
                tb.AutoCompleteCustomSource = ac;
                tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            }
        }

        // ── автодополнение в ячейке «Значение» фильтра: значения поля строки у блоков слоя ──
        private void Fgrid_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var tb = e.Control as TextBox;
            if (tb == null) return;
            tb.AutoCompleteMode = AutoCompleteMode.None;
            tb.AutoCompleteSource = AutoCompleteSource.None;
            if (fgrid.CurrentCell != null && fgrid.CurrentCell.OwningColumn != null
                && fgrid.CurrentCell.OwningColumn.Name == "fval")
            {
                int ri = fgrid.CurrentCell.RowIndex;
                string field = (ri >= 0) ? Convert.ToString(fgrid.Rows[ri].Cells["ffield"].Value) : "";
                var vals = ValuesFor(_layer, field ?? "");
                if (vals.Count > 0)
                {
                    var ac = new AutoCompleteStringCollection();
                    ac.AddRange(vals.ToArray());
                    tb.AutoCompleteCustomSource = ac;
                    tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                    tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                }
            }
        }

        // поля для combo «Поле» фильтра: поля блоков выбранного слоя (+ служебные), либо все.
        private List<string> FieldsFor(string layer)
        {
            var outl = new List<string>();
            Dictionary<string, List<string>> byField = null;
            if (_valuesByLayer != null)
            {
                if (!string.IsNullOrEmpty(layer)) _valuesByLayer.TryGetValue(layer, out byField);
                if (byField == null) _valuesByLayer.TryGetValue("", out byField);
            }
            if (byField != null) { foreach (var k in byField.Keys) outl.Add(k); }
            else outl.AddRange(_fields);
            foreach (var ex in new[] { "Слой", "Имя", "Длина", "Ширина", "Высота" })
                if (!outl.Exists(z => NkEq(z, ex))) outl.Add(ex);
            outl.Sort(StringComparer.OrdinalIgnoreCase);
            return outl;
        }

        private List<string> ValuesFor(string layer, string field)
        {
            var empty = new List<string>();
            if (string.IsNullOrEmpty(field)) return empty;
            if (NkEq(field, "Слой"))
            {
                var l = new List<string>();
                if (!string.IsNullOrEmpty(layer)) l.Add(layer); else l.AddRange(_layers);
                return l;
            }
            if (_valuesByLayer == null) return empty;
            Dictionary<string, List<string>> byField = null;
            if (!string.IsNullOrEmpty(layer)) _valuesByLayer.TryGetValue(layer, out byField);
            if (byField == null) _valuesByLayer.TryGetValue("", out byField);
            if (byField == null) return empty;
            foreach (var kv in byField) if (NkEq(kv.Key, field)) return kv.Value;
            return empty;
        }

        // пересобрать список полей в combo фильтра под текущий источник (сохраняя уже выбранные)
        private void RefreshFilterFields()
        {
            if (_colFField == null) return;
            var fs = FieldsFor(_layer);
            _colFField.Items.Clear();
            foreach (var f in fs) _colFField.Items.Add(f);
            if (fgrid != null)
                foreach (DataGridViewRow r in fgrid.Rows)
                {
                    if (r.IsNewRow) continue;
                    string cur = Convert.ToString(r.Cells["ffield"].Value);
                    if (!string.IsNullOrEmpty(cur) && !_colFField.Items.Contains(cur)) _colFField.Items.Add(cur);
                }
        }

        private static string Nk(string s)
        {
            if (s == null) return "";
            s = s.Trim().Trim('«', '»', '"', ' ');
            return s.ToUpperInvariant();
        }
        private static bool NkEq(string a, string b) { return Nk(a) == Nk(b); }

        private void RefreshSummary()
        {
            string src = string.IsNullOrEmpty(_layer) ? "—" : _layer;
            string flt = FilterText();
            string grp = "—";
            if (_groupIdx >= 0)
            {
                var h = CurrentHeaders();
                string col = (_groupIdx < h.Count && h[_groupIdx].Length > 0) ? h[_groupIdx] : ("№" + (_groupIdx + 1));
                string ar = _sortMode == 0 ? " ↑" : (_sortMode == 1 ? " ↓" : "");
                grp = col + ar;
            }
            string mrg = _merges.Count == 0 ? "" : ("    Объед.шапки: " + MergesText());
            if (lblSummary != null)
                lblSummary.Text = "Источник: " + src + "    Фильтр: " + flt + "    Группа: " + grp + mrg;
        }
        private string FilterText()
        {
            var parts = new List<string>();
            if (fgrid != null)
                foreach (DataGridViewRow r in fgrid.Rows)
                {
                    if (r.IsNewRow) continue;
                    string fld = (Convert.ToString(r.Cells["ffield"].Value) ?? "").Trim();
                    string op = (Convert.ToString(r.Cells["fop"].Value) ?? "").Trim();
                    string val = (Convert.ToString(r.Cells["fval"].Value) ?? "").Trim();
                    if (fld.Length == 0 || val.Length == 0) continue;
                    if (op.Length == 0) op = "=";
                    parts.Add(fld + " " + op + " " + val);
                }
            return parts.Count == 0 ? "—" : string.Join(" и ", parts.ToArray());
        }
        private string MergesText()
        {
            var parts = new List<string>();
            foreach (var sp in _merges) parts.Add((sp[0] + 1) + "–" + (sp[1] + 1));
            return string.Join(", ", parts.ToArray());
        }

        private void DeleteSelectedRows()
        {
            var idx = new SortedSet<int>();
            foreach (DataGridViewRow r in grid.SelectedRows) if (!r.IsNewRow) idx.Add(r.Index);
            foreach (DataGridViewCell c in grid.SelectedCells)
                if (c.OwningRow != null && !c.OwningRow.IsNewRow) idx.Add(c.RowIndex);
            if (idx.Count == 0 && grid.CurrentRow != null && !grid.CurrentRow.IsNewRow) idx.Add(grid.CurrentRow.Index);
            if (idx.Count == 0) return;
            var list = new List<int>(idx); list.Sort(); list.Reverse();
            foreach (int i in list)
                if (i >= 0 && i < grid.Rows.Count && !grid.Rows[i].IsNewRow) grid.Rows.RemoveAt(i);

            int rc = 0;
            foreach (DataGridViewRow r in grid.Rows) if (!r.IsNewRow) rc++;
            _merges.RemoveAll(sp => sp[1] >= rc);     // объединение за пределами — снять
            if (_groupIdx >= rc) _groupIdx = -1;      // группа за пределами — сбросить
            RefreshSummary();
        }

        // ── объединение ШАПКИ столбцов этой секции (только строка-шапка) ──
        private List<int> SelectedOutputColumns()
        {
            var set = new SortedSet<int>();
            foreach (DataGridViewCell c in grid.SelectedCells)
                if (c.OwningRow != null && !c.OwningRow.IsNewRow) set.Add(c.RowIndex);
            foreach (DataGridViewRow r in grid.SelectedRows)
                if (!r.IsNewRow) set.Add(r.Index);
            return new List<int>(set);
        }
        private void MergeSelectedHeader()
        {
            var cols = SelectedOutputColumns();
            if (cols.Count < 2)
            {
                MessageBox.Show("Выделите 2+ столбца (строки в таблице слева), чтобы объединить их шапку.",
                    "Объединение шапки", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int s = cols[0], e = cols[cols.Count - 1];
            _merges.RemoveAll(sp => !(sp[1] < s || sp[0] > e));
            _merges.Add(new[] { s, e });
            _merges.Sort((a, b) => a[0].CompareTo(b[0]));
            RefreshSummary();
        }
        private void UnmergeSelectedHeader()
        {
            var cols = SelectedOutputColumns();
            if (cols.Count == 0) return;
            int s = cols[0], e = cols[cols.Count - 1];
            int removed = _merges.RemoveAll(sp => !(sp[1] < s || sp[0] > e));
            if (removed == 0)
                MessageBox.Show("В выделении нет объединённой шапки.",
                    "Разъединение шапки", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshSummary();
        }

        public bool HasColumns()
        {
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string h = Convert.ToString(r.Cells[0].Value) ?? "";
                string e = Convert.ToString(r.Cells[1].Value) ?? "";
                if (h.Length > 0 || e.Length > 0) return true;
            }
            return false;
        }

        // ── всплывающие окна (источник, группировка) ──
        private void OpenSource()
        {
            using (var dlg = new SourceDialog(_layers, _layer))
                if (dlg.ShowDialog(this) == DialogResult.OK)
                { _layer = dlg.Layer; RefreshFilterFields(); RefreshSummary(); }
        }
        private void OpenGroup()
        {
            using (var dlg = new GroupDialog(CurrentHeaders(), _groupIdx, _sortMode))
                if (dlg.ShowDialog(this) == DialogResult.OK)
                { _groupIdx = dlg.GroupIdx; _sortMode = dlg.SortMode; RefreshSummary(); }
        }

        private List<string> CurrentHeaders()
        {
            var l = new List<string>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                l.Add(Convert.ToString(r.Cells[0].Value) ?? "");
            }
            return l;
        }

        private static Dictionary<string, object> Cond(string field, string op, string value)
        {
            return new Dictionary<string, object> { { "field", field }, { "op", op }, { "value", value } };
        }

        public Dictionary<string, object> ToDef()
        {
            var headers = new List<object>();
            var columns = new List<object>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string h = Convert.ToString(r.Cells[0].Value) ?? "";
                string e = Convert.ToString(r.Cells[1].Value) ?? "";
                if (h.Length == 0 && e.Length == 0) continue;
                headers.Add(h); columns.Add(e);
            }
            int colCount = columns.Count;

            var filters = new List<object>();
            if (!string.IsNullOrEmpty(_layer))
                filters.Add(Cond("Слой", "=", _layer));
            foreach (DataGridViewRow r in fgrid.Rows)
            {
                if (r.IsNewRow) continue;
                string fld = (Convert.ToString(r.Cells["ffield"].Value) ?? "").Trim();
                string op = (Convert.ToString(r.Cells["fop"].Value) ?? "").Trim();
                string val = (Convert.ToString(r.Cells["fval"].Value) ?? "").Trim();
                if (fld.Length == 0 || val.Length == 0) continue;
                if (op.Length == 0) op = "=";
                filters.Add(Cond(fld, op, val));
            }

            object groupBy = (_groupIdx >= 0 && _groupIdx < colCount) ? (object)_groupIdx : null;
            object sortBy = null;
            if (groupBy != null && _sortMode != 2)
                sortBy = new object[] { _groupIdx, _sortMode == 0 ? "asc" : "desc" };

            var merges = new List<object>();
            foreach (var sp in _merges) merges.Add(new List<object> { sp[0], sp[1] });

            return new Dictionary<string, object>
            {
                { "section_title", txtSecTitle.Text },
                { "hide_header", chkHideHeader.Checked },
                { "header", headers },
                { "header_merges", merges },
                { "columns", columns },
                { "filter", filters },
                { "group_by", groupBy },
                { "sort_by", sortBy },
                { "total_row", chkTotal.Checked }
            };
        }
    }

    // ───────────────────────── всплывающее окно: Источник ─────────────────────────
    public class SourceDialog : Form
    {
        private ComboBox cb;
        public string Layer { get { return cb.Text; } }

        public SourceDialog(List<string> layers, string current)
        {
            Text = "Источник: слой";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 110);
            MaximizeBox = false; MinimizeBox = false;

            Controls.Add(new Label { Left = 12, Top = 16, Width = 70, Text = "Слой:" });
            cb = new ComboBox { Left = 84, Top = 12, Width = 260, DropDownStyle = ComboBoxStyle.DropDown };
            if (layers != null) cb.Items.AddRange(layers.ToArray());
            cb.Text = current ?? "";
            Controls.Add(cb);

            var ok = new Button { Text = "OK", Left = 164, Top = 64, Width = 84, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Отмена", Left = 256, Top = 64, Width = 88, DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    // ───────────────────────── всплывающее окно: Группировка ─────────────────────────
    public class GroupDialog : Form
    {
        private ComboBox cbGroup, cbSort;
        public int GroupIdx { get { return cbGroup.SelectedIndex - 1; } }     // -1 = (нет)
        public int SortMode { get { return cbSort.SelectedIndex; } }          // 0/1/2

        public GroupDialog(List<string> colHeaders, int groupIdx, int sortMode)
        {
            Text = "Группировка и сортировка";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 150);
            MaximizeBox = false; MinimizeBox = false;

            Controls.Add(new Label { Left = 12, Top = 16, Width = 130, Text = "Группа по столбцу:" });
            cbGroup = new ComboBox { Left = 148, Top = 12, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            cbGroup.Items.Add("(нет)");
            int n = colHeaders == null ? 0 : colHeaders.Count;
            for (int i = 0; i < n; i++)
            {
                string h = colHeaders[i];
                cbGroup.Items.Add((i + 1) + ": " + (string.IsNullOrEmpty(h) ? "(без названия)" : h));
            }
            int gsel = (groupIdx >= 0 && groupIdx < n) ? groupIdx + 1 : 0;
            cbGroup.SelectedIndex = gsel;
            Controls.Add(cbGroup);

            Controls.Add(new Label { Left = 12, Top = 52, Width = 130, Text = "Сортировка:" });
            cbSort = new ComboBox { Left = 148, Top = 48, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            cbSort.Items.AddRange(new object[] { "по возрастанию", "по убыванию", "(без сортировки)" });
            cbSort.SelectedIndex = (sortMode >= 0 && sortMode <= 2) ? sortMode : 0;
            Controls.Add(cbSort);

            var ok = new Button { Text = "OK", Left = 184, Top = 100, Width = 84, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Отмена", Left = 276, Top = 100, Width = 92, DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    // ───────────────────────── стартовое наполнение секции (пресет) ─────────────────────────
    public class SectionSeed
    {
        public List<string[]> Columns = new List<string[]>();   // каждый — {Заголовок, Выражение}
        public int GroupIdx = -1;                                // 0-базовый столбец группировки (-1 = нет)
        public int SortMode = 0;                                 // 0 — по возр., 1 — по убыв., 2 — без сорт.
        public bool UseFirstLayer = false;                       // подставить первый слой источником
        public bool TotalRow = false;                            // строка ИТОГ (сумма столбцов с Count)
        public string SeedLayer = null;                          // предпочтительный слой-источник (если есть)
    }
}
