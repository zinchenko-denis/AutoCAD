// Редактируемый combo для столбца «Значение» в гриде построителя.
//
// Зачем НЕ DataGridViewComboBoxColumn: combo-колонка хранит ТОЛЬКО значения из Items —
// свободно введённый текст (в т.ч. подстрока для «содержит») отклоняется на коммите и
// ячейка чистится. Это подтверждено рантаймом Алексея (ночной заход) и зафиксировано как
// settled-решение: хранение строкой через TextBox-ячейку.
//
// Но у голой TextBox-ячейки нет кнопки выпадающего списка (автодополнение всплывает только
// при наборе) — Алексей просит кнопку вернуть. Решение: ЯЧЕЙКА наследует
// DataGridViewTextBoxCell (хранит ЛЮБУЮ строку), а РЕДАКТОР — ComboBox со стилем DropDown
// (кнопка списка + свободный ввод). Значение читается из ComboBox.Text, через Items НЕ
// маппится → свободный текст не теряется. Список наполняется per-row в
// ReportBuilderForm.Grid_EditingControlShowing из ValuesFor(слой, поле).

using System;
using System.Windows.Forms;

namespace AtSpecPlugin
{
    // Ячейка хранит строку (как TextBox), но редактируется выпадающим combo.
    internal class ValueComboCell : DataGridViewTextBoxCell
    {
        // базовый InitializeEditingControl null-безопасно кастит к TextBox-редактору и для
        // не-TextBox контрола просто ничего не делает — звать base безопасно.
        public override Type EditType { get { return typeof(ValueComboEditingControl); } }
        public override Type ValueType { get { return typeof(string); } }
        public override object DefaultNewRowValue { get { return string.Empty; } }

        public override void InitializeEditingControl(int rowIndex, object initialFormattedValue,
            DataGridViewCellStyle dataGridViewCellStyle)
        {
            base.InitializeEditingControl(rowIndex, initialFormattedValue, dataGridViewCellStyle);
            var ctl = DataGridView != null ? DataGridView.EditingControl as ValueComboEditingControl : null;
            if (ctl != null)
                ctl.Text = initialFormattedValue == null ? string.Empty : initialFormattedValue.ToString();
        }
    }

    // Редактор: editable ComboBox (кнопка списка + ручной ввод). Значение = Text, НЕ SelectedItem.
    internal class ValueComboEditingControl : ComboBox, IDataGridViewEditingControl
    {
        private DataGridView _dgv;
        private bool _changed;

        public ValueComboEditingControl()
        {
            DropDownStyle = ComboBoxStyle.DropDown;   // редактируемый: и выпадушка, и свободный ввод
            FlatStyle = FlatStyle.Flat;
        }

        // свободный текст коммитим как значение — НЕ через SelectedItem (иначе терялся бы)
        public object EditingControlFormattedValue
        {
            get { return Text; }
            set { Text = value == null ? string.Empty : value.ToString(); }
        }

        public object GetEditingControlFormattedValue(DataGridViewDataErrorContexts context) { return Text; }

        public void ApplyCellStyleToEditingControl(DataGridViewCellStyle s)
        {
            if (s == null) return;
            Font = s.Font;
            BackColor = s.BackColor;
            ForeColor = s.ForeColor;
        }

        public int EditingControlRowIndex { get; set; }

        public bool EditingControlWantsInputKey(Keys key, bool dataGridViewWantsInputKey)
        {
            switch (key & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                case Keys.Delete:
                    return true;   // навигация/правка нужны редактору combo
                default:
                    return !dataGridViewWantsInputKey;
            }
        }

        public void PrepareEditingControlForEdit(bool selectAll) { if (selectAll) SelectAll(); }

        public bool RepositionEditingControlOnValueChange { get { return false; } }

        public DataGridView EditingControlDataGridView { get { return _dgv; } set { _dgv = value; } }

        public bool EditingControlValueChanged { get { return _changed; } set { _changed = value; } }

        public Cursor EditingPanelCursor { get { return base.Cursor; } }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            _changed = true;
            if (_dgv != null) _dgv.NotifyCurrentCellDirty(true);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            // выбор из списка пишет Text = item; это и есть значение ячейки
            _changed = true;
            if (_dgv != null) _dgv.NotifyCurrentCellDirty(true);
        }
    }
}
