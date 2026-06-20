// Окно-построитель отчёта (Фаза 2): несколько секций-отчётов в ОДНОЙ таблице.
// Каждая секция = свой набор столбцов (Заголовок|Выражение) + свой Источник (слой) +
// свой Фильтр + своя Группировка/Сортировка. Источник/Фильтр/Группировка задаются в
// отдельных всплывающих окнах (кнопки на карточке секции). Секций — без ограничения
// (прокручиваемая стопка). Это нужно, например, для спецификации на стойки И ригеля
// в одной таблице.
//
// Формирует определение отчёта (ReportDef) для движка (action=report):
//   { title, hide_title, scale, sections:[ {section_title, hide_header, header,
//     header_merges, columns, filter, group_by, sort_by}, ... ] }
//
// Подсказка по выражениям (как в построителе СПДС):
//   =Object.«ИМЯ»  -> атрибут; =Object.Name -> имя блока; =Object.«Длина»-150 -> арифм.;
//   =«01 02 04» -> литерал; ="Опора "+Object.«ИМЯ» -> конкатенация;
//   =Count -> кол-во в группе; =row -> нумерация (1-базово, своя в каждой секции).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AtSpecPlugin
{
    public class ReportBuilderForm : Form
    {
        private readonly List<string> _layers, _fields;
        private readonly List<SectionCard> _cards = new List<SectionCard>();
        private TextBox txtTitle;
        private CheckBox chkHideTitle;
        private NumericUpDown nudScale;
        private FlowLayoutPanel flow;

        public Dictionary<string, object> ReportDef { get; private set; }

        public ReportBuilderForm(List<string> layers, List<string> fields)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            BuildUi();
            AddSection(true);   // стартуем с одной секции с дефолтным наполнением
        }

        private void BuildUi()
        {
            Text = "ATableSpec — построитель отчёта";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(740, 660);
            MinimumSize = new Size(700, 480);
            MaximizeBox = true; MinimizeBox = false;

            int x = 12, y = 12, lblW = 90;

            Controls.Add(new Label { Left = x, Top = y + 4, Width = lblW, Text = "Заголовок:" });
            txtTitle = new TextBox { Left = x + lblW, Top = y, Width = 380, Text = "СПЕЦИФИКАЦИЯ ЭЛЕМЕНТОВ",
                                     Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(txtTitle);
            chkHideTitle = new CheckBox { Left = x + lblW + 392, Top = y + 2, Width = 230,
                                          Text = "Скрыть заголовок таблицы", Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Controls.Add(chkHideTitle);
            y += 32;

            Controls.Add(new Label { Left = x, Top = y + 4, Width = 62, Text = "Масштаб:" });
            nudScale = new NumericUpDown { Left = x + 64, Top = y, Width = 90, Minimum = 1, Maximum = 100000,
                                           Value = 100, DecimalPlaces = 0, Increment = 100 };
            Controls.Add(nudScale);
            Controls.Add(new Label { Left = x + 170, Top = y + 4, Width = 540,
                Text = "Несколько секций = несколько спецификаций в одной таблице (стойки, ригеля, …).",
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
            btnAdd.Click += (s, e) => AddSection(false);
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

        private void AddSection(bool seedDefault)
        {
            var card = new SectionCard(_layers, _fields, seedDefault);
            card.MoveUpRequested += MoveCardUp;
            card.MoveDownRequested += MoveCardDown;
            card.RemoveRequested += RemoveCard;
            _cards.Add(card);
            flow.Controls.Add(card);
            Renumber();
            flow.ScrollControlIntoView(card);
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
        }
    }

    // ───────────────────────── карточка одной секции ─────────────────────────
    public class SectionCard : Panel
    {
        private readonly List<string> _layers, _fields;
        private Label lblNum, lblSummary;
        private TextBox txtSecTitle;
        private CheckBox chkHideHeader;
        private DataGridView grid;
        private readonly List<int[]> _merges = new List<int[]>();   // [s,e] 0-базово

        // состояние, задаваемое через всплывающие окна
        private string _layer = "";
        private string _ffield = "", _fop = "=", _fval = "";
        private int _groupIdx = -1;     // 0-базовый индекс столбца группировки (-1 = нет)
        private int _sortMode = 0;      // 0 — по возрастанию, 1 — по убыванию, 2 — без сортировки

        public event Action<SectionCard> MoveUpRequested, MoveDownRequested, RemoveRequested;

        public SectionCard(List<string> layers, List<string> fields, bool seedDefault)
        {
            _layers = layers; _fields = fields;
            BorderStyle = BorderStyle.FixedSingle;
            Width = 690; Height = 272; Margin = new Padding(0, 0, 0, 8);
            BuildUi(seedDefault);
            if (seedDefault && _layers.Count > 0) _layer = _layers[0];
            if (seedDefault) { _groupIdx = 1; _sortMode = 0; }   // группа по столбцу 2 (НАИМЕНОВАНИЕ)
            RefreshSummary();
        }

        private void BuildUi(bool seedDefault)
        {
            int y = 8;
            lblNum = new Label { Left = 8, Top = y + 4, Width = 70, Text = "Отчёт", Font = new Font(Font, FontStyle.Bold) };
            Controls.Add(lblNum);

            var btnSrc = new Button { Left = 84, Top = y, Width = 92, Text = "Источник…" };
            var btnFlt = new Button { Left = 178, Top = y, Width = 82, Text = "Фильтр…" };
            var btnGrp = new Button { Left = 262, Top = y, Width = 112, Text = "Группировка…" };
            btnSrc.Click += (s, e) => OpenSource();
            btnFlt.Click += (s, e) => OpenFilter();
            btnGrp.Click += (s, e) => OpenGroup();
            Controls.Add(btnSrc); Controls.Add(btnFlt); Controls.Add(btnGrp);

            var btnUp = new Button { Left = 588, Top = y, Width = 28, Text = "↑" };
            var btnDn = new Button { Left = 618, Top = y, Width = 28, Text = "↓" };
            var btnDel = new Button { Left = 648, Top = y, Width = 28, Text = "✕" };
            btnUp.Click += (s, e) => { var h = MoveUpRequested; if (h != null) h(this); };
            btnDn.Click += (s, e) => { var h = MoveDownRequested; if (h != null) h(this); };
            btnDel.Click += (s, e) => { var h = RemoveRequested; if (h != null) h(this); };
            Controls.Add(btnUp); Controls.Add(btnDn); Controls.Add(btnDel);
            y += 30;

            Controls.Add(new Label { Left = 8, Top = y + 4, Width = 118, Text = "Заголовок секции:" });
            txtSecTitle = new TextBox { Left = 128, Top = y, Width = 280 };
            if (seedDefault) txtSecTitle.Text = "";
            Controls.Add(txtSecTitle);
            chkHideHeader = new CheckBox { Left = 420, Top = y + 2, Width = 240, Text = "Скрыть шапку столбцов" };
            Controls.Add(chkHideHeader);
            y += 30;

            Controls.Add(new Label { Left = 8, Top = y, Width = 360, Text = "Столбцы (Заголовок | Выражение):" });
            y += 20;
            grid = new DataGridView
            {
                Left = 8, Top = y, Width = 668, Height = 140,
                AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            var colHdr = new DataGridViewTextBoxColumn { Name = "hdr", HeaderText = "Заголовок", Width = 220 };
            var colExpr = new DataGridViewComboBoxColumn
            {
                Name = "expr", HeaderText = "Выражение",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FlatStyle = FlatStyle.Flat, DropDownWidth = 260
            };
            var seed = new List<string> { "=row", "=Count", "=«шт.»", "=Object.Name", "=Object.«ИМЯ»", "=Object.«Длина»" };
            foreach (var f in _fields) seed.Add("=Object.«" + f + "»");
            foreach (var sx in seed) if (!colExpr.Items.Contains(sx)) colExpr.Items.Add(sx);
            grid.Columns.Add(colHdr); grid.Columns.Add(colExpr);
            grid.EditingControlShowing += Grid_EditingControlShowing;
            grid.DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };
            if (seedDefault)
            {
                grid.Rows.Add("№ п/п", "=row");
                grid.Rows.Add("НАИМЕНОВАНИЕ", "=Object.«ИМЯ»");
                grid.Rows.Add("Артикул", "=Object.Name");
                grid.Rows.Add("Длина, мм", "=Object.«Длина»");
                grid.Rows.Add("Колич.", "=Count");
                grid.Rows.Add("Ед. изм.", "=«шт.»");
            }
            var menu = new ContextMenuStrip();
            var miMerge = new ToolStripMenuItem("Объединить шапку выделенных столбцов");
            var miUnmerge = new ToolStripMenuItem("Разъединить шапку");
            miMerge.Click += (s, e) => MergeSelectedHeader();
            miUnmerge.Click += (s, e) => UnmergeSelectedHeader();
            menu.Items.Add(miMerge); menu.Items.Add(miUnmerge);
            grid.ContextMenuStrip = menu;
            Controls.Add(grid);
            y += 146;

            lblSummary = new Label { Left = 8, Top = y, Width = 668,
                Text = "Источник: —    Фильтр: —    Группа: —" };
            Controls.Add(lblSummary);
        }

        public void SetIndex(int n) { lblNum.Text = "Отчёт " + n; }

        // ── всплывающие окна ──
        private void OpenSource()
        {
            using (var dlg = new SourceDialog(_layers, _layer))
                if (dlg.ShowDialog(this) == DialogResult.OK) { _layer = dlg.Layer; RefreshSummary(); }
        }
        private void OpenFilter()
        {
            using (var dlg = new FilterDialog(_fields, _ffield, _fop, _fval))
                if (dlg.ShowDialog(this) == DialogResult.OK)
                { _ffield = dlg.FField; _fop = dlg.FOp; _fval = dlg.FVal; RefreshSummary(); }
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

        private void RefreshSummary()
        {
            string src = string.IsNullOrEmpty(_layer) ? "—" : _layer;
            string flt = (string.IsNullOrEmpty(_ffield) || string.IsNullOrEmpty(_fval)) ? "—" : (_ffield + " " + _fop + " " + _fval);
            string grp = "—";
            if (_groupIdx >= 0)
            {
                var h = CurrentHeaders();
                string col = (_groupIdx < h.Count && h[_groupIdx].Length > 0) ? h[_groupIdx] : ("№" + (_groupIdx + 1));
                string ar = _sortMode == 0 ? " ↑" : (_sortMode == 1 ? " ↓" : "");
                grp = col + ar;
            }
            string mrg = _merges.Count == 0 ? "" : ("    Объед.шапки: " + MergesText());
            lblSummary.Text = "Источник: " + src + "    Фильтр: " + flt + "    Группа: " + grp + mrg;
        }
        private string MergesText()
        {
            var parts = new List<string>();
            foreach (var sp in _merges) parts.Add((sp[0] + 1) + "–" + (sp[1] + 1));
            return string.Join(", ", parts.ToArray());
        }

        // ── редактируемый combo «Выражение» (выбор готовой вставки ИЛИ ручной ввод) ──
        private void Grid_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var cb = e.Control as ComboBox;
            if (cb == null) return;
            if (grid.CurrentCell == null || grid.CurrentCell.OwningColumn == null
                || grid.CurrentCell.OwningColumn.Name != "expr") return;
            cb.DropDownStyle = ComboBoxStyle.DropDown;
            cb.Validating -= Expr_Validating;
            cb.Validating += Expr_Validating;
        }
        private void Expr_Validating(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var cb = (ComboBox)sender;
            string txt = cb.Text;
            var col = grid.Columns["expr"] as DataGridViewComboBoxColumn;
            if (col != null && txt.Length > 0 && !col.Items.Contains(txt)) col.Items.Add(txt);
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
                filters.Add(new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", _layer } });
            if (!string.IsNullOrEmpty(_ffield) && !string.IsNullOrEmpty(_fval))
                filters.Add(new Dictionary<string, object> { { "field", _ffield }, { "op", _fop }, { "value", _fval } });

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
                { "sort_by", sortBy }
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

    // ───────────────────────── всплывающее окно: Фильтр ─────────────────────────
    public class FilterDialog : Form
    {
        private ComboBox cbField, cbOp;
        private TextBox txtVal;
        public string FField { get { return cbField.Text.Trim(); } }
        public string FOp { get { return cbOp.Text; } }
        public string FVal { get { return txtVal.Text.Trim(); } }

        public FilterDialog(List<string> fields, string field, string op, string val)
        {
            Text = "Фильтр секции";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 140);
            MaximizeBox = false; MinimizeBox = false;

            Controls.Add(new Label { Left = 12, Top = 14, Width = 396,
                Text = "Доп. условие к источнику (пустое поле = без фильтра):" });
            cbField = new ComboBox { Left = 12, Top = 40, Width = 170, DropDownStyle = ComboBoxStyle.DropDown };
            if (fields != null) cbField.Items.AddRange(fields.ToArray());
            cbField.Text = field ?? "";
            Controls.Add(cbField);
            cbOp = new ComboBox { Left = 188, Top = 40, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            cbOp.Items.AddRange(new object[] { "=", "≠", ">", "<", "≥", "содержит", "не содержит" });
            cbOp.SelectedItem = string.IsNullOrEmpty(op) ? "=" : op;
            if (cbOp.SelectedIndex < 0) cbOp.SelectedIndex = 0;
            Controls.Add(cbOp);
            txtVal = new TextBox { Left = 284, Top = 40, Width = 124, Text = val ?? "" };
            Controls.Add(txtVal);

            var ok = new Button { Text = "OK", Left = 224, Top = 92, Width = 84, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Отмена", Left = 316, Top = 92, Width = 92, DialogResult = DialogResult.Cancel };
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
}
