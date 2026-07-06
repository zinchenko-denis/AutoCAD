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
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Autodesk.AutoCAD.EditorInput;
using AcDb = Autodesk.AutoCAD.DatabaseServices;   // алиас: без блочного using (в нём свои Font/FlowDirection)
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AtSpecPlugin
{
    public class ReportBuilderForm : Form
    {
        private readonly List<string> _layers, _fields, _textStyles;
        // карта значений: слой -> поле -> отсортированные уникальные значения; ключ "" = все блоки.
        private readonly Dictionary<string, Dictionary<string, List<string>>> _valuesByLayer;
        private readonly List<SectionCard> _cards = new List<SectionCard>();
        private ComboBox cbTitle;          // «Заголовок» = редактируемый combo (выпадушка = шаблоны)
        private CheckBox chkHideTitle;
        private NumericUpDown nudScale;
        private ComboBox cmbFont;          // «Шрифт» = текстстиль чертежа (единый стиль ячеек)
        private FlowLayoutPanel flow;

        // подписи выпадушки заголовка -> номер шаблона (0 Ручное, 1 Спец, 2 Раскрой, 3 Заполнения)
        private static readonly string[] TplLabels = { "Спецификация", "Раскрой", "Заполнения", "Ручное (пусто)" };
        private static readonly int[] TplOrder = { 1, 2, 3, 0 };
        private const string NoFontLabel = "(по стилю таблицы)";   // дефолт «Шрифта» — не переопределять стиль

        private int _template;
        private bool _suppressTpl;
        private string _lastTitle = "";
        public Dictionary<string, object> ReportDef { get; private set; }

        // template: 0 — Ручное (пусто), 1 — Спецификация, 2 — Раскрой, 3 — Заполнения.
        public ReportBuilderForm(List<string> layers, List<string> fields,
                                 Dictionary<string, Dictionary<string, List<string>>> valuesByLayer = null,
                                 List<string> textStyles = null,
                                 int template = 1)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            _valuesByLayer = valuesByLayer;
            _textStyles = textStyles ?? new List<string>();
            _template = (template >= 0 && template <= 3) ? template : 1;
            BuildUi();
            _suppressTpl = true;
            cbTitle.Text = DefaultTitleFor(_template);
            _suppressTpl = false;
            chkHideTitle.Checked = (_template == 2);   // раскрой — по умолчанию скрыть заголовок
            _lastTitle = cbTitle.Text;
            AddSection(PresetFor(_template, true));    // стартовая секция, засеяна под шаблон
        }

        // Конструктор реверса (ATSPECEDIT): форма заполняется из готового определения —
        //  заголовок/скрытие/масштаб/шрифт + секции из набора seed'ов (FullRows).
        public ReportBuilderForm(List<string> layers, List<string> fields,
                                 Dictionary<string, Dictionary<string, List<string>>> valuesByLayer,
                                 List<string> textStyles,
                                 string title, bool hideTitle, double scale, string font,
                                 List<SectionSeed> sectionSeeds)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            _valuesByLayer = valuesByLayer;
            _textStyles = textStyles ?? new List<string>();
            _template = 1;
            BuildUi();
            _suppressTpl = true; cbTitle.Text = title ?? ""; _suppressTpl = false;
            chkHideTitle.Checked = hideTitle;
            if (scale >= 1) nudScale.Value = (decimal)Math.Min(scale, 100000);   // def перекрывает дефолт реестра
            if (!string.IsNullOrEmpty(font))
            {
                int fi = cmbFont.Items.IndexOf(font);
                if (fi >= 0) cmbFont.SelectedIndex = fi;     // иначе остаётся «(по стилю таблицы)»
            }
            _lastTitle = cbTitle.Text;
            if (sectionSeeds != null && sectionSeeds.Count > 0)
                foreach (var s in sectionSeeds) AddSection(s);
            else
                AddSection(PresetFor(1, true));              // пустую форму не оставляем
        }

        // def (десериализованный объект) -> готовая форма для правки на месте.
        public static ReportBuilderForm FromDef(object def,
            List<string> layers, List<string> fields,
            Dictionary<string, Dictionary<string, List<string>>> valuesByLayer, List<string> textStyles)
        {
            var d = def as Dictionary<string, object>;
            string title = AsStr(d, "title");
            bool hideTitle = AsBool(d, "hide_title");
            double scale = AsDouble(d, "scale", 1.0);
            string font = AsStr(d, "font");
            var seeds = new List<SectionSeed>();
            var secs = Val(d, "sections") as object[];
            if (secs != null)
                foreach (var so in secs)
                {
                    var seed = SeedFromSection(so as Dictionary<string, object>);
                    if (seed != null) seeds.Add(seed);
                }
            return new ReportBuilderForm(layers, fields, valuesByLayer, textStyles,
                                         title, hideTitle, scale, font, seeds);
        }

        // одна секция def -> seed (полный набор строк грида). Инверсия SectionCard.ToDef.
        private static SectionSeed SeedFromSection(Dictionary<string, object> sec)
        {
            if (sec == null) return null;
            var seed = new SectionSeed
            {
                FullRows = new List<string[]>(),
                SeedMerges = new List<int[]>(),
                SectionTitle = AsStr(sec, "section_title"),
                HideHeader = AsBool(sec, "hide_header"),
                TotalRow = AsBool(sec, "total_row")
            };
            var headers = ToStrListLocal(Val(sec, "header"));
            var columns = ToStrListLocal(Val(sec, "columns"));
            int colCount = Math.Max(headers.Count, columns.Count);

            // (вариант Б) фикс. ширины столбцов, мм — параллельно output-столбцам def;
            // строки-фильтры добавляются НИЖЕ строк-столбцов, выравнивание по индексу живёт.
            var cmArr = Val(sec, "col_mm") as object[];
            if (cmArr != null && cmArr.Length > 0)
            {
                seed.ColMm = new List<double>();
                foreach (var v in cmArr)
                {
                    double d2;
                    try { d2 = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
                    catch { d2 = -1; }
                    seed.ColMm.Add(d2);
                }
            }

            // фильтры: первый «Слой = X» -> источник секции; остальные -> условия
            var conds = new List<string[]>();   // {field, op, value}
            var filt = Val(sec, "filter") as object[];
            if (filt != null)
                foreach (var fo in filt)
                {
                    var fd = fo as Dictionary<string, object>;
                    if (fd == null) continue;
                    string field = AsStr(fd, "field"), op = AsStr(fd, "op"), value = AsStr(fd, "value");
                    if (seed.SeedLayer == null && op == "=" &&
                        string.Equals(field, "Слой", StringComparison.OrdinalIgnoreCase))
                        seed.SeedLayer = value;
                    else
                        conds.Add(new[] { field, op, value });
                }

            // строки-столбцы (output): индексы 0..colCount-1 = output-координаты (как в ToDef)
            for (int i = 0; i < colCount; i++)
            {
                string h  = i < headers.Count ? headers[i] : "";
                string ex = i < columns.Count ? columns[i] : "";
                seed.FullRows.Add(new[] { h, ex, "", "", "" });
            }

            // группа/сортировка: group_by (output idx) -> метка «Группа» строки
            int gby = AsInt(Val(sec, "group_by"), -1);
            if (gby >= 0 && gby < seed.FullRows.Count)
            {
                string label = "без сортировки";              // group_by есть, sort_by нет
                var sb = Val(sec, "sort_by") as object[];      // [idx, "asc"|"desc"]
                if (sb != null && sb.Length >= 2)
                    label = (Convert.ToString(sb[1]) == "desc") ? "по убыванию" : "по возрастанию";
                seed.FullRows[gby][4] = label;
            }

            // условия -> на строку-столбец с тем же полем (ExtractField), иначе — отдельная строка-фильтр
            foreach (var c in conds)
            {
                string field = c[0], op = c[1], value = c[2];
                bool placed = false;
                for (int i = 0; i < colCount; i++)
                    if (seed.FullRows[i][2].Length == 0 &&
                        string.Equals(SectionCard.ExtractField(seed.FullRows[i][1]) ?? "", field, StringComparison.OrdinalIgnoreCase))
                    { seed.FullRows[i][2] = op; seed.FullRows[i][3] = value; placed = true; break; }
                if (!placed)
                    seed.FullRows.Add(new[] { "", "=Object.«" + field + "»", op, value, "" });
            }

            // объединения шапки: header_merges в output-координатах == индексы строк-столбцов
            var hm = Val(sec, "header_merges") as object[];
            if (hm != null)
                foreach (var mo in hm)
                {
                    var ml = mo as object[];
                    if (ml != null && ml.Length >= 2)
                    {
                        int s = AsInt(ml[0], -1), e = AsInt(ml[1], -1);
                        if (s >= 0 && e >= s && e < colCount) seed.SeedMerges.Add(new[] { s, e });
                    }
                }
            return seed;
        }

        // --- мелкие хелперы разбора десериализованного def (JavaScriptSerializer: массив=object[]) ---
        private static object Val(Dictionary<string, object> d, string k)
        { object v; return (d != null && d.TryGetValue(k, out v)) ? v : null; }
        private static string AsStr(Dictionary<string, object> d, string k)
        { var v = Val(d, k); return v == null ? "" : Convert.ToString(v); }
        private static bool AsBool(Dictionary<string, object> d, string k)
        { var v = Val(d, k); try { return v != null && Convert.ToBoolean(v); } catch { return false; } }
        private static double AsDouble(Dictionary<string, object> d, string k, double def)
        { var v = Val(d, k); if (v == null) return def; try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return def; } }
        private static int AsInt(object v, int def)
        { if (v == null) return def; try { return Convert.ToInt32(v); } catch { return def; } }
        private static List<string> ToStrListLocal(object o)
        { var r = new List<string>(); var a = o as object[]; if (a != null) foreach (var x in a) r.Add(x == null ? "" : Convert.ToString(x)); return r; }

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

            // (Алексей) подсказку про шаблон убрали (перегружала интерфейс). Вместо неё — «Шрифт»:
            //  единый текстстиль чертежа на все ячейки таблицы (единый стиль документации).
            Controls.Add(new Label { Left = x + 162, Top = y + 4, Width = 52, Text = "Шрифт:" });
            cmbFont = new ComboBox
            {
                Left = x + 216, Top = y, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            cmbFont.Items.Add(NoFontLabel);                       // дефолт: не переопределять стиль таблицы
            foreach (var ts in _textStyles)
                if (!string.IsNullOrEmpty(ts)) cmbFont.Items.Add(ts);
            cmbFont.SelectedIndex = 0;
            Controls.Add(cmbFont);
            try   // последний выбранный шрифт — дефолт для новых таблиц (зеркало «Масштаба»)
            {
                using (var rk = Registry.CurrentUser.OpenSubKey(@"Software\ATableSpec"))
                {
                    object fv = rk == null ? null : rk.GetValue("Font");
                    string fs = fv == null ? "" : Convert.ToString(fv);
                    if (!string.IsNullOrEmpty(fs))
                    {
                        int idx = cmbFont.Items.IndexOf(fs);
                        if (idx >= 0) cmbFont.SelectedIndex = idx;
                    }
                }
            }
            catch { }
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
            chkHideTitle.Checked = (tpl == 2);   // раскрой — по умолчанию скрыть заголовок таблицы
            string deftitle = DefaultTitleFor(tpl);
            _suppressTpl = true; cbTitle.SelectedIndex = -1; _suppressTpl = false;
            // (Алексей) верхний заголовок обнулялся при выборе шаблона: редактируемый combo сам
            //  переписывает текст поля под выбранный пункт ПОСЛЕ закрытия выпадушки, затирая наш
            //  заголовок. Ставим его ОТЛОЖЕННО — после того как combo доработает выбор.
            BeginInvoke((Action)(() =>
            {
                _suppressTpl = true; cbTitle.Text = deftitle; _lastTitle = deftitle; _suppressTpl = false;
            }));
            AddSection(PresetFor(tpl, true));
        }

        private SectionCard NewCard(SectionSeed seed)
        {
            var card = new SectionCard(_layers, _fields, _valuesByLayer, seed);
            card.MoveUpRequested += MoveCardUp;
            card.MoveDownRequested += MoveCardDown;
            card.RemoveRequested += RemoveCard;
            card.CopyRequested += CopyCard;
            return card;
        }

        private void AddSection(SectionSeed seed)
        {
            var card = NewCard(seed);
            _cards.Add(card);
            flow.Controls.Add(card);
            Renumber();
            flow.ScrollControlIntoView(card);
        }

        // Копирование отчёта (фидбэк Алексея): дублирует секцию и вставляет в выбранную позицию
        // (выпадушка «перед отчётом N» / «в конец»); последующие отчёты перенумеровываются.
        // Клон строится через JSON-round-trip ToDef -> SeedFromSection — тот же проверенный путь,
        // что и реверс готовой таблицы (ATSPECEDIT), чтобы скопировалось ВСЁ: источник, столбцы,
        // фильтр, группа/сортировка, объединённая шапка.
        private void CopyCard(SectionCard card)
        {
            int srcIdx = _cards.IndexOf(card);
            if (srcIdx < 0) return;
            int pos = AskInsertPosition(srcIdx);
            if (pos < 0) return;                       // отмена

            // ВАЖНО: DeserializeObject (массивы -> object[]) — как в рабочем пути ATSPECEDIT.
            // Deserialize<Dictionary> отдаёт вложенные массивы иным типом, и SeedFromSection
            // (ToStrListLocal/«filter as object[]») их не разбирает -> пустая копия.
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var parsed = ser.DeserializeObject(ser.Serialize(card.ToDef())) as Dictionary<string, object>;
            if (parsed == null) return;
            var seed = SeedFromSection(parsed);
            if (seed == null) return;

            var nc = NewCard(seed);
            if (pos > _cards.Count) pos = _cards.Count;
            _cards.Insert(pos, nc);
            flow.Controls.Add(nc);
            SyncFlowOrder();
            Renumber();
            flow.ScrollControlIntoView(nc);
        }

        // Диалог позиции вставки копии. Индекс вставки 0..N (N = «в конец») или -1 при отмене.
        // По умолчанию — сразу после исходного отчёта.
        private int AskInsertPosition(int srcIdx)
        {
            int n = _cards.Count;
            using (var dlg = new Form())
            {
                dlg.Text = "Копировать отчёт";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false; dlg.MaximizeBox = false; dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(300, 112);

                var lbl = new Label { Left = 12, Top = 14, Width = 276, Text = "Вставить копию перед отчётом:" };
                var cmb = new ComboBox { Left = 12, Top = 38, Width = 276, DropDownStyle = ComboBoxStyle.DropDownList };
                for (int k = 1; k <= n; k++) cmb.Items.Add("отчёт " + k);
                cmb.Items.Add("в конец");
                int def = srcIdx + 1; if (def < 0) def = n; if (def > n) def = n;
                cmb.SelectedIndex = def;

                var ok = new Button { Text = "OK", Left = 132, Top = 76, Width = 75, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Отмена", Left = 213, Top = 76, Width = 75, DialogResult = DialogResult.Cancel };
                dlg.Controls.Add(lbl); dlg.Controls.Add(cmb); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;

                return dlg.ShowDialog(this) == DialogResult.OK ? cmb.SelectedIndex : -1;
            }
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
                    s.Columns.Add(new[] { "Наименование", "=Object.«ИМЯ»" });
                    s.Columns.Add(new[] { "Артикул", "=Object.«ПРОФ»" });
                    s.Columns.Add(new[] { "Длина, мм", "=Object.«Длина»" });
                    s.Columns.Add(new[] { "Колич.", "=Count" });
                    s.Columns.Add(new[] { "Ед. изм.", "=«шт.»" });
                    s.GroupIdx = 1; s.SortMode = 0;
                    // (вариант Б) фикс. ширины столбцов, мм (×масштаб при отрисовке)
                    s.ColMm = new List<double> { 10, 65, 35, 20, 15, 15 };
                    break;
                case 2: // Раскрой — контракт-шапка «<артикул> <рез> <хлыст>», ОБЪЕДИНЁННАЯ на обе
                        //  колонки, + две колонки Длина|Колич., группа по длине. Форма выверена по
                        //  живому использованию: R-Fasad читает таблицу напрямую и парсит рез/хлыст
                        //  ИЗ этой шапки. Артикул/рез/хлыст правит пользователь под профиль; источник —
                        //  слой профиля; на весь объект — по секции на профиль («+ Добавить отчёт»).
                    s.FullRows = new List<string[]>
                    {
                        new[] { "Артикул 8 6000", "=Object.«Длина»", "", "", "по возрастанию" },
                        new[] { "", "=Count", "", "", "" }
                    };
                    s.SeedMerges = new List<int[]> { new[] { 0, 1 } };   // объединить шапку обеих колонок
                    s.SectionTitle = "";
                    s.HideHeader = false;                                 // шапка-контракт должна быть видна
                    s.ColMm = new List<double> { 25, 15 };                // (вариант Б) Длина | Колич., мм
                    break;
                case 3: // Заполнения (Ш/В из РАЗМЕР_ЗАП; Тип — динам. параметр Visibility1)
                    s.Columns.Add(new[] { "№ п/п", "=row" });
                    s.Columns.Add(new[] { "Тип заполнения", "=Object.«Visibility1»" });
                    s.Columns.Add(new[] { "Марка", "=Object.«МАРКИРОВКА»" });
                    s.Columns.Add(new[] { "Ширина, мм", "=Object.«Ширина»" });
                    s.Columns.Add(new[] { "Высота, мм", "=Object.«Высота»" });
                    s.Columns.Add(new[] { "Колич.", "=Count" });
                    s.Columns.Add(new[] { "Площадь, м²", "=Col(4)*Col(5)*Count/1000000" });  // Ш=столбец4, В=столбец5
                    s.GroupIdx = 2; s.SortMode = 0;     // группа по марке (столбец 2: №=0, Тип=1, Марка=2)
                    s.TotalRow = true;                  // строка ИТОГ (сумма кол-ва и площади)
                    if (useFirstLayer) s.SeedLayer = "RF-заполнения";   // авто-источник для шаблона
                    // (вариант Б) №|Тип|Марка|Ш|В|Колич.|Площадь, мм
                    s.ColMm = new List<double> { 10, 40, 25, 20, 20, 15, 25 };
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
            // (аудит 05.07) столбцы итоговой таблицы ОБЩИЕ на все секции: при разном числе
            // столбцов ширины (в т.ч. мм-пресеты, слитые максимумом) применяются к общей сетке —
            // спецификация + раскрой в ОДНОЙ таблице разъедутся. Предупреждаем, не запрещаем.
            if (sections.Count > 1)
            {
                int minC = int.MaxValue, maxC = 0;
                foreach (var so in sections)
                {
                    var sd = so as Dictionary<string, object>;
                    var cl = (sd != null && sd.ContainsKey("columns")) ? sd["columns"] as System.Collections.IList : null;
                    int c = cl != null ? cl.Count : 0;
                    if (c < minC) minC = c;
                    if (c > maxC) maxC = c;
                }
                if (minC != maxC)
                {
                    var ans = MessageBox.Show(this,
                        "Секции отчёта имеют разное число столбцов (" + minC + " и " + maxC + ").\n" +
                        "Столбцы итоговой таблицы общие на все секции — ширины возьмутся по максимуму,\n" +
                        "и узкая секция (например, раскрой) растянется под широкую.\n\n" +
                        "Обычно раскрой строят ОТДЕЛЬНОЙ таблицей. Всё равно построить одной?",
                        "ATableSpec — разные секции", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ans != DialogResult.Yes)
                    {
                        DialogResult = DialogResult.None;   // остаться в форме
                        return;
                    }
                }
            }
            string fontName = (cmbFont != null && cmbFont.SelectedIndex > 0)
                ? Convert.ToString(cmbFont.SelectedItem) : "";   // индекс 0 = «(по стилю таблицы)» → не переопределять
            ReportDef = new Dictionary<string, object>
            {
                { "title", cbTitle.Text },
                { "hide_title", chkHideTitle.Checked },
                { "scale", (double)nudScale.Value },
                { "font", fontName },
                { "sections", sections }
            };
            try   // запомнить масштаб и шрифт — следующая таблица создастся с ними
            {
                using (var rk = Registry.CurrentUser.CreateSubKey(@"Software\ATableSpec"))
                    if (rk != null)
                    {
                        rk.SetValue("Scale", ((int)nudScale.Value).ToString());
                        rk.SetValue("Font", fontName);
                    }
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
        private ComboBox cmbSource;     // (2) Источник секции — редактируемый combo (как «Заголовок»)
        private CheckBox chkHideHeader, chkTotal;
        private DataGridView grid;     // Заголовок | Выражение | Условие | Значение
        private DataGridViewTextBoxColumn _colExpr;     // «Выражение» — TextBox (надёжный свободный ввод)
        private DataGridViewComboBoxColumn _colCond, _colGroup;
        private DataGridViewTextBoxColumn _colMm;       // скрытый: ширина столбца таблицы, мм (вариант Б)
        private DataGridViewColumn _colVal;             // «Значение» — combo DropDown на строковой ячейке (ValueComboCell)
        private ToolStripMenuItem _miInsert;
        private readonly List<int[]> _merges = new List<int[]>();   // [s,e] 0-базово (по строкам грида)
        private readonly List<string> _exprSuggest = new List<string>();
        private bool _syncingGrp;       // защита от реентрантности при «единственной группе»

        private string _layer = "";

        public event Action<SectionCard> MoveUpRequested, MoveDownRequested, RemoveRequested, CopyRequested;

        public SectionCard(List<string> layers, List<string> fields,
                           Dictionary<string, Dictionary<string, List<string>>> valuesByLayer, SectionSeed seed)
        {
            _layers = layers ?? new List<string>();
            _fields = fields ?? new List<string>();
            _valuesByLayer = valuesByLayer;
            BorderStyle = BorderStyle.FixedSingle;
            Width = 712; Height = 316; Margin = new Padding(0, 0, 0, 8);  // +8: ряд пипеток над гридом

            BuildUi(seed);
            if (seed != null)
            {
                if (!string.IsNullOrEmpty(seed.SeedLayer) && _layers.Contains(seed.SeedLayer))
                    _layer = seed.SeedLayer;                       // авто-источник из пресета (RF-заполнения)
                else if (seed.UseFirstLayer && _layers.Count > 0)
                    _layer = _layers[0];
            }
            if (cmbSource != null) cmbSource.Text = _layer ?? "";   // (2) показать текущий источник в combo
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

            // (2) Источник — редактируемый combo (как «Заголовок»): выбор слоя ИЛИ ручной ввод имени.
            Controls.Add(new Label { Left = 84, Top = y + 4, Width = 58, Text = "Источник:" });
            cmbSource = new ComboBox { Left = 144, Top = y, Width = 200, DropDownStyle = ComboBoxStyle.DropDown };
            cmbSource.Items.AddRange(_layers.ToArray());
            cmbSource.SelectedIndexChanged += (s, e) => OnSourceChanged();
            cmbSource.Leave += (s, e) => OnSourceChanged();
            Controls.Add(cmbSource);

            // (пипетка 1, вариант 1 Алексея) слой «Источника» — иконка-глиф ВПЛОТНУЮ к combo
            //  (текстовая кнопка в стороне была неинформативна: не видно, к какому полю относится).
            var btnPipSrc = new Button { Left = 346, Top = y, Width = 26, Height = 23,
                Image = PipBitmap(), ImageAlign = ContentAlignment.MiddleCenter };
            btnPipSrc.Click += (s, e) => PipSource();
            Controls.Add(btnPipSrc);

            // «Копировать» отчёт — правее пипетки: дублирует секцию (с выбором позиции вставки).
            var btnCopy = new Button { Left = 378, Top = y, Width = 104, Text = "Копировать" };
            btnCopy.Click += (s, e) => { var h = CopyRequested; if (h != null) h(this); };
            Controls.Add(btnCopy);

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

            Controls.Add(new Label { Left = 8, Top = y + 4, Width = 690,
                Text = "Столбцы (Заголовок | Выражение | Условие | Значение | Группа):" });
            // (пипетки 2 и 3, вариант 1 Алексея) — теперь ГЛИФЫ У ПРАВОГО КРАЯ ЯЧЕЕК
            //  «Заголовок»/«Значение» (Grid_CellPainting/Grid_CellMouseDown), а не кнопки над гридом.
            var tips = new ToolTip();
            tips.SetToolTip(btnPipSrc, "Слой с блока: взять слой указанного блока в «Источник» (поле остаётся редактируемым)");
            y += 26;
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
            // (Алексей) ВЕРНУТЬ кнопку выпадающего списка: «Значение» = редактируемый combo.
            //  Ячейка ValueComboCell хранит строку (как TextBox) → свободный ввод/подстрока «содержит»
            //  держится; редактор — ComboBox DropDown (кнопка списка + ручной ввод). Список значений
            //  наполняется per-row в Grid_EditingControlShowing. (combo-КОЛОНКА ранее теряла текст.)
            _colVal = new DataGridViewColumn(new ValueComboCell())
            {
                Name = "val", HeaderText = "Значение",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            };
            // (хотелка) группировка — инлайн-столбец правее «Значение»: непустое значение = группа
            //  по этому столбцу с выбранной сортировкой; единственная группа на секцию.
            _colGroup = new DataGridViewComboBoxColumn
            {
                Name = "grp", HeaderText = "Группа", Width = 120,
                FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing
            };
            _colGroup.Items.AddRange(new object[] { "", "по возрастанию", "по убыванию", "без сортировки" });
            // (вариант Б) скрытый столбец «mm» — ширина ЭТОГО столбца итоговой таблицы в мм
            //  (заполняется пресетом/реверсом; пусто = дефолт рендера 20 мм). В UI не виден.
            _colMm = new DataGridViewTextBoxColumn { Name = "mm", Visible = false };
            grid.Columns.Add(colHdr); grid.Columns.Add(_colExpr); grid.Columns.Add(_colCond);
            grid.Columns.Add(_colVal); grid.Columns.Add(_colGroup); grid.Columns.Add(_colMm);

            grid.EditingControlShowing += Grid_EditingControlShowing;
            grid.CellFormatting += Grid_CellFormatting;        // (5) «(объединено)» в подчинённых строках шапки
            grid.CellValidating += Grid_CellValidating;
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CellPainting += Grid_CellPainting;            // пипетки 2/3: глиф у правого края ячеек hdr/val
            grid.CellMouseDown += Grid_CellMouseDown;          //   клик по глифу — взять с блока (вариант 1)
            grid.RowsRemoved += Grid_RowsRemoved;              // сдвиг объединений шапки при удалении строк
                                                               //   (в т.ч. Del-клавишей мимо DeleteSelectedRows)
            grid.DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };
            grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode)
                { DeleteSelectedRows(); e.Handled = true; }
            };
            // засев строк
            string[] grpLabels = { "по возрастанию", "по убыванию", "без сортировки" };
            if (seed != null && seed.FullRows != null)
            {
                // реверс из def: полный набор строк грида + заголовок/скрытие шапки + объединения
                txtSecTitle.Text = seed.SectionTitle ?? "";
                chkHideHeader.Checked = seed.HideHeader;
                int fi = 0;
                foreach (var fr in seed.FullRows)
                {
                    if (fr == null) { fi++; continue; }
                    string h  = fr.Length > 0 ? (fr[0] ?? "") : "";
                    string ex = fr.Length > 1 ? (fr[1] ?? "") : "";
                    string op = fr.Length > 2 ? (fr[2] ?? "") : "";
                    string vl = fr.Length > 3 ? (fr[3] ?? "") : "";
                    string gr = fr.Length > 4 ? (fr[4] ?? "") : "";
                    string mm = (seed.ColMm != null && fi < seed.ColMm.Count && seed.ColMm[fi] > 0)
                        ? seed.ColMm[fi].ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                    grid.Rows.Add(h, ex, op, vl, gr, mm);
                    fi++;
                }
                if (seed.SeedMerges != null)
                    foreach (var sp in seed.SeedMerges)
                        if (sp != null && sp.Length == 2 && sp[0] >= 0 && sp[1] >= sp[0] && sp[1] < seed.FullRows.Count)
                            _merges.Add(new[] { sp[0], sp[1] });
                _merges.Sort((a, b) => a[0].CompareTo(b[0]));
            }
            else if (seed != null)
                for (int gi = 0; gi < seed.Columns.Count; gi++)
                {
                    var c = seed.Columns[gi];
                    if (c == null || c.Length < 2) continue;
                    string grp = "";
                    if (seed.GroupIdx >= 0 && gi == seed.GroupIdx)
                        grp = grpLabels[(seed.SortMode >= 0 && seed.SortMode <= 2) ? seed.SortMode : 0];
                    string mm = (seed.ColMm != null && gi < seed.ColMm.Count && seed.ColMm[gi] > 0)
                        ? seed.ColMm[gi].ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                    grid.Rows.Add(c[0], c[1], "", "", grp, mm);
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

        // ───────────── пипетки: взять слой/значение/артикул прямо с блока ─────────────
        // Форма модальная (ShowModalDialog) → на время выбора прячем её штатно через
        // Editor.StartUserInteraction. Пипетки ТОЛЬКО пишут текст в поля — ручное
        // редактирование всех трёх мест полностью сохраняется.
        // (вариант 1 Алексея) Триггеры — В САМИХ ПОЛЯХ: глиф-кнопка у combo «Источник»;
        // глифы у правого края ячеек «Заголовок» (артикул раскроя) и «Значение» (фильтр).

        private const int PipZone = 18;   // клик-зона пипетки у правого края ячейки, px
        private const int DdZone  = 18;   // клик-зона «раскрыть список» левее пипетки (только val), px

        // узнаваемая «пипетка» в произвольном box: ствол по диагонали + колба + капля у носика
        private static void DrawPipGlyph(Graphics g, Rectangle box, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 2f))
            using (var br = new SolidBrush(color))
            {
                float x = box.X, y = box.Y, w = box.Width, h = box.Height;
                g.DrawLine(pen, x + w * 0.22f, y + h * 0.78f, x + w * 0.62f, y + h * 0.38f); // ствол
                g.FillEllipse(br, x + w * 0.50f, y + h * 0.06f, w * 0.44f, h * 0.44f);        // колба
                g.FillEllipse(br, x + w * 0.06f, y + h * 0.80f, w * 0.18f, h * 0.18f);        // капля
            }
            g.SmoothingMode = old;
        }

        // маленький ▼ «раскрыть список» — рисуется в val ЛЕВЕЕ пипетки, чтобы клик по списку
        // и клик по пипетке не конфликтовали (фидбэк Алексея, видео 05.07)
        private static void DrawDropGlyph(Graphics g, Rectangle zone, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int cx = zone.X + zone.Width / 2, cy = zone.Y + zone.Height / 2;
            using (var br = new SolidBrush(color))
                g.FillPolygon(br, new[]
                {
                    new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3)
                });
            g.SmoothingMode = old;
        }

        private static Bitmap _pipBmp;
        private static Bitmap PipBitmap()
        {
            if (_pipBmp != null) return _pipBmp;
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
                DrawPipGlyph(g, new Rectangle(1, 1, 14, 14), Color.FromArgb(37, 99, 235));
            _pipBmp = bmp;
            return bmp;
        }

        // подчинённая строка объединённой шапки (внутри спана, не первая) — глиф не рисуем
        private bool IsMergeSub(int ri)
        {
            foreach (var m in _merges) if (ri > m[0] && ri <= m[1]) return true;
            return false;
        }

        // глава объединённой шапки: hdr-пипетка (артикул раскроя) живёт ТОЛЬКО здесь —
        // в спецификациях без merge её нет (фидбэк Алексея 05.07: «в шаблонах спецификаций
        // пипетки из заголовков убрать, в раскрое оставить»)
        private bool IsMergeHead(int ri)
        {
            foreach (var m in _merges) if (ri == m[0]) return true;
            return false;
        }

        // Удаление строк грида (кнопкой формы ИЛИ Del-клавишей): индексы объединений шапки
        // обязаны сдвинуться, иначе merge «переезжает» на чужие строки — «(объединено)»,
        // hdr-пипетка и header_merges в def оказываются не там (найдено аудитом 05.07).
        private void Grid_RowsRemoved(object sender, DataGridViewRowsRemovedEventArgs e)
        {
            int ri = e.RowIndex, n = e.RowCount;
            if (n <= 0) return;
            for (int k = _merges.Count - 1; k >= 0; k--)
            {
                int s = _merges[k][0], t = _merges[k][1];
                int ns = s >= ri + n ? s - n : (s >= ri ? ri : s);
                int nt = t >= ri + n ? t - n : (t >= ri ? ri - 1 : t);
                if (nt <= ns) { _merges.RemoveAt(k); continue; }   // спан ужался до <2 строк — снять
                _merges[k][0] = ns; _merges[k][1] = nt;
            }
            RefreshSummary();
        }

        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string cn = grid.Columns[e.ColumnIndex].Name;
            if (cn != "hdr" && cn != "val") return;
            if (grid.Rows[e.RowIndex].IsNewRow) return;
            if (cn == "hdr" && !IsMergeHead(e.RowIndex)) return;   // hdr-глиф — только на объединённой шапке
            e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
            bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
            var box = new Rectangle(e.CellBounds.Right - PipZone + 1,
                                    e.CellBounds.Top + (e.CellBounds.Height - 14) / 2, 14, 14);
            DrawPipGlyph(e.Graphics, box, sel ? Color.White : Color.FromArgb(37, 99, 235));
            if (cn == "val" && e.CellBounds.Width > PipZone + DdZone + 40)
            {
                var dz = new Rectangle(e.CellBounds.Right - PipZone - DdZone, e.CellBounds.Top,
                                       DdZone, e.CellBounds.Height);
                DrawDropGlyph(e.Graphics, dz, sel ? Color.White : Color.FromArgb(96, 96, 96));
            }
            e.Handled = true;
        }

        private void Grid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string cn = grid.Columns[e.ColumnIndex].Name;
            if (cn != "hdr" && cn != "val") return;
            if (grid.Rows[e.RowIndex].IsNewRow) return;
            if (cn == "hdr" && !IsMergeHead(e.RowIndex)) return;
            var rect = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            if (e.X > rect.Width - PipZone)
            {
                // пипетка — у самого правого края
                try
                {
                    grid.EndEdit();
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                }
                catch { }
                if (cn == "val") PipValue(e.RowIndex); else PipHeader(e.RowIndex);
                return;
            }
            if (cn == "val" && e.X > rect.Width - PipZone - DdZone && rect.Width > PipZone + DdZone + 40)
            {
                // ▼ левее пипетки: раскрыть список ОДНИМ кликом (вход в редактор + DroppedDown)
                try
                {
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    grid.BeginEdit(true);
                    grid.BeginInvoke(new Action(() =>
                    {
                        var cb = grid.EditingControl as ComboBox;
                        if (cb != null) cb.DroppedDown = true;
                    }));
                }
                catch { }
            }
        }

        private bool PickBlock(out string layer, out string name, out Dictionary<string, string> attrs)
        {
            layer = null; name = null;
            attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var frm = FindForm();
            if (doc == null || frm == null) return false;
            var ed = doc.Editor;
            EditorUserInteraction ui = null;
            try
            {
                ui = ed.StartUserInteraction(frm);
                var peo = new PromptEntityOptions("\nПипетка — укажите блок: ");
                peo.SetRejectMessage("\nЭто не вхождение блока.");
                peo.AddAllowedClass(typeof(AcDb.BlockReference), false);
                var res = ed.GetEntity(peo);
                if (res.Status != PromptStatus.OK) return false;
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    var br = (AcDb.BlockReference)tr.GetObject(res.ObjectId, AcDb.OpenMode.ForRead);
                    layer = br.Layer;
                    var btr = (AcDb.BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, AcDb.OpenMode.ForRead);
                    name = btr.Name;   // эффективное имя (динам. блоки)
                    foreach (AcDb.ObjectId id in br.AttributeCollection)
                    {
                        var ar = tr.GetObject(id, AcDb.OpenMode.ForRead) as AcDb.AttributeReference;
                        if (ar != null && !attrs.ContainsKey(ar.Tag)) attrs[ar.Tag] = ReportCommands.NumClean(ar.TextString ?? "");
                    }
                    // (видео Алексея 06.07-2) динамические свойства (Visibility1, размеры
                    // динблоков) пипетка НЕ видела — собирался только AttributeCollection,
                    // и «Тип заполнения» =Object.«Visibility1» давал «у блока нет поля».
                    // Добираем как в главном сборе ReportCommands (атрибуты приоритетнее).
                    if (br.IsDynamicBlock)
                    {
                        foreach (AcDb.DynamicBlockReferenceProperty dp in br.DynamicBlockReferencePropertyCollection)
                        {
                            string pn = dp.PropertyName;
                            if (string.IsNullOrEmpty(pn) || attrs.ContainsKey(pn)) continue;
                            attrs[pn] = ReportCommands.NumClean(
                                Convert.ToString(dp.Value, System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
                    tr.Commit();
                }
                // производные Ширина/Высота из РАЗМЕР_ЗАП («1125Х275») — как в движке
                string rz;
                if (attrs.TryGetValue("РАЗМЕР_ЗАП", out rz) && !string.IsNullOrEmpty(rz))
                {
                    var p = rz.Split(new[] { 'Х', 'х', 'X', 'x', '*' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 2)
                    {
                        if (!attrs.ContainsKey("Ширина")) attrs["Ширина"] = p[0].Trim();
                        if (!attrs.ContainsKey("Высота")) attrs["Высота"] = p[1].Trim();
                    }
                }
                return true;
            }
            catch { return false; }
            finally { if (ui != null) ui.End(); }
        }

        // (1) слой «Источника» с блока
        private void PipSource()
        {
            string layer, name; Dictionary<string, string> attrs;
            if (!PickBlock(out layer, out name, out attrs) || string.IsNullOrEmpty(layer)) return;
            cmbSource.Text = layer;
            OnSourceChanged();
        }

        // (2) «Значение» текущей строки — значение ЕЁ поля (из «Выражения») с блока;
        //     пустое «Условие» добиваем «=» (правится вручную).
        private void PipValue(int ri)
        {
            if (ri < 0 || ri >= grid.Rows.Count || grid.Rows[ri].IsNewRow)
            { MessageBox.Show(FindForm(), "Выберите строку столбца, куда вставить значение.", "Пипетка"); return; }
            string ex = Convert.ToString(grid.Rows[ri].Cells["expr"].Value) ?? "";
            string fld = ExtractField(ex);
            if (string.IsNullOrEmpty(fld))
            { MessageBox.Show(FindForm(), "В строке нет поля: «Выражение» должно быть вида =Object.«Поле».", "Пипетка"); return; }
            string layer, name; Dictionary<string, string> attrs;
            if (!PickBlock(out layer, out name, out attrs)) return;
            string val;
            if (fld.Equals("Слой", StringComparison.OrdinalIgnoreCase)) val = layer;
            else if (fld.Equals("Имя блока", StringComparison.OrdinalIgnoreCase) ||
                     fld.Equals("ИМЯ_БЛОКА", StringComparison.OrdinalIgnoreCase) ||
                     fld.Equals("Name", StringComparison.OrdinalIgnoreCase)) val = name;
            // (видео Алексея 06.07-2) поле «ИМЯ» — это АТРИБУТ (маркировка С01),
            // а не имя блока: старая ветка «Имя → name» вставляла КП45 (блоки названы
            // по профилю). Атрибуты/динсвойства смотрим ПЕРВЫМИ, имя блока — только
            // legacy-фолбэк для «Имя» без такого атрибута.
            else if (attrs.TryGetValue(fld, out val)) { }
            else if (fld.Equals("Имя", StringComparison.OrdinalIgnoreCase)) val = name;
            else
            { MessageBox.Show(FindForm(), "У блока нет поля «" + fld + "».", "Пипетка"); return; }
            grid.Rows[ri].Cells["val"].Value = val ?? "";
            string op = (Convert.ToString(grid.Rows[ri].Cells["cond"].Value) ?? "").Trim();
            if (op.Length == 0) grid.Rows[ri].Cells["cond"].Value = "=";
        }

        // (3) заголовок текущей строки (шапка раскроя): токен-артикул ← Object.«ПРОФ»
        //     (запасной «ПРОФИЛЬ»), хвост строки (пила/хлыст, по умолчанию «8 6000») сохраняется.
        private void PipHeader(int ri)
        {
            if (ri < 0 || ri >= grid.Rows.Count || grid.Rows[ri].IsNewRow)
            { MessageBox.Show(FindForm(), "Нет строки столбца для заголовка.", "Пипетка"); return; }
            string layer, name; Dictionary<string, string> attrs;
            if (!PickBlock(out layer, out name, out attrs)) return;
            string art;
            if (!attrs.TryGetValue("ПРОФ", out art) && !attrs.TryGetValue("ПРОФИЛЬ", out art))
            { MessageBox.Show(FindForm(), "У блока нет атрибута «ПРОФ» (или «ПРОФИЛЬ»).", "Пипетка"); return; }
            art = (art ?? "").Replace(" ", "");   // артикул в шапке раскроя — без пробелов (формат R-Fasad)
            if (art.Length == 0)
            { MessageBox.Show(FindForm(), "Атрибут «ПРОФ» у блока пуст.", "Пипетка"); return; }
            string curTxt = Convert.ToString(grid.Rows[ri].Cells["hdr"].Value) ?? "";
            var toks = curTxt.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string rest = (toks.Length >= 2) ? string.Join(" ", toks, 1, toks.Length - 1) : "8 6000";
            grid.Rows[ri].Cells["hdr"].Value = (art + " " + rest).Trim();
        }

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

        // редактор ячейки: «Выражение» — TextBox с автодополнением; «Значение» — редактируемый
        //  combo (кнопка списка + свободный ввод); «Условие» — только выбор.
        private void Grid_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var col = grid.CurrentCell != null ? grid.CurrentCell.OwningColumn : null;
            if (col == null) return;

            if (col.Name == "expr")
            {
                // «Выражение» — TextBox с автодополнением (combo терял введённый текст — фидбэк Алексея).
                var tb = e.Control as TextBox;
                if (tb == null) return;
                var ac = new AutoCompleteStringCollection();
                ac.AddRange(_exprSuggest.ToArray());
                tb.AutoCompleteCustomSource = ac;
                tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                return;
            }

            if (col.Name == "val")
            {
                // «Значение» — редактируемый combo (ValueComboCell): кнопка списка ВОЗВРАЩЕНА,
                //  свободный ввод держится (значение = Text, не SelectedItem). Список — контекстные
                //  значения поля строки (для «содержит» можно ввести подстроку руками).
                var cbv = e.Control as ComboBox;
                if (cbv == null) return;
                cbv.DropDownStyle = ComboBoxStyle.DropDown;
                int ri = grid.CurrentCell.RowIndex;
                string expr = ri >= 0 ? Convert.ToString(grid.Rows[ri].Cells["expr"].Value) : "";
                string keep = cbv.Text;          // не терять уже введённое при наполнении списка
                cbv.Items.Clear();
                var ac = new AutoCompleteStringCollection();
                foreach (var v in ValuesFor(_layer, ExtractField(expr) ?? ""))
                {
                    string sv = Convert.ToString(v);
                    cbv.Items.Add(sv);
                    ac.Add(sv);
                }
                cbv.Text = keep;
                cbv.AutoCompleteCustomSource = ac;
                cbv.AutoCompleteSource = AutoCompleteSource.CustomSource;
                cbv.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                return;
            }

            var cb = e.Control as ComboBox;
            if (cb == null) return;
            if (col.Name == "cond") { cb.DropDownStyle = ComboBoxStyle.DropDownList; }
        }

        // корень гонки combo: свободно введённый текст кладём в Items колонки ДО коммита →
        // combo его принимает (раньше отклонял и ячейка оставалась со старым значением).
        private void Grid_CellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            // «Значение» теперь TextBox — свободный ввод коммитится всегда, спец-валидация не нужна.
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
        internal static string ExtractField(string expr)
        {
            if (string.IsNullOrEmpty(expr)) return null;
            int i = expr.IndexOf('«');
            if (i >= 0) { int j = expr.IndexOf('»', i + 1); if (j > i + 1) return expr.Substring(i + 1, j - i - 1); }
            // (краш-тест 06.07-2) движок понимает =Object.Длина БЕЗ «ёлочек» (синтаксис СПДС),
            // а помощники формы (пипетка, список значений) такое поле не видели — берём
            // хвост-идентификатор после «Object.» (буквы/цифры/_ , юникод). Покрывает и Object.Name.
            int k = expr.IndexOf("Object.", StringComparison.OrdinalIgnoreCase);
            if (k >= 0)
            {
                int p = k + 7, q = p;
                while (q < expr.Length && (char.IsLetterOrDigit(expr[q]) || expr[q] == '_')) q++;
                if (q > p) return expr.Substring(p, q - p);
            }
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
            foreach (var ex in new[] { "Слой", "Имя блока", "Длина", "Ширина", "Высота" })
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
            RefreshMergeVisuals();   // (5) подсветка объединённой шапки в гриде
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
            // сдвиг/снятие объединений шапки делает Grid_RowsRemoved (на каждый RemoveAt)
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

        // (5) визуальная пометка объединённой шапки в гриде: общий фон группы; текст — в первой строке
        //     (редактируемо), в остальных «(объединено)» серым. «Заголовок» подчинённых строк НЕ затирается
        //     (показ через CellFormatting), при разъединении исходное значение возвращается.
        private static readonly Color MergeBack = Color.FromArgb(225, 236, 250);
        private bool InMerge(int rowIndex, out bool anchor)
        {
            anchor = false;
            foreach (var sp in _merges)
                if (rowIndex >= sp[0] && rowIndex <= sp[1]) { anchor = (rowIndex == sp[0]); return true; }
            return false;
        }
        private void RefreshMergeVisuals()
        {
            if (grid == null) return;
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                var cell = r.Cells["hdr"];
                bool anchor;
                if (InMerge(r.Index, out anchor))
                {
                    cell.Style.BackColor = MergeBack;
                    cell.ReadOnly = !anchor;        // править можно только первую строку группы (= слитую шапку)
                }
                else
                {
                    cell.Style.BackColor = Color.Empty;
                    cell.ReadOnly = false;
                }
            }
            var hc = grid.Columns["hdr"];
            if (hc != null) grid.InvalidateColumn(hc.Index);
        }
        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "hdr") return;
            bool anchor;
            if (InMerge(e.RowIndex, out anchor) && !anchor)
            {
                e.Value = "(объединено)";
                e.CellStyle.ForeColor = Color.Gray;
                e.FormattingApplied = true;
            }
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

        // (2) смена источника из combo (выбор из списка ИЛИ ручной ввод имени слоя)
        private void OnSourceChanged()
        {
            string v = (cmbSource.Text ?? "").Trim();
            if (v == _layer) return;
            _layer = v;
            RefreshContext();
            RefreshSummary();
        }

        // ── всплывающее окно: источник (запасной путь; основной выбор — combo «Источник») ──
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
            var colMm  = new List<double>();    // (вариант Б) мм по output-столбцам; -1 = дефолт рендера
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
                    double mv;
                    string mtxt = Convert.ToString(r.Cells["mm"].Value) ?? "";
                    if (!double.TryParse(mtxt, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out mv) || mv <= 0) mv = -1;
                    colMm.Add(mv);
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

            var def = new Dictionary<string, object>
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
            // (вариант Б) мм-ширины пишем, только если задана хоть одна (иначе легаси-AutoFit);
            // движок незнакомый ключ игнорирует (.get по известным полям).
            bool anyMm = false;
            foreach (var m in colMm) if (m > 0) { anyMm = true; break; }
            if (anyMm) def["col_mm"] = colMm;
            return def;
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
        public List<double> ColMm = null;                        // (вариант Б) ширины столбцов, мм — параллельно
                                                                 //  Columns / строкам-столбцам FullRows; ≤0 = нет

        // --- путь реверса (ATSPECEDIT / FromDef): восстановление формы из сохранённого def ---
        // Когда FullRows != null, секция засевается ИМИ (полные строки грида), а не Columns.
        public List<string[]> FullRows = null;   // каждая = {Заголовок, Выражение, Условие, Значение, Группа}
        public List<int[]> SeedMerges = null;     // объединения шапки [s,e] в координатах строк FullRows
        public string SectionTitle = null;        // «Заголовок секции»
        public bool HideHeader = false;           // «Скрыть шапку столбцов»
    }
}
