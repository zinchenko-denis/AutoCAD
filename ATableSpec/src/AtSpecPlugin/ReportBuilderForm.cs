// Окно-построитель отчёта (Фаза 2 + заход по фидбэку Алексея): несколько секций-отчётов
// в ОДНОЙ таблице. Каждая секция = свой грид «Заголовок | Выражение | Условие | Значение»
// + Источник (слой) + Группировка/Сортировка.
//
// Изменения этого захода (по 5 пунктам конструктора):
//   (1) Отдельного поля «Шаблон» нет. «Заголовок» — редактируемый combo: выпадушка = шаблоны
//       (Спецификация/Раскрой/Заполнения/Ручное) — выбор засевает секции (с подтверждением) и
//       подставляет заголовок по умолчанию; печать = свой заголовок. Чекбокс «Скрыть заголовок».
//   (2) «Выражение» — снова выпадающий список (combo), контекстный выбранному слою (как «Поле»),
//       но со свободным вводом: введённый текст ВСЕГДА коммитится (правится корень гонки — на
//       CellValidating значение кладётся в Items колонки, поэтому combo его принимает).
//   (3) «Значение» — выпадающий список реальных значений поля (из «слой→поле→значения») +
//       свободный ввод.
//   (4) Фильтр — НЕ отдельная мини-таблица, а столбцы «Условие|Значение» прямо в гриде секции.
//       Условие строки фильтрует по ПОЛЮ из «Выражение» этой строки; несколько строк = И; строка
//       с условием, но пустым «Заголовком» — фильтр без вывода столбца; если «Выражение» не поле
//       (=Count/=row/литерал/чистая арифметика) — Условие/Значение неактивны. Источник-слой —
//       селектор «Источник…» сверху карточки (даёт неявный Слой=X). Движок не менялся (filter[]).
//   (5) деталировочные поля (DOBL/…/UGR) вырезаются в ReportCommand нормализованным сравнением.
//
// Определение отчёта (ReportDef) для движка (action=report):
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
        private ComboBox cbTitle;          // «Заголовок» = редактируемый combo (выпадушка = шаблоны)
        private CheckBox chkHideTitle;
        private NumericUpDown nudScale;
        private FlowLayoutPanel flow;

        // подписи выпадушки заголовка -> номер шаблона (0 Ручное, 1 Спец, 2 Раскрой, 3 Заполнения)
        private static readonly string[] TplLabels = { "Спецификация", "Раскрой", "Заполнения", "Ручное (пусто)" };
        private static readonly int[] TplOrder = { 1, 2, 3, 0 };

        private int _template;
        private bool _suppressTpl;
        private string _lastTitle = "";
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
            cbTitle.Text = DefaultTitleFor(_template);
            _suppressTpl = false;
            chkHideTitle.Checked = (_template == 2);   // раскрой — по умолчанию скрыть заголовок
            _lastTitle = cbTitle.Text;
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
            // (1) «Заголовок» — редактируемый combo; выпадушка = шаблоны.
            cbTitle = new ComboBox
            {
                Left = x + lblW, Top = y, Width = 392, DropDownStyle = ComboBoxStyle.DropDown,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            cbTitle.Items.AddRange(TplLabels);
            cbTitle.Text = "СПЕЦИФИКАЦИЯ ЭЛЕМЕНТОВ";
            cbTitle.DropDown += (s, e) => { _lastTitle = cbTitle.Text; };   // запомнить заголовок до выбора
            cbTitle.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressTpl) return;
                int sel = cbTitle.SelectedIndex;
                if (sel < 0 || sel >= TplOrder.Length) return;
                ApplyTemplate(TplOrder[sel]);
            };
            Controls.Add(cbTitle);
            chkHideTitle = new CheckBox
            {
                Left = x + lblW + 404, Top = y + 2, Width = 230,
                Text = "Скрыть заголовок таблицы", Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            Controls.Add(chkHideTitle);
            y += 32;

            Controls.Add(new Label { Left = x, Top = y + 4, Width = 62, Text = "Масштаб:" });
            nudScale = new NumericUpDown
            {
                Left = x + 64, Top = y, Width = 84, Minimum = 1, Maximum = 100000,
                Value = 100, DecimalPlaces = 0, Increment = 100
            };
            Controls.Add(nudScale);
            try   // новые таблицы открываются с последним применённым масштабом (HKCU\Software\ATableSpec)
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

            Controls.Add(new Label
            {
                Left = x + 162, Top = y + 4, Width = 578,
                Text = "Выпадушка «Заголовка» = шаблон (засевает столбцы); всё редактируемо. «+ Добавить отчёт» — ещё секция.",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });
            y += 34;

            flow = new FlowLayoutPanel
            {
                Left = x, Top = y, Width = ClientSize.Width - 2 * x, Height = ClientSize.Height - y - 90,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                BorderStyle = BorderStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(flow);

            var btnAdd = new Button
            {
                Text = "+ Добавить отчёт", Left = x, Top = ClientSize.Height - 78, Width = 160,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnAdd.Click += (s, e) => AddSection(PresetFor(_template, false));
            Controls.Add(btnAdd);

            var ok = new Button
            {
                Text = "Построить", Width = 100, Top = ClientSize.Height - 40,
                Left = ClientSize.Width - 222, DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            var cancel = new Button
            {
                Text = "Отмена", Width = 100, Top = ClientSize.Height - 40,
                Left = ClientSize.Width - 112, DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            ok.Click += (s, e) => BuildDef();
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        // выбор шаблона из выпадушки заголовка: пересеять секции заготовкой (с подтверждением).
        private void ApplyTemplate(int tpl)
        {
            if (tpl < 0 || tpl > 3) return;
            if (_cards.Count > 0)
            {
                var r = MessageBox.Show("Заменить все секции заготовкой шаблона?", "ATableSpec",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    _suppressTpl = true; cbTitle.SelectedIndex = -1; cbTitle.Text = _lastTitle; _suppressTpl = false;
                    return;
                }
            }
            foreach (var c in new List<SectionCard>(_cards)) { flow.Controls.Remove(c); c.Dispose(); }
            _cards.Clear();
            _template = tpl;
            _suppressTpl = true; cbTitle.SelectedIndex = -1; cbTitle.Text = DefaultTitleFor(tpl); _suppressTpl = false;
            chkHideTitle.Checked = (tpl == 2);   // раскрой — по умолчанию скрыть заголовок таблицы
            _lastTitle = cbTitle.Text;
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
                    s.Columns.Add(new[] { "Артикул", "=Object.«ПРОФ»" });
                    s.Columns.Add(new[] { "Длина, мм", "=Object.«Длина»" });
                    s.Columns.Add(new[] { "Колич.", "=Count" });
                    s.Columns.Add(new[] { "Ед. изм.", "=«шт.»" });
                    s.GroupIdx = 1; s.SortMode = 0;
                    break;
                case 2: // Раскрой — только длина + количество (фидбэк 21.06: №п/п и профиль убрать)
                    s.Columns.Add(new[] { "Длина, мм", "=Object.«Длина»" });
                    s.Columns.Add(new[] { "Колич.", "=Count" });
                    s.GroupIdx = 0; s.SortMode = 0;   // группа по длине (теперь это столбец 0)
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
                case 2: return "";                       // раскрой — без заголовка (по задумке конструктора)
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
                { "title", cbTitle.Text },
                { "hide_title", chkHideTitle.Checked },
                { "scale", (double)nudScale.Value },
                { "sections", sections }
            };
            try   // запомнить масштаб — следующая таблица создастся с ним
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
        private DataGridView grid;     // Заголовок | Выражение | Условие | Значение
        private DataGridViewTextBoxColumn _colExpr;     // «Выражение» — TextBox (надёжный свободный ввод)
        private DataGridViewComboBoxColumn _colCond, _colVal, _colGroup;
        private ToolStripMenuItem _miInsert;
        private readonly List<int[]> _merges = new List<int[]>();   // [s,e] 0-базово (по строкам грида)
        private readonly List<string> _exprSuggest = new List<string>();
        private bool _syncingGrp;       // защита от реентрантности при «единственной группе»

        private string _layer = "";

        public event Action<SectionCard> MoveUpRequested, MoveDownRequested, RemoveRequested;

        public SectionCard(List<string> layers, List<string> fields,
                           Dictionary<string, Dictionary<string, List<string>>> valuesByLayer, SectionSeed seed)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            _valuesByLayer = valuesByLayer;
            BorderStyle = BorderStyle.FixedSingle;
            Width = 712; Height = 308; Margin = new Padding(0, 0, 0, 8);

            BuildUi(seed);
            if (seed != null)
            {
                if (!string.IsNullOrEmpty(seed.SeedLayer) && _layers.Contains(seed.SeedLayer))
                    _layer = seed.SeedLayer;                       // авто-источник из пресета (RF-заполнения)
                else if (seed.UseFirstLayer && _layers.Count > 0)
                    _layer = _layers[0];
            }
            RefreshContext();
            for (int i = 0; i < grid.Rows.Count; i++)
                if (!grid.Rows[i].IsNewRow) SetFilterEnabledForRow(i);
            RefreshSummary();
        }

        private void BuildUi(SectionSeed seed)
        {
            int y = 8;
            lblNum = new Label { Left = 8, Top = y + 4, Width = 70, Text = "Отчёт", Font = new Font(Font, FontStyle.Bold) };
            Controls.Add(lblNum);

            var btnSrc = new Button { Left = 84, Top = y, Width = 120, Text = "Источник…" };
            btnSrc.Click += (s, e) => OpenSource();
            Controls.Add(btnSrc);

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

            Controls.Add(new Label { Left = 8, Top = y, Width = 660,
                Text = "Столбцы, фильтр и группировка (Заголовок | Выражение | Условие | Значение | Группа):" });
            y += 18;
            grid = new DataGridView
            {
                Left = 8, Top = y, Width = 690, Height = 196,
                AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            var colHdr = new DataGridViewTextBoxColumn { Name = "hdr", HeaderText = "Заголовок", Width = 150 };
            // (2-fix) «Выражение» — обычный TextBox со свободным вводом (combo в DataGridView терял
            //         введённый текст на коммите — фидбэк Алексея). Подсказки — автодополнением
            //         (контекстный список слою) + ПКМ «Вставить выражение».
            _colExpr = new DataGridViewTextBoxColumn { Name = "expr", HeaderText = "Выражение", Width = 190 };
            // (4) условие фильтра — прямо в гриде
            _colCond = new DataGridViewComboBoxColumn
            {
                Name = "cond", HeaderText = "Условие", Width = 80,
                FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing
            };
            _colCond.Items.AddRange(new object[] { "", "=", "≠", "содержит", "не содержит", ">", "<", "≥", "≤" });
            // (3) значение фильтра — combo реальных значений поля + свободный ввод
            _colVal = new DataGridViewComboBoxColumn
            {
                Name = "val", HeaderText = "Значение",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FlatStyle = FlatStyle.Flat, DropDownWidth = 200,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing
            };
            // (хотелка) группировка — инлайн-столбец правее «Значение»: непустое значение = группа
            //  по этому столбцу с выбранной сортировкой; единственная группа на секцию.
            _colGroup = new DataGridViewComboBoxColumn
            {
                Name = "grp", HeaderText = "Группа", Width = 120,
                FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing
            };
            _colGroup.Items.AddRange(new object[] { "", "по возрастанию", "по убыванию", "без сортировки" });
            grid.Columns.Add(colHdr); grid.Columns.Add(_colExpr); grid.Columns.Add(_colCond);
            grid.Columns.Add(_colVal); grid.Columns.Add(_colGroup);

            // пустой пункт в combo «Значение» (чтобы пустая ячейка рендерилась без DataError)
            _colVal.Items.Add("");

            grid.EditingControlShowing += Grid_EditingControlShowing;
            grid.CellValidating += Grid_CellValidating;
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };
            grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode)
                { DeleteSelectedRows(); e.Handled = true; }
            };
            // засев строк: для строки-группы (seed.GroupIdx) ставим сортировку из seed.SortMode
            string[] grpLabels = { "по возрастанию", "по убыванию", "без сортировки" };
            if (seed != null)
                for (int gi = 0; gi < seed.Columns.Count; gi++)
                {
                    var c = seed.Columns[gi];
                    if (c == null || c.Length < 2) continue;
                    string grp = "";
                    if (seed.GroupIdx >= 0 && gi == seed.GroupIdx)
                        grp = grpLabels[(seed.SortMode >= 0 && seed.SortMode <= 2) ? seed.SortMode : 0];
                    grid.Rows.Add(c[0], c[1], "", "", grp);
                }

            var menu = new ContextMenuStrip();
            _miInsert = new ToolStripMenuItem("Вставить выражение");
            var miDelRow = new ToolStripMenuItem("Удалить строку");
            var miMerge = new ToolStripMenuItem("Объединить шапку выделенных столбцов");
            var miUnmerge = new ToolStripMenuItem("Разъединить шапку");
            miDelRow.Click += (s, e) => DeleteSelectedRows();
            miMerge.Click += (s, e) => MergeSelectedHeader();
            miUnmerge.Click += (s, e) => UnmergeSelectedHeader();
            menu.Items.Add(_miInsert);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miDelRow);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miMerge); menu.Items.Add(miUnmerge);
            grid.ContextMenuStrip = menu;
            Controls.Add(grid);
            y += 200;

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

        // редактор ячейки: combo «Выражение»/«Значение» — редактируемые; «Условие» — только выбор.
        private void Grid_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var col = grid.CurrentCell != null ? grid.CurrentCell.OwningColumn : null;
            if (col == null) return;
            if (col.Name == "expr")
            {
                // «Выражение» — TextBox с автодополнением (контекстный список слою); свободный ввод коммитится всегда.
                var tb = e.Control as TextBox;
                if (tb == null) return;
                var ac = new AutoCompleteStringCollection();
                ac.AddRange(_exprSuggest.ToArray());
                tb.AutoCompleteCustomSource = ac;
                tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                return;
            }
            var cb = e.Control as ComboBox;
            if (cb == null) return;
            if (col.Name == "cond") { cb.DropDownStyle = ComboBoxStyle.DropDownList; return; }
            if (col.Name == "val")
            {
                cb.DropDownStyle = ComboBoxStyle.DropDown;
                int ri = grid.CurrentCell.RowIndex;
                string expr = ri >= 0 ? Convert.ToString(grid.Rows[ri].Cells["expr"].Value) : "";
                string fld = ExtractField(expr);
                var vals = ValuesFor(_layer, fld ?? "");
                cb.Items.Clear();          // в выпадушке — контекстные значения поля строки
                foreach (var v in vals)
                {
                    cb.Items.Add(v);
                    if (!_colVal.Items.Contains(v)) _colVal.Items.Add(v);   // и в колонку — чтобы валидировались
                }
            }
        }

        // корень гонки combo: свободно введённый текст кладём в Items колонки ДО коммита →
        // combo его принимает (раньше отклонял и ячейка оставалась со старым значением).
        private void Grid_CellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            var col = grid.Columns[e.ColumnIndex];
            string v = Convert.ToString(e.FormattedValue);
            if (string.IsNullOrEmpty(v)) return;
            // только combo «Значение»: свободно введённое значение кладём в Items, чтобы оно прошло коммит
            if (col.Name == "val") { if (!_colVal.Items.Contains(v)) _colVal.Items.Add(v); }
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0)
            {
                string cn = grid.Columns[e.ColumnIndex].Name;
                if (cn == "expr") SetFilterEnabledForRow(e.RowIndex);
                else if (cn == "grp") EnforceSingleGroup(e.RowIndex);
            }
            RefreshSummary();
        }

        // единственная группа на секцию: при выборе «Группа» в строке снимаем её с остальных.
        private void EnforceSingleGroup(int ri)
        {
            if (_syncingGrp || ri < 0 || ri >= grid.Rows.Count) return;
            if (string.IsNullOrEmpty(Convert.ToString(grid.Rows[ri].Cells["grp"].Value))) return;
            _syncingGrp = true;
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow || r.Index == ri) continue;
                if (!string.IsNullOrEmpty(Convert.ToString(r.Cells["grp"].Value)))
                    r.Cells["grp"].Value = "";
            }
            _syncingGrp = false;
        }

        // Условие/Значение активны, только если «Выражение» строки ссылается на поле блока.
        private void SetFilterEnabledForRow(int ri)
        {
            if (ri < 0 || ri >= grid.Rows.Count) return;
            var row = grid.Rows[ri];
            if (row.IsNewRow) return;
            bool canFilter = ExtractField(Convert.ToString(row.Cells["expr"].Value)) != null;
            var bc = canFilter ? SystemColors.Window : SystemColors.Control;
            row.Cells["cond"].ReadOnly = !canFilter;
            row.Cells["val"].ReadOnly = !canFilter;
            row.Cells["cond"].Style.BackColor = bc;
            row.Cells["val"].Style.BackColor = bc;
        }

        // имя поля из выражения: первое «…» либо Object.Name (-> "Name"); иначе null (не поле).
        private static string ExtractField(string expr)
        {
            if (string.IsNullOrEmpty(expr)) return null;
            int i = expr.IndexOf('«');
            if (i >= 0) { int j = expr.IndexOf('»', i + 1); if (j > i + 1) return expr.Substring(i + 1, j - i - 1); }
            if (expr.IndexOf("Object.Name", StringComparison.OrdinalIgnoreCase) >= 0) return "Name";
            return null;
        }

        // поля выбранного слоя (+ служебные), либо все.
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

        // пересобрать контекст под текущий источник: подсказки выражений (combo «Выражение» + ПКМ).
        private void RefreshContext()
        {
            var fs = FieldsFor(_layer);
            _exprSuggest.Clear();
            _exprSuggest.AddRange(new[] { "=row", "=Count", "=Sum", "=«шт.»", "=Object.Name" });
            foreach (var f in fs)
            {
                if (NkEq(f, "Слой")) continue;     // слой задаётся источником, не выражением
                string ex = "=Object.«" + f + "»";
                if (!_exprSuggest.Contains(ex)) _exprSuggest.Add(ex);
            }
            // «Выражение» — TextBox: список подсказок (_exprSuggest) применяется автодополнением
            //  в Grid_EditingControlShowing; отдельный рендер Items не нужен.
            if (_miInsert != null)
            {
                _miInsert.DropDownItems.Clear();
                foreach (var sx in _exprSuggest)
                {
                    string val = sx;
                    var it = new ToolStripMenuItem(sx);
                    it.Click += (s, e) => InsertExpr(val);
                    _miInsert.DropDownItems.Add(it);
                }
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
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string g = (Convert.ToString(r.Cells["grp"].Value) ?? "").Trim();
                if (g.Length == 0) continue;
                string hh = Convert.ToString(r.Cells["hdr"].Value) ?? "";
                string col = hh.Length > 0 ? hh : ("№" + (r.Index + 1));
                string ar = g.StartsWith("по возр") ? " ↑" : (g.StartsWith("по убыв") ? " ↓" : "");
                grp = col + ar;
                break;
            }
            string mrg = _merges.Count == 0 ? "" : ("    Объед.шапки: " + MergesText());
            if (lblSummary != null)
                lblSummary.Text = "Источник: " + src + "    Фильтр: " + flt + "    Группа: " + grp + mrg;
        }

        // фильтр читается из строк грида: условие на ПОЛЕ из «Выражение» строки.
        private string FilterText()
        {
            var parts = new List<string>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string ex = Convert.ToString(r.Cells["expr"].Value) ?? "";
                string op = (Convert.ToString(r.Cells["cond"].Value) ?? "").Trim();
                string val = (Convert.ToString(r.Cells["val"].Value) ?? "").Trim();
                if (op.Length == 0 || val.Length == 0) continue;
                string fld = ExtractField(ex);
                if (string.IsNullOrEmpty(fld)) continue;
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
                string h = Convert.ToString(r.Cells["hdr"].Value) ?? "";
                string ex = Convert.ToString(r.Cells["expr"].Value) ?? "";
                string op = (Convert.ToString(r.Cells["cond"].Value) ?? "").Trim();
                string val = (Convert.ToString(r.Cells["val"].Value) ?? "").Trim();
                bool hasCond = op.Length > 0 && val.Length > 0;
                if (h.Length > 0 || (ex.Length > 0 && !hasCond)) return true;
            }
            return false;
        }

        // ── всплывающее окно: источник ──
        private void OpenSource()
        {
            using (var dlg = new SourceDialog(_layers, _layer))
                if (dlg.ShowDialog(this) == DialogResult.OK)
                { _layer = dlg.Layer; RefreshContext(); RefreshSummary(); }
        }

        private static Dictionary<string, object> Cond(string field, string op, string value)
        {
            return new Dictionary<string, object> { { "field", field }, { "op", op }, { "value", value } };
        }

        public Dictionary<string, object> ToDef()
        {
            var headers = new List<object>();
            var columns = new List<object>();
            var filters = new List<object>();
            if (!string.IsNullOrEmpty(_layer))
                filters.Add(Cond("Слой", "=", _layer));

            var rowToOut = new Dictionary<int, int>();   // индекс строки грида -> индекс выходного столбца
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string h = Convert.ToString(r.Cells["hdr"].Value) ?? "";
                string ex = Convert.ToString(r.Cells["expr"].Value) ?? "";
                string op = (Convert.ToString(r.Cells["cond"].Value) ?? "").Trim();
                string val = (Convert.ToString(r.Cells["val"].Value) ?? "").Trim();
                bool hasCond = op.Length > 0 && val.Length > 0;

                if (hasCond)   // условие фильтрует по полю из выражения этой строки
                {
                    string fld = ExtractField(ex);
                    if (!string.IsNullOrEmpty(fld)) filters.Add(Cond(fld, op, val));
                }
                // вывод столбца: есть заголовок ИЛИ (есть выражение и строка не «только-фильтр»)
                bool output = h.Length > 0 || (ex.Length > 0 && !hasCond);
                if (output)
                {
                    rowToOut[r.Index] = columns.Count;
                    headers.Add(h); columns.Add(ex);
                }
            }
            int colCount = columns.Count;

            // группа/сортировка — из инлайн-столбца «Группа» (строка с непустым значением)
            int gOut = -1, sortMode = 0;
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string g = (Convert.ToString(r.Cells["grp"].Value) ?? "").Trim();
                if (g.Length == 0) continue;
                int oi;
                if (rowToOut.TryGetValue(r.Index, out oi))
                {
                    gOut = oi;
                    sortMode = g.StartsWith("по возр") ? 0 : (g.StartsWith("по убыв") ? 1 : 2);
                }
                break;
            }
            if (gOut < 0 || gOut >= colCount) gOut = -1;
            object groupBy = gOut >= 0 ? (object)gOut : null;
            object sortBy = null;
            if (gOut >= 0 && sortMode != 2)
                sortBy = new object[] { gOut, sortMode == 0 ? "asc" : "desc" };

            var merges = new List<object>();   // объединения шапки — в координатах выходных столбцов
            foreach (var sp in _merges)
            {
                int s, e2;
                if (rowToOut.TryGetValue(sp[0], out s) && rowToOut.TryGetValue(sp[1], out e2) && e2 >= s)
                    merges.Add(new List<object> { s, e2 });
            }

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
