using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AFramePlugin
{
    /// <summary>
    /// Параметры команды размеров (фидбэк Германа 04.08): «хотелось бы
    /// иметь возможность выбрать слой из уже созданных путём выбора из
    /// раскрывающегося меню». В командной строке AutoCAD списка слоёв
    /// не сделать (кейворды не терпят пробелов и кириллицы вперемешку),
    /// поэтому маленькая форма: что образмерить + слой (список
    /// существующих со свободным вводом — можно и новый).
    /// Форма собрана кодом, без дизайнера: в бандле один DLL и никаких
    /// .resx (конвенция репо).
    /// </summary>
    public class DimForm : Form
    {
        private readonly ComboBox _kind = new ComboBox();
        private readonly ComboBox _layer = new ComboBox();

        /// <summary>true — ряд (по горизонтали), false — столбец.</summary>
        public bool ByRow { get { return _kind.SelectedIndex != 1; } }

        /// <summary>Выбранный или введённый слой.</summary>
        public string LayerName
        {
            get { return (_layer.Text ?? "").Trim(); }
        }

        /// <summary>Для подсистемы привычнее столбец — ставим его
        /// первым выбором (список пунктов не меняем).</summary>
        public void SelectColumnFirst()
        {
            _kind.SelectedIndex = 1;
        }

        public DimForm(string title, IEnumerable<string> layers,
                       string defLayer)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(330, 130);

            var l1 = new Label
            {
                Text = "Что образмерить:",
                Left = 12, Top = 15, Width = 120, AutoSize = false
            };
            _kind.DropDownStyle = ComboBoxStyle.DropDownList;
            _kind.Left = 140; _kind.Top = 12; _kind.Width = 175;
            _kind.Items.AddRange(new object[] { "Ряд", "Столбец" });
            _kind.SelectedIndex = 0;

            var l2 = new Label
            {
                Text = "Слой размеров:",
                Left = 12, Top = 48, Width = 120, AutoSize = false
            };
            // DropDown (не List): список существующих + свободный ввод,
            // чтобы можно было завести новый слой прямо здесь
            _layer.DropDownStyle = ComboBoxStyle.DropDown;
            _layer.Left = 140; _layer.Top = 45; _layer.Width = 175;
            var seen = new List<string>();
            foreach (var s in layers)
            {
                if (string.IsNullOrEmpty(s) || seen.Contains(s)) continue;
                seen.Add(s);
                _layer.Items.Add(s);
            }
            _layer.Text = defLayer;

            var ok = new Button
            {
                Text = "OK", Left = 140, Top = 88, Width = 85,
                DialogResult = DialogResult.OK
            };
            var cancel = new Button
            {
                Text = "Отмена", Left = 230, Top = 88, Width = 85,
                DialogResult = DialogResult.Cancel
            };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[]
                { l1, _kind, l2, _layer, ok, cancel });
        }
    }
}
