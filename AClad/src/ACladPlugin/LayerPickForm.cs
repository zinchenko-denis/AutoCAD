using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ACladPlugin
{
    /// <summary>
    /// Выбор/ввод имени слоя (просьба Германа 07.08: «везде, где
    /// программа просит указать слой, — возможность выбора из списка
    /// существующих»). Комбо DropDown: список слоёв чертежа +
    /// свободный ввод нового имени. Форма собрана кодом, без
    /// дизайнера и .resx (конвенция репо).
    /// </summary>
    public class LayerPickForm : Form
    {
        private readonly ComboBox _layer = new ComboBox();

        /// <summary>Выбранное или введённое имя.</summary>
        public string LayerName
        {
            get { return (_layer.Text ?? "").Trim(); }
        }

        public LayerPickForm(string title, string prompt,
                             IEnumerable<string> layers, string def)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(360, 108);

            var l1 = new Label
            {
                Text = prompt,
                Left = 12, Top = 12, Width = 336, AutoSize = false
            };
            _layer.DropDownStyle = ComboBoxStyle.DropDown;
            _layer.Left = 12; _layer.Top = 36; _layer.Width = 336;
            var seen = new List<string>();
            foreach (var s in layers)
            {
                if (string.IsNullOrEmpty(s) || seen.Contains(s))
                    continue;
                seen.Add(s);
                _layer.Items.Add(s);
            }
            _layer.Text = def ?? "";

            var ok = new Button
            {
                Text = "OK", Left = 172, Top = 70, Width = 85,
                DialogResult = DialogResult.OK
            };
            var cancel = new Button
            {
                Text = "Отмена", Left = 263, Top = 70, Width = 85,
                DialogResult = DialogResult.Cancel
            };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { l1, _layer, ok, cancel });
        }
    }
}
