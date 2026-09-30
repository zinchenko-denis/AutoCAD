using System;
using System.Drawing;
using System.Windows.Forms;

namespace AFacadesPlugin
{
    internal class QuantityTablePreview : Form
    {
        private readonly TextBox _note = new TextBox();
        internal string UserNote { get { return _note.Text; } }

        internal QuantityTablePreview(QuantityTableView data, string note)
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
            var messages = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
                ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Control,
                Text = (data.Complete ? "Состав учтён." : "Ведомость неполная. Неопределённости:") + Environment.NewLine +
                    string.Join(Environment.NewLine, data.Messages.ToArray()) };
            _note.Dock = DockStyle.Fill; _note.Multiline = true; _note.ScrollBars = ScrollBars.Vertical;
            _note.MaxLength = 2000; _note.Text = note ?? "";
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
            var accept = new Button { Text = "Продолжить", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(cancel); buttons.Controls.Add(accept);
            layout.Controls.Add(intro); layout.Controls.Add(grid); layout.Controls.Add(messages);
            layout.Controls.Add(new Label { Text = "Редактируйте примечание здесь. Ручные правки чисел в DWG/Excel не меняют исходные данные:", Dock = DockStyle.Fill });
            layout.Controls.Add(_note); layout.Controls.Add(buttons);
            Controls.Add(layout); AcceptButton = accept; CancelButton = cancel;
        }

        private static string CellText(QuantityTableView data, int rowIndex, int columnIndex)
        {
            if (rowIndex < 0 || rowIndex >= data.PreviewRows.Count || columnIndex < 0) return "";
            var row = data.PreviewRows[rowIndex];
            return columnIndex < row.Length ? CladdingTableData.Display(row[columnIndex]) : "";
        }
    }
}
