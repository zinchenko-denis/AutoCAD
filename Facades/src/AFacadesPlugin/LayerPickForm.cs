using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AFacadesPlugin
{
    /// <summary>
    /// ATFTABLE — выбор слоёв со штриховками зон (29.09n, Герман: «так как штриховки находятся в
    /// отдельном слое, добавить выбор слоя из уже существующих слоёв»). В списке — только слои, где
    /// есть штриховки/марки с данными ATFZONE, с числом зон. Можно отметить несколько слоёв: у
    /// ведомости работ площадь — общая, строка утеплителя делится по толщине из имени слоя.
    /// Без AutoCAD — проверяется под mono (Facades/tools/zone_ui).
    /// </summary>
    internal class LayerPickForm : Form
    {
        private readonly CheckedListBox _list = new CheckedListBox();
        private readonly List<string> _names = new List<string>();
        private readonly Button _ok = new Button();

        /// <summary>layers: имя слоя → число зон на нём; checkedByDefault — отмечены при открытии.</summary>
        internal LayerPickForm(IDictionary<string, int> layers, ICollection<string> checkedByDefault, string purpose)
            : this(layers, checkedByDefault, purpose, null, "зон") { }

        internal LayerPickForm(IDictionary<string, int> layers, ICollection<string> checkedByDefault,
            string purpose, string instruction, string countLabel)
        {
            Text = instruction == null ? "ATFTABLE — слои штриховок" : "ATFTABLE — слои " + purpose;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 330);
            Font = new Font("Segoe UI", 9f);

            var lbl = new Label
            {
                Text = instruction ?? ("Отметьте слои со штриховками зон" + (string.IsNullOrEmpty(purpose) ? "" : " — " + purpose) +
                       ". Несколько слоёв: площадь общая, утеплитель — по толщине из имени слоя."),
                Location = new Point(10, 8), Size = new Size(400, 36)
            };
            _list.Location = new Point(10, 48);
            _list.Size = new Size(400, 230);
            _list.CheckOnClick = true;
            var keys = new List<string>(layers.Keys);
            keys.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (var k in keys)
            {
                _names.Add(k);
                int i = _list.Items.Add(k + "   (" + countLabel + ": " + layers[k] + ")");
                if (checkedByDefault != null && checkedByDefault.Contains(k)) _list.SetItemChecked(i, true);
            }
            if (_list.CheckedIndices.Count == 0 && _list.Items.Count == 1) _list.SetItemChecked(0, true);
            _list.ItemCheck += (s, e) => BeginInvoke((Action)Sync);

            _ok.Text = "OK";
            _ok.Location = new Point(244, 290);
            _ok.Size = new Size(80, 28);
            _ok.DialogResult = DialogResult.OK;
            var cancel = new Button { Text = "Отмена", Location = new Point(330, 290), Size = new Size(80, 28),
                                      DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { lbl, _list, _ok, cancel });
            AcceptButton = _ok;
            CancelButton = cancel;
            Sync();
        }

        private void Sync() { _ok.Enabled = _list.CheckedIndices.Count > 0; }

        /// <summary>Отмеченные слои (по порядку списка).</summary>
        internal List<string> Selected
        {
            get
            {
                var r = new List<string>();
                foreach (int i in _list.CheckedIndices) r.Add(_names[i]);
                return r;
            }
        }

        // для проверки без мыши
        internal void CheckLayer(string name, bool on)
        {
            int i = _names.IndexOf(name);
            if (i >= 0) _list.SetItemChecked(i, on);
            Sync();
        }

        internal bool OkEnabled { get { return _ok.Enabled; } }
    }
}
