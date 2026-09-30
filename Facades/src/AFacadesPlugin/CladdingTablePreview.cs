using System;
using System.Drawing;
using System.Windows.Forms;

namespace AFacadesPlugin
{
    internal sealed class CladdingTablePreview : Form
    {
        private readonly TextBox _note = new TextBox();
        internal string UserNote { get { return _note.Text; } }

        internal CladdingTablePreview(CladdingTableData data, string note)
        {
            Text = "ATFTABLE — проверка ведомости облицовки";
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
            var intro = new Label { Dock = DockStyle.Fill, Text = data.Scope + Environment.NewLine + data.Coverage };
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AllowUserToOrderColumns = false, RowHeadersVisible = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            foreach (var header in CladdingTableData.Headers)
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = header == "Размер, мм / форма" ? 220 : 110 });
            foreach (var row in data.Installed) grid.Rows.Add(Array.ConvertAll(row, CladdingTableData.Display));
            if (data.Cutting.Count > 0)
            {
                grid.Rows.Add("РАСКРОЙ", "вся группа");
                foreach (var row in data.Cutting) grid.Rows.Add(Array.ConvertAll(row, CladdingTableData.Display));
            }
            var messages = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
                ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Control,
                Text = (data.Complete ? "Состав учтён." : "Ведомость неполная. Неопределённости:") + Environment.NewLine +
                    string.Join(Environment.NewLine, data.Information.ToArray()) + Environment.NewLine +
                    string.Join(Environment.NewLine, data.Warnings.ToArray()) + Environment.NewLine +
                    string.Join(Environment.NewLine, data.CuttingDetails.ToArray()) };
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
    }
}
