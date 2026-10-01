using System;
using System.Drawing;
using System.Windows.Forms;

namespace AFacadesPlugin
{
    internal class QuantityTablePreview : Form
    {
        private readonly TextBox _note = new TextBox();
        private bool _constrainingWindow;
        internal string UserNote { get { return _note.Text; } }

        internal QuantityTablePreview(QuantityTableView data, string note, bool allowContinue = true)
        {
            Text = "ATFTABLE — " + data.Title;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(880, 620);
            Size = new Size(1180, 760);
            Font = new Font("Segoe UI", 9f);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
                Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var intro = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                Text = data.Scope + Environment.NewLine + data.Coverage };
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AllowUserToOrderColumns = false, RowHeadersVisible = false,
                VirtualMode = true, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, ShowCellToolTips = true };
            grid.RowTemplate.Height = 24;
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            for (int col = 0; col < data.Headers.Length; col++)
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = data.Headers[col], Width = (int)(data.Widths[col] * 10),
                    SortMode = DataGridViewColumnSortMode.NotSortable });
            // Keep one source list; WinForms requests only visible cell values.
            // A large list of unique profile lengths must not create every cell eagerly.
            grid.CellValueNeeded += (sender, args) => args.Value = CellText(data, args.RowIndex, args.ColumnIndex);
            grid.CellToolTipTextNeeded += (sender, args) => args.ToolTipText = CellText(data, args.RowIndex, args.ColumnIndex);
            grid.RowCount = data.PreviewRows.Count;
            Control resultControl = grid;
            if (data.UnaccountedRows.Count > 0)
            {
                var tabs = new TabControl { Dock = DockStyle.Fill };
                if (allowContinue)
                {
                    var quantities = new TabPage("Учтённые позиции"); quantities.Controls.Add(grid); tabs.TabPages.Add(quantities);
                }
                var unknown = new TabPage("Не учтено объектов: " + data.UnaccountedRows.Count);
                var inventory = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true,
                    AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                    AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    ShowCellToolTips = true };
                inventory.RowTemplate.Height = 24;
                int[] widths = { 50, 95, 190, 190, 550 };
                for (int col = 0; col < QuantityTableView.UnaccountedHeaders.Length; col++)
                    inventory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = QuantityTableView.UnaccountedHeaders[col],
                        Width = widths[col], SortMode = DataGridViewColumnSortMode.NotSortable });
                inventory.CellValueNeeded += (sender, args) => args.Value = InventoryCell(data, args.RowIndex, args.ColumnIndex);
                inventory.CellToolTipTextNeeded += (sender, args) => args.ToolTipText = InventoryCell(data, args.RowIndex, args.ColumnIndex);
                inventory.RowCount = data.UnaccountedRows.Count;
                unknown.Controls.Add(inventory); tabs.TabPages.Add(unknown); resultControl = tabs;
            }
            var messages = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
                ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Control,
                Text = (data.Complete ? "Состав учтён." : "Ведомость неполная. Неопределённости:") + Environment.NewLine +
                    string.Join(Environment.NewLine, data.Messages.ToArray()) };
            _note.Dock = DockStyle.Fill; _note.Multiline = true; _note.ScrollBars = ScrollBars.Vertical;
            _note.MaxLength = 2000; _note.Text = note ?? "";
            _note.Enabled = allowContinue;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = allowContinue ? "Отмена" : "Закрыть", DialogResult = DialogResult.Cancel, AutoSize = true };
            var accept = new Button { Text = "Продолжить", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(cancel); if (allowContinue) buttons.Controls.Add(accept);
            layout.Controls.Add(intro); layout.Controls.Add(resultControl); layout.Controls.Add(messages);
            layout.Controls.Add(new Label { Text = "Редактируйте примечание здесь. Ручные правки чисел в DWG/Excel не меняют исходные данные:", Dock = DockStyle.Fill });
            layout.Controls.Add(_note); layout.Controls.Add(buttons);
            Controls.Add(layout); AcceptButton = allowContinue ? accept : cancel; CancelButton = cancel;
        }

        // The requested 1180 x 760 window can exceed a small monitor: Windows
        // does not keep the footer on-screen automatically. Apply the same native
        // size boundary as facade parameter dialogs, before WinForms caches its
        // client dimensions, so layout and the actual HWND remain consistent.
        protected override void SetClientSizeCore(int width, int height)
        {
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            FitMinimum(work.Size);
            Size border = SizeFromClientSize(Size.Empty);
            int minWidth = Math.Max(1, MinimumSize.Width - border.Width),
                minHeight = Math.Max(1, MinimumSize.Height - border.Height);
            base.SetClientSizeCore(Math.Max(minWidth, Math.Min(width, Math.Max(1, work.Width - border.Width))),
                Math.Max(minHeight, Math.Min(height, Math.Max(1, work.Height - border.Height))));
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (_constrainingWindow) { base.SetBoundsCore(x, y, width, height, specified); return; }
            Rectangle work = Screen.FromPoint(new Point(x, y)).WorkingArea;
            FitMinimum(work.Size);
            width = Math.Min(Math.Max(width, MinimumSize.Width), work.Width);
            height = Math.Min(Math.Max(height, MinimumSize.Height), work.Height);
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            base.SetBoundsCore(x, y, width, height, specified);
        }

        private void FitMinimum(Size available)
        {
            if (_constrainingWindow) return;
            Size minimum = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
            if (minimum == MinimumSize) return;
            _constrainingWindow = true;
            try { MinimumSize = minimum; }
            finally { _constrainingWindow = false; }
        }

        private static string CellText(QuantityTableView data, int rowIndex, int columnIndex)
        {
            if (rowIndex < 0 || rowIndex >= data.PreviewRows.Count || columnIndex < 0) return "";
            var row = data.PreviewRows[rowIndex];
            return columnIndex < row.Length ? CladdingTableData.Display(row[columnIndex]) : "";
        }
        private static string InventoryCell(QuantityTableView data, int row, int col)
        { return row >= 0 && row < data.UnaccountedRows.Count && col >= 0 && col < data.UnaccountedRows[row].Length ?
            CladdingTableData.Display(data.UnaccountedRows[row][col]) : ""; }
    }
}
