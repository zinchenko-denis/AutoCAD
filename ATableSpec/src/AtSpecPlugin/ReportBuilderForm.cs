// Окно-построитель "своего отчёта": источник (слой + опц. фильтр), столбцы как
// выражения (Заголовок | Выражение, по образцу редактора СПДС), группировка по
// столбцу и сортировка. Формирует определение отчёта (ReportDef) для движка
// (action=report). Каркас — один шаблон отчёта.
//
// Подсказка по выражениям (как в построителе СПДС):
//   =Object.«ИМЯ»        -> атрибут блока
//   =Object.Name          -> имя блока (часто = профиль)
//   =Object.«Длина»-150   -> арифметика над полем
//   =«01 02 04»           -> литерал (свой артикул)
//   ="Опора "+Object.«ИМЯ» -> конкатенация
//   =Count                -> количество в группе ; =row-1 -> нумерация

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AtSpecPlugin
{
    public class ReportBuilderForm : Form
    {
        private readonly List<string> _layers, _fields;
        private ComboBox cbLayer, cbFilterField, cbFilterOp, cbGroup, cbSort;
        private TextBox txtFilterVal, txtTitle;
        private DataGridView grid;
        private CheckBox chkHideTitle, chkHideHeader;
        private NumericUpDown nudScale;
        private Label lblMerges;
        private readonly List<int[]> _headerMerges = new List<int[]>();   // [startCol,endCol], 0-базово

        public Dictionary<string, object> ReportDef { get; private set; }

        public ReportBuilderForm(List<string> layers, List<string> fields)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "ATableSpec — построитель отчёта";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(660, 560);
            MaximizeBox = false; MinimizeBox = false;

            int x = 12, y = 12, lblW = 110;

            Controls.Add(new Label { Left = x, Top = y + 3, Width = lblW, Text = "Заголовок:" });
            txtTitle = new TextBox { Left = x + lblW, Top = y, Width = 420, Text = "СПЕЦИФИКАЦИЯ ЭЛЕМЕНТОВ" };
            Controls.Add(txtTitle); y += 32;

            Controls.Add(new Label { Left = x, Top = y + 3, Width = lblW, Text = "Источник: слой" });
            cbLayer = new ComboBox { Left = x + lblW, Top = y, Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
            cbLayer.Items.AddRange(_layers.ToArray());
            if (cbLayer.Items.Count > 0) cbLayer.SelectedIndex = 0;
            Controls.Add(cbLayer); y += 32;

            Controls.Add(new Label { Left = x, Top = y + 5, Width = 80, Text = "Фильтр:" });
            cbFilterField = new ComboBox { Left = x + 90, Top = y, Width = 150, DropDownStyle = ComboBoxStyle.DropDown };
            cbFilterField.Items.AddRange(_fields.ToArray());
            Controls.Add(cbFilterField);
            cbFilterOp = new ComboBox { Left = x + 250, Top = y, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            cbFilterOp.Items.AddRange(new object[] { "=", "≠", ">", "<", "≥", "содержит", "не содержит" });
            cbFilterOp.SelectedIndex = 0;
            Controls.Add(cbFilterOp);
            txtFilterVal = new TextBox { Left = x + 350, Top = y, Width = 130 };
            Controls.Add(txtFilterVal); y += 34;

            // (бывшая подсказка «Поля для Object…» убрана — поля и так есть в выпадающем
            //  списке столбца «Выражение».) Здесь — флажки скрытия строк итоговой таблицы.
            chkHideTitle = new CheckBox { Left = x, Top = y + 2, Width = 200, Text = "Скрыть заголовок" };
            chkHideHeader = new CheckBox { Left = x + 215, Top = y + 2, Width = 240, Text = "Скрыть шапку столбцов" };
            Controls.Add(chkHideTitle); Controls.Add(chkHideHeader);
            y += 32;

            Controls.Add(new Label { Left = x, Top = y, Width = 636, Text = "Столбцы (Заголовок | Выражение):" });
            y += 22;
            grid = new DataGridView
            {
                Left = x, Top = y, Width = 636, Height = 240,
                AllowUserToAddRows = true, AllowUserToDeleteRows = true,
                RowHeadersVisible = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            var colHdr = new DataGridViewTextBoxColumn { Name = "hdr", HeaderText = "Заголовок", Width = 200 };
            var colExpr = new DataGridViewComboBoxColumn
            {
                Name = "expr",
                HeaderText = "Выражение",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FlatStyle = FlatStyle.Flat,
                DropDownWidth = 260
            };
            // выпадающие варианты = служебные выражения + поля как готовые вставки Object.«…».
            // Список редактируемый: ручной ввод сохранён (см. Grid_EditingControlShowing).
            var seed = new List<string> { "=row", "=Count", "=«шт.»",
                                          "=Object.Name", "=Object.«ИМЯ»", "=Object.«Длина»" };
            foreach (var f in _fields) seed.Add("=Object.«" + f + "»");
            foreach (var sx in seed) if (!colExpr.Items.Contains(sx)) colExpr.Items.Add(sx);
            grid.Columns.Add(colHdr);
            grid.Columns.Add(colExpr);
            grid.EditingControlShowing += Grid_EditingControlShowing;
            grid.DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };
            // дефолт = базовая спецификация (как на скриншотах СПДС)
            grid.Rows.Add("№ п/п", "=row");
            grid.Rows.Add("НАИМЕНОВАНИЕ", "=Object.«ИМЯ»");
            grid.Rows.Add("Артикул", "=Object.Name");
            grid.Rows.Add("Длина, мм", "=Object.«Длина»");
            grid.Rows.Add("Колич.", "=Count");
            grid.Rows.Add("Ед. изм.", "=«шт.»");
            // #5: контекстное меню грида — объединение/разъединение ШАПКИ выделенных столбцов
            var menu = new ContextMenuStrip();
            var miMerge = new ToolStripMenuItem("Объединить шапку выделенных столбцов");
            var miUnmerge = new ToolStripMenuItem("Разъединить шапку");
            miMerge.Click += (s, e) => MergeSelectedHeader();
            miUnmerge.Click += (s, e) => UnmergeSelectedHeader();
            menu.Items.Add(miMerge); menu.Items.Add(miUnmerge);
            grid.ContextMenuStrip = menu;
            Controls.Add(grid); y += 250;

            lblMerges = new Label { Left = x, Top = y, Width = 636,
                Text = "Объединение шапки: (нет)  —  выделите столбцы (строки слева) и ПКМ" };
            Controls.Add(lblMerges); y += 24;

            Controls.Add(new Label { Left = x, Top = y + 3, Width = 120, Text = "Группа по столбцу №:" });
            cbGroup = new ComboBox { Left = x + 125, Top = y, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            cbGroup.Items.Add("(нет)");
            for (int i = 1; i <= 12; i++) cbGroup.Items.Add(i.ToString());
            cbGroup.SelectedIndex = 2;  // столбец 2 (НАИМЕНОВАНИЕ) -> 0-базовый индекс 1
            Controls.Add(cbGroup);
            Controls.Add(new Label { Left = x + 230, Top = y + 3, Width = 60, Text = "Сорт.:" });
            cbSort = new ComboBox { Left = x + 290, Top = y, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
            cbSort.Items.AddRange(new object[] { "по возрастанию", "по убыванию", "(без сортировки)" });
            cbSort.SelectedIndex = 0;
            Controls.Add(cbSort);
            Controls.Add(new Label { Left = x + 425, Top = y + 3, Width = 62, Text = "Масштаб:" });
            nudScale = new NumericUpDown { Left = x + 490, Top = y, Width = 100, Minimum = 1, Maximum = 100000, Value = 1000, DecimalPlaces = 0, Increment = 100 };
            Controls.Add(nudScale);
            y += 42;

            var ok = new Button { Text = "Построить", Left = 460, Top = y, Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Отмена", Left = 560, Top = y, Width = 90, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => BuildDef();
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        // Делает выпадающий список в ячейке «Выражение» редактируемым: можно выбрать
        // готовую вставку Object.«…» ИЛИ дописать/исправить выражение руками.
        private void Grid_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var cb = e.Control as ComboBox;
            if (cb == null) return;
            if (grid.CurrentCell == null || grid.CurrentCell.OwningColumn == null
                || grid.CurrentCell.OwningColumn.Name != "expr") return;
            cb.DropDownStyle = ComboBoxStyle.DropDown;     // разрешить ручной ввод
            cb.Validating -= Expr_Validating;
            cb.Validating += Expr_Validating;
        }

        // Введённое вручную выражение делаем валидным для combo-ячейки (иначе оно не
        // сохранится): добавляем его в список вариантов, если такого ещё нет.
        private void Expr_Validating(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var cb = (ComboBox)sender;
            string txt = cb.Text;
            var col = grid.Columns["expr"] as DataGridViewComboBoxColumn;
            if (col != null && txt.Length > 0 && !col.Items.Contains(txt))
                col.Items.Add(txt);
        }

        private void BuildDef()
        {
            var headers = new List<object>();
            var columns = new List<object>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string h = Convert.ToString(r.Cells[0].Value) ?? "";
                string e = Convert.ToString(r.Cells[1].Value) ?? "";
                if (h.Length == 0 && e.Length == 0) continue;
                headers.Add(h);
                columns.Add(e);
            }

            var filters = new List<object>();
            string layer = cbLayer.Text;
            if (!string.IsNullOrEmpty(layer))
                filters.Add(new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", layer } });
            if (cbFilterField.Text.Trim().Length > 0 && txtFilterVal.Text.Trim().Length > 0)
                filters.Add(new Dictionary<string, object>
                {
                    { "field", cbFilterField.Text.Trim() }, { "op", cbFilterOp.Text }, { "value", txtFilterVal.Text.Trim() }
                });

            object groupBy = null;
            if (cbGroup.SelectedIndex > 0) groupBy = cbGroup.SelectedIndex - 1;  // 0-базовый индекс столбца

            object sortBy = null;
            if (cbSort.SelectedIndex != 2 && groupBy is int)
                sortBy = new object[] { (int)groupBy, cbSort.SelectedIndex == 0 ? "asc" : "desc" };

            var template = new Dictionary<string, object>
            {
                { "filter", filters },
                { "columns", columns },
                { "group_by", groupBy },
                { "sort_by", sortBy }
            };
            var merges = new List<object>();
            foreach (var sp in _headerMerges) merges.Add(new List<object> { sp[0], sp[1] });
            ReportDef = new Dictionary<string, object>
            {
                { "title", txtTitle.Text },
                { "header", headers },
                { "templates", new List<object> { template } },
                { "hide_title", chkHideTitle.Checked },
                { "hide_header", chkHideHeader.Checked },
                { "scale", (double)nudScale.Value },
                { "header_merges", merges }
            };
        }

        // ── #5: объединение ШАПКИ столбцов (Вариант А): только строка-шапка, данные раздельны ──
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
            int s = cols[0], e = cols[cols.Count - 1];                // диапазон по краям выделения
            _headerMerges.RemoveAll(sp => !(sp[1] < s || sp[0] > e));  // убрать пересекающиеся
            _headerMerges.Add(new[] { s, e });
            _headerMerges.Sort((a, b) => a[0].CompareTo(b[0]));
            UpdateMergesLabel();
        }

        private void UnmergeSelectedHeader()
        {
            var cols = SelectedOutputColumns();
            if (cols.Count == 0) return;
            int s = cols[0], e = cols[cols.Count - 1];
            int removed = _headerMerges.RemoveAll(sp => !(sp[1] < s || sp[0] > e));
            if (removed == 0)
                MessageBox.Show("В выделении нет объединённой шапки.",
                    "Разъединение шапки", MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateMergesLabel();
        }

        private void UpdateMergesLabel()
        {
            if (lblMerges == null) return;
            if (_headerMerges.Count == 0)
            {
                lblMerges.Text = "Объединение шапки: (нет)  —  выделите столбцы (строки слева) и ПКМ";
                return;
            }
            var parts = new List<string>();
            foreach (var sp in _headerMerges) parts.Add((sp[0] + 1) + "–" + (sp[1] + 1)); // 1-базово для глаза
            lblMerges.Text = "Объединение шапки: столбцы " + string.Join(", ", parts.ToArray());
        }
    }
}
